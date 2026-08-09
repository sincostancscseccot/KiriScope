using System.Security.Cryptography;
using KiriScope.Core.Diagnostics;
using KiriScope.Core.Evidence;

namespace KiriScope.Resources;

/// <summary>Copies one named, validated PSB resource to a newly created output file.</summary>
public static class PsbResourceExtractor
{
    private const int CopyBufferSize = 128 * 1024;
    private const int MaximumExportedResources = 10_000;
    private const long MaximumExportedBytes = 4L * 1024 * 1024 * 1024;

    public static async Task<PsbResourceExtractionResult> ExtractAsync(string psbPath, string resourceName, string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(psbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var sourcePath = Path.GetFullPath(psbPath);
        var outputFullPath = Path.GetFullPath(outputPath);
        EnsureOutputIsOutsideInputDirectory(sourcePath, outputFullPath);
        if (File.Exists(outputFullPath) || Directory.Exists(outputFullPath))
        {
            throw new IOException($"PSB resource output already exists and will not be overwritten: {outputFullPath}");
        }

        await using var input = OpenInput(sourcePath);
        var probe = await PsbStructureProbe.ProbeAsync(input, cancellationToken).ConfigureAwait(false);
        var resource = probe.RootResources.FirstOrDefault(reference => string.Equals(probe.RootKeys[reference.RootKeyIndex], resourceName, StringComparison.Ordinal));
        if (resource is null || resource.Offset is null || resource.Length is null)
            return new(resourceName, EvidenceStage.ContainerIdentified, false, 0, [new KiriScopeDiagnostic("PSB_RESOURCE_NOT_FOUND", DiagnosticSeverity.Error, "The named PSB resource was not found in the validated root resource table.")]);

        var directory = Path.GetDirectoryName(outputFullPath);
        if (string.IsNullOrEmpty(directory)) throw new ArgumentException("Output path must have a parent directory.", nameof(outputPath));
        Directory.CreateDirectory(directory);
        input.Position = resource.Offset.Value;
        var temporaryPath = outputFullPath + ".kiriscope-partial-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await CopyRangeAsync(input, output, resource.Length.Value, cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, outputFullPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
        return new(resourceName, EvidenceStage.RawDataExtracted, true, resource.Length.Value, [new KiriScopeDiagnostic("PSB_RESOURCE_EXTRACTED", DiagnosticSeverity.Info, "Validated PSB resource was copied to a newly created output file.")]);
    }

    /// <summary>
    /// Copies every direct root resource from a validated PSB/PIMG into a new directory outside the input directory.
    /// The source is opened read-only; incomplete temporary output is removed rather than promoted.
    /// </summary>
    public static async Task<PsbResourceExportResult> ExportAllAsync(string psbPath, string outputDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(psbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var sourcePath = Path.GetFullPath(psbPath);
        var outputFullPath = Path.GetFullPath(outputDirectory);
        EnsureOutputIsOutsideInputDirectory(sourcePath, outputFullPath);
        if (Directory.Exists(outputFullPath) || File.Exists(outputFullPath))
        {
            throw new IOException($"PSB export output already exists and will not be overwritten: {outputFullPath}");
        }

        var sourceInfo = new FileInfo(sourcePath);
        if (!sourceInfo.Exists)
        {
            throw new FileNotFoundException("PSB export input does not exist.", sourcePath);
        }

        await using var input = OpenInput(sourcePath);
        var probe = await PsbStructureProbe.ProbeAsync(input, cancellationToken).ConfigureAwait(false);
        var pending = CreatePendingExports(probe);
        if (pending.Count == 0)
        {
            return new(outputFullPath, EvidenceStage.ContainerIdentified, false, Array.Empty<PsbExportedResource>(),
                [new KiriScopeDiagnostic("PSB_NO_MAPPED_ROOT_RESOURCES", DiagnosticSeverity.Warning, "The validated PSB/PIMG has no direct root resources with mapped data ranges.")]);
        }

        if (pending.Count > MaximumExportedResources)
        {
            throw new InvalidDataException($"PSB export has more than the {MaximumExportedResources:N0}-resource safety limit.");
        }

        var totalLength = pending.Aggregate(0L, static (total, item) => checked(total + item.Reference.Length!.Value));
        if (totalLength > MaximumExportedBytes)
        {
            throw new InvalidDataException($"PSB export exceeds the {MaximumExportedBytes:N0}-byte safety limit.");
        }

        var parent = Path.GetDirectoryName(outputFullPath);
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new ArgumentException("PSB export output directory must have a parent directory.", nameof(outputDirectory));
        }

        Directory.CreateDirectory(parent);
        var temporaryDirectory = outputFullPath + ".kiriscope-partial-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            var exported = new List<PsbExportedResource>(pending.Count);
            foreach (var item in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var temporaryPath = Path.Combine(temporaryDirectory, item.OutputFileName);
                input.Position = item.Reference.Offset!.Value;
                await using var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var copied = await CopyRangeAsync(input, output, item.Reference.Length!.Value, cancellationToken).ConfigureAwait(false);
                exported.Add(new PsbExportedResource(item.ResourceName, item.Reference.ResourceIndex, Path.Combine(outputFullPath, item.OutputFileName), copied.Length, copied.Sha256));
            }

            var sourceAfter = new FileInfo(sourcePath);
            if (sourceAfter.Length != sourceInfo.Length || sourceAfter.LastWriteTimeUtc != sourceInfo.LastWriteTimeUtc)
            {
                throw new IOException($"PSB export input changed while being read: {sourcePath}");
            }

            Directory.Move(temporaryDirectory, outputFullPath);
            return new(outputFullPath, EvidenceStage.RawDataExtracted, true, exported,
                [new KiriScopeDiagnostic("PSB_ROOT_RESOURCES_EXPORTED", DiagnosticSeverity.Info, "Validated direct PSB root resources were copied to a newly created output directory.")]);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    private static List<PendingExport> CreatePendingExports(PsbStructureProbeResult probe)
    {
        var pending = new List<PendingExport>();
        foreach (var reference in probe.RootResources.Where(reference => reference.Offset is not null && reference.Length is not null)
                     .OrderBy(reference => reference.ResourceIndex)
                     .ThenBy(reference => probe.RootKeys[reference.RootKeyIndex], StringComparer.Ordinal)
                     .ThenBy(reference => reference.RootKeyIndex))
        {
            var resourceName = probe.RootKeys[reference.RootKeyIndex];
            pending.Add(new PendingExport(reference, resourceName, CreateOutputFileName(reference, resourceName)));
        }

        return pending;
    }

    private static string CreateOutputFileName(PsbResourceReference reference, string resourceName)
    {
        var safeName = string.Concat(resourceName.Select(character => character is '/' or '\\' || char.IsControl(character) || Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Trim().TrimEnd('.');
        if (safeName.Length == 0)
        {
            safeName = "resource";
        }

        const int maximumNameLength = 180;
        if (safeName.Length > maximumNameLength)
        {
            safeName = safeName[..maximumNameLength];
        }

        return $"{reference.ResourceIndex:D6}-{reference.RootKeyIndex:D6}-{safeName}";
    }

    private static async Task<CopiedRange> CopyRangeAsync(Stream input, Stream output, long length, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var remaining = length;
        var buffer = new byte[CopyBufferSize];
        while (remaining > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidDataException("PSB resource ended before its validated length.");
            }

            hash.AppendData(buffer.AsSpan(0, read));
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }

        return new CopiedRange(length, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static FileStream OpenInput(string sourcePath) =>
        new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static void EnsureOutputIsOutsideInputDirectory(string sourcePath, string outputPath)
    {
        var sourceDirectory = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(sourceDirectory))
        {
            throw new ArgumentException("PSB input path must have a parent directory.", nameof(sourcePath));
        }

        var relative = Path.GetRelativePath(sourceDirectory, outputPath);
        if (!relative.Equals("..", StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative))
        {
            throw new ArgumentException("PSB output must be outside the input directory to avoid modifying the source tree.", nameof(outputPath));
        }
    }

    private sealed record PendingExport(PsbResourceReference Reference, string ResourceName, string OutputFileName);

    private sealed record CopiedRange(long Length, string Sha256);
}

public sealed record PsbResourceExtractionResult(string ResourceName, EvidenceStage Stage, bool Succeeded, long BytesWritten, IReadOnlyList<KiriScopeDiagnostic> Diagnostics);
public sealed record PsbExportedResource(string ResourceName, uint ResourceIndex, string OutputFile, long BytesWritten, string Sha256);
public sealed record PsbResourceExportResult(string OutputDirectory, EvidenceStage Stage, bool Succeeded, IReadOnlyList<PsbExportedResource> Resources, IReadOnlyList<KiriScopeDiagnostic> Diagnostics);
