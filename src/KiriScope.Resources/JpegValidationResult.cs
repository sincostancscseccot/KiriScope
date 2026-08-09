using KiriScope.Core.Diagnostics;
using KiriScope.Core.Evidence;

namespace KiriScope.Resources;

public sealed record JpegValidationResult(
    EvidenceStage Stage,
    int? Width,
    int? Height,
    byte? Precision,
    byte? ComponentCount,
    int ScanCount,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics)
{
    public bool IsValid => Stage >= EvidenceStage.FormatValidated;
}
