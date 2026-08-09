using KiriScope.Core.Diagnostics;
using KiriScope.Core.Evidence;

namespace KiriScope.Resources;

public sealed record BmpValidationResult(
    EvidenceStage Stage,
    int? Width,
    int? Height,
    ushort? BitCount,
    uint? Compression,
    long PixelDataOffset,
    long PixelDataLength,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics)
{
    public bool IsValid => Stage >= EvidenceStage.FormatValidated;
}
