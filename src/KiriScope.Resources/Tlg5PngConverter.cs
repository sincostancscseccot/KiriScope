using KiriScope.Core.Diagnostics;
using KiriScope.Core.Evidence;

namespace KiriScope.Resources;

/// <summary>Converts safely decoded standard TLG5 pixels to a newly-created and structurally verified PNG file.</summary>
public static class Tlg5PngConverter
{
    public static async Task<Tlg5PngConversionResult> ConvertAsync(
        string tlgPath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tlgPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        await using var input = new FileStream(tlgPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var decoded = await Tlg5Decoder.DecodeAsync(input, cancellationToken).ConfigureAwait(false);
        if (!decoded.Succeeded)
        {
            return new Tlg5PngConversionResult(decoded.Stage, false, 0, decoded.Diagnostics);
        }

        var encoded = PngRgbaEncoder.Encode(decoded.Image!);
        await using (var validationInput = new MemoryStream(encoded, writable: false))
        {
            var validation = await PngValidator.ValidateAsync(validationInput, cancellationToken).ConfigureAwait(false);
            if (!validation.IsValid)
            {
                return new Tlg5PngConversionResult(
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

        return new Tlg5PngConversionResult(
            EvidenceStage.FormatValidated,
            true,
            encoded.Length,
            [new KiriScopeDiagnostic("TLG5_CONVERTED_TO_PNG", DiagnosticSeverity.Info, "Decoded TLG5 was written as a newly-created and structurally verified PNG.")]);
    }
}

public sealed record Tlg5PngConversionResult(EvidenceStage Stage, bool Succeeded, long BytesWritten, IReadOnlyList<KiriScopeDiagnostic> Diagnostics);
