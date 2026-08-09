namespace KiriScope.Core.Projects;

/// <summary>Versioned root document for a KiriScope workspace.</summary>
public sealed record ProjectManifest(
    string SchemaVersion,
    string ProjectName,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<ArtifactRecord> Artifacts)
{
    public const string CurrentSchemaVersion = "1.0";

    public static ProjectManifest Create(string projectName) =>
        new(CurrentSchemaVersion, projectName, DateTimeOffset.UtcNow, Array.Empty<ArtifactRecord>());
}
