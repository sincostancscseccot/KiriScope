using KiriScope.Runtime;
using KiriScope.Worker.Protocol;

namespace KiriScope.Core.Tests;

public sealed class RuntimeEvidenceCollectorTests
{
    [Fact]
    public async Task CollectAsync_WhenNotExplicitlyEnabled_RefusesBeforeInspectingATarget()
    {
        var request = new RuntimeCaptureRequest(
            RuntimeCaptureRequest.CurrentSchemaVersion,
            Guid.NewGuid(),
            Environment.ProcessId,
            RuntimeCaptureMode.ProcessAndModuleInventory,
            ExplicitlyEnabled: false,
            DateTimeOffset.UtcNow);

        var response = await RuntimeEvidenceCollector.CollectAsync(request);

        Assert.False(response.Succeeded);
        Assert.Null(response.Process);
        Assert.Contains(response.Diagnostics, static diagnostic => diagnostic.Code == "RUNTIME_CAPTURE_NOT_EXPLICITLY_ENABLED");
    }

    [Fact]
    public async Task CollectAsync_RecordsCurrentProcessAndModuleFactsWithoutReadingProcessMemory()
    {
        var request = new RuntimeCaptureRequest(
            RuntimeCaptureRequest.CurrentSchemaVersion,
            Guid.NewGuid(),
            Environment.ProcessId,
            RuntimeCaptureMode.ProcessAndModuleInventory,
            ExplicitlyEnabled: true,
            DateTimeOffset.UtcNow);

        var response = await RuntimeEvidenceCollector.CollectAsync(request);

        Assert.True(response.Succeeded, string.Join(Environment.NewLine, response.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.NotNull(response.Process);
        Assert.Equal(Environment.ProcessId, response.Process!.ProcessId);
        Assert.NotEqual(RuntimeTargetArchitecture.Unknown, response.Process.Architecture);
        Assert.NotEmpty(response.Process.Modules);
        Assert.NotNull(response.Process.ExecutableSha256);
    }

    [Fact]
    public async Task RuntimeWorkerLauncher_CapturesTheCurrentProcessThroughAnIsolatedWorker()
    {
        var result = await RuntimeWorkerLauncher.CaptureAsync(new RuntimeCaptureLaunchRequest(Environment.ProcessId, ExplicitlyEnabled: true));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.NotNull(result.Request);
        Assert.NotNull(result.WorkerFile);
        Assert.NotNull(result.Invocation);
        Assert.NotNull(result.Response);
        Assert.NotEqual(Environment.ProcessId, result.Response!.Worker.ProcessId);
        Assert.Equal(Environment.ProcessId, result.Response.Process!.ProcessId);
    }
}
