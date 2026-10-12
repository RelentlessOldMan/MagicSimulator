using System.Text.RegularExpressions;
using MagicSim.Core.Forge;

namespace MagicSim.Core.Engine;

public enum CostPartKind { Sacrifice, Discard, PayLife, ExileFromGraveyard, TapUntapped, RemoveCounter, AddCounter, ExileSelf, ExileFromHand, ReturnToHand, Reveal, TapPower, ExileSource }

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

    /// <summary>This cost and another together (kicker, splice, escape's "plus exile …").</summary>
    public Cost Plus(Cost other) => new()
    {
        Mana = Mana.Plus(other.Mana), Tap = Tap || other.Tap, Untap = Untap || other.Untap, Parts = Parts.Concat(other.Parts).ToList(),
    };

    public static readonly Cost Free = new();
}

public enum AbilityKind { Spell, Activated, Sub, TriggerEffect }

/// <summary>A compiled spell/activated/sub ability: one Forge effect plus its chain of sub-abilities.</summary>
public sealed class AbilityDef
{
    public required ForgeParams P { get; init; }
    public required string Api { get; init; }
    /// <summary>The face whose SVars this ability's expressions use: the card that defines it, even when granted to another.</summary>
    public FaceDef? Face { get; init; }
    public AbilityKind Kind { get; init; }
    public Cost Cost { get; init; } = Cost.Free;
    public AbilityDef? Sub { get; set; }
    /// <summary>Targeting, if the ability has ValidTgts$.</summary>
    public Filter? TargetFilter { get; set; }
    public int TargetMin { get; set; }
    public int TargetMax { get; set; }
    /// <summary>"TargetMax$ X": a number of targets worked out when the ability is put on the stack ("OneEach" = one per player).</summary>
    public string? TargetMinExpr { get; set; }
    public string? TargetMaxExpr { get; set; }
    /// <summary>Cast for its evoke cost: sacrificed when it enters (rule 702.74).</summary>
    public bool Evoke { get; set; }
    /// <summary>A cycling ability: using it is cycling ("whenever you cycle", rule 702.29).</summary>
    public bool IsCycling { get; set; }
    /// <summary>Activated mana ability: resolves immediately, doesn't use the stack.</summary>
    public bool IsManaAbility { get; init; }
    /// <summary>Position in its chain (0 = the head) — index into StackItem.ChainTargets.</summary>
    public int ChainIndex { get; set; }
    /// <summary>"UnlessCost$": the effect doesn't happen if the payer pays this.</summary>
    public Cost? UnlessCost { get; set; }
    /// <summary>The effect object an "Effect" ability creates, compiled with the card.</summary>
    public CardDef? EffectDef { get; set; }
    /// <summary>Token definitions this ability creates (TokenScript$), compiled with the card.</summary>
    public List<CardDef>? Tokens { get; set; }
    /// <summary>For one combination of a modal ability: the chosen modes' text.</summary>
    public string? ModeDescription { get; set; }
    /// <summary>Chains an effect runs by name (FlipCoin's WinSubAbility$, Repeat's RepeatSubAbility$).</summary>
    public Dictionary<string, AbilityDef>? SideChains { get; set; }
    /// <summary>A spell cast for an alternative cost ("rather than pay this spell's mana cost").</summary>
    public Cost? AltCost { get; set; }
    /// <summary>The static granting <see cref="AltCost"/>; its conditions must hold to use it.</summary>
    public StaticDef? AltCostOf { get; set; }
    /// <summary>Abilities this effect gives (Animate's Abilities$/Triggers$/staticAbilities$).</summary>
    public Grants? Grants { get; set; }
    /// <summary>The trigger a DelayedTrigger effect sets up.</summary>
    public TriggerDef? Delayed { get; set; }
    /// <summary>A spell cast paying optional additional costs (kicker, bargain): what it adds, and their names.</summary>
    public Cost? ExtraCost { get; set; }
    public string[]? OptionalCosts { get; set; }
    /// <summary>For one combination of a modal ability whose number of modes is an expression: the expression, and how many modes this is.</summary>
    public string? CharmNumExpr { get; set; }
    public int CharmCount { get; set; }
    /// <summary>Cast face down as a 2/2 creature (morph, rule 702.37).</summary>
    public bool CastFaceDown { get; set; }
    /// <summary>A special action (turning a face-down permanent face up): no stack, any time you have priority (rule 116.2b).</summary>
    public bool IsSpecialAction { get; set; }

    public AbilityDef CloneHead() => (AbilityDef)MemberwiseClone();
    public string Description => ModeDescription ?? P["SpellDescription"] ?? P["StackDescription"] ?? Api;

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
    /// <summary>For a modal ("choose one") trigger: every allowed combination of modes.</summary>
    public IReadOnlyList<AbilityDef>? Modes { get; init; }
    /// <summary>The face whose SVars this trigger uses.</summary>
    public FaceDef? Face { get; init; }
    public string? this[string key] => P[key];
}

public sealed class StaticDef
{
    public required ForgeParams P { get; init; }
    public required string Mode { get; init; }
    /// <summary>The face whose SVars this static uses.</summary>
    public FaceDef? Face { get; init; }
    /// <summary>Abilities a Continuous static gives (AddAbility$/AddTrigger$).</summary>
    public Grants? Grants { get; set; }
    public string? this[string key] => P[key];
}

/// <summary>One face of a compiled card.</summary>
public sealed class FaceDef
{
    public required string Name { get; init; }
    public required ManaCost ManaCost { get; init; }
    public required TypeLine Types { get; set; }
    public int? Power { get; init; }
    public int? Toughness { get; init; }
    /// <summary>A planeswalker's printed loyalty: it enters with that many loyalty counters (rule 306.5b).</summary>
    public int? Loyalty { get; init; }
    public ColorMask Colors { get; set; }
    public HashSet<string> KeywordNames { get; } = new(StringComparer.Ordinal);
    public List<string> RawKeywords { get; } = new();
    /// <summary>Spell abilities (instants/sorceries; usually one, more for "choose one" alt modes).</summary>
    public List<AbilityDef> SpellAbilities { get; } = new();
    public List<AbilityDef> Activated { get; } = new();
    public List<TriggerDef> Triggers { get; } = new();
    public List<StaticDef> Statics { get; } = new();
    /// <summary>An Aura's spell: targets what it will enchant (rule 303.4a).</summary>
    public AbilityDef? AuraSpell { get; set; }
    /// <summary>"As this enters" replacements (enters tapped, shocklands, checklands).</summary>
    public List<AbilityDef> EtbReplacements { get; } = new();
    /// <summary>"Doesn't untap during your untap step."</summary>
    public bool DoesntUntap { get; set; }
    /// <summary>"Enters with N counters": (counter type, amount expression, condition such as "if it was kicked").</summary>
    public List<(string Kind, string Amount, ForgeParams? Condition)> EtbCounters { get; } = new();
    /// <summary>Flashback cost, if it has flashback.</summary>
    public Cost? Flashback { get; set; }
    public Dictionary<string, string> SVars { get; init; } = new();
    public string Oracle { get; init; } = "";
    /// <summary>"You may have this enter as a copy of …" (K:ETBReplacement:Copy).</summary>
    public AbilityDef? EtbCopy { get; set; }
    /// <summary>A Saga's chapter abilities, chapter I first (rule 714).</summary>
    public List<TriggerDef> Chapters { get; } = new();
    /// <summary>Replacement effects that modify events (damage, tokens, counters, zone changes).</summary>
    public List<ReplacementDef> Replacements { get; } = new();
    /// <summary>Evoke cost: added as an alternative-cost cast once the spell abilities are known.</summary>
    public Cost? EvokeCost { get; set; }
    /// <summary>"If this is in your opening hand, you may …" (Leylines).</summary>
    public AbilityDef? OpeningHand { get; set; }
    /// <summary>A Class's levels 2+: what each level gives (rule 716).</summary>
    public List<(int Level, Grants Grants)> ClassLevels { get; } = new();
    /// <summary>Optional additional costs (kicker, bargain): each becomes a twin of every spell ability.</summary>
    public List<(string[] Names, Cost Cost)> OptionalCosts { get; } = new();
    /// <summary>Its own escape cost, in Forge's form ("CardManaCost" = its mana cost).</summary>
    public string? EscapeCost { get; set; }
    /// <summary>"Splice onto [subtype] [cost]" (rule 702.47).</summary>
    public (string Onto, Cost Cost)? Splice { get; set; }
    /// <summary>Morph / megamorph: the cost to turn it face up, and whether it gets a +1/+1 counter then.</summary>
    public (string Cost, bool Mega)? Morph { get; set; }
    /// <summary>What it is while face down: a nameless colorless 2/2 creature that can be turned face up.</summary>
    public FaceDef? FaceDownFace { get; set; }
    /// <summary>Rebound: cast from hand, it's exiled and this triggers at your next upkeep (rule 702.88).</summary>
    public TriggerDef? Rebound { get; set; }

    /// <summary>This face as a copy effect changes it ("except it's an artifact", "except it has haste").</summary>
    public FaceDef CopyWith(TypeLine types, int? power, int? toughness, ColorMask colors, IReadOnlyList<string> extraKeywords)
    {
        var f = new FaceDef
        {
            Name = Name, ManaCost = ManaCost, Types = types, Power = power, Toughness = toughness, Colors = colors, Loyalty = Loyalty,
            AuraSpell = AuraSpell, DoesntUntap = DoesntUntap, Flashback = Flashback, SVars = SVars, Oracle = Oracle, EtbCopy = EtbCopy,
            EscapeCost = EscapeCost, Splice = Splice,
        };
        f.KeywordNames.UnionWith(KeywordNames);
        f.RawKeywords.AddRange(RawKeywords);
        foreach (var k in extraKeywords)
        {
            f.RawKeywords.Add(k);
            f.KeywordNames.Add(ForgeRequirements.KeywordName(k));
        }
        f.SpellAbilities.AddRange(SpellAbilities);
        f.Activated.AddRange(Activated);
        f.Triggers.AddRange(Triggers);
        f.Statics.AddRange(Statics);
        f.EtbReplacements.AddRange(EtbReplacements);
        f.EtbCounters.AddRange(EtbCounters);
        f.Chapters.AddRange(Chapters);
        f.Replacements.AddRange(Replacements);
        f.ClassLevels.AddRange(ClassLevels);
        return f;
    }
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
    /// <summary>Compiles a token script by name (TokenScript$); null when no token library is available.</summary>
    public Func<string, CardDef?>? Tokens { get; init; }
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
        "ValidDescription", "ValidCardsDesc", "ValidTgtsDesc", "ChoiceTitle", "AIRespondsToOwnAbility", "TgtPromptInfo",
        "AIPhyrexianPayment", "NoLooking", "UnlessAI", "ValidMessage",
    };

    /// <summary>Parameters any ability may have, handled by the engine rather than the effect.</summary>
    private static readonly HashSet<string> GenericParams = new(StringComparer.Ordinal)
    {
        "UnlessCost", "UnlessPayer", "UnlessResolveSubs", "ETB", "ActivationZone", "OptionalDecider", "Activation", "ActivationGameTypes", "ReduceCost",
    };

    public static CardDef Compile(ForgeCard source, Func<string, CardDef?>? tokens = null)
    {
        var def = new CardDef { Name = source.FullName, Source = source };
        foreach (var f in source.Faces)
            def.Faces.Add(CompileFace(f, def.Missing, tokens));
        // Two-faced transforming cards play their front face; other multi-face layouts need casting support first.
        if (source.AlternateMode is { } mode && mode is not ("DoubleFaced" or "Modal" or "Adventure"))
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

    private static FaceDef CompileFace(ForgeFace f, SortedSet<string> missing, Func<string, CardDef?>? tokens)
    {
        var cost = ManaCost.Parse(f.ManaCost);
        if (cost is null) { missing.Add("ManaSymbol:" + f.ManaCost); cost = ManaCost.None; }
        var types = TypeLine.Parse(f.Types);
        var face = new FaceDef
        {
            Name = f.Name, ManaCost = cost, Types = types, Oracle = f.Oracle, SVars = f.SVars,
            Power = ParsePT(f.PT, 0), Toughness = ParsePT(f.PT, 1),
            Colors = ParseColors(f.Colors) ?? ColorsOf(cost),
            Loyalty = int.TryParse(f.Loyalty, out var loyalty) ? loyalty : null,
        };
        var starPT = f.PT is not null && (face.Power is null || face.Toughness is null);
        if (types.Is(CardType.Planeswalker) && face.Loyalty is null) missing.Add("Loyalty:" + f.Loyalty);
        if (types.Is(CardType.Battle)) missing.Add("Type:Battle");

        var ctx = new CompileContext { Face = face, Missing = missing, Tokens = tokens };

        foreach (var k in f.Keywords)
        {
            var name = ForgeRequirements.KeywordName(k);
            face.RawKeywords.Add(k);
            face.KeywordNames.Add(name);
            if (!CompileKeyword(k, name, face, ctx) && !Keywords.Supported.Contains(name)) ctx.Need("Keyword:" + name);
        }

        foreach (var a in f.Abilities)
        {
            var ab = CompileAbility(a, ctx, a.AbilityKind == "SP" ? AbilityKind.Spell : AbilityKind.Activated, 0);
            if (ab is null) continue;
            foreach (var mode in ExpandModes(ab, ctx))
                if (mode.Kind == AbilityKind.Spell) face.SpellAbilities.Add(mode);
                else face.Activated.Add(mode);
        }

        // Basic land types grant "T: add one mana of that color" (rule 305.6).
        foreach (var (sub, mana) in BasicLandMana)
            if (types.Is(CardType.Land) && types.Subtypes.Contains(sub)) face.Activated.Add(mana);

        foreach (var t in f.Triggers) face.Triggers.Add(CompileTrigger(t, ctx));
        foreach (var s in f.Statics) face.Statics.Add(CompileStatic(s, ctx));

        foreach (var st in face.Statics.Where(st => st.Mode == "AlternativeCost").ToList()) AddAlternativeCost(face, st, ctx);
        if (face.EvokeCost is { } evoke)
            foreach (var ab in face.SpellAbilities.Where(a => a.Api == "PermanentSpell" && a.AltCost is null).ToList())
            {
                var alt = ab.CloneHead();
                alt.AltCost = evoke;
                alt.Evoke = true;
                alt.ModeDescription = "Evoke";
                face.SpellAbilities.Add(alt);
            }
        foreach (var (names, extra) in face.OptionalCosts) AddOptionalCost(face, names, extra, ctx);
        if (face.Morph is { } morph) AddMorph(face, morph.Cost, morph.Mega, ctx);
        foreach (var r in f.Replacements) CompileReplacement(r, face, ctx);
        // "*" power/toughness needs a characteristic-defining ability to say what it is.
        if (starPT && !face.Statics.Any(st => st.Mode == "Continuous" && st["CharacteristicDefining"] == "True")) ctx.Need("PT:*");
        if (face.KeywordNames.Contains("Devoid")) face.Colors = 0;
        return face;
    }

    /// <summary>The mana ability each basic land type stands for (rule 305.6).</summary>
    public static readonly IReadOnlyDictionary<string, AbilityDef> BasicLandMana =
        new[] { ("Plains", "W"), ("Island", "U"), ("Swamp", "B"), ("Mountain", "R"), ("Forest", "G") }.ToDictionary(p => p.Item1, p => new AbilityDef
        {
            P = new ForgeParams($"AB$ Mana | Cost$ T | Produced$ {p.Item2}"), Api = "Mana",
            Kind = AbilityKind.Activated, Cost = new Cost { Tap = true }, IsManaAbility = true,
        });

    public static TriggerDef CompileTrigger(ForgeParams t, CompileContext ctx)
    {
        var mode = t["Mode"] ?? "?";
        if (!Triggers.Supported.Contains(mode)) ctx.Need("Trigger:" + mode);
        AbilityDef? exec = null;
        if (t["Execute"] is { } svar)
        {
            if (ctx.Face.SVars.TryGetValue(svar, out var body)) exec = CompileAbility(new ForgeParams(body), ctx, AbilityKind.TriggerEffect, 0);
            else ctx.Need("SVarMissing:" + svar);
        }
        var modes = exec is null ? null : ExpandModes(exec, ctx);
        var td = new TriggerDef { P = t, Mode = mode, Execute = modes?.FirstOrDefault(), Modes = modes, Face = ctx.Face };
        Triggers.Validate(td, ctx);
        return td;
    }

    public static StaticDef CompileStatic(ForgeParams s, CompileContext ctx)
    {
        var sd = new StaticDef { P = s, Mode = s["Mode"] ?? "?", Face = ctx.Face };
        Statics.Validate(sd, ctx);
        return sd;
    }

    /// <summary>Keywords that stand for abilities (Equip, Cycling, …): adds those abilities. False if not one of these.</summary>
    private static bool CompileKeyword(string raw, string name, FaceDef face, CompileContext ctx)
    {
        var parts = raw.Split(':');
        string Part(int i) => i < parts.Length ? parts[i].Trim() : "";
        switch (name)
        {
            case "Equip":
                if (Part(4).Length > 0) { ctx.Need("Keyword:Equip+" + Part(4).Split('$')[0]); return true; }
                var valid = Part(2).Length > 0 ? Part(2) : "Creature.YouCtrl";
                AddGenerated(face, ctx, $"AB$ Attach | Cost$ {Part(1)} | ValidTgts$ {valid} | SorcerySpeed$ True | SpellDescription$ Equip {Part(1)}");
                return true;
            case "Enchant":
                // "Enchant creature card in a graveyard" (Animate Dead) targets there.
                face.AuraSpell = new AbilityDef
                {
                    P = new ForgeParams($"SP$ PermanentSpell | ValidTgts$ {Part(1)}" + (Part(1).Contains("inZoneGraveyard", StringComparison.Ordinal) ? " | TgtZone$ Graveyard" : "")),
                    Api = "PermanentSpell", Kind = AbilityKind.Spell,
                    TargetFilter = Filter.Parse(Part(1), ctx), TargetMin = 1, TargetMax = 1,
                };
                return true;
            case "Cycling":
                AddGenerated(face, ctx, $"AB$ Draw | Cost$ {Part(1)} Discard<1/CARDNAME> | ActivationZone$ Hand | Defined$ You | NumCards$ 1 | SpellDescription$ Cycling");
                face.Activated[^1].IsCycling = true;
                return true;
            case "etbCounter":
            {
                if (Part(1).StartsWith("EACH", StringComparison.Ordinal)) { ctx.Need("Keyword:etbCounter+EACH"); return true; }
                // "If it was kicked, it enters with …": K:etbCounter:P1P1:2:CheckSVar$ WasKicked:description
                ForgeParams? condition = null;
                if (Part(3).Length > 0 && Part(3) != "no Condition")
                {
                    condition = new ForgeParams(Part(3));
                    if (condition.Values.Keys.Any(k => !Presence.Params.Contains(k) && k != "ValidCard")) { ctx.Need("Keyword:etbCounter+" + Part(3).Split('$')[0]); return true; }
                    Presence.Validate(condition, ctx);
                    // "Escapes with …": ValidCard$ Card.Self+escaped
                    if (condition["ValidCard"] is { } ecv) Filter.Parse(ecv, ctx);
                }
                Amount.Parse(Part(2), ctx);
                face.EtbCounters.Add((Part(1), Part(2), condition));
                return true;
            }
            case "Flashback":
                face.Flashback = ParseCost(Part(1), ctx);
                return true;
            case "Chapter":
                // K:Chapter:3:DBFirst,DBSecond,DBThird — one ability per chapter.
                var chapters = Part(2).Split(',', StringSplitOptions.TrimEntries);
                if (!int.TryParse(Part(1), out var count) || count != chapters.Length) { ctx.Need("Keyword:Chapter+" + Part(1)); return true; }
                foreach (var ch in chapters)
                {
                    if (!face.SVars.TryGetValue(ch, out var chBody)) { ctx.Need("SVarMissing:" + ch); return true; }
                    var exec = CompileAbility(new ForgeParams(chBody), ctx, AbilityKind.TriggerEffect, 0);
                    var modes = exec is null ? null : ExpandModes(exec, ctx);
                    face.Chapters.Add(new TriggerDef { P = new ForgeParams("Mode$ Chapter"), Mode = "Chapter", Execute = modes?.FirstOrDefault(), Modes = modes, Face = face });
                }
                return true;
            case "Crew":
                // "Crew N: tap creatures with total power N or more: this becomes an artifact creature until end of turn."
                AddGenerated(face, ctx, $"AB$ Animate | Cost$ Crew<{Part(1)}> | Defined$ Self | Types$ Artifact,Creature | SpellDescription$ Crew {Part(1)}");
                return true;
            case "Prowess":
                // "Whenever you cast a noncreature spell, this creature gets +1/+1 until end of turn."
                face.SVars["__ProwessPump"] = "DB$ Pump | Defined$ Self | NumAtt$ +1 | NumDef$ +1";
                face.Triggers.Add(CompileTrigger(new ForgeParams(
                    "Mode$ SpellCast | ValidCard$ Card.nonCreature | ValidActivatingPlayer$ You | TriggerZones$ Battlefield | Execute$ __ProwessPump"), ctx));
                return true;
            case "Toxic":
            case "Affinity":
                if (name == "Toxic" && !int.TryParse(Part(1), out _)) ctx.Need("Keyword:Toxic+" + Part(1));
                if (name == "Affinity") Filter.Parse(Part(1), ctx);
                return true;
            case "Class":
            {
                // K:Class:Level:Cost:AddTrigger$ … | AddStaticAbility$ … — gaining a level is a sorcery-speed activated ability.
                if (!int.TryParse(Part(1), out var level)) { ctx.Need("Keyword:Class+" + Part(1)); return true; }
                var grantText = string.Join(':', parts.Skip(3));
                var gp = new ForgeParams(grantText);
                foreach (var key in gp.Values.Keys)
                    if (key is not ("AddTrigger" or "AddStaticAbility" or "AddAbility")) ctx.Need("Class:" + key);
                face.ClassLevels.Add((level, Engine.Grants.Compile(gp, ctx, "AddAbility", "AddTrigger", "AddStaticAbility") ?? new Grants()));
                AddGenerated(face, ctx, $"AB$ ClassLevelUp | Cost$ {Part(2)} | SorcerySpeed$ True | ClassLevel$ {level} | SpellDescription$ Level {level}");
                return true;
            }
            case "Myriad":
                // Attacking creates a copy attacking each other opponent; here they join the attack.
                face.SVars["__MyriadCopies"] = "DB$ CopyPermanent | Defined$ Self | NumCopies$ __MyriadX | TokenTapped$ True | TokenAttacking$ True | AtEOT$ Exile";
                face.SVars["__MyriadX"] = "PlayerCountOpponents$Amount/Minus.1";
                face.Triggers.Add(CompileTrigger(new ForgeParams("Mode$ Attacks | ValidCard$ Card.Self | TriggerZones$ Battlefield | Execute$ __MyriadCopies"), ctx));
                return true;
            case "Mobilize":
                // "Whenever this attacks, create N tapped and attacking 1/1 red Warriors; sacrifice them at the next end step."
                face.SVars["__Mobilize"] = $"DB$ Token | TokenScript$ r_1_1_warrior | TokenAmount$ {Part(1)} | TokenOwner$ You | TokenTapped$ True | TokenAttacking$ True | AtEOT$ Sacrifice";
                if (Part(1) == "X") face.SVars["__Mobilize"] = face.SVars["__Mobilize"].Replace("TokenAmount$ X", "TokenAmount$ __MobilizeX");
                face.SVars["__MobilizeX"] = "Count$CardPower";
                face.Triggers.Add(CompileTrigger(new ForgeParams("Mode$ Attacks | ValidCard$ Card.Self | TriggerZones$ Battlefield | Execute$ __Mobilize"), ctx));
                return true;
            case "Cascade":
                face.SVars["__Cascade"] = "DB$ Cascade";
                face.Triggers.Add(CompileTrigger(new ForgeParams("Mode$ SpellCast | ValidCard$ Card.Self | TriggerZones$ Stack | Execute$ __Cascade"), ctx));
                return true;
            case "Ninjutsu":
                // "Return an unblocked attacker you control to hand: put this onto the battlefield tapped and attacking."
                AddGenerated(face, ctx, $"AB$ Ninjutsu | Cost$ {Part(1)} Return<1/Creature.attacking+YouCtrl> | ActivationZone$ Hand | SpellDescription$ Ninjutsu");
                return true;
            case "Persist":
            case "Undying":
            {
                // "When this dies, if it had no -1/-1 (+1/+1) counters on it, return it with one."
                var kind = name == "Persist" ? "M1M1" : "P1P1";
                face.SVars["__" + name] = $"DB$ ChangeZone | Defined$ TriggeredNewCardLKICopy | Origin$ Graveyard | Destination$ Battlefield | WithCountersType$ {kind} | WithCountersAmount$ 1";
                face.Triggers.Add(CompileTrigger(new ForgeParams(
                    $"Mode$ ChangesZone | Origin$ Battlefield | Destination$ Graveyard | ValidCard$ Card.Self+counters_EQ0_{kind} | TriggerZones$ Battlefield | Execute$ __{name}"), ctx));
                return true;
            }
            case "Annihilator":
                return true;   // the defending goldfish opponent has no permanents to sacrifice
            case "TypeCycling":
            {
                // "Basic landcycling {1}{B}": search for a card of that type instead of drawing.
                var type = Part(1) == "Basic" ? "Land.Basic" : Part(1);
                AddGenerated(face, ctx, $"AB$ ChangeZone | Cost$ {Part(2)} Discard<1/CARDNAME> | ActivationZone$ Hand | Origin$ Library | Destination$ Hand | ChangeType$ {type} | ChangeNum$ 1 | SpellDescription$ {Part(1)}cycling");
                face.Activated[^1].IsCycling = true;
                return true;
            }
            case "MayEffectFromOpeningHand":
                if (!face.SVars.TryGetValue(Part(1), out var openingBody)) { ctx.Need("SVarMissing:" + Part(1)); return true; }
                face.OpeningHand = CompileAbility(new ForgeParams(openingBody), ctx, AbilityKind.Sub, 0);
                return true;
            case "Suspend":
                // Exile it from your hand with N time counters; remove one each upkeep; cast it free when the last is removed.
                if (!int.TryParse(Part(1), out _)) { ctx.Need("Keyword:Suspend+" + Part(1)); return true; }
                AddGenerated(face, ctx, $"AB$ Suspend | Cost$ {Part(2)} | ActivationZone$ Hand | TimeCounters$ {Part(1)}"
                    + (face.Types.Is(CardType.Instant) || face.KeywordNames.Contains("Flash") ? "" : " | SorcerySpeed$ True") + " | SpellDescription$ Suspend");
                return true;
            case "Evoke":
            {
                var cost = ParseCost(Part(1), ctx);
                if (face.SpellAbilities.Count == 0)
                    face.SpellAbilities.Add(new AbilityDef { P = new ForgeParams("SP$ PermanentSpell"), Api = "PermanentSpell", Kind = AbilityKind.Spell });
                face.EvokeCost = cost;
                return true;
            }
            case "Kicker":
                // "Kicker {cost}": one optional additional cost. Two different kicker costs aren't supported yet.
                if (Part(2).Length > 0) { ctx.Need("Keyword:Kicker+Second"); return true; }
                face.OptionalCosts.Add((new[] { "Kicker" }, ParseCost(Part(1), ctx)));
                return true;
            case "Bargain":
                // "You may sacrifice an artifact, enchantment, or token as you cast this spell."
                face.OptionalCosts.Add((new[] { "Bargain" }, ParseCost("Sac<1/Artifact;Enchantment;Card.token/artifact, enchantment, or token>", ctx)));
                return true;
            case "Escape":
                // K:Escape:<cost>, e.g. "3 B B ExileFromGrave<4/Card.Other/other>"; cast from the graveyard for it.
                ParseCost(string.Join(':', parts.Skip(1)).Replace("CardManaCost", "").Trim(), ctx);
                face.EscapeCost = string.Join(':', parts.Skip(1)).Trim();
                return true;
            case "Splice":
                // K:Splice:Arcane:1 R
                if (Part(1).Length == 0 || Part(2).Length == 0) { ctx.Need("Keyword:Splice+" + raw); return true; }
                face.Splice = (Part(1), ParseCost(Part(2), ctx));
                return true;
            case "Transmute":
                // "{cost}, discard this: search for a card with the same mana value. Transmute only as a sorcery."
                AddGenerated(face, ctx, $"AB$ ChangeZone | Cost$ {Part(1)} Discard<1/CARDNAME> | ActivationZone$ Hand | Origin$ Library | Destination$ Hand | ChangeType$ Card.cmcEQ{face.ManaCost.ManaValue} | ChangeNum$ 1 | SorcerySpeed$ True | SpellDescription$ Transmute");
                return true;
            case "Rebound":
                // "If you cast this from your hand, exile it as it resolves. At your next upkeep, you may cast it from exile free."
                face.SVars["__Rebound"] = "DB$ Play | Defined$ Self | WithoutManaCost$ True | Optional$ True";
                face.Rebound = CompileTrigger(new ForgeParams("Mode$ Phase | Phase$ Upkeep | ValidPlayer$ You | Execute$ __Rebound"), ctx);
                return true;
            case "Extort":
                // "Whenever you cast a spell, you may pay {W/B}. If you do, each opponent loses 1 life and you gain that much life."
                face.SVars["__Extort"] = "AB$ LoseLife | Cost$ W/B | Defined$ Opponent | LifeAmount$ 1 | SubAbility$ __ExtortGain";
                face.SVars["__ExtortGain"] = "DB$ GainLife | Defined$ You | LifeAmount$ __ExtortX";
                face.SVars["__ExtortX"] = "PlayerCountOpponents$Amount";
                face.Triggers.Add(CompileTrigger(new ForgeParams("Mode$ SpellCast | ValidActivatingPlayer$ You | TriggerZones$ Battlefield | Execute$ __Extort"), ctx));
                return true;
            case "Morph":
            case "Megamorph":
                // Cast face down for {3}; turn face up any time for the morph cost (rule 702.37).
                if (Part(1).Length == 0) { ctx.Need("Keyword:" + name + "+NoCost"); return true; }
                ParseCost(Part(1), ctx);
                face.Morph = (Part(1), name == "Megamorph");
                return true;
            case "Fabricate":
                // "When this enters, put N +1/+1 counters on it or create N 1/1 colorless Servo artifact creature tokens."
                if (!int.TryParse(Part(1), out var fab)) { ctx.Need("Keyword:Fabricate+" + Part(1)); return true; }
                face.SVars["__Fabricate"] = "DB$ GenericChoice | Choices$ __FabricateCounters,__FabricateServos";
                face.SVars["__FabricateCounters"] = $"DB$ PutCounter | Defined$ Self | CounterType$ P1P1 | CounterNum$ {fab} | SpellDescription$ {fab} +1/+1 counters";
                face.SVars["__FabricateServos"] = $"DB$ Token | TokenAmount$ {fab} | TokenScript$ c_1_1_a_servo | TokenOwner$ You | SpellDescription$ {fab} Servos";
                face.Triggers.Add(CompileTrigger(new ForgeParams("Mode$ ChangesZone | Origin$ Any | Destination$ Battlefield | ValidCard$ Card.Self | Execute$ __Fabricate"), ctx));
                return true;
            case "Changeling":
                face.Types = new TypeLine { Types = face.Types.Types, Supers = face.Types.Supers, Subtypes = face.Types.Subtypes, AllCreatureTypes = true };
                return true;
            case "ETBReplacement" when Part(1) == "Copy":
                // "You may have this enter as a copy of …": K:ETBReplacement:Copy:SVarName[:Optional]
                if (Part(3) is not ("" or "Optional")) { ctx.Need("Keyword:ETBReplacement:Copy+" + Part(3)); return true; }
                if (!face.SVars.TryGetValue(Part(2), out var copyBody)) { ctx.Need("SVarMissing:" + Part(2)); return true; }
                if (Part(3) == "Optional") copyBody += " | Optional$ True";
                face.EtbCopy = CompileAbility(new ForgeParams(copyBody), ctx, AbilityKind.Sub, 0);
                return true;
            case "ETBReplacement":
                // "As this enters, choose …": K:ETBReplacement:Other:SVarName[:Mandatory]
                if (Part(1) != "Other" || Part(3) is not ("" or "Mandatory") || Part(5).Length > 0)
                {
                    ctx.Need("Keyword:ETBReplacement:" + Part(1) + (Part(3).Length > 0 ? "+" + Part(3) : "") + (Part(5).Length > 0 ? "+Condition" : ""));
                    return true;
                }
                if (!face.SVars.TryGetValue(Part(2), out var etb)) { ctx.Need("SVarMissing:" + Part(2)); return true; }
                if (CompileAbility(new ForgeParams(etb), ctx, AbilityKind.Sub, 0) is { } etbAb) face.EtbReplacements.Add(etbAb);
                return true;
            default:
                return false;
        }
    }

    /// <summary>"You may [cost] rather than pay this spell's mana cost": each way to cast it gets an alternative-cost twin.</summary>
    private static void AddAlternativeCost(FaceDef face, StaticDef st, CompileContext ctx)
    {
        if (!(st["ValidSA"] == "Spell.Self" || st["ValidSA"] == "Spell" && st["ValidCard"] == "Card.Self")) { ctx.Need("AlternativeCost.ValidSA:" + st["ValidSA"]); return; }
        if (st["Cost"] is not { } costText) { ctx.Need("AlternativeCost:NoCost"); return; }
        var cost = ParseCost(costText, ctx);
        if (face.SpellAbilities.Count == 0)
        {
            if (face.AuraSpell is not null) { ctx.Need("AlternativeCost:Aura"); return; }
            // A permanent: list its normal cast explicitly so the alternative can sit next to it.
            face.SpellAbilities.Add(new AbilityDef { P = new ForgeParams("SP$ PermanentSpell"), Api = "PermanentSpell", Kind = AbilityKind.Spell });
        }
        foreach (var ab in face.SpellAbilities.Where(a => a.AltCostOf is null).ToList())
        {
            var alt = ab.CloneHead();
            alt.AltCost = cost;
            alt.AltCostOf = st;
            alt.ModeDescription = (ab.ModeDescription ?? ab.Description) + " (alternative cost)";
            face.SpellAbilities.Add(alt);
        }
    }

    /// <summary>"You may pay an additional [cost] as you cast this spell" (kicker, bargain): each way to cast it gets a twin that pays it.</summary>
    private static void AddOptionalCost(FaceDef face, string[] names, Cost extra, CompileContext ctx)
    {
        if (face.SpellAbilities.Count == 0)
        {
            if (face.AuraSpell is not null) { ctx.Need("OptionalCost:Aura"); return; }
            face.SpellAbilities.Add(new AbilityDef { P = new ForgeParams("SP$ PermanentSpell"), Api = "PermanentSpell", Kind = AbilityKind.Spell });
        }
        foreach (var ab in face.SpellAbilities.Where(a => a.OptionalCosts is null).ToList())
        {
            var twin = ab.CloneHead();
            twin.ExtraCost = extra;
            twin.OptionalCosts = names;
            twin.ModeDescription = (ab.ModeDescription ?? ab.Description) + $" ({string.Join(", ", names).ToLowerInvariant()})";
            face.SpellAbilities.Add(twin);
        }
    }

    /// <summary>Morph: a way to cast it face down for {3}, and its face-down self, which can be turned face up for the morph cost.</summary>
    private static void AddMorph(FaceDef face, string cost, bool mega, CompileContext ctx)
    {
        if (face.AuraSpell is not null) { ctx.Need("Morph:Aura"); return; }
        if (face.SpellAbilities.Count == 0)
            face.SpellAbilities.Add(new AbilityDef { P = new ForgeParams("SP$ PermanentSpell"), Api = "PermanentSpell", Kind = AbilityKind.Spell });
        face.SpellAbilities.Add(new AbilityDef
        {
            P = new ForgeParams("SP$ PermanentSpell"), Api = "PermanentSpell", Kind = AbilityKind.Spell,
            AltCost = new Cost { Mana = ManaCost.Parse("3")! }, CastFaceDown = true, ModeDescription = "face down (morph)",
        });
        var down = new FaceDef
        {
            Name = "Face-down creature", ManaCost = ManaCost.None, Types = TypeLine.Parse("Creature"), Power = 2, Toughness = 2, Colors = 0,
            SVars = face.SVars,
        };
        if (CompileAbility(new ForgeParams($"AB$ TurnFaceUp | Cost$ {cost}" + (mega ? " | Megamorph$ True" : "") + " | SpellDescription$ Turn face up"),
                ctx, AbilityKind.Activated, 0) is { } up)
        {
            up.IsSpecialAction = true;
            down.Activated.Add(up);
        }
        face.FaceDownFace = down;
    }

    /// <summary>Compiles a chain an effect refers to by name (e.g. FlipCoin's WinSubAbility$). Null if the parameter is absent.</summary>
    public static AbilityDef? CompileSideChain(AbilityDef ab, string key, CompileContext ctx)
    {
        if (ab[key] is not { } name) return null;
        if (!ctx.Face.SVars.TryGetValue(name, out var body)) { ctx.Need("SVarMissing:" + name); return null; }
        var chain = CompileAbility(new ForgeParams(body), ctx, AbilityKind.Sub, SideChainIndex);
        if (chain is null) return null;
        if (chain.Chain().Any(a => a.TargetFilter is not null)) ctx.Need("SideChain:Targets");
        (ab.SideChains ??= new())[key] = chain;
        return chain;
    }

    /// <summary>Side chains never choose their own targets; their links are numbered from here (see EffectContext.ParentTargets).</summary>
    public const int SideChainIndex = 100;

    private static void AddGenerated(FaceDef face, CompileContext ctx, string script)
    {
        if (CompileAbility(new ForgeParams(script), ctx, AbilityKind.Activated, 0) is { } ab) face.Activated.Add(ab);
    }

    private static void CompileReplacement(ForgeParams r, FaceDef face, CompileContext ctx)
    {
        var evt = r["Event"] ?? "?";
        var keys = r.Values.Keys.Where(k => k is not ("Event" or "Description" or "ValidCard" or "ReplacementResult")).ToList();
        if (r["ValidCard"] == "Card.Self" && CompileSelfReplacement(r, evt, keys, face, ctx)) return;
        if (Engine.Replacements.TryCompile(r, face, ctx)) return;
        ctx.Need("Replacement:" + evt);
    }

    /// <summary>A card's replacement effects on itself: "as this enters", "doesn't untap". False if not one of these.</summary>
    private static bool CompileSelfReplacement(ForgeParams r, string evt, List<string> keys, FaceDef face, CompileContext ctx)
    {
        switch (evt)
        {
            // "Enters tapped", shocklands, checklands: an effect applied as the card enters.
            case "Moved" when r["Destination"] == "Battlefield" && r["ReplaceWith"] is { } with
                              && keys.All(k => k is "Destination" or "ReplaceWith"):
                if (!face.SVars.TryGetValue(with, out var body)) { ctx.Need("SVarMissing:" + with); return true; }
                if (CompileAbility(new ForgeParams(body), ctx, AbilityKind.Sub, 0) is { } ab) face.EtbReplacements.Add(ab);
                return true;
            // Mana Vault, Grim Monolith: "doesn't untap during your untap step".
            case "Untap" when r["Layer"] == "CantHappen" && r["ValidStepTurnToController"] == "You"
                              && keys.All(k => k is "Layer" or "ValidStepTurnToController"):
                face.DoesntUntap = true;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// A modal ability ("Charm": choose one/two/…) becomes one ability per allowed combination of
    /// modes, so choosing modes is just choosing which ability to use. Anything else is returned as is.
    /// </summary>
    public static List<AbilityDef> ExpandModes(AbilityDef ab, CompileContext ctx)
    {
        if (ab.Api != "Charm") return new() { ab };
        var choices = (ab["Choices"] ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        // "Choose one. If you control a commander, you may choose both": the limit is worked out as it's cast.
        string? charmExpr = null;
        if (!int.TryParse(ab["CharmNum"] ?? "1", out var max))
        {
            charmExpr = ab["CharmNum"]!;
            Amount.Parse(charmExpr, ctx);
            max = choices.Length;
        }
        if (!int.TryParse(ab["MinCharmNum"] ?? max.ToString(), out var min)) { ctx.Need("Charm:MinCharmNum"); return new() { ab }; }
        min = Math.Max(1, min);
        max = Math.Min(max, choices.Length);
        foreach (var c in choices)
            if (!ctx.Face.SVars.ContainsKey(c)) { ctx.Need("SVarMissing:" + c); return new() { ab }; }

        var combos = new List<int[]>();
        for (var k = min; k <= max; k++) Combinations(choices.Length, k, 0, new List<int>(), combos);
        if (combos.Count > 40) { ctx.Need("Charm:TooManyModes"); return new() { ab }; }

        var result = new List<AbilityDef>();
        foreach (var combo in combos)
        {
            var head = new AbilityDef
            {
                P = ab.P, Api = "Charm", Kind = ab.Kind, Cost = ab.Cost, ChainIndex = ab.ChainIndex, Face = ab.Face,
                CharmNumExpr = charmExpr, CharmCount = combo.Length,
            };
            var tail = head;
            foreach (var i in combo)
            {
                var mode = CompileAbility(new ForgeParams(ctx.Face.SVars[choices[i]]), ctx, AbilityKind.Sub, tail.ChainIndex + 1);
                if (mode is null) continue;
                tail.Sub = mode;
                while (tail.Sub is not null) tail = tail.Sub;
            }
            if (ab["SubAbility"] is { } sub && ctx.Face.SVars.TryGetValue(sub, out var subBody))
                tail.Sub = CompileAbility(new ForgeParams(subBody), ctx, AbilityKind.Sub, tail.ChainIndex + 1);
            head.ModeDescription = string.Join(" + ", combo.Select(i => new ForgeParams(ctx.Face.SVars[choices[i]])["SpellDescription"] ?? choices[i]));
            result.Add(head);
        }
        return result;
    }

    private static void Combinations(int n, int k, int start, List<int> current, List<int[]> output)
    {
        if (current.Count == k) { output.Add(current.ToArray()); return; }
        for (var i = start; i < n; i++)
        {
            current.Add(i);
            Combinations(n, k, i + 1, current, output);
            current.RemoveAt(current.Count - 1);
        }
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
            P = p, Api = api, Kind = kind, Cost = cost, ChainIndex = chainIndex, Face = ctx.Face,
            IsManaAbility = api is "Mana" or "ManaReflected" && kind == AbilityKind.Activated && p["ValidTgts"] is null,
        };

        if (p["ValidTgts"] is { } vt)
        {
            ab.TargetFilter = Filter.Parse(vt, ctx);
            ab.TargetMin = TargetCount(p["TargetMin"], ctx, out var minExpr) ?? 1;
            ab.TargetMax = TargetCount(p["TargetMax"], ctx, out var maxExpr) ?? 1;
            ab.TargetMinExpr = minExpr;
            ab.TargetMaxExpr = maxExpr;
            if (maxExpr is not null && p["TargetMin"] is null) ab.TargetMin = 0;
        }

        if (p["OptionalDecider"] is { } od && od is not ("You" or "TargetedController" or "Targeted" or "Opponent" or "TriggeredActivator"))
            ctx.Need("OptionalDecider:" + od);
        // "Activate only if …" (Activation$ Metalcraft).
        if (p["Activation"] is { } activation && !Presence.IsKnownCondition(activation)) ctx.Need("Activation:" + activation);
        if (p["UnlessResolveSubs"] is { } urs && urs != "WhenPaid") ctx.Need("UnlessResolveSubs:" + urs);
        // "This ability costs {1} less to activate for each …"
        if (p["ReduceCost"] is { } reduce) Amount.Parse(reduce, ctx);
        if (p["UnlessCost"] is { } unless)
        {
            ab.UnlessCost = ParseCost(unless, ctx);
            if (p["UnlessPayer"] is { } payer && payer is not ("You" or "TargetedController" or "Targeted" or "Opponent" or "Player"
                    or "TriggeredActivator" or "TriggeredCardController" or "Remembered"))
                ctx.Need("UnlessPayer:" + payer);
        }

        if (handler is not null)
        {
            foreach (var key in p.Values.Keys)
                if (!CosmeticParams.Contains(key) && !GenericParams.Contains(key) && !handler.Params.Contains(key))
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

    /// <summary>A constant number of targets, or null with <paramref name="expr"/> set for one worked out later.</summary>
    private static int? TargetCount(string? text, CompileContext ctx, out string? expr)
    {
        expr = null;
        if (text is null) return null;
        if (int.TryParse(text, out var n)) return n;
        expr = text;
        if (text != "OneEach") Amount.Parse(text, ctx);
        return 0;
    }

    /// <summary>Splits a cost on spaces, keeping "Sac&lt;1/CARDNAME/this token&gt;" whole.</summary>
    public static IEnumerable<string> SplitCost(string text)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] == '<') depth++;
            else if (i < text.Length && text[i] == '>') depth = Math.Max(0, depth - 1);
            else if (i == text.Length || text[i] == ' ' && depth == 0)
            {
                if (i > start) yield return text[start..i];
                start = i + 1;
            }
        }
    }

    public static Cost ParseCost(string text, CompileContext ctx)
    {
        var manaTokens = new List<string>();
        bool tap = false, untap = false;
        var parts = new List<CostPart>();
        foreach (var token in SplitCost(text))
        {
            if (token == "T") { tap = true; continue; }
            if (token == "Q") { untap = true; ctx.Need("Cost:Untap"); continue; }
            var m = CostPartRx().Match(token);
            if (!m.Success) { manaTokens.Add(token); continue; }
            var args = m.Groups[2].Value.Split('/');
            var amount = args[0];
            var valid = args.Length > 1 ? args[1].Replace(';', ',') : "";   // "Artifact;Creature": either
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
                "ExileFromHand" => CostPartKind.ExileFromHand,
                "Return" => CostPartKind.ReturnToHand,
                "Reveal" => CostPartKind.Reveal,
                "Exile" when valid == "CARDNAME" || m.Groups[2].Value == "CARDNAME" => CostPartKind.ExileSource,
                "Crew" => CostPartKind.TapPower,
                _ => null,
            };
            if (kind is null) { ctx.Need("Cost:" + m.Groups[1].Value); continue; }
            if (kind is CostPartKind.Sacrifice or CostPartKind.Discard or CostPartKind.ExileFromGraveyard or CostPartKind.TapUntapped
                    or CostPartKind.ExileFromHand or CostPartKind.ReturnToHand or CostPartKind.Reveal
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
