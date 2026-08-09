using System.Text.Json;
using KiriScope.Plugins.Abstractions.Filters;

namespace Example.ContentFilter;

/// <summary>
/// Deterministic no-op reference. Replace TransformAsync with a buffer-only algorithm.
/// </summary>
public sealed class ExampleContentFilter : IContentFilter
{
    public ContentFilterDescriptor Descriptor { get; } = new(
        "example.content-filter",
        "Example content filter",
        "0.1.0");

    public ValueTask TransformAsync(
        ContentFilterContext context,
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        // Intentionally leaves buffer unchanged. Do not read or write external files here.
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A parameter-validation shape for a future explicit host registration. It is not auto-loaded.
/// </summary>
public static class ExampleContentFilterFactory
{
    public static IContentFilter Create(JsonElement parameters)
    {
        if (parameters.ValueKind is not JsonValueKind.Object)
        {
            throw new ArgumentException("Plugin parameters must be a JSON object.", nameof(parameters));
        }

        return new ExampleContentFilter();
    }
}
