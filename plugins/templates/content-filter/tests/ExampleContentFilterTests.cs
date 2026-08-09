using Example.ContentFilter;
using KiriScope.Plugins.Abstractions.Filters;

namespace Example.ContentFilter.Tests;

// Copy this skeleton into an xUnit project when turning the template into a plugin.
public sealed class ExampleContentFilterTests
{
    // [Fact]
    public async Task Transform_IsDeterministicAndHonorsCancellation()
    {
        var filter = new ExampleContentFilter();
        var context = new ContentFilterContext("synthetic.bin", null, 0, 0);
        var first = new byte[] { 1, 2, 3 };
        var second = first.ToArray();

        await filter.TransformAsync(context, first);
        await filter.TransformAsync(context, second);

        if (!first.SequenceEqual(second))
        {
            throw new InvalidOperationException("A content filter must be deterministic for identical input and context.");
        }
    }
}
