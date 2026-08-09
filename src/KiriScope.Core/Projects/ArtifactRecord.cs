using KiriScope.Core.Evidence;

namespace KiriScope.Core.Projects;

/// <summary>Tracks an input or generated artifact without embedding its contents in a manifest.</summary>
public sealed record ArtifactRecord(
    string RelativePath,
    string Sha256,
    long Length,
    EvidenceStage VerifiedStage,
    string? ParentArtifactId = null);
