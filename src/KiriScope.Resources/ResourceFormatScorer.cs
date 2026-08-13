using KiriScope.Core.Diagnostics;
using KiriScope.Core.Evidence;

namespace KiriScope.Resources;

/// <summary>
/// Assigns a conservative format score. Only a fully validated format is an accepted candidate;
/// signatures and partially identified containers remain evidence, not decryption success.
/// </summary>
public static class ResourceFormatScorer
{
    public static async Task<ResourceFormatScore> ScoreAsync(
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new MemoryStream(content.ToArray(), writable: false);
        return await ScoreAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Scores a seekable resource stream without requiring callers to materialize the entire file in memory.</summary>
    public static async Task<ResourceFormatScore> ScoreAsync(
        Stream input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead || !input.CanSeek)
        {
            throw new ArgumentException("Resource scoring requires a readable, seekable stream.", nameof(input));
        }

        input.Position = 0;
        var header = new byte[32];
        var headerLength = await input.ReadAsync(header, cancellationToken).ConfigureAwait(false);
        var format = ResourceFormatDetector.Detect(header.AsSpan(0, headerLength));
        input.Position = 0;

        return format switch
        {
            ResourceFormat.Png => FromPng(await PngValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false)),
            ResourceFormat.Bmp => FromBmp(await BmpValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false)),
            ResourceFormat.Wave => FromWave(await WaveValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false)),
            ResourceFormat.Jpeg => FromJpeg(await JpegValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false)),
            ResourceFormat.Tlg => FromTlg(await TlgMetadataReader.ReadAsync(input, cancellationToken).ConfigureAwait(false)),
            ResourceFormat.Psb => await FromPsbAsync(input, cancellationToken).ConfigureAwait(false),
            ResourceFormat.Pimg => IdentifiedOnly(format, "PIMG_FORMAT_IDENTIFIED", "PIMG signature identified, but no structural validator is available yet."),
            ResourceFormat.Ogg => IdentifiedOnly(format, "OGG_FORMAT_IDENTIFIED", "Ogg signature identified, but no structural validator is available yet."),
            ResourceFormat.MpegProgramStream => IdentifiedOnly(format, "MPEG_PROGRAM_STREAM_IDENTIFIED", "MPEG Program Stream pack signature identified."),
            ResourceFormat.OpenTypeFont => IdentifiedOnly(format, "OPENTYPE_FONT_IDENTIFIED", "OpenType font signature identified."),
            _ => new ResourceFormatScore(
                ResourceFormat.Unknown,
                EvidenceStage.Unidentified,
                0,
                [new KiriScopeDiagnostic("RESOURCE_FORMAT_UNRECOGNIZED", DiagnosticSeverity.Warning, "No supported resource signature was found after applying the candidate filter.")]),
        };
    }

    private static ResourceFormatScore FromPng(PngValidationResult result) =>
        new(ResourceFormat.Png, result.Stage, result.IsValid ? 100 : 20, result.Diagnostics);

    private static ResourceFormatScore FromBmp(BmpValidationResult result) =>
        new(ResourceFormat.Bmp, result.Stage, result.IsValid ? 100 : 20, result.Diagnostics);

    private static ResourceFormatScore FromWave(WaveValidationResult result) =>
        new(ResourceFormat.Wave, result.Stage, result.IsValid ? 100 : 20, result.Diagnostics);

    private static ResourceFormatScore FromJpeg(JpegValidationResult result) =>
        new(ResourceFormat.Jpeg, result.Stage, result.IsValid ? 100 : 20, result.Diagnostics);

    private static ResourceFormatScore FromTlg(TlgValidationResult result) =>
        new(ResourceFormat.Tlg, result.Stage, result.IsRecognized ? 40 : 0, result.Diagnostics);

    private static async Task<ResourceFormatScore> FromPsbAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = await PsbHeaderReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
        if (!header.IsRecognized)
        {
            return new ResourceFormatScore(ResourceFormat.Psb, header.Stage, 0, header.Diagnostics);
        }

        stream.Position = 0;
        var structure = await PsbStructureProbe.ProbeAsync(stream, cancellationToken).ConfigureAwait(false);
        return new ResourceFormatScore(
            ResourceFormat.Psb,
            header.Stage,
            structure.IsPimgCandidate ? 50 : 40,
            [.. header.Diagnostics, .. structure.Diagnostics]);
    }

    private static ResourceFormatScore IdentifiedOnly(ResourceFormat format, string code, string message) =>
        new(format, EvidenceStage.ContainerIdentified, 10,
            [new KiriScopeDiagnostic(code, DiagnosticSeverity.Info, message)]);
}
