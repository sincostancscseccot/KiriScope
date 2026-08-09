using System.Buffers.Binary;
using KiriScope.Core.Evidence;
using KiriScope.Resources;

namespace KiriScope.Core.Tests;

public sealed class BmpImageDecoderTests
{
    [Fact]
    public async Task DecodeAsync_ConvertsBottomUpBgrRowsToTopDownRgba()
    {
        await using var input = new MemoryStream(Create24BitBmp());

        var result = await BmpImageDecoder.DecodeAsync(input);

        Assert.True(result.Succeeded);
        Assert.Equal(EvidenceStage.ContentUsable, result.Stage);
        Assert.Equal(1, result.Image!.Width);
        Assert.Equal(2, result.Image.Height);
        Assert.Equal([255, 0, 0, 255, 0, 0, 255, 255], result.Image.Pixels);
        Assert.Equal("BMP_PIXELS_DECODED", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task Encode_RoundTripsDecodedPixelsThroughThePngValidator()
    {
        await using var input = new MemoryStream(Create24BitBmp());
        var decoded = await BmpImageDecoder.DecodeAsync(input);

        var png = PngRgbaEncoder.Encode(decoded.Image!);
        await using var pngInput = new MemoryStream(png);
        var validation = await PngValidator.ValidateAsync(pngInput);

        Assert.True(validation.IsValid);
        Assert.Equal(1, validation.Width);
        Assert.Equal(2, validation.Height);
        Assert.Equal((byte)6, validation.ColorType);
    }

    private static byte[] Create24BitBmp()
    {
        const int pixelOffset = 54;
        const int rowLength = 4;
        var bmp = new byte[pixelOffset + rowLength * 2];
        "BM"u8.CopyTo(bmp);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2), (uint)bmp.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10), pixelOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(28), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(34), rowLength * 2);
        new byte[] { 255, 0, 0 }.CopyTo(bmp.AsSpan(pixelOffset, 3));
        new byte[] { 0, 0, 255 }.CopyTo(bmp.AsSpan(pixelOffset + rowLength, 3));
        return bmp;
    }
}
