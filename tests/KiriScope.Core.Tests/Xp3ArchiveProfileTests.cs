using KiriScope.Core.Evidence;
using KiriScope.Xp3;

namespace KiriScope.Core.Tests;

public sealed class Xp3ArchiveProfileTests
{
    [Fact]
    public void FromIndex_AggregatesEncryptionSegmentsAdlerAndExtensionsWithoutReadingContent()
    {
        var index = new Xp3ArchiveIndex(
            EvidenceStage.IndexParsed,
            123,
            true,
            [
                new Xp3Entry("image/title.png", true, 10, 8, 1, [new Xp3Segment(true, 19, 10, 8)]),
                new Xp3Entry("script/main.tjs", false, 12, 12, null, [new Xp3Segment(false, 27, 4, 4), new Xp3Segment(false, 31, 8, 8)]),
                new Xp3Entry("license", true, 3, 3, 2, [new Xp3Segment(false, 39, 3, 3)]),
            ],
            Array.Empty<KiriScope.Core.Diagnostics.KiriScopeDiagnostic>());

        var profile = Xp3ArchiveProfile.FromIndex(index);

        Assert.Equal(EvidenceStage.IndexParsed, profile.Stage);
        Assert.True(profile.IsIndexCompressed);
        Assert.Equal(3, profile.EntryCount);
        Assert.Equal(2, profile.EncryptedEntryCount);
        Assert.Equal(1, profile.UnencryptedEntryCount);
        Assert.Equal(1, profile.MultiSegmentEntryCount);
        Assert.Equal(1, profile.CompressedSegmentCount);
        Assert.Equal(2, profile.EntriesWithAdler32Count);
        Assert.Equal(23, profile.PackedBytes);
        Assert.Equal(25, profile.UnpackedBytes);
        Assert.Equal([".tjs", ".png", "(none)"], profile.Extensions.Select(static item => item.Extension).ToArray());
    }
}
