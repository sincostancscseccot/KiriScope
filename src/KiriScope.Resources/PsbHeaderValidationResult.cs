using KiriScope.Core.Diagnostics;
using KiriScope.Core.Evidence;

namespace KiriScope.Resources;

public sealed record PsbHeaderValidationResult(
    EvidenceStage Stage,
    ushort? Version,
    bool HeaderMayBeEncrypted,
    uint? HeaderLength,
    uint? NamesOffset,
    uint? EntriesOffset,
    uint? ChunkOffsetsTableOffset,
    uint? ChunkLengthsTableOffset,
    uint? ChunkDataOffset,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics)
{
    public bool IsRecognized => Stage >= EvidenceStage.ContainerIdentified;
}
