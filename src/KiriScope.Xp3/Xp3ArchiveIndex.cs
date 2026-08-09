using KiriScope.Core.Diagnostics;
using KiriScope.Core.Evidence;

namespace KiriScope.Xp3;

public sealed record Xp3ArchiveIndex(
    EvidenceStage Stage,
    long IndexOffset,
    bool IsIndexCompressed,
    IReadOnlyList<Xp3Entry> Entries,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics);
