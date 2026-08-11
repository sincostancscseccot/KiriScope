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

    [Fact]
    public async Task Load_AcceptsALittleEndianBase64CxControlBlock()
    {
        var directory = Path.Combine(Path.GetTempPath(), "KiriScope.Tests", Guid.NewGuid().ToString("N"));
        var schemePath = Path.Combine(directory, "cx.scheme.json");
        Directory.CreateDirectory(directory);
        try
        {
            var controlBlock = Convert.ToBase64String(new byte[4096]);
            await File.WriteAllTextAsync(schemePath, $$"""
                {
                  "id": "test.cx-base64",
                  "displayName": "Test Cx base64",
                  "algorithmId": "builtin.cx-encryption",
                  "algorithmVersion": "1.0",
                  "parameterSource": {
                    "kind": "synthetic-test",
                    "reference": "tests/BuiltInContentFilterSchemeLoaderTests.cs"
                  },
                  "parameters": {
                    "mask": 0,
                    "offset": 0,
                    "prologOrder": [0, 1, 2],
                    "oddBranchOrder": [0, 1, 2, 3, 4, 5],
                    "evenBranchOrder": [0, 1, 2, 3, 4, 5, 6, 7],
                    "controlBlock": "base64le:{{controlBlock}}"
                  }
                }
                """);

            var scheme = BuiltInContentFilterSchemeLoader.Load(schemePath);

            Assert.Equal("test.cx-base64", scheme.Descriptor.Id);
            Assert.Equal("builtin.cx-encryption", scheme.Filter.Descriptor.Id);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
