namespace MagicSim.Core.Engine;

[Flags]
public enum CardType : ushort
{
    None = 0, Artifact = 1, Creature = 2, Enchantment = 4, Instant = 8, Land = 16,
    Planeswalker = 32, Sorcery = 64, Battle = 128, Kindred = 256,
}

[Flags]
public enum SuperType : byte { None = 0, Basic = 1, Legendary = 2, Snow = 4, World = 8 }

/// <summary>A parsed Forge "Types:" line, e.g. "Legendary Creature Kobold Warrior".</summary>
public sealed class TypeLine
{
    public CardType Types { get; init; }
    public SuperType Supers { get; init; }
    public IReadOnlyList<string> Subtypes { get; init; } = Array.Empty<string>();

    public bool Is(CardType t) => (Types & t) != 0;
    public bool Has(SuperType s) => (Supers & s) != 0;
    public bool HasSubtype(string s) => Subtypes.Contains(s, StringComparer.OrdinalIgnoreCase);
    public bool IsPermanent => (Types & (CardType.Artifact | CardType.Creature | CardType.Enchantment | CardType.Land | CardType.Planeswalker | CardType.Battle)) != 0;

    public static readonly string[] BasicLandTypes = { "Plains", "Island", "Swamp", "Mountain", "Forest" };

    public static TypeLine Parse(string text)
    {
        CardType types = 0;
        SuperType supers = 0;
        var subs = new List<string>();
        foreach (var w in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Enum.TryParse<CardType>(w, out var t) && t != CardType.None) types |= t;
            else if (w == "Tribal") types |= CardType.Kindred;
            else if (Enum.TryParse<SuperType>(w, out var s) && s != SuperType.None) supers |= s;
            else if (w is not "-" and not "—") subs.Add(w);
        }
        return new TypeLine { Types = types, Supers = supers, Subtypes = subs };
    }

    public override string ToString() =>
        string.Join(' ', new[] { Supers == 0 ? "" : Supers.ToString().Replace(",", ""), Types.ToString().Replace(",", "") }
            .Where(s => s.Length > 0).Concat(Subtypes));
}
