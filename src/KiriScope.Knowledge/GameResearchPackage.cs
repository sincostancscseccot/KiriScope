using KiriScope.Analysis;
using KiriScope.Core.Diagnostics;
using KiriScope.IO.Hashing;
using KiriScope.Xp3;

namespace KiriScope.Knowledge;

/// <summary>One XP3 observed during an offline game research collection.</summary>
public sealed record ResearchPackageArchiveItem(
    string RelativePath,
    string FullPath,
    string Sha256,
    long Length,
    Xp3ArchiveProfile? Profile,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics);

/// <summary>Hash-only reference to a report that the user explicitly chose to associate with research.</summary>
public sealed record ResearchPackageEvidenceReference(
    string FullPath,
    string Sha256,
    long Length,
    string Kind);

/// <summary>Bounded options for a read-only game research package collection.</summary>
public sealed record GameResearchPackageOptions
{
    public int MaximumArchiveCount { get; init; } = 2_048;

    public int MaximumRuntimeEvidenceCount { get; init; } = 512;

    public string? KnowledgeRoot { get; init; }

    public IReadOnlyList<string>? RuntimeEvidencePaths { get; init; }

    public PluginDirectoryAnalysisOptions PluginAnalysis { get; init; } = new();
}

/// <summary>
/// Self-describing offline research artifact. It never embeds game archives, extracted resources,
/// raw binary strings, or process-memory data; only metadata, hashes, sanitized static reports and
/// explicit report references.
/// </summary>
public sealed record GameResearchPackage(
    string SchemaVersion,
    DateTimeOffset CreatedAtUtc,
    string ReproductionCommand,
    GameInput Input,
    IReadOnlyList<ResearchPackageArchiveItem> Archives,
    PluginDirectoryAnalysisReport? StaticAnalysis,
    KnowledgeBatchScanReport? KnowledgeScan,
    IReadOnlyList<ResearchPackageEvidenceReference> RuntimeEvidenceReferences,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics)
{
    public const string CurrentSchemaVersion = "1.0";
}

/// <summary>
/// Collects reproducible, read-only inputs for researching an authorized game directory. Dynamic
/// evidence is never captured here: callers may only attach hashes of reports created elsewhere
/// after explicit runtime authorization.
/// </summary>
public static class GameResearchPackageService
{
    public static async Task<GameResearchPackage> CollectAsync(
        string gameDirectory,
        string reproductionCommand,
        GameResearchPackageOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(reproductionCommand);
        options ??= new GameResearchPackageOptions();
        ValidateOptions(options);
        var input = GameInput.FromPath(gameDirectory);
        if (input.Kind != GameInputKind.GameDirectory)
        {
            throw new ArgumentException("A game research package requires a game directory input.", nameof(gameDirectory));
        }

        var diagnostics = new List<KiriScopeDiagnostic>();
        var discovery = await GameExtractionService.DiscoverAsync(
            input,
            new GameExtractionOptions { MaximumDiscoveredArchiveCount = options.MaximumArchiveCount },
            cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(discovery.Diagnostics);

        var archives = new List<ResearchPackageArchiveItem>(discovery.Archives.Count);
        foreach (var archive in discovery.Archives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            archives.Add(await CollectArchiveAsync(archive, cancellationToken).ConfigureAwait(false));
        }

        PluginDirectoryAnalysisReport? staticAnalysis = null;
        try
        {
            var fullStaticAnalysis = await PluginDirectoryAnalyzer.AnalyzeAsync(input.InputPath, options.PluginAnalysis, cancellationToken).ConfigureAwait(false);
            var redactedStringCount = fullStaticAnalysis.Binaries.Sum(static report => report.Strings.Count);
            staticAnalysis = RemoveRawBinaryStrings(fullStaticAnalysis);
            diagnostics.AddRange(staticAnalysis.Diagnostics);
            if (redactedStringCount > 0)
            {
                diagnostics.Add(Info("RESEARCH_STATIC_STRINGS_REDACTED", $"Removed {redactedStringCount:N0} raw binary string finding(s) from the research package."));
            }
        }
        catch (IOException exception)
        {
            diagnostics.Add(Warning("RESEARCH_STATIC_ANALYSIS_FAILED", exception.Message));
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(Warning("RESEARCH_STATIC_ANALYSIS_FAILED", exception.Message));
        }

        KnowledgeBatchScanReport? knowledgeScan = null;
        if (!string.IsNullOrWhiteSpace(options.KnowledgeRoot))
        {
            try
            {
                var knowledgeBase = await KnowledgeBaseLoader.LoadAsync(options.KnowledgeRoot, cancellationToken).ConfigureAwait(false);
                knowledgeScan = await KnowledgeBatchScanner.ScanAsync(knowledgeBase, input.InputPath, cancellationToken: cancellationToken).ConfigureAwait(false);
                diagnostics.AddRange(knowledgeScan.Diagnostics);
            }
            catch (KnowledgeBaseException exception)
            {
                diagnostics.Add(Warning(exception.Code, exception.Message));
            }
            catch (IOException exception)
            {
                diagnostics.Add(Warning("RESEARCH_KNOWLEDGE_SCAN_FAILED", exception.Message));
            }
            catch (UnauthorizedAccessException exception)
            {
                diagnostics.Add(Warning("RESEARCH_KNOWLEDGE_SCAN_FAILED", exception.Message));
            }
        }

        var runtimeEvidence = new List<ResearchPackageEvidenceReference>();
        var configuredEvidence = options.RuntimeEvidencePaths ?? Array.Empty<string>();
        if (configuredEvidence.Count > options.MaximumRuntimeEvidenceCount)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The configured runtime evidence count exceeds the permitted limit.");
        }

        foreach (var evidencePath in configuredEvidence)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(evidencePath))
            {
                diagnostics.Add(Warning("RESEARCH_RUNTIME_EVIDENCE_PATH_INVALID", "A runtime evidence path was empty and was ignored."));
                continue;
            }

            try
            {
                var fullPath = Path.GetFullPath(evidencePath);
                var info = new FileInfo(fullPath);
                if (!info.Exists)
                {
                    diagnostics.Add(Warning("RESEARCH_RUNTIME_EVIDENCE_NOT_FOUND", $"Runtime evidence report does not exist: {fullPath}"));
                    continue;
                }

                runtimeEvidence.Add(new ResearchPackageEvidenceReference(
                    fullPath,
                    await Sha256Hasher.ComputeFileAsync(fullPath, cancellationToken).ConfigureAwait(false),
                    info.Length,
                    "UserSuppliedRuntimeReport"));
            }
            catch (IOException exception)
            {
                diagnostics.Add(Warning("RESEARCH_RUNTIME_EVIDENCE_READ_FAILED", exception.Message));
            }
            catch (UnauthorizedAccessException exception)
            {
                diagnostics.Add(Warning("RESEARCH_RUNTIME_EVIDENCE_READ_FAILED", exception.Message));
            }
        }

        return new GameResearchPackage(
            GameResearchPackage.CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            reproductionCommand,
            input,
            archives,
            staticAnalysis,
            knowledgeScan,
            runtimeEvidence,
            diagnostics);
    }

    public static async Task<string> CollectAndWriteNewAsync(
        string gameDirectory,
        string outputPath,
        string reproductionCommand,
        GameResearchPackageOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var input = GameInput.FromPath(gameDirectory);
        if (input.Kind != GameInputKind.GameDirectory)
        {
            throw new ArgumentException("A game research package requires a game directory input.", nameof(gameDirectory));
        }

        var fullOutputPath = Path.GetFullPath(outputPath);
        if (IsPathContainedBy(input.InputPath, fullOutputPath))
        {
            throw new ArgumentException("A research package must be written outside the selected game directory.", nameof(outputPath));
        }

        var package = await CollectAsync(input.InputPath, reproductionCommand, options, cancellationToken).ConfigureAwait(false);
        return await KnowledgeReportArchiveWriter.WriteNewAsync(fullOutputPath, package, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ResearchPackageArchiveItem> CollectArchiveAsync(DiscoveredGameArchive archive, CancellationToken cancellationToken)
    {
        var diagnostics = new List<KiriScopeDiagnostic>();
        try
        {
            var info = new FileInfo(archive.SourcePath);
            var hash = await Sha256Hasher.ComputeFileAsync(archive.SourcePath, cancellationToken).ConfigureAwait(false);
            await using var input = new FileStream(
                archive.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 128 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            var index = await Xp3ArchiveReader.ReadIndexAsync(input, cancellationToken: cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(index.Diagnostics);
            return new ResearchPackageArchiveItem(
                archive.RelativePath,
                archive.SourcePath,
                hash,
                info.Length,
                Xp3ArchiveProfile.FromIndex(index),
                diagnostics);
        }
        catch (IOException exception)
        {
            diagnostics.Add(Warning("RESEARCH_XP3_READ_FAILED", exception.Message));
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(Warning("RESEARCH_XP3_READ_FAILED", exception.Message));
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(Warning("RESEARCH_XP3_READ_FAILED", exception.Message));
        }

        return new ResearchPackageArchiveItem(archive.RelativePath, archive.SourcePath, string.Empty, 0, null, diagnostics);
    }

    private static bool IsPathContainedBy(string rootDirectory, string candidatePath)
    {
        var root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateOptions(GameResearchPackageOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumArchiveCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumRuntimeEvidenceCount);
        ArgumentNullException.ThrowIfNull(options.PluginAnalysis);
    }

    private static KiriScopeDiagnostic Warning(string code, string message) => new(code, DiagnosticSeverity.Warning, message);

    private static KiriScopeDiagnostic Info(string code, string message) => new(code, DiagnosticSeverity.Info, message);

    private static PluginDirectoryAnalysisReport RemoveRawBinaryStrings(PluginDirectoryAnalysisReport report) => report with
    {
        Binaries = report.Binaries
            .Select(static binary => binary with { Strings = Array.Empty<BinaryStringFinding>() })
            .ToArray(),
    };
}
