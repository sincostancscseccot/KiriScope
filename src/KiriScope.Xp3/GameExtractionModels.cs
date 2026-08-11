using KiriScope.Core.Diagnostics;
using KiriScope.Core.Evidence;
using KiriScope.Plugins.Abstractions.Filters;
using KiriScope.Resources;
using System.Text.Json.Serialization;

namespace KiriScope.Xp3;

/// <summary>Describes the user-facing source accepted by the one-click extraction flow.</summary>
public enum GameInputKind
{
    GameDirectory,
    Xp3Archive,
    GamePackage,
}

/// <summary>Identifies the path and kind of a game input without exposing implementation details to callers.</summary>
public sealed record GameInput(string InputPath, GameInputKind Kind)
{
    public static GameInput FromPath(string inputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        var fullPath = Path.GetFullPath(inputPath);
        if (Directory.Exists(fullPath))
        {
            return new GameInput(fullPath, GameInputKind.GameDirectory);
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The selected game input does not exist.", fullPath);
        }

        return Path.GetExtension(fullPath) switch
        {
            var extension when extension.Equals(".xp3", StringComparison.OrdinalIgnoreCase) => new GameInput(fullPath, GameInputKind.Xp3Archive),
            var extension when extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) => new GameInput(fullPath, GameInputKind.GamePackage),
            _ => throw new ArgumentException("The selected file must be an XP3 archive or a supported complete game package (ZIP).", nameof(inputPath)),
        };
    }
}

/// <summary>The resource categories exposed by the ordinary extraction flow.</summary>
public enum ResourceCategory
{
    All,
    Images,
    Audio,
    Scripts,
    Other,
}

/// <summary>Safety limits and optional behavior for one extraction task.</summary>
public sealed record GameExtractionOptions
{
    public int MaximumDiscoveredArchiveCount { get; init; } = 2_048;

    public int MaximumPackageEntryCount { get; init; } = 20_000;

    public long MaximumPackageEntryUnpackedBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    public long MaximumPackageUnpackedBytes { get; init; } = 8L * 1024 * 1024 * 1024;

    public string? TemporaryRootDirectory { get; init; }

    public Xp3EntryExtractionOptions? EntryExtractionOptions { get; init; }

    /// <summary>
    /// When no verified content filter is available, test marked entries as unfiltered only
    /// when their XP3 Adler-32 can prove the result. A mismatch leaves no output behind.
    /// </summary>
    public bool ProbeMarkedEntriesWithoutFilter { get; init; } = true;

    /// <summary>Whether newly exported files should receive bounded signature and structural validation.</summary>
    public bool ValidateExtractedResources { get; init; } = true;

    /// <summary>Maximum extracted file length eligible for post-extraction structural validation.</summary>
    public long MaximumResourceValidationBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>
    /// Optional trusted compatibility resolver. Ordinary callers never supply a scheme JSON;
    /// the resolver may return a filter only after a deterministic, verified fingerprint match.
    /// </summary>
    public IGameCompatibilityResolver? CompatibilityResolver { get; init; }

    /// <summary>
    /// Optional runtime-assisted fallback for a complete KiriKiri game directory. Implementations must
    /// decline safely when their prerequisites are not present; the normal XP3 path then remains in use.
    /// </summary>
    public IGameRuntimeExtractionFallback? RuntimeExtractionFallback { get; init; }
}

/// <summary>Terminal state of a compatibility lookup performed before one-click extraction.</summary>
public enum GameCompatibilityResolutionKind
{
    NotConfigured,
    NoMatch,
    Selected,
    Ambiguous,
    Unavailable,
}

/// <summary>One audited, hash-bound compatibility configuration candidate.</summary>
public sealed record GameCompatibilityCandidate(
    string SchemeId,
    string SchemeRevision,
    string DisplayName,
    string AlgorithmId,
    string AlgorithmVersion,
    string FingerprintId,
    string InputPath,
    string InputSha256,
    IReadOnlyList<string> MatchedEvidence,
    IReadOnlyList<string> VerifiedTargets,
    [property: JsonIgnore] IContentFilter? ContentFilter = null);

/// <summary>Compatibility lookup evidence carried into an extraction report.</summary>
public sealed record GameCompatibilityResolution(
    GameCompatibilityResolutionKind Kind,
    GameCompatibilityCandidate? Selected,
    IReadOnlyList<GameCompatibilityCandidate> Candidates,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics)
{
    public static GameCompatibilityResolution NotConfigured { get; } = new(
        GameCompatibilityResolutionKind.NotConfigured,
        null,
        Array.Empty<GameCompatibilityCandidate>(),
        Array.Empty<KiriScopeDiagnostic>());
}

/// <summary>Extensibility point that keeps the XP3 extraction core independent of any knowledge-base implementation.</summary>
public interface IGameCompatibilityResolver
{
    Task<GameCompatibilityResolution> ResolveAsync(
        GameInput input,
        GameInputDiscoveryResult discovery,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional host-provided runtime extraction route. This keeps the XP3 core independent from a particular
/// injected helper while allowing a packaged GUI to recover resources that require the game's own decoder.
/// </summary>
public interface IGameRuntimeExtractionFallback
{
    Task<ExtractionTaskResult?> TryExtractAsync(
        GameInput input,
        ResourceCategory category,
        string outputDirectory,
        GameInputDiscoveryResult discovery,
        GameCompatibilityResolution compatibility,
        IProgress<string>? progress,
        CancellationToken cancellationToken = default);
}

/// <summary>One XP3 archive discovered in a directory or complete game package.</summary>
public sealed record DiscoveredGameArchive(string SourcePath, string RelativePath, bool IsPackaged);

/// <summary>Discovery results used both for preflight and task reporting.</summary>
public sealed record GameInputDiscoveryResult(
    GameInput Input,
    IReadOnlyList<DiscoveredGameArchive> Archives,
    IReadOnlyList<string> Executables,
    IReadOnlyList<string> Plugins,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics)
{
    public bool HasErrors => Diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
}

/// <summary>Per-XP3 summary from a one-click extraction task.</summary>
public sealed record GameArchiveExtractionResult(
    string SourcePath,
    bool WasTemporarilyStaged,
    bool IndexWasParsed,
    int DiscoveredEntryCount,
    int SelectedEntryCount,
    int ExtractedEntryCount,
    int SkippedEntryCount,
    IReadOnlyList<Xp3EntryExtractionResult> Entries,
    IReadOnlyList<GameExtractedResourceValidation> ResourceValidations,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics);

/// <summary>Evidence collected after one output file has been extracted by the ordinary game flow.</summary>
public sealed record GameExtractedResourceValidation(
    string EntryName,
    string OutputRelativePath,
    ResourceCategory PathCategory,
    ResourceFormat DetectedFormat,
    ResourceCategory? DetectedCategory,
    EvidenceStage Stage,
    bool ValidationAttempted,
    bool IsFormatValidated,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics);

/// <summary>Auditable result of a complete one-click extraction task.</summary>
public sealed record ExtractionTaskResult(
    GameInput Input,
    ResourceCategory Category,
    GameCompatibilityResolution Compatibility,
    string OutputDirectory,
    bool OutputDirectoryCreated,
    IReadOnlyList<GameArchiveExtractionResult> Archives,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics)
{
    public int ExtractedEntryCount => Archives.Sum(static archive => archive.ExtractedEntryCount);

    public int SkippedEntryCount => Archives.Sum(static archive => archive.SkippedEntryCount);

    public int SelectedEntryCount => Archives.Sum(static archive => archive.SelectedEntryCount);

    public int TemporarilyStagedArchiveCount => Archives.Count(static archive => archive.WasTemporarilyStaged);

    public IReadOnlyList<GameExtractedResourceValidation> ResourceValidations => Archives
        .SelectMany(static archive => archive.ResourceValidations)
        .ToArray();

    public int RecognizedResourceCount => ResourceValidations.Count(static item => item.DetectedFormat != ResourceFormat.Unknown);

    public int FormatValidatedResourceCount => ResourceValidations.Count(static item => item.IsFormatValidated);

    public int ValidationSkippedResourceCount => ResourceValidations.Count(static item => !item.ValidationAttempted);

    public int CategoryMismatchCount => ResourceValidations.Count(static item => item.DetectedCategory is not null && item.DetectedCategory != item.PathCategory);

    public bool HasErrors => Diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) ||
        Archives.Any(static archive => !archive.IndexWasParsed);
}
