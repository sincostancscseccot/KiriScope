using KiriScope.Core.Diagnostics;
using KiriScope.Core.Evidence;
using KiriScope.IO.Paths;

namespace KiriScope.Resources;

/// <summary>Converts supported bitmap resources in a directory tree without overwriting existing outputs.</summary>
public static class ResourceBatchPngConverter
{
    public static async Task<ResourceBatchPngConversionResult> ConvertDirectoryAsync(
        string inputDirectory,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var inputRoot = Path.GetFullPath(inputDirectory);
        var outputRoot = Path.GetFullPath(outputDirectory);
        if (!Directory.Exists(inputRoot)) throw new DirectoryNotFoundException($"Input directory does not exist: {inputRoot}");
        if (IsNestedWithin(outputRoot, inputRoot)) throw new ArgumentException("Output directory must not be inside the input directory.", nameof(outputDirectory));

        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        var plans = Directory.EnumerateFiles(inputRoot, "*", enumeration)
            .Where(IsSupportedInput)
            .Select(path => new ConversionPlan(path, Path.ChangeExtension(Path.GetRelativePath(inputRoot, path), ".png")))
            .ToArray();
        var conflicts = plans.GroupBy(plan => plan.RelativeOutputPath, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Select(plan => plan.InputPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var results = new List<ResourceBatchPngItemResult>(plans.Length);
        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (conflicts.Contains(plan.InputPath))
            {
                results.Add(new ResourceBatchPngItemResult(plan.InputPath, plan.RelativeOutputPath, EvidenceStage.RawDataExtracted, false, 0,
                    [new KiriScopeDiagnostic("BATCH_OUTPUT_COLLISION", DiagnosticSeverity.Error, "Another source maps to the same PNG output path.")]));
                continue;
            }

            var outputPath = SafeOutputPath.Resolve(outputRoot, plan.RelativeOutputPath);
            try
            {
                if (Path.GetExtension(plan.InputPath).Equals(".bmp", StringComparison.OrdinalIgnoreCase))
                {
                    var result = await BmpPngConverter.ConvertAsync(plan.InputPath, outputPath, cancellationToken).ConfigureAwait(false);
                    results.Add(new ResourceBatchPngItemResult(plan.InputPath, plan.RelativeOutputPath, result.Stage, result.Succeeded, result.BytesWritten, result.Diagnostics));
                }
                else
                {
                    var result = await Tlg5PngConverter.ConvertAsync(plan.InputPath, outputPath, cancellationToken).ConfigureAwait(false);
                    results.Add(new ResourceBatchPngItemResult(plan.InputPath, plan.RelativeOutputPath, result.Stage, result.Succeeded, result.BytesWritten, result.Diagnostics));
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                results.Add(new ResourceBatchPngItemResult(plan.InputPath, plan.RelativeOutputPath, EvidenceStage.RawDataExtracted, false, 0,
                    [new KiriScopeDiagnostic("BATCH_CONVERSION_FAILED", DiagnosticSeverity.Error, exception.Message)]));
            }
        }

        return new ResourceBatchPngConversionResult(results);
    }

    private static bool IsSupportedInput(string path) => Path.GetExtension(path) is ".bmp" or ".BMP" or ".tlg" or ".TLG";

    private static bool IsNestedWithin(string candidate, string root)
    {
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ConversionPlan(string InputPath, string RelativeOutputPath);
}

public sealed record ResourceBatchPngItemResult(string InputPath, string RelativeOutputPath, EvidenceStage Stage, bool Succeeded, long BytesWritten, IReadOnlyList<KiriScopeDiagnostic> Diagnostics);

public sealed record ResourceBatchPngConversionResult(IReadOnlyList<ResourceBatchPngItemResult> Items)
{
    public int ConvertedCount => Items.Count(item => item.Succeeded);
    public int FailedCount => Items.Count(item => !item.Succeeded);
}
