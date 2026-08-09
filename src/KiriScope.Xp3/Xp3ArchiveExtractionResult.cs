using KiriScope.Core.Diagnostics;

namespace KiriScope.Xp3;

public sealed record Xp3ArchiveExtractionResult(
    bool IndexWasParsed,
    int ExtractedEntryCount,
    int SkippedEntryCount,
    IReadOnlyList<Xp3EntryExtractionResult> Entries,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics);
