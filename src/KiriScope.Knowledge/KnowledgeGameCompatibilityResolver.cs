using System.IO.Compression;
using KiriScope.Analysis;
using KiriScope.Core.Diagnostics;
using KiriScope.Filters.BuiltIn;
using KiriScope.Plugins.Abstractions.Filters;
using KiriScope.Xp3;

namespace KiriScope.Knowledge;

/// <summary>Safety limits for fingerprinting selected files inside a complete game ZIP.</summary>
public sealed record KnowledgeGameCompatibilityResolverOptions
{
    public int MaximumPackageFingerprintSources { get; init; } = 2_048;

    public long MaximumPackageFingerprintSourceBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    public long MaximumPackageFingerprintBytes { get; init; } = 8L * 1024 * 1024 * 1024;

    public string? TemporaryRootDirectory { get; init; }
}

/// <summary>
/// Applies a knowledge base to the ordinary extraction flow only when one verified scheme revision
/// is selected by an exact SHA-256 fingerprint. Heuristic, filename-only and ambiguous matches are
/// deliberately excluded from automatic use. ZIP inputs are fingerprinted one safe entry at a time.
/// </summary>
public sealed class KnowledgeGameCompatibilityResolver : IGameCompatibilityResolver
{
    private const int CopyBufferSize = 128 * 1024;
    private readonly string _knowledgeRoot;
    private readonly KnowledgeGameCompatibilityResolverOptions _options;

    public KnowledgeGameCompatibilityResolver(string knowledgeRoot, KnowledgeGameCompatibilityResolverOptions? options = null)
    {
        _knowledgeRoot = Path.GetFullPath(knowledgeRoot);
        _options = options ?? new KnowledgeGameCompatibilityResolverOptions();
        ValidateOptions(_options);
    }

    public async Task<GameCompatibilityResolution> ResolveAsync(
        GameInput input,
        GameInputDiscoveryResult discovery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(discovery);
        var knowledgeBase = await KnowledgeBaseLoader.LoadAsync(_knowledgeRoot, cancellationToken).ConfigureAwait(false);
        var diagnostics = new List<KiriScopeDiagnostic>();
        var candidates = new List<GameCompatibilityCandidate>();
        if (input.Kind == GameInputKind.GamePackage)
        {
            await EvaluatePackagedSourcesAsync(knowledgeBase, input, discovery, candidates, diagnostics, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            foreach (var source in DiscoverLocalFingerprintSources(input, discovery))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await EvaluateSourceAsync(knowledgeBase, source, candidates, diagnostics, cancellationToken).ConfigureAwait(false);
            }
        }

        return CompleteResolution(candidates, diagnostics);
    }

    private async Task EvaluatePackagedSourcesAsync(
        KnowledgeBase knowledgeBase,
        GameInput input,
        GameInputDiscoveryResult discovery,
        List<GameCompatibilityCandidate> candidates,
        List<KiriScopeDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            using var package = ZipFile.OpenRead(input.InputPath);
            var sources = DiscoverPackagedFingerprintSources(discovery);
            if (sources.Count > _options.MaximumPackageFingerprintSources)
            {
                diagnostics.Add(Warning("KNOWLEDGE_AUTO_MATCH_PACKAGE_SOURCE_LIMIT", "The complete game package has more fingerprint sources than the configured compatibility limit."));
                sources = sources.Take(_options.MaximumPackageFingerprintSources).ToArray();
            }

            long totalStagedBytes = 0;
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = package.GetEntry(source.RelativePath);
                if (entry is null)
                {
                    diagnostics.Add(Warning("KNOWLEDGE_AUTO_MATCH_PACKAGE_ENTRY_MISSING", $"Package entry '{source.RelativePath}' is no longer available for compatibility fingerprinting."));
                    continue;
                }

                if (!TryValidatePackageEntry(entry, out var pathError))
                {
                    diagnostics.Add(Warning("KNOWLEDGE_AUTO_MATCH_PACKAGE_PATH_REJECTED", pathError));
                    continue;
                }

                if (entry.Length > _options.MaximumPackageFingerprintSourceBytes ||
                    entry.Length > long.MaxValue - totalStagedBytes ||
                    (totalStagedBytes += entry.Length) > _options.MaximumPackageFingerprintBytes)
                {
                    diagnostics.Add(Warning("KNOWLEDGE_AUTO_MATCH_PACKAGE_SIZE_LIMIT", $"Package entry '{source.RelativePath}' exceeds the compatibility fingerprinting limits."));
                    continue;
                }

                string? stagedPath = null;
                try
                {
                    stagedPath = await StagePackageEntryAsync(entry, cancellationToken).ConfigureAwait(false);
                    await EvaluateSourceAsync(knowledgeBase, source with { FullPath = stagedPath }, candidates, diagnostics, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidDataException exception)
                {
                    diagnostics.Add(Warning("KNOWLEDGE_AUTO_MATCH_PACKAGE_ENTRY_READ_FAILED", exception.Message));
                }
                catch (IOException exception)
                {
                    diagnostics.Add(Warning("KNOWLEDGE_AUTO_MATCH_PACKAGE_ENTRY_READ_FAILED", exception.Message));
                }
                finally
                {
                    if (stagedPath is not null)
                    {
                        TryDeleteFile(stagedPath);
                        TryDeleteEmptyDirectory(Path.GetDirectoryName(stagedPath)!);
                    }
                }
            }
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(Warning("KNOWLEDGE_AUTO_MATCH_PACKAGE_READ_FAILED", exception.Message));
        }
        catch (IOException exception)
        {
            diagnostics.Add(Warning("KNOWLEDGE_AUTO_MATCH_PACKAGE_READ_FAILED", exception.Message));
        }
    }

    private async Task<string> StagePackageEntryAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        var temporaryRoot = _options.TemporaryRootDirectory is null
            ? Path.Combine(Path.GetTempPath(), "KiriScope")
            : Path.GetFullPath(_options.TemporaryRootDirectory);
        var taskDirectory = Path.Combine(temporaryRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(taskDirectory);
        var stagedPath = Path.Combine(taskDirectory, "fingerprint" + Path.GetExtension(entry.FullName));
        try
        {
            await using var source = entry.Open();
            await using var destination = new FileStream(
                stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: CopyBufferSize,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            await CopyExactlyAsync(source, destination, entry.Length, cancellationToken).ConfigureAwait(false);
            return stagedPath;
        }
        catch
        {
            TryDeleteFile(stagedPath);
            TryDeleteEmptyDirectory(taskDirectory);
            throw;
        }
    }

    private static async Task EvaluateSourceAsync(
        KnowledgeBase knowledgeBase,
        FingerprintSource source,
        List<GameCompatibilityCandidate> candidates,
        List<KiriScopeDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        StaticBinaryAnalysisReport analysis;
        try
        {
            analysis = await StaticBinaryAnalyzer.AnalyzeFileAsync(source.FullPath, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            diagnostics.Add(new KiriScopeDiagnostic("KNOWLEDGE_AUTO_MATCH_INPUT_READ_FAILED", DiagnosticSeverity.Warning, exception.Message));
            return;
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new KiriScopeDiagnostic("KNOWLEDGE_AUTO_MATCH_INPUT_ACCESS_DENIED", DiagnosticSeverity.Warning, exception.Message));
            return;
        }

        foreach (var match in KnowledgeFingerprintMatcher.Match(knowledgeBase, analysis))
        {
            var scheme = knowledgeBase.Schemes.Single(scheme =>
                string.Equals(scheme.Id, match.SchemeId, StringComparison.Ordinal) &&
                string.Equals(scheme.Revision, match.SchemeRevision, StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(scheme.Fingerprint?.RequiredSha256))
            {
                diagnostics.Add(Warning("KNOWLEDGE_AUTO_MATCH_REQUIRES_SHA256", $"Scheme '{scheme.Id}@{scheme.Revision}' matched non-hash observations and was not selected automatically."));
                continue;
            }

            if (scheme.Status != KnowledgeCompatibilityStatus.Verified)
            {
                diagnostics.Add(Info("KNOWLEDGE_AUTO_MATCH_SCHEME_NOT_VERIFIED", $"Scheme '{scheme.Id}@{scheme.Revision}' is not verified and was not selected automatically."));
                continue;
            }

            var targets = knowledgeBase.Compatibility
                .Where(entry =>
                    entry.Status == KnowledgeCompatibilityStatus.Verified &&
                    string.Equals(entry.SchemeId, scheme.Id, StringComparison.Ordinal) &&
                    string.Equals(entry.SchemeRevision, scheme.Revision, StringComparison.Ordinal))
                .Select(static entry => $"{entry.TargetId}@{entry.TargetVersion}")
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static target => target, StringComparer.Ordinal)
                .ToArray();
            if (targets.Length == 0)
            {
                diagnostics.Add(Info("KNOWLEDGE_AUTO_MATCH_COMPATIBILITY_NOT_VERIFIED", $"Scheme '{scheme.Id}@{scheme.Revision}' has no verified compatibility entry and was not selected automatically."));
                continue;
            }

            BuiltInContentFilterScheme loaded;
            try
            {
                loaded = BuiltInContentFilterSchemeLoader.Load(scheme.SchemePath);
            }
            catch (ContentFilterException exception)
            {
                diagnostics.Add(Warning(exception.Code, $"Verified scheme '{scheme.Id}@{scheme.Revision}' could not be loaded: {exception.Message}"));
                continue;
            }

            candidates.Add(new GameCompatibilityCandidate(
                scheme.Id,
                scheme.Revision,
                scheme.DisplayName,
                scheme.Descriptor.AlgorithmId,
                scheme.Descriptor.AlgorithmVersion,
                match.FingerprintId,
                source.RelativePath,
                analysis.Input.Sha256,
                match.MatchedEvidence,
                targets,
                loaded.Filter));
        }
    }

    private static GameCompatibilityResolution CompleteResolution(
        IReadOnlyList<GameCompatibilityCandidate> candidates,
        IReadOnlyList<KiriScopeDiagnostic> diagnostics)
    {
        var uniqueCandidates = candidates
            .GroupBy(static candidate => candidate.SchemeId + "@" + candidate.SchemeRevision, StringComparer.Ordinal)
            .Select(static group => group.OrderBy(static candidate => candidate.InputPath, StringComparer.OrdinalIgnoreCase).First())
            .OrderBy(static candidate => candidate.SchemeId, StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.SchemeRevision, StringComparer.Ordinal)
            .ToArray();
        return uniqueCandidates.Length switch
        {
            0 => new GameCompatibilityResolution(GameCompatibilityResolutionKind.NoMatch, null, uniqueCandidates,
                [.. diagnostics, Info("KNOWLEDGE_AUTO_MATCH_NONE", "No unique, verified SHA-256 compatibility match was found.")]),
            1 => new GameCompatibilityResolution(GameCompatibilityResolutionKind.Selected, uniqueCandidates[0], uniqueCandidates,
                [.. diagnostics, Info("KNOWLEDGE_AUTO_MATCH_SELECTED", $"Selected verified scheme '{uniqueCandidates[0].SchemeId}@{uniqueCandidates[0].SchemeRevision}' by exact SHA-256 fingerprint.")]),
            _ => new GameCompatibilityResolution(GameCompatibilityResolutionKind.Ambiguous, null, uniqueCandidates,
                [.. diagnostics, Warning("KNOWLEDGE_AUTO_MATCH_AMBIGUOUS", "More than one verified SHA-256 compatibility scheme matched; none was selected automatically.")]),
        };
    }

    private static IReadOnlyList<FingerprintSource> DiscoverLocalFingerprintSources(GameInput input, GameInputDiscoveryResult discovery)
    {
        var sources = new List<FingerprintSource>();
        if (input.Kind == GameInputKind.Xp3Archive)
        {
            sources.Add(new FingerprintSource(Path.GetFileName(input.InputPath), input.InputPath));
        }
        else if (input.Kind == GameInputKind.GameDirectory)
        {
            sources.AddRange(discovery.Archives
                .Where(static archive => !archive.IsPackaged)
                .Select(static archive => new FingerprintSource(archive.RelativePath, archive.SourcePath)));
            sources.AddRange(discovery.Executables.Select(path => new FingerprintSource(path, Path.Combine(input.InputPath, path))));
            sources.AddRange(discovery.Plugins.Select(path => new FingerprintSource(path, Path.Combine(input.InputPath, path))));
        }

        return NormalizeSources(sources);
    }

    private static IReadOnlyList<FingerprintSource> DiscoverPackagedFingerprintSources(GameInputDiscoveryResult discovery) =>
        NormalizeSources(
        [
            .. discovery.Archives.Where(static archive => archive.IsPackaged).Select(static archive => new FingerprintSource(archive.RelativePath, archive.RelativePath)),
            .. discovery.Executables.Select(static path => new FingerprintSource(path, path)),
            .. discovery.Plugins.Select(static path => new FingerprintSource(path, path)),
        ]);

    private static IReadOnlyList<FingerprintSource> NormalizeSources(IEnumerable<FingerprintSource> sources) => sources
        .GroupBy(static source => source.RelativePath, StringComparer.OrdinalIgnoreCase)
        .Select(static group => group.First())
        .OrderBy(static source => source.RelativePath, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static bool TryValidatePackageEntry(ZipArchiveEntry entry, out string error)
    {
        var path = entry.FullName.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\\') >= 0 || Path.IsPathRooted(path))
        {
            error = "A package entry selected for compatibility fingerprinting has an empty, rooted, or backslash-separated path.";
            return false;
        }

        if (path.Split('/', StringSplitOptions.None).Any(static segment => string.IsNullOrEmpty(segment) || segment is "." or ".."))
        {
            error = "A package entry selected for compatibility fingerprinting contains an invalid path segment.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static async Task CopyExactlyAsync(Stream source, Stream destination, long expectedLength, CancellationToken cancellationToken)
    {
        var buffer = new byte[CopyBufferSize];
        long totalRead = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (read > expectedLength - totalRead)
            {
                throw new InvalidDataException("A package entry exceeds its declared uncompressed size.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            totalRead += read;
        }

        if (totalRead != expectedLength)
        {
            throw new InvalidDataException("A package entry ended before its declared uncompressed size.");
        }
    }

    private static void ValidateOptions(KnowledgeGameCompatibilityResolverOptions options)
    {
        if (options.MaximumPackageFingerprintSources <= 0 || options.MaximumPackageFingerprintSourceBytes <= 0 ||
            options.MaximumPackageFingerprintBytes <= 0 || options.MaximumPackageFingerprintSourceBytes > options.MaximumPackageFingerprintBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Compatibility fingerprinting limits must be positive and the per-source limit cannot exceed the total limit.");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Cleanup failure must not change a completed extraction result.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup failure must not change a completed extraction result.
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: false);
            }
        }
        catch (IOException)
        {
            // Cleanup failure must not change a completed extraction result.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup failure must not change a completed extraction result.
        }
    }

    private static KiriScopeDiagnostic Info(string code, string message) => new(code, DiagnosticSeverity.Info, message);

    private static KiriScopeDiagnostic Warning(string code, string message) => new(code, DiagnosticSeverity.Warning, message);

    private sealed record FingerprintSource(string RelativePath, string FullPath);
}
