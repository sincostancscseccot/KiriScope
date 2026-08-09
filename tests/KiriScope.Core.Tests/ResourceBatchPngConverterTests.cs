using System.Buffers.Binary;
using KiriScope.Resources;

namespace KiriScope.Core.Tests;

public sealed class ResourceBatchPngConverterTests
{
    [Fact]
    public async Task ConvertDirectoryAsync_PreservesRelativePathsForBmpAndTlg5()
    {
        var root = Path.Combine(Path.GetTempPath(), "KiriScope.Tests", Guid.NewGuid().ToString("N"));
        var input = Path.Combine(root, "input");
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(Path.Combine(input, "nested"));
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(input, "sprite.bmp"), CreateBmp());
            await File.WriteAllBytesAsync(Path.Combine(input, "nested", "background.tlg"), CreateTlg5());

            var result = await ResourceBatchPngConverter.ConvertDirectoryAsync(input, output);

            Assert.Equal(2, result.ConvertedCount);
            Assert.Equal(0, result.FailedCount);
            await using var bmpPng = File.OpenRead(Path.Combine(output, "sprite.png"));
            await using var tlgPng = File.OpenRead(Path.Combine(output, "nested", "background.png"));
            Assert.True((await PngValidator.ValidateAsync(bmpPng)).IsValid);
            Assert.True((await PngValidator.ValidateAsync(tlgPng)).IsValid);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConvertDirectoryAsync_RejectsAnOutputDirectoryInsideTheInputDirectory()
    {
        var input = Path.Combine(Path.GetTempPath(), "KiriScope.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(input);
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => ResourceBatchPngConverter.ConvertDirectoryAsync(input, Path.Combine(input, "png")));
        }
        finally
        {
            if (Directory.Exists(input)) Directory.Delete(input, recursive: true);
        }
    }

    private static byte[] CreateBmp()
    {
        var bmp = new byte[58];
        "BM"u8.CopyTo(bmp);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2), (uint)bmp.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10), 54);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(28), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(34), 4);
        return bmp;
    }

    private static byte[] CreateTlg5()
    {
        using var output = new MemoryStream();
        output.Write("TLG5.0"u8);
        output.Write("\0raw\x1a"u8);
        output.WriteByte(3);
        WriteUInt32(output, 1);
        WriteUInt32(output, 1);
        WriteUInt32(output, 1);
        WriteUInt32(output, 0);
        foreach (var value in new byte[] { 10, 20, 30 })
        {
            output.WriteByte(1);
            WriteUInt32(output, 1);
            output.WriteByte(value);
        }

        return output.ToArray();
    }

    private static void WriteUInt32(Stream output, uint value)
    {
        Span<byte> data = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(data, value);
        output.Write(data);
    }
}
