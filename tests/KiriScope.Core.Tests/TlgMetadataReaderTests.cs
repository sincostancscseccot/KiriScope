using System.Buffers.Binary;
using KiriScope.Core.Evidence;
using KiriScope.Resources;

namespace KiriScope.Core.Tests;

public sealed class TlgMetadataReaderTests
{
    [Fact]
    public async Task ReadAsync_IdentifiesPlainTlg5Metadata()
    {
        await using var input = new MemoryStream(CreateTlg(version: 5, width: 640, height: 480, colors: 4));

        var result = await TlgMetadataReader.ReadAsync(input);

        Assert.True(result.IsRecognized);
        Assert.Equal(EvidenceStage.ContainerIdentified, result.Stage);
        Assert.Equal(5, result.Version);
        Assert.Equal(640, result.Width);
        Assert.Equal(480, result.Height);
        Assert.Equal((byte)4, result.ColorChannels);
        Assert.Equal(20, result.DataOffset);
        Assert.Equal("TLG_METADATA_IDENTIFIED", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task ReadAsync_IdentifiesSdsWrappedTlg6Metadata()
    {
        await using var input = new MemoryStream(CreateTlg(version: 6, width: 1, height: 2, colors: 3, sdsWrapper: true));

        var result = await TlgMetadataReader.ReadAsync(input);

        Assert.True(result.IsRecognized);
        Assert.True(result.HasSdsWrapper);
        Assert.Equal(6, result.Version);
        Assert.Equal(38, result.DataOffset);
    }

    [Fact]
    public async Task ReadAsync_RejectsTlg6WithNonZeroReservedBytes()
    {
        var data = CreateTlg(version: 6, width: 1, height: 2, colors: 3);
        data[12] = 1;
        await using var input = new MemoryStream(data);

        var result = await TlgMetadataReader.ReadAsync(input);

        Assert.False(result.IsRecognized);
        Assert.Equal("TLG6_RESERVED_BYTES_INVALID", Assert.Single(result.Diagnostics).Code);
    }

    private static byte[] CreateTlg(int version, uint width, uint height, byte colors, bool sdsWrapper = false)
    {
        var offset = sdsWrapper ? 15 : 0;
        var dimensionsOffset = offset + (version == 6 ? 15 : 12);
        var data = new byte[dimensionsOffset + 8];
        if (sdsWrapper)
        {
            "TLG0.0\0sds\x1a"u8.CopyTo(data);
        }

        (version == 5 ? "TLG5.0"u8 : "TLG6.0"u8).CopyTo(data.AsSpan(offset));
        "\0raw\x1a"u8.CopyTo(data.AsSpan(offset + 6));
        data[offset + 11] = colors;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(dimensionsOffset), width);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(dimensionsOffset + sizeof(uint)), height);
        return data;
    }
}
