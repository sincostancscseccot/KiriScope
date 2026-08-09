using KiriScope.Core.Diagnostics;
using KiriScope.Core.Evidence;
using KiriScope.Plugins.Abstractions.Filters;

namespace KiriScope.Resources;

/// <summary>One concrete filter scheme considered by the candidate pipeline.</summary>
public sealed record ContentFilterCandidate(ContentFilterSchemeDescriptor Scheme, IContentFilter Filter);

/// <summary>Evidence and diff metadata for one applied candidate.</summary>
public sealed record ContentFilterCandidateResult(
    ContentFilterSchemeDescriptor Scheme,
    ContentFilterDescriptor Filter,
    bool IsAccepted,
    ResourceFormatScore FormatScore,
    ContentByteDifference Difference,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics);

/// <summary>Candidate evaluation report suitable for CLI JSON output or GUI presentation.</summary>
public sealed record ContentFilterCandidateReport(
    string EntryName,
    uint? Adler32,
    int CiphertextBytes,
    IReadOnlyList<ContentFilterCandidateResult> Candidates);

/// <summary>
/// Applies independently supplied schemes to one decoded-entry byte sequence, compares the output,
/// and ranks only format-validated results as accepted.
/// </summary>
public static class ContentFilterCandidatePipeline
{
    public const int DefaultMaximumInputBytes = 128 * 1024 * 1024;

    public static async Task<ContentFilterCandidateReport> EvaluateAsync(
        ReadOnlyMemory<byte> ciphertext,
        ContentFilterContext context,
        IEnumerable<ContentFilterCandidate> candidates,
        int maximumInputBytes = DefaultMaximumInputBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(candidates);
        if (maximumInputBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumInputBytes));
        }

        if (ciphertext.Length > maximumInputBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ciphertext),
                $"Candidate scoring is limited to {maximumInputBytes:N0} bytes; split or narrow the input before testing schemes.");
        }

        var results = new List<ContentFilterCandidateResult>();
        foreach (var candidate in candidates)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            ArgumentNullException.ThrowIfNull(candidate.Scheme);
            ArgumentNullException.ThrowIfNull(candidate.Filter);
            cancellationToken.ThrowIfCancellationRequested();

            var plaintext = ciphertext.ToArray();
            try
            {
                await candidate.Filter.TransformAsync(context, plaintext, cancellationToken).ConfigureAwait(false);
                var difference = ContentByteDifference.Analyze(ciphertext.Span, plaintext);
                var formatScore = await ResourceFormatScorer.ScoreAsync(plaintext, cancellationToken).ConfigureAwait(false);
                var diagnostics = formatScore.IsAccepted
                    ? formatScore.Diagnostics
                    : [
                        .. formatScore.Diagnostics,
                        new KiriScopeDiagnostic(
                            "FILTER_CANDIDATE_REJECTED",
                            DiagnosticSeverity.Warning,
                            "The candidate did not reach full structural format validation and was not accepted as a successful decryption."),
                    ];
                results.Add(new ContentFilterCandidateResult(
                    candidate.Scheme,
                    candidate.Filter.Descriptor,
                    formatScore.IsAccepted,
                    formatScore,
                    difference,
                    diagnostics));
            }
            catch (ContentFilterException exception)
            {
                results.Add(Rejected(candidate, ciphertext.Span, plaintext, exception.Code, exception.Message));
            }
            catch (InvalidDataException exception)
            {
                results.Add(Rejected(candidate, ciphertext.Span, plaintext, "FILTER_CANDIDATE_VALIDATION_FAILED", exception.Message));
            }
            catch (ArgumentException exception)
            {
                results.Add(Rejected(candidate, ciphertext.Span, plaintext, "FILTER_CANDIDATE_PARAMETERS_INVALID", exception.Message));
            }
        }

        return new ContentFilterCandidateReport(
            context.EntryName,
            context.Adler32,
            ciphertext.Length,
            results
                .OrderByDescending(static result => result.IsAccepted)
                .ThenByDescending(static result => result.FormatScore.Score)
                .ThenBy(static result => result.Scheme.Id, StringComparer.Ordinal)
                .ToArray());
    }

    private static ContentFilterCandidateResult Rejected(
        ContentFilterCandidate candidate,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> attemptedPlaintext,
        string code,
        string message)
    {
        var diagnostic = new KiriScopeDiagnostic(code, DiagnosticSeverity.Error, message);
        return new ContentFilterCandidateResult(
            candidate.Scheme,
            candidate.Filter.Descriptor,
            false,
            new ResourceFormatScore(ResourceFormat.Unknown, EvidenceStage.Unidentified, 0, [diagnostic]),
            ContentByteDifference.Analyze(ciphertext, attemptedPlaintext),
            [
                diagnostic,
                new KiriScopeDiagnostic(
                    "FILTER_CANDIDATE_REJECTED",
                    DiagnosticSeverity.Warning,
                    "The candidate failed before full structural format validation and was not accepted."),
            ]);
    }
}
