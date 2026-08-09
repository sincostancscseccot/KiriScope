using System.Buffers.Binary;
using KiriScope.Core.Evidence;
using KiriScope.Resources;

namespace KiriScope.Core.Tests;

public sealed class BmpValidatorTests
{
    [Fact]
    public async Task ValidateAsync_AcceptsACompleteUncompressedBitmap()
    {
        await using var input = new MemoryStream(CreateBmp(width: 2, height: 1, bitCount: 24));

        var result = await BmpValidator.ValidateAsync(input);

        Assert.True(result.IsValid);
        Assert.Equal(EvidenceStage.FormatValidated, result.Stage);
        Assert.Equal(2, result.Width);
        Assert.Equal(1, result.Height);
        Assert.Equal((ushort)24, result.BitCount);
        Assert.Equal(8, result.PixelDataLength);
        Assert.Equal("BMP_VALIDATED", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task ValidateAsync_RejectsTruncatedPixelData()
    {
        var bmp = CreateBmp(width: 1, height: 1, bitCount: 24);
        Array.Resize(ref bmp, bmp.Length - 1);
        await using var input = new MemoryStream(bmp);

        var result = await BmpValidator.ValidateAsync(input);

        Assert.False(result.IsValid);
        Assert.Equal("BMP_DECLARED_LENGTH_INVALID", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task ValidateAsync_IdentifiesButDoesNotValidateCompressedPixels()
    {
        await using var input = new MemoryStream(CreateBmp(width: 1, height: 1, bitCount: 8, compression: 1, imageSize: 2));

        var result = await BmpValidator.ValidateAsync(input);

        Assert.False(result.IsValid);
        Assert.Equal(EvidenceStage.ContainerIdentified, result.Stage);
        Assert.Equal("BMP_COMPRESSED_CONTAINER_IDENTIFIED", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task ValidateAsync_RejectsALegacyDibHeader()
    {
        var bmp = new byte[18];
        "BM"u8.CopyTo(bmp);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2), (uint)bmp.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10), 18);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(14), 12);
        await using var input = new MemoryStream(bmp);

        var result = await BmpValidator.ValidateAsync(input);

        Assert.False(result.IsValid);
        Assert.Equal("BMP_DIB_HEADER_UNSUPPORTED", Assert.Single(result.Diagnostics).Code);
    }

    private static byte[] CreateBmp(int width, int height, ushort bitCount, uint compression = 0, uint imageSize = 0)
    {
        var paletteLength = bitCount <= 8 ? (1 << bitCount) * 4 : 0;
        var pixelOffset = 14 + 40 + paletteLength;
        var rowLength = ((width * bitCount + 31) / 32) * 4;
        var pixelsLength = compression == 0 ? rowLength * height : (int)imageSize;
        var data = new byte[pixelOffset + pixelsLength];
        "BM"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(2), (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(10), (uint)pixelOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(22), height);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(28), bitCount);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(30), compression);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(34), imageSize);
        return data;
    }
}
