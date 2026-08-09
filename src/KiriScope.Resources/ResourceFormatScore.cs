using KiriScope.Core.Diagnostics;
using KiriScope.Core.Evidence;

namespace KiriScope.Resources;

/// <summary>Evidence-backed score for one candidate's transformed content.</summary>
public sealed record ResourceFormatScore(
    ResourceFormat Format,
    EvidenceStage Stage,
    int Score,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics)
{
    public bool IsAccepted => Stage >= EvidenceStage.FormatValidated;
}
