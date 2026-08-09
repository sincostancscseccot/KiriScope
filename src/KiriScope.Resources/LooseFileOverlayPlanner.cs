using KiriScope.Core.Diagnostics;
using KiriScope.IO.Hashing;
using KiriScope.IO.Paths;

namespace KiriScope.Resources;

/// <summary>How one override path relates to its same-relative-path reference file.</summary>
public enum LooseFileOverlayChangeKind
{
    Added,
    Replaced,
    Identical,
    Conflict,
}

/// <summary>One read-only comparison item for a possible loose-file override.</summary>
public sealed record LooseFileOverlayItem(
    string RelativePath,
    LooseFileOverlayChangeKind ChangeKind,
    string OverridePath,
    long OverrideLength,
    string OverrideSha256,
    string? ReferencePath,
    long? ReferenceLength,
    string? ReferenceSha256,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics);

/// <summary>A new-only archive of a loose-file overlay plan, not proof that an engine honors it.</summary>
public sealed record LooseFileOverlayReport(
    string SchemaVersion,
    DateTimeOffset CreatedAtUtc,
    string ReferenceDirectory,
    string OverrideDirectory,
    IReadOnlyList<LooseFileOverlayItem> Items,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics,
    string? ReproductionCommand = null)
{
    public const string CurrentSchemaVersion = "1.0";
}

/// <summary>
/// Produces a bounded, read-only per-path overlay plan. It never deploys files or asserts engine support.
/// </summary>
public static class LooseFileOverlayPlanner
{
    public const int DefaultMaximumOverrideFiles = 250_000;

    public static async Task<LooseFileOverlayReport> PlanAsync(
        string referenceDirectory,
        string overrideDirectory,
        int maximumOverrideFiles = DefaultMaximumOverrideFiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(overrideDirectory);
        if (maximumOverrideFiles <= 0 || maximumOverrideFiles > DefaultMaximumOverrideFiles)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumOverrideFiles), $"Maximum override files must be between 1 and {DefaultMaximumOverrideFiles:N0}.");
        }

        var referenceRoot = Path.GetFullPath(referenceDirectory);
        var overrideRoot = Path.GetFullPath(overrideDirectory);
        if (!Directory.Exists(referenceRoot))
        {
            throw new DirectoryNotFoundException($"Overlay reference directory does not exist: {referenceRoot}");
        }

        if (!Directory.Exists(overrideRoot))
        {
            throw new DirectoryNotFoundException($"Overlay override directory does not exist: {overrideRoot}");
        }

        if (IsContainedBy(referenceRoot, overrideRoot) || IsContainedBy(overrideRoot, referenceRoot))
        {
            throw new ArgumentException("Overlay reference and override directories must not contain one another.");
        }

        var overridePaths = EnumerateOverrideFiles(overrideRoot, maximumOverrideFiles, cancellationToken);
        var items = new List<LooseFileOverlayItem>(overridePaths.Count);
        foreach (var overridePath in overridePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(overrideRoot, overridePath).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
            var referencePath = SafeOutputPath.Resolve(referenceRoot, relativePath);
            var overrideInfo = new FileInfo(overridePath);
            var overrideHash = await Sha256Hasher.ComputeFileAsync(overridePath, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(referencePath))
            {
                if (Directory.Exists(referencePath))
                {
                    items.Add(new LooseFileOverlayItem(
                        relativePath,
                        LooseFileOverlayChangeKind.Conflict,
                        overridePath,
                        overrideInfo.Length,
                        overrideHash,
                        referencePath,
                        null,
                        null,
                        [new KiriScopeDiagnostic("OVERLAY_REFERENCE_DIRECTORY_CONFLICT", DiagnosticSeverity.Warning, "The override path collides with a directory in the reference tree.")]));
                }
                else
                {
                    items.Add(new LooseFileOverlayItem(relativePath, LooseFileOverlayChangeKind.Added, overridePath, overrideInfo.Length, overrideHash, referencePath, null, null, Array.Empty<KiriScopeDiagnostic>()));
                }

                continue;
            }

            var referenceInfo = new FileInfo(referencePath);
            var referenceHash = await Sha256Hasher.ComputeFileAsync(referencePath, cancellationToken).ConfigureAwait(false);
            var kind = string.Equals(overrideHash, referenceHash, StringComparison.OrdinalIgnoreCase)
                ? LooseFileOverlayChangeKind.Identical
                : LooseFileOverlayChangeKind.Replaced;
            items.Add(new LooseFileOverlayItem(relativePath, kind, overridePath, overrideInfo.Length, overrideHash, referencePath, referenceInfo.Length, referenceHash, Array.Empty<KiriScopeDiagnostic>()));
        }

        var diagnostics = new List<KiriScopeDiagnostic>();
        if (items.Count == 0)
        {
            diagnostics.Add(new KiriScopeDiagnostic("OVERLAY_NO_OVERRIDE_FILES", DiagnosticSeverity.Warning, "Override directory contains no regular files to compare."));
        }

        return new LooseFileOverlayReport(
            LooseFileOverlayReport.CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            referenceRoot,
            overrideRoot,
            items,
            diagnostics);
    }

    public static bool IsContainedBy(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return !relative.Equals("..", StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative);
    }

    private static List<string> EnumerateOverrideFiles(string root, int maximumFiles, CancellationToken cancellationToken)
    {
        var paths = new List<string>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = false,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint,
            }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                paths.Add(Path.GetFullPath(path));
                if (paths.Count > maximumFiles)
                {
                    throw new IOException($"Overlay plan exceeds the configured {maximumFiles:N0}-file limit.");
                }
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new IOException("Overlay plan could not enumerate the override directory.", exception);
        }

        paths.Sort(StringComparer.OrdinalIgnoreCase);
        return paths;
    }
}
