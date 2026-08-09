using KiriScope.Plugins.Abstractions.Filters;

namespace KiriScope.Xp3;

public sealed record Xp3EntryExtractionOptions
{
    /// <summary>Content filter to use for entries marked encrypted.</summary>
    public IContentFilter? ContentFilter { get; init; }

    /// <summary>Allows research filters to run against unmarked entries; disabled by default.</summary>
    public bool ApplyFilterToUnmarkedEntries { get; init; }

    /// <summary>Validates Adler-32 for plain extracted content when the index supplies it.</summary>
    public bool VerifyAdler32 { get; init; } = true;

    /// <summary>
    /// Enables Adler-32 validation after an active content filter. Disabled by default because
    /// KiriKiri variants differ on whether the checksum is pre- or post-filter.
    /// </summary>
    public bool VerifyAdler32AfterFilter { get; init; }
}
