namespace KiriScope.Plugins.Abstractions.Filters;

/// <summary>
/// Transforms an entry buffer in place after segment decompression and before it reaches output.
/// Implementations must be deterministic for identical context and bytes.
/// </summary>
public interface IContentFilter
{
    ContentFilterDescriptor Descriptor { get; }

    ValueTask TransformAsync(
        ContentFilterContext context,
        Memory<byte> buffer,
        CancellationToken cancellationToken = default);
}
