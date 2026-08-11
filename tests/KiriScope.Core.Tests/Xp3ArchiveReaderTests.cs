using System.IO.Compression;
using System.Text;
using KiriScope.Core.Evidence;
using KiriScope.Xp3;

namespace KiriScope.Core.Tests;

public sealed class Xp3ArchiveReaderTests
{
    [Fact]
    public async Task ReadIndexAsync_AcceptsAnInfoNameWithTrailingUtf16Nul()
    {
        await using var archive = Xp3TestArchive.CreateWithTrailingNameNul();

        var result = await Xp3ArchiveReader.ReadIndexAsync(archive);

        Assert.Equal(EvidenceStage.IndexParsed, result.Stage);
        Assert.Equal("keycheck.tjs", Assert.Single(result.Entries).Name);
    }

    [Fact]
    public async Task ReadIndexAsync_ReadsAStandardFileEntry()
    {
        await using var archive = Xp3TestArchive.Create(compressIndex: false);

        var result = await Xp3ArchiveReader.ReadIndexAsync(archive);

        Assert.Equal(EvidenceStage.IndexParsed, result.Stage);
        Assert.False(result.IsIndexCompressed);
        var entry = Assert.Single(result.Entries);
        Assert.Equal("image/title.png", entry.Name);
        Assert.False(entry.IsMarkedEncrypted);
        Assert.Equal(3, entry.UnpackedSize);
        Assert.Equal(0x12345678U, entry.Adler32);
        var segment = Assert.Single(entry.Segments);
        Assert.False(segment.IsCompressed);
        Assert.Equal(19, segment.Offset);
        Assert.Equal(3, segment.PackedSize);
    }

    [Fact]
    public async Task ReadIndexAsync_ReadsACompressedIndex()
    {
        await using var archive = Xp3TestArchive.Create(compressIndex: true);

        var result = await Xp3ArchiveReader.ReadIndexAsync(archive);

        Assert.Equal(EvidenceStage.IndexParsed, result.Stage);
        Assert.True(result.IsIndexCompressed);
        Assert.Equal("image/title.png", Assert.Single(result.Entries).Name);
    }

    [Fact]
    public async Task ReadIndexAsync_RecoversCabbageNameListEntries()
    {
        await using var archive = Xp3TestArchive.CreateWithCabbageNameList();

        var result = await Xp3ArchiveReader.ReadIndexAsync(archive);

        Assert.Equal(EvidenceStage.IndexParsed, result.Stage);
        Assert.Equal("scenario/opening.ks", Assert.Single(result.Entries).Name);
        Assert.Equal("scenario/opening.ks", result.NameMappings[0xA1B2C3D4]);
    }

    [Fact]
    public async Task ReadIndexAsync_RejectsAnExcessiveDeclaredIndexSize()
    {
        await using var archive = Xp3TestArchive.CreateWithDeclaredIndexSize(Xp3ReadOptions.DefaultMaximumIndexSize + 1);

        var result = await Xp3ArchiveReader.ReadIndexAsync(archive);

        Assert.Equal(EvidenceStage.ContainerIdentified, result.Stage);
        Assert.Empty(result.Entries);
        Assert.Contains(result.Diagnostics, static item => item.Code == "XP3_INDEX_SIZE_INVALID");
    }

    private static class Xp3TestArchive
    {
        public static MemoryStream Create(bool compressIndex)
        {
            var index = BuildIndex();
            byte[] storedIndex;
            if (compressIndex)
            {
                using var compressedBuffer = new MemoryStream();
                using (var zlib = new ZLibStream(compressedBuffer, CompressionLevel.SmallestSize, leaveOpen: true))
                {
                    zlib.Write(index);
                }

                storedIndex = compressedBuffer.ToArray();
            }
            else
            {
                storedIndex = index;
            }

            var archive = new MemoryStream();
            archive.Write(Xp3Signature.Bytes);
            using var writer = new BinaryWriter(archive, Encoding.UTF8, leaveOpen: true);
            writer.Write(19L + 3L);
            writer.Write(new byte[] { 0xCA, 0xFE, 0x01 });
            writer.Write((byte)(compressIndex ? 1 : 0));
            writer.Write((long)storedIndex.Length);
            if (compressIndex)
            {
                writer.Write((long)index.Length);
            }

            writer.Write(storedIndex);
            archive.Position = 0;
            return archive;
        }

        public static MemoryStream CreateWithDeclaredIndexSize(long declaredSize)
        {
            var archive = new MemoryStream();
            archive.Write(Xp3Signature.Bytes);
            using var writer = new BinaryWriter(archive, Encoding.UTF8, leaveOpen: true);
            writer.Write(19L);
            writer.Write((byte)0);
            writer.Write(declaredSize);
            archive.Position = 0;
            return archive;
        }

        public static MemoryStream CreateWithTrailingNameNul()
        {
            var index = BuildIndex(trailingNameNul: true, name: "keycheck.tjs");
            var archive = new MemoryStream();
            archive.Write(Xp3Signature.Bytes);
            using var writer = new BinaryWriter(archive, Encoding.UTF8, leaveOpen: true);
            writer.Write(22L);
            writer.Write(new byte[] { 0xCA, 0xFE, 0x01 });
            writer.Write((byte)0);
            writer.Write((long)index.Length);
            writer.Write(index);
            archive.Position = 0;
            return archive;
        }

        public static MemoryStream CreateWithCabbageNameList()
        {
            const uint hash = 0xA1B2C3D4;
            const string opaqueName = "0123456789abcdef0123456789abcdef";
            const string originalName = "scenario/opening.ks";
            var nameList = BuildNameList(hash, originalName);
            var packedNameList = Compress(nameList);
            var index = BuildIndexWithNameList(opaqueName, hash, 19L + 9L, nameList.Length, packedNameList.Length);
            var nameListOffset = 19L + 9L + index.Length;
            index = BuildIndexWithNameList(opaqueName, hash, nameListOffset, nameList.Length, packedNameList.Length);

            var archive = new MemoryStream();
            archive.Write(Xp3Signature.Bytes);
            using var writer = new BinaryWriter(archive, Encoding.UTF8, leaveOpen: true);
            writer.Write(19L);
            writer.Write((byte)0);
            writer.Write((long)index.Length);
            writer.Write(index);
            writer.Write(packedNameList);
            archive.Position = 0;
            return archive;
        }

        private static byte[] BuildIndex(bool trailingNameNul = false, string name = "image/title.png")
        {
            using var info = new MemoryStream();
            using (var writer = new BinaryWriter(info, Encoding.Unicode, leaveOpen: true))
            {
                writer.Write(0U);
                writer.Write(3L);
                writer.Write(3L);
                writer.Write((ushort)name.Length);
                writer.Write(name.ToCharArray());
                if (trailingNameNul)
                {
                    writer.Write('\0');
                }
            }

            using var segments = new MemoryStream();
            using (var writer = new BinaryWriter(segments, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(0);
                writer.Write(19L);
                writer.Write(3L);
                writer.Write(3L);
            }

            using var file = new MemoryStream();
            using (var writer = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true))
            {
                WriteChunk(writer, 0x6F666E69, info.ToArray());
                WriteChunk(writer, 0x6D676573, segments.ToArray());
                WriteChunk(writer, 0x726C6461, BitConverter.GetBytes(0x12345678U));
            }

            using var index = new MemoryStream();
            using (var writer = new BinaryWriter(index, Encoding.UTF8, leaveOpen: true))
            {
                WriteChunk(writer, 0x656C6946, file.ToArray());
            }

            return index.ToArray();
        }

        private static byte[] BuildIndexWithNameList(string name, uint hash, long nameListOffset, int nameListLength, int packedNameListLength)
        {
            using var info = new MemoryStream();
            using (var writer = new BinaryWriter(info, Encoding.Unicode, leaveOpen: true))
            {
                writer.Write(1U);
                writer.Write(3L);
                writer.Write(3L);
                writer.Write((ushort)name.Length);
                writer.Write(name.ToCharArray());
            }

            using var segments = new MemoryStream();
            using (var writer = new BinaryWriter(segments, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(0);
                writer.Write(19L);
                writer.Write(3L);
                writer.Write(3L);
            }

            using var file = new MemoryStream();
            using (var writer = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true))
            {
                WriteChunk(writer, 0x6F666E69, info.ToArray());
                WriteChunk(writer, 0x6D676573, segments.ToArray());
                WriteChunk(writer, 0x726C6461, BitConverter.GetBytes(hash));
            }

            using var reference = new MemoryStream();
            using (var writer = new BinaryWriter(reference, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(nameListOffset);
                writer.Write((uint)nameListLength);
                writer.Write((uint)packedNameListLength);
            }

            using var index = new MemoryStream();
            using (var writer = new BinaryWriter(index, Encoding.UTF8, leaveOpen: true))
            {
                WriteChunk(writer, 0x656C6946, file.ToArray());
                WriteChunk(writer, 0x3A676263, reference.ToArray()); // cbg:
            }

            return index.ToArray();
        }

        private static byte[] BuildNameList(uint hash, string name)
        {
            using var names = new MemoryStream();
            using var writer = new BinaryWriter(names, Encoding.Unicode, leaveOpen: true);
            writer.Write(0x6D616E65U);
            writer.Write((long)(sizeof(uint) + sizeof(short) + (name.Length * sizeof(char))));
            writer.Write(hash);
            writer.Write((short)name.Length);
            writer.Write(name.ToCharArray());
            return names.ToArray();
        }

        private static byte[] Compress(byte[] source)
        {
            using var output = new MemoryStream();
            using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                zlib.Write(source);
            }

            return output.ToArray();
        }

        private static void WriteChunk(BinaryWriter writer, uint tag, byte[] data)
        {
            writer.Write(tag);
            writer.Write((long)data.Length);
            writer.Write(data);
        }
    }
}
