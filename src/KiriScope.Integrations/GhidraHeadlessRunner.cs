using System.Diagnostics;
using System.Text;
using System.Text.Json;
using KiriScope.Core.Diagnostics;
using KiriScope.IO.Hashing;

namespace KiriScope.Integrations;

/// <summary>Explicit request to create a new Ghidra project from one input file.</summary>
public sealed record GhidraHeadlessRequest(
    string AnalyzeHeadlessPath,
    string InputPath,
    string ProjectDirectory,
    string ProjectName,
    TimeSpan? Timeout = null);

/// <summary>Hash-identified external-tool input without embedding its contents.</summary>
public sealed record GhidraInputEvidence(string FullPath, string Sha256, long Length);

/// <summary>Captured evidence for one external process invocation.</summary>
public sealed record ExternalProcessEvidence(
    string Command,
    int? ExitCode,
    bool TimedOut,
    bool OutputWasTruncated,
    string StandardOutput,
    string StandardError);

/// <summary>Version identity read from the installed Ghidra distribution without launching the target input.</summary>
public sealed record GhidraToolVersionEvidence(
    string ToolPath,
    string? ApplicationVersion,
    string? ReleaseName,
    string? Revision,
    string? PropertiesPath);

/// <summary>Hash-identified Ghidra project file when the external analysis created one.</summary>
public sealed record GhidraProjectArtifact(string FullPath, string Sha256, long Length);

/// <summary>JSON payload retained next to an explicit Ghidra project.</summary>
public sealed record GhidraResearchArchive(
    string SchemaVersion,
    DateTimeOffset CreatedAtUtc,
    GhidraInputEvidence Input,
    GhidraToolVersionEvidence ToolVersion,
    ExternalProcessEvidence AnalysisInvocation,
    string ProjectDirectory,
    string ProjectName,
    GhidraProjectArtifact? ProjectArtifact,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics)
{
    public const string CurrentSchemaVersion = "1.0";
}

/// <summary>Result of an explicit, isolated Ghidra headless invocation.</summary>
public sealed record GhidraHeadlessResult(
    bool Succeeded,
    GhidraInputEvidence? Input,
    string? ArchivePath,
    GhidraToolVersionEvidence? ToolVersion,
    ExternalProcessEvidence? AnalysisInvocation,
    GhidraProjectArtifact? ProjectArtifact,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics);

/// <summary>
/// Optional Ghidra adapter. It only runs when called explicitly, never changes the input binary,
/// refuses to reuse a project/archive name, and preserves its command transcript in a JSON archive.
/// </summary>
public static class GhidraHeadlessRunner
{
    private const int MaximumCapturedOutputCharacters = 4 * 1024 * 1024;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(20);

    public static async Task<GhidraHeadlessResult> RunAsync(
        GhidraHeadlessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics = new List<KiriScopeDiagnostic>();
        if (string.IsNullOrWhiteSpace(request.AnalyzeHeadlessPath) || !File.Exists(request.AnalyzeHeadlessPath))
        {
            diagnostics.Add(new KiriScopeDiagnostic("GHIDRA_TOOL_NOT_FOUND", DiagnosticSeverity.Warning, "Ghidra analyzeHeadless was not found; no external analysis was started."));
            return new GhidraHeadlessResult(false, null, null, null, null, null, diagnostics);
        }

        if (string.IsNullOrWhiteSpace(request.InputPath) || !File.Exists(request.InputPath))
        {
            diagnostics.Add(new KiriScopeDiagnostic("GHIDRA_INPUT_NOT_FOUND", DiagnosticSeverity.Error, "Ghidra input does not exist; no external analysis was started."));
            return new GhidraHeadlessResult(false, null, null, null, null, null, diagnostics);
        }

        if (!IsValidProjectName(request.ProjectName))
        {
            diagnostics.Add(new KiriScopeDiagnostic("GHIDRA_PROJECT_NAME_INVALID", DiagnosticSeverity.Error, "Ghidra projectName must be a simple non-empty filename without path separators."));
            return new GhidraHeadlessResult(false, null, null, null, null, null, diagnostics);
        }

        var toolPath = Path.GetFullPath(request.AnalyzeHeadlessPath);
        var inputPath = Path.GetFullPath(request.InputPath);
        var projectDirectory = Path.GetFullPath(request.ProjectDirectory);
        var projectFilePath = Path.Combine(projectDirectory, request.ProjectName + ".gpr");
        var projectRepositoryPath = Path.Combine(projectDirectory, request.ProjectName + ".rep");
        var archivePath = Path.Combine(projectDirectory, request.ProjectName + ".kiriscope-analysis.json");
        if (File.Exists(projectFilePath) || Directory.Exists(projectRepositoryPath) || File.Exists(archivePath))
        {
            diagnostics.Add(new KiriScopeDiagnostic("GHIDRA_PROJECT_OR_ARCHIVE_EXISTS", DiagnosticSeverity.Error, "The requested Ghidra project or KiriScope archive already exists and will not be overwritten."));
            return new GhidraHeadlessResult(false, null, null, null, null, null, diagnostics);
        }

        var inputInfo = new FileInfo(inputPath);
        var input = new GhidraInputEvidence(inputPath, await Sha256Hasher.ComputeFileAsync(inputPath, cancellationToken).ConfigureAwait(false), inputInfo.Length);
        Directory.CreateDirectory(projectDirectory);

        var toolVersion = ReadToolVersion(toolPath, diagnostics);
        var timeout = request.Timeout ?? DefaultTimeout;
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Ghidra timeout must be positive.");
        }

        var analysisInvocation = await RunProcessAsync(
            toolPath,
            [projectDirectory, request.ProjectName, "-import", inputPath, "-analysisTimeoutPerFile", Math.Max(1, (int)timeout.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture)],
            timeout,
            cancellationToken).ConfigureAwait(false);

        GhidraProjectArtifact? projectArtifact = null;
        if (File.Exists(projectFilePath))
        {
            var projectInfo = new FileInfo(projectFilePath);
            projectArtifact = new GhidraProjectArtifact(projectFilePath, await Sha256Hasher.ComputeFileAsync(projectFilePath, cancellationToken).ConfigureAwait(false), projectInfo.Length);
        }
        else
        {
            diagnostics.Add(new KiriScopeDiagnostic("GHIDRA_PROJECT_FILE_MISSING", DiagnosticSeverity.Warning, "Ghidra completed without a discoverable .gpr project file."));
        }

        if (analysisInvocation.ExitCode != 0)
        {
            diagnostics.Add(new KiriScopeDiagnostic("GHIDRA_ANALYSIS_FAILED", DiagnosticSeverity.Error, "Ghidra headless analysis returned a non-zero exit code."));
        }

        if (analysisInvocation.TimedOut)
        {
            diagnostics.Add(new KiriScopeDiagnostic("GHIDRA_ANALYSIS_TIMED_OUT", DiagnosticSeverity.Error, "Ghidra headless analysis exceeded the configured timeout and was stopped."));
        }

        var archive = new GhidraResearchArchive(
            GhidraResearchArchive.CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            input,
            toolVersion,
            analysisInvocation,
            projectDirectory,
            request.ProjectName,
            projectArtifact,
            diagnostics);
        try
        {
            await WriteNewArchiveAsync(archivePath, archive, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            diagnostics.Add(new KiriScopeDiagnostic("GHIDRA_ARCHIVE_WRITE_FAILED", DiagnosticSeverity.Error, exception.Message));
        }

        var succeeded = analysisInvocation.ExitCode == 0 && !analysisInvocation.TimedOut && projectArtifact is not null && diagnostics.All(static diagnostic => diagnostic.Severity != DiagnosticSeverity.Error);
        return new GhidraHeadlessResult(succeeded, input, File.Exists(archivePath) ? archivePath : null, toolVersion, analysisInvocation, projectArtifact, diagnostics);
    }

    private static async Task<ExternalProcessEvidence> RunProcessAsync(
        string toolPath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var command = FormatCommand(toolPath, arguments);
        var startInfo = CreateStartInfo(toolPath, arguments);
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            return new ExternalProcessEvidence(command, null, false, false, string.Empty, "Process did not start.");
        }

        var standardOutputTask = ReadCappedAsync(process.StandardOutput);
        var standardErrorTask = ReadCappedAsync(process.StandardError);
        var exitTask = process.WaitForExitAsync();
        var timeoutTask = Task.Delay(timeout);
        var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var completed = await Task.WhenAny(exitTask, timeoutTask, cancellationTask).ConfigureAwait(false);
        var timedOut = completed == timeoutTask;
        if (completed != exitTask)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process exited between the wait and kill requests.
            }

            await exitTask.ConfigureAwait(false);
        }

        if (completed == cancellationTask)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        var standardOutput = await standardOutputTask.ConfigureAwait(false);
        var standardError = await standardErrorTask.ConfigureAwait(false);
        return new ExternalProcessEvidence(
            command,
            process.ExitCode,
            timedOut,
            standardOutput.WasTruncated || standardError.WasTruncated,
            standardOutput.Value,
            standardError.Value);
    }

    private static ProcessStartInfo CreateStartInfo(string toolPath, IReadOnlyList<string> arguments)
    {
        var extension = Path.GetExtension(toolPath);
        if (string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase))
        {
            var commandInterpreter = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            return new ProcessStartInfo(commandInterpreter)
            {
                Arguments = "/d /c \"" + FormatCommand(toolPath, arguments) + "\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
        }

        var executableStartInfo = new ProcessStartInfo(toolPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            executableStartInfo.ArgumentList.Add(argument);
        }

        return executableStartInfo;
    }

    private static async Task<(string Value, bool WasTruncated)> ReadCappedAsync(StreamReader reader)
    {
        var builder = new StringBuilder();
        var buffer = new char[8_192];
        var wasTruncated = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var writable = Math.Min(read, MaximumCapturedOutputCharacters - builder.Length);
            if (writable > 0)
            {
                builder.Append(buffer, 0, writable);
            }

            wasTruncated |= writable != read;
        }

        return (builder.ToString(), wasTruncated);
    }

    private static async Task WriteNewArchiveAsync(string archivePath, GhidraResearchArchive archive, CancellationToken cancellationToken)
    {
        var temporaryPath = archivePath + ".kiriscope-partial-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(output, archive, new JsonSerializerOptions { WriteIndented = true }, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, archivePath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool IsValidProjectName(string? projectName) =>
        !string.IsNullOrWhiteSpace(projectName) &&
        string.Equals(Path.GetFileName(projectName), projectName, StringComparison.Ordinal) &&
        projectName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static GhidraToolVersionEvidence ReadToolVersion(string toolPath, List<KiriScopeDiagnostic> diagnostics)
    {
        var supportDirectory = Path.GetDirectoryName(toolPath);
        var distributionRoot = supportDirectory is null ? null : Directory.GetParent(supportDirectory)?.FullName;
        var propertiesPath = distributionRoot is null ? null : Path.Combine(distributionRoot, "Ghidra", "application.properties");
        if (propertiesPath is null || !File.Exists(propertiesPath))
        {
            diagnostics.Add(new KiriScopeDiagnostic("GHIDRA_VERSION_PROPERTIES_NOT_FOUND", DiagnosticSeverity.Warning, "Ghidra application.properties was not found; tool version could not be recorded."));
            return new GhidraToolVersionEvidence(toolPath, null, null, null, null);
        }

        try
        {
            var values = File.ReadLines(propertiesPath)
                .Where(static line => !line.StartsWith('#') && line.Contains('='))
                .Select(static line => line.Split('=', 2))
                .Where(static parts => parts.Length == 2)
                .ToDictionary(static parts => parts[0].Trim(), static parts => parts[1].Trim(), StringComparer.Ordinal);
            return new GhidraToolVersionEvidence(
                toolPath,
                values.GetValueOrDefault("application.version"),
                values.GetValueOrDefault("application.release.name"),
                values.GetValueOrDefault("application.revision.ghidra"),
                propertiesPath);
        }
        catch (IOException exception)
        {
            diagnostics.Add(new KiriScopeDiagnostic("GHIDRA_VERSION_PROPERTIES_READ_FAILED", DiagnosticSeverity.Warning, exception.Message));
            return new GhidraToolVersionEvidence(toolPath, null, null, null, propertiesPath);
        }
    }

    private static string FormatCommand(string executable, IReadOnlyList<string> arguments) =>
        string.Join(" ", new[] { Quote(executable) }.Concat(arguments.Select(Quote)));

    private static string Quote(string value) => '"' + value.Replace("\"", string.Empty, StringComparison.Ordinal) + '"';
}
