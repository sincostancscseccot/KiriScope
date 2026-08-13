using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KiriScope.Core.Diagnostics;
using KiriScope.Core.Evidence;
using KiriScope.IO.Paths;
using KiriScope.Resources;
using KiriScope.Xp3;

namespace KiriScope.Gui;

/// <summary>
/// Uses the game's own KiriKiri storage runtime to enumerate and capture decoded XP3 resources.
/// The original game is never changed: a complete isolated copy is staged and its XP3 files are
/// hard-linked where Windows permits it. This route intentionally requires an x86 KiriKiri game.
/// </summary>
internal sealed class KirikiriRuntimeExtractionFallback : IGameRuntimeExtractionFallback
{
    private const int BufferSize = 128 * 1024;
    private const string ArchiveRequestFileName = "extract-archive-list.txt";
    private const string DirectRequestFileName = "extract-requested-files.txt";
    private const string CategoryFileName = "extract-resource-category.txt";
    private const char ArchiveEntryListSeparator = '\t';
    private const string CompletionFileName = "capture-complete.txt";
    private const string ManifestFileName = "capture-manifest.txt";
    // Intended for maintainers diagnosing an unsupported title. It is never
    // enabled by the UI: when explicitly set to "1", keep the isolated copy
    // so the helper manifest and diagnostics can be inspected after failure.
    private const string KeepStageEnvironmentVariable = "KIRISCOPE_KEEP_RUNTIME_CAPTURE_STAGE";
    // Maintainer-only switch for titles that use a non-standard KiriKiri
    // storage layer. It runs the title briefly without enumerating raw XP3
    // streams and retains the helper's control-flow trace when requested.
    private const string DiagnosticsOnlyEnvironmentVariable = "KIRISCOPE_RUNTIME_CAPTURE_DIAGNOSTICS";
    private const string DiagnosticsDurationSecondsEnvironmentVariable = "KIRISCOPE_RUNTIME_CAPTURE_DIAGNOSTICS_SECONDS";
    private const string DiagnosticsOnlyMarkerFileName = "runtime-capture-diagnostics-only.txt";
    private const long MaximumRuntimeValidationBytes = 64L * 1024 * 1024;
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromMinutes(25);
    private readonly string helperPath;

    public KirikiriRuntimeExtractionFallback(string helperPath)
    {
        this.helperPath = Path.GetFullPath(helperPath);
    }

    public async Task<ExtractionTaskResult?> TryExtractAsync(
        GameInput input,
        ResourceCategory category,
        string outputDirectory,
        GameInputDiscoveryResult discovery,
        GameCompatibilityResolution compatibility,
        IProgress<string>? progress,
        CancellationToken cancellationToken = default)
    {
        if (!CanAttempt(input, discovery, out var layout) || !File.Exists(helperPath))
        {
            return null;
        }

        var plan = await BuildPlanAsync(discovery, layout, cancellationToken).ConfigureAwait(false);
        if (plan.Archives.Count == 0)
        {
            return FailedRuntimePreflightResult(
                input,
                category,
                outputDirectory,
                compatibility,
                discovery,
                "The runtime resource plan could not be formed safely. KiriScope did not fall back to raw static extraction.");
        }

        if (!plan.HasRuntimeEnumerableEntries)
        {
            return FailedRuntimePreflightResult(
                input,
                category,
                outputDirectory,
                compatibility,
                discovery,
                "No XP3 archive with a readable entry index was available for safe runtime enumeration.");
        }

        // An isolated game copy can be several GiB. Keep it on the output
        // volume instead of silently consuming the system temp drive.
        var temporaryRoot = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(outputDirectory))!,
            ".KiriScope-runtime-capture");
        var stageDirectory = Path.Combine(temporaryRoot, Guid.NewGuid().ToString("N"));
        var diagnosticsOnly = IsRuntimeCaptureDiagnosticsOnly();
        Process? gameProcess = null;
        try
        {
            progress?.Report("Preparing an isolated KiriKiri runtime capture");
            await StageGameDirectoryAsync(input.InputPath, stageDirectory, cancellationToken).ConfigureAwait(false);

            var stagedRuntimeDirectory = ResolveStagedPath(stageDirectory, input.InputPath, layout.RuntimeDirectory);
            var stagedExecutable = ResolveStagedPath(stageDirectory, input.InputPath, layout.ExecutablePath);
            await InstallRuntimeHelperAsync(stagedRuntimeDirectory, cancellationToken).ConfigureAwait(false);
            await WriteArchiveCaptureListAsync(stagedRuntimeDirectory, plan.RuntimeEnumerationArchives, category, cancellationToken).ConfigureAwait(false);
            if (diagnosticsOnly)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(stagedRuntimeDirectory, DiagnosticsOnlyMarkerFileName),
                    "KiriScope maintainer diagnostic mode\n",
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken).ConfigureAwait(false);
            }

            progress?.Report("Starting the game runtime to enumerate and decode protected XP3 resources");
            gameProcess = Process.Start(new ProcessStartInfo(stagedExecutable)
            {
                WorkingDirectory = stagedRuntimeDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            }) ?? throw new InvalidOperationException("The staged KiriKiri executable could not be started.");

            if (diagnosticsOnly)
            {
                progress?.Report("Recording the title's KiriKiri storage-media chain");
                await Task.Delay(GetRuntimeCaptureDiagnosticsDuration(), cancellationToken).ConfigureAwait(false);
                return FailedCaptureResult(
                    input, category, outputDirectory, compatibility, plan,
                    "RUNTIME_CAPTURE_DIAGNOSTICS",
                    $"KiriScope recorded the selected runtime's storage-media chain without exporting raw XP3 streams. The isolated trace is at {stageDirectory} when {KeepStageEnvironmentVariable}=1 is set.");
            }

            var completionPath = Path.Combine(stagedRuntimeDirectory, CompletionFileName);
            var captureWait = await WaitForCompletionAsync(completionPath, gameProcess, progress, cancellationToken).ConfigureAwait(false);
            if (!captureWait.Completed)
            {
                return FailedCaptureResult(
                    input, category, outputDirectory, compatibility, plan,
                    "RUNTIME_CAPTURE_INCOMPLETE",
                    $"The selected runtime ({layout.DisplayName}) ended or timed out before confirming all requested resources were captured. {captureWait.Description} {RuntimeCaptureHelperDescription}");
            }

            var manifest = await ReadCaptureManifestAsync(
                Path.Combine(stagedRuntimeDirectory, ManifestFileName),
                plan,
                cancellationToken).ConfigureAwait(false);
            if (manifest.Expected.Count == 0)
            {
                return FailedCaptureResult(
                    input, category, outputDirectory, compatibility, plan,
                    "RUNTIME_CAPTURE_NO_RESOURCES",
                    "The game runtime completed but did not enumerate any resources in the selected category.");
            }

            progress?.Report("Verifying captured resource streams");
            // A stream is exportable only when the helper proves that its
            // decoded bytes match the XP3 Adler-32. A physically written file
            // or a non-standard checksum report is evidence of neither a
            // decoded resource nor a recovered path.
            var writtenCaptureKeys = manifest.Captured;
            var missingRequests = manifest.Expected
                .Where(request =>
                    !File.Exists(GetCapturePath(stagedRuntimeDirectory, request)) &&
                    !File.Exists(GetUncheckedCapturePath(stagedRuntimeDirectory, request)))
                .ToArray();
            var structurallyVerifiedRequests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var request in manifest.IntegrityUnconfirmed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!manifest.ExpectedByKey.TryGetValue(request, out var captureRequest))
                {
                    continue;
                }

                var uncheckedPath = GetUncheckedCapturePath(stagedRuntimeDirectory, captureRequest);
                if (await IsStructurallySafeRuntimeFallbackAsync(uncheckedPath, captureRequest.EntryName, cancellationToken).ConfigureAwait(false))
                {
                    structurallyVerifiedRequests.Add(request);
                }
            }
            if (missingRequests.Length > 0)
            {
                var capturedFiles = manifest.Expected.Count(request => File.Exists(GetCapturePath(stagedRuntimeDirectory, request)));
                var helperFailedEntries = missingRequests
                    .Where(request => manifest.Failures.Contains(GetManifestKey(request.Archive.CaptureRelativeDirectory, request.EntryName)))
                    .Select(request => $"{request.Archive.CaptureRelativeDirectory}/{request.EntryName}")
                    .Take(12)
                    .ToArray();
                var helperFailureDescription = helperFailedEntries.Length == 0
                    ? string.Empty
                    : $" The helper reported write failure for {helperFailedEntries.Length:N0} missing stream(s): {string.Join(", ", helperFailedEntries)}.";
                return FailedCaptureResult(
                    input, category, outputDirectory, compatibility, plan,
                    "RUNTIME_CAPTURE_MISSING_RESOURCES",
                    $"The selected runtime ({layout.DisplayName}) enumerated {manifest.Expected.Count:N0} resource stream(s), " +
                    $"the proxy declared {manifest.Captured.Count:N0} checksum-confirmed and {manifest.IntegrityUnconfirmed.Count:N0} checksum-unconfirmed captured stream(s), and {capturedFiles:N0} capture file(s) were present. " +
                    $"{missingRequests.Length:N0} requested stream(s) were not captured from the runtime." +
                    (manifest.FilterRetriedUnconfirmed.Count == 0
                        ? " The game did not expose a usable XP3 extraction-filter retry for the checksum-unconfirmed streams."
                        : $" The game's registered XP3 extraction filter was retried for {manifest.FilterRetriedUnconfirmed.Count:N0} stream(s), but those bytes still did not match the XP3 Adler-32.") +
                    helperFailureDescription +
                    $" {captureWait.Description} {RuntimeCaptureHelperDescription}");
            }

            progress?.Report("Classifying and moving verified runtime resource streams");
            Directory.CreateDirectory(outputDirectory);
            var results = new List<GameArchiveExtractionResult>(plan.Archives.Count);
            foreach (var archive in plan.Archives)
            {
                var archiveRequests = manifest.Expected
                    .Where(request => ReferenceEquals(request.Archive, archive))
                    .OrderBy(static request => request.EntryName, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var entries = new List<Xp3EntryExtractionResult>(archiveRequests.Length);
                var validations = new List<GameExtractedResourceValidation>(archiveRequests.Length);
                foreach (var request in archiveRequests)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var requestKey = GetManifestKey(request.Archive.CaptureRelativeDirectory, request.EntryName);
                    var isChecksumConfirmed = manifest.Captured.Contains(requestKey);
                    var sourcePath = isChecksumConfirmed
                        ? GetCapturePath(stagedRuntimeDirectory, request)
                        : GetUncheckedCapturePath(stagedRuntimeDirectory, request);
                    var analysis = await AnalyzeCapturedResourceAsync(sourcePath, request.EntryName, cancellationToken).ConfigureAwait(false);
                    if (!isChecksumConfirmed && !structurallyVerifiedRequests.Contains(requestKey))
                    {
                        continue;
                    }
                    if (!MatchesRuntimeCaptureCategory(analysis.Category, category))
                    {
                        continue;
                    }

                    var outputEntryName = BuildOutputEntryName(request.EntryName, analysis);
                    var outputRelativePath = Path.Combine(archive.CaptureRelativeDirectory, outputEntryName);
                    var outputPath = SafeOutputPath.Resolve(outputDirectory, outputRelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    await TransferCaptureAsync(sourcePath, outputPath, cancellationToken).ConfigureAwait(false);
                    entries.Add(new Xp3EntryExtractionResult(
                        request.EntryName,
                        analysis.Stage,
                        true,
                        new FileInfo(outputPath).Length,
                        null,
                        null,
                        analysis.IsOpaquePath ? "KiriKiriRuntimeIndexCapture" : "KiriKiriRuntimeCapture",
                        isChecksumConfirmed
                            ? Array.Empty<KiriScopeDiagnostic>()
                            : [Info("RUNTIME_CAPTURE_STRUCTURE_VERIFIED", "The runtime stream did not match the stale XP3 Adler-32 record but passed structural content validation.")]));
                    validations.Add(new GameExtractedResourceValidation(
                        request.EntryName,
                        outputRelativePath,
                        analysis.Category,
                        analysis.Format,
                        analysis.DetectedCategory,
                        analysis.Stage,
                        analysis.ValidationAttempted,
                        analysis.IsFormatValidated,
                        analysis.Diagnostics));
                }

                results.Add(new GameArchiveExtractionResult(
                    archive.Archive.RelativePath,
                    true,
                    true,
                    archive.AllEntryCount,
                    entries.Count,
                    entries.Count,
                    archive.StructurallyInvalidEntryCount,
                    entries,
                    validations,
                    archive.StructurallyInvalidEntryCount == 0
                        ? [Info("RUNTIME_CAPTURE_ARCHIVE_VERIFIED", "Resources were opened by the staged game runtime and captured from its decoded XP3 streams.")]
                        : [
                            Info("RUNTIME_CAPTURE_ARCHIVE_VERIFIED", "Resources were opened by the staged game runtime and captured from its decoded XP3 streams."),
                            Warning("RUNTIME_CAPTURE_INVALID_INDEX_ENTRIES_SKIPPED", $"Skipped {archive.StructurallyInvalidEntryCount:N0} structurally invalid XP3 index placeholder entr{(archive.StructurallyInvalidEntryCount == 1 ? "y" : "ies")}; it cannot describe a resource stream.")
                        ]));
            }

            if (category != ResourceCategory.All && results.Sum(static result => result.ExtractedEntryCount) == 0)
            {
                return new ExtractionTaskResult(
                    input,
                    category,
                    compatibility,
                    outputDirectory,
                    true,
                    results,
                    [.. discovery.Diagnostics, .. compatibility.Diagnostics,
                        Error("RUNTIME_CAPTURE_CATEGORY_NO_MATCH", "Runtime enumeration succeeded, but none of the captured streams could be classified into the selected resource category.")]);
            }

            return new ExtractionTaskResult(
                input,
                category,
                compatibility,
                outputDirectory,
                true,
                results,
                [.. discovery.Diagnostics, .. compatibility.Diagnostics,
                    Info("RUNTIME_CAPTURE_LAUNCH_TARGET", layout.Description),
                    Info("RUNTIME_CAPTURE_PROCESS_CHAIN", captureWait.Description),
                    Info("RUNTIME_CAPTURE_HELPER", RuntimeCaptureHelperDescription),
                    Info(
                        "RUNTIME_CAPTURE_INDEX_CHECKSUM_SUMMARY",
                        $"The capture helper reported {manifest.Captured.Count:N0} checksum-confirmed and {manifest.IntegrityUnconfirmed.Count:N0} checksum-unconfirmed stream(s); checksum-unconfirmed streams were exported only after structural validation."),
                    Info("RUNTIME_CAPTURE_VERIFIED", $"Enumerated, decoded, and verified {results.Sum(static archive => archive.ExtractedEntryCount):N0} resource stream(s) with the game's KiriKiri runtime."),
                    Info("RUNTIME_CAPTURE_NOTICE_ENTRIES_SKIPPED", $"Skipped {plan.NonResourceEntryCount:N0} non-resource or structurally invalid archive index entr{(plan.NonResourceEntryCount == 1 ? "y" : "ies")}.")]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            return FailedCaptureResult(
                input,
                category,
                outputDirectory,
                compatibility,
                plan,
                "RUNTIME_CAPTURE_SETUP_FAILED",
                $"KiriScope could not prepare or run the isolated runtime capture: {exception.Message}");
        }
        finally
        {
            StopProcess(gameProcess);
            if (!KeepRuntimeCaptureStage())
            {
                TryDeleteStageDirectory(stageDirectory, temporaryRoot);
            }
        }
    }

    private static bool KeepRuntimeCaptureStage() =>
        string.Equals(
            Environment.GetEnvironmentVariable(KeepStageEnvironmentVariable),
            "1",
            StringComparison.Ordinal);

    private static bool IsRuntimeCaptureDiagnosticsOnly() =>
        string.Equals(
            Environment.GetEnvironmentVariable(DiagnosticsOnlyEnvironmentVariable),
            "1",
            StringComparison.Ordinal);

    private static TimeSpan GetRuntimeCaptureDiagnosticsDuration()
    {
        const int defaultSeconds = 12;
        const int maximumSeconds = 120;
        return int.TryParse(
            Environment.GetEnvironmentVariable(DiagnosticsDurationSecondsEnvironmentVariable),
            out var seconds) && seconds is > 0 and <= maximumSeconds
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(defaultSeconds);
    }

    private static bool CanAttempt(GameInput input, GameInputDiscoveryResult discovery, out RuntimeLaunchLayout layout)
    {
        layout = default!;
        if (input.Kind != GameInputKind.GameDirectory || discovery.Archives.Count == 0)
        {
            return false;
        }

        var candidate = discovery.Executables
            .Select(relativePath => Path.Combine(input.InputPath, relativePath))
            .Select(KirikiriRuntimeExecutableProbe.TryRead)
            .OfType<KirikiriRuntimeExecutableProbe>()
            .Where(static probe => probe.IsX86)
            .Select(probe => new
            {
                probe.FullPath,
                RuntimeDirectory = Path.GetDirectoryName(probe.FullPath)!,
                probe.Length,
                probe.ImportsVersionDll,
                probe.HasProtectedLauncherHint,
            })
            .Where(candidate =>
                discovery.Archives.All(archive => IsPathWithin(candidate.RuntimeDirectory, archive.SourcePath)))
            // Compatibility must be based on the launcher that can actually start the installed
            // game. Import metadata is recorded for diagnosis only: localized launchers often
            // start a KiriKiri child process even when the original EXE cannot run by itself.
            .OrderByDescending(candidate => candidate.Length)
            .ThenBy(candidate => candidate.FullPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (candidate is null)
        {
            return false;
        }

        layout = new RuntimeLaunchLayout(
            candidate.FullPath,
            candidate.RuntimeDirectory,
            Path.GetRelativePath(input.InputPath, candidate.FullPath),
            candidate.ImportsVersionDll,
            candidate.HasProtectedLauncherHint);
        return true;
    }

    private string RuntimeCaptureHelperDescription
    {
        get
        {
            var helperDirectory = Path.GetDirectoryName(helperPath);
            var helperBuildId = helperDirectory is null ? string.Empty : Path.GetFileName(helperDirectory);
            return helperBuildId.Length == 64 && helperBuildId.All(static character => Uri.IsHexDigit(character))
                ? $"Bundled x86 runtime-capture helper SHA-256: {helperBuildId.ToUpperInvariant()}."
                : "Bundled x86 runtime-capture helper build identifier was unavailable.";
        }
    }

    private static async Task<CapturePlan> BuildPlanAsync(
        GameInputDiscoveryResult discovery,
        RuntimeLaunchLayout layout,
        CancellationToken cancellationToken)
    {
        var archivePlans = new List<CaptureArchivePlan>(discovery.Archives.Count);
        foreach (var archive in discovery.Archives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var allEntryCount = 0;
            IReadOnlyList<CaptureEntryPlan> entries = Array.Empty<CaptureEntryPlan>();
            IReadOnlySet<string> syntheticOpaqueAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                await using var stream = new FileStream(archive.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var index = await Xp3ArchiveReader.ReadIndexAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (index.Stage >= EvidenceStage.IndexParsed)
                {
                    allEntryCount = index.Entries.Count;
                    entries = index.Entries
                        .Select(static entry => new CaptureEntryPlan(
                            entry.Name,
                            entry.Adler32,
                            !HasValidStaticLayout(entry)))
                        .ToArray();
                    syntheticOpaqueAliases = index.HashedNameMappings
                        .Where(static mapping => IsSyntheticArchiveNotice(mapping.Value))
                        .Select(static mapping => mapping.Key)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (IOException)
            {
                // The runtime enumeration below is the authoritative path for protected indexes.
            }
            catch (InvalidDataException)
            {
                // A protected index is expected for this fallback.
            }

            var runtimeArchiveName = Path.GetRelativePath(layout.RuntimeDirectory, archive.SourcePath).Replace('\\', '/');
            var captureRelativeDirectory = Path.ChangeExtension(archive.RelativePath, null) ?? archive.RelativePath;
            if (!IsSafeRuntimeRelativePath(runtimeArchiveName) || !IsSafeRuntimeRelativePath(captureRelativeDirectory))
            {
                continue;
            }

            archivePlans.Add(new CaptureArchivePlan(
                archive,
                runtimeArchiveName,
                captureRelativeDirectory,
                allEntryCount,
                entries,
                syntheticOpaqueAliases));
        }

        return new CapturePlan(archivePlans);
    }

    private static bool IsPathWithin(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeRuntimeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.IndexOf('|') >= 0 || path.IndexOf('\0') >= 0)
        {
            return false;
        }

        try
        {
            _ = SafeOutputPath.Resolve(Path.Combine(Path.GetTempPath(), "KiriScope", "runtime-path-validation"), path);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsSafeEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOf('|') >= 0 || name.IndexOf('\0') >= 0 || Path.IsPathRooted(name))
        {
            return false;
        }

        try
        {
            _ = SafeOutputPath.Resolve(Path.Combine(Path.GetTempPath(), "KiriScope", "entry-name-validation"), name);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task StageGameDirectoryAsync(string sourceDirectory, string stageDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(stageDirectory);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };
        foreach (var sourcePath in Directory.EnumerateFiles(sourceDirectory, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
            var destinationPath = SafeOutputPath.Resolve(stageDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            if (Path.GetExtension(sourcePath).Equals(".xp3", StringComparison.OrdinalIgnoreCase) &&
                TryCreateHardLink(destinationPath, sourcePath))
            {
                continue;
            }

            await CopyFileAsync(sourcePath, destinationPath, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string ResolveStagedPath(string stageDirectory, string sourceRoot, string sourcePath)
    {
        var relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
        return relativePath == "." ? stageDirectory : SafeOutputPath.Resolve(stageDirectory, relativePath);
    }

    private static async Task WriteArchiveCaptureListAsync(
        string runtimeDirectory,
        IReadOnlyList<CaptureArchivePlan> archives,
        ResourceCategory category,
        CancellationToken cancellationToken)
    {
        var archiveListPath = Path.Combine(runtimeDirectory, ArchiveRequestFileName);
        await using (var writer = new StreamWriter(archiveListPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            await writer.WriteLineAsync("# KiriScope runtime archive enumeration v1").ConfigureAwait(false);
            foreach (var archive in archives)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // The native helper resolves every entry through the game's own storage API.
                // That forces each selected XP3 archive to materialize even when the title's
                // initial scene would not otherwise touch it (for example, video or CG packs).
                var runtimeEntryNames = archive.Entries
                    .Where(static entry => !entry.IsStructurallyInvalid)
                    .Select(entry => new RuntimeCaptureEntryConfiguration(
                        entry.EntryName.Replace('\\', '/'),
                        entry.Adler32))
                    .ToArray();
                if (runtimeEntryNames.Length == 0 ||
                    runtimeEntryNames.Any(entry =>
                        entry.Name.IndexOf(ArchiveEntryListSeparator) >= 0 ||
                        entry.Name.IndexOf('#') >= 0 ||
                        !IsSafeEntryName(entry.Name) ||
                        entry.Adler32 is null))
                {
                    throw new InvalidDataException($"The XP3 index for {archive.Archive.RelativePath} cannot be represented safely for runtime capture.");
                }

                // Preserve the path recovered from the XP3 index by its
                // ordinal. Protected v3 archives keep only MD5 aliases in the
                // native item vector, so the helper cannot reconstruct the
                // original path from the item name alone.
                var serializedEntryNames = string.Join(
                    ArchiveEntryListSeparator,
                    runtimeEntryNames.Select(static entry =>
                        $"{entry.Name}#{entry.Adler32!.Value:X8}"));
                await writer.WriteLineAsync(
                    $"{archive.RuntimeArchiveName}|{archive.CaptureRelativeDirectory}|{archive.ProbeEntryName}|{serializedEntryNames}")
                    .ConfigureAwait(false);
            }
        }

        await File.WriteAllTextAsync(
            Path.Combine(runtimeDirectory, CategoryFileName),
            category.ToString().ToLowerInvariant(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteDirectCaptureRequestsAsync(
        string runtimeDirectory,
        IReadOnlyList<CaptureRequest> requests,
        CancellationToken cancellationToken)
    {
        var requestListPath = Path.Combine(runtimeDirectory, DirectRequestFileName);
        await using var writer = new StreamWriter(requestListPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteLineAsync("# KiriScope recovered-path runtime capture v1").ConfigureAwait(false);
        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(
                $"{request.Archive.RuntimeArchiveName}|{request.EntryName}|{request.Archive.CaptureRelativeDirectory}").ConfigureAwait(false);
        }
    }

    private async Task InstallRuntimeHelperAsync(string stagedRuntimeDirectory, CancellationToken cancellationToken)
    {
        var stagedVersionPath = Path.Combine(stagedRuntimeDirectory, "version.dll");
        if (File.Exists(stagedVersionPath))
        {
            var preservedVersionPath = Path.Combine(stagedRuntimeDirectory, "KiriScope.original.version.dll");
            if (File.Exists(preservedVersionPath))
            {
                throw new IOException("The staged game already contains KiriScope.original.version.dll.");
            }

            File.Move(stagedVersionPath, preservedVersionPath, overwrite: false);
        }

        await CopyFileAsync(helperPath, stagedVersionPath, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CaptureManifest> ReadCaptureManifestAsync(
        string manifestPath,
        CapturePlan plan,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath))
        {
            return CaptureManifest.Empty;
        }

        var archivesByCaptureDirectory = plan.Archives.ToDictionary(
            archive => archive.CaptureRelativeDirectory,
            StringComparer.OrdinalIgnoreCase);
        var expected = new Dictionary<string, CaptureRequest>(StringComparer.OrdinalIgnoreCase);
        var captured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var integrityUnconfirmed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filterRetriedUnconfirmed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StreamReader(manifestPath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: true);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            var parts = line.Split('|', 3);
            if (parts.Length != 3 || (parts[0] != "E" && parts[0] != "C" && parts[0] != "V" && parts[0] != "R" && parts[0] != "F") ||
                !archivesByCaptureDirectory.TryGetValue(parts[1], out var archive) || !IsSafeEntryName(parts[2]))
            {
                continue;
            }

            if (archive.IsSyntheticRuntimeEntry(parts[2]) || archive.IsStructurallyInvalidEntry(parts[2]))
            {
                continue;
            }

            var key = GetManifestKey(parts[1], parts[2]);
            if (parts[0] == "E")
            {
                expected.TryAdd(key, new CaptureRequest(archive, parts[2].Replace('\\', '/')));
            }
            else if (parts[0] == "C")
            {
                captured.Add(key);
            }
            else if (parts[0] == "V")
            {
                integrityUnconfirmed.Add(key);
            }
            else if (parts[0] == "R")
            {
                integrityUnconfirmed.Add(key);
                filterRetriedUnconfirmed.Add(key);
            }
            else if (parts[0] == "F")
            {
                failures.Add(key);
            }
        }

        return new CaptureManifest(expected.Values.ToArray(), captured, integrityUnconfirmed, filterRetriedUnconfirmed, failures);
    }

    private static string GetCapturePath(string runtimeDirectory, CaptureRequest request) =>
        SafeOutputPath.Resolve(
            Path.Combine(runtimeDirectory, "unencrypted"),
            Path.Combine(request.Archive.CaptureRelativeDirectory, request.EntryName));

    private static string GetUncheckedCapturePath(string runtimeDirectory, CaptureRequest request) =>
        GetCapturePath(runtimeDirectory, request) + ".kiriscope-partial";

    private static async Task<bool> IsStructurallySafeRuntimeFallbackAsync(
        string sourcePath,
        string entryName,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath))
        {
            return false;
        }

        var analysis = await AnalyzeCapturedResourceAsync(sourcePath, entryName, cancellationToken).ConfigureAwait(false);
        return analysis.IsFormatValidated || analysis.Format is ResourceFormat.Ogg or ResourceFormat.MpegProgramStream or ResourceFormat.OpenTypeFont ||
            (analysis.Category == ResourceCategory.Scripts && LooksLikeRuntimeScript(await ReadRuntimeHeaderAsync(sourcePath, cancellationToken).ConfigureAwait(false)));
    }

    private static async Task<byte[]> ReadRuntimeHeaderAsync(string sourcePath, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[512];
        var count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.AsSpan(0, count).ToArray();
    }

    private static string GetManifestKey(string captureRelativeDirectory, string entryName) =>
        $"{captureRelativeDirectory}|{entryName.Replace('\\', '/')}";

    private static async Task<RuntimeCaptureWaitResult> WaitForCompletionAsync(
        string completionPath,
        Process process,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        var observedProcesses = new Dictionary<int, RuntimeProcessObservation>();
        while (started.Elapsed < CaptureTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var liveProcesses = GetLiveProcessTree(process.Id);
            foreach (var observed in liveProcesses)
            {
                observedProcesses[observed.ProcessId] = observed;
            }

            if (File.Exists(completionPath))
            {
                return new RuntimeCaptureWaitResult(true, observedProcesses.Values.ToArray());
            }

            // The helper writes its completion marker on the game engine
            // thread immediately before shutdown.  A very short interval can
            // observe the root process exit before the marker becomes visible
            // on NTFS; give that write a bounded grace period rather than
            // reporting a completed capture as incomplete.
            if (liveProcesses.Count == 0 && started.Elapsed >= TimeSpan.FromSeconds(3))
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                if (File.Exists(completionPath))
                {
                    return new RuntimeCaptureWaitResult(true, observedProcesses.Values.ToArray());
                }
                return new RuntimeCaptureWaitResult(false, observedProcesses.Values.ToArray());
            }

            progress?.Report($"The game runtime is enumerating and decoding protected resources ({started.Elapsed:mm\\:ss}; {liveProcesses.Count} process(es) in launch chain)");
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        return new RuntimeCaptureWaitResult(false, observedProcesses.Values.ToArray());
    }

    private static async Task<RuntimeCaptureAnalysis> AnalyzeCapturedResourceAsync(
        string sourcePath,
        string entryName,
        CancellationToken cancellationToken)
    {
        var opaquePath = IsOpaqueCapturedPath(entryName);
        ResourceCategory? pathCategory = opaquePath ? null : GameExtractionService.Classify(entryName);
        try
        {
            var info = new FileInfo(sourcePath);
            await using var content = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var header = new byte[512];
            var headerLength = await content.ReadAsync(header, cancellationToken).ConfigureAwait(false);
            var format = ResourceFormatDetector.Detect(header.AsSpan(0, headerLength));
            var detectedCategory = GetCategoryForFormat(format);
            var category = detectedCategory ?? pathCategory ??
                (LooksLikeRuntimeScript(header.AsSpan(0, headerLength)) ? ResourceCategory.Scripts : ResourceCategory.Other);

            if (info.Length > MaximumRuntimeValidationBytes)
            {
                return new RuntimeCaptureAnalysis(
                    category,
                    format,
                    detectedCategory,
                    EvidenceStage.ContentUsable,
                    false,
                    false,
                    opaquePath,
                    [Info("RUNTIME_CAPTURE_VALIDATION_SIZE_LIMIT", $"Structural validation was skipped because the captured stream exceeds the {MaximumRuntimeValidationBytes:N0}-byte limit.")]);
            }

            if (format == ResourceFormat.Unknown)
            {
                return new RuntimeCaptureAnalysis(
                    category,
                    format,
                    detectedCategory,
                    EvidenceStage.ContentUsable,
                    false,
                    false,
                    opaquePath,
                    [Info("RUNTIME_CAPTURE_FORMAT_UNRECOGNIZED", "The runtime-decoded stream has no currently recognized content signature.")]);
            }

            content.Position = 0;
            var score = await ResourceFormatScorer.ScoreAsync(content, cancellationToken).ConfigureAwait(false);
            var diagnostics = new List<KiriScopeDiagnostic>(score.Diagnostics);
            if (!opaquePath && detectedCategory is not null && detectedCategory != pathCategory)
            {
                diagnostics.Add(Warning(
                    "RUNTIME_CAPTURE_CATEGORY_MISMATCH",
                    $"The runtime-captured content was identified as {score.Format}, which does not match its recovered path category."));
            }

            return new RuntimeCaptureAnalysis(
                category,
                score.Format,
                GetCategoryForFormat(score.Format),
                score.Stage,
                true,
                score.IsAccepted,
                opaquePath,
                diagnostics);
        }
        catch (IOException exception)
        {
            return RuntimeCaptureAnalysis.Unavailable(opaquePath, [Warning("RUNTIME_CAPTURE_VALIDATION_READ_FAILED", exception.Message)]);
        }
        catch (UnauthorizedAccessException exception)
        {
            return RuntimeCaptureAnalysis.Unavailable(opaquePath, [Warning("RUNTIME_CAPTURE_VALIDATION_READ_FAILED", exception.Message)]);
        }
        catch (InvalidDataException exception)
        {
            return RuntimeCaptureAnalysis.Unavailable(opaquePath, [Warning("RUNTIME_CAPTURE_VALIDATION_FAILED", exception.Message)]);
        }
    }

    private static bool MatchesRuntimeCaptureCategory(ResourceCategory detectedCategory, ResourceCategory selectedCategory) =>
        selectedCategory == ResourceCategory.All || detectedCategory == selectedCategory;

    private static string BuildOutputEntryName(string entryName, RuntimeCaptureAnalysis analysis)
    {
        if (!analysis.IsOpaquePath || Path.HasExtension(entryName))
        {
            return entryName;
        }

        var extension = analysis.Format switch
        {
            ResourceFormat.Png => ".png",
            ResourceFormat.Tlg => ".tlg",
            ResourceFormat.Psb => ".psb",
            ResourceFormat.Pimg => ".pimg",
            ResourceFormat.Ogg => ".ogg",
            ResourceFormat.Wave => ".wav",
            ResourceFormat.Jpeg => ".jpg",
            ResourceFormat.Bmp => ".bmp",
            ResourceFormat.MpegProgramStream => ".mpg",
            ResourceFormat.OpenTypeFont => ".otf",
            _ => string.Empty,
        };
        return entryName + extension;
    }

    private static bool IsOpaqueCapturedPath(string entryName) =>
        entryName.StartsWith("__opaque__/", StringComparison.OrdinalIgnoreCase) ||
        entryName.StartsWith("__opaque__\\", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeRuntimeScript(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 2 &&
            ((header[0] == 0xff && header[1] == 0xfe) || (header[0] == 0xfe && header[1] == 0xff)))
        {
            return true;
        }

        if (header.Length >= 3 && header[0] == 0xfe && header[1] == 0xfe && header[2] == 0x01)
        {
            return true;
        }

        var printable = 0;
        foreach (var value in header)
        {
            if (value is 0x09 or 0x0a or 0x0d || (value >= 0x20 && value <= 0x7e))
            {
                printable++;
            }
        }

        return header.Length >= 32 && printable * 100 / header.Length >= 90;
    }

    private static ResourceCategory? GetCategoryForFormat(ResourceFormat format) => format switch
    {
        ResourceFormat.Png or ResourceFormat.Jpeg or ResourceFormat.Bmp or ResourceFormat.Tlg or ResourceFormat.Psb or ResourceFormat.Pimg => ResourceCategory.Images,
        ResourceFormat.Ogg or ResourceFormat.Wave => ResourceCategory.Audio,
        ResourceFormat.MpegProgramStream => ResourceCategory.Other,
        _ => null,
    };

    private static async Task TransferCaptureAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        try
        {
            File.Move(sourcePath, destinationPath, overwrite: false);
        }
        catch (IOException)
        {
            await CopyFileAsync(sourcePath, destinationPath, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task CopyFileAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, BufferSize, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryCreateHardLink(string destinationPath, string sourcePath)
    {
        try
        {
            return CreateHardLink(destinationPath, sourcePath, IntPtr.Zero);
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    private static ExtractionTaskResult FailedCaptureResult(
        GameInput input,
        ResourceCategory category,
        string outputDirectory,
        GameCompatibilityResolution compatibility,
        CapturePlan plan,
        string diagnosticCode,
        string message)
    {
        var archives = plan.Archives.Select(archive => new GameArchiveExtractionResult(
            archive.Archive.RelativePath,
            true,
            false,
            archive.AllEntryCount,
            0,
            0,
            0,
            Array.Empty<Xp3EntryExtractionResult>(),
            Array.Empty<GameExtractedResourceValidation>(),
            [Error(diagnosticCode, message)])).ToArray();
        return new ExtractionTaskResult(input, category, compatibility, outputDirectory, false, archives, [Error(diagnosticCode, message)]);
    }

    private static ExtractionTaskResult FailedRuntimePreflightResult(
        GameInput input,
        ResourceCategory category,
        string outputDirectory,
        GameCompatibilityResolution compatibility,
        GameInputDiscoveryResult discovery,
        string message)
    {
        var archives = discovery.Archives.Select(archive => new GameArchiveExtractionResult(
            archive.RelativePath,
            false,
            false,
            0,
            0,
            0,
            0,
            Array.Empty<Xp3EntryExtractionResult>(),
            Array.Empty<GameExtractedResourceValidation>(),
            [Error("RUNTIME_CAPTURE_PLAN_FAILED", message)])).ToArray();
        return new ExtractionTaskResult(
            input,
            category,
            compatibility,
            outputDirectory,
            false,
            archives,
            [.. discovery.Diagnostics, .. compatibility.Diagnostics, Error("RUNTIME_CAPTURE_PLAN_FAILED", message)]);
    }

    private static void StopProcess(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            }
        }
        catch (InvalidOperationException)
        {
            // The process ended between the state check and Kill.
        }
        finally
        {
            process.Dispose();
        }
    }

    private static IReadOnlyList<RuntimeProcessObservation> GetLiveProcessTree(int rootProcessId)
    {
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == InvalidSnapshotHandle)
        {
            return IsProcessAlive(rootProcessId)
                ? [new RuntimeProcessObservation(rootProcessId, 0, "<launch process>")]
                : Array.Empty<RuntimeProcessObservation>();
        }

        try
        {
            var entries = new List<ProcessEntry32>();
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry))
            {
                return IsProcessAlive(rootProcessId)
                    ? [new RuntimeProcessObservation(rootProcessId, 0, "<launch process>")]
                    : Array.Empty<RuntimeProcessObservation>();
            }

            do
            {
                entries.Add(entry);
                entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            }
            while (Process32Next(snapshot, ref entry));

            var childrenByParent = entries
                .GroupBy(static item => item.ParentProcessId)
                .ToDictionary(static group => group.Key, static group => group.ToArray());
            var result = new List<RuntimeProcessObservation>();
            var pending = new Queue<int>();
            var visited = new HashSet<int>();
            pending.Enqueue(rootProcessId);
            while (pending.Count > 0)
            {
                var processId = pending.Dequeue();
                if (!visited.Add(processId))
                {
                    continue;
                }

                var entryForProcess = entries.FirstOrDefault(item => item.ProcessId == processId);
                if (entryForProcess.ProcessId == 0)
                {
                    continue;
                }

                result.Add(new RuntimeProcessObservation(
                    (int)entryForProcess.ProcessId,
                    (int)entryForProcess.ParentProcessId,
                    entryForProcess.ExecutableName));
                if (!childrenByParent.TryGetValue(entryForProcess.ProcessId, out var children))
                {
                    continue;
                }

                foreach (var child in children)
                {
                    pending.Enqueue((int)child.ProcessId);
                }
            }

            return result;
        }
        catch (EntryPointNotFoundException)
        {
            return IsProcessAlive(rootProcessId)
                ? [new RuntimeProcessObservation(rootProcessId, 0, "<launch process>")]
                : Array.Empty<RuntimeProcessObservation>();
        }
        finally
        {
            _ = CloseHandle(snapshot);
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void TryDeleteStageDirectory(string stageDirectory, string temporaryRoot)
    {
        try
        {
            var root = Path.GetFullPath(temporaryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var stage = Path.GetFullPath(stageDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (stage.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(stage))
            {
                Directory.Delete(stage, recursive: true);
            }
        }
        catch (IOException)
        {
            // A failed cleanup is non-fatal. User output remains complete and valid.
        }
        catch (UnauthorizedAccessException)
        {
            // A failed cleanup is non-fatal. User output remains complete and valid.
        }
    }

    private static KiriScopeDiagnostic Info(string code, string message) => new(code, DiagnosticSeverity.Info, message);

    private static KiriScopeDiagnostic Warning(string code, string message) => new(code, DiagnosticSeverity.Warning, message);

    private static KiriScopeDiagnostic Error(string code, string message) => new(code, DiagnosticSeverity.Error, message);

    private const uint Th32csSnapProcess = 0x00000002;
    private static readonly IntPtr InvalidSnapshotHandle = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint ThreadCount;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableName;
    }

    private sealed record RuntimeLaunchLayout(
        string ExecutablePath,
        string RuntimeDirectory,
        string RelativeExecutablePath,
        bool ImportsVersionDll,
        bool HasProtectedLauncherHint)
    {
        public string DisplayName => RelativeExecutablePath.Replace('\\', '/');

        public string Description =>
            $"Runtime capture selected {DisplayName} " +
            $"by launch compatibility (direct VERSION.dll import: {(ImportsVersionDll ? "yes" : "no")}, " +
            $"protected-launcher hint: {(HasProtectedLauncherHint ? "yes" : "no")}).";
    }

    private sealed record RuntimeProcessObservation(int ProcessId, int ParentProcessId, string ExecutableName);

    private sealed record RuntimeCaptureWaitResult(bool Completed, IReadOnlyList<RuntimeProcessObservation> ObservedProcesses)
    {
        public string Description => ObservedProcesses.Count == 0
            ? "No launch-process metadata could be observed."
            : $"Observed launch chain: {string.Join(" -> ", ObservedProcesses.Select(static item => $"{item.ExecutableName} (PID {item.ProcessId}, parent {item.ParentProcessId})"))}.";
    }

    private sealed record CapturePlan(IReadOnlyList<CaptureArchivePlan> Archives)
    {
        public IReadOnlyList<CaptureArchivePlan> RuntimeEnumerationArchives => Archives
            .Where(static archive => archive.AllEntryCount > 0)
            .ToArray();

        public bool HasRuntimeEnumerableEntries => RuntimeEnumerationArchives.Count > 0;

        public bool HasCompleteResourceNames => Archives.Count > 0 &&
            Archives.All(static archive =>
                archive.AllEntryCount > 0 &&
                archive.Entries.Count == archive.AllEntryCount &&
                archive.Entries.All(static entry => IsSafeEntryName(entry.EntryName) && !IsOpaqueIndexName(entry.EntryName)));

        public IReadOnlyList<CaptureRequest> GetSelectedRequests(ResourceCategory category) =>
            GetUncollapsedSelectedRequests(category);

        public int GetDuplicateResourcePathCount(ResourceCategory category) =>
            GetUncollapsedSelectedRequests(category)
                .GroupBy(static request => GetManifestKey(request.Archive.CaptureRelativeDirectory, request.EntryName), StringComparer.OrdinalIgnoreCase)
                .Sum(static group => Math.Max(0, group.Count() - 1));

        private IReadOnlyList<CaptureRequest> GetUncollapsedSelectedRequests(ResourceCategory category) =>
            Archives
                .SelectMany(archive => archive.Entries
                    .Where(entry => !IsSyntheticArchiveNotice(entry.EntryName) && GameExtractionService.MatchesCategory(entry.EntryName, category))
                    .Select(entry => new CaptureRequest(archive, entry.EntryName)))
                .ToArray();

        public int NoticeEntryCount => Archives.Sum(static archive =>
            archive.Entries.Count(static entry => IsSyntheticArchiveNotice(entry.EntryName)));

        public int NonResourceEntryCount => Archives.Sum(static archive =>
            archive.Entries.Count(static entry => IsSyntheticArchiveNotice(entry.EntryName) || entry.IsStructurallyInvalid));
    }

    private sealed record CaptureArchivePlan(
        DiscoveredGameArchive Archive,
        string RuntimeArchiveName,
        string CaptureRelativeDirectory,
        int AllEntryCount,
        IReadOnlyList<CaptureEntryPlan> Entries,
        IReadOnlySet<string> SyntheticOpaqueAliases)
    {
        public string ProbeEntryName => Entries.FirstOrDefault(static entry => !entry.IsStructurallyInvalid)?.EntryName ?? Entries[0].EntryName;

        public int StructurallyInvalidEntryCount => Entries.Count(static entry => entry.IsStructurallyInvalid);

        public bool IsStructurallyInvalidEntry(string entryName)
        {
            var originalEntryName = entryName.Replace('\\', '/');
            var separator = originalEntryName.LastIndexOf('/');
            if (separator >= 0)
            {
                originalEntryName = originalEntryName[(separator + 1)..];
            }

            // The native helper keeps opaque index names collision-safe as
            // "00000000_<original-name>". Compare the original runtime name
            // against the statically validated index record.
            if (originalEntryName.Length > 9 && originalEntryName[8] == '_' &&
                originalEntryName[..8].All(static character => character is >= '0' and <= '9'))
            {
                originalEntryName = originalEntryName[9..];
            }

            return Entries.Any(entry =>
                entry.IsStructurallyInvalid && entry.EntryName.Equals(originalEntryName, StringComparison.OrdinalIgnoreCase));
        }

        public bool IsSyntheticRuntimeEntry(string entryName)
        {
            if (IsSyntheticArchiveNotice(entryName))
            {
                return true;
            }

            var normalized = entryName.Replace('\\', '/');
            var fileName = normalized[(normalized.LastIndexOf('/') + 1)..];
            if (fileName.Length != 41 || fileName[8] != '_' ||
                !fileName[..8].All(static character => character is >= '0' and <= '9'))
            {
                return false;
            }

            var alias = fileName[9..];
            return alias.All(static character =>
                       (character is >= '0' and <= '9') ||
                       (character is >= 'a' and <= 'f') ||
                       (character is >= 'A' and <= 'F')) &&
                   SyntheticOpaqueAliases.Contains(alias);
        }
    }

    private sealed record CaptureEntryPlan(
        string EntryName,
        uint? Adler32,
        bool IsStructurallyInvalid);

    private sealed record RuntimeCaptureEntryConfiguration(
        string Name,
        uint? Adler32);

    private sealed record CaptureRequest(CaptureArchivePlan Archive, string EntryName);

    private sealed record RuntimeCaptureAnalysis(
        ResourceCategory Category,
        ResourceFormat Format,
        ResourceCategory? DetectedCategory,
        EvidenceStage Stage,
        bool ValidationAttempted,
        bool IsFormatValidated,
        bool IsOpaquePath,
        IReadOnlyList<KiriScopeDiagnostic> Diagnostics)
    {
        public static RuntimeCaptureAnalysis Unavailable(bool opaquePath, IReadOnlyList<KiriScopeDiagnostic> diagnostics) =>
            new(ResourceCategory.Other, ResourceFormat.Unknown, null, EvidenceStage.ContentUsable, true, false, opaquePath, diagnostics);
    }

    private static bool IsOpaqueIndexName(string value) =>
        value.Length == 32 && value.All(static character =>
            (character is >= '0' and <= '9') ||
            (character is >= 'a' and <= 'f') ||
            (character is >= 'A' and <= 'F'));

    private static bool IsSyntheticArchiveNotice(string value) =>
        value.StartsWith("$$$ This is a protected archive. $$$", StringComparison.Ordinal);

    private static bool HasValidStaticLayout(Xp3Entry entry)
    {
        try
        {
            return entry.UnpackedSize >= 0 && entry.PackedSize >= 0 &&
                entry.Segments.All(static segment =>
                    segment.Offset >= 0 &&
                    segment.UnpackedSize >= 0 &&
                    segment.PackedSize >= 0 &&
                    (segment.IsCompressed || segment.UnpackedSize == segment.PackedSize)) &&
                entry.Segments.Aggregate(0L, static (total, segment) => checked(total + segment.UnpackedSize)) == entry.UnpackedSize &&
                entry.Segments.Aggregate(0L, static (total, segment) => checked(total + segment.PackedSize)) == entry.PackedSize;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private sealed record CaptureManifest(
        IReadOnlyList<CaptureRequest> Expected,
        IReadOnlySet<string> Captured,
        IReadOnlySet<string> IntegrityUnconfirmed,
        IReadOnlySet<string> FilterRetriedUnconfirmed,
        IReadOnlySet<string> Failures)
    {
        public IReadOnlyDictionary<string, CaptureRequest> ExpectedByKey => Expected.ToDictionary(
            static request => GetManifestKey(request.Archive.CaptureRelativeDirectory, request.EntryName),
            StringComparer.OrdinalIgnoreCase);

        public static CaptureManifest Empty { get; } = new(
            Array.Empty<CaptureRequest>(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }
}
