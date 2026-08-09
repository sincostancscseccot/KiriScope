namespace KiriScope.Xp3;

public sealed record Xp3ReadOptions
{
    public const long DefaultMaximumIndexSize = 64L * 1024 * 1024;

    /// <summary>Maximum decompressed index size accepted from an untrusted archive.</summary>
    public long MaximumIndexSize { get; init; } = DefaultMaximumIndexSize;

    /// <summary>Maximum number of entries accepted from one archive index.</summary>
    public int MaximumEntryCount { get; init; } = 250_000;
}
