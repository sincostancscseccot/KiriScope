using System.Buffers.Binary;
using KiriScope.Core.Diagnostics;
using KiriScope.Core.Evidence;

namespace KiriScope.Resources;

/// <summary>
/// Validates the file, DIB, palette, and pixel-data ranges of common Windows BMP files.
/// Compressed and legacy DIB variants remain identified containers until a decoder is available.
/// </summary>
public static class BmpValidator
{
    private const int FileHeaderLength = 14;
    private const int BitmapInfoHeaderLength = 40;
    private const uint BiRgb = 0;
    private const uint BiRle8 = 1;
    private const uint BiRle4 = 2;
    private const uint BiBitfields = 3;
    private const uint BiJpeg = 4;
    private const uint BiPng = 5;
    private const uint BiAlphaBitfields = 6;

    public static async Task<BmpValidationResult> ValidateAsync(
        Stream input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead || !input.CanSeek)
        {
            throw new ArgumentException("Input stream must be readable and seekable.", nameof(input));
        }

        var fileLength = input.Length;
        if (fileLength < FileHeaderLength)
        {
            return Failure("BMP_FILE_HEADER_TRUNCATED", "BMP ended before the complete file header.", EvidenceStage.Unidentified);
        }

        input.Position = 0;
        var fileHeader = await ReadExactlyAsync(input, FileHeaderLength, cancellationToken).ConfigureAwait(false);
        if (!fileHeader.AsSpan(0, 2).SequenceEqual("BM"u8))
        {
            return Failure("BMP_SIGNATURE_MISMATCH", "Input does not have the BMP file signature.", EvidenceStage.Unidentified);
        }

        var declaredFileLength = BinaryPrimitives.ReadUInt32LittleEndian(fileHeader.AsSpan(2, sizeof(uint)));
        var pixelDataOffset = BinaryPrimitives.ReadUInt32LittleEndian(fileHeader.AsSpan(10, sizeof(uint)));
        if (declaredFileLength != 0 && declaredFileLength > fileLength)
        {
            return Failure("BMP_DECLARED_LENGTH_INVALID", "BMP declared file length exceeds the input length.");
        }

        if (fileLength < FileHeaderLength + sizeof(uint))
        {
            return Failure("BMP_DIB_HEADER_TRUNCATED", "BMP ended before its DIB header length.");
        }

        var dibLengthBytes = await ReadExactlyAsync(input, sizeof(uint), cancellationToken).ConfigureAwait(false);
        var dibLength = BinaryPrimitives.ReadUInt32LittleEndian(dibLengthBytes);
        if (dibLength < BitmapInfoHeaderLength)
        {
            return Failure("BMP_DIB_HEADER_UNSUPPORTED", "BMP uses a legacy DIB header that is not validated yet.");
        }

        if (dibLength > fileLength - FileHeaderLength)
        {
            return Failure("BMP_DIB_HEADER_TRUNCATED", "BMP DIB header extends beyond the input length.");
        }

        if (dibLength > int.MaxValue)
        {
            return Failure("BMP_DIB_HEADER_TOO_LARGE", "BMP DIB header is excessively large.");
        }

        input.Position = FileHeaderLength;
        var dib = await ReadExactlyAsync(input, checked((int)dibLength), cancellationToken).ConfigureAwait(false);
        var width = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(4, sizeof(int)));
        var height = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8, sizeof(int)));
        var planes = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(12, sizeof(ushort)));
        var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14, sizeof(ushort)));
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(16, sizeof(uint)));
        var imageSize = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(20, sizeof(uint)));
        var colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(32, sizeof(uint)));

        if (width <= 0 || height is 0 or int.MinValue || planes != 1 || !IsSupportedBitCount(bitCount))
        {
            return Failure("BMP_HEADER_VALUES_INVALID", "BMP dimensions, plane count, or bit depth are invalid.", width, height, bitCount, compression, pixelDataOffset);
        }

        if (!IsKnownCompression(compression) || !IsCompressionCompatible(compression, bitCount, height))
        {
            return Failure("BMP_COMPRESSION_INVALID", "BMP compression is unknown or incompatible with its bit depth.", width, height, bitCount, compression, pixelDataOffset);
        }

        if (pixelDataOffset < FileHeaderLength + dibLength || pixelDataOffset > fileLength)
        {
            return Failure("BMP_PIXEL_OFFSET_INVALID", "BMP pixel-data offset is outside the file or overlaps the DIB header.", width, height, bitCount, compression, pixelDataOffset);
        }

        var paletteEntries = colorsUsed != 0 ? colorsUsed : bitCount <= 8 ? 1U << bitCount : 0;
        if (paletteEntries > (bitCount <= 8 ? 1U << bitCount : 0))
        {
            return Failure("BMP_PALETTE_INVALID", "BMP palette contains more colors than its bit depth permits.", width, height, bitCount, compression, pixelDataOffset);
        }

        var requiredMetadataLength = checked((long)FileHeaderLength + dibLength + checked((long)paletteEntries * 4) + GetExternalMaskLength(dibLength, compression));
        if (requiredMetadataLength > pixelDataOffset)
        {
            return Failure("BMP_PALETTE_OR_MASK_TRUNCATED", "BMP palette or bit-field masks overlap pixel data.", width, height, bitCount, compression, pixelDataOffset);
        }

        if (compression is BiJpeg or BiPng or BiRle4 or BiRle8)
        {
            return new BmpValidationResult(
                EvidenceStage.ContainerIdentified,
                width,
                height,
                bitCount,
                compression,
                pixelDataOffset,
                imageSize,
                [new KiriScopeDiagnostic("BMP_COMPRESSED_CONTAINER_IDENTIFIED", DiagnosticSeverity.Info, "BMP header was validated, but its compressed pixel stream has not been decoded.")]);
        }

        long expectedPixelDataLength;
        try
        {
            var rowLength = checked(((checked((long)width * bitCount) + 31) / 32) * 4);
            expectedPixelDataLength = checked(rowLength * Math.Abs((long)height));
        }
        catch (OverflowException)
        {
            return Failure("BMP_PIXEL_DATA_TOO_LARGE", "BMP dimensions imply an unsupported pixel-data length.", width, height, bitCount, compression, pixelDataOffset);
        }

        var effectiveFileLength = declaredFileLength == 0 ? fileLength : declaredFileLength;
        if (expectedPixelDataLength > effectiveFileLength - pixelDataOffset || imageSize != 0 && imageSize != expectedPixelDataLength)
        {
            return Failure("BMP_PIXEL_DATA_TRUNCATED", "BMP pixel-data range is truncated or conflicts with the declared image size.", width, height, bitCount, compression, pixelDataOffset);
        }

        return new BmpValidationResult(
            EvidenceStage.FormatValidated,
            width,
            height,
            bitCount,
            compression,
            pixelDataOffset,
            expectedPixelDataLength,
            [new KiriScopeDiagnostic("BMP_VALIDATED", DiagnosticSeverity.Info, "BMP file, DIB header, metadata ranges, and uncompressed pixel-data range were verified.")]);
    }

    private static bool IsSupportedBitCount(ushort bitCount) => bitCount is 1 or 4 or 8 or 16 or 24 or 32;

    private static bool IsKnownCompression(uint compression) => compression is >= BiRgb and <= BiAlphaBitfields;

    private static bool IsCompressionCompatible(uint compression, ushort bitCount, int height) => compression switch
    {
        BiRgb => true,
        BiRle8 => bitCount == 8 && height > 0,
        BiRle4 => bitCount == 4 && height > 0,
        BiBitfields or BiAlphaBitfields => bitCount is 16 or 32,
        BiJpeg or BiPng => true,
        _ => false,
    };

    private static long GetExternalMaskLength(uint dibLength, uint compression) =>
        compression is BiBitfields or BiAlphaBitfields && dibLength == BitmapInfoHeaderLength
            ? compression == BiAlphaBitfields ? 16 : 12
            : 0;

    private static async Task<byte[]> ReadExactlyAsync(Stream input, int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidDataException("BMP ended unexpectedly.");
            }

            offset += read;
        }

        return buffer;
    }

    private static BmpValidationResult Failure(
        string code,
        string message,
        int? width,
        int? height,
        ushort? bitCount,
        uint? compression,
        long pixelDataOffset) =>
        Failure(code, message, EvidenceStage.ContainerIdentified, width, height, bitCount, compression, pixelDataOffset);

    private static BmpValidationResult Failure(
        string code,
        string message,
        EvidenceStage stage = EvidenceStage.ContainerIdentified,
        int? width = null,
        int? height = null,
        ushort? bitCount = null,
        uint? compression = null,
        long pixelDataOffset = 0) =>
        new(
            stage,
            width,
            height,
            bitCount,
            compression,
            pixelDataOffset,
            0,
            [new KiriScopeDiagnostic(code, DiagnosticSeverity.Error, message)]);
}
