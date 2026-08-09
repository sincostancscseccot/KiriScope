using System.Buffers.Binary;
using KiriScope.Core.Diagnostics;
using KiriScope.Core.Evidence;

namespace KiriScope.Resources;

/// <summary>
/// Reads the stable, unencrypted TLG5/TLG6 header.  It validates only metadata;
/// decoding the compressed image body belongs to a later ContentUsable-stage feature.
/// </summary>
public static class TlgMetadataReader
{
    private static ReadOnlySpan<byte> SdsWrapperPrefix => "TLG0.0\0sds\x1a"u8;
    private static ReadOnlySpan<byte> RawMarker => "\0raw\x1a"u8;
    private static ReadOnlySpan<byte> Tlg5Marker => "TLG5.0"u8;
    private static ReadOnlySpan<byte> Tlg6Marker => "TLG6.0"u8;
    private const int MaximumHeaderLength = 0x26;
    private const int SdsWrapperLength = 0x0F;

    public static async Task<TlgValidationResult> ReadAsync(
        Stream input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead)
        {
            throw new ArgumentException("Input stream must be readable.", nameof(input));
        }

        var header = await ReadAtMostAsync(input, MaximumHeaderLength, cancellationToken).ConfigureAwait(false);
        // The TLG0 SDS wrapper consists of its 11-byte signature plus a 4-byte field.
        // The following actual TLG stream therefore begins at offset 0x0F.
        var offset = header.AsSpan().StartsWith(SdsWrapperPrefix) ? SdsWrapperLength : 0;
        var hasSdsWrapper = offset != 0;

        if (header.Length < offset + 12 || !header.AsSpan(offset + 6, RawMarker.Length).SequenceEqual(RawMarker))
        {
            return Failure("TLG_RAW_MARKER_MISSING", "Input does not contain a supported unencrypted TLG raw marker.", hasSdsWrapper);
        }

        var version = header.AsSpan(offset, 6) switch
        {
            var marker when marker.SequenceEqual(Tlg5Marker) => 5,
            var marker when marker.SequenceEqual(Tlg6Marker) => 6,
            _ => 0,
        };
        if (version == 0)
        {
            return Failure("TLG_VERSION_UNSUPPORTED", "TLG header version is not a supported plain TLG5 or TLG6 variant.", hasSdsWrapper);
        }

        var colors = header[offset + 11];
        var dimensionsOffset = version == 6 ? offset + 15 : offset + 12;
        if (header.Length < dimensionsOffset + 8)
        {
            return Failure("TLG_METADATA_TRUNCATED", "TLG ended before width and height metadata.", hasSdsWrapper, version, colors);
        }

        if ((version == 5 && colors is not (3 or 4)) ||
            (version == 6 && colors is not (1 or 3 or 4)))
        {
            return Failure("TLG_COLOR_CHANNELS_INVALID", "TLG color-channel count is invalid for this version.", hasSdsWrapper, version, colors);
        }

        if (version == 6 && (header[offset + 12] != 0 || header[offset + 13] != 0 || header[offset + 14] != 0))
        {
            return Failure("TLG6_RESERVED_BYTES_INVALID", "TLG6 reserved header bytes must be zero.", hasSdsWrapper, version, colors);
        }

        var encodedWidth = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(dimensionsOffset, sizeof(uint)));
        var encodedHeight = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(dimensionsOffset + sizeof(uint), sizeof(uint)));
        if (encodedWidth is 0 or > int.MaxValue || encodedHeight is 0 or > int.MaxValue)
        {
            return Failure("TLG_DIMENSIONS_INVALID", "TLG dimensions are outside the supported range.", hasSdsWrapper, version, colors);
        }

        var dataOffset = dimensionsOffset + 8;
        return new TlgValidationResult(
            EvidenceStage.ContainerIdentified,
            version,
            (int)encodedWidth,
            (int)encodedHeight,
            colors,
            dataOffset,
            hasSdsWrapper,
            [new KiriScopeDiagnostic(
                "TLG_METADATA_IDENTIFIED",
                DiagnosticSeverity.Info,
                "TLG header and image metadata were validated. Pixel data has not been decoded.")]);
    }

    private static async Task<byte[]> ReadAtMostAsync(Stream input, int maximumLength, CancellationToken cancellationToken)
    {
        var buffer = new byte[maximumLength];
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

        return buffer.AsSpan(0, totalRead).ToArray();
    }

    private static TlgValidationResult Failure(
        string code,
        string message,
        bool hasSdsWrapper,
        int? version = null,
        byte? colorChannels = null) =>
        new(
            EvidenceStage.Unidentified,
            version,
            null,
            null,
            colorChannels,
            null,
            hasSdsWrapper,
            [new KiriScopeDiagnostic(code, DiagnosticSeverity.Error, message)]);
}
