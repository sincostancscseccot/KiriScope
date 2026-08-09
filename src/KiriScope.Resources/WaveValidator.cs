using System.Buffers.Binary;
using KiriScope.Core.Diagnostics;
using KiriScope.Core.Evidence;

namespace KiriScope.Resources;

/// <summary>Validates RIFF/WAVE chunk bounds and the metadata required for uncompressed PCM or IEEE-float audio.</summary>
public static class WaveValidator
{
    private const int RiffHeaderLength = 12;
    private const int ChunkHeaderLength = 8;
    private const uint MaximumFormatChunkLength = 4096;
    private const ushort PcmFormat = 1;
    private const ushort IeeeFloatFormat = 3;
    private const ushort ExtensibleFormat = 0xFFFE;

    public static async Task<WaveValidationResult> ValidateAsync(Stream input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead || !input.CanSeek)
        {
            throw new ArgumentException("Input stream must be readable and seekable.", nameof(input));
        }

        if (input.Length < RiffHeaderLength)
        {
            return Failure("WAVE_RIFF_HEADER_TRUNCATED", "WAVE ended before the complete RIFF header.", EvidenceStage.Unidentified);
        }

        input.Position = 0;
        var riff = await ReadExactlyAsync(input, RiffHeaderLength, cancellationToken).ConfigureAwait(false);
        if (!riff.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !riff.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            return Failure("WAVE_SIGNATURE_MISMATCH", "Input does not have the RIFF/WAVE signature.", EvidenceStage.Unidentified);
        }

        var riffSize = BinaryPrimitives.ReadUInt32LittleEndian(riff.AsSpan(4, sizeof(uint)));
        var riffEnd = checked(8L + riffSize);
        if (riffSize < 4 || riffEnd > input.Length)
        {
            return Failure("WAVE_RIFF_LENGTH_INVALID", "RIFF declared length extends beyond the input.");
        }

        ushort? formatTag = null;
        ushort? channels = null;
        uint? sampleRate = null;
        ushort? bitsPerSample = null;
        ushort? blockAlignment = null;
        var sawFormat = false;
        var sawData = false;
        long dataBytes = 0;
        while (input.Position < riffEnd)
        {
            if (riffEnd - input.Position < ChunkHeaderLength)
            {
                return Failure("WAVE_CHUNK_HEADER_TRUNCATED", "RIFF ended before a complete chunk header.", formatTag, channels, sampleRate, bitsPerSample, dataBytes);
            }

            var chunkHeader = await ReadExactlyAsync(input, ChunkHeaderLength, cancellationToken).ConfigureAwait(false);
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.AsSpan(4, sizeof(uint)));
            var chunkDataStart = input.Position;
            var chunkDataEnd = checked(chunkDataStart + (long)chunkSize);
            var paddedChunkEnd = checked(chunkDataEnd + (chunkSize & 1));
            if (paddedChunkEnd > riffEnd)
            {
                return Failure("WAVE_CHUNK_TRUNCATED", "RIFF chunk extends beyond the declared RIFF length.", formatTag, channels, sampleRate, bitsPerSample, dataBytes);
            }

            if (chunkHeader.AsSpan(0, 4).SequenceEqual("fmt "u8))
            {
                if (sawFormat || sawData || chunkSize < 16 || chunkSize > MaximumFormatChunkLength)
                {
                    return Failure("WAVE_FORMAT_CHUNK_INVALID", "WAVE fmt chunk is duplicated, misplaced, truncated, or excessive.", formatTag, channels, sampleRate, bitsPerSample, dataBytes);
                }

                var format = await ReadExactlyAsync(input, checked((int)chunkSize), cancellationToken).ConfigureAwait(false);
                formatTag = BinaryPrimitives.ReadUInt16LittleEndian(format);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(2, sizeof(ushort)));
                sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(4, sizeof(uint)));
                var averageBytesPerSecond = BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(8, sizeof(uint)));
                blockAlignment = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(12, sizeof(ushort)));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(14, sizeof(ushort)));
                if (channels == 0 || sampleRate == 0 || blockAlignment == 0 || bitsPerSample == 0)
                {
                    return Failure("WAVE_FORMAT_VALUES_INVALID", "WAVE format metadata contains zero or invalid values.", formatTag, channels, sampleRate, bitsPerSample, dataBytes);
                }

                if (formatTag == ExtensibleFormat)
                {
                    if (format.Length < 40 || BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(16, sizeof(ushort))) < 22)
                    {
                        return Failure("WAVE_EXTENSIBLE_FORMAT_INVALID", "WAVE extensible format chunk is incomplete.", formatTag, channels, sampleRate, bitsPerSample, dataBytes);
                    }
                }

                if (IsPcmOrFloat(formatTag.Value) && !HasConsistentUncompressedMetadata(channels.Value, sampleRate.Value, blockAlignment.Value, bitsPerSample.Value, averageBytesPerSecond))
                {
                    return Failure("WAVE_UNCOMPRESSED_METADATA_INVALID", "WAVE uncompressed metadata has inconsistent byte rate or block alignment.", formatTag, channels, sampleRate, bitsPerSample, dataBytes);
                }

                sawFormat = true;
            }
            else if (chunkHeader.AsSpan(0, 4).SequenceEqual("data"u8))
            {
                if (!sawFormat)
                {
                    return Failure("WAVE_DATA_BEFORE_FORMAT", "WAVE data chunk occurs before its fmt chunk.", formatTag, channels, sampleRate, bitsPerSample, dataBytes);
                }

                sawData = true;
                dataBytes = checked(dataBytes + chunkSize);
                input.Position = chunkDataEnd;
            }
            else
            {
                input.Position = chunkDataEnd;
            }

            if ((chunkSize & 1) != 0)
            {
                input.Position++;
            }
        }

        if (!sawFormat || !sawData)
        {
            return Failure("WAVE_REQUIRED_CHUNK_MISSING", "WAVE is missing its fmt or data chunk.", formatTag, channels, sampleRate, bitsPerSample, dataBytes);
        }

        if (IsPcmOrFloat(formatTag!.Value))
        {
            if (dataBytes % blockAlignment!.Value != 0)
            {
                return Failure("WAVE_DATA_ALIGNMENT_INVALID", "WAVE data length does not align to complete sample frames.", formatTag, channels, sampleRate, bitsPerSample, dataBytes);
            }

            return new WaveValidationResult(
                EvidenceStage.FormatValidated,
                formatTag,
                channels,
                sampleRate,
                bitsPerSample,
                dataBytes,
                [new KiriScopeDiagnostic("WAVE_UNCOMPRESSED_VALIDATED", DiagnosticSeverity.Info, "RIFF/WAVE chunk boundaries and uncompressed audio metadata were verified.")]);
        }

        return new WaveValidationResult(
            EvidenceStage.ContainerIdentified,
            formatTag,
            channels,
            sampleRate,
            bitsPerSample,
            dataBytes,
            [new KiriScopeDiagnostic("WAVE_COMPRESSED_CONTAINER_IDENTIFIED", DiagnosticSeverity.Info, "RIFF/WAVE structure was verified, but this compressed codec has not been decoded.")]);
    }

    private static bool IsPcmOrFloat(ushort formatTag) => formatTag is PcmFormat or IeeeFloatFormat;

    private static bool HasConsistentUncompressedMetadata(ushort channels, uint sampleRate, ushort blockAlignment, ushort bitsPerSample, uint averageBytesPerSecond)
    {
        if (bitsPerSample % 8 != 0 || bitsPerSample is not (8 or 16 or 24 or 32 or 64))
        {
            return false;
        }

        var expectedBlockAlignment = checked(channels * (bitsPerSample / 8));
        return blockAlignment == expectedBlockAlignment && averageBytesPerSecond == checked(sampleRate * (uint)blockAlignment);
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream input, int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidDataException("WAVE ended unexpectedly.");
            }

            offset += read;
        }

        return buffer;
    }

    private static WaveValidationResult Failure(
        string code,
        string message,
        ushort? formatTag,
        ushort? channels,
        uint? sampleRate,
        ushort? bitsPerSample,
        long dataBytes) =>
        Failure(code, message, EvidenceStage.ContainerIdentified, formatTag, channels, sampleRate, bitsPerSample, dataBytes);

    private static WaveValidationResult Failure(
        string code,
        string message,
        EvidenceStage stage = EvidenceStage.ContainerIdentified,
        ushort? formatTag = null,
        ushort? channels = null,
        uint? sampleRate = null,
        ushort? bitsPerSample = null,
        long dataBytes = 0) =>
        new(stage, formatTag, channels, sampleRate, bitsPerSample, dataBytes, [new KiriScopeDiagnostic(code, DiagnosticSeverity.Error, message)]);
}
