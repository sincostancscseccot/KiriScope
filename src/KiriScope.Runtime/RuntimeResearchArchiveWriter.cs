using System.Text.Json;
using System.Text.Json.Serialization;

namespace KiriScope.Runtime;

/// <summary>Writes runtime-research evidence only to new JSON files.</summary>
public static class RuntimeResearchArchiveWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<string> WriteNewAsync<T>(string outputPath, T value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(value);
        var fullPath = Path.GetFullPath(outputPath);
        if (File.Exists(fullPath))
        {
            throw new IOException($"Runtime research archive already exists and will not be overwritten: {fullPath}");
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("Runtime research archive path has no parent directory.", nameof(outputPath));
        }

        Directory.CreateDirectory(directory);
        var temporaryPath = fullPath + ".kiriscope-partial-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(output, value, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, fullPath, overwrite: false);
            return fullPath;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

/// <summary>Traceable archive of an isolated, explicit runtime process observation.</summary>
public sealed record RuntimeProcessResearchArchive(
    string SchemaVersion,
    DateTimeOffset CreatedAtUtc,
    string ReproductionCommand,
    RuntimeCaptureResult Capture)
{
    public const string CurrentSchemaVersion = "1.0";
}

/// <summary>Traceable archive of a user-exported ProcMon CSV import.</summary>
public sealed record RuntimeFileAccessResearchArchive(
    string SchemaVersion,
    DateTimeOffset CreatedAtUtc,
    string ReproductionCommand,
    RuntimeFileAccessReport Report)
{
    public const string CurrentSchemaVersion = "1.0";
}

/// <summary>Traceable archive of an offline contrast between two user-exported ProcMon CSV files.</summary>
public sealed record RuntimeFileAccessComparisonResearchArchive(
    string SchemaVersion,
    DateTimeOffset CreatedAtUtc,
    string ReproductionCommand,
    RuntimeFileAccessComparisonReport Report)
{
    public const string CurrentSchemaVersion = "1.0";
}
