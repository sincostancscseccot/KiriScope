using KiriScope.Plugins.Abstractions.Filters;

namespace KiriScope.Filters.BuiltIn;

/// <summary>
/// A deliberately simple, opt-in reference filter used to exercise the plugin pipeline.
/// It is not a claim of compatibility with any specific KiriKiri title.
/// </summary>
public sealed class RepeatingXorContentFilter : IContentFilter
{
    private readonly byte[] _key;

    public RepeatingXorContentFilter(ReadOnlySpan<byte> key)
    {
        if (key.IsEmpty)
        {
            throw new ArgumentException("XOR key must not be empty.", nameof(key));
        }

        _key = key.ToArray();
    }

    public ContentFilterDescriptor Descriptor { get; } =
        new("builtin.repeating-xor", "Repeating XOR (reference)", "1.0");

    public ValueTask TransformAsync(
        ContentFilterContext context,
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var span = buffer.Span;
        for (var index = 0; index < span.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var keyIndex = (int)((context.LogicalOffset + index) % _key.Length);
            span[index] ^= _key[keyIndex];
        }

        return ValueTask.CompletedTask;
    }
}
