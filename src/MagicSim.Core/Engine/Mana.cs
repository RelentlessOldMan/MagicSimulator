namespace MagicSim.Core.Engine;

/// <summary>Mana types, in pool index order. C is colorless.</summary>
public enum ManaColor { W = 0, U = 1, B = 2, R = 3, G = 4, C = 5 }

[Flags]
public enum ColorMask : byte
{
    None = 0, W = 1, U = 2, B = 4, R = 8, G = 16, C = 32,
    AllColors = W | U | B | R | G,
}

public static class ManaColors
{
    public static readonly ManaColor[] All = { ManaColor.W, ManaColor.U, ManaColor.B, ManaColor.R, ManaColor.G, ManaColor.C };
    public static readonly ManaColor[] Colored = { ManaColor.W, ManaColor.U, ManaColor.B, ManaColor.R, ManaColor.G };

    public static ColorMask Mask(this ManaColor c) => (ColorMask)(1 << (int)c);

    public static ManaColor? FromLetter(char ch) => char.ToUpperInvariant(ch) switch
    {
        'W' => ManaColor.W, 'U' => ManaColor.U, 'B' => ManaColor.B, 'R' => ManaColor.R, 'G' => ManaColor.G, 'C' => ManaColor.C,
        _ => null,
    };

    public static ManaColor? FromWord(string w) => w.ToLowerInvariant() switch
    {
        "white" => ManaColor.W, "blue" => ManaColor.U, "black" => ManaColor.B, "red" => ManaColor.R, "green" => ManaColor.G,
        "colorless" => ManaColor.C,
        _ => FromLetter(w.Length == 1 ? w[0] : '?'),
    };
}

/// <summary>One symbol of a mana cost that isn't plain generic mana.</summary>
public readonly record struct Shard(ColorMask Colors, bool Phyrexian = false, bool TwoGeneric = false)
{
    public bool IsSingle => Colors is ColorMask.W or ColorMask.U or ColorMask.B or ColorMask.R or ColorMask.G or ColorMask.C;
}

/// <summary>A mana cost: generic + colored/hybrid/phyrexian shards + X count.</summary>
public sealed class ManaCost
{
    public int Generic { get; init; }
    public int XCount { get; init; }
    public IReadOnlyList<Shard> Shards { get; init; } = Array.Empty<Shard>();
    /// <summary>"no cost" — can't be cast normally (back faces, some suspend cards, lands).</summary>
    public bool NoCost { get; init; }

    public static readonly ManaCost Zero = new();
    public static readonly ManaCost None = new() { NoCost = true };

    /// <summary>Mana value, with X = 0.</summary>
    public int ManaValue => Generic + Shards.Sum(s => s.TwoGeneric ? 2 : 1);

    public bool IsZero => !NoCost && Generic == 0 && XCount == 0 && Shards.Count == 0;

    /// <summary>Devotion: how many symbols of this color (hybrid counts for each of its colors).</summary>
    public int Devotion(ManaColor c) => Shards.Count(s => (s.Colors & c.Mask()) != 0 && c != ManaColor.C);

    /// <summary>
    /// Parses Forge's form: space-separated "2", "U", "X", hybrid "UR", twobrid "2R",
    /// phyrexian "BP", "no cost". Returns null for symbols the engine doesn't handle (e.g. "S" snow).
    /// </summary>
    public static ManaCost? Parse(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || text == "0") return Zero;
        if (text == "no cost") return None;
        int generic = 0, x = 0;
        var shards = new List<Shard>();
        foreach (var raw in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var tok = raw.Replace("/", "");
            if (int.TryParse(tok, out var n)) { generic += n; continue; }
            if (tok == "X") { x++; continue; }
            // "X can't be 0": the choice of X isn't held to it here.
            if (tok == "XMin1") continue;
            if (tok.Length == 1 && ManaColors.FromLetter(tok[0]) is { } c) { shards.Add(new Shard(c.Mask())); continue; }
            // Phyrexian: "BP", "PB", or hybrid phyrexian "RWP".
            if (tok.Contains('P'))
            {
                var colors = Mask(tok.Replace("P", ""));
                if (colors is null) return null;
                shards.Add(new Shard(colors.Value, Phyrexian: true));
                continue;
            }
            if (tok.Length == 2 && tok[0] == '2' && ManaColors.FromLetter(tok[1]) is { } tc)
            {
                shards.Add(new Shard(tc.Mask(), TwoGeneric: true));
                continue;
            }
            var hybrid = Mask(tok);
            if (hybrid is null) return null;
            shards.Add(new Shard(hybrid.Value));
        }
        return new ManaCost { Generic = generic, XCount = x, Shards = shards };
    }

    private static ColorMask? Mask(string letters)
    {
        if (letters.Length == 0) return null;
        ColorMask m = 0;
        foreach (var ch in letters)
        {
            if (ManaColors.FromLetter(ch) is not { } c) return null;
            m |= c.Mask();
        }
        return m;
    }

    /// <summary>Both costs together (an additional cost on top of a spell's cost).</summary>
    public ManaCost Plus(ManaCost other) => other.IsZero ? this : new ManaCost
    {
        Generic = Generic + other.Generic, XCount = XCount + other.XCount, Shards = Shards.Concat(other.Shards).ToList(), NoCost = NoCost,
    };

    public ManaCost WithExtraGeneric(int extra) => extra == 0 ? this : new ManaCost
    {
        Generic = Math.Max(0, Generic + extra), XCount = XCount, Shards = Shards, NoCost = NoCost,
    };

    public override string ToString()
    {
        if (NoCost) return "no cost";
        var parts = new List<string>();
        for (var i = 0; i < XCount; i++) parts.Add("{X}");
        if (Generic > 0 || (Shards.Count == 0 && XCount == 0)) parts.Add($"{{{Generic}}}");
        foreach (var s in Shards)
        {
            var letters = string.Concat(ManaColors.All.Where(c => (s.Colors & c.Mask()) != 0).Select(c => c.ToString()));
            parts.Add(s.TwoGeneric ? $"{{2/{letters}}}" : s.Phyrexian ? $"{{{letters}/P}}" : letters.Length > 1 ? $"{{{string.Join('/', letters.ToCharArray())}}}" : $"{{{letters}}}");
        }
        return string.Concat(parts);
    }
}

/// <summary>A mana pool: counts per <see cref="ManaColor"/>.</summary>
/// <summary>Mana in a pool that only some payments may use: <paramref name="Restriction"/> as in Forge's RestrictValid$.</summary>
public sealed record RestrictedMana(int SourceId, string Restriction, ManaColor Color, int Amount);

public struct ManaPool
{
    private int _w, _u, _b, _r, _g, _c;

    public int this[ManaColor c]
    {
        readonly get => c switch { ManaColor.W => _w, ManaColor.U => _u, ManaColor.B => _b, ManaColor.R => _r, ManaColor.G => _g, _ => _c };
        set
        {
            switch (c)
            {
                case ManaColor.W: _w = value; break;
                case ManaColor.U: _u = value; break;
                case ManaColor.B: _b = value; break;
                case ManaColor.R: _r = value; break;
                case ManaColor.G: _g = value; break;
                default: _c = value; break;
            }
        }
    }

    public readonly int Total => _w + _u + _b + _r + _g + _c;
    public void Add(ManaColor c, int n = 1) => this[c] += n;
    public void Clear() => _w = _u = _b = _r = _g = _c = 0;

    public override readonly string ToString()
    {
        if (Total == 0) return "empty";
        var sb = new System.Text.StringBuilder();
        foreach (var c in ManaColors.All)
            for (var i = 0; i < this[c]; i++) sb.Append('{').Append(c).Append('}');
        return sb.ToString();
    }
}

/// <summary>Pays a mana cost from a pool, choosing which mana covers hybrid and generic symbols.</summary>
public static class ManaPayment
{
    /// <summary>
    /// Tries to pay <paramref name="cost"/> (with X = <paramref name="x"/>) from <paramref name="pool"/>.
    /// Phyrexian symbols are paid with mana when possible, else with 2 life each (counted in
    /// <paramref name="lifePaid"/>; the caller checks the player can afford it).
    /// On success the pool is reduced. Search over hybrid choices is exhaustive (costs are tiny).
    /// </summary>
    public static bool TryPay(ref ManaPool pool, ManaCost cost, int x, out int lifePaid, bool allowLifeForPhyrexian = true)
    {
        lifePaid = 0;
        if (cost.NoCost) return false;
        var work = pool;
        // Single-color shards have no choice: pay them first. (Index loops: this runs for every cost the planner tries.)
        List<Shard>? flexible = null;
        var shards = cost.Shards;
        for (var i = 0; i < shards.Count; i++)
        {
            var s = shards[i];
            if (s.IsSingle && !s.Phyrexian && !s.TwoGeneric)
            {
                var c = ColorOf(s.Colors);
                if (work[c] == 0) return false;
                work[c]--;
            }
            else (flexible ??= new()).Add(s);
        }
        var generic = cost.Generic + cost.XCount * x;
        if (flexible is null ? !PayGeneric(ref work, generic) : !PayFlexible(ref work, flexible, 0, generic, ref lifePaid, allowLifeForPhyrexian)) return false;
        pool = work;
        return true;
    }

    internal static ManaColor ColorOf(ColorMask mask)
    {
        foreach (var k in ManaColors.All)
            if (k.Mask() == mask) return k;
        throw new InvalidOperationException("not a single color: " + mask);
    }

    private static bool PayFlexible(ref ManaPool pool, List<Shard> shards, int i, int generic, ref int life, bool allowLife)
    {
        if (i == shards.Count) return PayGeneric(ref pool, generic);
        var s = shards[i];
        foreach (var c in ManaColors.All)
        {
            if ((s.Colors & c.Mask()) == 0 || pool[c] == 0) continue;
            var copy = pool;
            copy[c]--;
            var l = life;
            if (PayFlexible(ref copy, shards, i + 1, generic, ref l, allowLife)) { pool = copy; life = l; return true; }
        }
        if (s.TwoGeneric)
        {
            var copy = pool;
            var l = life;
            if (PayFlexible(ref copy, shards, i + 1, generic + 2, ref l, allowLife)) { pool = copy; life = l; return true; }
        }
        if (s.Phyrexian && allowLife)
        {
            var copy = pool;
            var l = life + 2;
            if (PayFlexible(ref copy, shards, i + 1, generic, ref l, allowLife)) { pool = copy; life = l; return true; }
        }
        return false;
    }

    /// <summary>Generic mana: spend colorless first, then the most plentiful color.</summary>
    private static bool PayGeneric(ref ManaPool pool, int n)
    {
        if (pool.Total < n) return false;
        while (n > 0)
        {
            var best = ManaColor.C;
            if (pool[ManaColor.C] == 0)
            {
                best = ManaColor.W;
                foreach (var c in ManaColors.Colored)
                    if (pool[c] > pool[best]) best = c;
            }
            pool[best]--;
            n--;
        }
        return true;
    }
}
