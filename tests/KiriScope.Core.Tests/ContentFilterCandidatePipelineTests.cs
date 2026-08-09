using KiriScope.Filters.BuiltIn;
using KiriScope.Plugins.Abstractions.Filters;
using KiriScope.Resources;

namespace KiriScope.Core.Tests;

public sealed class ContentFilterCandidatePipelineTests
{
    [Fact]
    public async Task EvaluateAsync_AcceptsOnlyTheCandidateThatPassesFullFormatValidation()
    {
        var plaintext = PngRgbaEncoder.Encode(new RgbaImage(1, 1, [12, 34, 56, 255]));
        var encrypted = plaintext.ToArray();
        var context = new ContentFilterContext("image/title.png", 0x34127856U, 0, 0);
        var encryptor = new RepeatingXorContentFilter([0xA5, 0x5A]);
        await encryptor.TransformAsync(context, encrypted);

        var report = await ContentFilterCandidatePipeline.EvaluateAsync(
            encrypted,
            context,
            [
                new ContentFilterCandidate(CreateScheme("right", "synthetic:known-plaintext"), new RepeatingXorContentFilter([0xA5, 0x5A])),
                new ContentFilterCandidate(CreateScheme("wrong", "synthetic:control"), new RepeatingXorContentFilter([0x11, 0x22])),
            ]);

        var accepted = Assert.Single(report.Candidates, static candidate => candidate.IsAccepted);
        Assert.Equal("right", accepted.Scheme.Id);
        Assert.Equal(ResourceFormat.Png, accepted.FormatScore.Format);
        Assert.Equal(100, accepted.FormatScore.Score);
        Assert.True(accepted.Difference.ChangedByteCount > 0);

        var rejected = Assert.Single(report.Candidates, static candidate => candidate.Scheme.Id == "wrong");
        Assert.False(rejected.IsAccepted);
        Assert.Contains(rejected.Diagnostics, static diagnostic => diagnostic.Code == "FILTER_CANDIDATE_REJECTED");
    }

    [Fact]
    public void Analyze_ReportsChangedRangesWithoutIncludingContent()
    {
        var difference = ContentByteDifference.Analyze([0, 1, 2, 3, 4], [0, 9, 8, 3, 7]);

        Assert.Equal(3, difference.ChangedByteCount);
        Assert.Equal(1, difference.FirstChangedOffset);
        Assert.Equal(4, difference.LastChangedOffset);
        Assert.Equal([new ContentByteDifferenceRange(1, 2), new ContentByteDifferenceRange(4, 1)], difference.ChangedRanges);
    }

    private static ContentFilterSchemeDescriptor CreateScheme(string id, string reference) =>
        new(
            id,
            id,
            "builtin.repeating-xor",
            "1.0",
            new ContentFilterParameterSource("test", reference));
}
