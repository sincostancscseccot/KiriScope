using System.Buffers.Binary;
using KiriScope.Core.Evidence;
using KiriScope.Resources;

namespace KiriScope.Core.Tests;

public sealed class Tlg5DecoderTests
{
    [Fact]
    public async Task DecodeAsync_DecodesRawPlanesAndVerticalPrediction()
    {
        await using var input = new MemoryStream(CreateTlg5(new byte[] { 10, 1 }, new byte[] { 20, 2 }, new byte[] { 30, 3 }));

        var result = await Tlg5Decoder.DecodeAsync(input);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Equal(EvidenceStage.ContentUsable, result.Stage);
        Assert.Equal(1, result.Image!.Width);
        Assert.Equal(2, result.Image.Height);
        Assert.Equal([50, 20, 30, 255, 55, 22, 33, 255], result.Image.Pixels);
        Assert.Equal("TLG5_PIXELS_DECODED", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task DecodeAsync_DecodesCompressedLiteralPlanes()
    {
        await using var input = new MemoryStream(CreateTlg5(new byte[] { 10 }, new byte[] { 20 }, new byte[] { 30 }, compressed: true));

        var result = await Tlg5Decoder.DecodeAsync(input);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Equal([50, 20, 30, 255], result.Image!.Pixels);
    }

    [Fact]
    public async Task DecodeAsync_RejectsTlg6WithoutClaimingUsablePixels()
    {
        await using var input = new MemoryStream(CreateTlg6Header());

        var result = await Tlg5Decoder.DecodeAsync(input);

        Assert.False(result.Succeeded);
        Assert.Equal(EvidenceStage.ContainerIdentified, result.Stage);
        Assert.Equal("TLG_DECODE_VARIANT_UNSUPPORTED", Assert.Single(result.Diagnostics).Code);
    }

    private static byte[] CreateTlg5(byte[] blue, byte[] green, byte[] red, bool compressed = false)
    {
        if (blue.Length != green.Length || blue.Length != red.Length) throw new ArgumentException("TLG5 planes must have matching lengths.");
        using var output = new MemoryStream();
        output.Write("TLG5.0"u8);
        output.Write("\0raw\x1a"u8);
        output.WriteByte(3);
        WriteUInt32(output, 1);
        WriteUInt32(output, (uint)blue.Length);
        WriteUInt32(output, (uint)blue.Length);
        WriteUInt32(output, 0);
        WritePlane(output, blue, compressed);
        WritePlane(output, green, compressed);
        WritePlane(output, red, compressed);
        return output.ToArray();
    }

    private static byte[] CreateTlg6Header()
    {
        var data = new byte[23];
        "TLG6.0"u8.CopyTo(data);
        "\0raw\x1a"u8.CopyTo(data.AsSpan(6));
        data[11] = 3;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(15), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(19), 1);
        return data;
    }

    private static void WritePlane(Stream output, byte[] values, bool compressed)
    {
        output.WriteByte(compressed ? (byte)0 : (byte)1);
        var data = compressed ? new byte[] { 0, values[0] } : values;
        WriteUInt32(output, (uint)data.Length);
        output.Write(data);
    }

    private static void WriteUInt32(Stream output, uint value)
    {
        Span<byte> data = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(data, value);
        output.Write(data);
    }
}
