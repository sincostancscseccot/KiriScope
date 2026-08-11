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

    /// <summary>
    /// Optional version-3 XP3 filename mappings. Keys are the opaque lowercase MD5 aliases stored
    /// in <c>info</c> sections, so this mapping remains unambiguous when several files share an
    /// Adler-32 content checksum.
    /// </summary>
    public IReadOnlyDictionary<string, string> HashedNameMappings { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
