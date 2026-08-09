using System.Security.Cryptography;
using System.Text;
using KiriScope.Core.Diagnostics;
using KiriScope.IO.Hashing;

namespace KiriScope.Xp3;

/// <summary>
/// Creates a new standard, unencrypted, uncompressed XP3 archive from a staging directory.
/// It never modifies a source file, never writes into the source tree, and never overwrites an archive.
/// </summary>
public static class Xp3ArchivePacker
{
    private const int ArchiveHeaderLength = 19;
    private const int CopyBufferSize = 128 * 1024;
    private const uint FileChunk = 0x656C6946; // File
    private const uint InfoChunk = 0x6F666E69; // info
    private const uint SegmentChunk = 0x6D676573; // segm
    private const uint AdlerChunk = 0x726C6461; // adlr

    public static async Task<Xp3ArchivePackResult> PackDirectoryAsync(
        string sourceDirectory,
        string outputPath,
        Xp3ArchivePackOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        options ??= new Xp3ArchivePackOptions();
        ValidateOptions(options);

        var sourceRoot = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException($"XP3 pack source directory does not exist: {sourceRoot}");
        }

        var outputFullPath = Path.GetFullPath(outputPath);
        var outputDirectory = Path.GetDirectoryName(outputFullPath);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("XP3 pack output path must have a parent directory.", nameof(outputPath));
        }

        if (IsContainedBy(sourceRoot, outputFullPath))
        {
            throw new ArgumentException("XP3 pack output must be outside the source directory to avoid modifying the staged input tree.", nameof(outputPath));
        }

        if (File.Exists(outputFullPath))
        {
            throw new IOException($"XP3 pack output already exists and will not be overwritten: {outputFullPath}");
        }

        var sources = EnumerateSources(sourceRoot, options, cancellationToken);
        if (sources.Count == 0)
        {
            throw new InvalidOperationException("XP3 pack source directory contains no regular files.");
        }

        Directory.CreateDirectory(outputDirectory);
        var temporaryPath = outputFullPath + ".kiriscope-partial-" + Guid.NewGuid().ToString("N");
        try
        {
            var pendingEntries = new List<PendingEntry>(sources.Count);
            long indexOffset;
            long archiveLength;
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous))
            {
                await output.WriteAsync(Xp3Signature.Bytes.ToArray(), cancellationToken).ConfigureAwait(false);
                await output.WriteAsync(new byte[sizeof(long)], cancellationToken).ConfigureAwait(false);

                long totalBytes = 0;
                foreach (var source in sources)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entryOffset = output.Position;
                    var copied = await CopySourceAsync(source, output, cancellationToken).ConfigureAwait(false);
                    totalBytes = checked(totalBytes + copied.Length);
                    if (totalBytes > options.MaximumTotalBytes)
                    {
                        throw new IOException($"XP3 pack source total exceeds the configured {options.MaximumTotalBytes:N0}-byte limit.");
                    }

                    pendingEntries.Add(new PendingEntry(source, entryOffset, copied.Length, copied.Sha256, copied.Adler32));
                }

                indexOffset = output.Position;
                var index = BuildIndex(pendingEntries);
                if (index.Length > Xp3ReadOptions.DefaultMaximumIndexSize)
                {
                    throw new IOException($"XP3 pack index exceeds the supported {Xp3ReadOptions.DefaultMaximumIndexSize:N0}-byte limit.");
                }

                await output.WriteAsync(new byte[] { 0 }, cancellationToken).ConfigureAwait(false);
                await WriteInt64Async(output, index.Length, cancellationToken).ConfigureAwait(false);
                await output.WriteAsync(index, cancellationToken).ConfigureAwait(false);

                output.Position = Xp3Signature.Bytes.Length;
                await WriteInt64Async(output, indexOffset, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                archiveLength = output.Length;
            }

            var entries = pendingEntries.Select(static entry => new Xp3PackedEntry(
                entry.Source.FullPath,
                entry.Source.EntryName,
                entry.Length,
                entry.Sha256,
                entry.Adler32)).ToArray();
            var archiveHash = await Sha256Hasher.ComputeFileAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, outputFullPath, overwrite: false);
            return new Xp3ArchivePackResult(
                outputFullPath,
                archiveHash,
                indexOffset,
                archiveLength,
                entries,
                [new KiriScopeDiagnostic("XP3_ARCHIVE_PACKED", DiagnosticSeverity.Info, "Created a standard unencrypted, uncompressed XP3 archive from the selected staging directory.")]);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static List<SourceFile> EnumerateSources(string root, Xp3ArchivePackOptions options, CancellationToken cancellationToken)
    {
        var sources = new List<SourceFile>();
        long totalBytes = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = false,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint,
            }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new FileInfo(path);
                if (info.Length > options.MaximumFileBytes)
                {
                    throw new IOException($"XP3 pack source file exceeds the configured {options.MaximumFileBytes:N0}-byte limit: {path}");
                }

                totalBytes = checked(totalBytes + info.Length);
                if (totalBytes > options.MaximumTotalBytes)
                {
                    throw new IOException($"XP3 pack source total exceeds the configured {options.MaximumTotalBytes:N0}-byte limit.");
                }

                var entryName = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
                if (entryName.Length == 0 || entryName.Length > ushort.MaxValue || entryName.IndexOf('\0') >= 0)
                {
                    throw new IOException($"XP3 pack source path cannot be represented as a standard XP3 entry name: {path}");
                }

                sources.Add(new SourceFile(Path.GetFullPath(path), entryName, info.Length, info.LastWriteTimeUtc));
                if (sources.Count > options.MaximumEntryCount)
                {
                    throw new IOException($"XP3 pack source exceeds the configured {options.MaximumEntryCount:N0}-entry limit.");
                }
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new IOException("XP3 pack could not enumerate an input directory.", exception);
        }

        sources.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.EntryName, right.EntryName));
        if (sources.Select(static source => source.EntryName).Distinct(StringComparer.Ordinal).Count() != sources.Count)
        {
            throw new IOException("XP3 pack source contains duplicate archive entry paths.");
        }

        return sources;
    }

    private static async Task<CopiedSource> CopySourceAsync(SourceFile source, Stream output, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            source.FullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var adler32 = new Adler32Accumulator();
        var buffer = new byte[CopyBufferSize];
        long copied = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var data = buffer.AsMemory(0, read);
            hash.AppendData(data.Span);
            adler32.Update(data.Span);
            await output.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            copied = checked(copied + read);
        }

        var after = new FileInfo(source.FullPath);
        if (copied != source.ExpectedLength || after.Length != source.ExpectedLength || after.LastWriteTimeUtc != source.LastWriteTimeUtc)
        {
            throw new IOException($"XP3 pack source changed while being read: {source.FullPath}");
        }

        return new CopiedSource(copied, Convert.ToHexStringLower(hash.GetHashAndReset()), adler32.Value);
    }

    private static byte[] BuildIndex(IReadOnlyList<PendingEntry> entries)
    {
        using var index = new MemoryStream();
        using var writer = new BinaryWriter(index, Encoding.Unicode, leaveOpen: true);
        foreach (var entry in entries)
        {
            var fileData = BuildFileChunk(entry);
            WriteChunk(writer, FileChunk, fileData);
        }

        writer.Flush();
        return index.ToArray();
    }

    private static byte[] BuildFileChunk(PendingEntry entry)
    {
        using var file = new MemoryStream();
        using var writer = new BinaryWriter(file, Encoding.Unicode, leaveOpen: true);

        using (var info = new MemoryStream())
        {
            using (var infoWriter = new BinaryWriter(info, Encoding.Unicode, leaveOpen: true))
            {
                infoWriter.Write(0U);
                infoWriter.Write(entry.Length);
                infoWriter.Write(entry.Length);
                infoWriter.Write((ushort)entry.Source.EntryName.Length);
                infoWriter.Write(entry.Source.EntryName.ToCharArray());
            }

            WriteChunk(writer, InfoChunk, info.ToArray());
        }

        using (var segments = new MemoryStream())
        {
            using (var segmentWriter = new BinaryWriter(segments, Encoding.UTF8, leaveOpen: true))
            {
                segmentWriter.Write(0);
                segmentWriter.Write(entry.Offset);
                segmentWriter.Write(entry.Length);
                segmentWriter.Write(entry.Length);
            }

            WriteChunk(writer, SegmentChunk, segments.ToArray());
        }

        WriteChunk(writer, AdlerChunk, BitConverter.GetBytes(entry.Adler32));
        writer.Flush();
        return file.ToArray();
    }

    private static void WriteChunk(BinaryWriter writer, uint tag, byte[] data)
    {
        writer.Write(tag);
        writer.Write((long)data.Length);
        writer.Write(data);
    }

    private static async Task WriteInt64Async(Stream output, long value, CancellationToken cancellationToken)
    {
        var bytes = BitConverter.GetBytes(value);
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsContainedBy(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return !relative.Equals("..", StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative);
    }

    private static void ValidateOptions(Xp3ArchivePackOptions options)
    {
        if (options.MaximumEntryCount <= 0 || options.MaximumEntryCount > 250_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaximumEntryCount must be between 1 and 250,000.");
        }

        if (options.MaximumFileBytes <= 0 || options.MaximumTotalBytes <= 0 || options.MaximumFileBytes > options.MaximumTotalBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "File and total byte limits must be positive, and file limit must not exceed total limit.");
        }
    }

    private sealed record SourceFile(string FullPath, string EntryName, long ExpectedLength, DateTime LastWriteTimeUtc);

    private sealed record PendingEntry(SourceFile Source, long Offset, long Length, string Sha256, uint Adler32);

    private sealed record CopiedSource(long Length, string Sha256, uint Adler32);
}
