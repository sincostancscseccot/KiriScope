using KiriScope.Core.Evidence;

namespace KiriScope.Xp3;

/// <summary>Aggregate metadata for one extension observed in a parsed XP3 index.</summary>
public sealed record Xp3ArchiveExtensionProfile(
    string Extension,
    int EntryCount,
    int EncryptedEntryCount,
    long PackedBytes,
    long UnpackedBytes);

/// <summary>
/// Compact, read-only summary of an XP3 index. It exposes no entry content and does not apply filters.
/// </summary>
public sealed record Xp3ArchiveProfile(
    EvidenceStage Stage,
    long IndexOffset,
    bool IsIndexCompressed,
    int EntryCount,
    int EncryptedEntryCount,
    int UnencryptedEntryCount,
    int MultiSegmentEntryCount,
    int CompressedSegmentCount,
    int EntriesWithAdler32Count,
    long PackedBytes,
    long UnpackedBytes,
    IReadOnlyList<Xp3ArchiveExtensionProfile> Extensions)
{
    public static Xp3ArchiveProfile FromIndex(Xp3ArchiveIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        var entries = index.Entries;
        var extensions = entries
            .GroupBy(static entry => GetExtension(entry.Name), StringComparer.OrdinalIgnoreCase)
            .Select(static group => new Xp3ArchiveExtensionProfile(
                group.Key,
                group.Count(),
                group.Count(static entry => entry.IsMarkedEncrypted),
                SaturatingSum(group.Select(static entry => entry.PackedSize)),
                SaturatingSum(group.Select(static entry => entry.UnpackedSize))))
            .OrderByDescending(static item => item.PackedBytes)
            .ThenBy(static item => item.Extension, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new Xp3ArchiveProfile(
            index.Stage,
            index.IndexOffset,
            index.IsIndexCompressed,
            entries.Count,
            entries.Count(static entry => entry.IsMarkedEncrypted),
            entries.Count(static entry => !entry.IsMarkedEncrypted),
            entries.Count(static entry => entry.Segments.Count > 1),
            entries.Sum(static entry => entry.Segments.Count(static segment => segment.IsCompressed)),
            entries.Count(static entry => entry.Adler32 is not null),
            SaturatingSum(entries.Select(static entry => entry.PackedSize)),
            SaturatingSum(entries.Select(static entry => entry.UnpackedSize)),
            extensions);
    }

    private static long SaturatingSum(IEnumerable<long> values)
    {
        long total = 0;
        foreach (var value in values)
        {
            if (value > 0 && total > long.MaxValue - value)
            {
                return long.MaxValue;
            }

            total += value;
        }

        return total;
    }

    private static string GetExtension(string entryName)
    {
        var extension = Path.GetExtension(entryName);
        return string.IsNullOrEmpty(extension) ? "(none)" : extension.ToLowerInvariant();
    }
}
