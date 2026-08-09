using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using KiriScope.Analysis;
using KiriScope.Core.Diagnostics;
using KiriScope.Knowledge;

namespace KiriScope.Core.Tests;

public sealed class KnowledgeBaseTests
{
    [Fact]
    public async Task LoaderAndMatcher_BindASchemeRevisionAndEmitOnlyHeuristicCandidates()
    {
        var root = await CreateKnowledgeBaseAsync(requiredStrings: ["fingerprint-marker"]);
        try
        {
            var knowledgeBase = await KnowledgeBaseLoader.LoadAsync(root);
            var data = "fingerprint-marker\0CxEncryption\0"u8.ToArray();
            var report = StaticBinaryAnalyzer.Analyze(new AnalysisInputIdentity("synthetic.dll", "00", data.Length), data);

            var candidate = Assert.Single(KnowledgeFingerprintMatcher.Match(knowledgeBase, report));
            Assert.Equal("reference.repeating-xor.a55a", candidate.SchemeId);
            Assert.Equal("1.0.0", candidate.SchemeRevision);
            Assert.Equal(AnalysisFindingKind.HeuristicCandidate, candidate.Kind);
            Assert.Contains("fingerprint-marker", candidate.MatchedEvidence.Single());

            await File.AppendAllTextAsync(Path.Combine(root, "schemes", "reference.scheme.json"), "\n");
            var exception = await Assert.ThrowsAsync<KnowledgeBaseException>(async () => await KnowledgeBaseLoader.LoadAsync(root));
            Assert.Equal("KNOWLEDGE_SCHEME_HASH_MISMATCH", exception.Code);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Matcher_DoesNotTreatAnExistingHeuristicFindingAsFingerprintEvidence()
    {
        var root = await CreateKnowledgeBaseAsync(requiredFindingIds: ["HEURISTIC_CX_ENCRYPTION"]);
        try
        {
            var knowledgeBase = await KnowledgeBaseLoader.LoadAsync(root);
            var data = "Encryption control block\0CxEncryption\0"u8.ToArray();
            var report = StaticBinaryAnalyzer.Analyze(new AnalysisInputIdentity("synthetic.dll", "00", data.Length), data);

            Assert.Contains(report.Findings, static finding => finding.Id == "HEURISTIC_CX_ENCRYPTION" && finding.Kind == AnalysisFindingKind.HeuristicCandidate);
            Assert.Empty(KnowledgeFingerprintMatcher.Match(knowledgeBase, report));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Loader_RejectsVerifiedEntriesWithoutFormatValidationEvidence()
    {
        var root = await CreateKnowledgeBaseAsync(status: KnowledgeCompatibilityStatus.Verified);
        try
        {
            var exception = await Assert.ThrowsAsync<KnowledgeBaseException>(async () => await KnowledgeBaseLoader.LoadAsync(root));
            Assert.Equal("KNOWLEDGE_SCHEME_VERIFICATION_EVIDENCE_REQUIRED", exception.Code);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task BatchScanAndComparison_AreReadOnlyStableAndNeverOverwriteArchives()
    {
        var root = await CreateKnowledgeBaseAsync(requiredStrings: ["fingerprint-marker"]);
        var input = Path.Combine(root, "input");
        var reportPath = Path.Combine(root, "reports", "scan.json");
        Directory.CreateDirectory(input);
        var binaryPath = Path.Combine(input, "candidate.dll");
        var original = "fingerprint-marker\0"u8.ToArray();
        await File.WriteAllBytesAsync(binaryPath, original);
        try
        {
            var knowledgeBase = await KnowledgeBaseLoader.LoadAsync(root);
            var report = await KnowledgeBatchScanner.ScanAsync(knowledgeBase, input);
            var item = Assert.Single(report.Items);
            Assert.Equal("candidate.dll", item.RelativePath);
            Assert.Single(item.Candidates);
            Assert.Equal(original, await File.ReadAllBytesAsync(binaryPath));

            await KnowledgeReportArchiveWriter.WriteNewAsync(reportPath, report);
            await Assert.ThrowsAsync<IOException>(async () => await KnowledgeReportArchiveWriter.WriteNewAsync(reportPath, report));

            var changedItem = item with { Sha256 = new string('f', 64) };
            var changedReport = report with { Items = [changedItem] };
            var comparison = KnowledgeBatchReportComparer.Compare("left.json", report, "right.json", changedReport);
            var difference = Assert.Single(comparison.Differences);
            Assert.Equal("Changed", difference.ChangeKind);
            Assert.Equal(item.Sha256, difference.LeftSha256);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task CliKnowledgeCommands_ValidateScanAndCompareWithoutApplyingAScheme()
    {
        var root = await CreateKnowledgeBaseAsync(requiredStrings: ["fingerprint-marker"]);
        var input = Path.Combine(root, "input");
        var scanPath = Path.Combine(root, "reports", "scan.json");
        var comparisonPath = Path.Combine(root, "reports", "comparison.json");
        Directory.CreateDirectory(input);
        await File.WriteAllBytesAsync(Path.Combine(input, "candidate.dll"), "fingerprint-marker\0"u8.ToArray());
        try
        {
            var validation = await RunCliAsync($"knowledge validate \"{root}\"");
            Assert.True(validation.ExitCode == 0, validation.StandardError);
            using (var validationJson = JsonDocument.Parse(validation.StandardOutput))
            {
                Assert.True(validationJson.RootElement.GetProperty("Succeeded").GetBoolean());
                Assert.Equal(1, validationJson.RootElement.GetProperty("SchemeRevisionCount").GetInt32());
            }

            var scan = await RunCliAsync($"knowledge scan \"{root}\" \"{input}\" \"{scanPath}\"");
            Assert.True(scan.ExitCode == 0, scan.StandardError);
            using (var scanJson = JsonDocument.Parse(scan.StandardOutput))
            {
                Assert.Equal(1, scanJson.RootElement.GetProperty("CandidateCount").GetInt32());
            }

            var comparison = await RunCliAsync($"knowledge compare \"{scanPath}\" \"{scanPath}\" \"{comparisonPath}\"");
            Assert.True(comparison.ExitCode == 0, comparison.StandardError);
            using var comparisonJson = JsonDocument.Parse(comparison.StandardOutput);
            Assert.Equal(0, comparisonJson.RootElement.GetProperty("DifferenceCount").GetInt32());
            using var archive = JsonDocument.Parse(await File.ReadAllTextAsync(comparisonPath));
            Assert.Equal("1.0", archive.RootElement.GetProperty("SchemaVersion").GetString());
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task StaticArchiveComparison_ReportsFactualPeAndFindingDeltas()
    {
        var root = Path.Combine(Path.GetTempPath(), "KiriScope.Tests", Guid.NewGuid().ToString("N"));
        var leftPath = Path.Combine(root, "left-static.json");
        var rightPath = Path.Combine(root, "right-static.json");
        var comparisonPath = Path.Combine(root, "comparison.json");
        Directory.CreateDirectory(root);
        try
        {
            var left = CreateStaticReport("a", "x86", ["kernel32.dll"], [new StaticAnalysisFinding(AnalysisFindingKind.ObservedFact, "LEFT_FACT", "left fact")]);
            var right = CreateStaticReport("b", "x64", ["user32.dll"], [new StaticAnalysisFinding(AnalysisFindingKind.HeuristicCandidate, "RIGHT_CANDIDATE", "right candidate")]);
            await ResearchAnalysisArchiveWriter.WriteNewAsync(leftPath, left, "left reproduction");
            await ResearchAnalysisArchiveWriter.WriteNewAsync(rightPath, right, "right reproduction");

            var report = await StaticAnalysisReportComparer.CompareFilesAsync(leftPath, rightPath);
            Assert.Contains(report.Differences, static difference => difference.Category == "PE import" && difference.ChangeKind == "Added" && difference.Identifier == "user32.dll");
            Assert.Contains(report.Differences, static difference => difference.Category == "Finding" && difference.FindingKind == AnalysisFindingKind.HeuristicCandidate && difference.Identifier == "RIGHT_CANDIDATE");

            var result = await RunCliAsync($"report compare static \"{leftPath}\" \"{rightPath}\" \"{comparisonPath}\"");
            Assert.True(result.ExitCode == 0, result.StandardError);
            using var output = JsonDocument.Parse(result.StandardOutput);
            Assert.True(output.RootElement.GetProperty("DifferenceCount").GetInt32() >= 5);
            using var archive = JsonDocument.Parse(await File.ReadAllTextAsync(comparisonPath));
            Assert.Equal("1.0", archive.RootElement.GetProperty("SchemaVersion").GetString());
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static async Task<string> CreateKnowledgeBaseAsync(
        IReadOnlyList<string>? requiredStrings = null,
        IReadOnlyList<string>? requiredFindingIds = null,
        KnowledgeCompatibilityStatus status = KnowledgeCompatibilityStatus.Candidate)
    {
        var root = Path.Combine(Path.GetTempPath(), "KiriScope.Tests", Guid.NewGuid().ToString("N"));
        var schemes = Path.Combine(root, "schemes");
        Directory.CreateDirectory(schemes);
        var schemePath = Path.Combine(schemes, "reference.scheme.json");
        await File.WriteAllTextAsync(schemePath, """
            {
              "id": "reference.repeating-xor.a55a",
              "displayName": "Reference repeating XOR (A5 5A)",
              "algorithmId": "builtin.repeating-xor",
              "algorithmVersion": "1.0",
              "parameterSource": {
                "kind": "test",
                "reference": "synthetic",
                "notes": "Synthetic test scheme."
              },
              "parameters": {
                "keyHex": "A55A"
              }
            }
            """);
        var schemeSha256 = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(schemePath)));
        var fingerprint = requiredStrings is null && requiredFindingIds is null
            ? null
            : new AlgorithmFingerprint("synthetic.direct-observation", RequiredStrings: requiredStrings, RequiredFindingIds: requiredFindingIds);
        var manifest = new KnowledgeBaseDocument(
            KnowledgeBaseLoader.CurrentSchemaVersion,
            "synthetic.knowledge-base",
            "Synthetic knowledge base",
            [new KnowledgeSchemeDocument(
                "reference.repeating-xor.a55a",
                "1.0.0",
                "Synthetic reference scheme",
                "schemes/reference.scheme.json",
                schemeSha256,
                "builtin.repeating-xor",
                "1.0",
                status,
                Fingerprint: fingerprint,
                Evidence: Array.Empty<KnowledgeVerificationEvidence>())],
            Array.Empty<KnowledgeCompatibilityEntry>());
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        await File.WriteAllTextAsync(Path.Combine(root, KnowledgeBaseLoader.ManifestFileName), JsonSerializer.Serialize(manifest, options));
        return root;
    }

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static StaticBinaryAnalysisReport CreateStaticReport(
        string sha256Suffix,
        string machine,
        IReadOnlyList<string> imports,
        IReadOnlyList<StaticAnalysisFinding> findings) => new(
            new AnalysisInputIdentity("synthetic.dll", new string(sha256Suffix[0], 64), 42),
            new PeMetadata(machine, 0, machine == "x64", 0, Array.Empty<PeSectionInfo>(), imports),
            Array.Empty<BinaryStringFinding>(),
            findings,
            Array.Empty<KiriScopeDiagnostic>());

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunCliAsync(string arguments)
    {
        var cliPath = Path.Combine(AppContext.BaseDirectory, "KiriScope.Cli.dll");
        Assert.True(File.Exists(cliPath), $"CLI assembly was not copied to the test output: {cliPath}");
        using var process = Process.Start(new ProcessStartInfo("dotnet")
        {
            Arguments = $"\"{cliPath}\" {arguments}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        });
        Assert.NotNull(process);
        var standardOutput = await process!.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, standardOutput, standardError);
    }
}
