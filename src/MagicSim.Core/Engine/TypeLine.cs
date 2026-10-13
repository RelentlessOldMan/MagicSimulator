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
    /// <summary>Changeling: every creature type (rule 702.73).</summary>
    public bool AllCreatureTypes { get; init; }

    public bool Is(CardType t) => (Types & t) != 0;
    public bool Has(SuperType s) => (Supers & s) != 0;
    public bool HasSubtype(string s) => Subtypes.Contains(s, StringComparer.OrdinalIgnoreCase) || AllCreatureTypes && IsCreatureType(s);
    public bool IsPermanent => (Types & (CardType.Artifact | CardType.Creature | CardType.Enchantment | CardType.Land | CardType.Planeswalker | CardType.Battle)) != 0;

    public static readonly string[] BasicLandTypes = { "Plains", "Island", "Swamp", "Mountain", "Forest" };

    /// <summary>Subtypes that belong to artifacts, enchantments and lands rather than creatures.</summary>
    private static readonly HashSet<string> NonCreatureSubtypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Plains", "Island", "Swamp", "Mountain", "Forest", "Gate", "Desert", "Lair", "Locus", "Mine", "Tower", "Urza's", "Cave",
        "Sphere", "Town", "Equipment", "Vehicle", "Fortification", "Aura", "Saga", "Class", "Shrine", "Cartouche", "Curse",
        "Room", "Case", "Background", "Role", "Rune", "Shard", "Food", "Treasure", "Clue", "Blood", "Gold", "Map",
        "Powerstone", "Incubator", "Junk", "Contraption", "Attraction", "Bobblehead", "Lesson", "Adventure", "Trap", "Arcane",
    };

    public static bool IsCreatureType(string subtype) => !NonCreatureSubtypes.Contains(subtype);

    private static readonly HashSet<string> LandSubtypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Plains", "Island", "Swamp", "Mountain", "Forest", "Gate", "Desert", "Lair", "Locus", "Mine", "Power-Plant", "Tower", "Urza's", "Cave",
        "Sphere", "Town",
    };

    public static bool IsLandType(string subtype) => LandSubtypes.Contains(subtype);

    public static TypeLine Parse(string text)
    {
        CardType types = 0;
        SuperType supers = 0;
        var subs = new List<string>();
        var all = false;
        foreach (var w in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (w == "AllCreatureTypes") { all = true; continue; }
            if (Enum.TryParse<CardType>(w, out var t) && t != CardType.None) types |= t;
            else if (w == "Tribal") types |= CardType.Kindred;
            else if (Enum.TryParse<SuperType>(w, out var s) && s != SuperType.None) supers |= s;
            else if (w is not "-" and not "—") subs.Add(w);
        }
        return new TypeLine { Types = types, Supers = supers, Subtypes = subs, AllCreatureTypes = all };
    }

    public override string ToString() =>
        string.Join(' ', new[] { Supers == 0 ? "" : Supers.ToString().Replace(",", ""), Types.ToString().Replace(",", "") }
            .Where(s => s.Length > 0).Concat(Subtypes).Concat(AllCreatureTypes ? new[] { "AllCreatureTypes" } : Array.Empty<string>()));
}
