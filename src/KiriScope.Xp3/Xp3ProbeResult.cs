using KiriScope.Core.Diagnostics;
using KiriScope.Core.Evidence;

namespace KiriScope.Xp3;

public sealed record Xp3ProbeResult(
    EvidenceStage Stage,
    long? IndexOffset,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics)
{
    public bool IsXp3 => Stage >= EvidenceStage.ContainerIdentified;
}
