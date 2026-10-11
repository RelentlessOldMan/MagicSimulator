using System.Text.RegularExpressions;

namespace MagicSim.Core.Forge;

/// <summary>
/// Lists everything the engine must implement to run a card: effect types ("Api:Mana"),
/// trigger modes ("Trigger:ChangesZone"), static modes, replacement events, keywords,
/// non-mana cost parts ("Cost:Sac") and Count$ expressions ("Count:Devotion").
/// A card is scripted-supported when every requirement is supported (CONTRACT.md §6).
/// </summary>
public static partial class ForgeRequirements
{
    [GeneratedRegex(@"^([A-Za-z]+)<")]
    private static partial Regex CostPartRx();

    [GeneratedRegex(@"Count\$([A-Za-z]+)")]
    private static partial Regex CountRx();

    public static SortedSet<string> For(ForgeCard card)
    {
        var req = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var f in card.Faces) AddFace(f, req);
        return req;
    }

    private static void AddFace(ForgeFace f, SortedSet<string> req)
    {
        foreach (var a in f.Abilities) AddAbility(a, req);
        foreach (var t in f.Triggers) req.Add("Trigger:" + (t["Mode"] ?? "?"));
        foreach (var s in f.Statics) req.Add("Static:" + (s["Mode"] ?? "?"));
        foreach (var r in f.Replacements) req.Add("Replacement:" + (r["Event"] ?? "?"));
        foreach (var k in f.Keywords) req.Add("Keyword:" + KeywordName(k));

        foreach (var (_, value) in f.SVars)
        {
            // SVars hold sub-abilities (DB$ …), Count$ expressions, and AI/deck hints.
            var p = new ForgeParams(value);
            if (p.Api is not null) AddAbility(p, req);
            foreach (Match m in CountRx().Matches(value)) req.Add("Count:" + m.Groups[1].Value);
        }
    }

    private static void AddAbility(ForgeParams a, SortedSet<string> req)
    {
        req.Add("Api:" + a.Api);
        if (a["Cost"] is { } cost) AddCost(cost, req);
    }

    public static void AddCost(string cost, ICollection<string> req)
    {
        foreach (var token in cost.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var m = CostPartRx().Match(token);
            if (m.Success) req.Add("Cost:" + m.Groups[1].Value);
            else if (token is "T") req.Add("Cost:Tap");
            else if (token is "Q") req.Add("Cost:Untap");
            // Plain mana symbols (1, B, X, W/U, …) are handled by the mana system itself.
        }
    }

    /// <summary>"Escape:CardManaCost …" → "Escape"; "Protection from red" → "Protection".</summary>
    public static string KeywordName(string k)
    {
        var name = k.Split(':')[0].Trim();
        foreach (var prefix in new[] { "Protection", "Enchant", "Hexproof", "Landwalk", "Ward", "Equip", "Cycling", "Kicker", "Affinity" })
            if (name.StartsWith(prefix, StringComparison.Ordinal)) return prefix;
        if (name.EndsWith("walk", StringComparison.Ordinal)) return "Landwalk";
        if (name.EndsWith("cycling", StringComparison.OrdinalIgnoreCase)) return "TypeCycling";
        return name;
    }
}
