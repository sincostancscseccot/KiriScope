using KiriScope.Core.Diagnostics;

namespace KiriScope.Analysis;

/// <summary>Limits for read-only executable and plugin relationship discovery.</summary>
public sealed record PluginDirectoryAnalysisOptions
{
    public int MaximumBinaryFiles { get; init; } = 1_024;

    public StaticAnalysisOptions StaticAnalysis { get; init; } = new();
}

/// <summary>A PE import relation resolved, when possible, to a discovered local binary.</summary>
public sealed record PluginRelationship(
    string SourcePath,
    string ImportedModule,
    string? ResolvedPath,
    AnalysisFindingKind Kind = AnalysisFindingKind.ObservedFact);

/// <summary>Read-only inventory of executable/plugin binaries and their directly parsed imports.</summary>
public sealed record PluginDirectoryAnalysisReport(
    string DirectoryPath,
    IReadOnlyList<StaticBinaryAnalysisReport> Binaries,
    IReadOnlyList<PluginRelationship> Relationships,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics);

/// <summary>Discovers local executables, DLLs, and TPM plugins without loading any of them.</summary>
public static class PluginDirectoryAnalyzer
{
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe",
        ".dll",
        ".tpm",
    };

    public static async Task<PluginDirectoryAnalysisReport> AnalyzeAsync(
        string directoryPath,
        PluginDirectoryAnalysisOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        options ??= new PluginDirectoryAnalysisOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumBinaryFiles);
        ArgumentNullException.ThrowIfNull(options.StaticAnalysis);

        var fullDirectoryPath = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(fullDirectoryPath))
        {
            throw new DirectoryNotFoundException($"Plugin analysis directory does not exist: {fullDirectoryPath}");
        }

        var diagnostics = new List<KiriScopeDiagnostic>();
        var candidates = new List<string>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(
                fullDirectoryPath,
                "*",
                new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    ReturnSpecialDirectories = false,
                }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!BinaryExtensions.Contains(Path.GetExtension(path)))
                {
                    continue;
                }

                candidates.Add(path);
                if (candidates.Count >= options.MaximumBinaryFiles)
                {
                    diagnostics.Add(new KiriScopeDiagnostic(
                        "ANALYSIS_BINARY_COUNT_CAPPED",
                        DiagnosticSeverity.Warning,
                        $"Plugin discovery stopped at the configured {options.MaximumBinaryFiles:N0} executable/plugin files."));
                    break;
                }
            }
        }
        catch (IOException exception)
        {
            diagnostics.Add(new KiriScopeDiagnostic("ANALYSIS_DIRECTORY_ENUMERATION_FAILED", DiagnosticSeverity.Error, exception.Message));
        }

        candidates.Sort(StringComparer.OrdinalIgnoreCase);
        var reports = new List<StaticBinaryAnalysisReport>(candidates.Count);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                reports.Add(await StaticBinaryAnalyzer.AnalyzeFileAsync(candidate, options.StaticAnalysis, cancellationToken).ConfigureAwait(false));
            }
            catch (IOException exception)
            {
                diagnostics.Add(new KiriScopeDiagnostic("ANALYSIS_BINARY_READ_FAILED", DiagnosticSeverity.Warning, exception.Message));
            }
            catch (UnauthorizedAccessException exception)
            {
                diagnostics.Add(new KiriScopeDiagnostic("ANALYSIS_BINARY_ACCESS_DENIED", DiagnosticSeverity.Warning, exception.Message));
            }
        }

        var binaryPathsByName = reports
            .GroupBy(static report => Path.GetFileName(report.Input.FullPath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First().Input.FullPath, StringComparer.OrdinalIgnoreCase);
        var relationships = reports
            .Where(static report => report.Pe is not null)
            .SelectMany(report => report.Pe!.ImportedModules.Select(module =>
                new PluginRelationship(
                    report.Input.FullPath,
                    module,
                    binaryPathsByName.GetValueOrDefault(module))))
            .OrderBy(static relation => relation.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static relation => relation.ImportedModule, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new PluginDirectoryAnalysisReport(fullDirectoryPath, reports, relationships, diagnostics);
    }
}
