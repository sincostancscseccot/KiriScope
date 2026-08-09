using System.Text.Json;
using KiriScope.Resources;

namespace KiriScope.Core.Tests;

public sealed class LooseFileOverlayPlannerTests
{
    [Fact]
    public async Task PlanAsync_ClassifiesAddedReplacedIdenticalAndDirectoryConflictsWithoutWritingInputs()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "KiriScope.Tests", Guid.NewGuid().ToString("N"));
        var reference = Path.Combine(temporaryRoot, "reference");
        var overrides = Path.Combine(temporaryRoot, "overrides");
        var reportPath = Path.Combine(temporaryRoot, "reports", "overlay.json");
        Directory.CreateDirectory(Path.Combine(reference, "folder"));
        Directory.CreateDirectory(overrides);
        await File.WriteAllTextAsync(Path.Combine(reference, "replace.txt"), "old");
        await File.WriteAllTextAsync(Path.Combine(reference, "same.txt"), "same");
        await File.WriteAllTextAsync(Path.Combine(overrides, "replace.txt"), "new");
        await File.WriteAllTextAsync(Path.Combine(overrides, "same.txt"), "same");
        await File.WriteAllTextAsync(Path.Combine(overrides, "added.txt"), "added");
        await File.WriteAllTextAsync(Path.Combine(overrides, "folder"), "conflict");
        try
        {
            var report = await LooseFileOverlayPlanner.PlanAsync(reference, overrides);

            Assert.Equal(4, report.Items.Count);
            Assert.Equal(LooseFileOverlayChangeKind.Added, report.Items.Single(static item => item.RelativePath == "added.txt").ChangeKind);
            Assert.Equal(LooseFileOverlayChangeKind.Replaced, report.Items.Single(static item => item.RelativePath == "replace.txt").ChangeKind);
            Assert.Equal(LooseFileOverlayChangeKind.Identical, report.Items.Single(static item => item.RelativePath == "same.txt").ChangeKind);
            Assert.Equal(LooseFileOverlayChangeKind.Conflict, report.Items.Single(static item => item.RelativePath == "folder").ChangeKind);
            Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(reference, "replace.txt")));
            Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(overrides, "replace.txt")));

            var writtenPath = await LooseFileOverlayReportWriter.WriteNewAsync(reportPath, report);
            Assert.Equal(Path.GetFullPath(reportPath), writtenPath);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
            Assert.Equal("1.0", json.RootElement.GetProperty("SchemaVersion").GetString());
            await Assert.ThrowsAsync<IOException>(async () => await LooseFileOverlayReportWriter.WriteNewAsync(reportPath, report));

            var nestedOverrides = Path.Combine(reference, "nested-overrides");
            Directory.CreateDirectory(nestedOverrides);
            await Assert.ThrowsAsync<ArgumentException>(async () => await LooseFileOverlayPlanner.PlanAsync(reference, nestedOverrides));
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }
}
