using System.IO.Compression;
using KiriScope.Core.Diagnostics;
using KiriScope.Core.Evidence;
using KiriScope.IO.Paths;
using KiriScope.Resources;

namespace KiriScope.Xp3;

/// <summary>
/// Runs the ordinary extraction flow for a game directory, one XP3 archive, or a complete ZIP game package.
/// Package contents are treated as a read-only virtual directory and only one required XP3 is staged at a time.
/// </summary>
public static class GameExtractionService
{
    private const int CopyBufferSize = 128 * 1024;

    public static Task<GameInputDiscoveryResult> DiscoverAsync(
        GameInput input,
        GameExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        options ??= new GameExtractionOptions();
        ValidateOptions(options);

        return input.Kind switch
        {
            GameInputKind.GameDirectory => Task.FromResult(DiscoverDirectory(input, options, cancellationToken)),
            GameInputKind.Xp3Archive => Task.FromResult(DiscoverSingleArchive(input)),
            GameInputKind.GamePackage => Task.FromResult(DiscoverZipPackage(input, options, cancellationToken)),
            _ => throw new ArgumentOutOfRangeException(nameof(input), "The game input kind is not supported."),
        };
    }

    public static async Task<ExtractionTaskResult> ExtractAsync(
        GameInput input,
        ResourceCategory category,
        string outputDirectory,
        GameExtractionOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        options ??= new GameExtractionOptions();
        ValidateOptions(options);

        var discovery = await DiscoverAsync(input, options, cancellationToken).ConfigureAwait(false);
        var fullOutputDirectory = ValidateOutputDirectory(input, outputDirectory);
        var compatibility = discovery.HasErrors
            ? GameCompatibilityResolution.NotConfigured
            : await ResolveCompatibilityAsync(input, discovery, options.CompatibilityResolver, cancellationToken).ConfigureAwait(false);
        if (!discovery.HasErrors &&
            compatibility.Kind is GameCompatibilityResolutionKind.NotConfigured or GameCompatibilityResolutionKind.NoMatch)
        {
            var staticCompatibility = await ProbeStaticContentFiltersAsync(input, discovery, options, cancellationToken).ConfigureAwait(false);
            if (staticCompatibility is not null)
            {
                compatibility = staticCompatibility with
                {
                    Diagnostics = [.. compatibility.Diagnostics, .. staticCompatibility.Diagnostics],
                };
            }
        }
        var effectiveEntryOptions = compatibility.Selected?.ContentFilter is { } contentFilter
            ? (options.EntryExtractionOptions ?? new Xp3EntryExtractionOptions()) with
            {
                ContentFilter = contentFilter,
                VerifyAdler32AfterFilter = compatibility.Selected.FingerprintId == "static-adler32-proof" || options.EntryExtractionOptions?.VerifyAdler32AfterFilter == true,
                FallbackToVerifiedUnfilteredMarkedEntry = compatibility.Selected.FingerprintId == "static-adler32-proof" || options.EntryExtractionOptions?.FallbackToVerifiedUnfilteredMarkedEntry == true,
            }
            : options.EntryExtractionOptions;
        if (options.ProbeMarkedEntriesWithoutFilter &&
            (compatibility.Kind is GameCompatibilityResolutionKind.NotConfigured or GameCompatibilityResolutionKind.NoMatch) &&
            effectiveEntryOptions?.ContentFilter is null)
        {
            effectiveEntryOptions = (effectiveEntryOptions ?? new Xp3EntryExtractionOptions()) with
            {
                AllowUnfilteredMarkedEntries = true,
                VerifyAdler32 = true,
            };
        }
        if (discovery.HasErrors || discovery.Archives.Count == 0)
        {
            IReadOnlyList<KiriScopeDiagnostic> diagnostics = discovery.Archives.Count == 0 && !discovery.HasErrors
                ? [.. discovery.Diagnostics, .. compatibility.Diagnostics, Error("GAME_XP3_NOT_FOUND", "No XP3 archives were found in the selected game input.")]
                : [.. discovery.Diagnostics, .. compatibility.Diagnostics];
            return new ExtractionTaskResult(input, category, compatibility, fullOutputDirectory, false, Array.Empty<GameArchiveExtractionResult>(), diagnostics);
        }

        if ((compatibility.Kind is GameCompatibilityResolutionKind.NotConfigured or GameCompatibilityResolutionKind.NoMatch) &&
            options.RuntimeExtractionFallback is { } runtimeFallback)
        {
            progress?.Report("Checking verified runtime extraction prerequisites");
            var runtimeResult = await runtimeFallback.TryExtractAsync(
                input,
                category,
                fullOutputDirectory,
                discovery,
                compatibility,
                progress,
                cancellationToken).ConfigureAwait(false);
            if (runtimeResult is not null)
            {
                return runtimeResult;
            }
        }

        Directory.CreateDirectory(fullOutputDirectory);
        var results = new List<GameArchiveExtractionResult>(discovery.Archives.Count);
        try
        {
            foreach (var archive in discovery.Archives)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"Reading {archive.RelativePath}");
                results.Add(await ExtractArchiveAsync(input, archive, category, fullOutputDirectory, options, effectiveEntryOptions, progress, cancellationToken).ConfigureAwait(false));
            }
        }
        catch
        {
            TryDeleteEmptyDirectory(fullOutputDirectory);
            throw;
        }

        return new ExtractionTaskResult(input, category, compatibility, fullOutputDirectory, true, results, [.. discovery.Diagnostics, .. compatibility.Diagnostics]);
    }

    public static bool MatchesCategory(string entryName, ResourceCategory category) =>
        category == ResourceCategory.All || Classify(entryName) == category;

    public static ResourceCategory Classify(string entryName)
    {
        var extension = Path.GetExtension(entryName);
        return extension.ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tlg" or ".tlg5" or ".tlg6" or ".psb" or ".pimg" or ".mdf" or ".mzs" => ResourceCategory.Images,
            ".ogg" or ".wav" or ".mp3" or ".amv" or ".mpg" => ResourceCategory.Audio,
            ".tjs" or ".ks" or ".kag" or ".scn" or ".txt" => ResourceCategory.Scripts,
            _ => ResourceCategory.Other,
        };
    }

    private static GameInputDiscoveryResult DiscoverDirectory(GameInput input, GameExtractionOptions options, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(input.InputPath))
        {
            return FailedDiscovery(input, "GAME_DIRECTORY_NOT_FOUND", "The selected game directory does not exist.");
        }

        var archives = new List<DiscoveredGameArchive>();
        var executables = new List<string>();
        var plugins = new List<string>();
        var diagnostics = new List<KiriScopeDiagnostic>();
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };

        foreach (var path in Directory.EnumerateFiles(input.InputPath, "*", enumerationOptions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(input.InputPath, path);
            var extension = Path.GetExtension(path);
            if (extension.Equals(".xp3", StringComparison.OrdinalIgnoreCase))
            {
                if (archives.Count == options.MaximumDiscoveredArchiveCount)
                {
                    diagnostics.Add(Error("GAME_XP3_LIMIT_EXCEEDED", "The configured XP3 archive discovery limit was reached."));
                    break;
                }

                archives.Add(new DiscoveredGameArchive(Path.GetFullPath(path), relativePath, false));
            }
            else if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                executables.Add(relativePath);
            }
            else if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) || extension.Equals(".tpm", StringComparison.OrdinalIgnoreCase))
            {
                plugins.Add(relativePath);
            }
        }

        archives.Sort(static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));
        executables.Sort(StringComparer.OrdinalIgnoreCase);
        plugins.Sort(StringComparer.OrdinalIgnoreCase);
        return new GameInputDiscoveryResult(input, archives, executables, plugins, diagnostics);
    }

    private static GameInputDiscoveryResult DiscoverSingleArchive(GameInput input)
    {
        if (!File.Exists(input.InputPath))
        {
            return FailedDiscovery(input, "GAME_XP3_NOT_FOUND", "The selected XP3 archive does not exist.");
        }

        return new GameInputDiscoveryResult(
            input,
            [new DiscoveredGameArchive(input.InputPath, Path.GetFileName(input.InputPath), false)],
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<KiriScopeDiagnostic>());
    }

    private static GameInputDiscoveryResult DiscoverZipPackage(GameInput input, GameExtractionOptions options, CancellationToken cancellationToken)
    {
        if (!File.Exists(input.InputPath))
        {
            return FailedDiscovery(input, "GAME_PACKAGE_NOT_FOUND", "The selected complete game package does not exist.");
        }

        try
        {
            using var package = ZipFile.OpenRead(input.InputPath);
            var archives = new List<DiscoveredGameArchive>();
            var executables = new List<string>();
            var plugins = new List<string>();
            var diagnostics = new List<KiriScopeDiagnostic>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long totalUnpackedBytes = 0;
            var entryCount = 0;

            foreach (var entry in package.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryValidatePackageEntry(entry, out var validationError))
                {
                    return FailedDiscovery(input, "GAME_PACKAGE_ENTRY_PATH_REJECTED", validationError);
                }

                if (!seenPaths.Add(entry.FullName.TrimEnd('/')))
                {
                    return FailedDiscovery(input, "GAME_PACKAGE_DUPLICATE_PATH", "The complete game package contains duplicate or case-conflicting paths.");
                }

                if (IsDirectoryEntry(entry))
                {
                    continue;
                }

                entryCount++;
                if (entryCount > options.MaximumPackageEntryCount)
                {
                    return FailedDiscovery(input, "GAME_PACKAGE_ENTRY_LIMIT_EXCEEDED", "The complete game package exceeds the configured entry limit.");
                }

                if (entry.Length > options.MaximumPackageEntryUnpackedBytes)
                {
                    return FailedDiscovery(input, "GAME_PACKAGE_ENTRY_SIZE_LIMIT_EXCEEDED", "A package entry exceeds the configured unpacked-size limit.");
                }

                if (entry.Length > long.MaxValue - totalUnpackedBytes || (totalUnpackedBytes += entry.Length) > options.MaximumPackageUnpackedBytes)
                {
                    return FailedDiscovery(input, "GAME_PACKAGE_TOTAL_SIZE_LIMIT_EXCEEDED", "The complete game package exceeds the configured total unpacked-size limit.");
                }

                var extension = Path.GetExtension(entry.FullName);
                if (extension.Equals(".xp3", StringComparison.OrdinalIgnoreCase))
                {
                    if (archives.Count == options.MaximumDiscoveredArchiveCount)
                    {
                        return FailedDiscovery(input, "GAME_XP3_LIMIT_EXCEEDED", "The configured XP3 archive discovery limit was reached.");
                    }

                    archives.Add(new DiscoveredGameArchive(entry.FullName, entry.FullName, true));
                }
                else if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    executables.Add(entry.FullName);
                }
                else if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) || extension.Equals(".tpm", StringComparison.OrdinalIgnoreCase))
                {
                    plugins.Add(entry.FullName);
                }
            }

            archives.Sort(static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));
            executables.Sort(StringComparer.OrdinalIgnoreCase);
            plugins.Sort(StringComparer.OrdinalIgnoreCase);
            return new GameInputDiscoveryResult(input, archives, executables, plugins, diagnostics);
        }
        catch (InvalidDataException exception)
        {
            return FailedDiscovery(input, "GAME_PACKAGE_INVALID_OR_PROTECTED", $"The complete game package cannot be read safely: {exception.Message}");
        }
        catch (IOException exception)
        {
            return FailedDiscovery(input, "GAME_PACKAGE_READ_FAILED", exception.Message);
        }
    }

    private static async Task<GameArchiveExtractionResult> ExtractArchiveAsync(
        GameInput input,
        DiscoveredGameArchive archive,
        ResourceCategory category,
        string outputRoot,
        GameExtractionOptions options,
        Xp3EntryExtractionOptions? entryExtractionOptions,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        string? temporaryPath = null;
        try
        {
            var archivePath = archive.SourcePath;
            if (archive.IsPackaged)
            {
                temporaryPath = await StagePackageArchiveAsync(input.InputPath, archive.SourcePath, options, cancellationToken).ConfigureAwait(false);
                archivePath = temporaryPath;
            }

            await using var inputStream = new FileStream(
                archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: CopyBufferSize,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            var index = await Xp3ArchiveReader.ReadIndexAsync(inputStream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (index.Stage < EvidenceStage.IndexParsed)
            {
                return new GameArchiveExtractionResult(archive.RelativePath, archive.IsPackaged, false, index.Entries.Count, 0, 0, 0, Array.Empty<Xp3EntryExtractionResult>(), Array.Empty<GameExtractedResourceValidation>(), index.Diagnostics);
            }

            var protectedNoticeCount = index.Entries.Count(static entry => IsProtectedArchiveNotice(entry.Name));
            var selectedEntries = index.Entries
                .Where(entry => !IsProtectedArchiveNotice(entry.Name) && MatchesCategory(entry.Name, category))
                .ToArray();
            var archiveOutputPath = Path.ChangeExtension(archive.RelativePath, null) ?? archive.RelativePath;
            var outputRelativePaths = Xp3EntryExtractor.PlanOutputRelativePaths(selectedEntries)
                .Select(path => Path.Combine(archiveOutputPath, path))
                .ToArray();
            var results = new List<Xp3EntryExtractionResult>(selectedEntries.Length);
            for (var entryIndex = 0; entryIndex < selectedEntries.Length; entryIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = selectedEntries[entryIndex];
                progress?.Report($"Extracting {archive.RelativePath}: {entry.Name}");
                try
                {
                    var result = await Xp3EntryExtractor.ExtractToFileAsync(
                        inputStream,
                        entry,
                        outputRoot,
                        outputRelativePaths[entryIndex],
                        entryExtractionOptions,
                        cancellationToken).ConfigureAwait(false);
                    results.Add(WithOutputPathDiagnostic(result, Path.Combine(archiveOutputPath, entry.Name), outputRelativePaths[entryIndex]));
                }
                catch (ArgumentException exception)
                {
                    results.Add(FailedEntry(entry, "GAME_OUTPUT_PATH_REJECTED", exception.Message));
                }
            }

            var validations = await ValidateExtractedResourcesAsync(
                results,
                outputRelativePaths,
                outputRoot,
                options,
                cancellationToken).ConfigureAwait(false);
            var resourceValidations = AssignDetectedFileExtensions(validations, outputRoot);
            return new GameArchiveExtractionResult(
                archive.RelativePath,
                archive.IsPackaged,
                true,
                index.Entries.Count,
                selectedEntries.Length,
                results.Count(static result => result.Succeeded),
                results.Count(static result => !result.Succeeded),
                results,
                resourceValidations,
                protectedNoticeCount == 0
                    ? index.Diagnostics
                    : [.. index.Diagnostics, Info("GAME_PROTECTED_ARCHIVE_NOTICE_SKIPPED", $"Skipped {protectedNoticeCount:N0} protected-archive notice entr{(protectedNoticeCount == 1 ? "y" : "ies")}; it does not describe a game resource.")]);
        }
        catch (InvalidDataException exception)
        {
            return FailedArchive(archive.RelativePath, archive.IsPackaged, "GAME_ARCHIVE_READ_FAILED", exception.Message);
        }
        catch (IOException exception)
        {
            return FailedArchive(archive.RelativePath, archive.IsPackaged, "GAME_ARCHIVE_READ_FAILED", exception.Message);
        }
        finally
        {
            if (temporaryPath is not null)
            {
                TryDeleteFile(temporaryPath);
                TryDeleteEmptyDirectory(Path.GetDirectoryName(temporaryPath)!);
            }
        }
    }

    private static async Task<string> StagePackageArchiveAsync(
        string packagePath,
        string entryName,
        GameExtractionOptions options,
        CancellationToken cancellationToken)
    {
        using var package = ZipFile.OpenRead(packagePath);
        var entry = package.GetEntry(entryName) ?? throw new InvalidDataException("The selected XP3 entry is no longer present in the complete game package.");
        if (!TryValidatePackageEntry(entry, out var validationError))
        {
            throw new InvalidDataException(validationError);
        }

        if (entry.Length > options.MaximumPackageEntryUnpackedBytes)
        {
            throw new InvalidDataException("The package XP3 entry exceeds the configured unpacked-size limit.");
        }

        var temporaryRoot = options.TemporaryRootDirectory is null
            ? Path.Combine(Path.GetTempPath(), "KiriScope")
            : Path.GetFullPath(options.TemporaryRootDirectory);
        var taskDirectory = Path.Combine(temporaryRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(taskDirectory);
        var temporaryPath = Path.Combine(taskDirectory, "archive.xp3");
        try
        {
            await using var source = entry.Open();
            await using var destination = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: CopyBufferSize,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            await CopyExactlyAsync(source, destination, entry.Length, cancellationToken).ConfigureAwait(false);
            return temporaryPath;
        }
        catch
        {
            TryDeleteFile(temporaryPath);
            TryDeleteEmptyDirectory(taskDirectory);
            throw;
        }
    }

    private static async Task<GameCompatibilityResolution> ResolveCompatibilityAsync(
        GameInput input,
        GameInputDiscoveryResult discovery,
        IGameCompatibilityResolver? resolver,
        CancellationToken cancellationToken)
    {
        if (resolver is null)
        {
            return GameCompatibilityResolution.NotConfigured;
        }

        try
        {
            return await resolver.ResolveAsync(input, discovery, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            return new GameCompatibilityResolution(
                GameCompatibilityResolutionKind.Unavailable,
                null,
                Array.Empty<GameCompatibilityCandidate>(),
                [new KiriScopeDiagnostic("GAME_COMPATIBILITY_RESOLUTION_FAILED", DiagnosticSeverity.Warning, exception.Message)]);
        }
    }

    private static async Task<GameCompatibilityResolution?> ProbeStaticContentFiltersAsync(
        GameInput input,
        GameInputDiscoveryResult discovery,
        GameExtractionOptions options,
        CancellationToken cancellationToken)
    {
        var candidates = options.StaticContentFilterCandidates;
        if (candidates is null || candidates.Count == 0)
        {
            return null;
        }

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var proofs = new List<string>(candidate.RequiredAdler32ProofCount);
            foreach (var archive in discovery.Archives)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? temporaryPath = null;
                try
                {
                    var archivePath = archive.SourcePath;
                    if (archive.IsPackaged)
                    {
                        temporaryPath = await StagePackageArchiveAsync(input.InputPath, archive.SourcePath, options, cancellationToken).ConfigureAwait(false);
                        archivePath = temporaryPath;
                    }

                    await using var archiveStream = new FileStream(
                        archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                        bufferSize: CopyBufferSize,
                        options: FileOptions.Asynchronous | FileOptions.SequentialScan);
                    var index = await Xp3ArchiveReader.ReadIndexAsync(archiveStream, cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (index.Stage < EvidenceStage.IndexParsed)
                    {
                        continue;
                    }

                    var attempted = 0;
                    foreach (var entry in index.Entries.Where(static entry => entry.IsMarkedEncrypted && entry.Adler32 is not null && entry.UnpackedSize >= 0))
                    {
                        if (attempted++ >= candidate.MaximumProbeEntriesPerArchive || entry.UnpackedSize > candidate.MaximumProbeEntryBytes)
                        {
                            continue;
                        }

                        archiveStream.Position = 0;
                        var verification = await Xp3EntryExtractor.ExtractAsync(
                            archiveStream,
                            entry,
                            Stream.Null,
                            new Xp3EntryExtractionOptions
                            {
                                ContentFilter = candidate.ContentFilter,
                                VerifyAdler32 = true,
                                VerifyAdler32AfterFilter = true,
                            },
                            cancellationToken).ConfigureAwait(false);
                        if (!verification.Succeeded || verification.ActualAdler32 != entry.Adler32)
                        {
                            continue;
                        }

                        proofs.Add($"{archive.RelativePath}:{entry.Name}:adler32={entry.Adler32:X8}");
                        if (proofs.Count < candidate.RequiredAdler32ProofCount)
                        {
                            continue;
                        }

                        var selected = new GameCompatibilityCandidate(
                            candidate.SchemeId,
                            candidate.SchemeRevision,
                            candidate.DisplayName,
                            candidate.ContentFilter.Descriptor.Id,
                            candidate.ContentFilter.Descriptor.Version,
                            "static-adler32-proof",
                            archive.RelativePath,
                            "not-computed; per-entry Adler-32 proof",
                            proofs,
                            ["The current input"],
                            candidate.ContentFilter);
                        return new GameCompatibilityResolution(
                            GameCompatibilityResolutionKind.Selected,
                            selected,
                            [selected],
                            [Info(
                                "STATIC_FILTER_PROFILE_SELECTED",
                                $"Selected static profile '{candidate.SchemeId}@{candidate.SchemeRevision}' after {proofs.Count:N0} independently Adler-32-verified encrypted XP3 entries. Source: {candidate.SourceReference}")]);
                    }
                }
                catch (InvalidDataException)
                {
                    // A malformed or unsupported archive is handled by the ordinary extraction report.
                }
                catch (IOException)
                {
                    // A transiently unreadable archive is handled by the ordinary extraction report.
                }
                finally
                {
                    if (temporaryPath is not null)
                    {
                        TryDeleteFile(temporaryPath);
                        TryDeleteEmptyDirectory(Path.GetDirectoryName(temporaryPath)!);
                    }
                }
            }
        }

        return new GameCompatibilityResolution(
            GameCompatibilityResolutionKind.NoMatch,
            null,
            Array.Empty<GameCompatibilityCandidate>(),
            [Info("STATIC_FILTER_PROFILE_NO_MATCH", "No bundled static filter profile produced the required Adler-32 proofs for this input.")]);
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
                throw new InvalidDataException("The package entry exceeds its declared uncompressed size.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            totalRead += read;
        }

        if (totalRead != expectedLength)
        {
            throw new InvalidDataException("The package entry ended before its declared uncompressed size.");
        }
    }

    private static string ValidateOutputDirectory(GameInput input, string outputDirectory)
    {
        var fullOutputDirectory = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(fullOutputDirectory) || File.Exists(fullOutputDirectory))
        {
            throw new ArgumentException("The output directory must not already exist.", nameof(outputDirectory));
        }

        if (input.Kind == GameInputKind.GameDirectory && IsPathContainedBy(input.InputPath, fullOutputDirectory))
        {
            throw new ArgumentException("The output directory must be outside the selected game directory.", nameof(outputDirectory));
        }

        return fullOutputDirectory;
    }

    private static bool TryValidatePackageEntry(ZipArchiveEntry entry, out string error)
    {
        error = string.Empty;
        var path = entry.FullName.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\\') >= 0 || Path.IsPathRooted(path))
        {
            error = "A complete game package entry has an empty, rooted, or backslash-separated path.";
            return false;
        }

        var segments = path.Split('/', StringSplitOptions.None);
        if (segments.Length == 0 || segments.Any(static segment => string.IsNullOrEmpty(segment) || segment is "." or ".."))
        {
            error = "A complete game package entry path contains an empty, current-directory, or parent-directory segment.";
            return false;
        }

        return true;
    }

    private static bool IsDirectoryEntry(ZipArchiveEntry entry) => entry.FullName.EndsWith("/", StringComparison.Ordinal);

    private static GameInputDiscoveryResult FailedDiscovery(GameInput input, string code, string message) =>
        new(input, Array.Empty<DiscoveredGameArchive>(), Array.Empty<string>(), Array.Empty<string>(), [Error(code, message)]);

    private static GameArchiveExtractionResult FailedArchive(string sourcePath, bool wasTemporarilyStaged, string code, string message) =>
        new(sourcePath, wasTemporarilyStaged, false, 0, 0, 0, 0, Array.Empty<Xp3EntryExtractionResult>(), Array.Empty<GameExtractedResourceValidation>(), [Error(code, message)]);

    private static Xp3EntryExtractionResult FailedEntry(Xp3Entry entry, string code, string message) =>
        new(entry.Name, EvidenceStage.EntryLocated, false, 0, entry.Adler32, null, null, [Error(code, message)]);

    private static KiriScopeDiagnostic Error(string code, string message) => new(code, DiagnosticSeverity.Error, message);

    private static KiriScopeDiagnostic Warning(string code, string message) => new(code, DiagnosticSeverity.Warning, message);

    private static KiriScopeDiagnostic Info(string code, string message) => new(code, DiagnosticSeverity.Info, message);

    private static async Task<IReadOnlyList<GameExtractedResourceValidation>> ValidateExtractedResourcesAsync(
        IReadOnlyList<Xp3EntryExtractionResult> entryResults,
        IReadOnlyList<string> outputRelativePaths,
        string outputRoot,
        GameExtractionOptions options,
        CancellationToken cancellationToken)
    {
        if (!options.ValidateExtractedResources)
        {
            return Array.Empty<GameExtractedResourceValidation>();
        }

        var validations = new List<GameExtractedResourceValidation>();
        for (var entryIndex = 0; entryIndex < entryResults.Count; entryIndex++)
        {
            var entry = entryResults[entryIndex];
            if (!entry.Succeeded)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var outputRelativePath = outputRelativePaths[entryIndex];
            var outputPath = SafeOutputPath.Resolve(outputRoot, outputRelativePath);
            var pathCategory = Classify(entry.EntryName);
            try
            {
                var info = new FileInfo(outputPath);
                if (!info.Exists)
                {
                    validations.Add(new GameExtractedResourceValidation(
                        entry.EntryName, outputRelativePath, pathCategory, ResourceFormat.Unknown, null,
                        entry.Stage, false, false,
                        [Warning("GAME_RESOURCE_OUTPUT_NOT_FOUND", "The extracted output file was not available for validation.")]));
                    continue;
                }

                if (info.Length > options.MaximumResourceValidationBytes)
                {
                    validations.Add(new GameExtractedResourceValidation(
                        entry.EntryName, outputRelativePath, pathCategory, ResourceFormat.Unknown, null,
                        entry.Stage, false, false,
                        [Info("GAME_RESOURCE_VALIDATION_SIZE_LIMIT", $"Structural validation was skipped because the extracted file exceeds the {options.MaximumResourceValidationBytes:N0}-byte limit.")]));
                    continue;
                }

                await using var content = new FileStream(
                    outputPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: CopyBufferSize,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan);
                var header = new byte[32];
                var headerLength = await content.ReadAsync(header, cancellationToken).ConfigureAwait(false);
                var detectedFormat = ResourceFormatDetector.Detect(header.AsSpan(0, headerLength));
                content.Position = 0;
                if (detectedFormat == ResourceFormat.Unknown && !HasSupportedResourceExtension(entry.EntryName))
                {
                    validations.Add(new GameExtractedResourceValidation(
                        entry.EntryName, outputRelativePath, pathCategory, detectedFormat, null,
                        entry.Stage, false, false,
                        [Info("GAME_RESOURCE_VALIDATION_NOT_AVAILABLE", "No structural validator is available for this extracted resource type.")]));
                    continue;
                }

                var score = await ResourceFormatScorer.ScoreAsync(content, cancellationToken).ConfigureAwait(false);
                var detectedCategory = GetCategoryForFormat(score.Format);
                var diagnostics = new List<KiriScopeDiagnostic>(score.Diagnostics);
                if (detectedCategory is not null &&
                    pathCategory is not ResourceCategory.Other &&
                    detectedCategory != pathCategory)
                {
                    diagnostics.Add(Warning(
                        "GAME_RESOURCE_CATEGORY_MISMATCH",
                        $"The extracted content was identified as {score.Format}, which does not match the path-based {pathCategory} category."));
                }

                validations.Add(new GameExtractedResourceValidation(
                    entry.EntryName,
                    outputRelativePath,
                    pathCategory,
                    score.Format,
                    detectedCategory,
                    score.Stage,
                    true,
                    score.IsAccepted,
                    diagnostics));
            }
            catch (IOException exception)
            {
                validations.Add(new GameExtractedResourceValidation(
                    entry.EntryName, outputRelativePath, pathCategory, ResourceFormat.Unknown, null,
                    entry.Stage, true, false,
                    [Warning("GAME_RESOURCE_VALIDATION_READ_FAILED", exception.Message)]));
            }
            catch (UnauthorizedAccessException exception)
            {
                validations.Add(new GameExtractedResourceValidation(
                    entry.EntryName, outputRelativePath, pathCategory, ResourceFormat.Unknown, null,
                    entry.Stage, true, false,
                    [Warning("GAME_RESOURCE_VALIDATION_READ_FAILED", exception.Message)]));
            }
            catch (InvalidDataException exception)
            {
                validations.Add(new GameExtractedResourceValidation(
                    entry.EntryName, outputRelativePath, pathCategory, ResourceFormat.Unknown, null,
                    entry.Stage, true, false,
                    [Warning("GAME_RESOURCE_VALIDATION_FAILED", exception.Message)]));
            }
        }

        return validations;
    }

    /// <summary>
    /// Makes protected-index exports usable in Explorer without inventing an original path. When an
    /// archive has only an opaque name but its decoded bytes identify a known format, retain that
    /// opaque base name and append the signature-derived extension.
    /// </summary>
    private static IReadOnlyList<GameExtractedResourceValidation> AssignDetectedFileExtensions(
        IReadOnlyList<GameExtractedResourceValidation> validations,
        string outputRoot)
    {
        var normalized = new List<GameExtractedResourceValidation>(validations.Count);
        foreach (var validation in validations)
        {
            if (Path.HasExtension(validation.OutputRelativePath) ||
                GetSuggestedExtension(validation.DetectedFormat) is not { } extension)
            {
                normalized.Add(validation);
                continue;
            }

            var sourcePath = SafeOutputPath.Resolve(outputRoot, validation.OutputRelativePath);
            var renamedRelativePath = Path.Combine(
                Path.GetDirectoryName(validation.OutputRelativePath) ?? string.Empty,
                Path.ChangeExtension(Path.GetFileName(validation.OutputRelativePath), extension));
            var destinationPath = SafeOutputPath.Resolve(outputRoot, renamedRelativePath);
            try
            {
                if (!File.Exists(sourcePath) || File.Exists(destinationPath))
                {
                    normalized.Add(validation with
                    {
                        Diagnostics =
                        [
                            .. validation.Diagnostics,
                            Warning("GAME_RESOURCE_EXTENSION_NOT_ASSIGNED", "A signature-derived extension could not be assigned without replacing an existing file."),
                        ],
                    });
                    continue;
                }

                File.Move(sourcePath, destinationPath, overwrite: false);
                normalized.Add(validation with
                {
                    OutputRelativePath = renamedRelativePath,
                    PathCategory = Classify(renamedRelativePath),
                    Diagnostics =
                    [
                        .. validation.Diagnostics,
                        Info("GAME_RESOURCE_EXTENSION_ASSIGNED", $"The opaque exported name was assigned the '{extension}' extension from its verified content signature."),
                    ],
                });
            }
            catch (IOException exception)
            {
                normalized.Add(validation with
                {
                    Diagnostics =
                    [
                        .. validation.Diagnostics,
                        Warning("GAME_RESOURCE_EXTENSION_NOT_ASSIGNED", exception.Message),
                    ],
                });
            }
            catch (UnauthorizedAccessException exception)
            {
                normalized.Add(validation with
                {
                    Diagnostics =
                    [
                        .. validation.Diagnostics,
                        Warning("GAME_RESOURCE_EXTENSION_NOT_ASSIGNED", exception.Message),
                    ],
                });
            }
        }

        return normalized;
    }

    private static bool HasSupportedResourceExtension(string entryName) => Path.GetExtension(entryName).ToLowerInvariant() is
        ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tlg" or ".tlg5" or ".tlg6" or ".psb" or ".pimg" or ".ogg" or ".wav";

    private static string? GetSuggestedExtension(ResourceFormat format) => format switch
    {
        ResourceFormat.Png => ".png",
        ResourceFormat.Tlg => ".tlg",
        ResourceFormat.Psb => ".psb",
        ResourceFormat.Pimg => ".pimg",
        ResourceFormat.Ogg => ".ogg",
        ResourceFormat.Wave => ".wav",
        ResourceFormat.Jpeg => ".jpg",
        ResourceFormat.Bmp => ".bmp",
        _ => null,
    };

    private static bool IsProtectedArchiveNotice(string entryName) =>
        entryName.StartsWith("$$$ This is a protected archive. $$$", StringComparison.Ordinal);

    private static Xp3EntryExtractionResult WithOutputPathDiagnostic(
        Xp3EntryExtractionResult result,
        string expectedOutputRelativePath,
        string outputRelativePath)
    {
        if (!result.Succeeded || string.Equals(expectedOutputRelativePath, outputRelativePath, StringComparison.Ordinal))
        {
            return result;
        }

        return result with
        {
            Diagnostics =
            [
                .. result.Diagnostics,
                Info("GAME_OUTPUT_NAME_COLLISION_DISAMBIGUATED", $"A duplicate XP3 path was exported as '{outputRelativePath}' without replacing the earlier entry."),
            ],
        };
    }

    private static ResourceCategory? GetCategoryForFormat(ResourceFormat format) => format switch
    {
        ResourceFormat.Png or ResourceFormat.Jpeg or ResourceFormat.Bmp or ResourceFormat.Tlg or ResourceFormat.Psb or ResourceFormat.Pimg => ResourceCategory.Images,
        ResourceFormat.Ogg or ResourceFormat.Wave => ResourceCategory.Audio,
        _ => null,
    };

    private static bool IsPathContainedBy(string rootDirectory, string candidatePath)
    {
        var root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateOptions(GameExtractionOptions options)
    {
        if (options.MaximumDiscoveredArchiveCount <= 0 || options.MaximumPackageEntryCount <= 0 ||
            options.MaximumPackageEntryUnpackedBytes <= 0 || options.MaximumPackageUnpackedBytes <= 0 ||
            options.MaximumPackageEntryUnpackedBytes > options.MaximumPackageUnpackedBytes ||
            options.MaximumResourceValidationBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Game extraction limits must be positive and the per-entry package limit cannot exceed the total limit.");
        }

        foreach (var candidate in options.StaticContentFilterCandidates ?? Array.Empty<StaticContentFilterCandidate>())
        {
            if (candidate is null || string.IsNullOrWhiteSpace(candidate.SchemeId) ||
                string.IsNullOrWhiteSpace(candidate.SchemeRevision) || string.IsNullOrWhiteSpace(candidate.DisplayName) ||
                string.IsNullOrWhiteSpace(candidate.SourceReference) || candidate.ContentFilter is null ||
                candidate.RequiredAdler32ProofCount <= 0 || candidate.MaximumProbeEntriesPerArchive <= 0 ||
                candidate.MaximumProbeEntryBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "Static content-filter candidates must be complete and use positive probe limits.");
            }
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
            // A failed cleanup is deliberately non-fatal; the extraction report remains valid.
        }
        catch (UnauthorizedAccessException)
        {
            // A failed cleanup is deliberately non-fatal; the extraction report remains valid.
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
            // A failed cleanup is deliberately non-fatal; the extraction report remains valid.
        }
        catch (UnauthorizedAccessException)
        {
            // A failed cleanup is deliberately non-fatal; the extraction report remains valid.
        }
    }
}
