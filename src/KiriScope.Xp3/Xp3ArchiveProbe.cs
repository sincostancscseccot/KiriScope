using System.Buffers.Binary;
using KiriScope.Core.Diagnostics;
using KiriScope.Core.Evidence;

namespace KiriScope.Xp3;

/// <summary>Performs a non-destructive XP3 header probe. Full index parsing follows in stage 1.</summary>
public static class Xp3ArchiveProbe
{
    private const int IndexOffsetLength = sizeof(long);
    private const int HeaderLength = 11 + IndexOffsetLength;

    public static async Task<Xp3ProbeResult> ProbeAsync(Stream input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead)
        {
            throw new ArgumentException("Input stream must be readable.", nameof(input));
        }

        var header = new byte[HeaderLength];
        var read = await ReadAtMostAsync(input, header, cancellationToken).ConfigureAwait(false);
        if (read < Xp3Signature.Bytes.Length)
        {
            return Failure("XP3_HEADER_TOO_SHORT", "Input is shorter than the XP3 signature.");
        }

        if (!header.AsSpan(0, Xp3Signature.Bytes.Length).SequenceEqual(Xp3Signature.Bytes))
        {
            return Failure("XP3_SIGNATURE_MISMATCH", "Input does not start with the standard XP3 signature.");
        }

        if (read < HeaderLength)
        {
            return new Xp3ProbeResult(
                EvidenceStage.ContainerIdentified,
                null,
                [new KiriScopeDiagnostic("XP3_INDEX_OFFSET_MISSING", DiagnosticSeverity.Error,
                    "XP3 signature is present but the first index offset is truncated.", Xp3Signature.Bytes.Length)]);
        }

        var indexOffset = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(Xp3Signature.Bytes.Length, IndexOffsetLength));
        if (indexOffset < HeaderLength)
        {
            return new Xp3ProbeResult(
                EvidenceStage.ContainerIdentified,
                indexOffset,
                [new KiriScopeDiagnostic("XP3_INDEX_OFFSET_INVALID", DiagnosticSeverity.Error,
                    "The first index offset points into the XP3 header.", Xp3Signature.Bytes.Length)]);
        }

        return new Xp3ProbeResult(
            EvidenceStage.ContainerIdentified,
            indexOffset,
            [new KiriScopeDiagnostic("XP3_HEADER_IDENTIFIED", DiagnosticSeverity.Info,
                "Standard XP3 signature and first index offset were identified.")]);
    }

    private static Xp3ProbeResult Failure(string code, string message) =>
        new(EvidenceStage.Unidentified, null, [new KiriScopeDiagnostic(code, DiagnosticSeverity.Error, message)]);

    private static async Task<int> ReadAtMostAsync(Stream input, Memory<byte> destination, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < destination.Length)
        {
            var bytesRead = await input.ReadAsync(destination[totalRead..], cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            totalRead += bytesRead;
        }

        return totalRead;
    }
}
