using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace KiriScope.Gui;

/// <summary>Expands the bundled, x86 KiriKiri stream-capture proxy into a content-addressed temporary file.</summary>
internal static class BundledRuntimeCapture
{
    private const string ResourceName = "KiriScope.Gui.RuntimeCapture.X86";
    private const int BufferSize = 128 * 1024;

    public static async Task<string?> ExtractAsync(CancellationToken cancellationToken = default)
    {
        var assembly = typeof(BundledRuntimeCapture).Assembly;
        if (assembly.GetManifestResourceInfo(ResourceName) is null)
        {
            return null;
        }

        await using var source = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("The bundled KiriKiri runtime-capture helper is unavailable.");

        var root = Path.Combine(Path.GetTempPath(), "KiriScope", "runtime-capture-helper");
        Directory.CreateDirectory(root);
        var stagingPath = Path.Combine(root, $".{Guid.NewGuid():N}.tmp");
        try
        {
            string sha256;
            await using (var destination = new FileStream(
                             stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[BufferSize];
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    hash.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            var helperDirectory = Path.Combine(root, sha256);
            Directory.CreateDirectory(helperDirectory);
            var helperPath = Path.Combine(helperDirectory, "version.dll");
            if (!File.Exists(helperPath))
            {
                try
                {
                    File.Move(stagingPath, helperPath, overwrite: false);
                }
                catch (IOException) when (File.Exists(helperPath))
                {
                    // Another KiriScope instance won the race. The hash check below verifies its copy.
                }
            }

            await VerifyHashAsync(helperPath, sha256, cancellationToken).ConfigureAwait(false);
            return helperPath;
        }
        finally
        {
            if (File.Exists(stagingPath))
            {
                File.Delete(stagingPath);
            }
        }
    }

    private static async Task VerifyHashAsync(string path, string expectedSha256, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actualSha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The expanded KiriKiri runtime-capture helper failed its SHA-256 check.");
        }
    }
}
