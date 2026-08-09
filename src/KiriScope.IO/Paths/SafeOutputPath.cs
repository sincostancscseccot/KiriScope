namespace KiriScope.IO.Paths;

/// <summary>Prevents archive entry names from escaping a user-selected extraction directory.</summary>
public static class SafeOutputPath
{
    public static string Resolve(string outputRoot, string archiveRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveRelativePath);

        if (Path.IsPathRooted(archiveRelativePath))
        {
            throw new ArgumentException("Archive entry path must be relative.", nameof(archiveRelativePath));
        }

        var root = Path.GetFullPath(outputRoot);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        var normalizedEntry = archiveRelativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(root, normalizedEntry));

        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Archive entry path escapes the selected output directory.", nameof(archiveRelativePath));
        }

        return candidate;
    }
}
