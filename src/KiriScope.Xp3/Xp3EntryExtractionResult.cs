using KiriScope.Core.Diagnostics;
using KiriScope.Core.Evidence;

namespace KiriScope.Xp3;

public sealed record Xp3EntryExtractionResult(
    string EntryName,
    EvidenceStage Stage,
    bool Succeeded,
    long BytesWritten,
    uint? ExpectedAdler32,
    uint? ActualAdler32,
    string? ContentFilterId,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics);
