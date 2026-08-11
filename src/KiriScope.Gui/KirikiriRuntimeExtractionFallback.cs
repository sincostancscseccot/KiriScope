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
    private const string CompletionFileName = "capture-complete.txt";
    private const string ManifestFileName = "capture-manifest.txt";
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

        var temporaryRoot = Path.Combine(Path.GetTempPath(), "KiriScope", "runtime-capture");
        var stageDirectory = Path.Combine(temporaryRoot, Guid.NewGuid().ToString("N"));
        Process? gameProcess = null;
        try
        {
            progress?.Report("Preparing an isolated KiriKiri runtime capture");
            await StageGameDirectoryAsync(input.InputPath, stageDirectory, cancellationToken).ConfigureAwait(false);

            var stagedRuntimeDirectory = ResolveStagedPath(stageDirectory, input.InputPath, layout.RuntimeDirectory);
            var stagedExecutable = ResolveStagedPath(stageDirectory, input.InputPath, layout.ExecutablePath);
            await InstallRuntimeHelperAsync(stagedRuntimeDirectory, cancellationToken).ConfigureAwait(false);
            await WriteArchiveCaptureListAsync(stagedRuntimeDirectory, plan.RuntimeEnumerationArchives, category, cancellationToken).ConfigureAwait(false);

            progress?.Report("Starting the game runtime to enumerate and decode protected XP3 resources");
            gameProcess = Process.Start(new ProcessStartInfo(stagedExecutable)
            {
                WorkingDirectory = stagedRuntimeDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            }) ?? throw new InvalidOperationException("The staged KiriKiri executable could not be started.");

            var completionPath = Path.Combine(stagedRuntimeDirectory, CompletionFileName);
            var captureCompleted = await WaitForCompletionAsync(completionPath, gameProcess, progress, cancellationToken).ConfigureAwait(false);
            if (!captureCompleted)
            {
                return FailedCaptureResult(
                    input, category, outputDirectory, compatibility, plan,
                    "RUNTIME_CAPTURE_INCOMPLETE",
                    "The game runtime ended or timed out before confirming all requested resources were captured.");
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
            var missingRequests = manifest.Expected
                .Where(request =>
                    !manifest.Captured.Contains(GetManifestKey(request.Archive.CaptureRelativeDirectory, request.EntryName)) ||
                    !File.Exists(GetCapturePath(stagedRuntimeDirectory, request)))
                .ToArray();
            if (missingRequests.Length > 0)
            {
                return FailedCaptureResult(
                    input, category, outputDirectory, compatibility, plan,
                    "RUNTIME_CAPTURE_MISSING_RESOURCES",
                    $"The game runtime completed, but {missingRequests.Length:N0} enumerated resource stream(s) were not captured.");
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
                    var sourcePath = GetCapturePath(stagedRuntimeDirectory, request);
                    var analysis = await AnalyzeCapturedResourceAsync(sourcePath, request.EntryName, cancellationToken).ConfigureAwait(false);
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
                        Array.Empty<KiriScopeDiagnostic>()));
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
                    Info("RUNTIME_CAPTURE_VERIFIED", $"Enumerated, captured, and verified {manifest.Expected.Count:N0} resource stream(s) with the game's KiriKiri runtime."),
                    Info("RUNTIME_CAPTURE_OPAQUE_INDEX_PATHS", $"Captured {manifest.Expected.Count(static request => IsOpaqueCapturedPath(request.EntryName)):N0} resource stream(s) whose original path was unavailable under an explicit __opaque__ index path."),
                    Info("RUNTIME_CAPTURE_NOTICE_ENTRIES_SKIPPED", $"Skipped {plan.NonResourceEntryCount:N0} non-resource or structurally invalid archive index entr{(plan.NonResourceEntryCount == 1 ? "y" : "ies")}.")]);
        }
        finally
        {
            StopProcess(gameProcess);
            TryDeleteStageDirectory(stageDirectory, temporaryRoot);
        }
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
            .Where(IsX86Executable)
            .Select(path => new
            {
                ExecutablePath = path,
                RuntimeDirectory = Path.GetDirectoryName(path)!,
                Size = new FileInfo(path).Length,
            })
            .Where(candidate =>
                discovery.Archives.All(archive => IsPathWithin(candidate.RuntimeDirectory, archive.SourcePath)))
            .OrderByDescending(candidate => candidate.Size)
            .ThenBy(candidate => candidate.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (candidate is null)
        {
            return false;
        }

        layout = new RuntimeLaunchLayout(candidate.ExecutablePath, candidate.RuntimeDirectory);
        return true;
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
            try
            {
                await using var stream = new FileStream(archive.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var index = await Xp3ArchiveReader.ReadIndexAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (index.Stage >= EvidenceStage.IndexParsed)
                {
                    allEntryCount = index.Entries.Count;
                    entries = index.Entries
                        .Select(static entry => new CaptureEntryPlan(entry.Name, !HasValidStaticLayout(entry)))
                        .ToArray();
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

            archivePlans.Add(new CaptureArchivePlan(archive, runtimeArchiveName, captureRelativeDirectory, allEntryCount, entries));
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
                // The native helper opens this known entry through the game's own storage API.
                // That forces each selected XP3 archive to materialize even when the title's
                // initial scene would not otherwise touch it (for example, video or CG packs).
                await writer.WriteLineAsync($"{archive.RuntimeArchiveName}|{archive.CaptureRelativeDirectory}|{archive.ProbeEntryName}").ConfigureAwait(false);
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
        using var reader = new StreamReader(manifestPath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: true);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            var parts = line.Split('|', 3);
            if (parts.Length != 3 || (parts[0] != "E" && parts[0] != "C" && parts[0] != "F") ||
                !archivesByCaptureDirectory.TryGetValue(parts[1], out var archive) || !IsSafeEntryName(parts[2]))
            {
                continue;
            }

            if (archive.IsStructurallyInvalidEntry(parts[2]))
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
        }

        return new CaptureManifest(expected.Values.ToArray(), captured);
    }

    private static string GetCapturePath(string runtimeDirectory, CaptureRequest request) =>
        SafeOutputPath.Resolve(
            Path.Combine(runtimeDirectory, "unencrypted"),
            Path.Combine(request.Archive.CaptureRelativeDirectory, request.EntryName));

    private static string GetManifestKey(string captureRelativeDirectory, string entryName) =>
        $"{captureRelativeDirectory}|{entryName.Replace('\\', '/')}";

    private static async Task<bool> WaitForCompletionAsync(string completionPath, Process process, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        while (started.Elapsed < CaptureTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(completionPath))
            {
                return true;
            }

            if (process.HasExited)
            {
                return false;
            }

            progress?.Report($"The game runtime is enumerating and decoding protected resources ({started.Elapsed:mm\\:ss})");
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        return false;
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

    private static bool IsX86Executable(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> dosHeader = stackalloc byte[64];
            if (stream.Read(dosHeader) != dosHeader.Length || dosHeader[0] != (byte)'M' || dosHeader[1] != (byte)'Z')
            {
                return false;
            }

            var peOffset = BitConverter.ToInt32(dosHeader.Slice(60, 4));
            if (peOffset < 64 || peOffset > 1_048_576)
            {
                return false;
            }

            stream.Position = peOffset;
            Span<byte> signatureAndMachine = stackalloc byte[6];
            return stream.Read(signatureAndMachine) == signatureAndMachine.Length &&
                signatureAndMachine[0] == (byte)'P' && signatureAndMachine[1] == (byte)'E' &&
                signatureAndMachine[2] == 0 && signatureAndMachine[3] == 0 &&
                BitConverter.ToUInt16(signatureAndMachine.Slice(4, 2)) == 0x014c;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
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

    private sealed record RuntimeLaunchLayout(string ExecutablePath, string RuntimeDirectory);

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
            GetUncollapsedSelectedRequests(category)
                .GroupBy(static request => GetManifestKey(request.Archive.CaptureRelativeDirectory, request.EntryName), StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First())
                .ToArray();

        public int GetDuplicateResourcePathCount(ResourceCategory category)
        {
            var uncollapsed = GetUncollapsedSelectedRequests(category);
            return uncollapsed.Count - uncollapsed
                .Select(static request => GetManifestKey(request.Archive.CaptureRelativeDirectory, request.EntryName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
        }

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
        IReadOnlyList<CaptureEntryPlan> Entries)
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
    }

    private sealed record CaptureEntryPlan(string EntryName, bool IsStructurallyInvalid);

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

    private sealed record CaptureManifest(IReadOnlyList<CaptureRequest> Expected, IReadOnlySet<string> Captured)
    {
        public static CaptureManifest Empty { get; } = new(Array.Empty<CaptureRequest>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }
}
