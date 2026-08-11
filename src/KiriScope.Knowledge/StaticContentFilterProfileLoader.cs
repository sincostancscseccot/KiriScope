using System.Text.Json;
using KiriScope.Filters.BuiltIn;
using KiriScope.IO.Hashing;
using KiriScope.Plugins.Abstractions.Filters;
using KiriScope.Xp3;

namespace KiriScope.Knowledge;

/// <summary>
/// Loads bundled static filter profiles that are eligible for verifier-driven automatic probing.
/// A listed profile is never selected merely from its label: GameExtractionService requires the
/// configured number of current-input Adler-32 proofs before it can be used.
/// </summary>
public static class StaticContentFilterProfileLoader
{
    public const string ManifestFileName = "static-filter-profiles.json";
    public const string CurrentSchemaVersion = "1.0";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<IReadOnlyList<StaticContentFilterCandidate>> LoadAsync(
        string rootDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var root = Path.GetFullPath(rootDirectory);
        var manifestPath = Path.Combine(root, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return Array.Empty<StaticContentFilterCandidate>();
        }

        StaticContentFilterProfileManifest? manifest;
        try
        {
            await using var input = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            manifest = await JsonSerializer.DeserializeAsync<StaticContentFilterProfileManifest>(input, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw Failure("STATIC_FILTER_PROFILE_MANIFEST_JSON_INVALID", exception.Message);
        }

        if (manifest is null || !string.Equals(manifest.SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw Failure("STATIC_FILTER_PROFILE_MANIFEST_INVALID", $"Static filter-profile schema '{manifest?.SchemaVersion ?? "(missing)"}' is not supported.");
        }

        var candidates = new List<StaticContentFilterCandidate>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in manifest.Profiles ?? Array.Empty<StaticContentFilterProfileDocument>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateProfile(profile);
            if (!ids.Add(profile.Id + "@" + profile.Revision))
            {
                throw Failure("STATIC_FILTER_PROFILE_DUPLICATE", $"Static filter profile '{profile.Id}@{profile.Revision}' is listed more than once.");
            }

            var schemePath = ResolveContainedPath(root, profile.SchemeFile);
            if (!File.Exists(schemePath))
            {
                throw Failure("STATIC_FILTER_PROFILE_SCHEME_NOT_FOUND", $"Static filter profile scheme does not exist: {profile.SchemeFile}");
            }

            var schemeHash = await Sha256Hasher.ComputeFileAsync(schemePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(schemeHash, profile.SchemeSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw Failure("STATIC_FILTER_PROFILE_SCHEME_HASH_MISMATCH", $"Static filter profile '{profile.Id}@{profile.Revision}' does not match its declared scheme SHA-256.");
            }

            BuiltInContentFilterScheme scheme;
            try
            {
                scheme = BuiltInContentFilterSchemeLoader.Load(schemePath);
            }
            catch (ContentFilterException exception)
            {
                throw Failure(exception.Code, $"Static filter profile '{profile.Id}@{profile.Revision}' could not load: {exception.Message}");
            }

            if (!string.Equals(scheme.Descriptor.Id, profile.Id, StringComparison.Ordinal) ||
                !string.Equals(scheme.Descriptor.AlgorithmId, profile.AlgorithmId, StringComparison.Ordinal) ||
                !string.Equals(scheme.Descriptor.AlgorithmVersion, profile.AlgorithmVersion, StringComparison.Ordinal))
            {
                throw Failure("STATIC_FILTER_PROFILE_DESCRIPTOR_MISMATCH", $"Static filter profile '{profile.Id}@{profile.Revision}' does not match its scheme descriptor.");
            }

            candidates.Add(new StaticContentFilterCandidate(
                profile.Id,
                profile.Revision,
                profile.DisplayName,
                profile.SourceReference,
                scheme.Filter,
                profile.RequiredAdler32ProofCount,
                profile.MaximumProbeEntriesPerArchive,
                profile.MaximumProbeEntryBytes));
        }

        return candidates
            .OrderBy(static candidate => candidate.SchemeId, StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.SchemeRevision, StringComparer.Ordinal)
            .ToArray();
    }

    private static void ValidateProfile(StaticContentFilterProfileDocument? profile)
    {
        if (profile is null || string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.Revision) ||
            string.IsNullOrWhiteSpace(profile.DisplayName) || string.IsNullOrWhiteSpace(profile.SchemeFile) ||
            string.IsNullOrWhiteSpace(profile.SchemeSha256) || string.IsNullOrWhiteSpace(profile.AlgorithmId) ||
            string.IsNullOrWhiteSpace(profile.AlgorithmVersion) || string.IsNullOrWhiteSpace(profile.SourceReference) ||
            profile.RequiredAdler32ProofCount <= 0 || profile.MaximumProbeEntriesPerArchive <= 0 || profile.MaximumProbeEntryBytes <= 0)
        {
            throw Failure("STATIC_FILTER_PROFILE_INVALID", "Static filter profiles must provide non-empty identifiers, source, scheme metadata, and positive probe limits.");
        }

        if (!Version.TryParse(profile.Revision, out _))
        {
            throw Failure("STATIC_FILTER_PROFILE_REVISION_INVALID", $"Static filter profile '{profile.Id}' must use a numeric revision.");
        }

        if (profile.SchemeSha256.Length != 64 || profile.SchemeSha256.Any(static value => !Uri.IsHexDigit(value)))
        {
            throw Failure("STATIC_FILTER_PROFILE_SCHEME_HASH_INVALID", $"Static filter profile '{profile.Id}' has an invalid scheme SHA-256.");
        }
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw Failure("STATIC_FILTER_PROFILE_SCHEME_PATH_INVALID", "A static filter profile scheme path must be relative.");
        }

        var resolved = Path.GetFullPath(Path.Combine(root, relativePath));
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!resolved.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw Failure("STATIC_FILTER_PROFILE_SCHEME_PATH_INVALID", "A static filter profile scheme path must stay below the profile root.");
        }

        return resolved;
    }

    private static KnowledgeBaseException Failure(string code, string message) => new(code, message);
}

/// <summary>JSON document stored beside the bundled knowledge base.</summary>
public sealed record StaticContentFilterProfileManifest(
    string SchemaVersion,
    IReadOnlyList<StaticContentFilterProfileDocument>? Profiles = null);

/// <summary>One source-bound, verifier-driven static filter candidate.</summary>
public sealed record StaticContentFilterProfileDocument(
    string Id,
    string Revision,
    string DisplayName,
    string SchemeFile,
    string SchemeSha256,
    string AlgorithmId,
    string AlgorithmVersion,
    string SourceReference,
    int RequiredAdler32ProofCount = 2,
    int MaximumProbeEntriesPerArchive = 32,
    long MaximumProbeEntryBytes = 8L * 1024 * 1024);
