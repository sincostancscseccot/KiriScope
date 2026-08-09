using System.Buffers.Binary;
using KiriScope.Core.Diagnostics;
using KiriScope.Core.Evidence;

namespace KiriScope.Resources;

/// <summary>Performs a bounded, read-only structural check of a plain M2 PSB header.</summary>
public static class PsbHeaderReader
{
    private const int MaximumHeaderLength = 56;

    public static async Task<PsbHeaderValidationResult> ReadAsync(Stream input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead || !input.CanSeek)
        {
            throw new ArgumentException("Input stream must be readable and seekable.", nameof(input));
        }

        var fileLength = input.Length;
        var data = new byte[Math.Min(MaximumHeaderLength, checked((int)Math.Min(fileLength, MaximumHeaderLength)))];
        var read = 0;
        while (read < data.Length)
        {
            var count = await input.ReadAsync(data.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            read += count;
        }

        if (read < 12 || !data.AsSpan(0, 4).SequenceEqual("PSB\0"u8))
            return Failure("PSB_SIGNATURE_MISMATCH", "Input does not have the M2 PSB signature.");

        var version = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4));
        var expectedLength = version switch { 1 or 2 => 40, 3 => 44, 4 => 56, _ => 0 };
        if (expectedLength == 0)
            return Failure("PSB_VERSION_UNSUPPORTED", "PSB version is not supported by the bounded header reader.", version);
        if (read < expectedLength)
            return Failure("PSB_HEADER_TRUNCATED", "PSB ended before its complete header.", version);

        var headerLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
        var namesOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
        var stringsOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16));
        var chunkOffsetsOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(24));
        var chunkLengthsOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(28));
        var chunkDataOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(32));
        var entriesOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(36));
        var headerMayBeEncrypted = headerLength > MaximumHeaderLength + 16 || namesOffset == 0 || (version > 1 && headerLength is not 0 && headerLength != namesOffset);
        if (headerMayBeEncrypted)
            return new PsbHeaderValidationResult(EvidenceStage.ContainerIdentified, version, true, headerLength, namesOffset, entriesOffset, chunkOffsetsOffset, chunkLengthsOffset, chunkDataOffset,
                [new KiriScopeDiagnostic("PSB_HEADER_MAY_BE_ENCRYPTED", DiagnosticSeverity.Warning, "PSB signature is valid, but header offsets indicate header protection or a nonstandard layout.")]);

        var offsets = new[] { namesOffset, stringsOffset, chunkOffsetsOffset, chunkLengthsOffset, chunkDataOffset, entriesOffset };
        if (offsets.Any(offset => offset >= fileLength))
            return Failure("PSB_OFFSET_OUT_OF_RANGE", "A PSB header offset points outside the file.", version, headerLength, namesOffset, entriesOffset, chunkDataOffset);

        if (version >= 3)
        {
            var expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(40));
            var actualChecksum = Adler32(data.AsSpan(8, 32));
            if (version == 4) actualChecksum = Adler32(data.AsSpan(44, 12), actualChecksum);
            if (expectedChecksum != actualChecksum)
                return Failure("PSB_HEADER_CHECKSUM_MISMATCH", "PSB header Adler-32 checksum does not match its offset fields.", version, headerLength, namesOffset, entriesOffset, chunkDataOffset);
        }

        return new PsbHeaderValidationResult(EvidenceStage.ContainerIdentified, version, false, headerLength, namesOffset, entriesOffset, chunkOffsetsOffset, chunkLengthsOffset, chunkDataOffset,
            [new KiriScopeDiagnostic("PSB_HEADER_IDENTIFIED", DiagnosticSeverity.Info, "Plain PSB header fields and applicable checksum were validated. Body objects and image data have not been decoded.")]);
    }

    private static PsbHeaderValidationResult Failure(string code, string message, ushort? version = null, uint? headerLength = null, uint? namesOffset = null, uint? entriesOffset = null, uint? chunkDataOffset = null) =>
        new(EvidenceStage.Unidentified, version, false, headerLength, namesOffset, entriesOffset, null, null, chunkDataOffset, [new KiriScopeDiagnostic(code, DiagnosticSeverity.Error, message)]);

    private static uint Adler32(ReadOnlySpan<byte> data, uint prior = 1)
    {
        var a = prior & 0xffff;
        var b = prior >> 16;
        foreach (var value in data) { a = (a + value) % 65521; b = (b + a) % 65521; }
        return (b << 16) | a;
    }
}
