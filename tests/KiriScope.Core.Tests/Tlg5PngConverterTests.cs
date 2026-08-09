using System.Buffers.Binary;
using KiriScope.Core.Evidence;
using KiriScope.Resources;

namespace KiriScope.Core.Tests;

public sealed class Tlg5PngConverterTests
{
    [Fact]
    public async Task ConvertAsync_WritesANewValidatedPng()
    {
        var directory = Path.Combine(Path.GetTempPath(), "KiriScope.Tests", Guid.NewGuid().ToString("N"));
        var tlgPath = Path.Combine(directory, "source.tlg");
        var pngPath = Path.Combine(directory, "converted.png");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllBytesAsync(tlgPath, CreateTlg5());

            var result = await Tlg5PngConverter.ConvertAsync(tlgPath, pngPath);

            Assert.True(result.Succeeded);
            Assert.Equal(EvidenceStage.FormatValidated, result.Stage);
            await using var png = File.OpenRead(pngPath);
            var validation = await PngValidator.ValidateAsync(png);
            Assert.True(validation.IsValid);
            Assert.Equal(1, validation.Width);
            Assert.Equal(1, validation.Height);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
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
