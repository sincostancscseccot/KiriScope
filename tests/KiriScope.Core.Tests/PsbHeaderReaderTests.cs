using System.Buffers.Binary;
using KiriScope.Core.Evidence;
using KiriScope.Resources;

namespace KiriScope.Core.Tests;

public sealed class PsbHeaderReaderTests
{
    [Fact]
    public async Task ReadAsync_IdentifiesAPlainVersion3Header()
    {
        await using var input = new MemoryStream(CreatePsb());
        var result = await PsbHeaderReader.ReadAsync(input);
        Assert.True(result.IsRecognized);
        Assert.Equal(EvidenceStage.ContainerIdentified, result.Stage);
        Assert.Equal((ushort)3, result.Version);
        Assert.Equal("PSB_HEADER_IDENTIFIED", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task ReadAsync_RejectsAnInvalidHeaderChecksum()
    {
        var psb = CreatePsb(); psb[40] ^= 1;
        await using var input = new MemoryStream(psb);
        var result = await PsbHeaderReader.ReadAsync(input);
        Assert.False(result.IsRecognized);
        Assert.Equal("PSB_HEADER_CHECKSUM_MISMATCH", Assert.Single(result.Diagnostics).Code);
    }

    private static byte[] CreatePsb()
    {
        var data = new byte[64]; "PSB\0"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), 3);
        foreach (var offset in new[] { 8, 12, 16, 20, 24, 28, 32, 36 }) BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), 44);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(40), Adler32(data.AsSpan(8, 32)));
        return data;
    }
    private static uint Adler32(ReadOnlySpan<byte> data) { uint a = 1, b = 0; foreach (var value in data) { a = (a + value) % 65521; b = (b + a) % 65521; } return (b << 16) | a; }
}
