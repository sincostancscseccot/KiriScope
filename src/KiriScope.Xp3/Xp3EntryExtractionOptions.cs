using KiriScope.Plugins.Abstractions.Filters;

namespace KiriScope.Xp3;

public sealed record Xp3EntryExtractionOptions
{
    /// <summary>Content filter to use for entries marked encrypted.</summary>
    public IContentFilter? ContentFilter { get; init; }

    /// <summary>Allows research filters to run against unmarked entries; disabled by default.</summary>
    public bool ApplyFilterToUnmarkedEntries { get; init; }

    /// <summary>
    /// Permits a marked entry to be read without a content filter. This is intended for
    /// KiriKiri variants that set the encrypted flag while using a no-op filter. Callers
    /// must keep plain-content Adler-32 verification enabled so a failed probe is never
    /// presented as extracted output.
    /// </summary>
    public bool AllowUnfilteredMarkedEntries { get; init; }

    /// <summary>Validates Adler-32 for plain extracted content when the index supplies it.</summary>
    public bool VerifyAdler32 { get; init; } = true;

    /// <summary>
    /// Enables Adler-32 validation after an active content filter. Disabled by default because
    /// KiriKiri variants differ on whether the checksum is pre- or post-filter.
    /// </summary>
    public bool VerifyAdler32AfterFilter { get; init; }
}
