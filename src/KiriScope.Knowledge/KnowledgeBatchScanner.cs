using KiriScope.Analysis;
using KiriScope.Core.Diagnostics;
using KiriScope.IO.Hashing;
using KiriScope.Xp3;

namespace KiriScope.Knowledge;

/// <summary>Bounded, read-only scan that proposes fingerprint candidates without applying any scheme.</summary>
public static class KnowledgeBatchScanner
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe",
        ".dll",
        ".tpm",
        ".xp3",
    };

    public static async Task<KnowledgeBatchScanReport> ScanAsync(
        KnowledgeBase knowledgeBase,
        string inputDirectory,
        KnowledgeBatchScanOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(knowledgeBase);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputDirectory);
        options ??= new KnowledgeBatchScanOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumFiles);

        var root = Path.GetFullPath(inputDirectory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Knowledge scan input directory does not exist: {root}");
        }

        var diagnostics = new List<KiriScopeDiagnostic>();
        var paths = new List<string>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
            }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!SupportedExtensions.Contains(Path.GetExtension(path)))
                {
                    continue;
                }

                paths.Add(path);
                if (paths.Count >= options.MaximumFiles)
                {
                    diagnostics.Add(new KiriScopeDiagnostic("KNOWLEDGE_SCAN_FILE_COUNT_CAPPED", DiagnosticSeverity.Warning, $"Knowledge scan stopped at the configured {options.MaximumFiles:N0} supported file(s)."));
                    break;
                }
            }
        }
        catch (IOException exception)
        {
            diagnostics.Add(new KiriScopeDiagnostic("KNOWLEDGE_SCAN_ENUMERATION_FAILED", DiagnosticSeverity.Error, exception.Message));
        }

        paths.Sort(StringComparer.OrdinalIgnoreCase);
        var items = new List<KnowledgeScanItem>(paths.Count);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(await ScanFileAsync(knowledgeBase, root, path, options, cancellationToken).ConfigureAwait(false));
        }

        return new KnowledgeBatchScanReport(
            KnowledgeBatchScanReport.CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            root,
            new KnowledgeBaseIdentity(knowledgeBase.Id, knowledgeBase.SchemaVersion, knowledgeBase.ManifestSha256),
            items,
            diagnostics);
    }

    private static async Task<KnowledgeScanItem> ScanFileAsync(
        KnowledgeBase knowledgeBase,
        string root,
        string path,
        KnowledgeBatchScanOptions options,
        CancellationToken cancellationToken)
    {
        var relativePath = Path.GetRelativePath(root, path);
        var info = new FileInfo(path);
        var diagnostics = new List<KiriScopeDiagnostic>();
        try
        {
            if (string.Equals(Path.GetExtension(path), ".xp3", StringComparison.OrdinalIgnoreCase))
            {
                var hash = await Sha256Hasher.ComputeFileAsync(path, cancellationToken).ConfigureAwait(false);
                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var probe = await Xp3ArchiveProbe.ProbeAsync(input, cancellationToken).ConfigureAwait(false);
                diagnostics.AddRange(probe.Diagnostics);
                return new KnowledgeScanItem(relativePath, path, hash, info.Length, "Xp3Archive", probe.Stage.ToString(), Array.Empty<KnowledgeSchemeCandidate>(), diagnostics);
            }

            var analysis = await StaticBinaryAnalyzer.AnalyzeFileAsync(path, options.StaticAnalysis, cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(analysis.Diagnostics);
            var candidates = KnowledgeFingerprintMatcher.Match(knowledgeBase, analysis);
            return new KnowledgeScanItem(
                relativePath,
                path,
                analysis.Input.Sha256,
                analysis.Input.Length,
                "Binary",
                analysis.Pe is null ? "Unidentified" : "ContainerIdentified",
                candidates,
                diagnostics);
        }
        catch (IOException exception)
        {
            diagnostics.Add(new KiriScopeDiagnostic("KNOWLEDGE_SCAN_FILE_READ_FAILED", DiagnosticSeverity.Warning, exception.Message));
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new KiriScopeDiagnostic("KNOWLEDGE_SCAN_FILE_ACCESS_DENIED", DiagnosticSeverity.Warning, exception.Message));
        }

        return new KnowledgeScanItem(relativePath, path, string.Empty, info.Exists ? info.Length : 0, "Unknown", "Unidentified", Array.Empty<KnowledgeSchemeCandidate>(), diagnostics);
    }
}

/// <summary>Limits for a read-only knowledge-base scan.</summary>
public sealed record KnowledgeBatchScanOptions
{
    public int MaximumFiles { get; init; } = 1_024;

    public StaticAnalysisOptions StaticAnalysis { get; init; } = new();
}
