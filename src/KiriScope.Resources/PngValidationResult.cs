using KiriScope.Core.Diagnostics;
using KiriScope.Core.Evidence;

namespace KiriScope.Resources;

public sealed record PngValidationResult(
    EvidenceStage Stage,
    int? Width,
    int? Height,
    byte? BitDepth,
    byte? ColorType,
    long IdatCompressedBytes,
    long IdatDecompressedBytes,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics)
{
    public bool IsValid => Stage >= EvidenceStage.FormatValidated;
}
