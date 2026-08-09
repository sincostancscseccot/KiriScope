using KiriScope.Core.Evidence;
using KiriScope.Xp3;

namespace KiriScope.Core.Tests;

public sealed class Xp3ArchivePackerTests
{
    [Fact]
    public async Task PackDirectoryAsync_WritesANewStandardArchiveThatRoundTripsThroughTheReaderAndExtractor()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "KiriScope.Tests", Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(temporaryRoot, "staging");
        var archivePath = Path.Combine(temporaryRoot, "repacked.xp3");
        var extracted = Path.Combine(temporaryRoot, "extracted");
        Directory.CreateDirectory(Path.Combine(staging, "image"));
        await File.WriteAllBytesAsync(Path.Combine(staging, "image", "title.bin"), [0xCA, 0xFE, 0x01]);
        await File.WriteAllTextAsync(Path.Combine(staging, "script.tjs"), "return;\n");
        try
        {
            var result = await Xp3ArchivePacker.PackDirectoryAsync(staging, archivePath);

            Assert.Equal(Path.GetFullPath(archivePath), result.OutputPath);
            Assert.Equal(2, result.Entries.Count);
            Assert.Equal(64, result.ArchiveSha256.Length);
            Assert.Equal([0xCA, 0xFE, 0x01], await File.ReadAllBytesAsync(Path.Combine(staging, "image", "title.bin")));

            await using var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var index = await Xp3ArchiveReader.ReadIndexAsync(input);
            Assert.Equal(EvidenceStage.IndexParsed, index.Stage);
            Assert.Equal(["image/title.bin", "script.tjs"], index.Entries.Select(static entry => entry.Name).Order(StringComparer.Ordinal).ToArray());
            Assert.All(index.Entries, static entry =>
            {
                Assert.False(entry.IsMarkedEncrypted);
                var segment = Assert.Single(entry.Segments);
                Assert.False(segment.IsCompressed);
                Assert.Equal(entry.UnpackedSize, segment.UnpackedSize);
            });

            var extraction = await Xp3EntryExtractor.ExtractAllAsync(archivePath, extracted);
            Assert.Equal(2, extraction.ExtractedEntryCount);
            Assert.Equal([0xCA, 0xFE, 0x01], await File.ReadAllBytesAsync(Path.Combine(extracted, "image", "title.bin")));
            Assert.Equal("return;\n", await File.ReadAllTextAsync(Path.Combine(extracted, "script.tjs")));

            await Assert.ThrowsAsync<IOException>(async () => await Xp3ArchivePacker.PackDirectoryAsync(staging, archivePath));
            await Assert.ThrowsAsync<ArgumentException>(async () => await Xp3ArchivePacker.PackDirectoryAsync(staging, Path.Combine(staging, "forbidden.xp3")));
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PackDirectoryAsync_RejectsFilesThatExceedTheConfiguredLimitWithoutCreatingOutput()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "KiriScope.Tests", Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(temporaryRoot, "staging");
        var archivePath = Path.Combine(temporaryRoot, "repacked.xp3");
        Directory.CreateDirectory(staging);
        await File.WriteAllBytesAsync(Path.Combine(staging, "large.bin"), new byte[5]);
        try
        {
            await Assert.ThrowsAsync<IOException>(async () => await Xp3ArchivePacker.PackDirectoryAsync(
                staging,
                archivePath,
                new Xp3ArchivePackOptions { MaximumFileBytes = 4, MaximumTotalBytes = 4 }));

            Assert.False(File.Exists(archivePath));
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }
}
