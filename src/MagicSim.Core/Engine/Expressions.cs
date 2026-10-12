using System.Text.RegularExpressions;

namespace MagicSim.Core.Engine;

/// <summary>What an effect is resolving with: the game, the stack item, and which ability in its chain.</summary>
public sealed class EffectContext
{
    public required Game Game { get; init; }
    public required StackItem Item { get; init; }
    public required AbilityDef Ability { get; init; }
    public GameState S => Game.State;
    public Card Host => Game.State[Item.SourceId];
    public int Controller => Item.Controller;
    public int X => Item.X;
    /// <summary>Targets chosen for this ability in the chain.</summary>
    public List<TargetRef> Targets => Item.ChainTargets.Count > Ability.ChainIndex ? Item.ChainTargets[Ability.ChainIndex] : new();
    /// <summary>Targets of the nearest earlier ability in the chain that had any ("ParentTarget").</summary>
    public List<TargetRef> ParentTargets
    {
        get
        {
            for (var i = Ability.ChainIndex - 1; i >= 0; i--)
                if (i < Item.ChainTargets.Count && Item.ChainTargets[i].Count > 0) return Item.ChainTargets[i];
            return new();
        }
    }

    /// <summary>"Targeted" in Forge: this ability's targets, or in a sub-ability without its own, the nearest parent's.</summary>
    public List<TargetRef> OwnOrParentTargets => Targets.Count > 0 ? Targets : ParentTargets;

    /// <summary>For a static or trigger: the face whose SVars it uses, when the ability doesn't say.</summary>
    public FaceDef? FaceOverride { get; init; }
    /// <summary>The face whose SVars this ability's expressions refer to: the card that defined it.</summary>
    public FaceDef SVarFace => Ability.Face ?? FaceOverride ?? Host.CurrentFace;
    /// <summary>For a replacement effect: the amount being replaced (ReplaceCount$…).</summary>
    public int ReplacedAmount { get; init; }

    public EffectContext For(AbilityDef sub) => new() { Game = Game, Item = Item, Ability = sub, FaceOverride = FaceOverride, ReplacedAmount = ReplacedAmount };
}

/// <summary>
/// A Forge "Valid" filter, e.g. "Creature.YouCtrl+cmcLE3,Artifact.Other". Comma = or; "+" = and.
/// Unknown properties are reported as missing at compile time, never guessed.
/// </summary>
public sealed class Filter
{
    private readonly List<Term> _terms;
    public string Text { get; }

    private Filter(string text, List<Term> terms) { Text = text; _terms = terms; }

    private sealed record Term(string Base, List<Prop> Props);
    private sealed record Prop(bool Negated, Func<Card?, int?, EffectContext, bool> Test);

    /// <summary>Subtypes seen in any card's type line (filled once the Forge library loads).</summary>
    public static HashSet<string> KnownSubtypes { get; } = new(StringComparer.Ordinal)
    {
        "Plains", "Island", "Swamp", "Mountain", "Forest", "Equipment", "Aura", "Treasure", "Food", "Clue",
    };

    private static readonly Dictionary<string, CardType> CardTypes = new(StringComparer.Ordinal)
    {
        ["Artifact"] = CardType.Artifact, ["Creature"] = CardType.Creature, ["Enchantment"] = CardType.Enchantment,
        ["Instant"] = CardType.Instant, ["Land"] = CardType.Land, ["Planeswalker"] = CardType.Planeswalker,
        ["Sorcery"] = CardType.Sorcery, ["Battle"] = CardType.Battle,
        ["Tribal"] = CardType.Kindred, ["Kindred"] = CardType.Kindred,
    };

    private static readonly Regex CompareRx = new(@"^(cmc|power|toughness)(LE|LT|GE|GT|EQ|NE)(.+)$", RegexOptions.Compiled);
    private static readonly Regex CountersRx = new(@"^counters_(GE|LE|EQ|GT|LT)(\d+)_([A-Z0-9]+)$", RegexOptions.Compiled);

    public static Filter Parse(string text, CompileContext ctx)
    {
        var terms = new List<Term>();
        foreach (var raw in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var dot = raw.IndexOf('.');
            var baseName = dot < 0 ? raw : raw[..dot];
            var props = new List<Prop>();
            if (!IsKnownBase(baseName)) ctx.Need("FilterBase:" + baseName);
            if (dot >= 0)
                foreach (var p in raw[(dot + 1)..].Split('+', StringSplitOptions.RemoveEmptyEntries))
                    if (ParseProp(p, ctx) is { } prop) props.Add(prop);
            terms.Add(new Term(baseName, props));
        }
        return new Filter(text, terms);
    }

    private static bool IsKnownBase(string b) =>
        b is "Card" or "Permanent" or "Spell" or "Player" or "Opponent" or "You" or "Any" or "Creature" or "Planeswalker"
        || CardTypes.ContainsKey(b) || KnownSubtypes.Contains(b);

    private static Prop? ParseProp(string p, CompileContext ctx)
    {
        var neg = false;
        if (p.StartsWith('!')) { neg = true; p = p[1..]; }
        else if (p.StartsWith("non", StringComparison.Ordinal) && p.Length > 3 && char.IsUpper(p[3])) { neg = true; p = p[3..]; }

        Func<Card?, int?, EffectContext, bool>? test = p switch
        {
            "YouCtrl" => (c, pl, x) => c is not null ? c.Controller == x.Controller : pl == x.Controller,
            "YouOwn" => (c, pl, x) => c is not null && c.Owner == x.Controller,
            "OppCtrl" => (c, pl, x) => c is not null ? c.Controller != x.Controller : pl is not null && pl != x.Controller,
            "OppOwn" => (c, pl, x) => c is not null && c.Owner != x.Controller,
            "YouDontCtrl" => (c, pl, x) => c is not null && c.Controller != x.Controller,
            "Opponent" => (c, pl, x) => pl is not null && pl != x.Controller,
            "You" => (c, pl, x) => pl == x.Controller,
            "Self" => (c, pl, x) => c is not null && c.Id == x.Item.SourceId,
            "Other" or "StrictlyOther" => (c, pl, x) => c is null || c.Id != x.Item.SourceId,
            "token" => (c, pl, x) => c is not null && c.IsToken,
            "tapped" => (c, pl, x) => c is not null && c.Tapped,
            "attacking" => (c, pl, x) => c is not null && x.S.Attackers.Contains(c.Id),
            "untapped" => (c, pl, x) => c is not null && !c.Tapped,
            "IsCommander" => (c, pl, x) => c is not null && c.IsCommander,
            "NamedCard" => (c, pl, x) => c is not null && x.Host.NamedCard is { } n && string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase),
            "IsRemembered" => (c, pl, x) => c is not null ? x.Host.Remembered?.Contains(c.Id) ?? false : pl is not null && pl == x.Item.IteratedPlayer,
            "IsTriggerRemembered" => (c, pl, x) => c is not null && (x.Item.DelayRemembered?.Contains(c.Id) ?? false),
            "ThisTurnEntered" => (c, pl, x) => c is not null && c.Zone == Zone.Battlefield && c.EnteredTurn == x.S.Turn,
            "Historic" => (c, pl, x) => c is not null && (c.Types.Has(SuperType.Legendary) || c.Types.Is(CardType.Artifact) || c.Types.HasSubtype("Saga")),
            "ChosenColor" => (c, pl, x) => c is not null && x.Host.ChosenColor is { } cc && (c.Colors & cc.Mask()) != 0,
            "ChosenType" => (c, pl, x) => c is not null && x.Host.ChosenType is { } ct && c.Types.HasSubtype(ct),
            "IsImprinted" => (c, pl, x) => c is not null && (x.Host.Imprinted?.Contains(c.Id) ?? false),
            "sharesNameWith Remembered" => (c, pl, x) => c is not null && x.Host.Remembered is { } rem
                && rem.Any(id => id != c.Id && x.S[id].Name == c.Name),
            "EquippedBy" or "EnchantedBy" or "AttachedBy" => (c, pl, x) => c is not null && x.Host.AttachedTo == c.Id,
            "TopLibrary" => (c, pl, x) => c is not null && c.Zone == Zone.Library && x.S.Players[c.Owner].Library.FirstOrDefault(-1) == c.Id,
            "Basic" => (c, pl, x) => c is not null && c.Types.Has(SuperType.Basic),
            "Legendary" => (c, pl, x) => c is not null && c.Types.Has(SuperType.Legendary),
            "Snow" => (c, pl, x) => c is not null && c.Types.Has(SuperType.Snow),
            "Colorless" => (c, pl, x) => c is not null && c.Colors == 0,
            "Multicolor" => (c, pl, x) => c is not null && System.Numerics.BitOperations.PopCount((uint)c.Colors) > 1,
            "MonoColor" => (c, pl, x) => c is not null && System.Numerics.BitOperations.PopCount((uint)c.Colors) == 1,
            "Permanent" => (c, pl, x) => c is not null && c.Types.IsPermanent,
            _ => null,
        };

        if (test is null && p.StartsWith("inZone", StringComparison.Ordinal) && Enum.TryParse<Zone>(p[6..], out var zone))
            test = (c, pl, x) => c is not null && c.Zone == zone;
        if (test is null && p.StartsWith("with", StringComparison.Ordinal) && !p.StartsWith("without", StringComparison.Ordinal))
        {
            var kw = SplitCamel(p[4..]);
            if (Keywords.Supported.Contains(kw)) test = (c, pl, x) => c is not null && c.HasKeyword(kw);
        }
        if (test is null && p.StartsWith("without", StringComparison.Ordinal))
        {
            var kw = SplitCamel(p[7..]);
            if (Keywords.Supported.Contains(kw)) test = (c, pl, x) => c is not null && !c.HasKeyword(kw);
        }
        if (test is null && CardTypes.TryGetValue(p, out var ct))
            test = (c, pl, x) => c is not null && c.Types.Is(ct);
        if (test is null && ManaColors.FromWord(p) is { } color && p.Length > 1)
            test = color == ManaColor.C
                ? (c, pl, x) => c is not null && c.Colors == 0
                : (c, pl, x) => c is not null && (c.Colors & color.Mask()) != 0;
        if (test is null && KnownSubtypes.Contains(p))
            test = (c, pl, x) => c is not null && c.Types.HasSubtype(p);
        if (test is null && CompareRx.Match(p) is { Success: true } m)
        {
            var amount = Amount.Parse(m.Groups[3].Value, ctx);
            var op = m.Groups[2].Value;
            Func<Card, int> stat = m.Groups[1].Value switch
            {
                "cmc" => c => c.CurrentFace.ManaCost.ManaValue,
                "power" => c => c.Power,
                _ => c => c.Toughness,
            };
            test = (c, pl, x) => c is not null && Compare(stat(c), op, amount.Eval(x));
        }
        if (test is null && CountersRx.Match(p) is { Success: true } cm)
        {
            var n = int.Parse(cm.Groups[2].Value);
            var kind = cm.Groups[3].Value;
            var op = cm.Groups[1].Value;
            test = (c, pl, x) => c is not null && Compare(c.CounterCount(kind), op, n);
        }

        if (test is null)
        {
            ctx.Need("FilterProp:" + p);
            return null;
        }
        return new Prop(neg, test);
    }

    public static bool Compare(int a, string op, int b) => op switch
    {
        "LE" => a <= b, "LT" => a < b, "GE" => a >= b, "GT" => a > b, "EQ" => a == b, "NE" => a != b,
        _ => false,
    };

    private static string SplitCamel(string s) => Regex.Replace(s, "(?<=[a-z])([A-Z])", " $1");

    public bool MatchesCard(Card c, EffectContext x) => _terms.Any(t => BaseMatchesCard(t.Base, c) && t.Props.All(p => p.Test(c, null, x) != p.Negated));

    public bool MatchesPlayer(int player, EffectContext x) => _terms.Any(t => BaseMatchesPlayer(t.Base, player, x) && t.Props.All(p => p.Test(null, player, x) != p.Negated));

    public bool CanTargetPlayers => _terms.Any(t => t.Base is "Player" or "Opponent" or "You" or "Any");
    public bool CanTargetCards => _terms.Any(t => t.Base is not ("Player" or "Opponent" or "You"));

    private static bool BaseMatchesCard(string b, Card c) => b switch
    {
        "Card" or "Spell" => true,
        "Permanent" => c.Zone == Zone.Battlefield || c.Types.IsPermanent,
        "Any" => c.Types.Is(CardType.Creature) || c.Types.Is(CardType.Planeswalker) || c.Types.Is(CardType.Battle),
        "Player" or "Opponent" or "You" => false,
        _ when CardTypes.TryGetValue(b, out var t) => c.Types.Is(t),
        _ => c.Types.HasSubtype(b),
    };

    private static bool BaseMatchesPlayer(string b, int player, EffectContext x) => b switch
    {
        "Player" or "Any" => true,
        "Opponent" => player != x.Controller,
        "You" => player == x.Controller,
        _ => false,
    };

    public override string ToString() => Text;
}

/// <summary>
/// A Forge amount: a number, or an SVar name whose value is "Count$…" / "Number$N" (optionally
/// with math such as "/Plus.2" or "/Twice"). Unknown Count$ kinds are reported as missing.
/// </summary>
public sealed class Amount
{
    private readonly Func<EffectContext, int> _eval;
    public string Text { get; }
    private Amount(string text, Func<EffectContext, int> eval) { Text = text; _eval = eval; }

    public int Eval(EffectContext x) => _eval(x);

    public static int ParseConst(string text, CompileContext ctx)
    {
        if (int.TryParse(text.Trim().TrimStart('+'), out var n)) return n;
        ctx.Need("ConstAmount:" + text);
        return 1;
    }

    public static Amount Const(int n) => new(n.ToString(), _ => n);

    public static Amount Parse(string text, CompileContext ctx) => Parse(text, ctx, depth: 0);

    private static Amount Parse(string text, CompileContext ctx, int depth)
    {
        text = text.Trim();
        if (text.StartsWith('+')) text = text[1..];   // "+X" in pump amounts
        if (int.TryParse(text, out var n)) return new(text, _ => n);
        var negate = text.StartsWith('-');
        var name = negate ? text[1..] : text;
        if (depth > 4) { ctx.Need("Amount:Recursive"); return Const(0); }

        if (ctx.Face.SVars.TryGetValue(name, out var body))
        {
            var inner = ParseExpression(body, ctx, depth);
            // StoreSVar$ can overwrite an SVar's value on the card at run time.
            return new(text, x =>
            {
                var v = x.Host.StoredVars is { } stored && stored.TryGetValue(name, out var sv) ? sv : inner.Eval(x);
                return negate ? -v : v;
            });
        }
        if (name == "X") return new(text, x => negate ? -x.X : x.X);   // bare X with no SVar: the paid X
        return ParseExpression(text, ctx, depth);
    }

    private static readonly Regex MathRx = new(@"/(Plus|Minus|Times|Twice|Thrice|HalfUp|HalfDown|Negative|LimitMax|LimitMin)(?:\.(\-?\w+))?", RegexOptions.Compiled);

    private static Amount ParseExpression(string body, CompileContext ctx, int depth)
    {
        // Split off math suffixes: "Count$CardsInYourHand/Plus.1"
        var mathStart = body.IndexOf('/');
        var core = mathStart >= 0 ? body[..mathStart] : body;
        var math = mathStart >= 0 ? body[mathStart..] : "";
        var baseAmount = ParseCore(core.Trim(), ctx, depth);
        var result = baseAmount;
        foreach (Match m in MathRx.Matches(math))
        {
            var prev = result;
            var argText = m.Groups[2].Success ? m.Groups[2].Value : "0";
            var arg = int.TryParse(argText, out var k) ? Const(k) : Parse(argText, ctx, depth + 1);
            result = m.Groups[1].Value switch
            {
                "Plus" => new(body, x => prev.Eval(x) + arg.Eval(x)),
                "Minus" => new(body, x => prev.Eval(x) - arg.Eval(x)),
                "Times" => new(body, x => prev.Eval(x) * arg.Eval(x)),
                "Twice" => new(body, x => prev.Eval(x) * 2),
                "Thrice" => new(body, x => prev.Eval(x) * 3),
                "HalfUp" => new(body, x => (prev.Eval(x) + 1) / 2),
                "HalfDown" => new(body, x => prev.Eval(x) / 2),
                "Negative" => new(body, x => -prev.Eval(x)),
                "LimitMax" => new(body, x => Math.Min(prev.Eval(x), arg.Eval(x))),
                _ => new(body, x => Math.Max(prev.Eval(x), arg.Eval(x))),
            };
        }
        if (MathRx.Replace(math, "").Trim().Length > 0) ctx.Need("AmountMath:" + math);
        return result;
    }

    private static Amount ParseCore(string core, CompileContext ctx, int depth)
    {
        if (core.StartsWith("Number$", StringComparison.Ordinal) && int.TryParse(core[7..], out var num)) return Const(num);
        if (core.StartsWith("SVar$", StringComparison.Ordinal)) return Parse(core[5..], ctx, depth + 1);
        // The amount a replacement effect is replacing: damage, tokens, counters, life.
        if (core is "ReplaceCount$DamageAmount" or "ReplaceCount$CounterNum" or "ReplaceCount$Amount" or "ReplaceCount$LifeGained")
            return new(core, x => x.ReplacedAmount);
        // The amount of the event that triggered this: damage dealt, life gained, cards moved.
        if (core is "TriggerCount$DamageAmount" or "TriggerCount$LifeAmount" or "TriggerCount$Amount")
            return new(core, x => x.Item.TriggerAmount);
        if (DefinedCardsRx.Match(core) is { Success: true } dm) return DefinedCardsAmount(core, dm, ctx);
        if (core == "PlayerCountOpponents$Amount")
            return new(core, x => x.S.Players.Count(p => p.Index != x.Controller && !p.Lost));
        if (!core.StartsWith("Count$", StringComparison.Ordinal))
        {
            ctx.Need("Amount:" + core);
            return Const(0);
        }
        var c = core[6..];
        var space = c.IndexOf(' ');
        var kind = space < 0 ? c : c[..space];
        var arg = space < 0 ? "" : c[(space + 1)..];

        if (kind.StartsWith("Threshold.", StringComparison.Ordinal) && kind.Split('.') is [_, var a, var b]
            && int.TryParse(a, out var ifYes) && int.TryParse(b, out var ifNo))
            return new(core, x => x.S.Players[x.Controller].Graveyard.Count >= 7 ? ifYes : ifNo);
        if (kind == "Compare" && arg.Split(' ') is [var left, var rest] && rest.Length > 2 && rest[2..].Split('.') is [var rhs, var yes, var no])
        {
            var l = Parse(left, ctx, depth + 1);
            var r = Parse(rhs, ctx, depth + 1);
            var y = Parse(yes, ctx, depth + 1);
            var nAmt = Parse(no, ctx, depth + 1);
            var op = rest[..2];
            return new(core, x => Filter.Compare(l.Eval(x), op, r.Eval(x)) ? y.Eval(x) : nAmt.Eval(x));
        }
        switch (kind)
        {
            case "xPaid": return new(core, x => x.X);
            case "CardPower": return new(core, x => x.Host.LastKnownPower);
            case var cc when cc.StartsWith("CardCounters.", StringComparison.Ordinal):
                var counterKind = cc[13..];
                return new(core, x => x.Host.CounterCount(counterKind));
            case "CardToughness": return new(core, x => x.Host.LastKnownToughness);
            case "LifeYouGainedThisTurn": return new(core, x => x.S.ThisTurn.LifeGained.GetValueOrDefault(x.Controller));
            case "CardsInYourHand": return new(core, x => x.S.Players[x.Controller].Hand.Count);
            case "YourLifeTotal": return new(core, x => x.S.Players[x.Controller].Life);
            case "YourStartingLife": return new(core, x => x.Game.Settings.StartingLife);
            case "RememberedSize": return new(core, x => x.Host.Remembered?.Count ?? 0);
            case "ThisTurnCast" when arg.Length == 0: return new(core, x => x.S.ThisTurn.SpellsCast);
            case "StormCount": return new(core, x => Math.Max(0, x.S.ThisTurn.SpellsCast - 1));
            case "InYourLibrary": return new(core, x => x.S.Players[x.Controller].Library.Count);
        }
        if (kind.StartsWith("Devotion.", StringComparison.Ordinal) || kind == "Devotion")
        {
            var colors = kind.Length > 9 ? kind[9..] : arg;
            var mask = ColorMask.None;
            foreach (var w in colors.Split(new[] { '.', ' ', '+' }, StringSplitOptions.RemoveEmptyEntries))
                if (ManaColors.FromWord(w) is { } col && col != ManaColor.C) mask |= col.Mask();
            if (mask == 0) { ctx.Need("Amount:" + core); return Const(0); }
            return new(core, x => Devotion(x, mask));
        }
        // "Count$Valid[Zone][,Zone…] Filter[$Aggregate]": how many cards match, or e.g. the greatest power among them.
        var zones = kind.StartsWith("Valid", StringComparison.Ordinal) ? kind[5..].Split(',').Select(w => w switch
        {
            "" or "Battlefield" => Zone.Battlefield,
            "Graveyard" => Zone.Graveyard,
            "Hand" => Zone.Hand,
            "Library" => Zone.Library,
            "Exile" => Zone.Exile,
            _ => Zone.None,
        }).ToList() : null;
        if (zones is not null && !zones.Contains(Zone.None) && arg.Length > 0)
        {
            var dollar = arg.IndexOf('$');
            var filter = Filter.Parse(dollar < 0 ? arg : arg[..dollar], ctx);
            IEnumerable<Card> Matching(EffectContext x) => zones.SelectMany(z => x.Game.CardsIn(z)).Where(card => filter.MatchesCard(card, x));
            switch (dollar < 0 ? null : arg[(dollar + 1)..])
            {
                case null: return new(core, x => Matching(x).Count());
                case "GreatestCardPower": return new(core, x => Matching(x).Select(card => card.Power).DefaultIfEmpty(0).Max());
                case "GreatestCardToughness": return new(core, x => Matching(x).Select(card => card.Toughness).DefaultIfEmpty(0).Max());
                case "GreatestCMC": return new(core, x => Matching(x).Select(card => card.CurrentFace.ManaCost.ManaValue).DefaultIfEmpty(0).Max());
                case "CardPower": return new(core, x => Matching(x).Sum(card => card.Power));
                case "CardToughness": return new(core, x => Matching(x).Sum(card => card.Toughness));
                case "SumCMC": return new(core, x => Matching(x).Sum(card => card.CurrentFace.ManaCost.ManaValue));
                default: ctx.Need("Count:" + kind + "$" + arg[(dollar + 1)..]); return Const(0);
            }
        }
        ctx.Need("Count:" + kind);
        return Const(0);
    }

    private static readonly Regex DefinedCardsRx = new(@"^(Targeted|TriggeredCard|Remembered|Imprinted|Sacrificed)\$(CardPower|CardToughness|CardManaCost|Amount|Valid)(?: (.+))?$", RegexOptions.Compiled);

    /// <summary>"Targeted$CardPower", "Remembered$CardManaCost", "Imprinted$Valid Card.X": a property of defined cards (summed), or how many there are.</summary>
    private static Amount DefinedCardsAmount(string core, Match m, CompileContext ctx)
    {
        var which = m.Groups[1].Value;
        var prop = m.Groups[2].Value;
        var filter = prop == "Valid" ? Filter.Parse(m.Groups[3].Value, ctx) : null;
        if (prop != "Valid" && m.Groups[3].Success) ctx.Need("Amount:" + core);
        List<Card> Cards(EffectContext x)
        {
            IEnumerable<int> ids = which switch
            {
                "Targeted" => x.OwnOrParentTargets.Where(t => t.CardId is not null).Select(t => t.CardId!.Value),
                "TriggeredCard" => x.Item.TriggeredCardId is { } id ? new[] { id } : Array.Empty<int>(),
                "Remembered" => x.Host.Remembered ?? new List<int>(),
                "Sacrificed" => x.Item.Sacrificed ?? new List<int>(),
                _ => x.Host.Imprinted ?? new List<int>(),
            };
            return ids.Select(id => x.S[id]).ToList();
        }
        return prop switch
        {
            "CardPower" => new(core, x => Cards(x).Sum(c => c.LastKnownPower)),
            "CardToughness" => new(core, x => Cards(x).Sum(c => c.LastKnownToughness)),
            "CardManaCost" => new(core, x => Cards(x).Sum(c => c.CurrentFace.ManaCost.ManaValue)),
            "Amount" => new(core, x => Cards(x).Count),
            _ => new(core, x => Cards(x).Count(c => filter!.MatchesCard(c, x))),
        };
    }

    private static int Devotion(EffectContext x, ColorMask mask)
    {
        var total = 0;
        foreach (var c in x.Game.State.BattlefieldCards)
        {
            if (c.Controller != x.Controller) continue;
            foreach (var s in c.CurrentFace.ManaCost.Shards)
                if ((s.Colors & mask) != 0) total++;
        }
        return total;
    }

    public override string ToString() => Text;
}

/// <summary>Forge "Defined$" references: which players or cards an effect applies to.</summary>
public static class Defined
{
    private static readonly HashSet<string> PlayerRefs = new(StringComparer.Ordinal)
    {
        "You", "Player", "Opponent", "Targeted", "TargetedPlayer", "ParentTarget", "TargetedController", "Player.Opponent",
        "TriggeredCardController", "TriggeredTarget", "TriggeredPlayer", "TargetedOwner", "ChosenPlayer", "Remembered", "TriggeredActivator",
    };

    private static readonly HashSet<string> CardRefs = new(StringComparer.Ordinal)
    {
        "Self", "Targeted", "ParentTarget", "Remembered", "TriggeredCard", "TriggeredCardLKICopy", "TriggeredNewCardLKICopy",
        "Enchanted", "Equipped", "Imprinted", "RememberedLKI", "RememberedCard", "Sacrificed", "SacrificedCards", "DelayTriggerRemembered", "DelayTriggerRememberedLKI",
    };

    public static void ValidatePlayers(string? text, CompileContext ctx)
    {
        if (text is not null && !PlayerRefs.Contains(text)) ctx.Need("DefinedPlayer:" + text);
    }

    public static void ValidateCards(string? text, CompileContext ctx)
    {
        if (text is not null && !CardRefs.Contains(text)) ctx.Need("DefinedCard:" + text);
    }

    public static List<int> Players(string? text, EffectContext x, string fallback = "You")
    {
        var players = x.S.Players;
        return (text ?? fallback) switch
        {
            "You" => new() { x.Controller },
            "Player" => players.Where(p => !p.Lost).Select(p => p.Index).ToList(),
            "Opponent" or "Player.Opponent" => players.Where(p => !p.Lost && p.Index != x.Controller).Select(p => p.Index).ToList(),
            "Targeted" or "TargetedPlayer" => x.OwnOrParentTargets.Where(t => t.Player is not null).Select(t => t.Player!.Value).ToList(),
            "ParentTarget" => x.ParentTargets.Where(t => t.Player is not null).Select(t => t.Player!.Value).ToList(),
            "TargetedController" => x.OwnOrParentTargets.Where(t => t.CardId is not null).Select(t => x.S[t.CardId!.Value].Controller).ToList(),
            "TriggeredCardController" => x.Item.TriggeredCardId is { } tc ? new() { x.S[tc].Controller } : new(),
            "ChosenPlayer" => x.Host.ChosenPlayer is { } cp ? new() { cp } : new(),
            "Remembered" => x.Item.IteratedPlayer is { } ip ? new() { ip } : new(),
            "TargetedOwner" => x.OwnOrParentTargets.Where(t => t.CardId is not null).Select(t => x.S[t.CardId!.Value].Owner).Take(1).ToList(),
            "TriggeredTarget" or "TriggeredPlayer" or "TriggeredActivator" => x.Item.TriggeredPlayer is { } tp ? new() { tp } : new(),
            _ => new() { x.Controller },
        };
    }

    public static List<int> Cards(string? text, EffectContext x, string fallback = "Self")
    {
        return (text ?? fallback) switch
        {
            "Self" => new() { x.Item.SourceId },
            "Targeted" => x.OwnOrParentTargets.Where(t => t.CardId is not null).Select(t => t.CardId!.Value).ToList(),
            "ParentTarget" => x.ParentTargets.Where(t => t.CardId is not null).Select(t => t.CardId!.Value).ToList(),
            "Remembered" or "RememberedLKI" or "RememberedCard" => x.Host.Remembered?.ToList() ?? new(),
            "DelayTriggerRemembered" or "DelayTriggerRememberedLKI" => x.Item.DelayRemembered?.ToList() ?? new(),
            "TriggeredCard" or "TriggeredCardLKICopy" or "TriggeredNewCardLKICopy" =>
                x.Item.TriggeredCardId is { } id ? new() { id } : new(),
            "Enchanted" or "Equipped" => x.Host.AttachedTo >= 0 ? new() { x.Host.AttachedTo } : new(),
            "Imprinted" => x.Host.Imprinted?.ToList() ?? new(),
            "Sacrificed" or "SacrificedCards" => x.Item.Sacrificed?.ToList() ?? new(),
            _ => new(),
        };
    }
}
