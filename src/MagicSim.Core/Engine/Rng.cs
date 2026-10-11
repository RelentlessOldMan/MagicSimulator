namespace MagicSim.Core.Engine;

/// <summary>
/// xoshiro256** seeded by SplitMix64. Our own PRNG (not System.Random) so results stay
/// identical across .NET versions — CONTRACT.md §3 promises bit-for-bit determinism.
/// A value type so cloning a game state clones its random stream.
/// </summary>
public struct Rng
{
    private ulong _s0, _s1, _s2, _s3;

    public Rng(ulong seed)
    {
        var x = seed;
        _s0 = SplitMix(ref x); _s1 = SplitMix(ref x); _s2 = SplitMix(ref x); _s3 = SplitMix(ref x);
    }

    /// <summary>Stream for game <paramref name="game"/> of a run: depends only on (seed, game).</summary>
    public static Rng ForGame(ulong seed, long game, ulong salt = 0) =>
        new(seed * 0x9E3779B97F4A7C15UL ^ (ulong)game * 0xD1B54A32D192ED03UL ^ salt * 0x94D049BB133111EBUL);

    private static ulong SplitMix(ref ulong x)
    {
        var z = x += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public ulong NextULong()
    {
        var result = RotL(_s1 * 5, 7) * 9;
        var t = _s1 << 17;
        _s2 ^= _s0; _s3 ^= _s1; _s1 ^= _s2; _s0 ^= _s3;
        _s2 ^= t;
        _s3 = RotL(_s3, 45);
        return result;
    }

    /// <summary>Uniform in [0, n) without modulo bias.</summary>
    public int Next(int n)
    {
        if (n <= 0) throw new ArgumentOutOfRangeException(nameof(n));
        var bound = (ulong)n;
        var limit = ulong.MaxValue - ulong.MaxValue % bound;
        ulong r;
        do r = NextULong(); while (r >= limit);
        return (int)(r % bound);
    }

    public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

    /// <summary>Fisher–Yates.</summary>
    public void Shuffle<T>(IList<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private static ulong RotL(ulong x, int k) => (x << k) | (x >> (64 - k));
}
