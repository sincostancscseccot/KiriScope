using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace KiriScope.Gui;

/// <summary>Extracts self-contained runtime workers embedded by the single-file publish script.</summary>
internal static class BundledRuntimeWorkers
{
    private const string X64ResourceName = "KiriScope.Gui.RuntimeWorkers.X64";
    private const string X86ResourceName = "KiriScope.Gui.RuntimeWorkers.X86";
    private const int BufferSize = 128 * 1024;

    /// <summary>
    /// Returns null for the normal multi-file layout. Embedded workers are expanded only after a caller has
    /// obtained explicit runtime-capture consent, and are content-addressed and verified before use.
    /// </summary>
    public static async Task<RuntimeWorkerPaths?> ExtractAsync(CancellationToken cancellationToken = default)
    {
        var assembly = typeof(BundledRuntimeWorkers).Assembly;
        var hasX64 = assembly.GetManifestResourceInfo(X64ResourceName) is not null;
        var hasX86 = assembly.GetManifestResourceInfo(X86ResourceName) is not null;
        if (!hasX64 && !hasX86)
        {
            return null;
        }

        if (!hasX64 || !hasX86)
        {
            throw new InvalidDataException("单文件 GUI 包未包含两个运行时工作进程。");
        }

        var x64Path = await ExtractResourceAsync(assembly, X64ResourceName, "KiriScope.Worker.X64.exe", cancellationToken).ConfigureAwait(false);
        var x86Path = await ExtractResourceAsync(assembly, X86ResourceName, "KiriScope.Worker.X86.exe", cancellationToken).ConfigureAwait(false);
        return new RuntimeWorkerPaths(x86Path, x64Path);
    }

    private static async Task<string> ExtractResourceAsync(
        Assembly assembly,
        string resourceName,
        string fileName,
        CancellationToken cancellationToken)
    {
        await using var source = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException($"缺少内嵌的运行时工作进程资源：{resourceName}。");

        var rootDirectory = Path.Combine(Path.GetTempPath(), "KiriScope", "runtime-workers");
        Directory.CreateDirectory(rootDirectory);
        var stagingPath = Path.Combine(rootDirectory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            string sha256;
            await using (var destination = new FileStream(
                             stagingPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             BufferSize,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[BufferSize];
                while (true)
                {
                    var bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    hash.AppendData(buffer, 0, bytesRead);
                    await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                }

                sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            var workerDirectory = Path.Combine(rootDirectory, sha256);
            Directory.CreateDirectory(workerDirectory);
            var workerPath = Path.Combine(workerDirectory, fileName);
            if (File.Exists(workerPath))
            {
                await VerifyHashAsync(workerPath, sha256, cancellationToken).ConfigureAwait(false);
                return workerPath;
            }

            try
            {
                File.Move(stagingPath, workerPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(workerPath))
            {
                await VerifyHashAsync(workerPath, sha256, cancellationToken).ConfigureAwait(false);
            }

            await VerifyHashAsync(workerPath, sha256, cancellationToken).ConfigureAwait(false);
            return workerPath;
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
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actualSha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"展开的运行时工作进程 SHA-256 校验不匹配：{path}。");
        }
    }
}

internal sealed record RuntimeWorkerPaths(string X86Path, string X64Path);
