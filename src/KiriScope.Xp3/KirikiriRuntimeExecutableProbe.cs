using System.Buffers.Binary;
using System.Text;

namespace KiriScope.Xp3;

/// <summary>
/// Reads only the small PE metadata subset needed to choose a KiriKiri executable that can load
/// the runtime-capture <c>version.dll</c> proxy. It never loads or executes the candidate.
/// </summary>
public sealed record KirikiriRuntimeExecutableProbe(
    string FullPath,
    long Length,
    bool IsX86,
    bool HasReadableImportDirectory,
    bool ImportsVersionDll,
    bool HasProtectedLauncherHint,
    IReadOnlyList<string> SectionNames)
{
    /// <summary>
    /// Higher values are safer candidates for the version.dll capture proxy. A direct import is
    /// essential; protected launcher section names only break ties against otherwise equivalent
    /// candidates, because they are a hint rather than a compatibility verdict.
    /// </summary>
    public int RuntimeCapturePriority =>
        (ImportsVersionDll ? 1_000 : 0) +
        (HasReadableImportDirectory ? 100 : 0) -
        (HasProtectedLauncherHint ? 250 : 0);

    /// <summary>Returns a bounded, read-only probe result, or <see langword="null"/> for an unreadable or malformed file.</summary>
    public static KirikiriRuntimeExecutableProbe? TryRead(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            var fullPath = Path.GetFullPath(path);
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length < 0x100)
            {
                return null;
            }

            Span<byte> dosHeader = stackalloc byte[64];
            if (!TryReadExactly(stream, 0, dosHeader) || dosHeader[0] != (byte)'M' || dosHeader[1] != (byte)'Z')
            {
                return null;
            }

            var peOffset = BinaryPrimitives.ReadInt32LittleEndian(dosHeader[60..]);
            if (peOffset < 64 || peOffset > 1_048_576 || !IsRangeWithin(stream.Length, peOffset, 24))
            {
                return null;
            }

            Span<byte> fileHeader = stackalloc byte[24];
            if (!TryReadExactly(stream, peOffset, fileHeader) ||
                fileHeader[0] != (byte)'P' || fileHeader[1] != (byte)'E' || fileHeader[2] != 0 || fileHeader[3] != 0)
            {
                return null;
            }

            var isX86 = BinaryPrimitives.ReadUInt16LittleEndian(fileHeader[4..]) == 0x014c;
            var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(fileHeader[6..]);
            var optionalHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(fileHeader[20..]);
            if (sectionCount == 0 || sectionCount > 96 || optionalHeaderSize < 112 || optionalHeaderSize > 4_096)
            {
                return null;
            }

            var optionalHeaderOffset = checked(peOffset + 24L);
            var sectionHeaderOffset = checked(optionalHeaderOffset + optionalHeaderSize);
            if (!IsRangeWithin(stream.Length, optionalHeaderOffset, optionalHeaderSize) ||
                !IsRangeWithin(stream.Length, sectionHeaderOffset, sectionCount * 40L))
            {
                return null;
            }

            var optionalHeader = new byte[optionalHeaderSize];
            if (!TryReadExactly(stream, optionalHeaderOffset, optionalHeader))
            {
                return null;
            }

            if (!isX86 || BinaryPrimitives.ReadUInt16LittleEndian(optionalHeader) != 0x010b)
            {
                return new KirikiriRuntimeExecutableProbe(fullPath, stream.Length, isX86, false, false, false, Array.Empty<string>());
            }

            var sections = ReadSections(stream, sectionHeaderOffset, sectionCount);
            if (sections is null)
            {
                return null;
            }

            var importDirectoryRva = BinaryPrimitives.ReadUInt32LittleEndian(optionalHeader.AsSpan(104, 4));
            var importDirectorySize = BinaryPrimitives.ReadUInt32LittleEndian(optionalHeader.AsSpan(108, 4));
            var imports = ReadImportedModules(stream, sections, importDirectoryRva, importDirectorySize);
            var sectionNames = sections.Select(static section => section.Name).ToArray();
            var protectedLauncherHint = sectionNames.Any(IsProtectedLauncherSection);
            return new KirikiriRuntimeExecutableProbe(
                fullPath,
                stream.Length,
                true,
                imports is not null,
                imports?.Contains("version.dll", StringComparer.OrdinalIgnoreCase) == true,
                protectedLauncherHint,
                sectionNames);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static IReadOnlyList<PeSection>? ReadSections(FileStream stream, long offset, int count)
    {
        var sections = new List<PeSection>(count);
        Span<byte> header = stackalloc byte[40];
        for (var index = 0; index < count; index++)
        {
            if (!TryReadExactly(stream, checked(offset + (index * 40L)), header))
            {
                return null;
            }

            var nameLength = header[..8].IndexOf((byte)0);
            if (nameLength < 0)
            {
                nameLength = 8;
            }

            var name = Encoding.ASCII.GetString(header[..nameLength]);
            sections.Add(new PeSection(
                name,
                BinaryPrimitives.ReadUInt32LittleEndian(header[8..]),
                BinaryPrimitives.ReadUInt32LittleEndian(header[12..]),
                BinaryPrimitives.ReadUInt32LittleEndian(header[16..]),
                BinaryPrimitives.ReadUInt32LittleEndian(header[20..])));
        }

        return sections;
    }

    private static HashSet<string>? ReadImportedModules(
        FileStream stream,
        IReadOnlyList<PeSection> sections,
        uint directoryRva,
        uint directorySize)
    {
        if (directoryRva == 0 || directorySize < 20 || !TryMapRva(stream.Length, sections, directoryRva, out var directoryOffset))
        {
            return null;
        }

        var descriptors = Math.Min(1_024, (int)(directorySize / 20));
        var modules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Span<byte> descriptor = stackalloc byte[20];
        Span<byte> emptyDescriptor = stackalloc byte[20];
        for (var index = 0; index < descriptors; index++)
        {
            if (!TryReadExactly(stream, checked(directoryOffset + (index * 20L)), descriptor))
            {
                return modules;
            }

            if (descriptor.SequenceEqual(emptyDescriptor))
            {
                break;
            }

            var nameRva = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[12..]);
            if (nameRva == 0 || !TryMapRva(stream.Length, sections, nameRva, out var nameOffset))
            {
                continue;
            }

            var moduleName = TryReadAsciiZeroTerminated(stream, nameOffset, 260);
            if (!string.IsNullOrWhiteSpace(moduleName))
            {
                modules.Add(moduleName);
            }
        }

        return modules;
    }

    private static bool TryMapRva(long fileLength, IReadOnlyList<PeSection> sections, uint rva, out long fileOffset)
    {
        foreach (var section in sections)
        {
            var span = Math.Max(section.VirtualSize, section.RawSize);
            if (span == 0 || rva < section.VirtualAddress || (ulong)rva - section.VirtualAddress >= span)
            {
                continue;
            }

            fileOffset = checked((long)section.RawOffset + ((long)rva - section.VirtualAddress));
            return IsRangeWithin(fileLength, fileOffset, 1);
        }

        fileOffset = 0;
        return false;
    }

    private static string? TryReadAsciiZeroTerminated(FileStream stream, long offset, int maximumLength)
    {
        if (!IsRangeWithin(stream.Length, offset, 1))
        {
            return null;
        }

        var length = (int)Math.Min(maximumLength, stream.Length - offset);
        var bytes = new byte[length];
        if (!TryReadExactly(stream, offset, bytes))
        {
            return null;
        }

        var terminator = Array.IndexOf(bytes, (byte)0);
        return terminator <= 0 ? null : Encoding.ASCII.GetString(bytes, 0, terminator);
    }

    private static bool IsProtectedLauncherSection(string sectionName) =>
        sectionName.StartsWith(".enigma", StringComparison.OrdinalIgnoreCase) ||
        sectionName.StartsWith(".vmp", StringComparison.OrdinalIgnoreCase) ||
        sectionName.Equals("UPX0", StringComparison.OrdinalIgnoreCase) ||
        sectionName.Equals("UPX1", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadExactly(FileStream stream, long offset, Span<byte> destination)
    {
        if (!IsRangeWithin(stream.Length, offset, destination.Length))
        {
            return false;
        }

        stream.Position = offset;
        return stream.Read(destination) == destination.Length;
    }

    private static bool IsRangeWithin(long length, long offset, long count) =>
        offset >= 0 && count >= 0 && offset <= length && count <= length - offset;

    private sealed record PeSection(string Name, uint VirtualSize, uint VirtualAddress, uint RawSize, uint RawOffset);
}
