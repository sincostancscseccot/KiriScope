using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using KiriScope.Core.Diagnostics;
using KiriScope.Core.Evidence;

namespace KiriScope.Xp3;

/// <summary>
/// Reads standard, non-obfuscated XP3 index data. Content filters are deliberately out of scope
/// for this component and will be applied after raw archive parsing succeeds.
/// </summary>
public static class Xp3ArchiveReader
{
    private const int ArchiveHeaderLength = 19;
    private const int IndexHeaderLength = 9;
    private const int CompressedIndexHeaderLength = 17;
    private const uint FileChunk = 0x656C6946; // File
    private const uint InfoChunk = 0x6F666E69; // info
    private const uint SegmentChunk = 0x6D676573; // segm
    private const uint AdlerChunk = 0x726C6461; // adlr
    private const int SegmentRecordLength = 28;

    public static async Task<Xp3ArchiveIndex> ReadIndexAsync(
        Stream input,
        Xp3ReadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead || !input.CanSeek)
        {
            throw new ArgumentException("Input stream must be readable and seekable.", nameof(input));
        }

        options ??= new Xp3ReadOptions();
        ValidateOptions(options);

        var probe = await Xp3ArchiveProbe.ProbeAsync(input, cancellationToken).ConfigureAwait(false);
        if (!probe.IsXp3 || probe.IndexOffset is null)
        {
            return new Xp3ArchiveIndex(probe.Stage, 0, false, Array.Empty<Xp3Entry>(), probe.Diagnostics);
        }

        var diagnostics = new List<KiriScopeDiagnostic>(probe.Diagnostics);
        var indexOffset = probe.IndexOffset.Value;
        if (indexOffset >= input.Length)
        {
            diagnostics.Add(Error("XP3_INDEX_OFFSET_OUT_OF_RANGE", "The first index offset is beyond the end of the input.", indexOffset));
            return new Xp3ArchiveIndex(EvidenceStage.ContainerIdentified, indexOffset, false, Array.Empty<Xp3Entry>(), diagnostics);
        }

        if (await IsKiriKiriZLinkAsync(input, indexOffset, cancellationToken).ConfigureAwait(false))
        {
            var linkedOffset = await ReadInt64AtAsync(input, indexOffset + 9, cancellationToken).ConfigureAwait(false);
            if (linkedOffset < ArchiveHeaderLength || linkedOffset >= input.Length)
            {
                diagnostics.Add(Error("XP3_KRKRZ_LINK_INVALID", "KiriKiri Z index link points outside the input.", indexOffset + 9));
                return new Xp3ArchiveIndex(EvidenceStage.ContainerIdentified, indexOffset, false, Array.Empty<Xp3Entry>(), diagnostics);
            }

            indexOffset = linkedOffset;
            diagnostics.Add(new KiriScopeDiagnostic(
                "XP3_KRKRZ_INDEX_LINK",
                DiagnosticSeverity.Info,
                "Followed a KiriKiri Z index link.",
                indexOffset));
        }

        if (!RangeFits(indexOffset, IndexHeaderLength, input.Length))
        {
            diagnostics.Add(Error("XP3_INDEX_HEADER_TRUNCATED", "The XP3 index header is truncated.", indexOffset));
            return new Xp3ArchiveIndex(EvidenceStage.ContainerIdentified, indexOffset, false, Array.Empty<Xp3Entry>(), diagnostics);
        }

        var indexHeader = await ReadBytesAtAsync(input, indexOffset, IndexHeaderLength, cancellationToken).ConfigureAwait(false);
        var indexKind = indexHeader[0];
        if (indexKind is not 0 and not 1)
        {
            diagnostics.Add(Error("XP3_INDEX_KIND_UNKNOWN", $"Unsupported XP3 index kind: {indexKind}.", indexOffset));
            return new Xp3ArchiveIndex(EvidenceStage.ContainerIdentified, indexOffset, false, Array.Empty<Xp3Entry>(), diagnostics);
        }

        var packedSize = BinaryPrimitives.ReadInt64LittleEndian(indexHeader.AsSpan(1, sizeof(long)));
        long unpackedSize;
        if (indexKind == 0)
        {
            unpackedSize = packedSize;
        }
        else
        {
            if (!RangeFits(indexOffset, CompressedIndexHeaderLength, input.Length))
            {
                diagnostics.Add(Error("XP3_COMPRESSED_INDEX_HEADER_TRUNCATED", "The compressed XP3 index header is truncated.", indexOffset));
                return new Xp3ArchiveIndex(EvidenceStage.ContainerIdentified, indexOffset, true, Array.Empty<Xp3Entry>(), diagnostics);
            }

            unpackedSize = await ReadInt64AtAsync(input, indexOffset + 9, cancellationToken).ConfigureAwait(false);
        }

        if (!IsSafeIndexSize(packedSize, options.MaximumIndexSize) ||
            !IsSafeIndexSize(unpackedSize, options.MaximumIndexSize))
        {
            diagnostics.Add(Error("XP3_INDEX_SIZE_INVALID", "The XP3 index declares an invalid or excessive size.", indexOffset + 1));
            return new Xp3ArchiveIndex(EvidenceStage.ContainerIdentified, indexOffset, indexKind == 1, Array.Empty<Xp3Entry>(), diagnostics);
        }

        var dataOffset = indexOffset + (indexKind == 0 ? IndexHeaderLength : CompressedIndexHeaderLength);
        if (!RangeFits(dataOffset, packedSize, input.Length))
        {
            diagnostics.Add(Error("XP3_INDEX_DATA_OUT_OF_RANGE", "The XP3 index data exceeds the input length.", dataOffset));
            return new Xp3ArchiveIndex(EvidenceStage.ContainerIdentified, indexOffset, indexKind == 1, Array.Empty<Xp3Entry>(), diagnostics);
        }

        byte[] indexData;
        try
        {
            indexData = await ReadIndexDataAsync(input, dataOffset, packedSize, unpackedSize, indexKind == 1, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(Error("XP3_INDEX_DECOMPRESSION_FAILED", exception.Message, dataOffset));
            return new Xp3ArchiveIndex(EvidenceStage.ContainerIdentified, indexOffset, true, Array.Empty<Xp3Entry>(), diagnostics);
        }

        var entries = ParseEntries(indexData, input.Length, options, diagnostics);
        var stage = diagnostics.Any(static item => item.Severity == DiagnosticSeverity.Error)
            ? EvidenceStage.ContainerIdentified
            : EvidenceStage.IndexParsed;
        return new Xp3ArchiveIndex(stage, indexOffset, indexKind == 1, entries, diagnostics);
    }

    private static List<Xp3Entry> ParseEntries(
        ReadOnlySpan<byte> indexData,
        long archiveLength,
        Xp3ReadOptions options,
        List<KiriScopeDiagnostic> diagnostics)
    {
        var entries = new List<Xp3Entry>();
        var offset = 0;
        while (offset < indexData.Length)
        {
            if (entries.Count >= options.MaximumEntryCount)
            {
                diagnostics.Add(Error("XP3_ENTRY_LIMIT_EXCEEDED", "The configured XP3 entry limit was reached.", offset));
                break;
            }

            if (!TryReadChunkHeader(indexData, ref offset, out var chunkTag, out var chunkLength, out var chunkDataOffset, diagnostics))
            {
                break;
            }

            if (chunkTag == FileChunk)
            {
                var entry = ParseFileEntry(indexData.Slice(chunkDataOffset, chunkLength), archiveLength, diagnostics);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }

            offset = checked(chunkDataOffset + chunkLength);
        }

        return entries;
    }

    private static Xp3Entry? ParseFileEntry(ReadOnlySpan<byte> entryData, long archiveLength, List<KiriScopeDiagnostic> diagnostics)
    {
        string? name = null;
        var isMarkedEncrypted = false;
        long unpackedSize = 0;
        long packedSize = 0;
        uint? adler32 = null;
        var segments = new List<Xp3Segment>();
        var offset = 0;

        while (offset < entryData.Length)
        {
            if (!TryReadChunkHeader(entryData, ref offset, out var sectionTag, out var sectionLength, out var sectionDataOffset, diagnostics))
            {
                return null;
            }

            var section = entryData.Slice(sectionDataOffset, sectionLength);
            switch (sectionTag)
            {
                case InfoChunk:
                    if (!TryParseInfo(section, out name, out isMarkedEncrypted, out unpackedSize, out packedSize))
                    {
                        diagnostics.Add(Error("XP3_INFO_INVALID", "An XP3 info section is malformed.", sectionDataOffset));
                        return null;
                    }

                    break;
                case SegmentChunk:
                    if (!TryParseSegments(section, archiveLength, segments, diagnostics, sectionDataOffset))
                    {
                        return null;
                    }

                    break;
                case AdlerChunk when section.Length == sizeof(uint):
                    adler32 = BinaryPrimitives.ReadUInt32LittleEndian(section);
                    break;
            }

            offset = checked(sectionDataOffset + sectionLength);
        }

        if (string.IsNullOrEmpty(name) || segments.Count == 0)
        {
            diagnostics.Add(Error("XP3_FILE_ENTRY_INCOMPLETE", "An XP3 File chunk lacks a usable info or segm section."));
            return null;
        }

        return new Xp3Entry(name, isMarkedEncrypted, unpackedSize, packedSize, adler32, segments);
    }

    private static bool TryParseInfo(
        ReadOnlySpan<byte> section,
        out string? name,
        out bool isMarkedEncrypted,
        out long unpackedSize,
        out long packedSize)
    {
        name = null;
        isMarkedEncrypted = false;
        unpackedSize = 0;
        packedSize = 0;
        const int fixedLength = sizeof(uint) + sizeof(long) + sizeof(long) + sizeof(ushort);
        if (section.Length < fixedLength)
        {
            return false;
        }

        isMarkedEncrypted = BinaryPrimitives.ReadUInt32LittleEndian(section) != 0;
        unpackedSize = BinaryPrimitives.ReadInt64LittleEndian(section.Slice(sizeof(uint), sizeof(long)));
        packedSize = BinaryPrimitives.ReadInt64LittleEndian(section.Slice(sizeof(uint) + sizeof(long), sizeof(long)));
        var characterCount = BinaryPrimitives.ReadUInt16LittleEndian(section.Slice(sizeof(uint) + sizeof(long) + sizeof(long), sizeof(ushort)));
        var byteLength = checked(characterCount * sizeof(char));
        var nameDataLength = section.Length - fixedLength;
        // Some KiriKiri builds retain a UTF-16 NUL after the counted name.
        // Accept that exact, harmless variant while rejecting all other trailing data.
        var hasTerminatingNul = nameDataLength == byteLength + sizeof(char) &&
            section.Slice(fixedLength + byteLength, sizeof(char)).SequenceEqual(stackalloc byte[sizeof(char)]);
        if (unpackedSize < 0 || packedSize < 0 || (nameDataLength != byteLength && !hasTerminatingNul))
        {
            return false;
        }

        name = Encoding.Unicode.GetString(section.Slice(fixedLength, byteLength));
        return name.IndexOf('\0') < 0;
    }

    private static bool TryParseSegments(
        ReadOnlySpan<byte> section,
        long archiveLength,
        ICollection<Xp3Segment> destination,
        List<KiriScopeDiagnostic> diagnostics,
        long sectionOffset)
    {
        if (section.Length == 0 || section.Length % SegmentRecordLength != 0)
        {
            diagnostics.Add(Error("XP3_SEGMENT_LAYOUT_INVALID", "An XP3 segm section has an invalid record length.", sectionOffset));
            return false;
        }

        for (var offset = 0; offset < section.Length; offset += SegmentRecordLength)
        {
            var flags = BinaryPrimitives.ReadInt32LittleEndian(section.Slice(offset, sizeof(int)));
            var segmentOffset = BinaryPrimitives.ReadInt64LittleEndian(section.Slice(offset + sizeof(int), sizeof(long)));
            var unpackedSize = BinaryPrimitives.ReadInt64LittleEndian(section.Slice(offset + sizeof(int) + sizeof(long), sizeof(long)));
            var packedSize = BinaryPrimitives.ReadInt64LittleEndian(section.Slice(offset + sizeof(int) + (2 * sizeof(long)), sizeof(long)));
            if (segmentOffset < 0 || unpackedSize < 0 || packedSize < 0 || !RangeFits(segmentOffset, packedSize, archiveLength))
            {
                diagnostics.Add(Error("XP3_SEGMENT_OUT_OF_RANGE", "An XP3 segment points outside the input.", sectionOffset + offset));
                return false;
            }

            destination.Add(new Xp3Segment(flags != 0, segmentOffset, unpackedSize, packedSize));
        }

        return true;
    }

    private static bool TryReadChunkHeader(
        ReadOnlySpan<byte> source,
        ref int offset,
        out uint tag,
        out int length,
        out int dataOffset,
        List<KiriScopeDiagnostic> diagnostics)
    {
        tag = 0;
        length = 0;
        dataOffset = 0;
        const int chunkHeaderLength = sizeof(uint) + sizeof(long);
        if (source.Length - offset < chunkHeaderLength)
        {
            diagnostics.Add(Error("XP3_CHUNK_HEADER_TRUNCATED", "An XP3 index chunk header is truncated.", offset));
            return false;
        }

        tag = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(offset, sizeof(uint)));
        var longLength = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(offset + sizeof(uint), sizeof(long)));
        dataOffset = offset + chunkHeaderLength;
        if (longLength < 0 || longLength > source.Length - dataOffset)
        {
            diagnostics.Add(Error("XP3_CHUNK_LENGTH_INVALID", "An XP3 index chunk length is invalid.", offset + sizeof(uint)));
            return false;
        }

        length = checked((int)longLength);
        return true;
    }

    private static async Task<bool> IsKiriKiriZLinkAsync(Stream input, long offset, CancellationToken cancellationToken)
    {
        if (!RangeFits(offset, CompressedIndexHeaderLength, input.Length))
        {
            return false;
        }

        var marker = await ReadBytesAtAsync(input, offset, sizeof(uint), cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt32LittleEndian(marker) == 0x80;
    }

    private static async Task<long> ReadInt64AtAsync(Stream input, long offset, CancellationToken cancellationToken)
    {
        var bytes = await ReadBytesAtAsync(input, offset, sizeof(long), cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt64LittleEndian(bytes);
    }

    private static async Task<byte[]> ReadIndexDataAsync(
        Stream input,
        long dataOffset,
        long packedSize,
        long unpackedSize,
        bool isCompressed,
        CancellationToken cancellationToken)
    {
        input.Position = dataOffset;
        if (!isCompressed)
        {
            return await ReadExactlyAsync(input, checked((int)packedSize), cancellationToken).ConfigureAwait(false);
        }

        await using var compressed = new LimitedReadStream(input, packedSize, leaveOpen: true);
        await using var zlib = new ZLibStream(compressed, CompressionMode.Decompress, leaveOpen: true);
        var data = await ReadExactlyAsync(zlib, checked((int)unpackedSize), cancellationToken).ConfigureAwait(false);
        if (await zlib.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new InvalidDataException("Decompressed XP3 index exceeds its declared size.");
        }

        return data;
    }

    private static async Task<byte[]> ReadBytesAtAsync(Stream input, long offset, int length, CancellationToken cancellationToken)
    {
        input.Position = offset;
        return await ReadExactlyAsync(input, length, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream input, int length, CancellationToken cancellationToken)
    {
        var data = new byte[length];
        var totalRead = 0;
        while (totalRead < data.Length)
        {
            var bytesRead = await input.ReadAsync(data.AsMemory(totalRead), cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                throw new InvalidDataException("Input ended before the declared XP3 structure was complete.");
            }

            totalRead += bytesRead;
        }

        return data;
    }

    private static bool IsSafeIndexSize(long size, long maximumIndexSize) =>
        size >= 0 && size <= maximumIndexSize;

    private static bool RangeFits(long offset, long size, long length) =>
        offset >= 0 && size >= 0 && offset <= length && size <= length - offset;

    private static void ValidateOptions(Xp3ReadOptions options)
    {
        if (options.MaximumIndexSize <= 0 || options.MaximumIndexSize > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum index size must be between 1 and Int32.MaxValue.");
        }

        if (options.MaximumEntryCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum entry count must be positive.");
        }
    }

    private static KiriScopeDiagnostic Error(string code, string message, long? offset = null) =>
        new(code, DiagnosticSeverity.Error, message, offset);

    private sealed class LimitedReadStream(Stream inner, long remaining, bool leaveOpen) : Stream
    {
        private long _remaining = remaining;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_remaining == 0)
            {
                return 0;
            }

            var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
            _remaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining == 0)
            {
                return 0;
            }

            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken).ConfigureAwait(false);
            _remaining -= read;
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !leaveOpen)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
