using System.ComponentModel;
using System.Runtime.InteropServices;
using KiriScope.Core.Diagnostics;
using KiriScope.Worker.Protocol;

namespace KiriScope.Runtime;

/// <summary>Reads a local process architecture with query-only Windows APIs.</summary>
public static class RuntimeArchitectureInspector
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const ushort ImageFileMachineUnknown = 0x0000;
    private const ushort ImageFileMachineI386 = 0x014c;
    private const ushort ImageFileMachineAmd64 = 0x8664;
    private const ushort ImageFileMachineArm64 = 0xaa64;

    public static RuntimeArchitectureInspection Inspect(int processId)
    {
        var diagnostics = new List<KiriScopeDiagnostic>();
        if (processId <= 0)
        {
            diagnostics.Add(new KiriScopeDiagnostic("RUNTIME_TARGET_PID_INVALID", DiagnosticSeverity.Error, "A runtime capture target PID must be positive."));
            return new RuntimeArchitectureInspection(RuntimeTargetArchitecture.Unknown, diagnostics);
        }

        if (!OperatingSystem.IsWindows())
        {
            diagnostics.Add(new KiriScopeDiagnostic("RUNTIME_WINDOWS_REQUIRED", DiagnosticSeverity.Error, "Runtime process observation is currently available only on Windows."));
            return new RuntimeArchitectureInspection(RuntimeTargetArchitecture.Unknown, diagnostics);
        }

        var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (processHandle == IntPtr.Zero)
        {
            diagnostics.Add(new KiriScopeDiagnostic("RUNTIME_TARGET_OPEN_FAILED", DiagnosticSeverity.Error, new Win32Exception(Marshal.GetLastWin32Error()).Message));
            return new RuntimeArchitectureInspection(RuntimeTargetArchitecture.Unknown, diagnostics);
        }

        try
        {
            if (!IsWow64Process2(processHandle, out var processMachine, out var nativeMachine))
            {
                diagnostics.Add(new KiriScopeDiagnostic("RUNTIME_TARGET_ARCHITECTURE_UNAVAILABLE", DiagnosticSeverity.Error, new Win32Exception(Marshal.GetLastWin32Error()).Message));
                return new RuntimeArchitectureInspection(RuntimeTargetArchitecture.Unknown, diagnostics);
            }

            var machine = processMachine == ImageFileMachineUnknown ? nativeMachine : processMachine;
            var architecture = ToArchitecture(machine);
            if (architecture == RuntimeTargetArchitecture.Unknown)
            {
                diagnostics.Add(new KiriScopeDiagnostic("RUNTIME_TARGET_ARCHITECTURE_UNKNOWN", DiagnosticSeverity.Error, $"Windows reported an unsupported target machine value 0x{machine:X4}."));
            }

            return new RuntimeArchitectureInspection(architecture, diagnostics);
        }
        finally
        {
            _ = CloseHandle(processHandle);
        }
    }

    public static RuntimeTargetArchitecture GetCurrentProcessArchitecture() =>
        Environment.Is64BitProcess ? RuntimeTargetArchitecture.X64 : RuntimeTargetArchitecture.X86;

    private static RuntimeTargetArchitecture ToArchitecture(ushort machine) => machine switch
    {
        ImageFileMachineI386 => RuntimeTargetArchitecture.X86,
        ImageFileMachineAmd64 => RuntimeTargetArchitecture.X64,
        ImageFileMachineArm64 => RuntimeTargetArchitecture.Arm64,
        _ => RuntimeTargetArchitecture.Unknown,
    };

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(IntPtr processHandle, out ushort processMachine, out ushort nativeMachine);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>Result of a query-only target-architecture probe.</summary>
public sealed record RuntimeArchitectureInspection(
    RuntimeTargetArchitecture Architecture,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics);
