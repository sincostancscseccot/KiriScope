using System.Buffers.Binary;
using System.Text;
using KiriScope.Core.Evidence;
using KiriScope.Resources;

namespace KiriScope.Core.Tests;

public sealed class PsbStructureProbeTests
{
    [Fact]
    public async Task ProbeAsync_IdentifiesPimgKeysAndMapsANamedResource()
    {
        var (psb, resourceData) = CreatePsb();
        await using var input = new MemoryStream(psb);

        var result = await PsbStructureProbe.ProbeAsync(input);

        Assert.Equal(EvidenceStage.ContainerIdentified, result.Stage);
        Assert.True(result.IsPimgCandidate);
        Assert.Equal(["layers", "width", "height", "asset"], result.RootKeys);
        var resource = Assert.Single(result.RootResources);
        Assert.Equal(3, resource.RootKeyIndex);
        Assert.Equal((uint)0, resource.ResourceIndex);
        Assert.NotNull(resource.Offset);
        Assert.Equal(resourceData.Length, resource.Length);
        Assert.Equal("PSB_PIMG_SIGNATURE_IDENTIFIED", Assert.Single(result.Diagnostics).Code);
        Assert.Collection(
            result.RootUnsignedIntegers.OrderBy(value => value.RootKeyIndex),
            value => { Assert.Equal(1, value.RootKeyIndex); Assert.Equal((uint)80, value.Value); },
            value => { Assert.Equal(2, value.RootKeyIndex); Assert.Equal((uint)40, value.Value); });
    }

    [Fact]
    public async Task ExtractAsync_UsesRootKeyPositionRatherThanNameTableIndex()
    {
        var (psb, expectedData) = CreatePsb();
        var root = Path.Combine(Path.GetTempPath(), "KiriScope.Tests", Guid.NewGuid().ToString("N"));
        var inputDirectory = Path.Combine(root, "input");
        var psbPath = Path.Combine(inputDirectory, "sample.psb");
        var outputPath = Path.Combine(root, "asset.tlg");
        Directory.CreateDirectory(inputDirectory);
        try
        {
            await File.WriteAllBytesAsync(psbPath, psb);

            var result = await PsbResourceExtractor.ExtractAsync(psbPath, "asset", outputPath);

            Assert.True(result.Succeeded);
            Assert.Equal(EvidenceStage.RawDataExtracted, result.Stage);
            Assert.Equal(expectedData.Length, result.BytesWritten);
            Assert.Equal(expectedData, await File.ReadAllBytesAsync(outputPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProfileAsync_ReportsOnlyMappedRootResourcesWithoutCopyingTheirData()
    {
        var (psb, _) = CreatePsb();
        await using var input = new MemoryStream(psb);

        var result = await PsbResourceProfiler.ProfileAsync(input);

        Assert.Equal(EvidenceStage.ContainerIdentified, result.Stage);
        Assert.True(result.IsPimgCandidate);
        var resource = Assert.Single(result.Resources);
        Assert.Equal("asset", resource.ResourceName);
        Assert.Equal(ResourceFormat.Unknown, resource.DetectedFormat);
        Assert.False(resource.TlgMetadataRecognized);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PSB_DIRECT_RESOURCES_PROFILED");
    }

    [Fact]
    public async Task ExportAllAsync_CopiesMappedRootResourcesToANewDirectoryOutsideTheInputTree()
    {
        var (psb, expectedData) = CreatePsb();
        var root = Path.Combine(Path.GetTempPath(), "KiriScope.Tests", Guid.NewGuid().ToString("N"));
        var inputDirectory = Path.Combine(root, "input");
        var psbPath = Path.Combine(inputDirectory, "sample.psb");
        var outputDirectory = Path.Combine(root, "exported");
        Directory.CreateDirectory(inputDirectory);
        try
        {
            await File.WriteAllBytesAsync(psbPath, psb);

            var result = await PsbResourceExtractor.ExportAllAsync(psbPath, outputDirectory);

            Assert.True(result.Succeeded);
            Assert.Equal(EvidenceStage.RawDataExtracted, result.Stage);
            var resource = Assert.Single(result.Resources);
            Assert.Equal("asset", resource.ResourceName);
            Assert.Equal((uint)0, resource.ResourceIndex);
            Assert.Equal(expectedData.Length, resource.BytesWritten);
            Assert.Equal(expectedData, await File.ReadAllBytesAsync(resource.OutputFile));
            Assert.Equal(psb, await File.ReadAllBytesAsync(psbPath));
            Assert.Equal("PSB_ROOT_RESOURCES_EXPORTED", Assert.Single(result.Diagnostics).Code);

            await Assert.ThrowsAsync<IOException>(() => PsbResourceExtractor.ExportAllAsync(psbPath, outputDirectory));
            await Assert.ThrowsAsync<ArgumentException>(() => PsbResourceExtractor.ExportAllAsync(psbPath, Path.Combine(inputDirectory, "not-allowed")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    internal static (byte[] Psb, byte[] ResourceData) CreatePsb()
    {
        var names = CreateNames(["unused", "layers", "width", "height", "asset"]);
        var resourceData = "resource-data"u8.ToArray();
        using var output = new MemoryStream();
        output.Write(new byte[44]);
        var namesOffset = checked((uint)output.Position);
        output.Write(names);
        var entriesOffset = checked((uint)output.Position);
        WriteRootDictionary(output, [1, 2, 3, 4]);
        var chunkOffsetsTableOffset = checked((uint)output.Position);
        WriteArray(output, [0]);
        var chunkLengthsTableOffset = checked((uint)output.Position);
        WriteArray(output, [(uint)resourceData.Length]);
        var chunkDataOffset = checked((uint)output.Position);
        output.Write(resourceData);

        var psb = output.ToArray();
        "PSB\0"u8.CopyTo(psb);
        BinaryPrimitives.WriteUInt16LittleEndian(psb.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(psb.AsSpan(8), namesOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(psb.AsSpan(12), namesOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(psb.AsSpan(16), namesOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(psb.AsSpan(24), chunkOffsetsTableOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(psb.AsSpan(28), chunkLengthsTableOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(psb.AsSpan(32), chunkDataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(psb.AsSpan(36), entriesOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(psb.AsSpan(40), Adler32(psb.AsSpan(8, 32)));
        return (psb, resourceData);
    }

    private static byte[] CreateNames(IReadOnlyList<string> names)
    {
        var charset = new List<uint> { 0 };
        var data = new List<uint> { 0 };
        var indexes = new List<uint>(names.Count);
        foreach (var name in names)
        {
            var parent = 0U;
            foreach (var character in Encoding.UTF8.GetBytes(name))
            {
                var delta = parent == 0 ? 1U : checked((uint)data.Count + 1);
                var child = checked(delta + character);
                EnsureLength(charset, child + 1);
                EnsureLength(data, child + 1);
                charset[checked((int)parent)] = delta;
                data[checked((int)child)] = parent;
                parent = child;
            }

            indexes.Add(parent);
        }

        using var output = new MemoryStream();
        WriteArray(output, charset);
        WriteArray(output, data);
        WriteArray(output, indexes);
        return output.ToArray();
    }

    private static void WriteRootDictionary(Stream output, IReadOnlyList<uint> nameIndexes)
    {
        output.WriteByte(0x21);
        WriteArray(output, nameIndexes);
        WriteArray(output, [0U, 2, 4, 6]);
        output.Write([0x0D, 0, 0x05, 80, 0x05, 40, 0x19, 0]);
    }

    private static void WriteArray(Stream output, IReadOnlyList<uint> values)
    {
        var width = GetWidth(values.Append((uint)values.Count).Max());
        output.WriteByte((byte)(0x0C + width));
        WriteUnsigned(output, (uint)values.Count, width);
        output.WriteByte((byte)(0x0C + width));
        foreach (var value in values) WriteUnsigned(output, value, width);
    }

    private static void WriteUnsigned(Stream output, uint value, int width)
    {
        for (var index = 0; index < width; index++) output.WriteByte((byte)(value >> (index * 8)));
    }

    private static int GetWidth(uint value) => value switch { <= byte.MaxValue => 1, <= ushort.MaxValue => 2, <= 0x00FF_FFFF => 3, _ => 4 };

    private static void EnsureLength(List<uint> values, uint length)
    {
        while (values.Count < length) values.Add(0);
    }

    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        uint a = 1, b = 0;
        foreach (var value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }
}
