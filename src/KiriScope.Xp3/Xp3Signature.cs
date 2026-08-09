namespace KiriScope.Xp3;

public static class Xp3Signature
{
    /// <summary>The 11-byte XP3 archive signature, expressed as raw bytes.</summary>
    public static ReadOnlySpan<byte> Bytes =>
    [
        0x58, 0x50, 0x33, 0x0D, 0x0A, 0x20,
        0x0A, 0x1A, 0x8B, 0x67, 0x01,
    ];
}
