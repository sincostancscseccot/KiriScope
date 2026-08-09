using KiriScope.Core.Diagnostics;

namespace KiriScope.Runtime;

/// <summary>Compares two imported, hash-identified file-access reports without replaying either capture.</summary>
public static class RuntimeFileAccessComparer
{
    public static RuntimeFileAccessComparisonReport Compare(RuntimeFileAccessReport left, RuntimeFileAccessReport right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.TargetProcessId != right.TargetProcessId)
        {
            throw new ArgumentException("Offline file-access reports must use the same target PID.");
        }

        var leftCounts = Count(left.Observations);
        var rightCounts = Count(right.Observations);
        var differences = leftCounts.Keys
            .Concat(rightCounts.Keys)
            .Distinct()
            .OrderBy(static key => key.Operation, StringComparer.Ordinal)
            .ThenBy(static key => key.Path, StringComparer.Ordinal)
            .ThenBy(static key => key.Result, StringComparer.Ordinal)
            .Select(key => new RuntimeFileAccessDifference(
                key.Operation,
                key.Path,
                key.Result,
                leftCounts.GetValueOrDefault(key),
                rightCounts.GetValueOrDefault(key)))
            .Where(static difference => difference.LeftCount != difference.RightCount)
            .ToArray();
        return new RuntimeFileAccessComparisonReport(
            left.TargetProcessId,
            DateTimeOffset.UtcNow,
            left.Source,
            right.Source,
            left.Observations.Count,
            right.Observations.Count,
            differences,
            Array.Empty<KiriScopeDiagnostic>());
    }

    private static IReadOnlyDictionary<FileAccessKey, int> Count(IReadOnlyList<RuntimeFileAccessEvidence> observations)
    {
        var counts = new Dictionary<FileAccessKey, int>();
        foreach (var observation in observations)
        {
            var key = new FileAccessKey(observation.Operation, observation.Path, observation.Result);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        return counts;
    }

    private sealed record FileAccessKey(string Operation, string Path, string Result);
}

/// <summary>Count difference for one operation/path/result tuple between two offline evidence inputs.</summary>
public sealed record RuntimeFileAccessDifference(string Operation, string Path, string Result, int LeftCount, int RightCount);

/// <summary>Offline contrast of two source-hash-identified file-access imports.</summary>
public sealed record RuntimeFileAccessComparisonReport(
    int TargetProcessId,
    DateTimeOffset ComparedAtUtc,
    RuntimeExternalEvidenceSource LeftSource,
    RuntimeExternalEvidenceSource RightSource,
    int LeftObservationCount,
    int RightObservationCount,
    IReadOnlyList<RuntimeFileAccessDifference> Differences,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics);
