using System.Text.Json;
using System.Text.Json.Serialization;
using KiriScope.Analysis;
using KiriScope.Filters.BuiltIn;
using KiriScope.IO.Hashing;
using KiriScope.Plugins.Abstractions.Filters;

namespace KiriScope.Knowledge;

/// <summary>Loads only explicit, hash-bound knowledge-base revisions from a chosen directory.</summary>
public static class KnowledgeBaseLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public const string ManifestFileName = "knowledge-base.json";
    public const string CurrentSchemaVersion = "1.0";

    public static async Task<KnowledgeBase> LoadAsync(string rootDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var root = Path.GetFullPath(rootDirectory);
        if (!Directory.Exists(root))
        {
            throw Failure("KNOWLEDGE_ROOT_NOT_FOUND", $"Knowledge-base directory does not exist: {root}");
        }

        var manifestPath = Path.Combine(root, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw Failure("KNOWLEDGE_MANIFEST_NOT_FOUND", $"Knowledge-base manifest was not found: {manifestPath}");
        }

        KnowledgeBaseDocument? document;
        try
        {
            await using var manifest = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            document = await JsonSerializer.DeserializeAsync<KnowledgeBaseDocument>(manifest, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw Failure("KNOWLEDGE_MANIFEST_JSON_INVALID", exception.Message);
        }

        if (document is null)
        {
            throw Failure("KNOWLEDGE_MANIFEST_EMPTY", "Knowledge-base manifest was empty.");
        }

        ValidateDocument(document);
        var loadedSchemes = new List<LoadedKnowledgeScheme>();
        var schemeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scheme in document.Schemes ?? Array.Empty<KnowledgeSchemeDocument>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (scheme is null)
            {
                throw Failure("KNOWLEDGE_SCHEME_PROPERTY_INVALID", "Knowledge scheme entries must not be null.");
            }

            ValidateSchemeDocument(scheme);
            var revisionKey = scheme.Id + "@" + scheme.Revision;
            if (!schemeIds.Add(revisionKey))
            {
                throw Failure("KNOWLEDGE_SCHEME_REVISION_DUPLICATE", $"Knowledge base contains duplicate scheme revision '{revisionKey}'.");
            }

            var schemePath = ResolveContainedPath(root, scheme.SchemeFile, "KNOWLEDGE_SCHEME_PATH_INVALID");
            if (!File.Exists(schemePath))
            {
                throw Failure("KNOWLEDGE_SCHEME_FILE_NOT_FOUND", $"Knowledge scheme file does not exist: {scheme.SchemeFile}");
            }

            var actualHash = await Sha256Hasher.ComputeFileAsync(schemePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualHash, scheme.SchemeSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw Failure("KNOWLEDGE_SCHEME_HASH_MISMATCH", $"Knowledge scheme '{revisionKey}' does not match its declared SHA-256.");
            }

            BuiltInContentFilterScheme loaded;
            try
            {
                loaded = BuiltInContentFilterSchemeLoader.Load(schemePath);
            }
            catch (ContentFilterException exception)
            {
                throw Failure(exception.Code, $"Knowledge scheme '{revisionKey}' is not loadable: {exception.Message}");
            }

            if (!string.Equals(loaded.Descriptor.Id, scheme.Id, StringComparison.Ordinal) ||
                !string.Equals(loaded.Descriptor.AlgorithmId, scheme.AlgorithmId, StringComparison.Ordinal) ||
                !string.Equals(loaded.Descriptor.AlgorithmVersion, scheme.AlgorithmVersion, StringComparison.Ordinal))
            {
                throw Failure("KNOWLEDGE_SCHEME_DESCRIPTOR_MISMATCH", $"Knowledge entry '{revisionKey}' does not match the ID or algorithm declared by its scheme JSON.");
            }

            loadedSchemes.Add(new LoadedKnowledgeScheme(
                scheme.Id,
                scheme.Revision,
                scheme.DisplayName,
                schemePath,
                actualHash,
                loaded.Descriptor,
                scheme.Status,
                scheme.Applicability ?? Array.Empty<KnowledgeApplicability>(),
                scheme.Fingerprint,
                scheme.Evidence ?? Array.Empty<KnowledgeVerificationEvidence>(),
                scheme.Supersedes ?? Array.Empty<string>()));
        }

        ValidateCompatibility(document.Compatibility ?? Array.Empty<KnowledgeCompatibilityEntry>(), loadedSchemes);
        return new KnowledgeBase(
            document.SchemaVersion,
            document.Id,
            document.DisplayName,
            root,
            manifestPath,
            await Sha256Hasher.ComputeFileAsync(manifestPath, cancellationToken).ConfigureAwait(false),
            loadedSchemes.OrderBy(static scheme => scheme.Id, StringComparer.Ordinal).ThenBy(static scheme => scheme.Revision, StringComparer.Ordinal).ToArray(),
            (document.Compatibility ?? Array.Empty<KnowledgeCompatibilityEntry>())
                .OrderBy(static entry => entry.TargetId, StringComparer.Ordinal)
                .ThenBy(static entry => entry.TargetVersion, StringComparer.Ordinal)
                .ThenBy(static entry => entry.SchemeId, StringComparer.Ordinal)
                .ThenBy(static entry => entry.SchemeRevision, StringComparer.Ordinal)
                .ToArray());
    }

    private static void ValidateDocument(KnowledgeBaseDocument document)
    {
        if (!string.Equals(document.SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw Failure("KNOWLEDGE_SCHEMA_VERSION_UNSUPPORTED", $"Knowledge-base schema '{document.SchemaVersion}' is not supported.");
        }

        Require(document.Id, "KNOWLEDGE_MANIFEST_PROPERTY_INVALID", "Knowledge-base id is required.");
        Require(document.DisplayName, "KNOWLEDGE_MANIFEST_PROPERTY_INVALID", "Knowledge-base displayName is required.");
    }

    private static void ValidateSchemeDocument(KnowledgeSchemeDocument scheme)
    {
        Require(scheme.Id, "KNOWLEDGE_SCHEME_PROPERTY_INVALID", "Knowledge scheme id is required.");
        RequireSemanticVersion(scheme.Revision, "KNOWLEDGE_SCHEME_REVISION_INVALID");
        Require(scheme.DisplayName, "KNOWLEDGE_SCHEME_PROPERTY_INVALID", "Knowledge scheme displayName is required.");
        Require(scheme.SchemeFile, "KNOWLEDGE_SCHEME_PROPERTY_INVALID", "Knowledge scheme schemeFile is required.");
        RequireSha256(scheme.SchemeSha256, "KNOWLEDGE_SCHEME_HASH_INVALID");
        Require(scheme.AlgorithmId, "KNOWLEDGE_SCHEME_PROPERTY_INVALID", "Knowledge scheme algorithmId is required.");
        Require(scheme.AlgorithmVersion, "KNOWLEDGE_SCHEME_PROPERTY_INVALID", "Knowledge scheme algorithmVersion is required.");
        ValidateApplicability(scheme.Applicability ?? Array.Empty<KnowledgeApplicability>());
        ValidateSupersedes(scheme.Supersedes ?? Array.Empty<string>());
        ValidateEvidence(scheme.Status, scheme.Evidence ?? Array.Empty<KnowledgeVerificationEvidence>(), "KNOWLEDGE_SCHEME_VERIFICATION_EVIDENCE_REQUIRED");
        if (scheme.Fingerprint is not null)
        {
            Require(scheme.Fingerprint.Id, "KNOWLEDGE_FINGERPRINT_INVALID", "Fingerprint id is required.");
            if (scheme.Fingerprint.RequiredSha256 is not null)
            {
                RequireSha256(scheme.Fingerprint.RequiredSha256, "KNOWLEDGE_FINGERPRINT_HASH_INVALID");
            }

            if (scheme.Fingerprint.RequiredMachine is not null)
            {
                Require(scheme.Fingerprint.RequiredMachine, "KNOWLEDGE_FINGERPRINT_INVALID", "Fingerprint machine must not be empty when specified.");
            }

            ValidateNonBlankList(scheme.Fingerprint.RequiredStrings, "KNOWLEDGE_FINGERPRINT_INVALID", "Fingerprint string conditions must not be empty.");
            ValidateNonBlankList(scheme.Fingerprint.RequiredImportedModules, "KNOWLEDGE_FINGERPRINT_INVALID", "Fingerprint import conditions must not be empty.");
            ValidateNonBlankList(scheme.Fingerprint.RequiredFindingIds, "KNOWLEDGE_FINGERPRINT_INVALID", "Fingerprint finding conditions must not be empty.");
            if (!HasFingerprintCondition(scheme.Fingerprint))
            {
                throw Failure("KNOWLEDGE_FINGERPRINT_EMPTY", $"Fingerprint '{scheme.Fingerprint.Id}' has no direct observable condition.");
            }
        }
    }

    private static void ValidateApplicability(IReadOnlyList<KnowledgeApplicability> entries)
    {
        foreach (var entry in entries)
        {
            if (entry is null)
            {
                throw Failure("KNOWLEDGE_APPLICABILITY_INVALID", "Knowledge applicability entries must not be null.");
            }

            Require(entry.TargetId, "KNOWLEDGE_APPLICABILITY_INVALID", "Knowledge applicability targetId is required.");
            Require(entry.TargetVersion, "KNOWLEDGE_APPLICABILITY_INVALID", "Knowledge applicability targetVersion is required.");
        }
    }

    private static void ValidateSupersedes(IReadOnlyList<string> entries)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            Require(entry, "KNOWLEDGE_SUPERSEDES_INVALID", "Supersedes entries must use 'scheme-id@major.minor.patch' form.");
            var separator = entry.LastIndexOf('@');
            if (separator <= 0 || separator == entry.Length - 1)
            {
                throw Failure("KNOWLEDGE_SUPERSEDES_INVALID", "Supersedes entries must use 'scheme-id@major.minor.patch' form.");
            }

            RequireSemanticVersion(entry[(separator + 1)..], "KNOWLEDGE_SUPERSEDES_INVALID");
            if (!values.Add(entry))
            {
                throw Failure("KNOWLEDGE_SUPERSEDES_DUPLICATE", $"Duplicate supersedes entry '{entry}'.");
            }
        }
    }

    private static void ValidateCompatibility(IReadOnlyList<KnowledgeCompatibilityEntry> entries, IReadOnlyList<LoadedKnowledgeScheme> schemes)
    {
        var schemeKeys = schemes.Select(static scheme => scheme.Id + "@" + scheme.Revision).ToHashSet(StringComparer.Ordinal);
        var entryKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry is null)
            {
                throw Failure("KNOWLEDGE_COMPATIBILITY_PROPERTY_INVALID", "Compatibility entries must not be null.");
            }

            Require(entry.TargetId, "KNOWLEDGE_COMPATIBILITY_PROPERTY_INVALID", "Compatibility targetId is required.");
            Require(entry.TargetVersion, "KNOWLEDGE_COMPATIBILITY_PROPERTY_INVALID", "Compatibility targetVersion is required.");
            Require(entry.SchemeId, "KNOWLEDGE_COMPATIBILITY_PROPERTY_INVALID", "Compatibility schemeId is required.");
            RequireSemanticVersion(entry.SchemeRevision, "KNOWLEDGE_COMPATIBILITY_REVISION_INVALID");
            if (!schemeKeys.Contains(entry.SchemeId + "@" + entry.SchemeRevision))
            {
                throw Failure("KNOWLEDGE_COMPATIBILITY_SCHEME_UNKNOWN", $"Compatibility entry references unknown scheme revision '{entry.SchemeId}@{entry.SchemeRevision}'.");
            }

            var key = string.Join("|", entry.TargetId, entry.TargetVersion, entry.SchemeId, entry.SchemeRevision);
            if (!entryKeys.Add(key))
            {
                throw Failure("KNOWLEDGE_COMPATIBILITY_DUPLICATE", $"Compatibility matrix contains duplicate entry '{key}'.");
            }

            ValidateEvidence(entry.Status, entry.Evidence ?? Array.Empty<KnowledgeVerificationEvidence>(), "KNOWLEDGE_COMPATIBILITY_VERIFICATION_EVIDENCE_REQUIRED");
        }
    }

    private static void ValidateEvidence(KnowledgeCompatibilityStatus status, IReadOnlyList<KnowledgeVerificationEvidence> evidence, string missingCode)
    {
        if (status == KnowledgeCompatibilityStatus.Verified && evidence.Count == 0)
        {
            throw Failure(missingCode, "Verified compatibility requires at least one source-bound format-validation evidence record.");
        }

        foreach (var item in evidence)
        {
            if (item is null)
            {
                throw Failure("KNOWLEDGE_EVIDENCE_PROPERTY_INVALID", "Evidence entries must not be null.");
            }

            Require(item.SampleVersion, "KNOWLEDGE_EVIDENCE_PROPERTY_INVALID", "Evidence sampleVersion is required.");
            RequireSha256(item.SampleSha256, "KNOWLEDGE_EVIDENCE_HASH_INVALID");
            Require(item.VerifiedStage, "KNOWLEDGE_EVIDENCE_PROPERTY_INVALID", "Evidence verifiedStage is required.");
            Require(item.ReproductionCommand, "KNOWLEDGE_EVIDENCE_PROPERTY_INVALID", "Evidence reproductionCommand is required.");
            if (status == KnowledgeCompatibilityStatus.Verified && item.VerifiedStage is not ("FormatValidated" or "ContentUsable"))
            {
                throw Failure("KNOWLEDGE_EVIDENCE_STAGE_INSUFFICIENT", "Verified compatibility evidence must reach FormatValidated or ContentUsable.");
            }
        }
    }

    private static void ValidateNonBlankList(IReadOnlyList<string>? values, string code, string message)
    {
        if (values is null)
        {
            return;
        }

        foreach (var value in values)
        {
            Require(value, code, message);
        }
    }

    private static bool HasFingerprintCondition(AlgorithmFingerprint fingerprint) =>
        !string.IsNullOrWhiteSpace(fingerprint.RequiredSha256) ||
        !string.IsNullOrWhiteSpace(fingerprint.RequiredMachine) ||
        (fingerprint.RequiredStrings?.Count ?? 0) > 0 ||
        (fingerprint.RequiredImportedModules?.Count ?? 0) > 0 ||
        (fingerprint.RequiredFindingIds?.Count ?? 0) > 0;

    private static string ResolveContainedPath(string root, string relativePath, string code)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw Failure(code, "Knowledge-base paths must be relative.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative.Equals("..", StringComparison.Ordinal) || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw Failure(code, "Knowledge-base path escapes the selected root directory.");
        }

        return fullPath;
    }

    private static void Require(string? value, string code, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Failure(code, message);
        }
    }

    private static void RequireSemanticVersion(string? value, string code)
    {
        if (!Version.TryParse(value, out var version) || version.Major < 0 || version.Minor < 0 || version.Build < 0)
        {
            throw Failure(code, "Scheme revision must use major.minor.patch numeric form.");
        }
    }

    private static void RequireSha256(string? value, string code)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64 || !value.All(static character => char.IsAsciiHexDigit(character)))
        {
            throw Failure(code, "Value must be a 64-character SHA-256 hexadecimal string.");
        }
    }

    private static KnowledgeBaseException Failure(string code, string message) => new(code, message);
}
