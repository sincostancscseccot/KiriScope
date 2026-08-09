using System.Text.Json;
using KiriScope.Core.Diagnostics;

namespace KiriScope.Analysis;

/// <summary>Writes a new research archive without replacing an earlier investigation record.</summary>
public static class ResearchAnalysisArchiveWriter
{
    public static async Task<string> WriteNewAsync(
        string outputPath,
        StaticBinaryAnalysisReport report,
        string reproductionCommand,
        IReadOnlyList<KiriScopeDiagnostic>? diagnostics = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(reproductionCommand);

        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("Research archive output must have a parent directory.", nameof(outputPath));
        }

        if (File.Exists(fullPath))
        {
            throw new IOException($"Research archive already exists and will not be overwritten: {fullPath}");
        }

        Directory.CreateDirectory(directory);
        var temporaryPath = fullPath + ".kiriscope-partial-" + Guid.NewGuid().ToString("N");
        try
        {
            var archive = new ResearchAnalysisArchive(
                ResearchAnalysisArchive.CurrentSchemaVersion,
                DateTimeOffset.UtcNow,
                reproductionCommand,
                report,
                diagnostics ?? Array.Empty<KiriScopeDiagnostic>());
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(output, archive, new JsonSerializerOptions { WriteIndented = true }, cancellationToken).ConfigureAwait(false);
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
