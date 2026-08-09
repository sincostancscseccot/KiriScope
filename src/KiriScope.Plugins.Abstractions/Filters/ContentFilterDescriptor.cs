namespace KiriScope.Plugins.Abstractions.Filters;

/// <summary>Stable identity and capability statement for a content-filter plugin.</summary>
public sealed record ContentFilterDescriptor(
    string Id,
    string DisplayName,
    string Version);
