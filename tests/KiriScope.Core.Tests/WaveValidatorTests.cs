using System.Buffers.Binary;
using KiriScope.Core.Evidence;
using KiriScope.Resources;

namespace KiriScope.Core.Tests;

public sealed class WaveValidatorTests
{
    [Fact]
    public async Task ValidateAsync_AcceptsAlignedPcmWave()
    {
        await using var input = new MemoryStream(CreateWave());

        var result = await WaveValidator.ValidateAsync(input);

        Assert.True(result.IsValid);
        Assert.Equal(EvidenceStage.FormatValidated, result.Stage);
        Assert.Equal((ushort)1, result.FormatTag);
        Assert.Equal((ushort)1, result.ChannelCount);
        Assert.Equal((uint)22_050, result.SampleRate);
        Assert.Equal((ushort)16, result.BitsPerSample);
        Assert.Equal(4, result.DataBytes);
        Assert.Equal("WAVE_UNCOMPRESSED_VALIDATED", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task ValidateAsync_RejectsInconsistentPcmByteRate()
    {
        var wave = CreateWave();
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(28), 1);
        await using var input = new MemoryStream(wave);

        var result = await WaveValidator.ValidateAsync(input);

        Assert.False(result.IsValid);
        Assert.Equal("WAVE_UNCOMPRESSED_METADATA_INVALID", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task ValidateAsync_RejectsAChunkThatExceedsRiffLength()
    {
        var wave = CreateWave();
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(40), 5);
        await using var input = new MemoryStream(wave);

        var result = await WaveValidator.ValidateAsync(input);

        Assert.False(result.IsValid);
        Assert.Equal("WAVE_CHUNK_TRUNCATED", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task ValidateAsync_IdentifiesCompressedWaveWithoutClaimingDecodedAudio()
    {
        await using var input = new MemoryStream(CreateWave(formatTag: 0x0055));

        var result = await WaveValidator.ValidateAsync(input);

        Assert.False(result.IsValid);
        Assert.Equal(EvidenceStage.ContainerIdentified, result.Stage);
        Assert.Equal("WAVE_COMPRESSED_CONTAINER_IDENTIFIED", Assert.Single(result.Diagnostics).Code);
    }

    private static byte[] CreateWave(ushort formatTag = 1)
    {
        var data = new byte[48];
        "RIFF"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 40);
        "WAVE"u8.CopyTo(data.AsSpan(8));
        "fmt "u8.CopyTo(data.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(20), formatTag);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), 22_050);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28), 44_100);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(34), 16);
        "data"u8.CopyTo(data.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(40), 4);
        return data;
    }
}
