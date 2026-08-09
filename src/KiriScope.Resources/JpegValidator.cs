using System.Buffers.Binary;
using KiriScope.Core.Diagnostics;
using KiriScope.Core.Evidence;

namespace KiriScope.Resources;

/// <summary>Validates JPEG marker framing, frame metadata, scan framing, and end-of-image termination without decoding DCT samples.</summary>
public static class JpegValidator
{
    private const byte StartOfImage = 0xD8;
    private const byte EndOfImage = 0xD9;
    private const byte StartOfScan = 0xDA;

    public static async Task<JpegValidationResult> ValidateAsync(Stream input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead || !input.CanSeek)
        {
            throw new ArgumentException("Input stream must be readable and seekable.", nameof(input));
        }

        input.Position = 0;
        if (input.Length < 4 || await ReadByteAsync(input, cancellationToken).ConfigureAwait(false) != 0xFF || await ReadByteAsync(input, cancellationToken).ConfigureAwait(false) != StartOfImage)
        {
            return Failure("JPEG_SIGNATURE_MISMATCH", "Input does not have the JPEG start-of-image marker.", EvidenceStage.Unidentified);
        }

        int? width = null;
        int? height = null;
        byte? precision = null;
        byte? components = null;
        var sawFrame = false;
        var scans = 0;
        byte? pendingMarker = null;
        try
        {
            while (pendingMarker is not null || input.Position < input.Length)
            {
                var marker = pendingMarker ?? await ReadMarkerAsync(input, cancellationToken).ConfigureAwait(false);
                pendingMarker = null;
                if (marker == EndOfImage)
                {
                    if (!sawFrame || scans == 0)
                    {
                        return Failure("JPEG_REQUIRED_MARKER_MISSING", "JPEG is missing a frame or scan before end-of-image.", width, height, precision, components, scans);
                    }

                    return new JpegValidationResult(
                        EvidenceStage.FormatValidated,
                        width,
                        height,
                        precision,
                        components,
                        scans,
                        [new KiriScopeDiagnostic("JPEG_VALIDATED", DiagnosticSeverity.Info, "JPEG marker framing, frame metadata, scan framing, and end-of-image marker were verified.")]);
                }

                if (marker == StartOfImage || IsRestartMarker(marker))
                {
                    return Failure("JPEG_MARKER_ORDER_INVALID", "JPEG contains a standalone marker in an invalid position.", width, height, precision, components, scans);
                }

                if (marker == 0x01)
                {
                    continue;
                }

                var segmentLength = await ReadUInt16BigEndianAsync(input, cancellationToken).ConfigureAwait(false);
                if (segmentLength < 2 || segmentLength - 2 > input.Length - input.Position)
                {
                    return Failure("JPEG_SEGMENT_TRUNCATED", "JPEG marker segment is truncated or has an invalid length.", width, height, precision, components, scans);
                }

                var segment = await ReadExactlyAsync(input, segmentLength - 2, cancellationToken).ConfigureAwait(false);
                if (IsStartOfFrame(marker))
                {
                    if (sawFrame || segment.Length < 8)
                    {
                        return Failure("JPEG_FRAME_MARKER_INVALID", "JPEG frame marker is duplicated or too short.", width, height, precision, components, scans);
                    }

                    precision = segment[0];
                    height = BinaryPrimitives.ReadUInt16BigEndian(segment.AsSpan(1, sizeof(ushort)));
                    width = BinaryPrimitives.ReadUInt16BigEndian(segment.AsSpan(3, sizeof(ushort)));
                    components = segment[5];
                    if (precision is 0 or > 16 || width == 0 || height == 0 || components is 0 or > 4 || segment.Length != 6 + components * 3)
                    {
                        return Failure("JPEG_FRAME_VALUES_INVALID", "JPEG frame dimensions, precision, component count, or component table are invalid.", width, height, precision, components, scans);
                    }

                    sawFrame = true;
                }
                else if (marker == StartOfScan)
                {
                    if (!sawFrame || segment.Length < 6 || segment[0] is 0 or > 4 || segment.Length != 4 + segment[0] * 2)
                    {
                        return Failure("JPEG_SCAN_MARKER_INVALID", "JPEG scan marker is missing its frame or has an invalid component table.", width, height, precision, components, scans);
                    }

                    scans++;
                    var followingMarker = await ScanEntropyDataAsync(input, cancellationToken).ConfigureAwait(false);
                    pendingMarker = followingMarker;
                }
            }
        }
        catch (InvalidDataException exception)
        {
            return Failure("JPEG_DATA_INVALID", exception.Message, width, height, precision, components, scans);
        }

        return Failure("JPEG_END_MARKER_MISSING", "JPEG ended before an end-of-image marker.", width, height, precision, components, scans);
    }

    private static bool IsStartOfFrame(byte marker) => marker is >= 0xC0 and <= 0xCF && marker is not 0xC4 and not 0xC8 and not 0xCC;

    private static bool IsRestartMarker(byte marker) => marker is >= 0xD0 and <= 0xD7;

    private static async Task<byte> ScanEntropyDataAsync(Stream input, CancellationToken cancellationToken)
    {
        while (input.Position < input.Length)
        {
            if (await ReadByteAsync(input, cancellationToken).ConfigureAwait(false) != 0xFF)
            {
                continue;
            }

            byte marker;
            do
            {
                marker = await ReadByteAsync(input, cancellationToken).ConfigureAwait(false);
            }
            while (marker == 0xFF);

            if (marker == 0)
            {
                continue;
            }

            if (IsRestartMarker(marker))
            {
                continue;
            }

            return marker;
        }

        throw new InvalidDataException("JPEG scan data ended before a following marker.");
    }

    private static async Task<byte> ReadMarkerAsync(Stream input, CancellationToken cancellationToken)
    {
        if (await ReadByteAsync(input, cancellationToken).ConfigureAwait(false) != 0xFF)
        {
            throw new InvalidDataException("JPEG expected a marker prefix.");
        }

        byte marker;
        do
        {
            marker = await ReadByteAsync(input, cancellationToken).ConfigureAwait(false);
        }
        while (marker == 0xFF);
        if (marker == 0)
        {
            throw new InvalidDataException("JPEG marker prefix is followed by an invalid stuffed byte.");
        }

        return marker;
    }

    private static async Task<byte> ReadByteAsync(Stream input, CancellationToken cancellationToken)
    {
        var buffer = await ReadExactlyAsync(input, 1, cancellationToken).ConfigureAwait(false);
        return buffer[0];
    }

    private static async Task<ushort> ReadUInt16BigEndianAsync(Stream input, CancellationToken cancellationToken) =>
        BinaryPrimitives.ReadUInt16BigEndian(await ReadExactlyAsync(input, sizeof(ushort), cancellationToken).ConfigureAwait(false));

    private static async Task<byte[]> ReadExactlyAsync(Stream input, int length, CancellationToken cancellationToken)
    {
        var data = new byte[length];
        var offset = 0;
        while (offset < data.Length)
        {
            var read = await input.ReadAsync(data.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new InvalidDataException("JPEG ended unexpectedly.");
            offset += read;
        }

        return data;
    }

    private static JpegValidationResult Failure(
        string code,
        string message,
        EvidenceStage stage = EvidenceStage.ContainerIdentified,
        int? width = null,
        int? height = null,
        byte? precision = null,
        byte? components = null,
        int scans = 0) =>
        new(stage, width, height, precision, components, scans, [new KiriScopeDiagnostic(code, DiagnosticSeverity.Error, message)]);

    private static JpegValidationResult Failure(string code, string message, int? width, int? height, byte? precision, byte? components, int scans) =>
        Failure(code, message, EvidenceStage.ContainerIdentified, width, height, precision, components, scans);
}
