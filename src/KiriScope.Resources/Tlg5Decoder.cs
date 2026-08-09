using System.Buffers.Binary;
using KiriScope.Core.Diagnostics;
using KiriScope.Core.Evidence;

namespace KiriScope.Resources;

/// <summary>
/// Decodes the standard, unencrypted TLG5 plane stream into RGBA pixels.
/// The algorithm was independently adapted from the MIT-licensed GARbro TLG reader;
/// attribution is recorded in THIRD_PARTY_NOTICES.md.
/// </summary>
public static class Tlg5Decoder
{
    private const long MaximumDecodedBytes = 256L * 1024 * 1024;
    private const int MaximumPlaneBytes = 64 * 1024 * 1024;

    public static async Task<TlgDecodeResult> DecodeAsync(Stream input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead || !input.CanSeek)
        {
            throw new ArgumentException("Input stream must be readable and seekable.", nameof(input));
        }

        input.Position = 0;
        var metadata = await TlgMetadataReader.ReadAsync(input, cancellationToken).ConfigureAwait(false);
        if (!metadata.IsRecognized)
        {
            return new TlgDecodeResult(metadata.Stage, null, metadata.Diagnostics);
        }

        if (metadata.Version != 5 || metadata.Width is null || metadata.Height is null || metadata.ColorChannels is not (3 or 4) || metadata.DataOffset is null)
        {
            return new TlgDecodeResult(
                metadata.Stage,
                null,
                [new KiriScopeDiagnostic("TLG_DECODE_VARIANT_UNSUPPORTED", DiagnosticSeverity.Warning, "Only standard unencrypted TLG5 with three or four color channels can be decoded currently.")]);
        }

        var width = metadata.Width.Value;
        var height = metadata.Height.Value;
        long decodedLength;
        try
        {
            decodedLength = checked((long)width * height * 4);
        }
        catch (OverflowException)
        {
            return TooLarge(metadata.Stage);
        }

        if (decodedLength > MaximumDecodedBytes || decodedLength > int.MaxValue)
        {
            return TooLarge(metadata.Stage);
        }

        try
        {
            input.Position = metadata.DataOffset.Value;
            var blockHeight = await ReadInt32Async(input, cancellationToken).ConfigureAwait(false);
            if (blockHeight <= 0)
            {
                return Failure("TLG5_BLOCK_HEIGHT_INVALID", "TLG5 block height must be positive.");
            }

            var blockCount = checked((int)(((long)height + blockHeight - 1) / blockHeight));
            if ((long)blockCount * sizeof(uint) > input.Length - input.Position)
            {
                return Failure("TLG5_BLOCK_TABLE_TRUNCATED", "TLG5 ended inside its block-size table.");
            }

            for (var block = 0; block < blockCount; block++)
            {
                _ = await ReadUInt32Async(input, cancellationToken).ConfigureAwait(false);
            }

            var planeLength = checked((long)blockHeight * width);
            if (planeLength > MaximumPlaneBytes)
            {
                return TooLarge(metadata.Stage);
            }

            var planes = new byte[metadata.ColorChannels.Value][];
            for (var channel = 0; channel < planes.Length; channel++)
            {
                planes[channel] = new byte[checked((int)planeLength)];
            }

            var dictionary = new byte[4096];
            var dictionaryPosition = 0;
            var pixels = new byte[checked((int)decodedLength)];
            for (var blockTop = 0; blockTop < height; blockTop += blockHeight)
            {
                for (var channel = 0; channel < planes.Length; channel++)
                {
                    var marker = await ReadByteAsync(input, cancellationToken).ConfigureAwait(false);
                    var encodedLength = await ReadUInt32Async(input, cancellationToken).ConfigureAwait(false);
                    if (encodedLength > MaximumPlaneBytes || encodedLength > input.Length - input.Position)
                    {
                        return Failure("TLG5_PLANE_LENGTH_INVALID", "TLG5 channel-plane length is excessive or extends beyond the input.");
                    }

                    var encoded = await ReadExactlyAsync(input, checked((int)encodedLength), cancellationToken).ConfigureAwait(false);
                    if (marker == 0)
                    {
                        DecompressPlane(encoded, planes[channel], dictionary, ref dictionaryPosition);
                    }
                    else if (marker == 1 && encoded.Length == planes[channel].Length)
                    {
                        encoded.CopyTo(planes[channel], 0);
                    }
                    else
                    {
                        return Failure("TLG5_PLANE_ENCODING_UNSUPPORTED", "TLG5 channel plane uses an unsupported marker or raw length.");
                    }
                }

                var rowsInBlock = Math.Min(blockHeight, height - blockTop);
                ComposeBlock(pixels, width, blockTop, rowsInBlock, planes);
            }

            return new TlgDecodeResult(
                EvidenceStage.ContentUsable,
                new RgbaImage(width, height, pixels),
                [new KiriScopeDiagnostic("TLG5_PIXELS_DECODED", DiagnosticSeverity.Info, "Standard TLG5 planes were decoded to top-down RGBA pixels.")]);
        }
        catch (InvalidDataException exception)
        {
            return Failure("TLG5_DATA_INVALID", exception.Message);
        }
        catch (OverflowException)
        {
            return TooLarge(metadata.Stage);
        }
    }

    private static void ComposeBlock(byte[] output, int width, int blockTop, int rowsInBlock, IReadOnlyList<byte[]> planes)
    {
        for (var rowInBlock = 0; rowInBlock < rowsInBlock; rowInBlock++)
        {
            byte previousBlue = 0, previousGreen = 0, previousRed = 0, previousAlpha = 0;
            var planeOffset = rowInBlock * width;
            var outputOffset = (blockTop + rowInBlock) * width * 4;
            var upperOffset = outputOffset - width * 4;
            for (var column = 0; column < width; column++)
            {
                var planeIndex = planeOffset + column;
                var blueDifference = Add(planes[0][planeIndex], planes[1][planeIndex]);
                var greenDifference = planes[1][planeIndex];
                var redDifference = Add(planes[2][planeIndex], planes[1][planeIndex]);
                var alphaDifference = planes.Count == 4 ? planes[3][planeIndex] : byte.MaxValue;
                previousBlue = Add(previousBlue, blueDifference);
                previousGreen = Add(previousGreen, greenDifference);
                previousRed = Add(previousRed, redDifference);
                previousAlpha = planes.Count == 4 ? Add(previousAlpha, alphaDifference) : byte.MaxValue;
                if (blockTop + rowInBlock > 0)
                {
                    previousBlue = Add(previousBlue, output[upperOffset + column * 4 + 2]);
                    previousGreen = Add(previousGreen, output[upperOffset + column * 4 + 1]);
                    previousRed = Add(previousRed, output[upperOffset + column * 4]);
                    if (planes.Count == 4)
                    {
                        previousAlpha = Add(previousAlpha, output[upperOffset + column * 4 + 3]);
                    }
                }

                var destination = outputOffset + column * 4;
                output[destination] = previousRed;
                output[destination + 1] = previousGreen;
                output[destination + 2] = previousBlue;
                output[destination + 3] = previousAlpha;
            }
        }
    }

    private static void DecompressPlane(ReadOnlySpan<byte> encoded, Span<byte> destination, byte[] dictionary, ref int dictionaryPosition)
    {
        var source = 0;
        var destinationPosition = 0;
        uint flags = 0;
        while (source < encoded.Length)
        {
            if (((flags >>= 1) & 0x100) == 0)
            {
                if (source >= encoded.Length) throw new InvalidDataException("TLG5 compressed plane ended before its flag byte.");
                flags = (uint)(encoded[source++] | 0xFF00);
            }

            if ((flags & 1) == 0)
            {
                if (source >= encoded.Length || destinationPosition >= destination.Length) throw new InvalidDataException("TLG5 literal data exceeds its plane bounds.");
                var value = encoded[source++];
                destination[destinationPosition++] = value;
                dictionary[dictionaryPosition] = value;
                dictionaryPosition = (dictionaryPosition + 1) & 0xFFF;
                continue;
            }

            if (source + 2 > encoded.Length) throw new InvalidDataException("TLG5 back-reference is truncated.");
            var matchPosition = encoded[source] | ((encoded[source + 1] & 0x0F) << 8);
            var matchLength = (encoded[source + 1] >> 4) + 3;
            source += 2;
            if (matchLength == 18)
            {
                if (source >= encoded.Length) throw new InvalidDataException("TLG5 long back-reference is truncated.");
                matchLength += encoded[source++];
            }

            if (matchLength > destination.Length - destinationPosition) throw new InvalidDataException("TLG5 back-reference exceeds its plane bounds.");
            for (var copy = 0; copy < matchLength; copy++)
            {
                var value = dictionary[matchPosition];
                matchPosition = (matchPosition + 1) & 0xFFF;
                destination[destinationPosition++] = value;
                dictionary[dictionaryPosition] = value;
                dictionaryPosition = (dictionaryPosition + 1) & 0xFFF;
            }
        }

        if (destinationPosition != destination.Length) throw new InvalidDataException("TLG5 compressed plane did not produce the expected number of bytes.");
    }

    private static byte Add(byte left, byte right) => unchecked((byte)(left + right));

    private static async Task<byte> ReadByteAsync(Stream input, CancellationToken cancellationToken)
    {
        var value = await ReadExactlyAsync(input, 1, cancellationToken).ConfigureAwait(false);
        return value[0];
    }

    private static async Task<int> ReadInt32Async(Stream input, CancellationToken cancellationToken) =>
        BinaryPrimitives.ReadInt32LittleEndian(await ReadExactlyAsync(input, sizeof(int), cancellationToken).ConfigureAwait(false));

    private static async Task<uint> ReadUInt32Async(Stream input, CancellationToken cancellationToken) =>
        BinaryPrimitives.ReadUInt32LittleEndian(await ReadExactlyAsync(input, sizeof(uint), cancellationToken).ConfigureAwait(false));

    private static async Task<byte[]> ReadExactlyAsync(Stream input, int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new InvalidDataException("TLG5 ended unexpectedly.");
            offset += read;
        }

        return buffer;
    }

    private static TlgDecodeResult Failure(string code, string message) =>
        new(EvidenceStage.ContainerIdentified, null, [new KiriScopeDiagnostic(code, DiagnosticSeverity.Error, message)]);

    private static TlgDecodeResult TooLarge(EvidenceStage stage) =>
        new(stage, null, [new KiriScopeDiagnostic("TLG5_DECODE_TOO_LARGE", DiagnosticSeverity.Warning, "TLG5 dimensions or planes exceed the configured safe decoded-image limit.")]);
}

public sealed record TlgDecodeResult(EvidenceStage Stage, RgbaImage? Image, IReadOnlyList<KiriScopeDiagnostic> Diagnostics)
{
    public bool Succeeded => Image is not null && Stage >= EvidenceStage.ContentUsable;
}
