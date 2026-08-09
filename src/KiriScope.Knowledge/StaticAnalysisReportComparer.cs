using System.Text.Json;
using System.Text.Json.Serialization;
using KiriScope.Analysis;
using KiriScope.Core.Diagnostics;

namespace KiriScope.Knowledge;

/// <summary>One factual static-analysis delta; it never represents a compatibility conclusion.</summary>
public sealed record StaticAnalysisDifference(
    string Category,
    string ChangeKind,
    string Identifier,
    string? LeftValue,
    string? RightValue,
    AnalysisFindingKind? FindingKind = null);

/// <summary>Offline comparison of two immutable static-analysis archives.</summary>
public sealed record StaticAnalysisComparisonReport(
    string SchemaVersion,
    DateTimeOffset ComparedAtUtc,
    string LeftArchivePath,
    string RightArchivePath,
    AnalysisInputIdentity LeftInput,
    AnalysisInputIdentity RightInput,
    IReadOnlyList<StaticAnalysisDifference> Differences,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics,
    string? ReproductionCommand = null)
{
    public const string CurrentSchemaVersion = "1.0";
}

/// <summary>Reads static archives and compares direct metadata without executing or rescanning either input.</summary>
public static class StaticAnalysisReportComparer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<StaticAnalysisComparisonReport> CompareFilesAsync(
        string leftArchivePath,
        string rightArchivePath,
        CancellationToken cancellationToken = default)
    {
        var fullLeftPath = Path.GetFullPath(leftArchivePath);
        var fullRightPath = Path.GetFullPath(rightArchivePath);
        var left = await ReadArchiveAsync(fullLeftPath, cancellationToken).ConfigureAwait(false);
        var right = await ReadArchiveAsync(fullRightPath, cancellationToken).ConfigureAwait(false);
        return Compare(fullLeftPath, left, fullRightPath, right);
    }

    public static StaticAnalysisComparisonReport Compare(
        string leftArchivePath,
        ResearchAnalysisArchive left,
        string rightArchivePath,
        ResearchAnalysisArchive right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var differences = new List<StaticAnalysisDifference>();
        AddValueDifference(differences, "Input", "Sha256", left.Report.Input.Sha256, right.Report.Input.Sha256);
        AddValueDifference(differences, "Input", "Length", left.Report.Input.Length.ToString(), right.Report.Input.Length.ToString());
        AddValueDifference(differences, "PE", "Machine", left.Report.Pe?.Machine, right.Report.Pe?.Machine);

        AddSetDifferences(
            differences,
            "PE import",
            left.Report.Pe?.ImportedModules ?? Array.Empty<string>(),
            right.Report.Pe?.ImportedModules ?? Array.Empty<string>());

        var leftFindings = ToFindingMap(left.Report.Findings);
        var rightFindings = ToFindingMap(right.Report.Findings);
        foreach (var key in leftFindings.Keys.Concat(rightFindings.Keys).Distinct().Order())
        {
            var hasLeft = leftFindings.TryGetValue(key, out var leftFinding);
            var hasRight = rightFindings.TryGetValue(key, out var rightFinding);
            if (hasLeft && hasRight)
            {
                continue;
            }

            var finding = leftFinding ?? rightFinding!;
            differences.Add(new StaticAnalysisDifference(
                "Finding",
                hasRight ? "Added" : "Removed",
                finding.Id,
                leftFinding?.Summary,
                rightFinding?.Summary,
                finding.Kind));
        }

        var diagnostics = new List<KiriScopeDiagnostic>();
        if (!string.Equals(left.Report.Input.Sha256, right.Report.Input.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new KiriScopeDiagnostic("STATIC_COMPARISON_INPUT_CHANGED", DiagnosticSeverity.Info, "The static-analysis archives describe different input SHA-256 values."));
        }

        return new StaticAnalysisComparisonReport(
            StaticAnalysisComparisonReport.CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            leftArchivePath,
            rightArchivePath,
            left.Report.Input,
            right.Report.Input,
            differences,
            diagnostics);
    }

    private static async Task<ResearchAnalysisArchive> ReadArchiveAsync(string archivePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("Static-analysis archive does not exist.", archivePath);
        }

        try
        {
            await using var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var archive = await JsonSerializer.DeserializeAsync<ResearchAnalysisArchive>(input, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (archive is null || !string.Equals(archive.SchemaVersion, ResearchAnalysisArchive.CurrentSchemaVersion, StringComparison.Ordinal))
            {
                throw new KnowledgeBaseException("STATIC_COMPARISON_ARCHIVE_INVALID", "Input is not a supported static-analysis archive.");
            }

            return archive;
        }
        catch (JsonException exception)
        {
            throw new KnowledgeBaseException("STATIC_COMPARISON_ARCHIVE_JSON_INVALID", exception.Message);
        }
    }

    private static void AddValueDifference(List<StaticAnalysisDifference> differences, string category, string identifier, string? left, string? right)
    {
        if (!string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            differences.Add(new StaticAnalysisDifference(category, "Changed", identifier, left, right));
        }
    }

    private static void AddSetDifferences(List<StaticAnalysisDifference> differences, string category, IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var leftValues = left.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rightValues = right.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var value in leftValues.Except(rightValues, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            differences.Add(new StaticAnalysisDifference(category, "Removed", value, value, null));
        }

        foreach (var value in rightValues.Except(leftValues, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            differences.Add(new StaticAnalysisDifference(category, "Added", value, null, value));
        }
    }

    private static IReadOnlyDictionary<string, StaticAnalysisFinding> ToFindingMap(IReadOnlyList<StaticAnalysisFinding> findings) => findings
        .GroupBy(static finding => finding.Kind + "|" + finding.Id, StringComparer.Ordinal)
        .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
}
