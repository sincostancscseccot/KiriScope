using KiriScope.Analysis;
using KiriScope.Core.Diagnostics;
using KiriScope.Plugins.Abstractions.Filters;

namespace KiriScope.Knowledge;

/// <summary>Lifecycle state for an explicitly versioned compatibility entry.</summary>
public enum KnowledgeCompatibilityStatus
{
    ReferenceOnly,
    Candidate,
    Verified,
    Incompatible,
    Retired,
}

/// <summary>Source-bound evidence required before a compatibility entry can claim verification.</summary>
public sealed record KnowledgeVerificationEvidence(
    string SampleVersion,
    string SampleSha256,
    string VerifiedStage,
    string ReproductionCommand,
    string? Notes = null);

/// <summary>Applicability scope for a scheme; the target ID is an evidence label, not a filename matcher.</summary>
public sealed record KnowledgeApplicability(string TargetId, string TargetVersion, string? Notes = null);

/// <summary>Portable fingerprint signals used only to propose a scheme candidate during read-only scanning.</summary>
public sealed record AlgorithmFingerprint(
    string Id,
    string? RequiredSha256 = null,
    string? RequiredMachine = null,
    IReadOnlyList<string>? RequiredStrings = null,
    IReadOnlyList<string>? RequiredImportedModules = null,
    IReadOnlyList<string>? RequiredFindingIds = null);

/// <summary>One portable scheme file and its applicability metadata inside a knowledge base manifest.</summary>
public sealed record KnowledgeSchemeDocument(
    string Id,
    string Revision,
    string DisplayName,
    string SchemeFile,
    string SchemeSha256,
    string AlgorithmId,
    string AlgorithmVersion,
    KnowledgeCompatibilityStatus Status,
    IReadOnlyList<KnowledgeApplicability>? Applicability = null,
    AlgorithmFingerprint? Fingerprint = null,
    IReadOnlyList<KnowledgeVerificationEvidence>? Evidence = null,
    IReadOnlyList<string>? Supersedes = null);

/// <summary>Versioned compatibility statement attached to a scheme and target version.</summary>
public sealed record KnowledgeCompatibilityEntry(
    string TargetId,
    string TargetVersion,
    string SchemeId,
    string SchemeRevision,
    KnowledgeCompatibilityStatus Status,
    IReadOnlyList<KnowledgeVerificationEvidence>? Evidence = null,
    string? Limitations = null);

/// <summary>JSON document stored at knowledge-base.json.</summary>
public sealed record KnowledgeBaseDocument(
    string SchemaVersion,
    string Id,
    string DisplayName,
    IReadOnlyList<KnowledgeSchemeDocument>? Schemes = null,
    IReadOnlyList<KnowledgeCompatibilityEntry>? Compatibility = null);

/// <summary>Loaded scheme without exposing its parameter values in the knowledge-base report.</summary>
public sealed record LoadedKnowledgeScheme(
    string Id,
    string Revision,
    string DisplayName,
    string SchemePath,
    string SchemeSha256,
    ContentFilterSchemeDescriptor Descriptor,
    KnowledgeCompatibilityStatus Status,
    IReadOnlyList<KnowledgeApplicability> Applicability,
    AlgorithmFingerprint? Fingerprint,
    IReadOnlyList<KnowledgeVerificationEvidence> Evidence,
    IReadOnlyList<string> Supersedes);

/// <summary>Validated, directory-rooted knowledge base that can be used without modifying core code.</summary>
public sealed record KnowledgeBase(
    string SchemaVersion,
    string Id,
    string DisplayName,
    string RootDirectory,
    string ManifestPath,
    string ManifestSha256,
    IReadOnlyList<LoadedKnowledgeScheme> Schemes,
    IReadOnlyList<KnowledgeCompatibilityEntry> Compatibility);

/// <summary>Heuristic scheme proposal from a fingerprint match; never a compatibility conclusion.</summary>
public sealed record KnowledgeSchemeCandidate(
    string SchemeId,
    string SchemeRevision,
    string FingerprintId,
    int Score,
    IReadOnlyList<string> MatchedEvidence,
    AnalysisFindingKind Kind = AnalysisFindingKind.HeuristicCandidate);

/// <summary>Compact, hash-identified item from a knowledge-base batch scan.</summary>
public sealed record KnowledgeScanItem(
    string RelativePath,
    string FullPath,
    string Sha256,
    long Length,
    string Kind,
    string EvidenceStage,
    IReadOnlyList<KnowledgeSchemeCandidate> Candidates,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics);

/// <summary>Bounded, read-only batch scan report.</summary>
public sealed record KnowledgeBatchScanReport(
    string SchemaVersion,
    DateTimeOffset CreatedAtUtc,
    string InputDirectory,
    KnowledgeBaseIdentity KnowledgeBase,
    IReadOnlyList<KnowledgeScanItem> Items,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics,
    string? ReproductionCommand = null)
{
    public const string CurrentSchemaVersion = "1.0";
}

/// <summary>Hash identity for the exact knowledge-base revision used by a scan.</summary>
public sealed record KnowledgeBaseIdentity(string Id, string SchemaVersion, string ManifestSha256);

/// <summary>One path-level difference between two read-only batch scan reports.</summary>
public sealed record KnowledgeScanDifference(
    string RelativePath,
    string ChangeKind,
    string? LeftSha256,
    string? RightSha256,
    IReadOnlyList<string> LeftCandidateIds,
    IReadOnlyList<string> RightCandidateIds);

/// <summary>Offline comparison of two known scan-report archives.</summary>
public sealed record KnowledgeBatchComparisonReport(
    string SchemaVersion,
    DateTimeOffset ComparedAtUtc,
    string LeftReportPath,
    string RightReportPath,
    KnowledgeBaseIdentity LeftKnowledgeBase,
    KnowledgeBaseIdentity RightKnowledgeBase,
    IReadOnlyList<KnowledgeScanDifference> Differences,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics,
    string? ReproductionCommand = null)
{
    public const string CurrentSchemaVersion = "1.0";
}

/// <summary>Stable parse/validation failure for knowledge-base documents.</summary>
public sealed class KnowledgeBaseException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
