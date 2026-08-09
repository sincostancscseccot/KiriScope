using KiriScope.IO.Paths;

namespace KiriScope.Core.Tests;

public sealed class SafeOutputPathTests
{
    [Fact]
    public void Resolve_CombinesAValidRelativeArchivePath()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), "KiriScopeTests", "output");

        var result = SafeOutputPath.Resolve(outputRoot, "image/background/title.png");

        Assert.Equal(Path.Combine(Path.GetFullPath(outputRoot), "image", "background", "title.png"), result);
    }

    [Theory]
    [InlineData("../outside.bin")]
    [InlineData("C:\\Windows\\system32\\outside.bin")]
    public void Resolve_RejectsUnsafeArchivePaths(string archivePath)
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), "KiriScopeTests", "output");

        Assert.Throws<ArgumentException>(() => SafeOutputPath.Resolve(outputRoot, archivePath));
    }
}
