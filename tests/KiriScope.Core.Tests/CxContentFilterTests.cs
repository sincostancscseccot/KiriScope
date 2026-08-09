using KiriScope.Filters.BuiltIn;
using KiriScope.Plugins.Abstractions.Filters;

namespace KiriScope.Core.Tests;

public sealed class CxContentFilterTests
{
    [Theory]
    [InlineData(CxRandomFamily.Standard)]
    [InlineData(CxRandomFamily.Nana)]
    public async Task TransformAsync_IsSymmetricAcrossLogicalBufferBoundaries(CxRandomFamily randomFamily)
    {
        var configuration = CreateConfiguration(randomFamily);
        var encryptor = new CxContentFilter(configuration);
        var plaintext = Enumerable.Range(0, 131_075).Select(static value => (byte)(value * 31)).ToArray();
        var encrypted = plaintext.ToArray();
        var context = new ContentFilterContext("image/title.png", 0x72E81C43U, 0, 0);

        await encryptor.TransformAsync(context, encrypted);
        Assert.NotEqual(plaintext, encrypted);

        var decryptor = new CxContentFilter(configuration);
        var offset = 0;
        var segmentIndex = 0;
        while (offset < encrypted.Length)
        {
            var length = Math.Min(7_919, encrypted.Length - offset);
            await decryptor.TransformAsync(
                new ContentFilterContext("image/title.png", 0x72E81C43U, segmentIndex++, offset),
                encrypted.AsMemory(offset, length));
            offset += length;
        }

        Assert.Equal(plaintext, encrypted);
    }

    [Fact]
    public async Task TransformAsync_RequiresTheEntryAdler32()
    {
        var filter = new CxContentFilter(CreateConfiguration(CxRandomFamily.Standard));

        var exception = await Assert.ThrowsAsync<ContentFilterException>(async () =>
            await filter.TransformAsync(new ContentFilterContext("image/title.png", null, 0, 0), new byte[16]));

        Assert.Equal("CX_ADLER32_REQUIRED", exception.Code);
    }

    [Theory]
    [InlineData(CxRandomFamily.Standard)]
    [InlineData(CxRandomFamily.Nana)]
    public async Task TransformAsync_ExecutesEveryProgramSeed(CxRandomFamily randomFamily)
    {
        var filter = new CxContentFilter(CreateConfiguration(randomFamily));
        for (uint seed = 0; seed < 0x80; seed++)
        {
            var data = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
            var original = data.ToArray();
            var context = new ContentFilterContext("seed-test.bin", seed, 0, 0);

            await filter.TransformAsync(context, data);
            await filter.TransformAsync(context, data);

            Assert.Equal(original, data);
        }
    }

    private static CxSchemeConfiguration CreateConfiguration(CxRandomFamily randomFamily) =>
        new(
            0,
            17,
            [0, 1, 2],
            [0, 1, 2, 3, 4, 5],
            [0, 1, 2, 3, 4, 5, 6, 7],
            Enumerable.Range(0, 0x400).Select(static index => unchecked((uint)(index * 2_654_435_761U))).ToArray(),
            randomFamily,
            randomFamily == CxRandomFamily.Nana ? 0x4F1BBCDCU : null);
}
