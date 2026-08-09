using System.Text;
using KiriScope.Xp3;

namespace KiriScope.Core.Tests;

public sealed class Xp3ArchiveExtractionTests
{
    [Fact]
    public async Task ExtractAllAsync_ExtractsAParsedEntryToTheRequestedRoot()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "KiriScopeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        var archivePath = Path.Combine(temporaryRoot, "sample.xp3");
        var outputPath = Path.Combine(temporaryRoot, "output");

        try
        {
            await File.WriteAllBytesAsync(archivePath, CreateArchive("image/title.bin", "art"));

            var result = await Xp3EntryExtractor.ExtractAllAsync(archivePath, outputPath);

            Assert.True(result.IndexWasParsed);
            Assert.Equal(1, result.ExtractedEntryCount);
            Assert.Equal(0, result.SkippedEntryCount);
            Assert.Equal("art", await File.ReadAllTextAsync(Path.Combine(outputPath, "image", "title.bin")));
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static byte[] CreateArchive(string entryName, string content)
    {
        var contentBytes = Encoding.UTF8.GetBytes(content);
        const int archiveHeaderLength = 19;
        var indexOffset = archiveHeaderLength + contentBytes.Length;

        using var info = new MemoryStream();
        using (var writer = new BinaryWriter(info, Encoding.Unicode, leaveOpen: true))
        {
            writer.Write(0U);
            writer.Write((long)contentBytes.Length);
            writer.Write((long)contentBytes.Length);
            writer.Write((ushort)entryName.Length);
            writer.Write(entryName.ToCharArray());
        }

        using var segments = new MemoryStream();
        using (var writer = new BinaryWriter(segments, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0);
            writer.Write((long)archiveHeaderLength);
            writer.Write((long)contentBytes.Length);
            writer.Write((long)contentBytes.Length);
        }

        using var file = new MemoryStream();
        using (var writer = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true))
        {
            WriteChunk(writer, 0x6F666E69, info.ToArray());
            WriteChunk(writer, 0x6D676573, segments.ToArray());
        }

        using var index = new MemoryStream();
        using (var writer = new BinaryWriter(index, Encoding.UTF8, leaveOpen: true))
        {
            WriteChunk(writer, 0x656C6946, file.ToArray());
        }

        using var archive = new MemoryStream();
        archive.Write(Xp3Signature.Bytes);
        using (var writer = new BinaryWriter(archive, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((long)indexOffset);
            writer.Write(contentBytes);
            writer.Write((byte)0);
            writer.Write((long)index.Length);
            writer.Write(index.ToArray());
        }

        return archive.ToArray();
    }

    private static void WriteChunk(BinaryWriter writer, uint tag, byte[] data)
    {
        writer.Write(tag);
        writer.Write((long)data.Length);
        writer.Write(data);
    }
}
