namespace MagicSim.Core.Forge;

/// <summary>
/// A "Key$ Value | Key$ Value" parameter line from a Forge script: abilities (A:, SVar DB$),
/// triggers (T:), statics (S:) and replacements (R:) all use this shape.
/// </summary>
public sealed class ForgeParams
{
    public string Raw { get; }
    public IReadOnlyDictionary<string, string> Values => _values;
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public ForgeParams(string raw)
    {
        Raw = raw;
        foreach (var part in raw.Split('|'))
        {
            var p = part.Trim();
            var i = p.IndexOf('$');
            if (i <= 0) continue;
            var key = p[..i].Trim();
            // First occurrence wins; Forge never repeats a key on one line.
            _values.TryAdd(key, p[(i + 1)..].Trim());
        }
    }

    public string? this[string key] => _values.TryGetValue(key, out var v) ? v : null;
    public bool Has(string key) => _values.ContainsKey(key);

    /// <summary>For an ability: "SP", "AB", "DB" or "ST" — whichever key names the effect type.</summary>
    public string? AbilityKind => new[] { "SP", "AB", "DB", "ST" }.FirstOrDefault(Has);

    /// <summary>For an ability: the effect type (ApiType), e.g. "Mana", "ChangeZone".</summary>
    public string? Api => AbilityKind is { } k ? this[k] : null;
}

/// <summary>One face of a Forge card script (most cards have one; split/DFC/adventure have two).</summary>
public sealed class ForgeFace
{
    public string Name { get; set; } = "";
    public string ManaCost { get; set; } = "";
    public string Types { get; set; } = "";
    public string? PT { get; set; }
    public string? Loyalty { get; set; }
    public string? Defense { get; set; }
    public string? Colors { get; set; }
    public string Oracle { get; set; } = "";
    public List<ForgeParams> Abilities { get; } = new();
    public List<string> Keywords { get; } = new();
    public List<ForgeParams> Triggers { get; } = new();
    public List<ForgeParams> Statics { get; } = new();
    public List<ForgeParams> Replacements { get; } = new();
    public Dictionary<string, string> SVars { get; } = new(StringComparer.Ordinal);
    /// <summary>AI hints such as "RemoveDeck:All". Informational only.</summary>
    public List<string> AiHints { get; } = new();
}

/// <summary>A whole Forge card script file.</summary>
public sealed class ForgeCard
{
    public List<ForgeFace> Faces { get; } = new();
    /// <summary>Split, DoubleFaced, Modal, Adventure, Flip, … or null for a single-faced card.</summary>
    public string? AlternateMode { get; set; }
    public string File { get; set; } = "";

    public ForgeFace Front => Faces[0];

    /// <summary>Scryfall-style full name: "Front // Back" for split, DFC, MDFC and adventure cards.</summary>
    public string FullName => Faces.Count > 1 && AlternateMode is not null and not "Flip" and not "Meld"
        ? string.Join(" // ", Faces.Select(f => f.Name))
        : Front.Name;

    public static ForgeCard Parse(string text, string file = "")
    {
        var card = new ForgeCard { File = file };
        var face = new ForgeFace();
        card.Faces.Add(face);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim() == "ALTERNATE")
            {
                face = new ForgeFace();
                card.Faces.Add(face);
                continue;
            }
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon];
            var value = line[(colon + 1)..];
            switch (key)
            {
                case "Name": face.Name = value.Trim(); break;
                case "ManaCost": face.ManaCost = value.Trim(); break;
                case "Types": face.Types = value.Trim(); break;
                case "PT": face.PT = value.Trim(); break;
                case "Loyalty": face.Loyalty = value.Trim(); break;
                case "Defense": face.Defense = value.Trim(); break;
                case "Colors": face.Colors = value.Trim(); break;
                case "Oracle": face.Oracle = value.Replace("\\n", "\n"); break;
                case "A": face.Abilities.Add(new ForgeParams(value)); break;
                case "K": face.Keywords.Add(value.Trim()); break;
                case "T": face.Triggers.Add(new ForgeParams(value)); break;
                case "S": face.Statics.Add(new ForgeParams(value)); break;
                case "R": face.Replacements.Add(new ForgeParams(value)); break;
                case "AI": face.AiHints.Add(value.Trim()); break;
                case "AlternateMode": card.AlternateMode = value.Trim(); break;
                case "SVar":
                    var c2 = value.IndexOf(':');
                    if (c2 > 0) face.SVars[value[..c2]] = value[(c2 + 1)..];
                    break;
                // DeckHas/DeckHints/DeckNeeds/Draft/Text/etc. are deck-building hints; ignored.
            }
        }
        // Drop an empty trailing face from a stray separator.
        if (card.Faces.Count > 1 && card.Faces[^1].Name.Length == 0) card.Faces.RemoveAt(card.Faces.Count - 1);
        return card;
    }
}
