using KiriScope.Analysis;

namespace KiriScope.Knowledge;

/// <summary>Matches only directly observed static facts to declared knowledge-base fingerprints.</summary>
public static class KnowledgeFingerprintMatcher
{
    public static IReadOnlyList<KnowledgeSchemeCandidate> Match(KnowledgeBase knowledgeBase, StaticBinaryAnalysisReport report)
    {
        ArgumentNullException.ThrowIfNull(knowledgeBase);
        ArgumentNullException.ThrowIfNull(report);
        var candidates = new List<KnowledgeSchemeCandidate>();
        foreach (var scheme in knowledgeBase.Schemes)
        {
            if (scheme.Fingerprint is null)
            {
                continue;
            }

            var matched = new List<string>();
            if (!MatchFingerprint(scheme.Fingerprint, report, matched, out var score))
            {
                continue;
            }

            candidates.Add(new KnowledgeSchemeCandidate(scheme.Id, scheme.Revision, scheme.Fingerprint.Id, score, matched));
        }

        return candidates
            .OrderByDescending(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.SchemeId, StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.SchemeRevision, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool MatchFingerprint(AlgorithmFingerprint fingerprint, StaticBinaryAnalysisReport report, List<string> matched, out int score)
    {
        score = 0;
        if (!string.IsNullOrWhiteSpace(fingerprint.RequiredSha256))
        {
            if (!string.Equals(fingerprint.RequiredSha256, report.Input.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            matched.Add("input SHA-256");
            score += 35;
        }

        if (!string.IsNullOrWhiteSpace(fingerprint.RequiredMachine))
        {
            if (!string.Equals(fingerprint.RequiredMachine, report.Pe?.Machine, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            matched.Add($"PE machine {report.Pe!.Machine}");
            score += 15;
        }

        foreach (var required in fingerprint.RequiredStrings ?? Array.Empty<string>())
        {
            if (!report.Strings.Any(finding => finding.Value.Contains(required, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            matched.Add($"string '{required}'");
            score += 15;
        }

        foreach (var required in fingerprint.RequiredImportedModules ?? Array.Empty<string>())
        {
            if (report.Pe is null || !report.Pe.ImportedModules.Contains(required, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            matched.Add($"import '{required}'");
            score += 15;
        }

        foreach (var required in fingerprint.RequiredFindingIds ?? Array.Empty<string>())
        {
            if (!report.Findings.Any(finding => finding.Kind == AnalysisFindingKind.ObservedFact && string.Equals(finding.Id, required, StringComparison.Ordinal)))
            {
                return false;
            }

            matched.Add($"observed finding '{required}'");
            score += 15;
        }

        score = Math.Min(score, 100);
        return matched.Count != 0;
    }
}
