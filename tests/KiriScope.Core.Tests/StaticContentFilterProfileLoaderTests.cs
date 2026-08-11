using KiriScope.IO.Hashing;
using KiriScope.Knowledge;

namespace KiriScope.Core.Tests;

public sealed class StaticContentFilterProfileLoaderTests
{
    [Fact]
    public async Task LoadAsync_LoadsOnlyAHashBoundStaticProfile()
    {
        var root = Path.Combine(Path.GetTempPath(), "KiriScope.Tests", Guid.NewGuid().ToString("N"));
        var schemes = Path.Combine(root, "schemes");
        var schemePath = Path.Combine(schemes, "reference.scheme.json");
        Directory.CreateDirectory(schemes);
        try
        {
            await File.WriteAllTextAsync(schemePath, """
                {
                  "id": "test.static-xor",
                  "displayName": "Test static XOR",
                  "algorithmId": "builtin.repeating-xor",
                  "algorithmVersion": "1.0",
                  "parameterSource": {
                    "kind": "synthetic-test",
                    "reference": "tests/StaticContentFilterProfileLoaderTests.cs"
                  },
                  "parameters": { "keyHex": "A55A" }
                }
                """);
            var hash = await Sha256Hasher.ComputeFileAsync(schemePath);
            await File.WriteAllTextAsync(Path.Combine(root, StaticContentFilterProfileLoader.ManifestFileName), $$"""
                {
                  "schemaVersion": "1.0",
                  "profiles": [
                    {
                      "id": "test.static-xor",
                      "revision": "1.0.0",
                      "displayName": "Test static XOR",
                      "schemeFile": "schemes/reference.scheme.json",
                      "schemeSha256": "{{hash}}",
                      "algorithmId": "builtin.repeating-xor",
                      "algorithmVersion": "1.0",
                      "sourceReference": "synthetic test fixture",
                      "requiredAdler32ProofCount": 2,
                      "maximumProbeEntriesPerArchive": 4,
                      "maximumProbeEntryBytes": 1024
                    }
                  ]
                }
                """);

            var profiles = await StaticContentFilterProfileLoader.LoadAsync(root);

            var profile = Assert.Single(profiles);
            Assert.Equal("test.static-xor", profile.SchemeId);
            Assert.Equal(2, profile.RequiredAdler32ProofCount);
            Assert.Equal("builtin.repeating-xor", profile.ContentFilter.Descriptor.Id);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
