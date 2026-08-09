namespace KiriScope.Plugins.Abstractions.Filters;

/// <summary>
/// Records where a scheme's parameter values came from. Values themselves remain in the scheme file.
/// </summary>
public sealed record ContentFilterParameterSource(
    string Kind,
    string Reference,
    string? Notes = null);
