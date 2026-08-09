using System.Buffers.Binary;
using System.Text;
using KiriScope.Core.Diagnostics;
using KiriScope.IO.Hashing;

namespace KiriScope.Analysis;

/// <summary>
/// Performs bounded, read-only PE metadata, printable-string, and encryption-hint inspection.
/// It never loads a target as code and labels algorithm hints as heuristics rather than facts.
/// </summary>
public static class StaticBinaryAnalyzer
{
    private const int MaximumSections = 96;
    private const int MaximumImports = 1_024;
    private const int MaximumConstantOccurrences = 32;
    private const int MaximumRepeatedDiagnosticsPerCode = 32;

    private static readonly KnownConstant[] KnownConstants =
    [
        new(0xAAAAAAAA, "CX_CONSTANT_AAAAAAAA", "Observed 0xAAAAAAAA, a mask used by known CxEncryption programs.", 15),
        new(0x55555555, "CX_CONSTANT_55555555", "Observed 0x55555555, a mask used by known CxEncryption programs.", 15),
        new(0x41C64E6D, "CX_STANDARD_LCG_MULTIPLIER", "Observed 0x41C64E6D, a linear-congruential multiplier used by the standard Cx program generator.", 15),
        new(0x00003039, "CX_STANDARD_LCG_INCREMENT", "Observed 0x00003039, a linear-congruential increment used by the standard Cx program generator.", 10),
        new(0x000003FF, "CX_CONTROL_BLOCK_MASK", "Observed 0x000003FF, a possible 1024-word control-block mask.", 5),
    ];

    public static async Task<StaticBinaryAnalysisReport> AnalyzeFileAsync(
        string inputPath,
        StaticAnalysisOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        options ??= new StaticAnalysisOptions();
        ValidateOptions(options);

        var fullPath = Path.GetFullPath(inputPath);
        var fileInfo = new FileInfo(fullPath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("Static analysis input does not exist.", fullPath);
        }

        var hash = await Sha256Hasher.ComputeFileAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var identity = new AnalysisInputIdentity(fullPath, hash, fileInfo.Length);
        if (fileInfo.Length > options.MaximumFileBytes)
        {
            return new StaticBinaryAnalysisReport(
                identity,
                null,
                Array.Empty<BinaryStringFinding>(),
                Array.Empty<StaticAnalysisFinding>(),
                [new KiriScopeDiagnostic(
                    "ANALYSIS_INPUT_TOO_LARGE",
                    DiagnosticSeverity.Warning,
                    $"Static analysis declined to allocate {fileInfo.Length:N0} bytes; the configured limit is {options.MaximumFileBytes:N0} bytes.")]);
        }

        var data = new byte[checked((int)fileInfo.Length)];
        await using (var input = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await input.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
        }

        return Analyze(identity, data, options);
    }

    public static StaticBinaryAnalysisReport Analyze(
        AnalysisInputIdentity input,
        ReadOnlySpan<byte> data,
        StaticAnalysisOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        options ??= new StaticAnalysisOptions();
        ValidateOptions(options);
        if (data.Length != input.Length)
        {
            throw new ArgumentException("The supplied input identity length does not match the byte sequence.", nameof(data));
        }

        var diagnostics = new List<KiriScopeDiagnostic>();
        var findings = new List<StaticAnalysisFinding>();
        var pe = ParsePe(data, diagnostics, findings);
        var strings = ExtractStrings(data, options, diagnostics);
        AddConstantFacts(data, findings);
        AddFilterCandidates(strings, findings);

        return new StaticBinaryAnalysisReport(input, pe, strings, findings, diagnostics);
    }

    private static PeMetadata? ParsePe(
        ReadOnlySpan<byte> data,
        List<KiriScopeDiagnostic> diagnostics,
        List<StaticAnalysisFinding> findings)
    {
        if (data.Length < 2 || !data[..2].SequenceEqual("MZ"u8))
        {
            diagnostics.Add(new KiriScopeDiagnostic("PE_SIGNATURE_NOT_FOUND", DiagnosticSeverity.Info, "Input does not begin with an MZ DOS signature."));
            return null;
        }

        if (data.Length < 0x40)
        {
            diagnostics.Add(new KiriScopeDiagnostic("PE_DOS_HEADER_TRUNCATED", DiagnosticSeverity.Error, "MZ signature is present but the DOS header is truncated."));
            return null;
        }

        var peOffsetValue = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(0x3C, sizeof(uint)));
        if (peOffsetValue > int.MaxValue || peOffsetValue > data.Length - 24)
        {
            diagnostics.Add(new KiriScopeDiagnostic("PE_HEADER_OFFSET_INVALID", DiagnosticSeverity.Error, "DOS e_lfanew points outside the input.", 0x3C));
            return null;
        }

        var peOffset = (int)peOffsetValue;
        if (!data.Slice(peOffset, 4).SequenceEqual("PE\0\0"u8))
        {
            diagnostics.Add(new KiriScopeDiagnostic("PE_SIGNATURE_INVALID", DiagnosticSeverity.Error, "DOS e_lfanew does not point to a PE signature.", peOffset));
            return null;
        }

        var coffOffset = peOffset + 4;
        var machine = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(coffOffset, sizeof(ushort)));
        var declaredSectionCount = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(coffOffset + 2, sizeof(ushort)));
        var timestamp = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(coffOffset + 4, sizeof(uint)));
        var optionalHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(coffOffset + 16, sizeof(ushort)));
        var optionalHeaderOffset = coffOffset + 20;
        if (optionalHeaderSize < sizeof(ushort) || optionalHeaderOffset > data.Length - optionalHeaderSize)
        {
            diagnostics.Add(new KiriScopeDiagnostic("PE_OPTIONAL_HEADER_INVALID", DiagnosticSeverity.Error, "PE optional-header range is outside the input.", optionalHeaderOffset));
            return null;
        }

        try
        {
            var optionalMagic = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(optionalHeaderOffset, sizeof(ushort)));
            var isPe32Plus = optionalMagic switch
            {
                0x10B => false,
                0x20B => true,
                _ => throw new PeParseException("PE_OPTIONAL_HEADER_MAGIC_INVALID", "PE optional-header magic is neither PE32 nor PE32+.", optionalHeaderOffset),
            };
            var directoryOffset = optionalHeaderOffset + (isPe32Plus ? 112 : 96);
            if (optionalHeaderSize < (isPe32Plus ? 112 : 96) || optionalHeaderOffset > data.Length - 64)
            {
                throw new PeParseException("PE_OPTIONAL_HEADER_TRUNCATED", "PE optional header is too short for required fields.", optionalHeaderOffset);
            }

            var sizeOfHeaders = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(optionalHeaderOffset + 60, sizeof(uint)));
            var sectionOffset = optionalHeaderOffset + optionalHeaderSize;
            var sectionCount = Math.Min((int)declaredSectionCount, MaximumSections);
            if (declaredSectionCount > MaximumSections)
            {
                diagnostics.Add(new KiriScopeDiagnostic("PE_SECTION_COUNT_CAPPED", DiagnosticSeverity.Warning, $"PE declares {declaredSectionCount} sections; only the first {MaximumSections} will be reported.", coffOffset + 2));
            }

            if (sectionOffset > data.Length || sectionCount > (data.Length - sectionOffset) / 40)
            {
                throw new PeParseException("PE_SECTION_TABLE_TRUNCATED", "PE section table is outside the input.", sectionOffset);
            }

            var sections = new List<PeSectionInfo>(sectionCount);
            for (var index = 0; index < sectionCount; index++)
            {
                var offset = sectionOffset + (index * 40);
                var name = ReadFixedAscii(data.Slice(offset, 8));
                var virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 8, sizeof(uint)));
                var virtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 12, sizeof(uint)));
                var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 16, sizeof(uint)));
                var rawOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 20, sizeof(uint)));
                var characteristics = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 36, sizeof(uint)));
                double? entropy = null;
                if (rawSize != 0)
                {
                    if (rawOffset > data.Length || rawSize > data.Length - rawOffset)
                    {
                        diagnostics.Add(new KiriScopeDiagnostic("PE_SECTION_RAW_RANGE_INVALID", DiagnosticSeverity.Warning, $"Section '{name}' raw-data range points outside the input.", offset + 20));
                    }
                    else
                    {
                        entropy = CalculateEntropy(data.Slice((int)rawOffset, (int)rawSize));
                    }
                }

                sections.Add(new PeSectionInfo(name, virtualAddress, virtualSize, rawOffset, rawSize, characteristics, entropy));
            }

            var imports = ReadImports(data, sections, sizeOfHeaders, directoryOffset, optionalHeaderOffset + optionalHeaderSize, diagnostics);
            findings.Add(new StaticAnalysisFinding(
                AnalysisFindingKind.ObservedFact,
                "PE_HEADER_PARSED",
                $"Parsed a {(isPe32Plus ? "PE32+" : "PE32")} image for {MachineName(machine)} with {declaredSectionCount} declared section(s).",
                peOffset));
            foreach (var import in imports)
            {
                findings.Add(new StaticAnalysisFinding(AnalysisFindingKind.ObservedFact, "PE_IMPORT_MODULE", $"Observed PE import module '{import}'."));
            }

            return new PeMetadata(MachineName(machine), machine, isPe32Plus, timestamp, sections, imports);
        }
        catch (PeParseException exception)
        {
            diagnostics.Add(new KiriScopeDiagnostic(exception.Code, DiagnosticSeverity.Error, exception.Message, exception.Offset));
            return null;
        }
    }

    private static IReadOnlyList<string> ReadImports(
        ReadOnlySpan<byte> data,
        IReadOnlyList<PeSectionInfo> sections,
        uint sizeOfHeaders,
        int directoryOffset,
        int optionalHeaderEnd,
        List<KiriScopeDiagnostic> diagnostics)
    {
        if (directoryOffset > optionalHeaderEnd - 16)
        {
            return Array.Empty<string>();
        }

        var importRva = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(directoryOffset + 8, sizeof(uint)));
        var importSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(directoryOffset + 12, sizeof(uint)));
        if (importRva == 0 || importSize == 0)
        {
            return Array.Empty<string>();
        }

        if (!TryMapRva(importRva, sections, sizeOfHeaders, data.Length, out var importOffset))
        {
            diagnostics.Add(new KiriScopeDiagnostic("PE_IMPORT_DIRECTORY_OUT_OF_RANGE", DiagnosticSeverity.Warning, "PE import directory cannot be mapped to a file offset."));
            return Array.Empty<string>();
        }

        var descriptors = Math.Min((int)(importSize / 20), MaximumImports);
        var modules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < descriptors; index++)
        {
            var offset = importOffset + (index * 20);
            if (offset > data.Length - 20)
            {
                diagnostics.Add(new KiriScopeDiagnostic("PE_IMPORT_DESCRIPTOR_TRUNCATED", DiagnosticSeverity.Warning, "PE import descriptor list ends outside the input.", offset));
                break;
            }

            var nameRva = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 12, sizeof(uint)));
            var originalThunk = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, sizeof(uint)));
            var firstThunk = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 16, sizeof(uint)));
            if (nameRva == 0 && originalThunk == 0 && firstThunk == 0)
            {
                break;
            }

            if (!TryMapRva(nameRva, sections, sizeOfHeaders, data.Length, out var nameOffset))
            {
                AddRepeatedDiagnostic(
                    diagnostics,
                    "PE_IMPORT_NAME_OUT_OF_RANGE",
                    "PE import module name cannot be mapped to a file offset.",
                    offset + 12);
                continue;
            }

            var moduleName = ReadNullTerminatedAscii(data, nameOffset, 1_024);
            if (string.IsNullOrEmpty(moduleName))
            {
                AddRepeatedDiagnostic(
                    diagnostics,
                    "PE_IMPORT_NAME_INVALID",
                    "PE import module name is empty or unterminated.",
                    nameOffset);
                continue;
            }

            modules.Add(moduleName);
        }

        return modules.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool TryMapRva(uint rva, IReadOnlyList<PeSectionInfo> sections, uint sizeOfHeaders, int inputLength, out int fileOffset)
    {
        if (rva < sizeOfHeaders && rva < inputLength)
        {
            fileOffset = (int)rva;
            return true;
        }

        foreach (var section in sections)
        {
            var span = Math.Max(section.VirtualSize, section.RawDataSize);
            if (rva < section.VirtualAddress || (ulong)rva >= (ulong)section.VirtualAddress + span)
            {
                continue;
            }

            var relative = rva - section.VirtualAddress;
            if (relative >= section.RawDataSize || section.RawDataOffset > inputLength || relative > inputLength - section.RawDataOffset)
            {
                break;
            }

            fileOffset = checked((int)(section.RawDataOffset + relative));
            return true;
        }

        fileOffset = default;
        return false;
    }

    private static void AddRepeatedDiagnostic(
        List<KiriScopeDiagnostic> diagnostics,
        string code,
        string message,
        long offset)
    {
        var count = diagnostics.Count(diagnostic => string.Equals(diagnostic.Code, code, StringComparison.Ordinal));
        if (count < MaximumRepeatedDiagnosticsPerCode)
        {
            diagnostics.Add(new KiriScopeDiagnostic(code, DiagnosticSeverity.Warning, message, offset));
            return;
        }

        if (count == MaximumRepeatedDiagnosticsPerCode)
        {
            diagnostics.Add(new KiriScopeDiagnostic(
                code + "_CAPPED",
                DiagnosticSeverity.Warning,
                $"Additional '{code}' diagnostics were omitted after {MaximumRepeatedDiagnosticsPerCode:N0} occurrences."));
        }
    }

    private static IReadOnlyList<BinaryStringFinding> ExtractStrings(
        ReadOnlySpan<byte> data,
        StaticAnalysisOptions options,
        List<KiriScopeDiagnostic> diagnostics)
    {
        var strings = new List<BinaryStringFinding>();
        ExtractAsciiStrings(data, options, strings);
        if (strings.Count < options.MaximumStrings)
        {
            ExtractUtf16LeStrings(data, options, strings);
        }

        if (strings.Count == options.MaximumStrings)
        {
            diagnostics.Add(new KiriScopeDiagnostic("ANALYSIS_STRING_COUNT_CAPPED", DiagnosticSeverity.Warning, $"String reporting stopped at the configured {options.MaximumStrings:N0} findings."));
        }

        return strings.OrderBy(static value => value.Offset).ThenBy(static value => value.Encoding, StringComparer.Ordinal).ToArray();
    }

    private static void ExtractAsciiStrings(ReadOnlySpan<byte> data, StaticAnalysisOptions options, List<BinaryStringFinding> strings)
    {
        for (var index = 0; index < data.Length && strings.Count < options.MaximumStrings;)
        {
            if (!IsPrintableAscii(data[index]))
            {
                index++;
                continue;
            }

            var start = index;
            while (index < data.Length && IsPrintableAscii(data[index]))
            {
                index++;
            }

            var length = index - start;
            if (length >= options.MinimumStringLength)
            {
                strings.Add(new BinaryStringFinding(start, "ascii", length, DisplayString(Encoding.ASCII.GetString(data.Slice(start, length)), options.MaximumDisplayedStringLength)));
            }
        }
    }

    private static void ExtractUtf16LeStrings(ReadOnlySpan<byte> data, StaticAnalysisOptions options, List<BinaryStringFinding> strings)
    {
        for (var index = 0; index <= data.Length - 2 && strings.Count < options.MaximumStrings;)
        {
            if (!IsPrintableAscii(data[index]) || data[index + 1] != 0)
            {
                index++;
                continue;
            }

            var start = index;
            while (index <= data.Length - 2 && IsPrintableAscii(data[index]) && data[index + 1] == 0)
            {
                index += 2;
            }

            var byteLength = index - start;
            var characterLength = byteLength / 2;
            if (characterLength >= options.MinimumStringLength)
            {
                strings.Add(new BinaryStringFinding(start, "utf-16le", characterLength, DisplayString(Encoding.Unicode.GetString(data.Slice(start, byteLength)), options.MaximumDisplayedStringLength)));
            }
        }
    }

    private static void AddConstantFacts(ReadOnlySpan<byte> data, List<StaticAnalysisFinding> findings)
    {
        Span<byte> pattern = stackalloc byte[sizeof(uint)];
        foreach (var constant in KnownConstants)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(pattern, constant.Value);
            var remaining = data;
            var baseOffset = 0;
            var matches = 0;
            while (matches < MaximumConstantOccurrences)
            {
                var relative = remaining.IndexOf(pattern);
                if (relative < 0)
                {
                    break;
                }

                var offset = baseOffset + relative;
                findings.Add(new StaticAnalysisFinding(AnalysisFindingKind.ObservedFact, constant.Id, constant.Summary, offset));
                matches++;
                var next = relative + 1;
                remaining = remaining[next..];
                baseOffset += next;
            }
        }
    }

    private static void AddFilterCandidates(IReadOnlyList<BinaryStringFinding> strings, List<StaticAnalysisFinding> findings)
    {
        var score = 0;
        var evidence = new List<string>();
        if (strings.Any(static value => value.Value.Contains("Encryption control block", StringComparison.OrdinalIgnoreCase)))
        {
            score += 70;
            evidence.Add("control-block signature string");
        }

        if (strings.Any(static value => value.Value.Contains("CxEncryption", StringComparison.OrdinalIgnoreCase)))
        {
            score += 60;
            evidence.Add("CxEncryption string");
        }

        if (strings.Any(static value => value.Value.Contains(".tpm", StringComparison.OrdinalIgnoreCase)))
        {
            score += 15;
            evidence.Add("TPM plugin reference");
        }

        var cxConstantCount = findings.Count(static finding => finding.Id.StartsWith("CX_", StringComparison.Ordinal));
        if (cxConstantCount > 0)
        {
            score += Math.Min(cxConstantCount * 10, 30);
            evidence.Add($"{cxConstantCount} Cx-related constant occurrence(s)");
        }

        if (score == 0)
        {
            return;
        }

        score = Math.Min(score, 100);
        var confidence = score >= 80 ? "strong" : score >= 45 ? "medium" : "weak";
        findings.Add(new StaticAnalysisFinding(
            AnalysisFindingKind.HeuristicCandidate,
            "HEURISTIC_CX_ENCRYPTION",
            $"{confidence} CxEncryption candidate ({string.Join(", ", evidence)}). This is a heuristic only and does not establish game compatibility or usable parameters.",
            Score: score));
    }

    private static string ReadFixedAscii(ReadOnlySpan<byte> data)
    {
        var length = data.IndexOf((byte)0);
        return Encoding.ASCII.GetString(length < 0 ? data : data[..length]);
    }

    private static string ReadNullTerminatedAscii(ReadOnlySpan<byte> data, int offset, int maximumLength)
    {
        if (offset < 0 || offset >= data.Length)
        {
            return string.Empty;
        }

        var length = Math.Min(maximumLength, data.Length - offset);
        var bytes = data.Slice(offset, length);
        var terminator = bytes.IndexOf((byte)0);
        return terminator <= 0 ? string.Empty : Encoding.ASCII.GetString(bytes[..terminator]);
    }

    private static double CalculateEntropy(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return 0;
        }

        Span<int> counts = stackalloc int[256];
        foreach (var value in data)
        {
            counts[value]++;
        }

        var entropy = 0d;
        foreach (var count in counts)
        {
            if (count == 0)
            {
                continue;
            }

            var probability = (double)count / data.Length;
            entropy -= probability * Math.Log2(probability);
        }

        return entropy;
    }

    private static bool IsPrintableAscii(byte value) => value is >= 0x20 and <= 0x7E;

    private static string DisplayString(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength] + "…";

    private static string MachineName(ushort machine) => machine switch
    {
        0x014C => "x86",
        0x8664 => "x64",
        0x01C0 => "ARM",
        0xAA64 => "ARM64",
        _ => $"unknown-0x{machine:X4}",
    };

    private static void ValidateOptions(StaticAnalysisOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumStrings);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MinimumStringLength, 2);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumDisplayedStringLength);
    }

    private sealed record KnownConstant(uint Value, string Id, string Summary, int Weight);

    private sealed class PeParseException(string code, string message, long offset) : Exception(message)
    {
        public string Code { get; } = code;

        public long Offset { get; } = offset;
    }
}
