using KiriScope.Core.Diagnostics;
using KiriScope.Core.Evidence;

namespace KiriScope.Resources;

/// <summary>Converts a safely decoded BMP into a newly-created and structurally verified PNG file.</summary>
public static class BmpPngConverter
{
    public static async Task<BmpPngConversionResult> ConvertAsync(
        string bmpPath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bmpPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        await using var input = new FileStream(bmpPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var decoded = await BmpImageDecoder.DecodeAsync(input, cancellationToken).ConfigureAwait(false);
        if (!decoded.Succeeded)
        {
            return new BmpPngConversionResult(decoded.Stage, false, 0, decoded.Diagnostics);
        }

        var encoded = PngRgbaEncoder.Encode(decoded.Image!);
        await using (var validationInput = new MemoryStream(encoded, writable: false))
        {
            var validation = await PngValidator.ValidateAsync(validationInput, cancellationToken).ConfigureAwait(false);
            if (!validation.IsValid)
            {
                return new BmpPngConversionResult(
                    EvidenceStage.ContentUsable,
                    false,
                    0,
                    [new KiriScopeDiagnostic("PNG_OUTPUT_VALIDATION_FAILED", DiagnosticSeverity.Error, "Converted PNG did not pass the project validator.")]);
            }
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("Output path must have a parent directory.", nameof(outputPath));
        }

        Directory.CreateDirectory(directory);
        var temporaryPath = outputPath + ".kiriscope-partial-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await output.WriteAsync(encoded, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, outputPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }

        return new BmpPngConversionResult(
            EvidenceStage.FormatValidated,
            true,
            encoded.Length,
            [new KiriScopeDiagnostic("BMP_CONVERTED_TO_PNG", DiagnosticSeverity.Info, "Decoded BMP was written as a newly-created and structurally verified PNG.")]);
    }
}

public sealed record BmpPngConversionResult(EvidenceStage Stage, bool Succeeded, long BytesWritten, IReadOnlyList<KiriScopeDiagnostic> Diagnostics);
