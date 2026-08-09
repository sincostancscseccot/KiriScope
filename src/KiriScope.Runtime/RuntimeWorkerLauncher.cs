using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KiriScope.Core.Diagnostics;
using KiriScope.Worker.Protocol;

namespace KiriScope.Runtime;

/// <summary>Explicitly launches a matching KiriScope worker and validates its single JSON response.</summary>
public static class RuntimeWorkerLauncher
{
    private const int MaximumCapturedOutputCharacters = 4 * 1024 * 1024;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = false,
    };

    public static async Task<RuntimeCaptureResult> CaptureAsync(
        RuntimeCaptureLaunchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics = new List<KiriScopeDiagnostic>();
        if (!request.ExplicitlyEnabled)
        {
            diagnostics.Add(new KiriScopeDiagnostic("RUNTIME_CAPTURE_NOT_EXPLICITLY_ENABLED", DiagnosticSeverity.Error, "Runtime capture is disabled until the caller explicitly enables it."));
            return new RuntimeCaptureResult(false, null, null, null, null, diagnostics);
        }

        var targetArchitecture = RuntimeArchitectureInspector.Inspect(request.TargetProcessId);
        diagnostics.AddRange(targetArchitecture.Diagnostics);
        if (targetArchitecture.Architecture is RuntimeTargetArchitecture.Unknown or RuntimeTargetArchitecture.Arm64)
        {
            if (targetArchitecture.Architecture == RuntimeTargetArchitecture.Arm64)
            {
                diagnostics.Add(new KiriScopeDiagnostic("RUNTIME_TARGET_ARCHITECTURE_UNSUPPORTED", DiagnosticSeverity.Error, "No ARM64 runtime worker is included in this revision."));
            }

            return new RuntimeCaptureResult(false, null, null, null, null, diagnostics);
        }

        var workerPath = ResolveWorkerPath(request, targetArchitecture.Architecture);
        if (workerPath is null || !File.Exists(workerPath))
        {
            diagnostics.Add(new KiriScopeDiagnostic("RUNTIME_WORKER_NOT_FOUND", DiagnosticSeverity.Error, "The matching KiriScope runtime worker was not found beside the controller."));
            return new RuntimeCaptureResult(false, null, null, null, null, diagnostics);
        }

        var workerIdentity = await IdentifyWorkerAsync(workerPath, targetArchitecture.Architecture, cancellationToken).ConfigureAwait(false);
        var captureRequest = new RuntimeCaptureRequest(
            RuntimeCaptureRequest.CurrentSchemaVersion,
            Guid.NewGuid(),
            request.TargetProcessId,
            RuntimeCaptureMode.ProcessAndModuleInventory,
            true,
            DateTimeOffset.UtcNow);
        var timeout = request.Timeout ?? DefaultTimeout;
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Runtime worker timeout must be positive.");
        }

        var invocation = await RunWorkerAsync(request.DotnetHostPath ?? "dotnet", workerPath, captureRequest, timeout, cancellationToken).ConfigureAwait(false);
        if (invocation.TimedOut)
        {
            diagnostics.Add(new KiriScopeDiagnostic("RUNTIME_WORKER_TIMED_OUT", DiagnosticSeverity.Error, "The isolated runtime worker exceeded the configured timeout and was stopped."));
        }

        if (invocation.ExitCode != 0)
        {
            diagnostics.Add(new KiriScopeDiagnostic("RUNTIME_WORKER_FAILED", DiagnosticSeverity.Error, "The isolated runtime worker returned a non-zero exit code."));
        }

        RuntimeCaptureResponse? response = null;
        if (!invocation.TimedOut && invocation.ExitCode == 0)
        {
            try
            {
                response = JsonSerializer.Deserialize<RuntimeCaptureResponse>(invocation.StandardOutput, JsonOptions);
            }
            catch (JsonException exception)
            {
                diagnostics.Add(new KiriScopeDiagnostic("RUNTIME_WORKER_RESPONSE_INVALID", DiagnosticSeverity.Error, exception.Message));
            }

            if (response is null)
            {
                diagnostics.Add(new KiriScopeDiagnostic("RUNTIME_WORKER_RESPONSE_MISSING", DiagnosticSeverity.Error, "The isolated worker did not emit a runtime response."));
            }
            else
            {
                ValidateResponse(captureRequest, targetArchitecture.Architecture, response, diagnostics);
                diagnostics.AddRange(response.Diagnostics);
            }
        }

        var succeeded = response is not null && response.Succeeded && !invocation.TimedOut && invocation.ExitCode == 0 && diagnostics.All(static diagnostic => diagnostic.Severity != DiagnosticSeverity.Error);
        return new RuntimeCaptureResult(succeeded, captureRequest, workerIdentity, invocation, response, diagnostics);
    }

    private static string? ResolveWorkerPath(RuntimeCaptureLaunchRequest request, RuntimeTargetArchitecture targetArchitecture)
    {
        var configured = targetArchitecture == RuntimeTargetArchitecture.X86 ? request.WorkerX86Path : request.WorkerX64Path;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        var name = targetArchitecture == RuntimeTargetArchitecture.X86 ? "KiriScope.Worker.X86" : "KiriScope.Worker.X64";
        var architectureDirectory = targetArchitecture == RuntimeTargetArchitecture.X86 ? "x86" : "x64";
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "workers", architectureDirectory, name + ".exe"),
            Path.Combine(AppContext.BaseDirectory, "workers", architectureDirectory, name + ".dll"),
            Path.Combine(AppContext.BaseDirectory, name + ".exe"),
            Path.Combine(AppContext.BaseDirectory, name + ".dll"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task<RuntimeWorkerFileIdentity> IdentifyWorkerAsync(string path, RuntimeTargetArchitecture expectedArchitecture, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        return new RuntimeWorkerFileIdentity(fullPath, expectedArchitecture, hash, info.Length);
    }

    private static async Task<RuntimeWorkerInvocation> RunWorkerAsync(
        string dotnetHostPath,
        string workerPath,
        RuntimeCaptureRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var isManagedAssembly = string.Equals(Path.GetExtension(workerPath), ".dll", StringComparison.OrdinalIgnoreCase);
        var executablePath = isManagedAssembly ? dotnetHostPath : workerPath;
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (isManagedAssembly)
        {
            startInfo.ArgumentList.Add(workerPath);
        }
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            return new RuntimeWorkerInvocation(executablePath, workerPath, null, false, false, string.Empty, "Worker process did not start.");
        }

        var stdoutTask = ReadCappedAsync(process.StandardOutput);
        var stderrTask = ReadCappedAsync(process.StandardError);
        await JsonSerializer.SerializeAsync(process.StandardInput.BaseStream, request, JsonOptions, cancellationToken).ConfigureAwait(false);
        await process.StandardInput.DisposeAsync().ConfigureAwait(false);

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
                // The process exited before the cancellation/timeout kill request.
            }

            await exitTask.ConfigureAwait(false);
        }

        if (completed == cancellationTask)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new RuntimeWorkerInvocation(executablePath, workerPath, process.ExitCode, timedOut, stdout.WasTruncated || stderr.WasTruncated, stdout.Value, stderr.Value);
    }

    private static async Task<(string Value, bool WasTruncated)> ReadCappedAsync(StreamReader reader)
    {
        var builder = new StringBuilder();
        var buffer = new char[8192];
        var truncated = false;
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

            truncated |= writable != read;
        }

        return (builder.ToString(), truncated);
    }

    private static void ValidateResponse(
        RuntimeCaptureRequest request,
        RuntimeTargetArchitecture expectedArchitecture,
        RuntimeCaptureResponse response,
        List<KiriScopeDiagnostic> diagnostics)
    {
        if (!string.Equals(response.SchemaVersion, RuntimeCaptureResponse.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            diagnostics.Add(new KiriScopeDiagnostic("RUNTIME_WORKER_RESPONSE_VERSION_UNSUPPORTED", DiagnosticSeverity.Error, $"Worker returned schema {response.SchemaVersion}."));
        }

        if (response.RequestId != request.RequestId)
        {
            diagnostics.Add(new KiriScopeDiagnostic("RUNTIME_WORKER_RESPONSE_REQUEST_MISMATCH", DiagnosticSeverity.Error, "Worker response request ID does not match the controller request."));
        }

        if (response.Worker.Architecture != expectedArchitecture)
        {
            diagnostics.Add(new KiriScopeDiagnostic("RUNTIME_WORKER_ARCHITECTURE_MISMATCH", DiagnosticSeverity.Error, $"Expected {expectedArchitecture} worker but response reported {response.Worker.Architecture}."));
        }

        if (response.Process is not null && response.Process.ProcessId != request.TargetProcessId)
        {
            diagnostics.Add(new KiriScopeDiagnostic("RUNTIME_WORKER_TARGET_MISMATCH", DiagnosticSeverity.Error, "Worker response target PID does not match the controller request."));
        }
    }
}

/// <summary>Controller inputs. Explicit enablement is required before any worker process is launched.</summary>
public sealed record RuntimeCaptureLaunchRequest(
    int TargetProcessId,
    bool ExplicitlyEnabled,
    string? WorkerX86Path = null,
    string? WorkerX64Path = null,
    string? DotnetHostPath = null,
    TimeSpan? Timeout = null);

/// <summary>Hash-identified worker binary selected by the controller.</summary>
public sealed record RuntimeWorkerFileIdentity(
    string FullPath,
    RuntimeTargetArchitecture ExpectedArchitecture,
    string Sha256,
    long Length);

/// <summary>Controller-side transcript of an isolated worker invocation.</summary>
public sealed record RuntimeWorkerInvocation(
    string DotnetHostPath,
    string WorkerPath,
    int? ExitCode,
    bool TimedOut,
    bool OutputWasTruncated,
    string StandardOutput,
    string StandardError);

/// <summary>Validated runtime observation returned from an isolated worker.</summary>
public sealed record RuntimeCaptureResult(
    bool Succeeded,
    RuntimeCaptureRequest? Request,
    RuntimeWorkerFileIdentity? WorkerFile,
    RuntimeWorkerInvocation? Invocation,
    RuntimeCaptureResponse? Response,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics);
