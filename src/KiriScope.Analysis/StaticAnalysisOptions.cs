namespace KiriScope.Analysis;

/// <summary>Resource limits for read-only static binary inspection.</summary>
public sealed record StaticAnalysisOptions
{
    public const int DefaultMaximumFileBytes = 256 * 1024 * 1024;

    public int MaximumFileBytes { get; init; } = DefaultMaximumFileBytes;

    public int MaximumStrings { get; init; } = 5_000;

    public int MinimumStringLength { get; init; } = 4;

    public int MaximumDisplayedStringLength { get; init; } = 256;
}
