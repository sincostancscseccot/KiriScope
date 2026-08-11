using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using KiriScope.Analysis;
using KiriScope.Core.Evidence;
using KiriScope.Filters.BuiltIn;
using KiriScope.Integrations;
using KiriScope.IO.Hashing;
using KiriScope.Knowledge;
using KiriScope.Plugins.Abstractions.Filters;
using KiriScope.Resources;
using KiriScope.Runtime;
using KiriScope.Xp3;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    if (args is ["version"])
    {
        var version = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? "0.0.1-dev";
        Console.WriteLine($"KiriScope {version}");
        return 0;
    }

    if (args is ["probe", var probeFilePath])
    {
        return await ProbeAsync(probeFilePath);
    }

    if (args is ["analyze", "pe", var analysisInputPath])
    {
        return await AnalyzePeAsync(analysisInputPath);
    }

    if (args is ["analyze", "directory", var analysisDirectoryPath])
    {
        return await AnalyzeDirectoryAsync(analysisDirectoryPath);
    }

    if (args is ["analyze", "archive", var archiveInputPath, var analysisArchivePath])
    {
        return await ArchiveAnalysisAsync(archiveInputPath, analysisArchivePath);
    }

    if (args is ["analyze", "ghidra", var ghidraInputPath, var ghidraProjectDirectory, var ghidraProjectName, "--headless", var ghidraHeadlessPath])
    {
        return await RunGhidraAsync(ghidraInputPath, ghidraProjectDirectory, ghidraProjectName, ghidraHeadlessPath);
    }

    if (args is ["analyze", "runtime", "snapshot", var runtimeProcessId, var runtimeArchivePath, "--enable-runtime-capture"])
    {
        return await CaptureRuntimeSnapshotAsync(runtimeProcessId, runtimeArchivePath);
    }

    if (args is ["analyze", "runtime", "inspect", var inspectProcessId])
    {
        return InspectRuntimeTarget(inspectProcessId);
    }

    if (args is ["analyze", "runtime", "snapshot", _, _])
    {
        Console.Error.WriteLine("Runtime capture is disabled by default. Repeat the command with --enable-runtime-capture after reviewing the target PID and action.");
        return 2;
    }

    if (args is ["analyze", "runtime", "import-procmon", var procmonProcessId, var procmonCsvPath, var procmonArchivePath, "--enable-runtime-capture"])
    {
        return await ImportProcmonEvidenceAsync(procmonProcessId, procmonCsvPath, procmonArchivePath);
    }

    if (args is ["analyze", "runtime", "import-procmon", _, _, _])
    {
        Console.Error.WriteLine("Runtime evidence import requires --enable-runtime-capture to acknowledge the selected PID and source file.");
        return 2;
    }

    if (args is ["analyze", "runtime", "compare-procmon", var comparisonProcessId, var leftProcmonCsvPath, var rightProcmonCsvPath, var comparisonArchivePath, "--enable-runtime-capture"])
    {
        return await CompareProcmonEvidenceAsync(comparisonProcessId, leftProcmonCsvPath, rightProcmonCsvPath, comparisonArchivePath);
    }

    if (args is ["analyze", "runtime", "compare-procmon", _, _, _, _])
    {
        Console.Error.WriteLine("Offline ProcMon comparison requires --enable-runtime-capture to acknowledge the selected PID and source files.");
        return 2;
    }

    if (args is ["knowledge", "validate", var knowledgeRoot])
    {
        return await ValidateKnowledgeBaseAsync(knowledgeRoot);
    }

    if (args is ["knowledge", "list", var knowledgeListRoot])
    {
        return await ListKnowledgeBaseAsync(knowledgeListRoot);
    }

    if (args is ["knowledge", "match", var knowledgeMatchRoot, var knowledgeBinaryPath])
    {
        return await MatchKnowledgeBaseAsync(knowledgeMatchRoot, knowledgeBinaryPath);
    }

    if (args is ["knowledge", "scan", var knowledgeScanRoot, var knowledgeScanDirectory, var knowledgeScanOutputPath])
    {
        return await ScanKnowledgeBaseAsync(knowledgeScanRoot, knowledgeScanDirectory, knowledgeScanOutputPath);
    }

    if (args is ["knowledge", "compare", var leftKnowledgeReportPath, var rightKnowledgeReportPath, var knowledgeComparisonOutputPath])
    {
        return await CompareKnowledgeReportsAsync(leftKnowledgeReportPath, rightKnowledgeReportPath, knowledgeComparisonOutputPath);
    }

    if (args is ["overlay", "plan", var overlayReferenceDirectory, var overlayOverrideDirectory, var overlayReportPath])
    {
        return await PlanLooseFileOverlayAsync(overlayReferenceDirectory, overlayOverrideDirectory, overlayReportPath);
    }

    if (args is ["report", "compare", "static", var leftStaticArchivePath, var rightStaticArchivePath, var staticComparisonOutputPath])
    {
        return await CompareStaticReportsAsync(leftStaticArchivePath, rightStaticArchivePath, staticComparisonOutputPath);
    }

    if (args is ["xp3", "list", var archivePath])
    {
        return await ListAsync(archivePath);
    }

    if (args is ["xp3", "profile", var profileArchivePath])
    {
        return await ProfileXp3Async(profileArchivePath, includeHash: false);
    }

    if (args is ["xp3", "profile", var profileWithHashArchivePath, "--hash"])
    {
        return await ProfileXp3Async(profileWithHashArchivePath, includeHash: true);
    }

    if (args is ["xp3", "pack", var packSourceDirectory, var packOutputPath])
    {
        return await PackXp3Async(packSourceDirectory, packOutputPath);
    }

    if (args is ["xp3", "extract", var extractionArchivePath, var outputDirectory])
    {
        return await ExtractAsync(extractionArchivePath, outputDirectory, null, null);
    }

    if (args is ["xp3", "extract", var filteredArchivePath, var filteredOutputDirectory, "--xor-hex", var xorKey])
    {
        if (!TryCreateXorFilter(xorKey, out var filter))
        {
            Console.Error.WriteLine("The XOR key must be a non-empty even-length hexadecimal string.");
            return 2;
        }

        var xorFilter = filter!;
        var scheme = new ContentFilterSchemeDescriptor(
            "command-line.repeating-xor",
            "Repeating XOR supplied on the command line",
            xorFilter.Descriptor.Id,
            xorFilter.Descriptor.Version,
            new ContentFilterParameterSource("command-line", "--xor-hex", "The key itself is intentionally omitted from reports."));
        return await ExtractAsync(
            filteredArchivePath,
            filteredOutputDirectory,
            new Xp3EntryExtractionOptions { ContentFilter = xorFilter },
            scheme);
    }

    if (args is ["xp3", "extract", var schemeArchivePath, var schemeOutputDirectory, "--scheme", var schemePath])
    {
        try
        {
            var scheme = BuiltInContentFilterSchemeLoader.Load(schemePath);
            return await ExtractAsync(
                schemeArchivePath,
                schemeOutputDirectory,
                new Xp3EntryExtractionOptions { ContentFilter = scheme.Filter },
                scheme.Descriptor);
        }
        catch (ContentFilterException exception)
        {
            WriteJson(new { SchemeFile = Path.GetFullPath(schemePath), Succeeded = false, exception.Code, exception.Message });
            return 3;
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 3;
        }
    }

    if (args is ["unpack", var gameInputPath, var gameOutputDirectory])
    {
        return await UnpackGameAsync(gameInputPath, gameOutputDirectory, ResourceCategory.All, null);
    }

    if (args is ["unpack", var categorizedGameInputPath, var categorizedGameOutputDirectory, "--category", var categoryText])
    {
        if (!TryParseResourceCategory(categoryText, out var category))
        {
            Console.Error.WriteLine("--category must be one of: all, images, audio, scripts, other.");
            return 2;
        }

        return await UnpackGameAsync(categorizedGameInputPath, categorizedGameOutputDirectory, category, null);
    }

    if (args is ["unpack", var knowledgeGameInputPath, var knowledgeGameOutputDirectory, "--knowledge-root", var unpackKnowledgeRoot])
    {
        return await UnpackGameAsync(knowledgeGameInputPath, knowledgeGameOutputDirectory, ResourceCategory.All, unpackKnowledgeRoot);
    }

    if (args is ["unpack", var categorizedKnowledgeGameInputPath, var categorizedKnowledgeGameOutputDirectory, "--category", var categorizedKnowledgeCategoryText, "--knowledge-root", var categorizedKnowledgeRoot])
    {
        if (!TryParseResourceCategory(categorizedKnowledgeCategoryText, out var category))
        {
            Console.Error.WriteLine("--category must be one of: all, images, audio, scripts, other.");
            return 2;
        }

        return await UnpackGameAsync(categorizedKnowledgeGameInputPath, categorizedKnowledgeGameOutputDirectory, category, categorizedKnowledgeRoot);
    }

    if (args.Length >= 4 && string.Equals(args[0], "research", StringComparison.Ordinal) && string.Equals(args[1], "package", StringComparison.Ordinal))
    {
        return await CreateGameResearchPackageAsync(args[2..]);
    }

    if (args.Length >= 4 && string.Equals(args[0], "filter", StringComparison.Ordinal) && string.Equals(args[1], "score", StringComparison.Ordinal))
    {
        return await ScoreFilterCandidatesAsync(args[2..]);
    }

    if (args is ["psb", "extract", var psbPath, var resourceName, var resourceOutputPath])
    {
        return await ExtractPsbResourceAsync(psbPath, resourceName, resourceOutputPath);
    }

    if (args is ["psb", "profile", var psbProfilePath])
    {
        return await ProfilePsbResourcesAsync(psbProfilePath);
    }

    if (args is ["psb", "extract-all", var psbExportPath, var psbExportOutputDirectory])
    {
        return await ExportPsbResourcesAsync(psbExportPath, psbExportOutputDirectory);
    }

    if (args is ["convert", "bmp-to-png", var bmpPath, var pngPath])
    {
        return await ConvertBmpToPngAsync(bmpPath, pngPath);
    }

    if (args is ["convert", "tlg5-to-png", var tlgPath, var tlgPngPath])
    {
        return await ConvertTlg5ToPngAsync(tlgPath, tlgPngPath);
    }

    if (args is ["convert", "batch-to-png", var batchInputDirectory, var batchOutputDirectory])
    {
        return await ConvertDirectoryToPngAsync(batchInputDirectory, batchOutputDirectory);
    }

    if (args is ["convert", "tlg-to-png", var externalTlgPath, var externalPngPath, "--freemote", var freeMotePath])
    {
        return await ConvertTlgWithFreeMoteAsync(externalTlgPath, externalPngPath, freeMotePath);
    }

    if (args is ["verify", var resourcePath])
    {
        return await VerifyAsync(resourcePath);
    }

    Console.Error.WriteLine("""
        Usage:
          kiriscope version
          kiriscope probe <file>
          kiriscope analyze pe <binary>
          kiriscope analyze directory <game-directory>
          kiriscope analyze archive <binary> <output-json>
          kiriscope analyze ghidra <binary> <project-directory> <project-name> --headless <analyzeHeadless.bat-or-executable>
          kiriscope analyze runtime snapshot <pid> <new-report-json> --enable-runtime-capture
          kiriscope analyze runtime inspect <pid>
          kiriscope analyze runtime import-procmon <pid> <procmon-csv> <new-report-json> --enable-runtime-capture
          kiriscope analyze runtime compare-procmon <pid> <left-procmon-csv> <right-procmon-csv> <new-report-json> --enable-runtime-capture
          kiriscope knowledge validate <knowledge-root>
          kiriscope knowledge list <knowledge-root>
          kiriscope knowledge match <knowledge-root> <binary>
          kiriscope knowledge scan <knowledge-root> <input-directory> <new-report-json>
          kiriscope knowledge compare <left-scan-report.json> <right-scan-report.json> <new-report.json>
          kiriscope overlay plan <reference-directory> <override-directory> <new-report.json>
          kiriscope report compare static <left-static-archive.json> <right-static-archive.json> <new-report.json>
          kiriscope verify <resource>
          kiriscope xp3 list <archive>
          kiriscope xp3 profile <archive> [--hash]
          kiriscope xp3 pack <staging-directory> <new-archive.xp3>
          kiriscope xp3 extract <archive> <output-directory>
          kiriscope xp3 extract <archive> <output-directory> --xor-hex <hex-key>
          kiriscope xp3 extract <archive> <output-directory> --scheme <scheme-json>
          kiriscope unpack <game-directory-or-xp3-or-game.zip> <new-output-directory> [--category all|images|audio|scripts|other] [--knowledge-root <trusted-knowledge-root>]
          kiriscope research package <game-directory> <new-report.json> [--knowledge-root <trusted-knowledge-root>] [--runtime-evidence <existing-report.json> ...]
          kiriscope filter score <input> <scheme-json> [<scheme-json> ...] [--entry <entry-name>] [--adler32 <hex-or-decimal>]
          kiriscope psb profile <psb-or-pimg>
          kiriscope psb extract <psb-or-pimg> <resource-name> <output-file>
          kiriscope psb extract-all <psb-or-pimg> <new-output-directory>
          kiriscope convert bmp-to-png <input-bmp> <output-png>
          kiriscope convert tlg5-to-png <input-tlg> <output-png>
          kiriscope convert batch-to-png <input-directory> <output-directory>
          kiriscope convert tlg-to-png <input-tlg> <output-png> --freemote <EmtConvert.exe>
        """);
    return 1;
}

static async Task<int> AnalyzePeAsync(string inputPath)
{
    if (!File.Exists(inputPath))
    {
        Console.Error.WriteLine($"Input file does not exist: {inputPath}");
        return 2;
    }

    try
    {
        var report = await StaticBinaryAnalyzer.AnalyzeFileAsync(inputPath);
        WriteJson(report);
        return report.Pe is not null && report.Diagnostics.All(static diagnostic => diagnostic.Severity != KiriScope.Core.Diagnostics.DiagnosticSeverity.Error) ? 0 : 3;
    }
    catch (IOException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
    catch (ArgumentException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
}

static async Task<int> AnalyzeDirectoryAsync(string directoryPath)
{
    if (!Directory.Exists(directoryPath))
    {
        Console.Error.WriteLine($"Analysis directory does not exist: {directoryPath}");
        return 2;
    }

    try
    {
        var report = await PluginDirectoryAnalyzer.AnalyzeAsync(directoryPath);
        WriteJson(report);
        return report.Diagnostics.All(static diagnostic => diagnostic.Severity != KiriScope.Core.Diagnostics.DiagnosticSeverity.Error) ? 0 : 3;
    }
    catch (IOException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
    catch (ArgumentException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
}

static async Task<int> ArchiveAnalysisAsync(string inputPath, string outputPath)
{
    if (!File.Exists(inputPath))
    {
        Console.Error.WriteLine($"Input file does not exist: {inputPath}");
        return 2;
    }

    try
    {
        var report = await StaticBinaryAnalyzer.AnalyzeFileAsync(inputPath);
        var archivePath = await ResearchAnalysisArchiveWriter.WriteNewAsync(
            outputPath,
            report,
            $"kiriscope analyze archive \"{Path.GetFullPath(inputPath)}\" \"{Path.GetFullPath(outputPath)}\"");
        WriteJson(new { Input = report.Input, ArchivePath = archivePath, report.Pe, report.Diagnostics });
        return 0;
    }
    catch (IOException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
    catch (ArgumentException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
}

static async Task<int> RunGhidraAsync(string inputPath, string projectDirectory, string projectName, string headlessPath)
{
    try
    {
        var result = await GhidraHeadlessRunner.RunAsync(new GhidraHeadlessRequest(headlessPath, inputPath, projectDirectory, projectName));
        WriteJson(result);
        return result.Succeeded ? 0 : 3;
    }
    catch (IOException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
    catch (ArgumentException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
}

static async Task<int> CaptureRuntimeSnapshotAsync(string processIdText, string outputPath)
{
    if (!int.TryParse(processIdText, out var processId) || processId <= 0)
    {
        Console.Error.WriteLine("Runtime target PID must be a positive integer.");
        return 2;
    }

    try
    {
        var capture = await RuntimeWorkerLauncher.CaptureAsync(new RuntimeCaptureLaunchRequest(processId, ExplicitlyEnabled: true));
        var archivePath = await RuntimeResearchArchiveWriter.WriteNewAsync(
            outputPath,
            new RuntimeProcessResearchArchive(
                RuntimeProcessResearchArchive.CurrentSchemaVersion,
                DateTimeOffset.UtcNow,
                $"kiriscope analyze runtime snapshot {processId} \"{Path.GetFullPath(outputPath)}\" --enable-runtime-capture",
                capture));
        var process = capture.Response?.Process;
        WriteJson(new
        {
            ArchivePath = archivePath,
            capture.Succeeded,
            capture.Request,
            capture.WorkerFile,
            Process = process is null
                ? null
                : new
                {
                    process.ProcessId,
                    process.ProcessName,
                    process.Architecture,
                    process.ExecutablePath,
                    process.ExecutableSha256,
                    ModuleCount = process.Modules.Count,
                    process.ObservedAtUtc,
                },
            capture.Diagnostics,
        });
        return capture.Succeeded ? 0 : 3;
    }
    catch (IOException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
    catch (ArgumentException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
}

static int InspectRuntimeTarget(string processIdText)
{
    if (!int.TryParse(processIdText, out var processId) || processId <= 0)
    {
        Console.Error.WriteLine("Runtime target PID must be a positive integer.");
        return 2;
    }

    var inspection = RuntimeArchitectureInspector.Inspect(processId);
    WriteJson(new
    {
        TargetProcessId = processId,
        inspection.Architecture,
        PlannedAction = "No worker was launched. A later explicit snapshot will read process and module metadata only.",
        inspection.Diagnostics,
    });
    return inspection.Architecture == KiriScope.Worker.Protocol.RuntimeTargetArchitecture.Unknown ? 3 : 0;
}

static async Task<int> ValidateKnowledgeBaseAsync(string rootDirectory)
{
    try
    {
        var knowledgeBase = await KnowledgeBaseLoader.LoadAsync(rootDirectory);
        WriteJson(new
        {
            Succeeded = true,
            knowledgeBase.Id,
            knowledgeBase.DisplayName,
            knowledgeBase.SchemaVersion,
            knowledgeBase.ManifestPath,
            knowledgeBase.ManifestSha256,
            SchemeRevisionCount = knowledgeBase.Schemes.Count,
            CompatibilityEntryCount = knowledgeBase.Compatibility.Count,
        });
        return 0;
    }
    catch (KnowledgeBaseException exception)
    {
        WriteJson(new { Succeeded = false, exception.Code, exception.Message });
        return 3;
    }
    catch (IOException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
}

static async Task<int> ListKnowledgeBaseAsync(string rootDirectory)
{
    try
    {
        var knowledgeBase = await KnowledgeBaseLoader.LoadAsync(rootDirectory);
        WriteJson(new
        {
            knowledgeBase.Id,
            knowledgeBase.DisplayName,
            knowledgeBase.SchemaVersion,
            knowledgeBase.ManifestPath,
            knowledgeBase.ManifestSha256,
            Schemes = knowledgeBase.Schemes.Select(static scheme => new
            {
                scheme.Id,
                scheme.Revision,
                scheme.DisplayName,
                scheme.Status,
                scheme.SchemePath,
                scheme.SchemeSha256,
                scheme.Descriptor.AlgorithmId,
                scheme.Descriptor.AlgorithmVersion,
                scheme.Applicability,
                scheme.Fingerprint,
                scheme.Evidence,
                scheme.Supersedes,
            }),
            knowledgeBase.Compatibility,
        });
        return 0;
    }
    catch (KnowledgeBaseException exception)
    {
        WriteJson(new { Succeeded = false, exception.Code, exception.Message });
        return 3;
    }
    catch (IOException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
}

static async Task<int> MatchKnowledgeBaseAsync(string rootDirectory, string binaryPath)
{
    if (!File.Exists(binaryPath))
    {
        Console.Error.WriteLine($"Binary input does not exist: {binaryPath}");
        return 2;
    }

    try
    {
        var knowledgeBase = await KnowledgeBaseLoader.LoadAsync(rootDirectory);
        var analysis = await StaticBinaryAnalyzer.AnalyzeFileAsync(binaryPath);
        var candidates = KnowledgeFingerprintMatcher.Match(knowledgeBase, analysis);
        WriteJson(new
        {
            KnowledgeBase = new KnowledgeBaseIdentity(knowledgeBase.Id, knowledgeBase.SchemaVersion, knowledgeBase.ManifestSha256),
            analysis.Input,
            PeMachine = analysis.Pe?.Machine,
            Candidates = candidates,
            Message = "Fingerprint matches are heuristic candidates only. No scheme was applied and no compatibility claim was made.",
            analysis.Diagnostics,
        });
        return analysis.Diagnostics.All(static diagnostic => diagnostic.Severity != KiriScope.Core.Diagnostics.DiagnosticSeverity.Error) ? 0 : 3;
    }
    catch (KnowledgeBaseException exception)
    {
        WriteJson(new { Succeeded = false, exception.Code, exception.Message });
        return 3;
    }
    catch (IOException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
}

static async Task<int> ScanKnowledgeBaseAsync(string rootDirectory, string inputDirectory, string outputPath)
{
    if (!Directory.Exists(inputDirectory))
    {
        Console.Error.WriteLine($"Knowledge scan input directory does not exist: {inputDirectory}");
        return 2;
    }

    try
    {
        var knowledgeBase = await KnowledgeBaseLoader.LoadAsync(rootDirectory);
        var report = await KnowledgeBatchScanner.ScanAsync(knowledgeBase, inputDirectory);
        report = report with
        {
            ReproductionCommand = $"kiriscope knowledge scan \"{Path.GetFullPath(rootDirectory)}\" \"{Path.GetFullPath(inputDirectory)}\" \"{Path.GetFullPath(outputPath)}\"",
        };
        var archivePath = await KnowledgeReportArchiveWriter.WriteNewAsync(outputPath, report);
        WriteJson(new
        {
            ArchivePath = archivePath,
            ItemCount = report.Items.Count,
            CandidateCount = report.Items.Sum(static item => item.Candidates.Count),
            report.KnowledgeBase,
            report.Diagnostics,
        });
        return report.Diagnostics.All(static diagnostic => diagnostic.Severity != KiriScope.Core.Diagnostics.DiagnosticSeverity.Error) ? 0 : 3;
    }
    catch (KnowledgeBaseException exception)
    {
        WriteJson(new { Succeeded = false, exception.Code, exception.Message });
        return 3;
    }
    catch (IOException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
    catch (ArgumentException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
}

static async Task<int> CompareKnowledgeReportsAsync(string leftPath, string rightPath, string outputPath)
{
    try
    {
        var report = await KnowledgeBatchReportComparer.CompareFilesAsync(leftPath, rightPath);
        report = report with
        {
            ReproductionCommand = $"kiriscope knowledge compare \"{Path.GetFullPath(leftPath)}\" \"{Path.GetFullPath(rightPath)}\" \"{Path.GetFullPath(outputPath)}\"",
        };
        var archivePath = await KnowledgeReportArchiveWriter.WriteNewAsync(outputPath, report);
        WriteJson(new { ArchivePath = archivePath, DifferenceCount = report.Differences.Count, report.Diagnostics });
        return report.Diagnostics.All(static diagnostic => diagnostic.Severity != KiriScope.Core.Diagnostics.DiagnosticSeverity.Error) ? 0 : 3;
    }
    catch (KnowledgeBaseException exception)
    {
        WriteJson(new { Succeeded = false, exception.Code, exception.Message });
        return 3;
    }
    catch (IOException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
    catch (ArgumentException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
}

static async Task<int> CompareStaticReportsAsync(string leftPath, string rightPath, string outputPath)
{
    try
    {
        var report = await StaticAnalysisReportComparer.CompareFilesAsync(leftPath, rightPath);
        report = report with
        {
            ReproductionCommand = $"kiriscope report compare static \"{Path.GetFullPath(leftPath)}\" \"{Path.GetFullPath(rightPath)}\" \"{Path.GetFullPath(outputPath)}\"",
        };
        var archivePath = await KnowledgeReportArchiveWriter.WriteNewAsync(outputPath, report);
        WriteJson(new
        {
            ArchivePath = archivePath,
            DifferenceCount = report.Differences.Count,
            Message = "Static archive differences are factual deltas only; they do not claim compatibility or decryption success.",
            report.Diagnostics,
        });
        return report.Diagnostics.All(static diagnostic => diagnostic.Severity != KiriScope.Core.Diagnostics.DiagnosticSeverity.Error) ? 0 : 3;
    }
    catch (KnowledgeBaseException exception)
    {
        WriteJson(new { Succeeded = false, exception.Code, exception.Message });
        return 3;
    }
    catch (IOException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
    catch (ArgumentException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
}

static async Task<int> PlanLooseFileOverlayAsync(string referenceDirectory, string overrideDirectory, string outputPath)
{
    if (!Directory.Exists(referenceDirectory) || !Directory.Exists(overrideDirectory))
    {
        Console.Error.WriteLine("Overlay plan requires existing reference and override directories.");
        return 2;
    }

    var referenceRoot = Path.GetFullPath(referenceDirectory);
    var overrideRoot = Path.GetFullPath(overrideDirectory);
    var outputFullPath = Path.GetFullPath(outputPath);
    if (LooseFileOverlayPlanner.IsContainedBy(referenceRoot, outputFullPath) || LooseFileOverlayPlanner.IsContainedBy(overrideRoot, outputFullPath))
    {
        Console.Error.WriteLine("Overlay report output must be outside both input directories to preserve the read-only plan boundary.");
        return 2;
    }

    try
    {
        var report = await LooseFileOverlayPlanner.PlanAsync(referenceRoot, overrideRoot);
        report = report with
        {
            ReproductionCommand = $"kiriscope overlay plan \"{referenceRoot}\" \"{overrideRoot}\" \"{outputFullPath}\"",
        };
        var archivePath = await LooseFileOverlayReportWriter.WriteNewAsync(outputFullPath, report);
        WriteJson(new
        {
            ArchivePath = archivePath,
            ItemCount = report.Items.Count,
            AddedCount = report.Items.Count(static item => item.ChangeKind == LooseFileOverlayChangeKind.Added),
            ReplacedCount = report.Items.Count(static item => item.ChangeKind == LooseFileOverlayChangeKind.Replaced),
            IdenticalCount = report.Items.Count(static item => item.ChangeKind == LooseFileOverlayChangeKind.Identical),
            ConflictCount = report.Items.Count(static item => item.ChangeKind == LooseFileOverlayChangeKind.Conflict),
            Message = "This is a read-only path and hash comparison. It does not deploy files or prove that a target engine honors loose-file overrides.",
            report.Diagnostics,
        });
        return report.Diagnostics.All(static diagnostic => diagnostic.Severity != KiriScope.Core.Diagnostics.DiagnosticSeverity.Error) ? 0 : 3;
    }
    catch (IOException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
    catch (ArgumentException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 2;
    }
}

static async Task<int> ImportProcmonEvidenceAsync(string processIdText, string csvPath, string outputPath)
{
    if (!int.TryParse(processIdText, out var processId) || processId <= 0)
    {
        Console.Error.WriteLine("ProcMon evidence target PID must be a positive integer.");
        return 2;
    }

    if (!File.Exists(csvPath))
    {
        Console.Error.WriteLine($"ProcMon CSV does not exist: {csvPath}");
        return 2;
    }

    try
    {
        var report = await ProcmonCsvImporter.ImportAsync(new RuntimeFileAccessImportRequest(processId, csvPath));
        var archivePath = await RuntimeResearchArchiveWriter.WriteNewAsync(
            outputPath,
            new RuntimeFileAccessResearchArchive(
                RuntimeFileAccessResearchArchive.CurrentSchemaVersion,
                DateTimeOffset.UtcNow,
                $"kiriscope analyze runtime import-procmon {processId} \"{Path.GetFullPath(csvPath)}\" \"{Path.GetFullPath(outputPath)}\" --enable-runtime-capture",
                report));
        WriteJson(new { ArchivePath = archivePath, Succeeded = report.Diagnostics.All(static diagnostic => diagnostic.Severity != KiriScope.Core.Diagnostics.DiagnosticSeverity.Error), report });
        return report.Diagnostics.All(static diagnostic => diagnostic.Severity != KiriScope.Core.Diagnostics.DiagnosticSeverity.Error) ? 0 : 3;
    }
    catch (IOException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
    catch (ArgumentException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
}

static async Task<int> CompareProcmonEvidenceAsync(string processIdText, string leftCsvPath, string rightCsvPath, string outputPath)
{
    if (!int.TryParse(processIdText, out var processId) || processId <= 0)
    {
        Console.Error.WriteLine("ProcMon comparison target PID must be a positive integer.");
        return 2;
    }

    if (!File.Exists(leftCsvPath) || !File.Exists(rightCsvPath))
    {
        Console.Error.WriteLine("Both ProcMon CSV inputs must exist.");
        return 2;
    }

    try
    {
        var left = await ProcmonCsvImporter.ImportAsync(new RuntimeFileAccessImportRequest(processId, leftCsvPath));
        var right = await ProcmonCsvImporter.ImportAsync(new RuntimeFileAccessImportRequest(processId, rightCsvPath));
        var report = RuntimeFileAccessComparer.Compare(left, right);
        var archivePath = await RuntimeResearchArchiveWriter.WriteNewAsync(
            outputPath,
            new RuntimeFileAccessComparisonResearchArchive(
                RuntimeFileAccessComparisonResearchArchive.CurrentSchemaVersion,
                DateTimeOffset.UtcNow,
                $"kiriscope analyze runtime compare-procmon {processId} \"{Path.GetFullPath(leftCsvPath)}\" \"{Path.GetFullPath(rightCsvPath)}\" \"{Path.GetFullPath(outputPath)}\" --enable-runtime-capture",
                report));
        WriteJson(new { ArchivePath = archivePath, Succeeded = true, report.TargetProcessId, report.LeftObservationCount, report.RightObservationCount, DifferenceCount = report.Differences.Count, report.Diagnostics });
        return 0;
    }
    catch (IOException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
    catch (ArgumentException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
}

static async Task<int> ConvertBmpToPngAsync(string bmpPath, string pngPath)
{
    if (!File.Exists(bmpPath)) { Console.Error.WriteLine($"Input file does not exist: {bmpPath}"); return 2; }
    try
    {
        var result = await BmpPngConverter.ConvertAsync(bmpPath, pngPath);
        WriteJson(new { Input = Path.GetFullPath(bmpPath), Output = Path.GetFullPath(pngPath), result.Stage, result.Succeeded, result.BytesWritten, result.Diagnostics });
        return result.Succeeded ? 0 : 3;
    }
    catch (IOException exception) { Console.Error.WriteLine(exception.Message); return 3; }
}

static async Task<int> ConvertTlg5ToPngAsync(string tlgPath, string pngPath)
{
    if (!File.Exists(tlgPath)) { Console.Error.WriteLine($"Input file does not exist: {tlgPath}"); return 2; }
    try
    {
        var result = await Tlg5PngConverter.ConvertAsync(tlgPath, pngPath);
        WriteJson(new { Input = Path.GetFullPath(tlgPath), Output = Path.GetFullPath(pngPath), result.Stage, result.Succeeded, result.BytesWritten, result.Diagnostics });
        return result.Succeeded ? 0 : 3;
    }
    catch (IOException exception) { Console.Error.WriteLine(exception.Message); return 3; }
}

static async Task<int> ConvertDirectoryToPngAsync(string inputDirectory, string outputDirectory)
{
    if (!Directory.Exists(inputDirectory)) { Console.Error.WriteLine($"Input directory does not exist: {inputDirectory}"); return 2; }
    try
    {
        var result = await ResourceBatchPngConverter.ConvertDirectoryAsync(inputDirectory, outputDirectory);
        WriteJson(new { Input = Path.GetFullPath(inputDirectory), Output = Path.GetFullPath(outputDirectory), result.ConvertedCount, result.FailedCount, result.Items });
        return result.FailedCount == 0 ? 0 : 3;
    }
    catch (ArgumentException exception) { Console.Error.WriteLine(exception.Message); return 2; }
    catch (IOException exception) { Console.Error.WriteLine(exception.Message); return 3; }
}

static async Task<int> ConvertTlgWithFreeMoteAsync(string tlgPath, string pngPath, string toolPath)
{
    try
    {
        var result = await FreeMoteTlgConverter.ConvertAsync(tlgPath, pngPath, toolPath);
        WriteJson(new { Input = Path.GetFullPath(tlgPath), Output = Path.GetFullPath(pngPath), result.Stage, result.Succeeded, result.BytesWritten, result.ToolPath, result.ExitCode, result.StandardOutput, result.StandardError, result.Diagnostics });
        return result.Succeeded ? 0 : 3;
    }
    catch (IOException exception) { Console.Error.WriteLine(exception.Message); return 3; }
}

static async Task<int> ExtractPsbResourceAsync(string psbPath, string resourceName, string outputPath)
{
    if (!File.Exists(psbPath)) { Console.Error.WriteLine($"Input file does not exist: {psbPath}"); return 2; }
    try
    {
        var result = await PsbResourceExtractor.ExtractAsync(psbPath, resourceName, outputPath);
        WriteJson(new { Input = Path.GetFullPath(psbPath), Output = Path.GetFullPath(outputPath), result.ResourceName, result.Stage, result.Succeeded, result.BytesWritten, result.Diagnostics });
        return result.Succeeded ? 0 : 3;
    }
    catch (ArgumentException exception) { Console.Error.WriteLine(exception.Message); return 2; }
    catch (IOException exception) { Console.Error.WriteLine(exception.Message); return 3; }
}

static async Task<int> ProfilePsbResourcesAsync(string psbPath)
{
    if (!File.Exists(psbPath)) { Console.Error.WriteLine($"Input file does not exist: {psbPath}"); return 2; }
    try
    {
        await using var input = OpenRead(psbPath);
        var result = await PsbResourceProfiler.ProfileAsync(input);
        WriteJson(new { Input = Path.GetFullPath(psbPath), result.Stage, result.IsPimgCandidate, result.RootUnsignedIntegers, result.Resources, result.Diagnostics });
        return result.Stage >= EvidenceStage.ContainerIdentified ? 0 : 3;
    }
    catch (ArgumentException exception) { Console.Error.WriteLine(exception.Message); return 2; }
    catch (IOException exception) { Console.Error.WriteLine(exception.Message); return 3; }
}

static async Task<int> ExportPsbResourcesAsync(string psbPath, string outputDirectory)
{
    if (!File.Exists(psbPath)) { Console.Error.WriteLine($"Input file does not exist: {psbPath}"); return 2; }
    try
    {
        var result = await PsbResourceExtractor.ExportAllAsync(psbPath, outputDirectory);
        WriteJson(new { Input = Path.GetFullPath(psbPath), result.OutputDirectory, result.Stage, result.Succeeded, result.Resources, result.Diagnostics });
        return result.Succeeded ? 0 : 3;
    }
    catch (ArgumentException exception) { Console.Error.WriteLine(exception.Message); return 2; }
    catch (IOException exception) { Console.Error.WriteLine(exception.Message); return 3; }
}

static async Task<int> ProbeAsync(string filePath)
{
    if (!File.Exists(filePath))
    {
        Console.Error.WriteLine($"Input file does not exist: {filePath}");
        return 2;
    }

    await using var stream = OpenRead(filePath);
    var probe = await Xp3ArchiveProbe.ProbeAsync(stream);
    var sha256 = await Sha256Hasher.ComputeFileAsync(filePath);

    WriteJson(new
    {
        File = Path.GetFullPath(filePath),
        Sha256 = sha256,
        probe.Stage,
        probe.IndexOffset,
        probe.Diagnostics,
    });

    return probe.IsXp3 ? 0 : 3;
}

static async Task<int> ListAsync(string archivePath)
{
    if (!File.Exists(archivePath))
    {
        Console.Error.WriteLine($"Input file does not exist: {archivePath}");
        return 2;
    }

    await using var stream = OpenRead(archivePath);
    var archive = await Xp3ArchiveReader.ReadIndexAsync(stream);
    WriteJson(new
    {
        File = Path.GetFullPath(archivePath),
        archive.Stage,
        archive.IndexOffset,
        archive.IsIndexCompressed,
        EntryCount = archive.Entries.Count,
        archive.Entries,
        archive.Diagnostics,
    });

    return archive.Stage >= KiriScope.Core.Evidence.EvidenceStage.IndexParsed ? 0 : 3;
}

static async Task<int> ProfileXp3Async(string archivePath, bool includeHash)
{
    if (!File.Exists(archivePath))
    {
        Console.Error.WriteLine($"Input file does not exist: {archivePath}");
        return 2;
    }

    try
    {
        Xp3ArchiveIndex index;
        await using (var stream = OpenRead(archivePath))
        {
            index = await Xp3ArchiveReader.ReadIndexAsync(stream);
        }

        var profile = Xp3ArchiveProfile.FromIndex(index);
        WriteJson(new
        {
            Archive = Path.GetFullPath(archivePath),
            Length = new FileInfo(archivePath).Length,
            Sha256 = includeHash ? await Sha256Hasher.ComputeFileAsync(archivePath) : null,
            Profile = profile,
            index.Diagnostics,
            Message = "Profile is derived from the XP3 index only. It does not extract content, apply a filter, or claim decryption success.",
        });
        return profile.Stage >= KiriScope.Core.Evidence.EvidenceStage.IndexParsed ? 0 : 3;
    }
    catch (IOException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
    catch (InvalidDataException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
}

static async Task<int> PackXp3Async(string sourceDirectory, string outputPath)
{
    if (!Directory.Exists(sourceDirectory))
    {
        Console.Error.WriteLine($"XP3 pack source directory does not exist: {sourceDirectory}");
        return 2;
    }

    try
    {
        var result = await Xp3ArchivePacker.PackDirectoryAsync(sourceDirectory, outputPath);
        WriteJson(new
        {
            SourceDirectory = Path.GetFullPath(sourceDirectory),
            result.OutputPath,
            result.ArchiveSha256,
            result.IndexOffset,
            result.ArchiveLength,
            EntryCount = result.Entries.Count,
            result.Entries,
            Message = "Created a new standard unencrypted XP3 archive. The source directory and any existing archive were not modified.",
            result.Diagnostics,
        });
        return 0;
    }
    catch (IOException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
    catch (ArgumentException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 2;
    }
    catch (InvalidOperationException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
}

static async Task<int> ExtractAsync(
    string archivePath,
    string outputDirectory,
    Xp3EntryExtractionOptions? options,
    ContentFilterSchemeDescriptor? scheme)
{
    if (!File.Exists(archivePath))
    {
        Console.Error.WriteLine($"Input file does not exist: {archivePath}");
        return 2;
    }

    var result = await Xp3EntryExtractor.ExtractAllAsync(archivePath, outputDirectory, options);
    WriteJson(new
    {
        Archive = Path.GetFullPath(archivePath),
        OutputDirectory = Path.GetFullPath(outputDirectory),
        result.IndexWasParsed,
        result.ExtractedEntryCount,
        result.SkippedEntryCount,
        ContentFilter = scheme is null ? null : new
        {
            Scheme = scheme,
            Algorithm = options?.ContentFilter?.Descriptor,
        },
        result.Entries,
        result.Diagnostics,
    });

    return result.IndexWasParsed && result.SkippedEntryCount == 0 ? 0 : 3;
}

static async Task<int> UnpackGameAsync(string inputPath, string outputDirectory, ResourceCategory category, string? knowledgeRoot)
{
    try
    {
        var input = GameInput.FromPath(inputPath);
        var resolvedKnowledgeRoot = knowledgeRoot ?? FindBundledKnowledgeRoot();
        var options = resolvedKnowledgeRoot is null
            ? null
            : new GameExtractionOptions { CompatibilityResolver = new KnowledgeGameCompatibilityResolver(resolvedKnowledgeRoot) };
        var result = await GameExtractionService.ExtractAsync(input, category, outputDirectory, options);
        WriteJson(result);
        return result.HasErrors ? 3 : 0;
    }
    catch (FileNotFoundException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 2;
    }
    catch (ArgumentException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 2;
    }
    catch (IOException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
}

static string? FindBundledKnowledgeRoot()
{
    var candidates = new[]
    {
        Path.Combine(AppContext.BaseDirectory, "plugins"),
        Path.Combine(Environment.CurrentDirectory, "plugins"),
    };
    return candidates.FirstOrDefault(candidate => File.Exists(Path.Combine(candidate, KnowledgeBaseLoader.ManifestFileName)));
}

static async Task<int> CreateGameResearchPackageAsync(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("research package requires a game directory and a new report path.");
        return 1;
    }

    var gameDirectory = args[0];
    var outputPath = args[1];
    string? knowledgeRoot = null;
    var runtimeEvidencePaths = new List<string>();
    for (var index = 2; index < args.Length; index++)
    {
        if (string.Equals(args[index], "--knowledge-root", StringComparison.Ordinal))
        {
            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            {
                Console.Error.WriteLine("--knowledge-root requires a directory path.");
                return 1;
            }

            knowledgeRoot = args[index];
            continue;
        }

        if (string.Equals(args[index], "--runtime-evidence", StringComparison.Ordinal))
        {
            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            {
                Console.Error.WriteLine("--runtime-evidence requires an existing report path.");
                return 1;
            }

            runtimeEvidencePaths.Add(args[index]);
            continue;
        }

        Console.Error.WriteLine($"Unknown research package option: {args[index]}");
        return 1;
    }

    try
    {
        var resolvedKnowledgeRoot = knowledgeRoot ?? FindBundledKnowledgeRoot();
        var reproductionArguments = new List<string>
        {
            "kiriscope research package",
            $"\"{Path.GetFullPath(gameDirectory)}\"",
            $"\"{Path.GetFullPath(outputPath)}\"",
        };
        if (!string.IsNullOrWhiteSpace(resolvedKnowledgeRoot))
        {
            reproductionArguments.Add("--knowledge-root");
            reproductionArguments.Add($"\"{Path.GetFullPath(resolvedKnowledgeRoot)}\"");
        }

        foreach (var runtimeEvidencePath in runtimeEvidencePaths)
        {
            reproductionArguments.Add("--runtime-evidence");
            reproductionArguments.Add($"\"{Path.GetFullPath(runtimeEvidencePath)}\"");
        }

        var reproductionCommand = string.Join(' ', reproductionArguments);
        var reportPath = await GameResearchPackageService.CollectAndWriteNewAsync(
            gameDirectory,
            outputPath,
            reproductionCommand,
            new GameResearchPackageOptions
            {
                KnowledgeRoot = resolvedKnowledgeRoot,
                RuntimeEvidencePaths = runtimeEvidencePaths,
            });
        WriteJson(new
        {
            Succeeded = true,
            ReportPath = reportPath,
            InputDirectory = Path.GetFullPath(gameDirectory),
            KnowledgeRoot = resolvedKnowledgeRoot,
            RuntimeEvidenceReferenceCount = runtimeEvidencePaths.Count,
        });
        return 0;
    }
    catch (FileNotFoundException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 2;
    }
    catch (DirectoryNotFoundException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 2;
    }
    catch (ArgumentException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 2;
    }
    catch (IOException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
}

static bool TryParseResourceCategory(string value, out ResourceCategory category)
{
    category = value.ToLowerInvariant() switch
    {
        "all" => ResourceCategory.All,
        "images" or "image" => ResourceCategory.Images,
        "audio" => ResourceCategory.Audio,
        "scripts" or "script" => ResourceCategory.Scripts,
        "other" => ResourceCategory.Other,
        _ => default,
    };
    return value.Equals("all", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("images", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("image", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("audio", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("scripts", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("script", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("other", StringComparison.OrdinalIgnoreCase);
}

static async Task<int> ScoreFilterCandidatesAsync(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("filter score requires an input file and at least one scheme JSON file.");
        return 1;
    }

    var inputPath = args[0];
    if (!File.Exists(inputPath))
    {
        Console.Error.WriteLine($"Input file does not exist: {inputPath}");
        return 2;
    }

    var entryName = Path.GetFileName(inputPath);
    uint? adler32 = null;
    var schemePaths = new List<string>();
    for (var index = 1; index < args.Length; index++)
    {
        if (string.Equals(args[index], "--entry", StringComparison.Ordinal))
        {
            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            {
                Console.Error.WriteLine("--entry requires a non-empty decoded entry name.");
                return 1;
            }

            entryName = args[index];
            continue;
        }

        if (string.Equals(args[index], "--adler32", StringComparison.Ordinal))
        {
            if (++index >= args.Length || !TryParseUInt32(args[index], out var parsedAdler32))
            {
                Console.Error.WriteLine("--adler32 requires an unsigned decimal or 0x-prefixed hexadecimal value.");
                return 1;
            }

            adler32 = parsedAdler32;
            continue;
        }

        schemePaths.Add(args[index]);
    }

    if (schemePaths.Count == 0)
    {
        Console.Error.WriteLine("filter score requires at least one scheme JSON file.");
        return 1;
    }

    var fileLength = new FileInfo(inputPath).Length;
    if (fileLength > ContentFilterCandidatePipeline.DefaultMaximumInputBytes)
    {
        WriteJson(new
        {
            Input = Path.GetFullPath(inputPath),
            Succeeded = false,
            MaximumBytes = ContentFilterCandidatePipeline.DefaultMaximumInputBytes,
            ActualBytes = fileLength,
            Message = "Candidate scoring refused the input before allocating memory; narrow the sample before testing schemes.",
        });
        return 3;
    }

    var candidates = new List<ContentFilterCandidate>();
    var schemeLoadDiagnostics = new List<object>();
    foreach (var schemePath in schemePaths)
    {
        try
        {
            var scheme = BuiltInContentFilterSchemeLoader.Load(schemePath);
            candidates.Add(new ContentFilterCandidate(scheme.Descriptor, scheme.Filter));
        }
        catch (ContentFilterException exception)
        {
            schemeLoadDiagnostics.Add(new
            {
                SchemeFile = Path.GetFullPath(schemePath),
                Succeeded = false,
                exception.Code,
                exception.Message,
            });
        }
        catch (IOException exception)
        {
            schemeLoadDiagnostics.Add(new
            {
                SchemeFile = Path.GetFullPath(schemePath),
                Succeeded = false,
                Code = "FILTER_SCHEME_READ_FAILED",
                exception.Message,
            });
        }
    }

    try
    {
        var ciphertext = await File.ReadAllBytesAsync(inputPath);
        var report = await ContentFilterCandidatePipeline.EvaluateAsync(
            ciphertext,
            new ContentFilterContext(entryName, adler32, 0, 0),
            candidates);
        WriteJson(new
        {
            Input = Path.GetFullPath(inputPath),
            report.EntryName,
            report.Adler32,
            report.CiphertextBytes,
            AcceptedCandidateCount = report.Candidates.Count(static candidate => candidate.IsAccepted),
            report.Candidates,
            SchemeLoadDiagnostics = schemeLoadDiagnostics,
        });
        return report.Candidates.Any(static candidate => candidate.IsAccepted) ? 0 : 3;
    }
    catch (IOException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
    catch (ArgumentException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
}

static async Task<int> VerifyAsync(string resourcePath)
{
    if (!File.Exists(resourcePath))
    {
        Console.Error.WriteLine($"Input file does not exist: {resourcePath}");
        return 2;
    }

    await using var stream = OpenRead(resourcePath);
    var header = new byte[32];
    var read = await stream.ReadAsync(header);
    var format = ResourceFormatDetector.Detect(header.AsSpan(0, read));
    stream.Position = 0;

    if (format == ResourceFormat.Png)
    {
        var result = await PngValidator.ValidateAsync(stream);
        WriteJson(new
        {
            File = Path.GetFullPath(resourcePath),
            Format = format,
            result.Stage,
            result.Width,
            result.Height,
            result.BitDepth,
            result.ColorType,
            result.IdatCompressedBytes,
            result.IdatDecompressedBytes,
            result.Diagnostics,
        });
        return result.IsValid ? 0 : 3;
    }

    if (format == ResourceFormat.Tlg)
    {
        var result = await TlgMetadataReader.ReadAsync(stream);
        WriteJson(new
        {
            File = Path.GetFullPath(resourcePath),
            Format = format,
            result.Stage,
            result.Version,
            result.Width,
            result.Height,
            result.ColorChannels,
            result.DataOffset,
            result.HasSdsWrapper,
            result.Diagnostics,
        });
        return result.IsRecognized ? 0 : 3;
    }

    if (format == ResourceFormat.Bmp)
    {
        var result = await BmpValidator.ValidateAsync(stream);
        WriteJson(new
        {
            File = Path.GetFullPath(resourcePath),
            Format = format,
            result.Stage,
            result.Width,
            result.Height,
            result.BitCount,
            result.Compression,
            result.PixelDataOffset,
            result.PixelDataLength,
            result.Diagnostics,
        });
        return result.IsValid ? 0 : 3;
    }

    if (format == ResourceFormat.Wave)
    {
        var result = await WaveValidator.ValidateAsync(stream);
        WriteJson(new
        {
            File = Path.GetFullPath(resourcePath),
            Format = format,
            result.Stage,
            result.FormatTag,
            result.ChannelCount,
            result.SampleRate,
            result.BitsPerSample,
            result.DataBytes,
            result.Diagnostics,
        });
        return result.IsValid ? 0 : 3;
    }

    if (format == ResourceFormat.Jpeg)
    {
        var result = await JpegValidator.ValidateAsync(stream);
        WriteJson(new
        {
            File = Path.GetFullPath(resourcePath),
            Format = format,
            result.Stage,
            result.Width,
            result.Height,
            result.Precision,
            result.ComponentCount,
            result.ScanCount,
            result.Diagnostics,
        });
        return result.IsValid ? 0 : 3;
    }

    if (format == ResourceFormat.Psb)
    {
        var result = await PsbHeaderReader.ReadAsync(stream);
        stream.Position = 0;
        var structure = await PsbStructureProbe.ProbeAsync(stream);
        var rootResources = structure.RootResources.Select(reference => new { Name = structure.RootKeys[reference.RootKeyIndex], reference.ResourceIndex, reference.Offset, reference.Length });
        var rootUnsignedIntegers = structure.RootUnsignedIntegers.Select(value => new { Name = structure.RootKeys[value.RootKeyIndex], value.Value });
        WriteJson(new { File = Path.GetFullPath(resourcePath), Format = format, result.Stage, result.Version, result.HeaderMayBeEncrypted, result.HeaderLength, result.NamesOffset, result.EntriesOffset, result.ChunkOffsetsTableOffset, result.ChunkLengthsTableOffset, result.ChunkDataOffset, result.Diagnostics, structure.IsPimgCandidate, structure.RootKeys, RootResources = rootResources, RootUnsignedIntegers = rootUnsignedIntegers, StructureDiagnostics = structure.Diagnostics });
        return result.IsRecognized ? 0 : 3;
    }

    WriteJson(new
    {
        File = Path.GetFullPath(resourcePath),
        Format = format,
        Stage = "RawDataExtracted",
        Message = "Format detection completed, but a structural validator is not available for this format yet.",
    });
    return format == ResourceFormat.Unknown ? 3 : 0;
}

static FileStream OpenRead(string filePath) =>
    new(
        filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
        bufferSize: 1024 * 128,
        options: FileOptions.Asynchronous | FileOptions.SequentialScan);

static void WriteJson<T>(T value) =>
    Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    }));

static bool TryCreateXorFilter(string hexKey, out RepeatingXorContentFilter? filter)
{
    filter = null;
    if (string.IsNullOrWhiteSpace(hexKey) || hexKey.Length % 2 != 0)
    {
        return false;
    }

    try
    {
        var key = Convert.FromHexString(hexKey);
        if (key.Length == 0)
        {
            return false;
        }

        filter = new RepeatingXorContentFilter(key);
        return true;
    }
    catch (FormatException)
    {
        return false;
    }
}

static bool TryParseUInt32(string value, out uint parsed)
{
    if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
    {
        return uint.TryParse(value[2..], System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out parsed);
    }

    return uint.TryParse(value, out parsed);
}
