using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using KiriScope.Core.Diagnostics;
using KiriScope.Worker.Protocol;

namespace KiriScope.Runtime;

/// <summary>
/// Collects a bounded, query-only process and module inventory. It never reads target-process memory,
/// starts, suspends, terminates, or injects into the target process.
/// </summary>
public static class RuntimeEvidenceCollector
{
    private const int MaximumModules = 1_024;

    public static async Task<RuntimeCaptureResponse> CollectAsync(
        RuntimeCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics = new List<KiriScopeDiagnostic>();
        var worker = new RuntimeWorkerIdentity(
            Environment.ProcessId,
            RuntimeArchitectureInspector.GetCurrentProcessArchitecture(),
            Environment.Version.ToString());

        if (!string.Equals(request.SchemaVersion, RuntimeCaptureRequest.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            diagnostics.Add(new KiriScopeDiagnostic("RUNTIME_PROTOCOL_VERSION_UNSUPPORTED", DiagnosticSeverity.Error, $"Worker supports schema {RuntimeCaptureRequest.CurrentSchemaVersion}, not {request.SchemaVersion}."));
            return Failed(request, worker, diagnostics);
        }

        if (!request.ExplicitlyEnabled)
        {
            diagnostics.Add(new KiriScopeDiagnostic("RUNTIME_CAPTURE_NOT_EXPLICITLY_ENABLED", DiagnosticSeverity.Error, "The worker refuses runtime observation unless explicit enablement is recorded in the request."));
            return Failed(request, worker, diagnostics);
        }

        if (request.TargetProcessId <= 0)
        {
            diagnostics.Add(new KiriScopeDiagnostic("RUNTIME_TARGET_PID_INVALID", DiagnosticSeverity.Error, "A runtime capture target PID must be positive."));
            return Failed(request, worker, diagnostics);
        }

        if (request.Mode != RuntimeCaptureMode.ProcessAndModuleInventory)
        {
            diagnostics.Add(new KiriScopeDiagnostic("RUNTIME_CAPTURE_MODE_UNSUPPORTED", DiagnosticSeverity.Error, $"The worker does not support capture mode '{request.Mode}'."));
            return Failed(request, worker, diagnostics);
        }

        try
        {
            using var process = Process.GetProcessById(request.TargetProcessId);
            if (process.HasExited)
            {
                diagnostics.Add(new KiriScopeDiagnostic("RUNTIME_TARGET_EXITED", DiagnosticSeverity.Error, "The target process exited before observation started."));
                return Failed(request, worker, diagnostics);
            }

            var architecture = RuntimeArchitectureInspector.Inspect(process.Id);
            diagnostics.AddRange(architecture.Diagnostics);
            if (architecture.Architecture == RuntimeTargetArchitecture.Unknown)
            {
                return Failed(request, worker, diagnostics);
            }

            var executablePath = TryGetMainModulePath(process, diagnostics);
            var executableInfo = TryGetFileInfo(executablePath, diagnostics, "RUNTIME_IMAGE_FILE_INFO_UNAVAILABLE");
            var executableHash = await TryComputeHashAsync(executablePath, diagnostics, "RUNTIME_IMAGE_HASH_UNAVAILABLE", cancellationToken).ConfigureAwait(false);
            var modules = await GetModulesAsync(process, diagnostics, cancellationToken).ConfigureAwait(false);
            var evidence = new RuntimeProcessEvidence(
                process.Id,
                process.ProcessName,
                null,
                TryGetSessionId(process, diagnostics),
                TryGetStartTime(process, diagnostics),
                DateTimeOffset.UtcNow,
                architecture.Architecture,
                executablePath,
                executableInfo?.Length,
                executableHash,
                modules);

            var succeeded = diagnostics.All(static diagnostic => diagnostic.Severity != DiagnosticSeverity.Error);
            return new RuntimeCaptureResponse(
                RuntimeCaptureResponse.CurrentSchemaVersion,
                request.RequestId,
                worker,
                succeeded,
                evidence,
                diagnostics);
        }
        catch (ArgumentException exception)
        {
            diagnostics.Add(new KiriScopeDiagnostic("RUNTIME_TARGET_NOT_FOUND", DiagnosticSeverity.Error, exception.Message));
        }
        catch (InvalidOperationException exception)
        {
            diagnostics.Add(new KiriScopeDiagnostic("RUNTIME_TARGET_EXITED", DiagnosticSeverity.Error, exception.Message));
        }
        catch (Win32Exception exception)
        {
            diagnostics.Add(new KiriScopeDiagnostic("RUNTIME_TARGET_ACCESS_DENIED", DiagnosticSeverity.Error, exception.Message));
        }

        return Failed(request, worker, diagnostics);
    }

    private static RuntimeCaptureResponse Failed(
        RuntimeCaptureRequest request,
        RuntimeWorkerIdentity worker,
        IReadOnlyList<KiriScopeDiagnostic> diagnostics) =>
        new(RuntimeCaptureResponse.CurrentSchemaVersion, request.RequestId, worker, false, null, diagnostics);

    private static string? TryGetMainModulePath(Process process, List<KiriScopeDiagnostic> diagnostics)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Win32Exception exception)
        {
            diagnostics.Add(new KiriScopeDiagnostic("RUNTIME_MAIN_MODULE_UNAVAILABLE", DiagnosticSeverity.Warning, exception.Message));
            return null;
        }
        catch (InvalidOperationException exception)
        {
            diagnostics.Add(new KiriScopeDiagnostic("RUNTIME_MAIN_MODULE_UNAVAILABLE", DiagnosticSeverity.Warning, exception.Message));
            return null;
        }
    }

    private static async Task<IReadOnlyList<RuntimeModuleEvidence>> GetModulesAsync(
        Process process,
        List<KiriScopeDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            var modules = process.Modules.Cast<ProcessModule>().ToArray();
            if (modules.Length > MaximumModules)
            {
                diagnostics.Add(new KiriScopeDiagnostic("RUNTIME_MODULE_COUNT_CAPPED", DiagnosticSeverity.Warning, $"Target exposes {modules.Length} modules; only the first {MaximumModules} are captured."));
            }

            var results = new List<RuntimeModuleEvidence>(Math.Min(modules.Length, MaximumModules));
            foreach (var module in modules.Take(MaximumModules))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? fullPath;
                try
                {
                    fullPath = module.FileName;
                }
                catch (Win32Exception exception)
                {
                    diagnostics.Add(new KiriScopeDiagnostic("RUNTIME_MODULE_PATH_UNAVAILABLE", DiagnosticSeverity.Warning, exception.Message));
                    fullPath = null;
                }

                var fileInfo = TryGetFileInfo(fullPath, diagnostics, "RUNTIME_MODULE_FILE_INFO_UNAVAILABLE");
                var hash = await TryComputeHashAsync(fullPath, diagnostics, "RUNTIME_MODULE_HASH_UNAVAILABLE", cancellationToken).ConfigureAwait(false);
                results.Add(new RuntimeModuleEvidence(
                    module.ModuleName,
                    fullPath,
                    module.BaseAddress.ToInt64(),
                    module.ModuleMemorySize,
                    fileInfo?.Length,
                    hash));
            }

            return results;
        }
        catch (Win32Exception exception)
        {
            diagnostics.Add(new KiriScopeDiagnostic("RUNTIME_MODULE_ENUMERATION_UNAVAILABLE", DiagnosticSeverity.Error, exception.Message));
            return Array.Empty<RuntimeModuleEvidence>();
        }
        catch (InvalidOperationException exception)
        {
            diagnostics.Add(new KiriScopeDiagnostic("RUNTIME_MODULE_ENUMERATION_UNAVAILABLE", DiagnosticSeverity.Error, exception.Message));
            return Array.Empty<RuntimeModuleEvidence>();
        }
    }

    private static FileInfo? TryGetFileInfo(string? path, List<KiriScopeDiagnostic> diagnostics, string diagnosticCode)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var file = new FileInfo(path);
            return file.Exists ? file : null;
        }
        catch (IOException exception)
        {
            diagnostics.Add(new KiriScopeDiagnostic(diagnosticCode, DiagnosticSeverity.Warning, exception.Message));
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new KiriScopeDiagnostic(diagnosticCode, DiagnosticSeverity.Warning, exception.Message));
            return null;
        }
    }

    private static async Task<string?> TryComputeHashAsync(string? path, List<KiriScopeDiagnostic> diagnostics, string diagnosticCode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }
        catch (IOException exception)
        {
            diagnostics.Add(new KiriScopeDiagnostic(diagnosticCode, DiagnosticSeverity.Warning, exception.Message));
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new KiriScopeDiagnostic(diagnosticCode, DiagnosticSeverity.Warning, exception.Message));
            return null;
        }
    }

    private static DateTimeOffset? TryGetStartTime(Process process, List<KiriScopeDiagnostic> diagnostics)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime());
        }
        catch (Win32Exception exception)
        {
            diagnostics.Add(new KiriScopeDiagnostic("RUNTIME_START_TIME_UNAVAILABLE", DiagnosticSeverity.Warning, exception.Message));
            return null;
        }
        catch (InvalidOperationException exception)
        {
            diagnostics.Add(new KiriScopeDiagnostic("RUNTIME_START_TIME_UNAVAILABLE", DiagnosticSeverity.Warning, exception.Message));
            return null;
        }
    }

    private static int? TryGetSessionId(Process process, List<KiriScopeDiagnostic> diagnostics)
    {
        try
        {
            return process.SessionId;
        }
        catch (InvalidOperationException exception)
        {
            diagnostics.Add(new KiriScopeDiagnostic("RUNTIME_SESSION_UNAVAILABLE", DiagnosticSeverity.Warning, exception.Message));
            return null;
        }
    }

}
