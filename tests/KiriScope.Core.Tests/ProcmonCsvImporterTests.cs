using KiriScope.Runtime;

namespace KiriScope.Core.Tests;

public sealed class ProcmonCsvImporterTests
{
    [Fact]
    public async Task ImportAsync_RetainsOnlyMatchingFileSystemObservationsAndKeepsTheSourceUntouched()
    {
        var directory = Path.Combine(Path.GetTempPath(), "KiriScope.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var csvPath = Path.Combine(directory, "events.csv");
            const string csv = "Time of Day,Process Name,PID,Operation,Path,Result,Detail\r\n" +
                "10:00:00.0000000,game.exe,42,CreateFile,\"C:\\game,data.xp3\",SUCCESS,Desired Access: Read\r\n" +
                "10:00:00.1000000,game.exe,42,RegQueryKey,HKCU\\Software,SUCCESS,\r\n" +
                "10:00:00.2000000,other.exe,43,ReadFile,C:\\other.bin,SUCCESS,\r\n";
            await File.WriteAllTextAsync(csvPath, csv);
            var original = await File.ReadAllTextAsync(csvPath);

            var report = await ProcmonCsvImporter.ImportAsync(new RuntimeFileAccessImportRequest(42, csvPath));

            var observation = Assert.Single(report.Observations);
            Assert.Equal("CreateFile", observation.Operation);
            Assert.Equal("C:\\game,data.xp3", observation.Path);
            Assert.Equal(42, observation.ProcessId);
            Assert.NotEmpty(report.Source.Sha256);
            Assert.Equal(original, await File.ReadAllTextAsync(csvPath));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ImportAsync_ReportsMissingRequiredColumns()
    {
        var directory = Path.Combine(Path.GetTempPath(), "KiriScope.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var csvPath = Path.Combine(directory, "invalid.csv");
            await File.WriteAllTextAsync(csvPath, "PID,Path\r\n42,C:\\game.xp3\r\n");

            var report = await ProcmonCsvImporter.ImportAsync(new RuntimeFileAccessImportRequest(42, csvPath));

            Assert.Empty(report.Observations);
            Assert.Contains(report.Diagnostics, static diagnostic => diagnostic.Code == "RUNTIME_PROCMON_CSV_COLUMNS_MISSING");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Compare_ReportsOnlyOfflineCountDifferences()
    {
        var source = new RuntimeExternalEvidenceSource("source.csv", "00", 1);
        var left = new RuntimeFileAccessReport(
            source,
            42,
            DateTimeOffset.UtcNow,
            new Dictionary<string, int>(),
            [new RuntimeFileAccessEvidence(2, "10:00", "game.exe", 42, "CreateFile", "C:\\game.xp3", "SUCCESS", string.Empty)],
            Array.Empty<KiriScope.Core.Diagnostics.KiriScopeDiagnostic>());
        var right = left with
        {
            Observations =
            [
                new RuntimeFileAccessEvidence(2, "10:00", "game.exe", 42, "CreateFile", "C:\\game.xp3", "SUCCESS", string.Empty),
                new RuntimeFileAccessEvidence(3, "10:01", "game.exe", 42, "ReadFile", "C:\\game.xp3", "SUCCESS", string.Empty),
            ],
        };

        var comparison = RuntimeFileAccessComparer.Compare(left, right);

        var difference = Assert.Single(comparison.Differences);
        Assert.Equal("ReadFile", difference.Operation);
        Assert.Equal(0, difference.LeftCount);
        Assert.Equal(1, difference.RightCount);
    }
}
