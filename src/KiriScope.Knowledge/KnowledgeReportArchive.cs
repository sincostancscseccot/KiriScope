using System.Text.Json;
using System.Text.Json.Serialization;
using KiriScope.Core.Diagnostics;

namespace KiriScope.Knowledge;

/// <summary>Writes knowledge reports to new JSON files and never replaces old conclusions.</summary>
public static class KnowledgeReportArchiveWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<string> WriteNewAsync<T>(string outputPath, T report, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(report);
        var fullPath = Path.GetFullPath(outputPath);
        if (File.Exists(fullPath))
        {
            throw new IOException($"Knowledge report already exists and will not be overwritten: {fullPath}");
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("Knowledge report output path has no parent directory.", nameof(outputPath));
        }

        Directory.CreateDirectory(directory);
        var temporaryPath = fullPath + ".kiriscope-partial-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(output, report, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, fullPath, overwrite: false);
            return fullPath;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

/// <summary>Offline comparison of scan reports; it does not rescan or modify either input.</summary>
public static class KnowledgeBatchReportComparer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<KnowledgeBatchComparisonReport> CompareFilesAsync(string leftPath, string rightPath, CancellationToken cancellationToken = default)
    {
        var fullLeftPath = Path.GetFullPath(leftPath);
        var fullRightPath = Path.GetFullPath(rightPath);
        var left = await ReadAsync(fullLeftPath, cancellationToken).ConfigureAwait(false);
        var right = await ReadAsync(fullRightPath, cancellationToken).ConfigureAwait(false);
        return Compare(fullLeftPath, left, fullRightPath, right);
    }

    public static KnowledgeBatchComparisonReport Compare(string leftPath, KnowledgeBatchScanReport left, string rightPath, KnowledgeBatchScanReport right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var leftItems = left.Items.ToDictionary(static item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        var rightItems = right.Items.ToDictionary(static item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        var differences = new List<KnowledgeScanDifference>();
        foreach (var relativePath in leftItems.Keys.Concat(rightItems.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            var hasLeft = leftItems.TryGetValue(relativePath, out var leftItem);
            var hasRight = rightItems.TryGetValue(relativePath, out var rightItem);
            var leftCandidates = hasLeft ? leftItem!.Candidates.Select(static candidate => candidate.SchemeId + "@" + candidate.SchemeRevision).ToArray() : Array.Empty<string>();
            var rightCandidates = hasRight ? rightItem!.Candidates.Select(static candidate => candidate.SchemeId + "@" + candidate.SchemeRevision).ToArray() : Array.Empty<string>();
            var changed = hasLeft && hasRight && (!string.Equals(leftItem!.Sha256, rightItem!.Sha256, StringComparison.OrdinalIgnoreCase) || !leftCandidates.SequenceEqual(rightCandidates, StringComparer.Ordinal));
            if (!hasLeft && hasRight || hasLeft && !hasRight || changed)
            {
                differences.Add(new KnowledgeScanDifference(
                    relativePath,
                    hasLeft && hasRight ? "Changed" : hasRight ? "Added" : "Removed",
                    leftItem?.Sha256,
                    rightItem?.Sha256,
                    leftCandidates,
                    rightCandidates));
            }
        }

        var diagnostics = new List<KiriScopeDiagnostic>();
        if (!string.Equals(left.KnowledgeBase.ManifestSha256, right.KnowledgeBase.ManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new KiriScopeDiagnostic("KNOWLEDGE_COMPARISON_LIBRARY_CHANGED", DiagnosticSeverity.Warning, "The two scan reports were produced by different knowledge-base manifest revisions."));
        }

        return new KnowledgeBatchComparisonReport(
            KnowledgeBatchComparisonReport.CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            leftPath,
            rightPath,
            left.KnowledgeBase,
            right.KnowledgeBase,
            differences,
            diagnostics);
    }

    private static async Task<KnowledgeBatchScanReport> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Knowledge scan report does not exist.", path);
        }

        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var report = await JsonSerializer.DeserializeAsync<KnowledgeBatchScanReport>(input, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (report is null || !string.Equals(report.SchemaVersion, KnowledgeBatchScanReport.CurrentSchemaVersion, StringComparison.Ordinal))
            {
                throw new KnowledgeBaseException("KNOWLEDGE_SCAN_REPORT_INVALID", "Input is not a supported knowledge scan report.");
            }

            return report;
        }
        catch (JsonException exception)
        {
            throw new KnowledgeBaseException("KNOWLEDGE_SCAN_REPORT_JSON_INVALID", exception.Message);
        }
    }
}
