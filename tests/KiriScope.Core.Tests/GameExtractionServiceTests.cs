using System.IO.Compression;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using KiriScope.Core.Diagnostics;
using KiriScope.Filters.BuiltIn;
using KiriScope.Resources;
using KiriScope.Xp3;

namespace KiriScope.Core.Tests;

public sealed class GameExtractionServiceTests
{
    [Fact]
    public async Task ExtractAsync_RuntimeFallbackFailureDoesNotSilentlyRunRawStaticExtraction()
    {
        var root = CreateTemporaryRoot();
        var gameDirectory = Path.Combine(root, "game");
        var outputDirectory = Path.Combine(root, "output");
        Directory.CreateDirectory(gameDirectory);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(gameDirectory, "data.xp3"), CreateArchive([("title.bin", "content"u8.ToArray())]));
            var fallback = new FailingRuntimeFallback();

            var result = await GameExtractionService.ExtractAsync(
                GameInput.FromPath(gameDirectory),
                ResourceCategory.All,
                outputDirectory,
                new GameExtractionOptions { RuntimeExtractionFallback = fallback });

            Assert.True(fallback.WasCalled);
            Assert.True(result.HasErrors);
            Assert.False(result.OutputDirectoryCreated);
            Assert.False(Directory.Exists(outputDirectory));
            Assert.Equal(0, result.ExtractedEntryCount);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "RUNTIME_CAPTURE_PLAN_FAILED");
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task ExtractAsync_GameDirectoryExportsOnlyTheRequestedCategory()
    {
        var root = CreateTemporaryRoot();
        var gameDirectory = Path.Combine(root, "game");
        var outputDirectory = Path.Combine(root, "output");
        Directory.CreateDirectory(gameDirectory);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(gameDirectory, "data.xp3"), CreateArchive(
            [
                ("art/title.png", "image"u8.ToArray()),
                ("audio/theme.ogg", "audio"u8.ToArray()),
                ("script/start.tjs", "script"u8.ToArray()),
            ]));
            await File.WriteAllBytesAsync(Path.Combine(gameDirectory, "game.exe"), [0x4D, 0x5A]);

            var discovery = await GameExtractionService.DiscoverAsync(GameInput.FromPath(gameDirectory));
            var result = await GameExtractionService.ExtractAsync(
                GameInput.FromPath(gameDirectory), ResourceCategory.Images, outputDirectory);

            Assert.False(discovery.HasErrors);
            Assert.Equal(["game.exe"], discovery.Executables);
            Assert.True(result.OutputDirectoryCreated);
            Assert.Equal(1, result.SelectedEntryCount);
            Assert.Equal(1, result.ExtractedEntryCount);
            Assert.Equal(0, result.SkippedEntryCount);
            Assert.Equal("image", await File.ReadAllTextAsync(Path.Combine(outputDirectory, "data", "art", "title.png")));
            Assert.False(File.Exists(Path.Combine(outputDirectory, "data", "audio", "theme.ogg")));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task ExtractAsync_CompleteGameZipStagesOnlyTheInnerXp3AndCleansUp()
    {
        var root = CreateTemporaryRoot();
        var packagePath = Path.Combine(root, "complete-game.zip");
        var outputDirectory = Path.Combine(root, "output");
        var temporaryRoot = Path.Combine(root, "temporary");
        try
        {
            using (var package = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                var executable = package.CreateEntry("Game/game.exe");
                await using (var executableStream = executable.Open())
                {
                    await executableStream.WriteAsync("MZ"u8.ToArray());
                }

                var archive = package.CreateEntry("Game/data.xp3", CompressionLevel.Optimal);
                await using var archiveStream = archive.Open();
                await archiveStream.WriteAsync(CreateArchive([("art/title.png", "image"u8.ToArray())]));
            }

            var input = GameInput.FromPath(packagePath);
            var discovery = await GameExtractionService.DiscoverAsync(input);
            var result = await GameExtractionService.ExtractAsync(
                input,
                ResourceCategory.Images,
                outputDirectory,
                new GameExtractionOptions { TemporaryRootDirectory = temporaryRoot });

            Assert.False(discovery.HasErrors);
            Assert.Equal(GameInputKind.GamePackage, input.Kind);
            Assert.Equal(["Game/game.exe"], discovery.Executables);
            Assert.Equal(1, result.ExtractedEntryCount);
            Assert.Equal(1, result.TemporarilyStagedArchiveCount);
            Assert.Equal("image", await File.ReadAllTextAsync(Path.Combine(outputDirectory, "Game", "data", "art", "title.png")));
            Assert.False(File.Exists(Path.Combine(root, "Game", "data.xp3")));
            Assert.True(!Directory.Exists(temporaryRoot) || !Directory.EnumerateFileSystemEntries(temporaryRoot).Any());
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task ExtractAsync_RejectsPackagePathTraversalBeforeCreatingOutput()
    {
        var root = CreateTemporaryRoot();
        var packagePath = Path.Combine(root, "unsafe-game.zip");
        var outputDirectory = Path.Combine(root, "output");
        try
        {
            using (var package = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                var archive = package.CreateEntry("../data.xp3");
                await using var archiveStream = archive.Open();
                await archiveStream.WriteAsync(CreateArchive([("art/title.png", "image"u8.ToArray())]));
            }

            var result = await GameExtractionService.ExtractAsync(
                GameInput.FromPath(packagePath), ResourceCategory.All, outputDirectory);

            Assert.True(result.HasErrors);
            Assert.False(result.OutputDirectoryCreated);
            Assert.False(Directory.Exists(outputDirectory));
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GAME_PACKAGE_ENTRY_PATH_REJECTED");
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task ExtractAsync_RejectsAnOutputDirectoryInsideTheGameDirectory()
    {
        var root = CreateTemporaryRoot();
        var gameDirectory = Path.Combine(root, "game");
        Directory.CreateDirectory(gameDirectory);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(gameDirectory, "data.xp3"), CreateArchive([("title.bin", "data"u8.ToArray())]));

            var exception = await Assert.ThrowsAsync<ArgumentException>(() => GameExtractionService.ExtractAsync(
                GameInput.FromPath(gameDirectory), ResourceCategory.All, Path.Combine(gameDirectory, "output")));

            Assert.Contains("outside", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task ExtractAsync_ValidatesRecognizedOutputsAndReportsCategoryMismatches()
    {
        var root = CreateTemporaryRoot();
        var gameDirectory = Path.Combine(root, "game");
        var outputDirectory = Path.Combine(root, "output");
        Directory.CreateDirectory(gameDirectory);
        try
        {
            var png = PngRgbaEncoder.Encode(new RgbaImage(1, 1, [12, 34, 56, 255]));
            await File.WriteAllBytesAsync(Path.Combine(gameDirectory, "data.xp3"), CreateArchive(
            [
                ("art/title.png", png),
                ("audio/mislabeled.ogg", png),
                ("scripts/start.tjs", "print('hello');"u8.ToArray()),
            ]));

            var result = await GameExtractionService.ExtractAsync(
                GameInput.FromPath(gameDirectory), ResourceCategory.All, outputDirectory);

            Assert.Equal(3, result.ResourceValidations.Count);
            Assert.Equal(2, result.RecognizedResourceCount);
            Assert.Equal(2, result.FormatValidatedResourceCount);
            Assert.Equal(1, result.ValidationSkippedResourceCount);
            Assert.Equal(1, result.CategoryMismatchCount);
            var title = Assert.Single(result.ResourceValidations, item => item.EntryName == "art/title.png");
            Assert.Equal(ResourceFormat.Png, title.DetectedFormat);
            Assert.True(title.IsFormatValidated);
            var mismatch = Assert.Single(result.ResourceValidations, item => item.EntryName == "audio/mislabeled.ogg");
            Assert.Equal(ResourceCategory.Images, mismatch.DetectedCategory);
            Assert.Contains(mismatch.Diagnostics, diagnostic => diagnostic.Code == "GAME_RESOURCE_CATEGORY_MISMATCH");
            var script = Assert.Single(result.ResourceValidations, item => item.EntryName == "scripts/start.tjs");
            Assert.False(script.ValidationAttempted);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task ExtractAsync_DoesNotTreatAnExtensionlessPathAsAnIncorrectResourceCategory()
    {
        var root = CreateTemporaryRoot();
        var gameDirectory = Path.Combine(root, "game");
        var outputDirectory = Path.Combine(root, "output");
        Directory.CreateDirectory(gameDirectory);
        try
        {
            var png = PngRgbaEncoder.Encode(new RgbaImage(1, 1, [12, 34, 56, 255]));
            await File.WriteAllBytesAsync(Path.Combine(gameDirectory, "data.xp3"), CreateArchive(
            [
                ("assets/6ecb3132a8fefc0632a2f6c3911ebc41", png),
            ]));

            var result = await GameExtractionService.ExtractAsync(
                GameInput.FromPath(gameDirectory), ResourceCategory.All, outputDirectory);

            Assert.Equal(1, result.RecognizedResourceCount);
            Assert.Equal(0, result.CategoryMismatchCount);
            Assert.DoesNotContain(result.ResourceValidations.Single().Diagnostics,
                diagnostic => diagnostic.Code == "GAME_RESOURCE_CATEGORY_MISMATCH");
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task ExtractAsync_SelectsAStaticFilterOnlyAfterCurrentInputAdler32Proofs()
    {
        var root = CreateTemporaryRoot();
        var gameDirectory = Path.Combine(root, "game");
        var outputDirectory = Path.Combine(root, "output");
        Directory.CreateDirectory(gameDirectory);
        try
        {
            var firstPlain = "first protected entry"u8.ToArray();
            var secondPlain = "second protected entry"u8.ToArray();
            await File.WriteAllBytesAsync(Path.Combine(gameDirectory, "data.xp3"), CreateArchive(
            [
                new ArchiveEntryFixture("script/startup.tjs", "bootstrap"u8.ToArray(), true, Adler32("bootstrap"u8)),
                new ArchiveEntryFixture("script/first.tjs", Xor(firstPlain, [0xA5, 0x5A]), true, Adler32(firstPlain)),
                new ArchiveEntryFixture("script/second.tjs", Xor(secondPlain, [0xA5, 0x5A]), true, Adler32(secondPlain)),
            ]));
            var candidate = new StaticContentFilterCandidate(
                "test.static-xor",
                "1.0.0",
                "Test static XOR",
                "synthetic test fixture",
                new RepeatingXorContentFilter([0xA5, 0x5A]),
                RequiredAdler32ProofCount: 2,
                MaximumProbeEntriesPerArchive: 8,
                MaximumProbeEntryBytes: 1024);

            var result = await GameExtractionService.ExtractAsync(
                GameInput.FromPath(gameDirectory),
                ResourceCategory.All,
                outputDirectory,
                new GameExtractionOptions { StaticContentFilterCandidates = [candidate] });

            Assert.False(result.HasErrors);
            Assert.Equal(GameCompatibilityResolutionKind.Selected, result.Compatibility.Kind);
            Assert.Equal("static-adler32-proof", result.Compatibility.Selected?.FingerprintId);
            Assert.Equal(3, result.ExtractedEntryCount);
            Assert.Equal(0, result.SkippedEntryCount);
            Assert.Equal("bootstrap", await File.ReadAllTextAsync(Path.Combine(outputDirectory, "data", "script", "startup.tjs")));
            Assert.Equal("first protected entry", await File.ReadAllTextAsync(Path.Combine(outputDirectory, "data", "script", "first.tjs")));
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STATIC_FILTER_PROFILE_SELECTED");
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task CliUnpack_ExportsTheRequestedCategoryFromAGameDirectory()
    {
        var root = CreateTemporaryRoot();
        var gameDirectory = Path.Combine(root, "game");
        var outputDirectory = Path.Combine(root, "output");
        Directory.CreateDirectory(gameDirectory);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(gameDirectory, "data.xp3"), CreateArchive(
            [
                ("art/title.png", "image"u8.ToArray()),
                ("audio/theme.ogg", "audio"u8.ToArray()),
            ]));

            var cliPath = Path.Combine(AppContext.BaseDirectory, "KiriScope.Cli.dll");
            Assert.True(File.Exists(cliPath), $"CLI assembly was not copied to the test output: {cliPath}");
            using var process = Process.Start(new ProcessStartInfo("dotnet")
            {
                Arguments = $"\"{cliPath}\" unpack \"{gameDirectory}\" \"{outputDirectory}\" --category images",
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
            Assert.Equal("Images", report.RootElement.GetProperty("Category").GetString());
            Assert.Equal(1, report.RootElement.GetProperty("ExtractedEntryCount").GetInt32());
            Assert.Equal(1, report.RootElement.GetProperty("ResourceValidations").GetArrayLength());
            Assert.Equal("image", await File.ReadAllTextAsync(Path.Combine(outputDirectory, "data", "art", "title.png")));
            Assert.False(File.Exists(Path.Combine(outputDirectory, "data", "audio", "theme.ogg")));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
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

    private static byte[] CreateArchive(IReadOnlyList<(string Name, byte[] Content)> entries)
    {
        return CreateArchive(entries.Select(static entry => new ArchiveEntryFixture(entry.Name, entry.Content, false, null)).ToArray());
    }

    private static byte[] CreateArchive(IReadOnlyList<ArchiveEntryFixture> entries)
    {
        const int archiveHeaderLength = 19;
        var dataLength = entries.Sum(static entry => entry.Content.Length);
        var dataOffsets = new long[entries.Count];
        var nextOffset = archiveHeaderLength;
        for (var index = 0; index < entries.Count; index++)
        {
            dataOffsets[index] = nextOffset;
            nextOffset += entries[index].Content.Length;
        }

        using var indexData = new MemoryStream();
        using (var indexWriter = new BinaryWriter(indexData, Encoding.UTF8, leaveOpen: true))
        {
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                using var info = new MemoryStream();
                using (var infoWriter = new BinaryWriter(info, Encoding.Unicode, leaveOpen: true))
                {
                    infoWriter.Write(entry.IsMarkedEncrypted ? 1U : 0U);
                    infoWriter.Write((long)entry.Content.Length);
                    infoWriter.Write((long)entry.Content.Length);
                    infoWriter.Write((ushort)entry.Name.Length);
                    infoWriter.Write(entry.Name.ToCharArray());
                }

                using var segments = new MemoryStream();
                using (var segmentWriter = new BinaryWriter(segments, Encoding.UTF8, leaveOpen: true))
                {
                    segmentWriter.Write(0);
                    segmentWriter.Write(dataOffsets[index]);
                    segmentWriter.Write((long)entry.Content.Length);
                    segmentWriter.Write((long)entry.Content.Length);
                }

                using var file = new MemoryStream();
                using (var fileWriter = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true))
                {
                    WriteChunk(fileWriter, 0x6F666E69, info.ToArray());
                    WriteChunk(fileWriter, 0x6D676573, segments.ToArray());
                    if (entry.Adler32 is { } adler32)
                    {
                        WriteChunk(fileWriter, 0x726C6461, BitConverter.GetBytes(adler32));
                    }
                }

                WriteChunk(indexWriter, 0x656C6946, file.ToArray());
            }
        }

        var indexOffset = archiveHeaderLength + dataLength;
        using var archive = new MemoryStream();
        archive.Write(Xp3Signature.Bytes);
        using (var writer = new BinaryWriter(archive, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((long)indexOffset);
            foreach (var entry in entries)
            {
                writer.Write(entry.Content);
            }

            writer.Write((byte)0);
            writer.Write((long)indexData.Length);
            writer.Write(indexData.ToArray());
        }

        return archive.ToArray();
    }

    private static void WriteChunk(BinaryWriter writer, uint tag, byte[] data)
    {
        writer.Write(tag);
        writer.Write((long)data.Length);
        writer.Write(data);
    }

    private static uint Adler32(ReadOnlySpan<byte> input)
    {
        const uint modulo = 65521;
        uint a = 1;
        uint b = 0;
        foreach (var value in input)
        {
            a = (a + value) % modulo;
            b = (b + a) % modulo;
        }

        return (b << 16) | a;
    }

    private static byte[] Xor(ReadOnlySpan<byte> input, ReadOnlySpan<byte> key)
    {
        var output = input.ToArray();
        for (var index = 0; index < output.Length; index++)
        {
            output[index] ^= key[index % key.Length];
        }

        return output;
    }

    private sealed record ArchiveEntryFixture(string Name, byte[] Content, bool IsMarkedEncrypted, uint? Adler32);

    private sealed class FailingRuntimeFallback : IGameRuntimeExtractionFallback
    {
        public bool WasCalled { get; private set; }

        public Task<ExtractionTaskResult?> TryExtractAsync(
            GameInput input,
            ResourceCategory category,
            string outputDirectory,
            GameInputDiscoveryResult discovery,
            GameCompatibilityResolution compatibility,
            IProgress<string>? progress,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            var archive = new GameArchiveExtractionResult(
                "data.xp3",
                false,
                false,
                0,
                0,
                0,
                0,
                Array.Empty<Xp3EntryExtractionResult>(),
                Array.Empty<GameExtractedResourceValidation>(),
                [new KiriScopeDiagnostic("RUNTIME_CAPTURE_PLAN_FAILED", DiagnosticSeverity.Error, "Synthetic runtime-plan failure.")]);
            return Task.FromResult<ExtractionTaskResult?>(new ExtractionTaskResult(
                input,
                category,
                compatibility,
                outputDirectory,
                false,
                [archive],
                [new KiriScopeDiagnostic("RUNTIME_CAPTURE_PLAN_FAILED", DiagnosticSeverity.Error, "Synthetic runtime-plan failure.")]));
        }
    }
}
