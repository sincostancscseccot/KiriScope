using System.Buffers.Binary;
using System.IO.Compression;
using KiriScope.Core.Diagnostics;
using KiriScope.Core.Evidence;

namespace KiriScope.Resources;

/// <summary>
/// Validates PNG container integrity including all chunk CRCs and complete IDAT decompression.
/// It does not yet render pixels; that is an explicitly later ContentUsable-stage capability.
/// </summary>
public static class PngValidator
{
    public static ReadOnlySpan<byte> Signature =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
    ];

    private const uint Ihdr = 0x49484452;
    private const uint Idat = 0x49444154;
    private const uint Iend = 0x49454E44;
    private const long MaximumChunkLength = 128L * 1024 * 1024;
    private const long MaximumInflatedIdatLength = 512L * 1024 * 1024;

    public static async Task<PngValidationResult> ValidateAsync(
        Stream input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead)
        {
            throw new ArgumentException("Input stream must be readable.", nameof(input));
        }

        var diagnostics = new List<KiriScopeDiagnostic>();
        var signature = await TryReadExactlyAsync(input, Signature.Length, cancellationToken).ConfigureAwait(false);
        if (signature is null || !signature.AsSpan().SequenceEqual(Signature))
        {
            return Failure(
                "PNG_SIGNATURE_MISMATCH",
                "Input does not have the PNG signature.",
                stage: EvidenceStage.Unidentified);
        }

        var hasIhdr = false;
        var hasIdat = false;
        var hasIend = false;
        int? width = null;
        int? height = null;
        byte? bitDepth = null;
        byte? colorType = null;
        byte? interlaceMethod = null;
        long compressedBytes = 0;
        var idat = new MemoryStream();

        while (!hasIend)
        {
            var header = await TryReadExactlyAsync(input, 8, cancellationToken).ConfigureAwait(false);
            if (header is null)
            {
                return Failure("PNG_CHUNK_HEADER_TRUNCATED", "PNG ended before an IEND chunk.", width, height, bitDepth, colorType, compressedBytes);
            }

            var chunkLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, sizeof(uint)));
            var chunkType = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(sizeof(uint), sizeof(uint)));
            if (chunkLength > MaximumChunkLength)
            {
                return Failure("PNG_CHUNK_TOO_LARGE", "PNG contains an excessively large chunk.", width, height, bitDepth, colorType, compressedBytes);
            }

            var data = await TryReadExactlyAsync(input, checked((int)chunkLength), cancellationToken).ConfigureAwait(false);
            var crcBytes = await TryReadExactlyAsync(input, sizeof(uint), cancellationToken).ConfigureAwait(false);
            if (data is null || crcBytes is null)
            {
                return Failure("PNG_CHUNK_TRUNCATED", "PNG ended inside a chunk.", width, height, bitDepth, colorType, compressedBytes);
            }

            var expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(crcBytes);
            var actualCrc = Crc32.Compute(chunkType, data);
            if (expectedCrc != actualCrc)
            {
                return Failure("PNG_CRC_MISMATCH", "PNG chunk CRC does not match its contents.", width, height, bitDepth, colorType, compressedBytes);
            }

            if (chunkType == Ihdr)
            {
                if (hasIhdr || data.Length != 13)
                {
                    return Failure("PNG_IHDR_INVALID", "PNG has a duplicate or malformed IHDR chunk.", width, height, bitDepth, colorType, compressedBytes);
                }

                var encodedWidth = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, sizeof(uint)));
                var encodedHeight = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(sizeof(uint), sizeof(uint)));
                if (encodedWidth is 0 or > int.MaxValue || encodedHeight is 0 or > int.MaxValue)
                {
                    return Failure("PNG_IHDR_VALUES_INVALID", "PNG IHDR dimensions are outside the supported range.", compressedBytes: compressedBytes);
                }

                width = (int)encodedWidth;
                height = (int)encodedHeight;
                bitDepth = data[8];
                colorType = data[9];
                interlaceMethod = data[12];
                if (width <= 0 || height <= 0 || !IsValidHeader(bitDepth.Value, colorType.Value, data[10], data[11], interlaceMethod.Value))
                {
                    return Failure("PNG_IHDR_VALUES_INVALID", "PNG IHDR values are not supported by the PNG specification.", width, height, bitDepth, colorType, compressedBytes);
                }

                hasIhdr = true;
            }
            else if (chunkType == Idat)
            {
                if (!hasIhdr)
                {
                    return Failure("PNG_IDAT_BEFORE_IHDR", "PNG contains IDAT before IHDR.", width, height, bitDepth, colorType, compressedBytes);
                }

                hasIdat = true;
                compressedBytes += data.Length;
                await idat.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            }
            else if (chunkType == Iend)
            {
                if (data.Length != 0)
                {
                    return Failure("PNG_IEND_INVALID", "PNG IEND chunk must be empty.", width, height, bitDepth, colorType, compressedBytes);
                }

                hasIend = true;
            }
        }

        if (!hasIhdr || !hasIdat)
        {
            return Failure("PNG_REQUIRED_CHUNK_MISSING", "PNG is missing IHDR or IDAT.", width, height, bitDepth, colorType, compressedBytes);
        }

        try
        {
            var inflatedBytes = await ValidateIdatAsync(
                idat.GetBuffer().AsMemory(0, checked((int)idat.Length)),
                width!.Value,
                height!.Value,
                bitDepth!.Value,
                colorType!.Value,
                interlaceMethod!.Value,
                cancellationToken).ConfigureAwait(false);
            diagnostics.Add(new KiriScopeDiagnostic(
                "PNG_VALIDATED",
                DiagnosticSeverity.Info,
                "PNG signature, chunks, CRCs, and complete IDAT decompression were verified."));
            return new PngValidationResult(
                EvidenceStage.FormatValidated,
                width,
                height,
                bitDepth,
                colorType,
                compressedBytes,
                inflatedBytes,
                diagnostics);
        }
        catch (InvalidDataException exception)
        {
            return Failure("PNG_IDAT_DECOMPRESSION_FAILED", exception.Message, width, height, bitDepth, colorType, compressedBytes);
        }
    }

    private static async Task<long> ValidateIdatAsync(
        Memory<byte> compressedData,
        int width,
        int height,
        byte bitDepth,
        byte colorType,
        byte interlaceMethod,
        CancellationToken cancellationToken)
    {
        await using var compressed = new MemoryStream(compressedData.ToArray(), writable: false);
        await using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        var expectedLength = interlaceMethod == 0
            ? GetExpectedNonInterlacedLength(width, height, bitDepth, colorType)
            : (long?)null;
        var buffer = new byte[128 * 1024];
        long totalRead = 0;

        while (true)
        {
            var read = await zlib.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            totalRead = checked(totalRead + read);
            if (totalRead > MaximumInflatedIdatLength || (expectedLength is not null && totalRead > expectedLength.Value))
            {
                throw new InvalidDataException("PNG IDAT decompressed data exceeds its expected size.");
            }
        }

        if (expectedLength is not null && totalRead != expectedLength.Value)
        {
            throw new InvalidDataException("PNG IDAT decompressed data does not match the expected scanline size.");
        }

        return totalRead;
    }

    private static long GetExpectedNonInterlacedLength(int width, int height, byte bitDepth, byte colorType)
    {
        var channels = colorType switch
        {
            0 or 3 => 1,
            2 => 3,
            4 => 2,
            6 => 4,
            _ => throw new InvalidDataException("PNG color type is invalid."),
        };
        var bitsPerRow = checked((long)width * channels * bitDepth);
        var bytesPerRow = checked((bitsPerRow + 7) / 8);
        return checked((bytesPerRow + 1) * height);
    }

    private static bool IsValidHeader(byte bitDepth, byte colorType, byte compressionMethod, byte filterMethod, byte interlaceMethod) =>
        compressionMethod == 0 &&
        filterMethod == 0 &&
        interlaceMethod is 0 or 1 &&
        colorType switch
        {
            0 => bitDepth is 1 or 2 or 4 or 8 or 16,
            2 => bitDepth is 8 or 16,
            3 => bitDepth is 1 or 2 or 4 or 8,
            4 or 6 => bitDepth is 8 or 16,
            _ => false,
        };

    private static async Task<byte[]?> TryReadExactlyAsync(Stream input, int length, CancellationToken cancellationToken)
    {
        var result = new byte[length];
        var offset = 0;
        while (offset < result.Length)
        {
            var read = await input.ReadAsync(result.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            offset += read;
        }

        return result;
    }

    private static PngValidationResult Failure(
        string code,
        string message,
        int? width = null,
        int? height = null,
        byte? bitDepth = null,
        byte? colorType = null,
        long compressedBytes = 0,
        EvidenceStage stage = EvidenceStage.ContainerIdentified) =>
        new(
            stage,
            width,
            height,
            bitDepth,
            colorType,
            compressedBytes,
            0,
            [new KiriScopeDiagnostic(code, DiagnosticSeverity.Error, message)]);

    private static class Crc32
    {
        private const uint Polynomial = 0xEDB88320;

        public static uint Compute(uint chunkType, ReadOnlySpan<byte> data)
        {
            var crc = 0xFFFFFFFFU;
            Span<byte> type = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32BigEndian(type, chunkType);
            crc = Update(crc, type);
            crc = Update(crc, data);
            return ~crc;
        }

        private static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var value in data)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ Polynomial : crc >> 1;
                }
            }

            return crc;
        }
    }
}
