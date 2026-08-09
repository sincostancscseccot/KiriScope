namespace KiriScope.Resources;

public static class ResourceFormatDetector
{
    public static ResourceFormat Detect(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith(PngValidator.Signature))
        {
            return ResourceFormat.Png;
        }

        if (header.StartsWith("TLG"u8))
        {
            return ResourceFormat.Tlg;
        }

        if (header.StartsWith("PSB\0"u8))
        {
            return ResourceFormat.Psb;
        }

        if (header.StartsWith("PIMG"u8))
        {
            return ResourceFormat.Pimg;
        }

        if (header.StartsWith("OggS"u8))
        {
            return ResourceFormat.Ogg;
        }

        if (header.StartsWith("RIFF"u8) && header.Length >= 12 && header.Slice(8).StartsWith("WAVE"u8))
        {
            return ResourceFormat.Wave;
        }

        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return ResourceFormat.Jpeg;
        }

        if (header.StartsWith("BM"u8))
        {
            return ResourceFormat.Bmp;
        }

        return ResourceFormat.Unknown;
    }
}
