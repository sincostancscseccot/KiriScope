using System.Buffers.Binary;
using KiriScope.Core.Evidence;
using KiriScope.Xp3;

namespace KiriScope.Core.Tests;

public sealed class Xp3ArchiveProbeTests
{
    [Fact]
    public async Task ProbeAsync_RecognizesStandardSignatureAndIndexOffset()
    {
        var bytes = new byte[Xp3Signature.Bytes.Length + sizeof(long)];
        Xp3Signature.Bytes.CopyTo(bytes);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(Xp3Signature.Bytes.Length), 0x40);

        await using var stream = new MemoryStream(bytes);
        var result = await Xp3ArchiveProbe.ProbeAsync(stream);

        Assert.Equal(EvidenceStage.ContainerIdentified, result.Stage);
        Assert.Equal(0x40, result.IndexOffset);
        Assert.Equal("XP3_HEADER_IDENTIFIED", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task ProbeAsync_RejectsNonXp3Input()
    {
        await using var stream = new MemoryStream("not an xp3 archive"u8.ToArray());

        var result = await Xp3ArchiveProbe.ProbeAsync(stream);

        Assert.Equal(EvidenceStage.Unidentified, result.Stage);
        Assert.Equal("XP3_SIGNATURE_MISMATCH", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task ProbeAsync_DiagnosesTruncatedIndexOffset()
    {
        await using var stream = new MemoryStream(Xp3Signature.Bytes.ToArray());

        var result = await Xp3ArchiveProbe.ProbeAsync(stream);

        Assert.Equal(EvidenceStage.ContainerIdentified, result.Stage);
        Assert.Null(result.IndexOffset);
        Assert.Equal("XP3_INDEX_OFFSET_MISSING", Assert.Single(result.Diagnostics).Code);
    }
}
