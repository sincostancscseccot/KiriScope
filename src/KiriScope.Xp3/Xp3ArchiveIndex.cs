using KiriScope.Core.Diagnostics;
using KiriScope.Core.Evidence;

namespace KiriScope.Xp3;

public sealed record Xp3ArchiveIndex(
    EvidenceStage Stage,
    long IndexOffset,
    bool IsIndexCompressed,
    IReadOnlyList<Xp3Entry> Entries,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics)
{
    /// <summary>
    /// Optional original filename mappings recovered from a protected XP3 name-list section.
    /// Keys are XP3 <c>adlr</c> values.  The collection is empty for ordinary archives.
    /// </summary>
    public IReadOnlyDictionary<uint, string> NameMappings { get; init; } =
        new Dictionary<uint, string>();
}
