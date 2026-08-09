using KiriScope.Core.Diagnostics;
using KiriScope.Core.Evidence;

namespace KiriScope.Resources;

/// <summary>Reads only the PSB name table and root dictionary keys; it never decodes PSB values or resources.</summary>
public static class PsbStructureProbe
{
    private const int MaxEntries = 100_000;

    public static async Task<PsbStructureProbeResult> ProbeAsync(Stream input, CancellationToken cancellationToken = default)
    {
        var header = await PsbHeaderReader.ReadAsync(input, cancellationToken).ConfigureAwait(false);
        if (!header.IsRecognized || header.HeaderMayBeEncrypted || header.Version is < 2 or > 4 || header.NamesOffset is null || header.EntriesOffset is null)
            return new(header.Stage, false, Array.Empty<string>(), Array.Empty<PsbResourceReference>(), Array.Empty<PsbRootUnsignedInteger>(), header.Diagnostics);

        try
        {
            var names = await ReadNamesAsync(input, header.NamesOffset.Value, cancellationToken).ConfigureAwait(false);
            input.Position = header.EntriesOffset.Value;
            if (await ReadByteAsync(input, cancellationToken).ConfigureAwait(false) != 0x21)
                return Failure("PSB_ROOT_NOT_DICTIONARY", "PSB root value is not an object dictionary.");
            var keyIndexes = await ReadArrayAsync(input, cancellationToken).ConfigureAwait(false);
            _ = await ReadArrayAsync(input, cancellationToken).ConfigureAwait(false); // value-relative offsets
            if (keyIndexes.Count > MaxEntries || keyIndexes.Any(index => index >= names.Count))
                return Failure("PSB_ROOT_KEY_INDEX_INVALID", "PSB root dictionary key indexes are invalid or excessive.");
            var keys = keyIndexes.Select(index => names[(int)index]).ToArray();
            var pimg = keys.Contains("layers", StringComparer.Ordinal) && keys.Contains("width", StringComparer.Ordinal) && keys.Contains("height", StringComparer.Ordinal);
            var rootMetadata = await ReadRootMetadataAsync(input, header.EntriesOffset.Value, keyIndexes, cancellationToken).ConfigureAwait(false);
            var resources = await MapResourcesAsync(input, header, rootMetadata.Resources, cancellationToken).ConfigureAwait(false);
            return new PsbStructureProbeResult(EvidenceStage.ContainerIdentified, pimg, keys, resources, rootMetadata.UnsignedIntegers,
                [new KiriScopeDiagnostic(pimg ? "PSB_PIMG_SIGNATURE_IDENTIFIED" : "PSB_ROOT_KEYS_IDENTIFIED", DiagnosticSeverity.Info,
                    pimg ? "PSB root keys match the PIMG structural signature." : "PSB name table and root object keys were read; no PIMG signature was found.")]);
        }
        catch (InvalidDataException exception)
        {
            return Failure("PSB_STRUCTURE_INVALID", exception.Message);
        }
    }

    private static async Task<IReadOnlyList<PsbResourceReference>> MapResourcesAsync(Stream input, PsbHeaderValidationResult header, IReadOnlyList<PsbResourceReference> references, CancellationToken cancellationToken)
    {
        if (header.ChunkOffsetsTableOffset is null || header.ChunkLengthsTableOffset is null || header.ChunkDataOffset is null) return references;
        input.Position = header.ChunkOffsetsTableOffset.Value; var offsets = await ReadArrayAsync(input, cancellationToken).ConfigureAwait(false);
        input.Position = header.ChunkLengthsTableOffset.Value; var lengths = await ReadArrayAsync(input, cancellationToken).ConfigureAwait(false);
        if (offsets.Count != lengths.Count || offsets.Count > MaxEntries) throw new InvalidDataException("PSB resource tables are inconsistent or excessive.");
        var mapped = new List<PsbResourceReference>(references.Count);
        foreach (var reference in references)
        {
            if (reference.ResourceIndex >= offsets.Count)
                throw new InvalidDataException("PSB resource reference points outside the file.");
            var index = checked((int)reference.ResourceIndex);
            if (offsets[index] > input.Length - header.ChunkDataOffset.Value || lengths[index] > input.Length - header.ChunkDataOffset.Value - offsets[index])
                throw new InvalidDataException("PSB resource reference points outside the file.");
            mapped.Add(reference with { Offset = header.ChunkDataOffset.Value + offsets[index], Length = lengths[index] });
        }
        return mapped;
    }

    private static async Task<RootMetadata> ReadRootMetadataAsync(Stream input, uint entriesOffset, IReadOnlyList<uint> keyIndexes, CancellationToken cancellationToken)
    {
        input.Position = entriesOffset + 1;
        _ = await ReadArrayAsync(input, cancellationToken).ConfigureAwait(false);
        var offsets = await ReadArrayAsync(input, cancellationToken).ConfigureAwait(false);
        if (offsets.Count != keyIndexes.Count)
        {
            throw new InvalidDataException("PSB root dictionary key and value counts differ.");
        }

        var valueBase = input.Position;
        var resources = new List<PsbResourceReference>();
        var unsignedIntegers = new List<PsbRootUnsignedInteger>();
        for (var i = 0; i < Math.Min(offsets.Count, keyIndexes.Count); i++)
        {
            input.Position = valueBase + offsets[i]; var type = await ReadByteAsync(input, cancellationToken).ConfigureAwait(false);
            if (type is >= 0x19 and <= 0x1C)
            {
                resources.Add(new PsbResourceReference(i, await ReadUnsignedAsync(input, type - 0x18, cancellationToken).ConfigureAwait(false)));
            }
            else if (type is >= 0x04 and <= 0x08)
            {
                // PSB integer tags encode their byte width as tag - 0x04: 0x04 is zero,
                // 0x05 is one byte, and so on through the four-byte 0x08 form.
                unsignedIntegers.Add(new PsbRootUnsignedInteger(i, await ReadUnsignedAsync(input, type - 0x04, cancellationToken).ConfigureAwait(false)));
            }
        }
        return new RootMetadata(resources, unsignedIntegers);
    }

    private static async Task<List<string>> ReadNamesAsync(Stream input, uint offset, CancellationToken cancellationToken)
    {
        input.Position = offset;
        var charset = await ReadArrayAsync(input, cancellationToken).ConfigureAwait(false);
        var data = await ReadArrayAsync(input, cancellationToken).ConfigureAwait(false);
        var indexes = await ReadArrayAsync(input, cancellationToken).ConfigureAwait(false);
        if (charset.Count > MaxEntries || data.Count > MaxEntries || indexes.Count > MaxEntries) throw new InvalidDataException("PSB name table exceeds the safety limit.");
        var result = new List<string>(indexes.Count);
        foreach (var start in indexes)
        {
            if (start >= data.Count) throw new InvalidDataException("PSB name index is outside the name data table.");
            var bytes = new List<byte>(); var current = start; var steps = 0;
            while (current != 0)
            {
                if (current >= data.Count || steps++ > data.Count) throw new InvalidDataException("PSB name trie contains an invalid cycle.");
                var code = data[(int)current];
                if (code >= data.Count || code >= charset.Count) throw new InvalidDataException("PSB name trie references an invalid character.");
                var delta = charset[(int)code];
                if (current < delta || current - delta > byte.MaxValue) throw new InvalidDataException("PSB name trie character is invalid.");
                bytes.Add((byte)(current - delta)); current = code;
            }
            bytes.Reverse(); result.Add(System.Text.Encoding.UTF8.GetString([.. bytes]).TrimEnd('\0'));
        }
        return result;
    }

    private static async Task<List<uint>> ReadArrayAsync(Stream input, CancellationToken cancellationToken)
    {
        var type = await ReadByteAsync(input, cancellationToken).ConfigureAwait(false);
        if (type is < 0x0D or > 0x10) throw new InvalidDataException("PSB array type is unsupported.");
        var countBytes = type - 0x0C; var count = await ReadUnsignedAsync(input, countBytes, cancellationToken).ConfigureAwait(false);
        var valueType = await ReadByteAsync(input, cancellationToken).ConfigureAwait(false);
        if (valueType is < 0x0D or > 0x10 || count > MaxEntries) throw new InvalidDataException("PSB array length is invalid or excessive.");
        var width = valueType - 0x0C; var values = new List<uint>((int)count);
        for (var i = 0; i < count; i++) values.Add(await ReadUnsignedAsync(input, width, cancellationToken).ConfigureAwait(false));
        return values;
    }

    private static async Task<uint> ReadUnsignedAsync(Stream input, int count, CancellationToken cancellationToken)
    {
        uint value = 0; for (var i = 0; i < count; i++) value |= (uint)(await ReadByteAsync(input, cancellationToken).ConfigureAwait(false) << (i * 8)); return value;
    }
    private static async Task<byte> ReadByteAsync(Stream input, CancellationToken cancellationToken)
    { var value = new byte[1]; if (await input.ReadAsync(value, cancellationToken).ConfigureAwait(false) != 1) throw new InvalidDataException("PSB ended unexpectedly."); return value[0]; }
    private static PsbStructureProbeResult Failure(string code, string message) => new(EvidenceStage.ContainerIdentified, false, Array.Empty<string>(), Array.Empty<PsbResourceReference>(), Array.Empty<PsbRootUnsignedInteger>(), [new KiriScopeDiagnostic(code, DiagnosticSeverity.Error, message)]);

    private sealed record RootMetadata(IReadOnlyList<PsbResourceReference> Resources, IReadOnlyList<PsbRootUnsignedInteger> UnsignedIntegers);
}

/// <summary>Maps a root-dictionary key position to a resource-table entry and, when available, its data range.</summary>
public sealed record PsbResourceReference(int RootKeyIndex, uint ResourceIndex, long? Offset = null, long? Length = null);
/// <summary>Maps a root-dictionary key position to a directly encoded unsigned integer value.</summary>
public sealed record PsbRootUnsignedInteger(int RootKeyIndex, uint Value);
public sealed record PsbStructureProbeResult(EvidenceStage Stage, bool IsPimgCandidate, IReadOnlyList<string> RootKeys, IReadOnlyList<PsbResourceReference> RootResources, IReadOnlyList<PsbRootUnsignedInteger> RootUnsignedIntegers, IReadOnlyList<KiriScopeDiagnostic> Diagnostics);
