using KiriScope.Filters.BuiltIn;

namespace KiriScope.Core.Tests;

public sealed class BuiltInContentFilterSchemeLoaderTests
{
    [Fact]
    public async Task Load_CreatesAReportableSchemeWithItsParameterSource()
    {
        var directory = Path.Combine(Path.GetTempPath(), "KiriScope.Tests", Guid.NewGuid().ToString("N"));
        var schemePath = Path.Combine(directory, "reference.scheme.json");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(schemePath, """
                {
                  "id": "test.reference-xor",
                  "displayName": "Test reference XOR",
                  "algorithmId": "builtin.repeating-xor",
                  "algorithmVersion": "1.0",
                  "parameterSource": {
                    "kind": "synthetic-test",
                    "reference": "tests/BuiltInContentFilterSchemeLoaderTests.cs",
                    "notes": "No game-specific parameter is asserted."
                  },
                  "parameters": {
                    "keyHex": "A55A"
                  }
                }
                """);

            var scheme = BuiltInContentFilterSchemeLoader.Load(schemePath);

            Assert.Equal("test.reference-xor", scheme.Descriptor.Id);
            Assert.Equal("builtin.repeating-xor", scheme.Descriptor.AlgorithmId);
            Assert.Equal("synthetic-test", scheme.Descriptor.ParameterSource.Kind);
            Assert.Equal(Path.GetFullPath(schemePath), scheme.SourcePath);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
