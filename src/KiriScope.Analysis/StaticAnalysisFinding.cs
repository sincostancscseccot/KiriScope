namespace KiriScope.Analysis;

/// <summary>A traceable fact or explicitly non-conclusive candidate found during static analysis.</summary>
public sealed record StaticAnalysisFinding(
    AnalysisFindingKind Kind,
    string Id,
    string Summary,
    long? FileOffset = null,
    int? Score = null);
