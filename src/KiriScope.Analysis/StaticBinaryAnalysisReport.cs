using KiriScope.Core.Diagnostics;

namespace KiriScope.Analysis;

/// <summary>Hash-identified input used by a read-only static analysis operation.</summary>
public sealed record AnalysisInputIdentity(string FullPath, string Sha256, long Length);

/// <summary>One PE section parsed directly from an input binary.</summary>
public sealed record PeSectionInfo(
    string Name,
    uint VirtualAddress,
    uint VirtualSize,
    uint RawDataOffset,
    uint RawDataSize,
    uint Characteristics,
    double? Entropy);

/// <summary>Directly parsed PE metadata. A null value means the input was not a valid PE image.</summary>
public sealed record PeMetadata(
    string Machine,
    ushort MachineValue,
    bool IsPe32Plus,
    uint Timestamp,
    IReadOnlyList<PeSectionInfo> Sections,
    IReadOnlyList<string> ImportedModules);

/// <summary>One printable byte sequence actually found in the input.</summary>
public sealed record BinaryStringFinding(long Offset, string Encoding, int Length, string Value);

/// <summary>Complete read-only static report. Findings distinguish observed facts from heuristic candidates.</summary>
public sealed record StaticBinaryAnalysisReport(
    AnalysisInputIdentity Input,
    PeMetadata? Pe,
    IReadOnlyList<BinaryStringFinding> Strings,
    IReadOnlyList<StaticAnalysisFinding> Findings,
    IReadOnlyList<KiriScopeDiagnostic> Diagnostics);
