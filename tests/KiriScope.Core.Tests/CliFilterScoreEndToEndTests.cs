using System.Diagnostics;
using System.Text;
using System.Text.Json;
using KiriScope.Filters.BuiltIn;
using KiriScope.Plugins.Abstractions.Filters;
using KiriScope.Resources;
using KiriScope.Xp3;

namespace KiriScope.Core.Tests;

public sealed class CliFilterScoreEndToEndTests
{
    [Fact]
    public async Task FilterScore_ReportsTheAcceptedSchemeAndParameterSource()
    {
        var directory = Path.Combine(Path.GetTempPath(), "KiriScope.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var inputPath = Path.Combine(directory, "title.bin");
            var schemePath = Path.Combine(directory, "reference.scheme.json");
            var plaintext = PngRgbaEncoder.Encode(new RgbaImage(1, 1, [25, 50, 75, 255]));
            var ciphertext = plaintext.ToArray();
            await new RepeatingXorContentFilter([0xA5, 0x5A]).TransformAsync(
                new ContentFilterContext("image/title.png", 0x34127856U, 0, 0),
                ciphertext);
            await File.WriteAllBytesAsync(inputPath, ciphertext);
            await File.WriteAllTextAsync(schemePath, """
                {
                  "id": "test.cli-reference-xor",
                  "displayName": "CLI reference XOR",
                  "algorithmId": "builtin.repeating-xor",
                  "algorithmVersion": "1.0",
                  "parameterSource": {
                    "kind": "synthetic-test",
                    "reference": "tests/CliFilterScoreEndToEndTests.cs"
                  },
                  "parameters": { "keyHex": "A55A" }
                }
                """);

            var cliPath = Path.Combine(AppContext.BaseDirectory, "KiriScope.Cli.dll");
            Assert.True(File.Exists(cliPath), $"CLI assembly was not copied to the test output: {cliPath}");
            using var process = Process.Start(new ProcessStartInfo("dotnet")
            {
                Arguments = $"\"{cliPath}\" filter score \"{inputPath}\" \"{schemePath}\" --entry image/title.png --adler32 0x34127856",
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
            Assert.Equal(1, report.RootElement.GetProperty("AcceptedCandidateCount").GetInt32());
            var candidate = Assert.Single(report.RootElement.GetProperty("Candidates").EnumerateArray());
            Assert.True(candidate.GetProperty("IsAccepted").GetBoolean());
            Assert.Equal("test.cli-reference-xor", candidate.GetProperty("Scheme").GetProperty("Id").GetString());
            Assert.Equal("synthetic-test", candidate.GetProperty("Scheme").GetProperty("ParameterSource").GetProperty("Kind").GetString());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Xp3Extract_WithSchemeReportsTheAppliedSchemeAndWritesPlaintext()
    {
        var directory = Path.Combine(Path.GetTempPath(), "KiriScope.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var archivePath = Path.Combine(directory, "encrypted.xp3");
            var outputPath = Path.Combine(directory, "output");
            var schemePath = Path.Combine(directory, "reference.scheme.json");
            var plaintext = "scheme-backed extraction"u8.ToArray();
            var ciphertext = plaintext.ToArray();
            await new RepeatingXorContentFilter([0xA5, 0x5A]).TransformAsync(
                new ContentFilterContext("image/title.bin", null, 0, 0),
                ciphertext);
            await File.WriteAllBytesAsync(archivePath, CreateArchive("image/title.bin", ciphertext, true));
            await File.WriteAllTextAsync(schemePath, """
                {
                  "id": "test.cli-extract-xor",
                  "displayName": "CLI extraction XOR",
                  "algorithmId": "builtin.repeating-xor",
                  "algorithmVersion": "1.0",
                  "parameterSource": {
                    "kind": "synthetic-test",
                    "reference": "tests/CliFilterScoreEndToEndTests.cs"
                  },
                  "parameters": { "keyHex": "A55A" }
                }
                """);

            var result = await RunCliAsync($"xp3 extract \"{archivePath}\" \"{outputPath}\" --scheme \"{schemePath}\"");

            Assert.True(result.ExitCode == 0, result.StandardError);
            using var report = JsonDocument.Parse(result.StandardOutput);
            Assert.Equal("test.cli-extract-xor", report.RootElement.GetProperty("ContentFilter").GetProperty("Scheme").GetProperty("Id").GetString());
            Assert.Equal("builtin.repeating-xor", report.RootElement.GetProperty("ContentFilter").GetProperty("Algorithm").GetProperty("Id").GetString());
            Assert.Equal(plaintext, await File.ReadAllBytesAsync(Path.Combine(outputPath, "image", "title.bin")));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunCliAsync(string arguments)
    {
        var cliPath = Path.Combine(AppContext.BaseDirectory, "KiriScope.Cli.dll");
        Assert.True(File.Exists(cliPath), $"CLI assembly was not copied to the test output: {cliPath}");
        using var process = Process.Start(new ProcessStartInfo("dotnet")
        {
            Arguments = $"\"{cliPath}\" {arguments}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        });
        Assert.NotNull(process);
        var standardOutput = await process!.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, standardOutput, standardError);
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
}
