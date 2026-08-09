namespace KiriScope.Resources;

/// <summary>One changed byte range in a ciphertext/plaintext comparison.</summary>
public sealed record ContentByteDifferenceRange(int Offset, int Length);

/// <summary>
/// Summary-only ciphertext/plaintext diff. It intentionally does not repeat transformed content in reports.
/// </summary>
public sealed record ContentByteDifference(
    int CiphertextLength,
    int PlaintextLength,
    int ChangedByteCount,
    int? FirstChangedOffset,
    int? LastChangedOffset,
    IReadOnlyList<ContentByteDifferenceRange> ChangedRanges)
{
    public static ContentByteDifference Analyze(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> plaintext, int maximumRanges = 32)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumRanges);

        var changed = 0;
        int? first = null;
        int? last = null;
        var ranges = new List<ContentByteDifferenceRange>(Math.Min(maximumRanges, 32));
        var limit = Math.Max(ciphertext.Length, plaintext.Length);
        var rangeStart = -1;
        for (var index = 0; index < limit; index++)
        {
            var differs = index >= ciphertext.Length || index >= plaintext.Length || ciphertext[index] != plaintext[index];
            if (differs)
            {
                changed++;
                first ??= index;
                last = index;
                rangeStart = rangeStart < 0 ? index : rangeStart;
            }
            else if (rangeStart >= 0)
            {
                AddRange(ranges, maximumRanges, rangeStart, index - rangeStart);
                rangeStart = -1;
            }
        }

        if (rangeStart >= 0)
        {
            AddRange(ranges, maximumRanges, rangeStart, limit - rangeStart);
        }

        return new ContentByteDifference(ciphertext.Length, plaintext.Length, changed, first, last, ranges);
    }

    private static void AddRange(List<ContentByteDifferenceRange> ranges, int maximumRanges, int offset, int length)
    {
        if (ranges.Count < maximumRanges)
        {
            ranges.Add(new ContentByteDifferenceRange(offset, length));
        }
    }
}
