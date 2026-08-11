using System.IO.Compression;
using System.Text;
using KiriScope.Filters.BuiltIn;
using KiriScope.Xp3;

namespace KiriScope.Core.Tests;

public sealed class Xp3EntryExtractorTests
{
    [Fact]
    public async Task ExtractAsync_ConcatenatesMultipleUncompressedSegments()
    {
        await using var archive = new MemoryStream(Encoding.ASCII.GetBytes("abcde"));
        await using var output = new MemoryStream();
        var entry = new Xp3Entry(
            "scenario/startup.tjs",
            false,
            5,
            5,
            null,
            [
                new Xp3Segment(false, 0, 2, 2),
                new Xp3Segment(false, 2, 3, 3),
            ]);

        var result = await Xp3EntryExtractor.ExtractAsync(archive, entry, output);

        Assert.True(result.Succeeded);
        Assert.Equal(5, result.BytesWritten);
        Assert.Equal("abcde", Encoding.ASCII.GetString(output.ToArray()));
    }

    [Fact]
    public async Task ExtractAsync_DecompressesACompressedSegment()
    {
        var original = Encoding.UTF8.GetBytes("compressed index and resource data");
        var compressed = Compress(original);
        await using var archive = new MemoryStream(compressed);
        await using var output = new MemoryStream();
        var entry = new Xp3Entry(
            "image/title.png",
            false,
            original.Length,
            compressed.Length,
            null,
            [new Xp3Segment(true, 0, original.Length, compressed.Length)]);

        var result = await Xp3EntryExtractor.ExtractAsync(archive, entry, output);

        Assert.True(result.Succeeded);
        Assert.Equal(original, output.ToArray());
    }

    [Fact]
    public async Task ExtractAsync_SkipsAnEncryptedEntry()
    {
        await using var archive = new MemoryStream([0x01, 0x02, 0x03]);
        await using var output = new MemoryStream();
        var entry = new Xp3Entry(
            "image/title.png",
            true,
            3,
            3,
            null,
            [new Xp3Segment(false, 0, 3, 3)]);

        var result = await Xp3EntryExtractor.ExtractAsync(archive, entry, output);

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.BytesWritten);
        Assert.Empty(output.ToArray());
        Assert.Equal("XP3_CONTENT_FILTER_REQUIRED", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task ExtractAsync_AllowsMarkedEntryWithoutFilterOnlyWhenExplicitlyEnabled()
    {
        await using var archive = new MemoryStream("abc"u8.ToArray());
        await using var output = new MemoryStream();
        var entry = new Xp3Entry(
            "image/title.png",
            true,
            3,
            3,
            0x024D0127U,
            [new Xp3Segment(false, 0, 3, 3)]);

        var result = await Xp3EntryExtractor.ExtractAsync(
            archive,
            entry,
            output,
            new Xp3EntryExtractionOptions { AllowUnfilteredMarkedEntries = true });

        Assert.True(result.Succeeded);
        Assert.Equal("abc", Encoding.ASCII.GetString(output.ToArray()));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("Adler-32", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAsync_RejectsUnfilteredMarkedEntryWhenAdler32DoesNotMatch()
    {
        await using var archive = new MemoryStream("abc"u8.ToArray());
        await using var output = new MemoryStream();
        var entry = new Xp3Entry(
            "image/title.png",
            true,
            3,
            3,
            0U,
            [new Xp3Segment(false, 0, 3, 3)]);

        var result = await Xp3EntryExtractor.ExtractAsync(
            archive,
            entry,
            output,
            new Xp3EntryExtractionOptions { AllowUnfilteredMarkedEntries = true });

        Assert.False(result.Succeeded);
        Assert.Equal("XP3_ADLER32_MISMATCH", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task ExtractAsync_RejectsMismatchedInfoAndSegmentSizes()
    {
        await using var archive = new MemoryStream(Encoding.ASCII.GetBytes("abc"));
        await using var output = new MemoryStream();
        var entry = new Xp3Entry(
            "image/title.png",
            false,
            4,
            3,
            null,
            [new Xp3Segment(false, 0, 3, 3)]);

        var result = await Xp3EntryExtractor.ExtractAsync(archive, entry, output);

        Assert.False(result.Succeeded);
        Assert.Equal("XP3_ENTRY_SIZE_MISMATCH", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task ExtractAsync_AppliesAContentFilterToAnEncryptedEntry()
    {
        var plainText = Encoding.ASCII.GetBytes("filtered content");
        var encrypted = Xor(plainText, [0xAA, 0x55]);
        await using var archive = new MemoryStream(encrypted);
        await using var output = new MemoryStream();
        var entry = new Xp3Entry(
            "image/title.png",
            true,
            plainText.Length,
            encrypted.Length,
            null,
            [new Xp3Segment(false, 0, encrypted.Length, encrypted.Length)]);

        var result = await Xp3EntryExtractor.ExtractAsync(
            archive,
            entry,
            output,
            new Xp3EntryExtractionOptions
            {
                ContentFilter = new RepeatingXorContentFilter([0xAA, 0x55]),
            });

        Assert.True(result.Succeeded);
        Assert.Equal(KiriScope.Core.Evidence.EvidenceStage.ContentFilterApplied, result.Stage);
        Assert.Equal("builtin.repeating-xor", result.ContentFilterId);
        Assert.Equal(plainText, output.ToArray());
    }

    [Fact]
    public async Task ExtractAsync_RejectsAnAdler32MismatchForPlainContent()
    {
        await using var archive = new MemoryStream(Encoding.ASCII.GetBytes("abc"));
        await using var output = new MemoryStream();
        var entry = new Xp3Entry(
            "image/title.png",
            false,
            3,
            3,
            0U,
            [new Xp3Segment(false, 0, 3, 3)]);

        var result = await Xp3EntryExtractor.ExtractAsync(archive, entry, output);

        Assert.False(result.Succeeded);
        Assert.Equal("XP3_ADLER32_MISMATCH", Assert.Single(result.Diagnostics).Code);
        Assert.NotEqual(0U, result.ActualAdler32);
    }

    [Fact]
    public async Task ExtractAsync_ReportsAContentFilterFailureSeparatelyFromDecompression()
    {
        await using var archive = new MemoryStream([0x01, 0x02, 0x03]);
        await using var output = new MemoryStream();
        var entry = new Xp3Entry(
            "image/title.png",
            true,
            3,
            3,
            null,
            [new Xp3Segment(false, 0, 3, 3)]);

        var result = await Xp3EntryExtractor.ExtractAsync(
            archive,
            entry,
            output,
            new Xp3EntryExtractionOptions
            {
                ContentFilter = new CxContentFilter(new CxSchemeConfiguration(
                    0,
                    0,
                    [0, 1, 2],
                    [0, 1, 2, 3, 4, 5],
                    [0, 1, 2, 3, 4, 5, 6, 7],
                    new uint[0x400])),
            });

        Assert.False(result.Succeeded);
        Assert.Equal("CX_ADLER32_REQUIRED", Assert.Single(result.Diagnostics).Code);
    }

    private static byte[] Compress(byte[] input)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(input);
        }

        return output.ToArray();
    }

    private static byte[] Xor(byte[] input, byte[] key)
    {
        var output = new byte[input.Length];
        for (var index = 0; index < output.Length; index++)
        {
            output[index] = (byte)(input[index] ^ key[index % key.Length]);
        }

        return output;
    }
}
