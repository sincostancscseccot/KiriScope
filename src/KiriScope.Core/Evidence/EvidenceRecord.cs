namespace KiriScope.Core.Evidence;

/// <summary>An immutable, human-readable fact recorded while analysing an input.</summary>
public sealed record EvidenceRecord(
    EvidenceStage Stage,
    string Source,
    string Summary,
    DateTimeOffset ObservedAtUtc)
{
    public static EvidenceRecord Create(EvidenceStage stage, string source, string summary) =>
        new(stage, source, summary, DateTimeOffset.UtcNow);
}
