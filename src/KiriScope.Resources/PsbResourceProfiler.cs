using KiriScope.Core.Diagnostics;
using KiriScope.Core.Evidence;

namespace KiriScope.Resources;

/// <summary>
/// Profiles direct PSB/PIMG root resources by reading only their short leading headers.
/// It does not copy, decode, or modify resource data.
/// </summary>
public static class PsbResourceProfiler
{
    private const int ProfileHeaderLength = 0x26;
    private const int MaximumProfiledResources = 10_000;

    public static async Task<PsbResourceProfileResult> ProfileAsync(Stream input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead || !input.CanSeek)
        {
            throw new ArgumentException("PSB resource profiling requires a readable, seekable input stream.", nameof(input));
        }

        var structure = await PsbStructureProbe.ProbeAsync(input, cancellationToken).ConfigureAwait(false);
        var references = structure.RootResources
            .Where(reference => reference.Offset is not null && reference.Length is not null)
            .OrderBy(reference => reference.ResourceIndex)
            .ThenBy(reference => structure.RootKeys[reference.RootKeyIndex], StringComparer.Ordinal)
            .ThenBy(reference => reference.RootKeyIndex)
            .ToArray();
        var diagnostics = structure.Diagnostics.ToList();
        if (references.Length > MaximumProfiledResources)
        {
            diagnostics.Add(new KiriScopeDiagnostic("PSB_RESOURCE_PROFILE_CAPPED", DiagnosticSeverity.Warning,
                $"Only the first {MaximumProfiledResources:N0} direct root resources were profiled."));
            references = references[..MaximumProfiledResources];
        }

        var resources = new List<PsbEmbeddedResourceProfile>(references.Length);
        foreach (var reference in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            input.Position = reference.Offset!.Value;
            var header = await ReadAtMostAsync(input, (int)Math.Min(ProfileHeaderLength, reference.Length!.Value), cancellationToken).ConfigureAwait(false);
            var format = ResourceFormatDetector.Detect(header);
            int? tlgVersion = null;
            int? width = null;
            int? height = null;
            var tlgMetadataRecognized = false;
            if (format == ResourceFormat.Tlg)
            {
                await using var metadataInput = new MemoryStream(header, writable: false);
                var metadata = await TlgMetadataReader.ReadAsync(metadataInput, cancellationToken).ConfigureAwait(false);
                if (metadata.IsRecognized)
                {
                    tlgVersion = metadata.Version;
                    width = metadata.Width;
                    height = metadata.Height;
                    tlgMetadataRecognized = true;
                }
            }

            resources.Add(new PsbEmbeddedResourceProfile(
                structure.RootKeys[reference.RootKeyIndex],
                reference.ResourceIndex,
                reference.Offset.Value,
                reference.Length.Value,
                format,
                tlgVersion,
                width,
                height,
                tlgMetadataRecognized));
        }

        diagnostics.Add(new KiriScopeDiagnostic("PSB_DIRECT_RESOURCES_PROFILED", DiagnosticSeverity.Info,
            "Direct PSB root resources were profiled from their leading bytes only; no resource data was copied or decoded."));
        return new PsbResourceProfileResult(structure.Stage, structure.IsPimgCandidate, structure.RootUnsignedIntegers, resources, diagnostics);
    }

    private static async Task<byte[]> ReadAtMostAsync(Stream input, int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(totalRead), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead == buffer.Length ? buffer : buffer[..totalRead];
    }
}

public sealed record PsbEmbeddedResourceProfile(
    string ResourceName,
    uint ResourceIndex,
    long Offset,
    long Length,
    ResourceFormat DetectedFormat,
    int? TlgVersion,
    int? Width,
    int? Height,
    bool TlgMetadataRecognized);

public sealed record PsbResourceProfileResult(
    EvidenceStage Stage,
    bool IsPimgCandidate,
    IReadOnlyList<PsbRootUnsignedInteger> RootUnsignedIntegers,
    IReadOnlyList<PsbEmbeddedResourceProfile> Resources,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics);
