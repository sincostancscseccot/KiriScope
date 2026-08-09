using KiriScope.Resources;

namespace KiriScope.Core.Tests;

public sealed class FreeMoteTlgConverterTests
{
    [Fact]
    public async Task ConvertAsync_RejectsAMissingToolWithoutCreatingOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), "KiriScope.Tests", Guid.NewGuid().ToString("N"));
        var input = Path.Combine(root, "source.tlg");
        var output = Path.Combine(root, "output.png");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllBytesAsync(input, [1, 2, 3]);

            var result = await FreeMoteTlgConverter.ConvertAsync(input, output, Path.Combine(root, "missing.exe"));

            Assert.False(result.Succeeded);
            Assert.False(File.Exists(output));
            Assert.Equal("FREEMOTE_TOOL_MISSING", Assert.Single(result.Diagnostics).Code);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
