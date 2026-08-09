using System.Buffers.Binary;
using KiriScope.Core.Evidence;
using KiriScope.Resources;

namespace KiriScope.Core.Tests;

public sealed class JpegValidatorTests
{
    [Fact]
    public async Task ValidateAsync_AcceptsAFramedBaselineJpeg()
    {
        await using var input = new MemoryStream(CreateJpeg());

        var result = await JpegValidator.ValidateAsync(input);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Equal(EvidenceStage.FormatValidated, result.Stage);
        Assert.Equal(3, result.Width);
        Assert.Equal(2, result.Height);
        Assert.Equal((byte)8, result.Precision);
        Assert.Equal((byte)1, result.ComponentCount);
        Assert.Equal(1, result.ScanCount);
        Assert.Equal("JPEG_VALIDATED", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task ValidateAsync_RejectsATruncatedSegment()
    {
        var jpeg = CreateJpeg();
        BinaryPrimitives.WriteUInt16BigEndian(jpeg.AsSpan(4), 100);
        await using var input = new MemoryStream(jpeg);

        var result = await JpegValidator.ValidateAsync(input);

        Assert.False(result.IsValid);
        Assert.Equal("JPEG_SEGMENT_TRUNCATED", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task ValidateAsync_RejectsAScanBeforeAFrame()
    {
        var jpeg = CreateJpeg();
        jpeg[3] = 0xDA;
        await using var input = new MemoryStream(jpeg);

        var result = await JpegValidator.ValidateAsync(input);

        Assert.False(result.IsValid);
        Assert.Equal("JPEG_SCAN_MARKER_INVALID", Assert.Single(result.Diagnostics).Code);
    }

    private static byte[] CreateJpeg()
    {
        using var output = new MemoryStream();
        output.Write([0xFF, 0xD8]);
        output.Write([0xFF, 0xC0]);
        WriteUInt16(output, 11);
        output.Write([8, 0, 2, 0, 3, 1, 1, 0x11, 0]);
        output.Write([0xFF, 0xDA]);
        WriteUInt16(output, 8);
        output.Write([1, 1, 0, 0, 63, 0]);
        output.Write([0x2A, 0xFF, 0]);
        output.Write([0xFF, 0xD9]);
        return output.ToArray();
    }

    private static void WriteUInt16(Stream output, ushort value)
    {
        Span<byte> data = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16BigEndian(data, value);
        output.Write(data);
    }
}
