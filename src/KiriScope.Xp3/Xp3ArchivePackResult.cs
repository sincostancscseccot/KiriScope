using KiriScope.Core.Diagnostics;

namespace KiriScope.Xp3;

/// <summary>Hash-identified source file included unchanged in a newly built XP3 archive.</summary>
public sealed record Xp3PackedEntry(
    string SourcePath,
    string EntryName,
    long Length,
    string Sha256,
    uint Adler32);

/// <summary>Traceable result of a new-only standard XP3 packing operation.</summary>
public sealed record Xp3ArchivePackResult(
    string OutputPath,
    string ArchiveSha256,
    long IndexOffset,
    long ArchiveLength,
    IReadOnlyList<Xp3PackedEntry> Entries,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics);
