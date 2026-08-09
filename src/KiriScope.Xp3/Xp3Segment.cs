namespace KiriScope.Xp3;

public sealed record Xp3Segment(
    bool IsCompressed,
    long Offset,
    long UnpackedSize,
    long PackedSize);
