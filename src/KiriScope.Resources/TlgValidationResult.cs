using KiriScope.Core.Diagnostics;
using KiriScope.Core.Evidence;

namespace KiriScope.Resources;

/// <summary>
/// The result of a conservative TLG header and metadata validation.
/// This result intentionally does not claim that image pixels were decoded.
/// </summary>
public sealed record TlgValidationResult(
    EvidenceStage Stage,
    int? Version,
    int? Width,
    int? Height,
    byte? ColorChannels,
    int? DataOffset,
    bool HasSdsWrapper,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics)
{
    public bool IsRecognized => Stage >= EvidenceStage.ContainerIdentified;
}
