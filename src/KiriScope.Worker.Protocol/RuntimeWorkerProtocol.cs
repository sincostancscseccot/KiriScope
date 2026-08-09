using KiriScope.Core.Diagnostics;

namespace KiriScope.Worker.Protocol;

/// <summary>Architecture reported by the target or isolated worker.</summary>
public enum RuntimeTargetArchitecture
{
    Unknown,
    X86,
    X64,
    Arm64,
}

/// <summary>The narrowly scoped, read-only action allowed in the first worker protocol revision.</summary>
public enum RuntimeCaptureMode
{
    ProcessAndModuleInventory,
}

/// <summary>Versioned, single-request message sent from the controller to an isolated worker over standard input.</summary>
public sealed record RuntimeCaptureRequest(
    string SchemaVersion,
    Guid RequestId,
    int TargetProcessId,
    RuntimeCaptureMode Mode,
    bool ExplicitlyEnabled,
    DateTimeOffset RequestedAtUtc)
{
    public const string CurrentSchemaVersion = "1.0";
}

/// <summary>Identity of the helper process that performed the observation.</summary>
public sealed record RuntimeWorkerIdentity(
    int ProcessId,
    RuntimeTargetArchitecture Architecture,
    string RuntimeVersion);

/// <summary>One loaded module observed without reading target-process memory.</summary>
public sealed record RuntimeModuleEvidence(
    string Name,
    string? FullPath,
    long? BaseAddress,
    long? ImageSize,
    long? FileLength,
    string? Sha256);

/// <summary>Hash-identified process image and loaded-module inventory.</summary>
public sealed record RuntimeProcessEvidence(
    int ProcessId,
    string ProcessName,
    int? ParentProcessId,
    int? SessionId,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset ObservedAtUtc,
    RuntimeTargetArchitecture Architecture,
    string? ExecutablePath,
    long? ExecutableLength,
    string? ExecutableSha256,
    IReadOnlyList<RuntimeModuleEvidence> Modules);

/// <summary>Single worker response written as JSON to standard output.</summary>
public sealed record RuntimeCaptureResponse(
    string SchemaVersion,
    Guid RequestId,
    RuntimeWorkerIdentity Worker,
    bool Succeeded,
    RuntimeProcessEvidence? Process,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics)
{
    public const string CurrentSchemaVersion = "1.0";
}
