using KiriScope.Core.Diagnostics;
using KiriScope.Core.Evidence;

namespace KiriScope.Resources;

/// <summary>Decodes validated 24-bit and 32-bit BI_RGB BMP pixels into top-down RGBA rows.</summary>
public static class BmpImageDecoder
{
    private const uint BiRgb = 0;
    private const long MaximumDecodedBytes = 256L * 1024 * 1024;

    public static async Task<BmpDecodeResult> DecodeAsync(Stream input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead || !input.CanSeek)
        {
            throw new ArgumentException("Input stream must be readable and seekable.", nameof(input));
        }

        input.Position = 0;
        var validation = await BmpValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return new BmpDecodeResult(validation.Stage, null, validation.Diagnostics);
        }

        if (validation.Compression != BiRgb || validation.BitCount is not (24 or 32) || validation.Width is null || validation.Height is null)
        {
            return new BmpDecodeResult(
                validation.Stage,
                null,
                [new KiriScopeDiagnostic("BMP_DECODE_FORMAT_UNSUPPORTED", DiagnosticSeverity.Warning, "BMP is structurally valid but only 24-bit and 32-bit BI_RGB pixels can be decoded currently.")]);
        }

        var width = validation.Width.Value;
        var height = validation.Height.Value;
        var absoluteHeight = Math.Abs((long)height);
        long decodedLength;
        try
        {
            decodedLength = checked((long)width * absoluteHeight * 4);
        }
        catch (OverflowException)
        {
            return TooLarge(validation);
        }

        if (decodedLength > MaximumDecodedBytes || decodedLength > int.MaxValue)
        {
            return TooLarge(validation);
        }

        var bitsPerRow = checked((long)width * validation.BitCount.Value);
        var sourceRowLength = checked((int)(((bitsPerRow + 31) / 32) * 4));
        var sourceRow = new byte[sourceRowLength];
        var pixels = new byte[checked((int)decodedLength)];
        input.Position = validation.PixelDataOffset;
        for (var sourceRowIndex = 0; sourceRowIndex < absoluteHeight; sourceRowIndex++)
        {
            await ReadExactlyAsync(input, sourceRow, cancellationToken).ConfigureAwait(false);
            var destinationRowIndex = height > 0 ? absoluteHeight - sourceRowIndex - 1 : sourceRowIndex;
            var destinationOffset = checked((int)(destinationRowIndex * width * 4));
            for (var column = 0; column < width; column++)
            {
                var sourceOffset = column * (validation.BitCount.Value / 8);
                var destination = destinationOffset + column * 4;
                pixels[destination] = sourceRow[sourceOffset + 2];
                pixels[destination + 1] = sourceRow[sourceOffset + 1];
                pixels[destination + 2] = sourceRow[sourceOffset];
                pixels[destination + 3] = validation.BitCount == 32 ? sourceRow[sourceOffset + 3] : byte.MaxValue;
            }
        }

        return new BmpDecodeResult(
            EvidenceStage.ContentUsable,
            new RgbaImage(width, checked((int)absoluteHeight), pixels),
            [new KiriScopeDiagnostic("BMP_PIXELS_DECODED", DiagnosticSeverity.Info, "BMP pixel rows were decoded to top-down RGBA data.")]);
    }

    private static async Task ReadExactlyAsync(Stream input, Memory<byte> destination, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await input.ReadAsync(destination[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidDataException("BMP pixel data ended unexpectedly after structural validation.");
            }

            offset += read;
        }
    }

    private static BmpDecodeResult TooLarge(BmpValidationResult validation) =>
        new(
            validation.Stage,
            null,
            [new KiriScopeDiagnostic("BMP_DECODE_TOO_LARGE", DiagnosticSeverity.Warning, "BMP dimensions exceed the configured safe decoded-image limit.")]);
}

public sealed record RgbaImage(int Width, int Height, byte[] Pixels);

public sealed record BmpDecodeResult(EvidenceStage Stage, RgbaImage? Image, IReadOnlyList<KiriScopeDiagnostic> Diagnostics)
{
    public bool Succeeded => Image is not null && Stage >= EvidenceStage.ContentUsable;
}
