using System.Globalization;
using KiriScope.Core.Diagnostics;
using KiriScope.IO.Hashing;

namespace KiriScope.Runtime;

/// <summary>
/// Imports a user-exported ProcMon CSV as offline evidence. It never starts ProcMon, loads a driver,
/// or attempts to observe live file access itself.
/// </summary>
public static class ProcmonCsvImporter
{
    private const long MaximumInputBytes = 256L * 1024 * 1024;
    private const int MaximumObservations = 100_000;
    private static readonly HashSet<string> FileOperations = new(StringComparer.OrdinalIgnoreCase)
    {
        "CreateFile",
        "ReadFile",
        "WriteFile",
        "CloseFile",
        "CleanupFile",
        "FlushBuffersFile",
        "LockFile",
        "UnlockFile",
        "QueryDirectory",
        "QueryInformationFile",
        "SetInformationFile",
        "DirectoryControl",
        "FileSystemControl",
    };

    public static async Task<RuntimeFileAccessReport> ImportAsync(
        RuntimeFileAccessImportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics = new List<KiriScopeDiagnostic>();
        if (request.TargetProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "A ProcMon import target PID must be positive.");
        }

        var fullPath = Path.GetFullPath(request.CsvPath);
        var file = new FileInfo(fullPath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("ProcMon CSV does not exist.", fullPath);
        }

        if (file.Length > MaximumInputBytes)
        {
            return new RuntimeFileAccessReport(
                new RuntimeExternalEvidenceSource(fullPath, await Sha256Hasher.ComputeFileAsync(fullPath, cancellationToken).ConfigureAwait(false), file.Length),
                request.TargetProcessId,
                DateTimeOffset.UtcNow,
                new Dictionary<string, int>(),
                Array.Empty<RuntimeFileAccessEvidence>(),
                [new KiriScopeDiagnostic("RUNTIME_PROCMON_CSV_TOO_LARGE", DiagnosticSeverity.Warning, $"ProcMon CSV is {file.Length:N0} bytes; the configured limit is {MaximumInputBytes:N0} bytes.")]);
        }

        var source = new RuntimeExternalEvidenceSource(fullPath, await Sha256Hasher.ComputeFileAsync(fullPath, cancellationToken).ConfigureAwait(false), file.Length);
        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        var headerLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(headerLine))
        {
            return new RuntimeFileAccessReport(source, request.TargetProcessId, DateTimeOffset.UtcNow, new Dictionary<string, int>(), Array.Empty<RuntimeFileAccessEvidence>(),
                [new KiriScopeDiagnostic("RUNTIME_PROCMON_CSV_HEADER_MISSING", DiagnosticSeverity.Error, "ProcMon CSV did not contain a header row.")]);
        }

        var header = ParseCsvLine(headerLine);
        var columns = BuildColumnMap(header);
        if (!RequireColumns(columns, diagnostics, "PID", "Process Name", "Operation", "Path"))
        {
            return new RuntimeFileAccessReport(source, request.TargetProcessId, DateTimeOffset.UtcNow, columns, Array.Empty<RuntimeFileAccessEvidence>(), diagnostics);
        }

        var observations = new List<RuntimeFileAccessEvidence>();
        var lineNumber = 1;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            lineNumber++;
            if (line.Length == 0)
            {
                continue;
            }

            var values = ParseCsvLine(line);
            if (!TryGetValue(values, columns, "PID", out var pidText) || !int.TryParse(pidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            {
                AddLimitedDiagnostic(diagnostics, "RUNTIME_PROCMON_CSV_PID_INVALID", $"Line {lineNumber} has an invalid PID value.");
                continue;
            }

            if (pid != request.TargetProcessId || !TryGetValue(values, columns, "Operation", out var operation) || !FileOperations.Contains(operation))
            {
                continue;
            }

            if (!TryGetValue(values, columns, "Path", out var path) || string.IsNullOrWhiteSpace(path))
            {
                AddLimitedDiagnostic(diagnostics, "RUNTIME_PROCMON_CSV_PATH_MISSING", $"Line {lineNumber} has no file-system path.");
                continue;
            }

            if (observations.Count == MaximumObservations)
            {
                diagnostics.Add(new KiriScopeDiagnostic("RUNTIME_PROCMON_CSV_OBSERVATION_CAPPED", DiagnosticSeverity.Warning, $"Only the first {MaximumObservations:N0} matching file-system events are retained."));
                break;
            }

            _ = TryGetValue(values, columns, "Time of Day", out var timeOfDay);
            _ = TryGetValue(values, columns, "Process Name", out var processName);
            _ = TryGetValue(values, columns, "Result", out var result);
            _ = TryGetValue(values, columns, "Detail", out var detail);
            observations.Add(new RuntimeFileAccessEvidence(lineNumber, timeOfDay, processName, pid, operation, path, result, detail));
        }

        return new RuntimeFileAccessReport(source, request.TargetProcessId, DateTimeOffset.UtcNow, columns, observations, diagnostics);
    }

    private static IReadOnlyDictionary<string, int> BuildColumnMap(IReadOnlyList<string> header)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < header.Count; index++)
        {
            var name = header[index].Trim().TrimStart('\uFEFF');
            if (!string.IsNullOrEmpty(name) && !map.ContainsKey(name))
            {
                map.Add(name, index);
            }
        }

        return map;
    }

    private static bool RequireColumns(IReadOnlyDictionary<string, int> columns, List<KiriScopeDiagnostic> diagnostics, params string[] required)
    {
        var missing = required.Where(column => !columns.ContainsKey(column)).ToArray();
        if (missing.Length == 0)
        {
            return true;
        }

        diagnostics.Add(new KiriScopeDiagnostic("RUNTIME_PROCMON_CSV_COLUMNS_MISSING", DiagnosticSeverity.Error, $"ProcMon CSV is missing required columns: {string.Join(", ", missing)}."));
        return false;
    }

    private static bool TryGetValue(IReadOnlyList<string> values, IReadOnlyDictionary<string, int> columns, string column, out string value)
    {
        if (columns.TryGetValue(column, out var index) && index < values.Count)
        {
            value = values[index];
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static void AddLimitedDiagnostic(List<KiriScopeDiagnostic> diagnostics, string code, string message)
    {
        if (diagnostics.Count(diagnostic => diagnostic.Code == code) < 32)
        {
            diagnostics.Add(new KiriScopeDiagnostic(code, DiagnosticSeverity.Warning, message));
        }
    }

    private static IReadOnlyList<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var value = new System.Text.StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    value.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ',' && !quoted)
            {
                values.Add(value.ToString());
                value.Clear();
            }
            else
            {
                value.Append(character);
            }
        }

        values.Add(value.ToString());
        return values;
    }
}

/// <summary>Explicit offline import input for a user-exported ProcMon CSV.</summary>
public sealed record RuntimeFileAccessImportRequest(int TargetProcessId, string CsvPath);

/// <summary>Hash-identified external evidence source; its content is not embedded in the report.</summary>
public sealed record RuntimeExternalEvidenceSource(string FullPath, string Sha256, long Length);

/// <summary>One source-line fact from a user-exported ProcMon file-system event.</summary>
public sealed record RuntimeFileAccessEvidence(
    int SourceLine,
    string TimeOfDay,
    string ProcessName,
    int ProcessId,
    string Operation,
    string Path,
    string Result,
    string Detail);

/// <summary>Offline file-access evidence filtered to one explicit process ID.</summary>
public sealed record RuntimeFileAccessReport(
    RuntimeExternalEvidenceSource Source,
    int TargetProcessId,
    DateTimeOffset ImportedAtUtc,
    IReadOnlyDictionary<string, int> ColumnMap,
    IReadOnlyList<RuntimeFileAccessEvidence> Observations,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics);
