namespace KiriScope.Core.Diagnostics;

/// <summary>A stable diagnostic for the GUI, CLI, and reports.</summary>
public sealed record KiriScopeDiagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    long? Offset = null);
