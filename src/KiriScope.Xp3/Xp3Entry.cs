namespace KiriScope.Xp3;

public sealed record Xp3Entry(
    string Name,
    bool IsMarkedEncrypted,
    long UnpackedSize,
    long PackedSize,
    uint? Adler32,
    IReadOnlyList<Xp3Segment> Segments);
