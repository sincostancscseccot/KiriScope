using System.Diagnostics;
using System.Text;
using System.Text.Json;
using KiriScope.Knowledge;

namespace KiriScope.Core.Tests;

public sealed class GameResearchPackageTests
{
    [Fact]
    public async Task CollectAndWriteNewAsync_CreatesMetadataOnlyPackageAndHashesExplicitRuntimeEvidence()
    {
        var root = CreateTemporaryRoot();
        var gameDirectory = Path.Combine(root, "game");
        var outputPath = Path.Combine(root, "research-package.json");
        var runtimeEvidencePath = Path.Combine(root, "runtime-evidence.json");
        Directory.CreateDirectory(gameDirectory);
        try
        {
            var archivePath = Path.Combine(gameDirectory, "data.xp3");
            await File.WriteAllBytesAsync(archivePath, CreateArchive([("scripts/start.tjs", "secret-game-content"u8.ToArray())]));
            await File.WriteAllBytesAsync(Path.Combine(gameDirectory, "game.exe"), "MZ\0local-static-secret"u8.ToArray());
            await File.WriteAllTextAsync(runtimeEvidencePath, "{\"capture\":\"existing\"}");
            var archiveBefore = await File.ReadAllBytesAsync(archivePath);

            var reportPath = await GameResearchPackageService.CollectAndWriteNewAsync(
                gameDirectory,
                outputPath,
                "kiriscope research package \"game\" \"research-package.json\"",
                new GameResearchPackageOptions { RuntimeEvidencePaths = [runtimeEvidencePath] });

            Assert.Equal(Path.GetFullPath(outputPath), reportPath);
            Assert.Equal(archiveBefore, await File.ReadAllBytesAsync(archivePath));
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
            Assert.Equal(GameResearchPackage.CurrentSchemaVersion, report.RootElement.GetProperty("SchemaVersion").GetString());
            Assert.Equal(1, report.RootElement.GetProperty("Archives").GetArrayLength());
            Assert.Equal(1, report.RootElement.GetProperty("RuntimeEvidenceReferences").GetArrayLength());
            Assert.Equal("UserSuppliedRuntimeReport", report.RootElement.GetProperty("RuntimeEvidenceReferences")[0].GetProperty("Kind").GetString());
            Assert.DoesNotContain("secret-game-content", await File.ReadAllTextAsync(outputPath), StringComparison.Ordinal);
            Assert.DoesNotContain("local-static-secret", await File.ReadAllTextAsync(outputPath), StringComparison.Ordinal);
            Assert.Contains(report.RootElement.GetProperty("Diagnostics").EnumerateArray(), diagnostic =>
                diagnostic.GetProperty("Code").GetString() == "RESEARCH_STATIC_STRINGS_REDACTED");
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task CollectAndWriteNewAsync_RejectsOutputInsideGameDirectoryAndExistingReports()
    {
        var root = CreateTemporaryRoot();
        var gameDirectory = Path.Combine(root, "game");
        var existingOutput = Path.Combine(root, "existing.json");
        Directory.CreateDirectory(gameDirectory);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(gameDirectory, "data.xp3"), CreateArchive([("title.bin", "data"u8.ToArray())]));
            await File.WriteAllTextAsync(existingOutput, "existing");

            var insideException = await Assert.ThrowsAsync<ArgumentException>(() => GameResearchPackageService.CollectAndWriteNewAsync(
                gameDirectory,
                Path.Combine(gameDirectory, "research.json"),
                "kiriscope research package"));
            var rootException = await Assert.ThrowsAsync<ArgumentException>(() => GameResearchPackageService.CollectAndWriteNewAsync(
                gameDirectory,
                gameDirectory,
                "kiriscope research package"));
            var overwriteException = await Assert.ThrowsAsync<IOException>(() => GameResearchPackageService.CollectAndWriteNewAsync(
                gameDirectory,
                existingOutput,
                "kiriscope research package"));

            Assert.Contains("outside", insideException.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("outside", rootException.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("already exists", overwriteException.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("existing", await File.ReadAllTextAsync(existingOutput));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task CliResearchPackage_WritesANewPackageAndReferencesExistingRuntimeEvidence()
    {
        var root = CreateTemporaryRoot();
        var gameDirectory = Path.Combine(root, "game");
        var outputPath = Path.Combine(root, "research-package.json");
        var runtimeEvidencePath = Path.Combine(root, "runtime-evidence.json");
        Directory.CreateDirectory(gameDirectory);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(gameDirectory, "data.xp3"), CreateArchive([("art/title.png", "image"u8.ToArray())]));
            await File.WriteAllTextAsync(runtimeEvidencePath, "{\"authorized\":true}");

            var cliPath = Path.Combine(AppContext.BaseDirectory, "KiriScope.Cli.dll");
            Assert.True(File.Exists(cliPath), $"CLI assembly was not copied to the test output: {cliPath}");
            using var process = Process.Start(new ProcessStartInfo("dotnet")
            {
                Arguments = $"\"{cliPath}\" research package \"{gameDirectory}\" \"{outputPath}\" --runtime-evidence \"{runtimeEvidencePath}\"",
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
            using var commandReport = JsonDocument.Parse(standardOutput);
            Assert.True(commandReport.RootElement.GetProperty("Succeeded").GetBoolean());
            Assert.Equal(1, commandReport.RootElement.GetProperty("RuntimeEvidenceReferenceCount").GetInt32());
            using var package = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
            Assert.Equal(1, package.RootElement.GetProperty("RuntimeEvidenceReferences").GetArrayLength());
            Assert.Contains("--runtime-evidence", package.RootElement.GetProperty("ReproductionCommand").GetString(), StringComparison.Ordinal);
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
                    infoWriter.Write(0U);
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
                }

                WriteChunk(indexWriter, 0x656C6946, file.ToArray());
            }
        }

        var indexOffset = archiveHeaderLength + dataLength;
        using var archive = new MemoryStream();
        archive.Write(KiriScope.Xp3.Xp3Signature.Bytes);
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
}
