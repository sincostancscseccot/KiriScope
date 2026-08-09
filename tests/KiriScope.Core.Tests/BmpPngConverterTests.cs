using KiriScope.Core.Evidence;
using KiriScope.Resources;

namespace KiriScope.Core.Tests;

public sealed class BmpPngConverterTests
{
    [Fact]
    public async Task ConvertAsync_WritesANewValidatedPngWithoutChangingTheInput()
    {
        var directory = Path.Combine(Path.GetTempPath(), "KiriScope.Tests", Guid.NewGuid().ToString("N"));
        var bmpPath = Path.Combine(directory, "source.bmp");
        var pngPath = Path.Combine(directory, "converted.png");
        Directory.CreateDirectory(directory);
        try
        {
            var bmp = CreateBmp();
            await File.WriteAllBytesAsync(bmpPath, bmp);

            var result = await BmpPngConverter.ConvertAsync(bmpPath, pngPath);

            Assert.True(result.Succeeded);
            Assert.Equal(EvidenceStage.FormatValidated, result.Stage);
            Assert.Equal(bmp, await File.ReadAllBytesAsync(bmpPath));
            await using var png = File.OpenRead(pngPath);
            Assert.True((await PngValidator.ValidateAsync(png)).IsValid);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static byte[] CreateBmp()
    {
        var bmp = new byte[58];
        "BM"u8.CopyTo(bmp);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2), (uint)bmp.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10), 54);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(14), 40);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(26), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(28), 24);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(34), 4);
        bmp[54] = 1;
        bmp[55] = 2;
        bmp[56] = 3;
        return bmp;
    }
}
