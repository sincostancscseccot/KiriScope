using System.Buffers.Binary;
using System.IO.Compression;

namespace KiriScope.Resources;

/// <summary>Encodes top-down, 8-bit RGBA pixels as a standards-compliant PNG with filter type zero.</summary>
public static class PngRgbaEncoder
{
    private const uint Ihdr = 0x49484452;
    private const uint Idat = 0x49444154;
    private const uint Iend = 0x49454E44;

    public static byte[] Encode(RgbaImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width <= 0 || image.Height <= 0)
        {
            throw new ArgumentException("Image dimensions must be positive.", nameof(image));
        }

        var expectedPixels = checked((long)image.Width * image.Height * 4);
        if (image.Pixels.Length != expectedPixels)
        {
            throw new ArgumentException("RGBA pixel buffer length does not match image dimensions.", nameof(image));
        }

        var rawRows = new byte[checked((int)(expectedPixels + image.Height))];
        var sourceRowLength = image.Width * 4;
        for (var row = 0; row < image.Height; row++)
        {
            var destinationOffset = row * (sourceRowLength + 1);
            image.Pixels.AsSpan(row * sourceRowLength, sourceRowLength).CopyTo(rawRows.AsSpan(destinationOffset + 1));
        }

        byte[] compressed;
        using (var compressedStream = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressedStream, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                zlib.Write(rawRows);
            }

            compressed = compressedStream.ToArray();
        }

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, checked((uint)image.Width));
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], checked((uint)image.Height));
        header[8] = 8;
        header[9] = 6;
        using var output = new MemoryStream();
        output.Write(PngValidator.Signature);
        WriteChunk(output, Ihdr, header);
        WriteChunk(output, Idat, compressed);
        WriteChunk(output, Iend, []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, uint type, ReadOnlySpan<byte> data)
    {
        Span<byte> value = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(value, checked((uint)data.Length));
        output.Write(value);
        BinaryPrimitives.WriteUInt32BigEndian(value, type);
        output.Write(value);
        output.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(value, Crc32.Compute(type, data));
        output.Write(value);
    }

    private static class Crc32
    {
        private const uint Polynomial = 0xEDB88320;

        public static uint Compute(uint chunkType, ReadOnlySpan<byte> data)
        {
            var crc = 0xFFFFFFFFU;
            Span<byte> type = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32BigEndian(type, chunkType);
            crc = Update(crc, type);
            crc = Update(crc, data);
            return ~crc;
        }

        private static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var value in data)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ Polynomial : crc >> 1;
                }
            }

            return crc;
        }
    }
}
