namespace KiriScope.Xp3;

internal sealed class Adler32Accumulator
{
    private const uint Modulus = 65521;
    private uint _s1 = 1;
    private uint _s2;

    public uint Value => (_s2 << 16) | _s1;

    public void Update(ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            _s1 = (_s1 + value) % Modulus;
            _s2 = (_s2 + _s1) % Modulus;
        }
    }
}
