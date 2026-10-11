using System.Text.RegularExpressions;
using MagicSim.Core.Forge;

namespace MagicSim.Core.Engine;

public enum CostPartKind { Sacrifice, Discard, PayLife, ExileFromGraveyard, TapUntapped, RemoveCounter, AddCounter, ExileSelf }

/// <summary>A non-mana cost component: "Sac&lt;1/CARDNAME&gt;", "Discard&lt;0/Hand&gt;", "PayLife&lt;2&gt;", …</summary>
public sealed record CostPart(CostPartKind Kind, string Amount, string Valid, string Raw);

/// <summary>An ability or spell cost.</summary>
public sealed class Cost
{
    public ManaCost Mana { get; init; } = ManaCost.Zero;
    public bool Tap { get; init; }
    public bool Untap { get; init; }
    public IReadOnlyList<CostPart> Parts { get; init; } = Array.Empty<CostPart>();
    public bool HasNonManaParts => Tap || Untap || Parts.Count > 0;

    public static readonly Cost Free = new();
}

public enum AbilityKind { Spell, Activated, Sub, TriggerEffect }

/// <summary>A compiled spell/activated/sub ability: one Forge effect plus its chain of sub-abilities.</summary>
public sealed class AbilityDef
{
    public required ForgeParams P { get; init; }
    public required string Api { get; init; }
    public AbilityKind Kind { get; init; }
    public Cost Cost { get; init; } = Cost.Free;
    public AbilityDef? Sub { get; set; }
    /// <summary>Targeting, if the ability has ValidTgts$.</summary>
    public Filter? TargetFilter { get; set; }
    public int TargetMin { get; set; }
    public int TargetMax { get; set; }
    /// <summary>Activated mana ability: resolves immediately, doesn't use the stack.</summary>
    public bool IsManaAbility { get; init; }
    /// <summary>Position in its chain (0 = the head) — index into StackItem.ChainTargets.</summary>
    public int ChainIndex { get; set; }
    public string Description => P["SpellDescription"] ?? P["StackDescription"] ?? Api;

    public IEnumerable<AbilityDef> Chain()
    {
        for (var a = this; a is not null; a = a.Sub) yield return a;
    }

    public string? this[string key] => P[key];
}

public sealed class TriggerDef
{
    public required ForgeParams P { get; init; }
    public required string Mode { get; init; }
    public AbilityDef? Execute { get; init; }
    public string? this[string key] => P[key];
}

public sealed class StaticDef
{
    public required ForgeParams P { get; init; }
    public required string Mode { get; init; }
    public string? this[string key] => P[key];
}

/// <summary>One face of a compiled card.</summary>
public sealed class FaceDef
{
    public required string Name { get; init; }
    public required ManaCost ManaCost { get; init; }
    public required TypeLine Types { get; init; }
    public int? Power { get; init; }
    public int? Toughness { get; init; }
    public ColorMask Colors { get; init; }
    public HashSet<string> KeywordNames { get; } = new(StringComparer.Ordinal);
    public List<string> RawKeywords { get; } = new();
    /// <summary>Spell abilities (instants/sorceries; usually one, more for "choose one" alt modes).</summary>
    public List<AbilityDef> SpellAbilities { get; } = new();
    public List<AbilityDef> Activated { get; } = new();
    public List<TriggerDef> Triggers { get; } = new();
    public List<StaticDef> Statics { get; } = new();
    public Dictionary<string, string> SVars { get; init; } = new();
    public string Oracle { get; init; } = "";
}

/// <summary>A card compiled from its Forge script. If <see cref="Missing"/> is non-empty the card is unsupported.</summary>
public sealed class CardDef
{
    public required string Name { get; init; }
    public required ForgeCard Source { get; init; }
    public List<FaceDef> Faces { get; } = new();
    public string? Layout => Source.AlternateMode;
    public SortedSet<string> Missing { get; } = new(StringComparer.Ordinal);
    public bool Supported => Missing.Count == 0;
    public FaceDef Front => Faces[0];

    /// <summary>A blank stand-in for an unsupported card: same name/types/cost, no abilities.</summary>
    public bool IsBlank { get; init; }
    /// <summary>From Scryfall's color_identity (set by CardPool).</summary>
    public ColorMask ColorIdentity { get; set; }
    public override string ToString() => Name;
}

/// <summary>Collects what a card needs that the engine can't do yet.</summary>
public sealed class CompileContext
{
    public required FaceDef Face { get; init; }
    public required SortedSet<string> Missing { get; init; }
    public void Need(string requirement) => Missing.Add(requirement);
}

public static partial class CardCompiler
{
    [GeneratedRegex(@"^([A-Za-z]+)<([^>]*)>$")]
    private static partial Regex CostPartRx();

    /// <summary>Parameters that only describe the ability (text, AI hints) and never change what it does.</summary>
    private static readonly HashSet<string> CosmeticParams = new(StringComparer.Ordinal)
    {
        "SP", "AB", "DB", "ST", "Cost", "SubAbility", "ValidTgts", "TgtPrompt", "TargetMin", "TargetMax",
        "SpellDescription", "StackDescription", "AILogic", "AIPreference", "AITgts", "AIManaPref", "AINoRecursiveCheck",
        "PrecostDesc", "CostDesc", "IsCurse", "AILifeThreshold", "AIMaxTgtsCount", "AITgtBeforeCostEval", "AIPowerThreshold",
        "SorcerySpeed", "ActivationPhases", "InstantSpeed", "PlayerTurn", "AnyPlayer", "TargetType", "MayChooseTarget",
        "Planeswalker", "Ultimate", "AIHintRequired", "AIActivateLast", "ShowCurrentCard", "AICheckSVar", "AISVarCompare",
    };

    public static CardDef Compile(ForgeCard source)
    {
        var def = new CardDef { Name = source.FullName, Source = source };
        foreach (var f in source.Faces)
            def.Faces.Add(CompileFace(f, def.Missing));
        // Two-faced transforming cards play their front face; other multi-face layouts need casting support first.
        if (source.AlternateMode is { } mode && mode != "DoubleFaced")
            def.Missing.Add("Layout:" + mode);
        return def;
    }

    /// <summary>A blank version of a card: kept for its name, types and cost; it never does anything.</summary>
    public static CardDef Blank(ForgeCard source)
    {
        var def = new CardDef { Name = source.FullName, Source = source, IsBlank = true };
        foreach (var f in source.Faces)
        {
            var types = TypeLine.Parse(f.Types);
            def.Faces.Add(new FaceDef
            {
                Name = f.Name, ManaCost = ManaCost.Parse(f.ManaCost) ?? ManaCost.None, Types = types,
                Power = ParsePT(f.PT, 0), Toughness = ParsePT(f.PT, 1), Oracle = f.Oracle,
            });
        }
        return def;
    }

    private static FaceDef CompileFace(ForgeFace f, SortedSet<string> missing)
    {
        var cost = ManaCost.Parse(f.ManaCost);
        if (cost is null) { missing.Add("ManaSymbol:" + f.ManaCost); cost = ManaCost.None; }
        var types = TypeLine.Parse(f.Types);
        var face = new FaceDef
        {
            Name = f.Name, ManaCost = cost, Types = types, Oracle = f.Oracle, SVars = f.SVars,
            Power = ParsePT(f.PT, 0), Toughness = ParsePT(f.PT, 1),
            Colors = ParseColors(f.Colors) ?? ColorsOf(cost),
        };
        if (f.PT is not null && (face.Power is null || face.Toughness is null)) missing.Add("PT:*");
        if (types.Is(CardType.Planeswalker)) missing.Add("Type:Planeswalker");
        if (types.Is(CardType.Battle)) missing.Add("Type:Battle");

        var ctx = new CompileContext { Face = face, Missing = missing };

        foreach (var k in f.Keywords)
        {
            var name = ForgeRequirements.KeywordName(k);
            face.RawKeywords.Add(k);
            face.KeywordNames.Add(name);
            if (!Keywords.Supported.Contains(name)) ctx.Need("Keyword:" + name);
        }

        foreach (var a in f.Abilities)
        {
            var ab = CompileAbility(a, ctx, a.AbilityKind == "SP" ? AbilityKind.Spell : AbilityKind.Activated, 0);
            if (ab is null) continue;
            if (ab.Kind == AbilityKind.Spell) face.SpellAbilities.Add(ab);
            else face.Activated.Add(ab);
        }

        // Basic land types grant "T: add one mana of that color" (rule 305.6).
        foreach (var (sub, color) in new[] { ("Plains", "W"), ("Island", "U"), ("Swamp", "B"), ("Mountain", "R"), ("Forest", "G") })
            if (types.Is(CardType.Land) && types.HasSubtype(sub))
                face.Activated.Add(new AbilityDef
                {
                    P = new ForgeParams($"AB$ Mana | Cost$ T | Produced$ {color}"), Api = "Mana",
                    Kind = AbilityKind.Activated, Cost = new Cost { Tap = true }, IsManaAbility = true,
                });

        foreach (var t in f.Triggers)
        {
            var mode = t["Mode"] ?? "?";
            if (!Triggers.Supported.Contains(mode)) ctx.Need("Trigger:" + mode);
            AbilityDef? exec = null;
            if (t["Execute"] is { } svar)
            {
                if (f.SVars.TryGetValue(svar, out var body)) exec = CompileAbility(new ForgeParams(body), ctx, AbilityKind.TriggerEffect, 0);
                else ctx.Need("SVarMissing:" + svar);
            }
            var td = new TriggerDef { P = t, Mode = mode, Execute = exec };
            Triggers.Validate(td, ctx);
            face.Triggers.Add(td);
        }

        foreach (var s in f.Statics)
        {
            var mode = s["Mode"] ?? "?";
            var sd = new StaticDef { P = s, Mode = mode };
            Statics.Validate(sd, ctx);
            face.Statics.Add(sd);
        }

        foreach (var r in f.Replacements) ctx.Need("Replacement:" + (r["Event"] ?? "?"));
        return face;
    }

    public static AbilityDef? CompileAbility(ForgeParams p, CompileContext ctx, AbilityKind kind, int chainIndex)
    {
        var api = p.Api;
        if (api is null) { ctx.Need("Ability:NoApi"); return null; }
        var cost = p["Cost"] is { } c ? ParseCost(c, ctx) : Cost.Free;
        var handler = Effects.Get(api);
        if (handler is null) ctx.Need("Api:" + api);

        var ab = new AbilityDef
        {
            P = p, Api = api, Kind = kind, Cost = cost, ChainIndex = chainIndex,
            IsManaAbility = api == "Mana" && kind == AbilityKind.Activated && p["ValidTgts"] is null,
        };

        if (p["ValidTgts"] is { } vt)
        {
            ab.TargetFilter = Filter.Parse(vt, ctx);
            ab.TargetMin = p["TargetMin"] is { } mn ? Amount.ParseConst(mn, ctx) : 1;
            ab.TargetMax = p["TargetMax"] is { } mx ? Amount.ParseConst(mx, ctx) : 1;
        }

        if (handler is not null)
        {
            foreach (var key in p.Values.Keys)
                if (!CosmeticParams.Contains(key) && !handler.Params.Contains(key))
                    ctx.Need($"Param:{api}.{key}");
            handler.Validate(ab, ctx);
            Conditions.Validate(ab, ctx);
        }

        if (p["SubAbility"] is { } subName)
        {
            if (ctx.Face.SVars.TryGetValue(subName, out var body))
                ab.Sub = CompileAbility(new ForgeParams(body), ctx, AbilityKind.Sub, chainIndex + 1);
            else ctx.Need("SVarMissing:" + subName);
        }
        return ab;
    }

    public static Cost ParseCost(string text, CompileContext ctx)
    {
        var manaTokens = new List<string>();
        bool tap = false, untap = false;
        var parts = new List<CostPart>();
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token == "T") { tap = true; continue; }
            if (token == "Q") { untap = true; ctx.Need("Cost:Untap"); continue; }
            var m = CostPartRx().Match(token);
            if (!m.Success) { manaTokens.Add(token); continue; }
            var args = m.Groups[2].Value.Split('/');
            var amount = args[0];
            var valid = args.Length > 1 ? args[1] : "";
            CostPartKind? kind = m.Groups[1].Value switch
            {
                "Sac" => CostPartKind.Sacrifice,
                "Discard" => CostPartKind.Discard,
                "PayLife" => CostPartKind.PayLife,
                "ExileFromGrave" => CostPartKind.ExileFromGraveyard,
                "tapXType" => CostPartKind.TapUntapped,
                "SubCounter" => CostPartKind.RemoveCounter,
                "AddCounter" => CostPartKind.AddCounter,
                "ExileFromHand" when valid == "CARDNAME" => CostPartKind.ExileSelf,
                _ => null,
            };
            if (kind is null) { ctx.Need("Cost:" + m.Groups[1].Value); continue; }
            if (kind is CostPartKind.Sacrifice or CostPartKind.Discard or CostPartKind.ExileFromGraveyard or CostPartKind.TapUntapped
                && valid is not ("CARDNAME" or "Hand" or "Random" or "Card" or ""))
                Filter.Parse(valid, ctx);
            parts.Add(new CostPart(kind.Value, amount, valid, token));
        }
        var mana = manaTokens.Count == 0 ? ManaCost.Zero : ManaCost.Parse(string.Join(' ', manaTokens));
        if (mana is null) { ctx.Need("CostSymbol:" + string.Join(' ', manaTokens)); mana = ManaCost.Zero; }
        return new Cost { Mana = mana, Tap = tap, Untap = untap, Parts = parts };
    }

    private static int? ParsePT(string? pt, int index)
    {
        if (pt is null) return null;
        var parts = pt.Split('/');
        return parts.Length == 2 && int.TryParse(parts[index], out var v) ? v : null;
    }

    private static ColorMask? ParseColors(string? colors)
    {
        if (colors is null) return null;
        ColorMask m = 0;
        foreach (var w in colors.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            if (ManaColors.FromWord(w) is { } c && c != ManaColor.C) m |= c.Mask();
        return m;
    }

    private static ColorMask ColorsOf(ManaCost cost)
    {
        ColorMask m = 0;
        foreach (var s in cost.Shards) m |= s.Colors & ColorMask.AllColors;
        return m;
    }
}
