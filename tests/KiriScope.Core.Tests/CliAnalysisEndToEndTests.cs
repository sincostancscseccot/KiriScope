using System.Diagnostics;
using System.Text.Json;
using KiriScope.Analysis;

namespace KiriScope.Core.Tests;

public sealed class CliAnalysisEndToEndTests
{
    [Fact]
    public async Task Version_ReportsTheAssemblyInformationalVersion()
    {
        var result = await RunCliAsync("version");

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.Matches("^KiriScope \\S+", result.StandardOutput.Trim());
        Assert.DoesNotContain("0.0.1-dev", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnalyzePe_ReportsPeFactsAndHeuristicCandidatesSeparately()
    {
        var result = await RunCliAsync($"analyze pe \"{typeof(StaticBinaryAnalyzer).Assembly.Location}\"");

        Assert.True(result.ExitCode == 0, result.StandardError);
        using var report = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(typeof(StaticBinaryAnalyzer).Assembly.Location, report.RootElement.GetProperty("Input").GetProperty("FullPath").GetString());
        Assert.Equal("x86", report.RootElement.GetProperty("Pe").GetProperty("Machine").GetString());
        Assert.Contains(report.RootElement.GetProperty("Findings").EnumerateArray(), static finding =>
            finding.GetProperty("Kind").GetString() == "ObservedFact" && finding.GetProperty("Id").GetString() == "PE_HEADER_PARSED");
        Assert.Contains(report.RootElement.GetProperty("Findings").EnumerateArray(), static finding =>
            finding.GetProperty("Kind").GetString() == "HeuristicCandidate" && finding.GetProperty("Id").GetString() == "HEURISTIC_CX_ENCRYPTION");
    }

    [Fact]
    public async Task AnalyzeArchive_WritesANewReproductionRecord()
    {
        var directory = Path.Combine(Path.GetTempPath(), "KiriScope.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var archivePath = Path.Combine(directory, "analysis.json");
            var result = await RunCliAsync($"analyze archive \"{typeof(StaticBinaryAnalyzer).Assembly.Location}\" \"{archivePath}\"");

            Assert.True(result.ExitCode == 0, result.StandardError);
            Assert.True(File.Exists(archivePath));
            using var archive = JsonDocument.Parse(await File.ReadAllTextAsync(archivePath));
            Assert.Equal("1.0", archive.RootElement.GetProperty("SchemaVersion").GetString());
            Assert.Contains("kiriscope analyze archive", archive.RootElement.GetProperty("ReproductionCommand").GetString());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AnalyzeRuntimeSnapshot_RequiresExplicitEnablementAndWritesAnIsolatedCaptureArchive()
    {
        var directory = Path.Combine(Path.GetTempPath(), "KiriScope.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var archivePath = Path.Combine(directory, "runtime.json");
            var inspection = await RunCliAsync($"analyze runtime inspect {Environment.ProcessId}");
            Assert.True(inspection.ExitCode == 0, inspection.StandardError);
            using (var inspectionResponse = JsonDocument.Parse(inspection.StandardOutput))
            {
                Assert.Equal(Environment.ProcessId, inspectionResponse.RootElement.GetProperty("TargetProcessId").GetInt32());
                Assert.NotEqual("Unknown", inspectionResponse.RootElement.GetProperty("Architecture").GetString());
            }

            var disabled = await RunCliAsync($"analyze runtime snapshot {Environment.ProcessId} \"{archivePath}\"");
            Assert.Equal(2, disabled.ExitCode);
            Assert.False(File.Exists(archivePath));

            var enabled = await RunCliAsync($"analyze runtime snapshot {Environment.ProcessId} \"{archivePath}\" --enable-runtime-capture");
            Assert.True(enabled.ExitCode == 0, enabled.StandardError);
            using var response = JsonDocument.Parse(enabled.StandardOutput);
            Assert.True(response.RootElement.GetProperty("Succeeded").GetBoolean());
            Assert.Equal(Environment.ProcessId, response.RootElement.GetProperty("Process").GetProperty("ProcessId").GetInt32());
            Assert.True(File.Exists(archivePath));
            using var archive = JsonDocument.Parse(await File.ReadAllTextAsync(archivePath));
            Assert.Equal("1.0", archive.RootElement.GetProperty("SchemaVersion").GetString());
            Assert.True(archive.RootElement.GetProperty("Capture").GetProperty("Succeeded").GetBoolean());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AnalyzeRuntimeCompareProcmon_WritesAnOfflineDifferenceArchive()
    {
        var directory = Path.Combine(Path.GetTempPath(), "KiriScope.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var leftPath = Path.Combine(directory, "left.csv");
            var rightPath = Path.Combine(directory, "right.csv");
            var archivePath = Path.Combine(directory, "comparison.json");
            await File.WriteAllTextAsync(leftPath, "Time of Day,Process Name,PID,Operation,Path,Result,Detail\r\n10:00,game.exe,42,CreateFile,C:\\game.xp3,SUCCESS,\r\n");
            await File.WriteAllTextAsync(rightPath, "Time of Day,Process Name,PID,Operation,Path,Result,Detail\r\n10:00,game.exe,42,CreateFile,C:\\game.xp3,SUCCESS,\r\n10:01,game.exe,42,ReadFile,C:\\game.xp3,SUCCESS,\r\n");

            var result = await RunCliAsync($"analyze runtime compare-procmon 42 \"{leftPath}\" \"{rightPath}\" \"{archivePath}\" --enable-runtime-capture");

            Assert.True(result.ExitCode == 0, result.StandardError);
            using var response = JsonDocument.Parse(result.StandardOutput);
            Assert.Equal(1, response.RootElement.GetProperty("DifferenceCount").GetInt32());
            using var archive = JsonDocument.Parse(await File.ReadAllTextAsync(archivePath));
            Assert.Equal("1.0", archive.RootElement.GetProperty("SchemaVersion").GetString());
            Assert.Equal(1, archive.RootElement.GetProperty("Report").GetProperty("Differences").GetArrayLength());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Xp3Pack_CreatesANewArchiveWithoutWritingIntoTheStagingDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "KiriScope.Tests", Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(directory, "staging");
        var archivePath = Path.Combine(directory, "packed.xp3");
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(staging, "sample.txt"), "sample");
        try
        {
            var result = await RunCliAsync($"xp3 pack \"{staging}\" \"{archivePath}\"");

            Assert.True(result.ExitCode == 0, result.StandardError);
            Assert.Equal("sample", await File.ReadAllTextAsync(Path.Combine(staging, "sample.txt")));
            using var response = JsonDocument.Parse(result.StandardOutput);
            Assert.Equal(1, response.RootElement.GetProperty("EntryCount").GetInt32());
            Assert.True(File.Exists(archivePath));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task OverlayPlan_WritesAReadOnlyNewReport()
    {
        var directory = Path.Combine(Path.GetTempPath(), "KiriScope.Tests", Guid.NewGuid().ToString("N"));
        var reference = Path.Combine(directory, "reference");
        var overrides = Path.Combine(directory, "overrides");
        var reportPath = Path.Combine(directory, "reports", "overlay.json");
        Directory.CreateDirectory(reference);
        Directory.CreateDirectory(overrides);
        await File.WriteAllTextAsync(Path.Combine(reference, "item.txt"), "before");
        await File.WriteAllTextAsync(Path.Combine(overrides, "item.txt"), "after");
        try
        {
            var result = await RunCliAsync($"overlay plan \"{reference}\" \"{overrides}\" \"{reportPath}\"");

            Assert.True(result.ExitCode == 0, result.StandardError);
            Assert.Equal("before", await File.ReadAllTextAsync(Path.Combine(reference, "item.txt")));
            using var response = JsonDocument.Parse(result.StandardOutput);
            Assert.Equal(1, response.RootElement.GetProperty("ReplacedCount").GetInt32());
            using var archive = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
            Assert.Contains("kiriscope overlay plan", archive.RootElement.GetProperty("ReproductionCommand").GetString());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Xp3Profile_ReportsCompactIndexStatisticsWithoutExtractingContent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "KiriScope.Tests", Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(directory, "staging");
        var archivePath = Path.Combine(directory, "packed.xp3");
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(staging, "sample.tjs"), "return;");
        try
        {
            var packed = await RunCliAsync($"xp3 pack \"{staging}\" \"{archivePath}\"");
            Assert.True(packed.ExitCode == 0, packed.StandardError);

            var profile = await RunCliAsync($"xp3 profile \"{archivePath}\"");
            Assert.True(profile.ExitCode == 0, profile.StandardError);
            using var response = JsonDocument.Parse(profile.StandardOutput);
            Assert.Equal(1, response.RootElement.GetProperty("Profile").GetProperty("EntryCount").GetInt32());
            Assert.Equal(0, response.RootElement.GetProperty("Profile").GetProperty("EncryptedEntryCount").GetInt32());
            Assert.Equal(".tjs", response.RootElement.GetProperty("Profile").GetProperty("Extensions")[0].GetProperty("Extension").GetString());
            Assert.Equal("return;", await File.ReadAllTextAsync(Path.Combine(staging, "sample.tjs")));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PsbExtractAll_CreatesANewOutputDirectoryOutsideTheInputTree()
    {
        var (psb, expectedResource) = PsbStructureProbeTests.CreatePsb();
        var directory = Path.Combine(Path.GetTempPath(), "KiriScope.Tests", Guid.NewGuid().ToString("N"));
        var inputDirectory = Path.Combine(directory, "input");
        var psbPath = Path.Combine(inputDirectory, "sample.pimg");
        var outputDirectory = Path.Combine(directory, "exported");
        Directory.CreateDirectory(inputDirectory);
        try
        {
            await File.WriteAllBytesAsync(psbPath, psb);

            var result = await RunCliAsync($"psb extract-all \"{psbPath}\" \"{outputDirectory}\"");

            Assert.True(result.ExitCode == 0, result.StandardError);
            using var response = JsonDocument.Parse(result.StandardOutput);
            Assert.True(response.RootElement.GetProperty("Succeeded").GetBoolean());
            var exported = Assert.Single(response.RootElement.GetProperty("Resources").EnumerateArray());
            var outputFile = exported.GetProperty("OutputFile").GetString();
            Assert.NotNull(outputFile);
            Assert.Equal(expectedResource, await File.ReadAllBytesAsync(outputFile!));
            Assert.Equal(psb, await File.ReadAllBytesAsync(psbPath));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PsbProfile_ReportsDirectResourcesWithoutCreatingOutput()
    {
        var (psb, _) = PsbStructureProbeTests.CreatePsb();
        var directory = Path.Combine(Path.GetTempPath(), "KiriScope.Tests", Guid.NewGuid().ToString("N"));
        var psbPath = Path.Combine(directory, "sample.pimg");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllBytesAsync(psbPath, psb);

            var result = await RunCliAsync($"psb profile \"{psbPath}\"");

            Assert.True(result.ExitCode == 0, result.StandardError);
            using var response = JsonDocument.Parse(result.StandardOutput);
            Assert.True(response.RootElement.GetProperty("IsPimgCandidate").GetBoolean());
            var resource = Assert.Single(response.RootElement.GetProperty("Resources").EnumerateArray());
            Assert.Equal("asset", resource.GetProperty("ResourceName").GetString());
            Assert.Equal("Unknown", resource.GetProperty("DetectedFormat").GetString());
            Assert.Equal(psb, await File.ReadAllBytesAsync(psbPath));
            Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(directory), path => !string.Equals(path, psbPath, StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

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
