using KiriScope.Integrations;

namespace KiriScope.Core.Tests;

public sealed class GhidraHeadlessRunnerTests
{
    [Fact]
    public async Task RunAsync_WhenGivenABatchLauncher_ExecutesItAndArchivesTheNewProject()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), "KiriScope.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var inputPath = Path.Combine(directory, "input.bin");
            var toolPath = Path.Combine(directory, "analyzeHeadless.cmd");
            var projectDirectory = Path.Combine(directory, "project");
            await File.WriteAllBytesAsync(inputPath, [1, 2, 3, 4]);
            await File.WriteAllTextAsync(toolPath, "@echo off\r\necho launcher-ran\r\necho project>\"%~1\\%~2.gpr\"\r\nexit /b 0\r\n");

            var result = await GhidraHeadlessRunner.RunAsync(new GhidraHeadlessRequest(
                toolPath,
                inputPath,
                projectDirectory,
                "research"));

            Assert.True(
                result.Succeeded,
                $"Exit={result.AnalysisInvocation?.ExitCode}; Error={result.AnalysisInvocation?.StandardError}; Output={result.AnalysisInvocation?.StandardOutput}; Diagnostics={string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.Code))}");
            Assert.NotNull(result.ProjectArtifact);
            Assert.True(File.Exists(Path.Combine(projectDirectory, "research.gpr")));
            Assert.NotNull(result.ArchivePath);
            Assert.Equal(0, result.AnalysisInvocation!.ExitCode);
            Assert.Contains("launcher-ran", result.AnalysisInvocation.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_WhenToolIsMissing_DoesNotCreateAProjectOrTouchTheInput()
    {
        var directory = Path.Combine(Path.GetTempPath(), "KiriScope.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var inputPath = Path.Combine(directory, "input.bin");
            var projectDirectory = Path.Combine(directory, "project");
            await File.WriteAllBytesAsync(inputPath, [1, 2, 3, 4]);

            var result = await GhidraHeadlessRunner.RunAsync(new GhidraHeadlessRequest(
                Path.Combine(directory, "missing-analyzeHeadless.bat"),
                inputPath,
                projectDirectory,
                "research"));

            Assert.False(result.Succeeded);
            Assert.Null(result.Input);
            Assert.False(Directory.Exists(projectDirectory));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(inputPath));
            Assert.Equal("GHIDRA_TOOL_NOT_FOUND", Assert.Single(result.Diagnostics).Code);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
