using System.IO.Compression;
using KiriScope.Core.Diagnostics;
using KiriScope.Core.Evidence;
using KiriScope.IO.Paths;
using KiriScope.Plugins.Abstractions.Filters;

namespace KiriScope.Xp3;

/// <summary>
/// Extracts XP3 entry data after index parsing. Content filters operate after segment
/// decompression; without a filter, encrypted entries are never presented as decoded output.
/// </summary>
public static class Xp3EntryExtractor
{
    private const int CopyBufferSize = 128 * 1024;

    public static async Task<Xp3EntryExtractionResult> ExtractAsync(
        Stream archive,
        Xp3Entry entry,
        Stream output,
        Xp3EntryExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(output);
        if (!archive.CanRead || !archive.CanSeek)
        {
            throw new ArgumentException("Archive stream must be readable and seekable.", nameof(archive));
        }

        if (!output.CanWrite)
        {
            throw new ArgumentException("Output stream must be writable.", nameof(output));
        }

        options ??= new Xp3EntryExtractionOptions();
        var shouldApplyFilter = options.ContentFilter is not null &&
            (entry.IsMarkedEncrypted || options.ApplyFilterToUnmarkedEntries);
        if (entry.IsMarkedEncrypted && !shouldApplyFilter && !options.AllowUnfilteredMarkedEntries)
        {
            return Failed(
                entry,
                EvidenceStage.RawDataExtracted,
                "XP3_CONTENT_FILTER_REQUIRED",
                "The entry is marked as encrypted and requires a content filter before extraction.");
        }

        if (!ValidateEntryLayout(entry, archive.Length, out var layoutDiagnostic))
        {
            return new Xp3EntryExtractionResult(
                entry.Name,
                EvidenceStage.EntryLocated,
                false,
                0,
                entry.Adler32,
                null,
                options.ContentFilter?.Descriptor.Id,
                [layoutDiagnostic]);
        }

        var adler32 = new Adler32Accumulator();
        long bytesWritten = 0;
        try
        {
            for (var segmentIndex = 0; segmentIndex < entry.Segments.Count; segmentIndex++)
            {
                var segment = entry.Segments[segmentIndex];
                archive.Position = segment.Offset;
                await using var boundedInput = new BoundedReadStream(archive, segment.PackedSize, leaveOpen: true);
                Stream decodedInput = boundedInput;
                if (segment.IsCompressed)
                {
                    decodedInput = new ZLibStream(boundedInput, CompressionMode.Decompress, leaveOpen: true);
                }

                await using (decodedInput.ConfigureAwait(false))
                {
                    await CopyExactlyAsync(
                        decodedInput,
                        output,
                        segment.UnpackedSize,
                        entry,
                        segmentIndex,
                        bytesWritten,
                        shouldApplyFilter ? options.ContentFilter : null,
                        adler32,
                        cancellationToken).ConfigureAwait(false);
                    await EnsureEndOfStreamAsync(decodedInput, cancellationToken).ConfigureAwait(false);
                }

                bytesWritten = checked(bytesWritten + segment.UnpackedSize);
            }

            if (bytesWritten != entry.UnpackedSize)
            {
                return Failed(
                    entry,
                    EvidenceStage.EntryLocated,
                    "XP3_EXTRACTED_SIZE_MISMATCH",
                    "The extracted byte count does not match the XP3 info size.",
                    bytesWritten,
                    adler32.Value,
                    options.ContentFilter?.Descriptor.Id);
            }

            var verifyAdler32 = entry.Adler32 is not null &&
                (!shouldApplyFilter ? options.VerifyAdler32 : options.VerifyAdler32AfterFilter);
            if (verifyAdler32 && adler32.Value != entry.Adler32)
            {
                return Failed(
                    entry,
                    shouldApplyFilter ? EvidenceStage.ContentFilterApplied : EvidenceStage.RawDataExtracted,
                    "XP3_ADLER32_MISMATCH",
                    "Extracted content does not match the Adler-32 recorded in the XP3 index.",
                    bytesWritten,
                    adler32.Value,
                    options.ContentFilter?.Descriptor.Id);
            }

            var stage = shouldApplyFilter ? EvidenceStage.ContentFilterApplied : EvidenceStage.RawDataExtracted;
            var successCode = shouldApplyFilter ? "XP3_CONTENT_FILTER_APPLIED" : "XP3_ENTRY_EXTRACTED";
            var successMessage = shouldApplyFilter
                ? "The content filter was applied and output size was validated."
                : entry.IsMarkedEncrypted
                    ? "The marked entry was accepted without a content filter after its plain-content Adler-32 was validated."
                    : "The unencrypted XP3 entry was extracted and size-validated.";
            return new Xp3EntryExtractionResult(
                entry.Name,
                stage,
                true,
                bytesWritten,
                entry.Adler32,
                entry.Adler32 is null ? null : adler32.Value,
                shouldApplyFilter ? options.ContentFilter!.Descriptor.Id : null,
                [new KiriScopeDiagnostic(successCode, DiagnosticSeverity.Info, successMessage)]);
        }
        catch (ContentFilterException exception)
        {
            return Failed(
                entry,
                EvidenceStage.RawDataExtracted,
                exception.Code,
                exception.Message,
                bytesWritten,
                adler32.Value,
                options.ContentFilter?.Descriptor.Id);
        }
        catch (InvalidDataException exception)
        {
            return Failed(entry, EvidenceStage.EntryLocated, "XP3_SEGMENT_DECOMPRESSION_FAILED", exception.Message, bytesWritten);
        }
        catch (IOException exception)
        {
            return Failed(entry, EvidenceStage.EntryLocated, "XP3_SEGMENT_READ_FAILED", exception.Message, bytesWritten);
        }
    }

    public static async Task<Xp3ArchiveExtractionResult> ExtractAllAsync(
        string archivePath,
        string outputRoot,
        Xp3EntryExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);

        await using var archive = new FileStream(
            archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: CopyBufferSize,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        var index = await Xp3ArchiveReader.ReadIndexAsync(archive, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (index.Stage < EvidenceStage.IndexParsed)
        {
            return new Xp3ArchiveExtractionResult(false, 0, 0, Array.Empty<Xp3EntryExtractionResult>(), index.Diagnostics);
        }

        var results = new List<Xp3EntryExtractionResult>(index.Entries.Count);
        foreach (var entry in index.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                results.Add(await ExtractToFileAsync(archive, entry, outputRoot, entry.Name, options, cancellationToken).ConfigureAwait(false));
            }
            catch (ArgumentException exception)
            {
                results.Add(Failed(entry, EvidenceStage.EntryLocated, "XP3_OUTPUT_PATH_REJECTED", exception.Message));
            }
        }

        return new Xp3ArchiveExtractionResult(
            true,
            results.Count(static result => result.Succeeded),
            results.Count(static result => !result.Succeeded),
            results,
            index.Diagnostics);
    }

    /// <summary>Extracts one entry to a safe, non-overwriting path below an output root.</summary>
    public static Task<Xp3EntryExtractionResult> ExtractToFileAsync(
        Stream archive,
        Xp3Entry entry,
        string outputRoot,
        string outputRelativePath,
        Xp3EntryExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var destinationPath = SafeOutputPath.Resolve(outputRoot, outputRelativePath);
        return ExtractToFileCoreAsync(archive, entry, destinationPath, options, cancellationToken);
    }

    private static async Task<Xp3EntryExtractionResult> ExtractToFileCoreAsync(
        Stream archive,
        Xp3Entry entry,
        string destinationPath,
        Xp3EntryExtractionOptions? options,
        CancellationToken cancellationToken)
    {
        if (entry.IsMarkedEncrypted && options?.ContentFilter is null && options?.AllowUnfilteredMarkedEntries != true)
        {
            return Failed(
                entry,
                EvidenceStage.RawDataExtracted,
                "XP3_CONTENT_FILTER_REQUIRED",
                "The entry is marked as encrypted and requires a content filter before extraction.");
        }

        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrEmpty(destinationDirectory))
        {
            return Failed(entry, EvidenceStage.EntryLocated, "XP3_OUTPUT_PATH_INVALID", "The resolved output path has no parent directory.");
        }

        Directory.CreateDirectory(destinationDirectory);
        var temporaryPath = destinationPath + ".kiriscope-partial-" + Guid.NewGuid().ToString("N");
        try
        {
            Xp3EntryExtractionResult extractionResult;
            await using (var output = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: CopyBufferSize,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                extractionResult = await ExtractAsync(archive, entry, output, options, cancellationToken).ConfigureAwait(false);
                if (!extractionResult.Succeeded &&
                    options?.FallbackToVerifiedUnfilteredMarkedEntry == true &&
                    entry.IsMarkedEncrypted &&
                    options.ContentFilter is not null &&
                    extractionResult.Diagnostics.Any(static diagnostic => diagnostic.Code == "XP3_ADLER32_MISMATCH"))
                {
                    output.Position = 0;
                    output.SetLength(0);
                    var unfilteredOptions = options with
                    {
                        ContentFilter = null,
                        AllowUnfilteredMarkedEntries = true,
                        VerifyAdler32 = true,
                        VerifyAdler32AfterFilter = false,
                        FallbackToVerifiedUnfilteredMarkedEntry = false,
                    };
                    extractionResult = await ExtractAsync(archive, entry, output, unfilteredOptions, cancellationToken).ConfigureAwait(false);
                    if (extractionResult.Succeeded)
                    {
                        extractionResult = extractionResult with
                        {
                            Diagnostics =
                            [
                                .. extractionResult.Diagnostics,
                                new KiriScopeDiagnostic(
                                    "XP3_MARKED_ENTRY_ACCEPTED_UNFILTERED",
                                    DiagnosticSeverity.Info,
                                    "The marked entry did not match the active content filter, but its unfiltered bytes matched the XP3 Adler-32 and were accepted as plain content."),
                            ],
                        };
                    }
                }
                if (!extractionResult.Succeeded)
                {
                    return extractionResult;
                }
            }

            File.Move(temporaryPath, destinationPath, overwrite: false);
            return extractionResult with
            {
                Diagnostics =
                [
                    .. extractionResult.Diagnostics,
                    new KiriScopeDiagnostic("XP3_OUTPUT_WRITTEN", DiagnosticSeverity.Info, $"Extracted to '{destinationPath}'."),
                ],
            };
        }
        catch (IOException exception)
        {
            return Failed(entry, EvidenceStage.EntryLocated, "XP3_OUTPUT_WRITE_FAILED", exception.Message);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool ValidateEntryLayout(Xp3Entry entry, long archiveLength, out KiriScopeDiagnostic diagnostic)
    {
        long totalPacked = 0;
        long totalUnpacked = 0;
        foreach (var segment in entry.Segments)
        {
            if (segment.Offset < 0 || segment.PackedSize < 0 || segment.UnpackedSize < 0 ||
                segment.Offset > archiveLength || segment.PackedSize > archiveLength - segment.Offset)
            {
                diagnostic = new KiriScopeDiagnostic("XP3_SEGMENT_OUT_OF_RANGE", DiagnosticSeverity.Error,
                    "An XP3 segment points outside the archive.", segment.Offset);
                return false;
            }

            if (!segment.IsCompressed && segment.PackedSize != segment.UnpackedSize)
            {
                diagnostic = new KiriScopeDiagnostic("XP3_UNCOMPRESSED_SEGMENT_SIZE_INVALID", DiagnosticSeverity.Error,
                    "An uncompressed XP3 segment has unequal packed and unpacked sizes.", segment.Offset);
                return false;
            }

            try
            {
                totalPacked = checked(totalPacked + segment.PackedSize);
                totalUnpacked = checked(totalUnpacked + segment.UnpackedSize);
            }
            catch (OverflowException)
            {
                diagnostic = new KiriScopeDiagnostic("XP3_SEGMENT_SIZE_OVERFLOW", DiagnosticSeverity.Error,
                    "XP3 segment sizes overflowed a signed 64-bit total.");
                return false;
            }
        }

        if (totalPacked != entry.PackedSize || totalUnpacked != entry.UnpackedSize)
        {
            diagnostic = new KiriScopeDiagnostic("XP3_ENTRY_SIZE_MISMATCH", DiagnosticSeverity.Error,
                "The XP3 info sizes do not equal the sum of segment sizes.");
            return false;
        }

        diagnostic = null!;
        return true;
    }

    private static async Task CopyExactlyAsync(
        Stream input,
        Stream output,
        long expectedLength,
        Xp3Entry entry,
        int segmentIndex,
        long logicalOffset,
        IContentFilter? contentFilter,
        Adler32Accumulator adler32,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[CopyBufferSize];
        var remaining = expectedLength;
        var processed = 0L;
        while (remaining > 0)
        {
            var requested = (int)Math.Min(buffer.Length, remaining);
            var read = await input.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidDataException("XP3 segment ended before its declared size.");
            }

            var data = buffer.AsMemory(0, read);
            if (contentFilter is not null)
            {
                await contentFilter.TransformAsync(
                    new ContentFilterContext(entry.Name, entry.Adler32, segmentIndex, logicalOffset + processed),
                    data,
                    cancellationToken).ConfigureAwait(false);
            }

            adler32.Update(data.Span);
            await output.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            processed += read;
            remaining -= read;
        }
    }

    private static async Task EnsureEndOfStreamAsync(Stream input, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        if (await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new InvalidDataException("XP3 segment exceeds its declared unpacked size.");
        }
    }

    private static Xp3EntryExtractionResult Failed(
        Xp3Entry entry,
        EvidenceStage stage,
        string code,
        string message,
        long bytesWritten = 0,
        uint? actualAdler32 = null,
        string? contentFilterId = null) =>
        new(entry.Name, stage, false, bytesWritten, entry.Adler32, actualAdler32, contentFilterId,
            [new KiriScopeDiagnostic(code, DiagnosticSeverity.Error, message)]);

    private sealed class BoundedReadStream(Stream inner, long length, bool leaveOpen) : Stream
    {
        private long _remaining = length;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_remaining == 0)
            {
                return 0;
            }

            var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
            _remaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining == 0)
            {
                return 0;
            }

            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken).ConfigureAwait(false);
            _remaining -= read;
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !leaveOpen)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
