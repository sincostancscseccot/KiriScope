namespace KiriScope.Plugins.Abstractions.Filters;

/// <summary>
/// Stable, reportable identity for a concrete set of parameters applied through one algorithm.
/// </summary>
public sealed record ContentFilterSchemeDescriptor(
    string Id,
    string DisplayName,
    string AlgorithmId,
    string AlgorithmVersion,
    ContentFilterParameterSource ParameterSource);
