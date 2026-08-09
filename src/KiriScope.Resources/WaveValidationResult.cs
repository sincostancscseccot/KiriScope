using KiriScope.Core.Diagnostics;
using KiriScope.Core.Evidence;

namespace KiriScope.Resources;

public sealed record WaveValidationResult(
    EvidenceStage Stage,
    ushort? FormatTag,
    ushort? ChannelCount,
    uint? SampleRate,
    ushort? BitsPerSample,
    long DataBytes,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics)
{
    public bool IsValid => Stage >= EvidenceStage.FormatValidated;
}
