using System.Buffers.Binary;
using System.IO.Compression;
using KiriScope.Core.Evidence;
using KiriScope.Resources;

namespace KiriScope.Core.Tests;

public sealed class PngValidatorTests
{
    [Fact]
    public async Task ValidateAsync_AcceptsACompleteOnePixelPng()
    {
        await using var input = new MemoryStream(CreatePng([0, 0]));

        var result = await PngValidator.ValidateAsync(input);

        Assert.Equal(EvidenceStage.FormatValidated, result.Stage);
        Assert.Equal(1, result.Width);
        Assert.Equal(1, result.Height);
        Assert.Equal(2, result.IdatDecompressedBytes);
        Assert.Equal("PNG_VALIDATED", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task ValidateAsync_RejectsACrcMismatch()
    {
        var png = CreatePng([0, 0]);
        png[^1] ^= 0xFF;
        await using var input = new MemoryStream(png);

        var result = await PngValidator.ValidateAsync(input);

        Assert.False(result.IsValid);
        Assert.Equal("PNG_CRC_MISMATCH", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task ValidateAsync_ReportsAnUnidentifiedStageForNonPngInput()
    {
        await using var input = new MemoryStream("not a png"u8.ToArray());

        var result = await PngValidator.ValidateAsync(input);

        Assert.Equal(EvidenceStage.Unidentified, result.Stage);
        Assert.Equal("PNG_SIGNATURE_MISMATCH", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task ValidateAsync_RejectsUnexpectedInflatedScanlineLength()
    {
        await using var input = new MemoryStream(CreatePng([0]));

        var result = await PngValidator.ValidateAsync(input);

        Assert.False(result.IsValid);
        Assert.Equal("PNG_IDAT_DECOMPRESSION_FAILED", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task ValidateAsync_ReportsAnOversizedDimensionWithoutThrowing()
    {
        await using var input = new MemoryStream(CreatePng([0], uint.MaxValue, 1));

        var result = await PngValidator.ValidateAsync(input);

        Assert.False(result.IsValid);
        Assert.Equal("PNG_IHDR_VALUES_INVALID", Assert.Single(result.Diagnostics).Code);
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, ResourceFormat.Png)]
    [InlineData(new byte[] { 0x4F, 0x67, 0x67, 0x53 }, ResourceFormat.Ogg)]
    [InlineData(new byte[] { 0x50, 0x53, 0x42, 0x00 }, ResourceFormat.Psb)]
    [InlineData(new byte[] { 0x54, 0x4C, 0x47, 0x36, 0x2E, 0x30 }, ResourceFormat.Tlg)]
    [InlineData(new byte[] { 0x00, 0x00, 0x01, 0xBA }, ResourceFormat.MpegProgramStream)]
    [InlineData(new byte[] { 0x4F, 0x54, 0x54, 0x4F }, ResourceFormat.OpenTypeFont)]
    [InlineData(new byte[] { 0x00, 0x01, 0x00, 0x00 }, ResourceFormat.OpenTypeFont)]
    public void Detect_RecognizesKnownResourceMagic(byte[] header, ResourceFormat expected)
    {
        Assert.Equal(expected, ResourceFormatDetector.Detect(header));
    }

    private static byte[] CreatePng(byte[] decompressedScanlines, uint width = 1, uint height = 1)
    {
        using var imageHeader = new MemoryStream();
        using (var writer = new BinaryWriter(imageHeader, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WriteUInt32BigEndian(writer, width);
            WriteUInt32BigEndian(writer, height);
            writer.Write((byte)8);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((byte)0);
        }

        byte[] idat;
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                zlib.Write(decompressedScanlines);
            }

            idat = compressed.ToArray();
        }

        using var output = new MemoryStream();
        output.Write(PngValidator.Signature);
        using (var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WriteChunk(writer, 0x49484452, imageHeader.ToArray());
            WriteChunk(writer, 0x49444154, idat);
            WriteChunk(writer, 0x49454E44, []);
        }

        return output.ToArray();
    }

    private static void WriteChunk(BinaryWriter writer, uint type, byte[] data)
    {
        WriteUInt32BigEndian(writer, (uint)data.Length);
        WriteUInt32BigEndian(writer, type);
        writer.Write(data);
        WriteUInt32BigEndian(writer, ComputeCrc(type, data));
    }

    private static void WriteUInt32BigEndian(BinaryWriter writer, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        writer.Write(bytes);
    }

    private static uint ComputeCrc(uint chunkType, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFU;
        Span<byte> type = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(type, chunkType);
        crc = UpdateCrc(crc, type);
        crc = UpdateCrc(crc, data);
        return ~crc;
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        const uint polynomial = 0xEDB88320;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ polynomial : crc >> 1;
            }
        }

        return crc;
    }
}
