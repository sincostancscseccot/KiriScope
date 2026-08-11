using System.Security.Cryptography;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KiriScope.Knowledge;
using KiriScope.Xp3;

namespace KiriScope.Core.Tests;

public sealed class KnowledgeGameCompatibilityResolverTests
{
    [Fact]
    public async Task ExtractAsync_AutomaticallyAppliesOneVerifiedExactHashScheme()
    {
        var root = CreateTemporaryRoot();
        var gameDirectory = Path.Combine(root, "game");
        var outputDirectory = Path.Combine(root, "output");
        Directory.CreateDirectory(gameDirectory);
        try
        {
            var plaintext = "verified compatibility output"u8.ToArray();
            var ciphertext = ApplyReferenceXor(plaintext);
            var archivePath = Path.Combine(gameDirectory, "data.xp3");
            await File.WriteAllBytesAsync(archivePath, CreateArchive("art/title.bin", ciphertext, encrypted: true));
            var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(archivePath)));
            var knowledgeRoot = await CreateVerifiedKnowledgeBaseAsync(root, hash, schemeCount: 1);

            var result = await GameExtractionService.ExtractAsync(
                GameInput.FromPath(gameDirectory),
                ResourceCategory.All,
                outputDirectory,
                new GameExtractionOptions { CompatibilityResolver = new KnowledgeGameCompatibilityResolver(knowledgeRoot) });

            Assert.False(result.HasErrors);
            Assert.Equal(GameCompatibilityResolutionKind.Selected, result.Compatibility.Kind);
            Assert.Equal("verified.xor.1", result.Compatibility.Selected?.SchemeId);
            Assert.Equal(hash, result.Compatibility.Selected?.InputSha256);
            Assert.Equal(1, result.ExtractedEntryCount);
            Assert.Equal(plaintext, await File.ReadAllBytesAsync(Path.Combine(outputDirectory, "data", "art", "title.bin")));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task ExtractAsync_RefusesToChooseBetweenMultipleVerifiedExactHashSchemes()
    {
        var root = CreateTemporaryRoot();
        var archivePath = Path.Combine(root, "data.xp3");
        var outputDirectory = Path.Combine(root, "output");
        try
        {
            var ciphertext = ApplyReferenceXor("ambiguous"u8.ToArray());
            await File.WriteAllBytesAsync(archivePath, CreateArchive("art/title.bin", ciphertext, encrypted: true));
            var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(archivePath)));
            var knowledgeRoot = await CreateVerifiedKnowledgeBaseAsync(root, hash, schemeCount: 2);

            var result = await GameExtractionService.ExtractAsync(
                GameInput.FromPath(archivePath),
                ResourceCategory.All,
                outputDirectory,
                new GameExtractionOptions { CompatibilityResolver = new KnowledgeGameCompatibilityResolver(knowledgeRoot) });

            Assert.Equal(GameCompatibilityResolutionKind.Ambiguous, result.Compatibility.Kind);
            Assert.Null(result.Compatibility.Selected);
            Assert.Equal(2, result.Compatibility.Candidates.Count);
            Assert.Equal(0, result.ExtractedEntryCount);
            Assert.Equal(1, result.SkippedEntryCount);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "KNOWLEDGE_AUTO_MATCH_AMBIGUOUS");
            Assert.False(File.Exists(Path.Combine(outputDirectory, "data", "art", "title.bin")));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task CliUnpack_UsesAnExplicitTrustedKnowledgeRootWithoutRequiringSchemeJson()
    {
        var root = CreateTemporaryRoot();
        var gameDirectory = Path.Combine(root, "game");
        var outputDirectory = Path.Combine(root, "output");
        Directory.CreateDirectory(gameDirectory);
        try
        {
            var plaintext = "cli verified compatibility output"u8.ToArray();
            var archivePath = Path.Combine(gameDirectory, "data.xp3");
            await File.WriteAllBytesAsync(archivePath, CreateArchive("art/title.bin", ApplyReferenceXor(plaintext), encrypted: true));
            var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(archivePath)));
            var knowledgeRoot = await CreateVerifiedKnowledgeBaseAsync(root, hash, schemeCount: 1);
            var cliPath = Path.Combine(AppContext.BaseDirectory, "KiriScope.Cli.dll");
            Assert.True(File.Exists(cliPath), $"CLI assembly was not copied to the test output: {cliPath}");
            using var process = Process.Start(new ProcessStartInfo("dotnet")
            {
                Arguments = $"\"{cliPath}\" unpack \"{gameDirectory}\" \"{outputDirectory}\" --knowledge-root \"{knowledgeRoot}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            Assert.NotNull(process);
            var standardOutput = await process!.StandardOutput.ReadToEndAsync();
            var standardError = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.True(process.ExitCode == 0, standardError);
            using var report = JsonDocument.Parse(standardOutput);
            Assert.Equal("Selected", report.RootElement.GetProperty("Compatibility").GetProperty("Kind").GetString());
            Assert.Equal("verified.xor.1", report.RootElement.GetProperty("Compatibility").GetProperty("Selected").GetProperty("SchemeId").GetString());
            Assert.Equal(plaintext, await File.ReadAllBytesAsync(Path.Combine(outputDirectory, "data", "art", "title.bin")));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task ExtractAsync_UsesTheSameVerifiedExactHashSelectionForAnInnerZipXp3()
    {
        var root = CreateTemporaryRoot();
        var packagePath = Path.Combine(root, "game.zip");
        var outputDirectory = Path.Combine(root, "output");
        var temporaryRoot = Path.Combine(root, "temporary");
        try
        {
            var plaintext = "zip verified compatibility output"u8.ToArray();
            var archiveBytes = CreateArchive("art/title.bin", ApplyReferenceXor(plaintext), encrypted: true);
            await using (var packageStream = new FileStream(packagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var package = new ZipArchive(packageStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                var archive = package.CreateEntry("Game/data.xp3");
                await using var archiveStream = archive.Open();
                await archiveStream.WriteAsync(archiveBytes);
            }

            var hash = Convert.ToHexStringLower(SHA256.HashData(archiveBytes));
            var knowledgeRoot = await CreateVerifiedKnowledgeBaseAsync(root, hash, schemeCount: 1);
            var resolver = new KnowledgeGameCompatibilityResolver(
                knowledgeRoot,
                new KnowledgeGameCompatibilityResolverOptions { TemporaryRootDirectory = temporaryRoot });
            var result = await GameExtractionService.ExtractAsync(
                GameInput.FromPath(packagePath),
                ResourceCategory.All,
                outputDirectory,
                new GameExtractionOptions { CompatibilityResolver = resolver });

            Assert.False(result.HasErrors);
            Assert.Equal(GameCompatibilityResolutionKind.Selected, result.Compatibility.Kind);
            Assert.Equal("Game/data.xp3", result.Compatibility.Selected?.InputPath);
            Assert.Equal(plaintext, await File.ReadAllBytesAsync(Path.Combine(outputDirectory, "Game", "data", "art", "title.bin")));
            Assert.True(!Directory.Exists(temporaryRoot) || !Directory.EnumerateFileSystemEntries(temporaryRoot).Any());
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    private static async Task<string> CreateVerifiedKnowledgeBaseAsync(string root, string fingerprintSha256, int schemeCount)
    {
        var knowledgeRoot = Path.Combine(root, "knowledge");
        var schemesDirectory = Path.Combine(knowledgeRoot, "schemes");
        Directory.CreateDirectory(schemesDirectory);
        var schemes = new List<KnowledgeSchemeDocument>();
        var compatibility = new List<KnowledgeCompatibilityEntry>();
        var evidence = new KnowledgeVerificationEvidence(
            "1.0",
            fingerprintSha256,
            "FormatValidated",
            "kiriscope unpack <authorized-input> <new-output>",
            "Synthetic exact-hash compatibility evidence.");
        for (var index = 1; index <= schemeCount; index++)
        {
            var schemeId = $"verified.xor.{index}";
            var schemeFile = $"schemes/{schemeId}.json";
            var schemePath = Path.Combine(schemesDirectory, $"{schemeId}.json");
            await File.WriteAllTextAsync(schemePath, $$"""
                {
                  "id": "{{schemeId}}",
                  "displayName": "Verified synthetic XOR {{index}}",
                  "algorithmId": "builtin.repeating-xor",
                  "algorithmVersion": "1.0",
                  "parameterSource": {
                    "kind": "synthetic-test",
                    "reference": "tests/KnowledgeGameCompatibilityResolverTests.cs"
                  },
                  "parameters": { "keyHex": "A55A" }
                }
                """);
            var schemeHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(schemePath)));
            schemes.Add(new KnowledgeSchemeDocument(
                schemeId,
                "1.0.0",
                $"Verified synthetic XOR {index}",
                schemeFile,
                schemeHash,
                "builtin.repeating-xor",
                "1.0",
                KnowledgeCompatibilityStatus.Verified,
                Fingerprint: new AlgorithmFingerprint($"exact-archive-{index}", RequiredSha256: fingerprintSha256),
                Evidence: [evidence]));
            compatibility.Add(new KnowledgeCompatibilityEntry(
                "synthetic.authorized-game",
                "1.0",
                schemeId,
                "1.0.0",
                KnowledgeCompatibilityStatus.Verified,
                [evidence]));
        }

        var document = new KnowledgeBaseDocument(
            KnowledgeBaseLoader.CurrentSchemaVersion,
            "synthetic.verified-compatibility",
            "Synthetic verified compatibility",
            schemes,
            compatibility);
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        await File.WriteAllTextAsync(
            Path.Combine(knowledgeRoot, KnowledgeBaseLoader.ManifestFileName),
            JsonSerializer.Serialize(document, options));
        return knowledgeRoot;
    }

    private static byte[] ApplyReferenceXor(byte[] value)
    {
        var result = value.ToArray();
        for (var index = 0; index < result.Length; index++)
        {
            result[index] ^= index % 2 == 0 ? (byte)0xA5 : (byte)0x5A;
        }

        return result;
    }

    private static byte[] CreateArchive(string entryName, byte[] content, bool encrypted)
    {
        const int archiveHeaderLength = 19;
        var indexOffset = archiveHeaderLength + content.Length;
        using var info = new MemoryStream();
        using (var writer = new BinaryWriter(info, Encoding.Unicode, leaveOpen: true))
        {
            writer.Write(encrypted ? 0x80000000U : 0U);
            writer.Write((long)content.Length);
            writer.Write((long)content.Length);
            writer.Write((ushort)entryName.Length);
            writer.Write(entryName.ToCharArray());
        }

        using var segments = new MemoryStream();
        using (var writer = new BinaryWriter(segments, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0);
            writer.Write((long)archiveHeaderLength);
            writer.Write((long)content.Length);
            writer.Write((long)content.Length);
        }

        using var file = new MemoryStream();
        using (var writer = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true))
        {
            WriteChunk(writer, 0x6F666E69, info.ToArray());
            WriteChunk(writer, 0x6D676573, segments.ToArray());
        }

        using var index = new MemoryStream();
        using (var writer = new BinaryWriter(index, Encoding.UTF8, leaveOpen: true))
        {
            WriteChunk(writer, 0x656C6946, file.ToArray());
        }

        using var archive = new MemoryStream();
        archive.Write(Xp3Signature.Bytes);
        using (var writer = new BinaryWriter(archive, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((long)indexOffset);
            writer.Write(content);
            writer.Write((byte)0);
            writer.Write((long)index.Length);
            writer.Write(index.ToArray());
        }

        return archive.ToArray();
    }

    private static void WriteChunk(BinaryWriter writer, uint tag, byte[] data)
    {
        writer.Write(tag);
        writer.Write((long)data.Length);
        writer.Write(data);
    }

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "KiriScopeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTemporaryRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
