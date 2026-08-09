namespace KiriScope.Plugins.Abstractions.Filters;

/// <summary>
/// Per-buffer context. LogicalOffset is relative to the decoded entry, not to a packed segment.
/// </summary>
public sealed record ContentFilterContext(
    string EntryName,
    uint? Adler32,
    int SegmentIndex,
    long LogicalOffset);
