namespace KiriScope.Plugins.Abstractions.Filters;

/// <summary>
/// Identifies an expected, reportable failure while a content filter is being applied.
/// </summary>
public sealed class ContentFilterException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
