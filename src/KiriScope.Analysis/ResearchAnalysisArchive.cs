using KiriScope.Core.Diagnostics;

namespace KiriScope.Analysis;

/// <summary>Persisted, self-describing static-analysis artifact that does not embed the input binary.</summary>
public sealed record ResearchAnalysisArchive(
    string SchemaVersion,
    DateTimeOffset CreatedAtUtc,
    string ReproductionCommand,
    StaticBinaryAnalysisReport Report,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics)
{
    public const string CurrentSchemaVersion = "1.0";
}
