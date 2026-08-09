using System.Buffers.Binary;
using System.Text.Json;
using KiriScope.Analysis;

namespace KiriScope.Core.Tests;

public sealed class StaticBinaryAnalyzerTests
{
    [Fact]
    public async Task AnalyzeFileAsync_ParsesTheCurrentManagedAssemblyWithoutLoadingItAsCode()
    {
        var report = await StaticBinaryAnalyzer.AnalyzeFileAsync(typeof(StaticBinaryAnalyzer).Assembly.Location);

        Assert.NotNull(report.Pe);
        Assert.NotEmpty(report.Pe!.Sections);
        Assert.Contains(report.Findings, static finding => finding.Kind == AnalysisFindingKind.ObservedFact && finding.Id == "PE_HEADER_PARSED");
    }

    [Fact]
    public void Analyze_SeparatesObservedConstantsFromACxEncryptionCandidate()
    {
        var data = new List<byte>();
        data.AddRange(" Encryption control block\0"u8.ToArray());
        data.AddRange(BitConverter.GetBytes(0xAAAAAAAAU));
        data.AddRange(BitConverter.GetBytes(0x55555555U));
        data.AddRange(BitConverter.GetBytes(0x41C64E6DU));
        var report = StaticBinaryAnalyzer.Analyze(
            new AnalysisInputIdentity("synthetic.bin", "00", data.Count),
            data.ToArray());

        Assert.Contains(report.Findings, static finding => finding.Kind == AnalysisFindingKind.ObservedFact && finding.Id == "CX_CONSTANT_AAAAAAAA");
        var candidate = Assert.Single(report.Findings, static finding => finding.Id == "HEURISTIC_CX_ENCRYPTION");
        Assert.Equal(AnalysisFindingKind.HeuristicCandidate, candidate.Kind);
        Assert.True(candidate.Score >= 80);
    }

    [Fact]
    public void Analyze_CapsRepeatedMalformedImportDiagnosticsWhileRetainingTheirCause()
    {
        var data = new byte[0x1000];
        "MZ"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x3C), 0x80);
        "PE\0\0"u8.CopyTo(data.AsSpan(0x80));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x84), 0x14C);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x86), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x94), 0xE0);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x98), 0x10B);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0xD4), 0x200);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x100), 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x104), 20 * 40);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x180), 0xE00);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x184), 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x188), 0xE00);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x18C), 0x200);
        for (var index = 0; index < 40; index++)
        {
            var descriptor = data.AsSpan(0x200 + (index * 20), 20);
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor, 1);
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor[12..], 0x4000);
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor[16..], 1);
        }

        var report = StaticBinaryAnalyzer.Analyze(new AnalysisInputIdentity("malformed-imports.exe", "00", data.Length), data);

        Assert.Equal(32, report.Diagnostics.Count(static diagnostic => diagnostic.Code == "PE_IMPORT_NAME_OUT_OF_RANGE"));
        Assert.Contains(report.Diagnostics, static diagnostic => diagnostic.Code == "PE_IMPORT_NAME_OUT_OF_RANGE_CAPPED");
    }

    [Fact]
    public async Task ResearchArchiveWriter_WritesANewTraceableArchiveWithoutOverwriting()
    {
        var directory = Path.Combine(Path.GetTempPath(), "KiriScope.Tests", Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(directory, "analysis.json");
        Directory.CreateDirectory(directory);
        try
        {
            var report = StaticBinaryAnalyzer.Analyze(
                new AnalysisInputIdentity("synthetic.bin", "00", 4),
                new byte[] { 1, 2, 3, 4 });

            var writtenPath = await ResearchAnalysisArchiveWriter.WriteNewAsync(archivePath, report, "kiriscope analyze archive synthetic.bin analysis.json");

            Assert.Equal(Path.GetFullPath(archivePath), writtenPath);
            using var archive = JsonDocument.Parse(await File.ReadAllTextAsync(archivePath));
            Assert.Equal("1.0", archive.RootElement.GetProperty("SchemaVersion").GetString());
            Assert.Equal("00", archive.RootElement.GetProperty("Report").GetProperty("Input").GetProperty("Sha256").GetString());
            await Assert.ThrowsAsync<IOException>(async () => await ResearchAnalysisArchiveWriter.WriteNewAsync(archivePath, report, "repeat"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PluginDirectoryAnalyzer_ReportsTheDiscoveredManagedAssembly()
    {
        var directory = Path.Combine(Path.GetTempPath(), "KiriScope.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var targetPath = Path.Combine(directory, "analysis.dll");
            File.Copy(typeof(StaticBinaryAnalyzer).Assembly.Location, targetPath);

            var report = await PluginDirectoryAnalyzer.AnalyzeAsync(directory);

            Assert.Single(report.Binaries);
            Assert.Equal(Path.GetFullPath(targetPath), report.Binaries[0].Input.FullPath);
            Assert.NotNull(report.Binaries[0].Pe);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
