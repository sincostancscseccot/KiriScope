namespace KiriScope.Xp3;

/// <summary>Conservative limits for creating a new, standard unencrypted XP3 archive.</summary>
public sealed record Xp3ArchivePackOptions
{
    /// <summary>Maximum number of regular files accepted from one staging directory.</summary>
    public int MaximumEntryCount { get; init; } = 250_000;

    /// <summary>Maximum bytes allowed for one source file.</summary>
    public long MaximumFileBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Maximum aggregate bytes copied into the newly created archive.</summary>
    public long MaximumTotalBytes { get; init; } = 16L * 1024 * 1024 * 1024;
}
