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
    /// <summary>A property; <paramref name="OwnerTest"/> when it's only about whose the card is, for cards nobody can see.</summary>
    private sealed record Prop(bool Negated, Func<Card?, int?, EffectContext, bool> Test, Func<int, EffectContext, bool>? OwnerTest = null);

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
    private static readonly Regex HasCardsRx = new(@"^HasCardsIn(Hand|Graveyard|Library|Exile)_(.+)_(LE|LT|GE|GT|EQ|NE)(\d+)$", RegexOptions.Compiled);

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
        b is "Card" or "Permanent" or "Spell" or "Player" or "Opponent" or "You" or "Any" or "Creature" or "Planeswalker" or "Emblem" or "Effect"
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
            "YouDontOwn" => (c, pl, x) => c is not null && c.Owner != x.Controller,
            // Owned by the player being voted for / iterated over ("a permanent owned by the voter").
            "RememberedPlayerOwn" => (c, pl, x) => c is not null && c.Owner == x.Item.IteratedPlayer,
            "OppCtrl" => (c, pl, x) => c is not null ? c.Controller != x.Controller : pl is not null && pl != x.Controller,
            "OppOwn" => (c, pl, x) => c is not null && c.Owner != x.Controller,
            "ActivePlayerCtrl" => (c, pl, x) => c is not null ? c.Controller == x.S.ActivePlayer : pl == x.S.ActivePlayer,
            "ChosenCtrl" => (c, pl, x) => c is not null ? c.Controller == x.Host.ChosenPlayer : pl is not null && pl == x.Host.ChosenPlayer,
            "Chosen" => (c, pl, x) => c is null && pl is not null && pl == x.Host.ChosenPlayer,
            "DrawnThisTurn" => (c, pl, x) => c is not null && x.S.ThisTurn.Drawn.Contains(c.Id),
            // Shares a color with another remembered card; the unknown ones are only the color every card is (Painter's Servant).
            "SharesColorWithOther Remembered" => (c, pl, x) => c is not null && c.Colors != 0
                && ((x.Host.Remembered ?? new()).Any(id => id != c.Id && (x.S[id].Colors & c.Colors) != 0)
                    || x.Host.RememberedUnknown > 0 && (Statics.EverywhereColors(x.Game) & c.Colors) != 0),
            // Soulbond: paired with anything, or with this card.
            "Paired" => (c, pl, x) => c?.PairedWith is not null,
            "PairedWith" => (c, pl, x) => c is not null && c.PairedWith == x.Host.Id,
            "prowled" => (c, pl, x) => c is not null && (c.PaidOptional?.Contains("Prowl") ?? false),
            // "If you cast it" (from your hand): the permanent came from a spell. Only you cast spells here.
            "wasCast" or "wasCastByYou" => (c, pl, x) => c is not null && c.CastFrom is not null && c.Controller == x.Controller,
            "wasCastFromHand" or "wasCastFromYourHand" or "wasCastFromYourHandByYou" => (c, pl, x) => c is not null && c.CastFrom == Zone.Hand && c.Controller == x.Controller,
            "ChosenCard" => (c, pl, x) => c is not null && (x.Host.ChosenCards?.Contains(c.Id) ?? false),
            // The controller of the creature this Aura enchants (Dance of the Dead's upkeep).
            "EnchantedController" => (c, pl, x) => c is null && pl is not null && x.Host.AttachedTo >= 0 && pl == x.S[x.Host.AttachedTo].Controller,
            "YouDontCtrl" => (c, pl, x) => c is not null && c.Controller != x.Controller,
            "Opponent" => (c, pl, x) => pl is not null && pl != x.Controller,
            "You" => (c, pl, x) => pl == x.Controller,
            "Self" or "StrictlySelf" => (c, pl, x) => c is not null && c.Id == x.Item.SourceId,
            "escaped" => (c, pl, x) => c is not null && c.Escaped,
            "TriggeredCard" => (c, pl, x) => c is not null && c.Id == x.Item.TriggeredCardId,
            // "Player.OpponentOf Remembered": an opponent of any of those players.
            _ when p.StartsWith("OpponentOf ", StringComparison.Ordinal) && Defined.IsKnownPlayerRef(p[11..]) =>
                (c, pl, x) => pl is { } who && Defined.Players(p[11..], x) is var of && of.Count > 0 && of.All(o => o != who),
            _ when p.StartsWith("ChosenMode", StringComparison.Ordinal) && p.Length > 10 => (c, pl, x) => c is not null && c.ChosenMode == p[10..],
            "kicked" => (c, pl, x) => c is not null && (c.PaidOptional?.Any(o => o.StartsWith("Kicker", StringComparison.Ordinal)) ?? false),
            "Other" or "StrictlyOther" => (c, pl, x) => c is null || c.Id != x.Item.SourceId,
            "token" => (c, pl, x) => c is not null && c.IsToken,
            "tapped" => (c, pl, x) => c is not null && c.Tapped,
            "attacking" => (c, pl, x) => c is not null && x.S.Attackers.Contains(c.Id),
            "untapped" => (c, pl, x) => c is not null && !c.Tapped,
            "IsCommander" => (c, pl, x) => c is not null && c.IsCommander,
            "NamedCard" => (c, pl, x) => c is not null && x.Host.NamedCard is { } n && string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase),
            "isMonarch" => (c, pl, x) => pl is not null && x.S.Monarch == pl,
            "IsRemembered" => (c, pl, x) => c is not null ? x.Host.Remembered?.Contains(c.Id) ?? false : pl is not null && pl == x.Item.IteratedPlayer,
            "IsTriggerRemembered" => (c, pl, x) => c is not null && (x.Item.DelayRemembered?.Contains(c.Id) ?? false),
            "ThisTurnEntered" => (c, pl, x) => c is not null && c.Zone == Zone.Battlefield && c.EnteredTurn == x.S.Turn,
            "Historic" => (c, pl, x) => c is not null && (c.Types.Has(SuperType.Legendary) || c.Types.Is(CardType.Artifact) || c.Types.HasSubtype("Saga")),
            "ChosenColor" => (c, pl, x) => c is not null && x.Host.ChosenColor is { } cc && (c.Colors & cc.Mask()) != 0,
            "ChosenType" => (c, pl, x) => c is not null && x.Host.ChosenType is { } ct
                && (c.Types.HasSubtype(ct) || CardTypes.TryGetValue(ct, out var chosenCardType) && c.Types.Is(chosenCardType)),
            "IsImprinted" => (c, pl, x) => c is not null && (x.Host.Imprinted?.Contains(c.Id) ?? false),
            // "Shares a creature type with your commander" (Path of Ancestry).
            "sharesCreatureTypeWith Commander" => (c, pl, x) => c is not null && x.S.Cards.Any(k => k.IsCommander && k.Owner == x.Controller && SharesCreatureType(c.Types, k.Types)),
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
            "ExiledWithSource" => (c, pl, x) => c is not null && c.Zone == Zone.Exile && c.ExiledWith == x.Item.SourceId,
            "ExiledWithSourceLKI" => (c, pl, x) => c is not null && c.ExiledWith == x.Item.SourceId,
            _ => null,
        };
        // "Creature.ControlledBy TriggeredTarget": what the player this triggered on ("that player") controls or owns.
        if (test is null && TriggeredPlayerProp(p) is { } byWhom)
            test = byWhom == "ControlledBy"
                ? (c, pl, x) => c is not null && x.Item.TriggeredPlayer is { } tp && c.Controller == tp
                : (c, pl, x) => c is not null && x.Item.TriggeredPlayer is { } tp && c.Owner == tp;
        // "namedRite of Flame": cards with that name.
        if (test is null && p.StartsWith("named", StringComparison.Ordinal) && p.Length > 5)
        {
            var name = p[5..];
            test = (c, pl, x) => c is not null && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase);
        }

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
            test = (c, pl, x) => c is not null && Compare(c.LastKnownCounterCount(kind), op, n);
        }

        // "HasCardsInHand_Card_EQ0": a player with that many such cards there.
        if (test is null && HasCardsRx.Match(p) is { Success: true } hm)
        {
            var inZone = Enum.Parse<Zone>(hm.Groups[1].Value);
            var cards = Parse(hm.Groups[2].Value, ctx);
            var op = hm.Groups[3].Value;
            var n = int.Parse(hm.Groups[4].Value);
            test = (c, pl, x) => c is null && pl is { } who && Compare(cards.CountIn(new[] { inZone }, x, who), op, n);
        }

        if (test is null)
        {
            ctx.Need("FilterProp:" + p);
            return null;
        }
        Func<int, EffectContext, bool>? owner = p switch
        {
            "YouCtrl" or "YouOwn" => (o, x) => o == x.Controller,
            "YouDontOwn" => (o, x) => o != x.Controller,
            "OppCtrl" or "OppOwn" => (o, x) => o != x.Controller,
            "ActivePlayerCtrl" => (o, x) => o == x.S.ActivePlayer,
            "ChosenCtrl" => (o, x) => o == x.Host.ChosenPlayer,
            // An unknown card is a color only if every card is; then it shares it with any other remembered card.
            "SharesColorWithOther Remembered" => (o, x) => Statics.EverywhereColors(x.Game) != 0 && (x.Host.Remembered?.Count ?? 0) + x.Host.RememberedUnknown >= 2,
            _ when TriggeredPlayerProp(p) is not null => (o, x) => o == x.Item.TriggeredPlayer,
            _ => null,
        };
        return new Prop(neg, test, owner);
    }

    /// <summary>"ControlledBy TriggeredTarget", "OwnedBy Spawner>TriggeredTarget": "ControlledBy" or "OwnedBy", else null.</summary>
    private static string? TriggeredPlayerProp(string p) =>
        p.Split(' ') is [var by and ("ControlledBy" or "OwnedBy"), "TriggeredTarget" or "TriggeredPlayer" or "Spawner>TriggeredTarget"] ? by : null;

    private static bool SharesCreatureType(TypeLine a, TypeLine b) =>
        a.AllCreatureTypes && (b.AllCreatureTypes || b.Subtypes.Any(TypeLine.IsCreatureType))
        || a.Subtypes.Any(s => TypeLine.IsCreatureType(s) && b.HasSubtype(s));

    public static bool Compare(int a, string op, int b) => op switch
    {
        "LE" => a <= b, "LT" => a < b, "GE" => a >= b, "GT" => a > b, "EQ" => a == b, "NE" => a != b,
        _ => false,
    };

    private static string SplitCamel(string s) => Regex.Replace(s, "(?<=[a-z])([A-Z])", " $1");

    /// <summary>
    /// Whether a card nobody can see fits: one in a goldfish opponent's hand, which is only a count. It fits a "Card"
    /// filter about whose it is ("Card.OppOwn"), never one about what it is (a type, a cost).
    /// </summary>
    /// <summary>An opponent of the controller, as the owner of unknown cards (they're only ever from goldfish libraries and hands).</summary>
    public static int SomeOpponent(EffectContext x) => x.S.Players.FirstOrDefault(p => p.Index != x.Controller)?.Index ?? -1;

    public bool MatchesUnknownCard(int owner, EffectContext x) =>
        _terms.Any(t => t.Base == "Card" && t.Props.All(p => p.OwnerTest is { } o && o(owner, x) != p.Negated));

    /// <summary>How many cards in these zones fit (only <paramref name="player"/>'s, if given), counting goldfish opponents' hands.</summary>
    public int CountIn(IReadOnlyCollection<Zone> zones, EffectContext x, int? player = null)
    {
        var n = 0;
        foreach (var z in zones)
            foreach (var c in x.Game.CardsIn(z))
                if ((player is null || (z == Zone.Battlefield ? c.Controller : c.Owner) == player) && MatchesCard(c, x)) n++;
        if (zones.Contains(Zone.Hand))
            foreach (var p in x.S.Players)
                if (p.IsGoldfish && (player is null || p.Index == player) && MatchesUnknownCard(p.Index, x)) n += p.AbstractHand;
        return n;
    }

    public bool MatchesCard(Card c, EffectContext x) => _terms.Any(t => BaseMatchesCard(t.Base, c) && t.Props.All(p => p.Test(c, null, x) != p.Negated));

    public bool MatchesPlayer(int player, EffectContext x) => _terms.Any(t => BaseMatchesPlayer(t.Base, player, x) && t.Props.All(p => p.Test(null, player, x) != p.Negated));

    public bool CanTargetPlayers => _terms.Any(t => t.Base is "Player" or "Opponent" or "You" or "Any");
    public bool CanTargetCards => _terms.Any(t => t.Base is not ("Player" or "Opponent" or "You"));

    private static bool BaseMatchesCard(string b, Card c) => b switch
    {
        "Card" or "Spell" => true,
        // Emblems aren't cards, and their abilities never go on the stack here.
        "Emblem" => false,
        // An effect in the command zone ("Count$ValidCommand Effect.YouCtrl+namedApproachingSuns").
        "Effect" => c.IsEffect,
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

    private static readonly Regex MathRx = new(@"/(Plus|NMinus|Minus|Times|Twice|Thrice|HalfUp|HalfDown|Negative|LimitMax|LimitMin)(?:\.(\-?\w+))?", RegexOptions.Compiled);

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
                "NMinus" => new(body, x => arg.Eval(x) - prev.Eval(x)),   // "/NMinus.3": 3 minus it
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
        // The number a delayed trigger remembers (Mana Drain).
        if (core == "Count$TriggerRememberAmount") return new(core, x => x.Item.DelayNumber);
        if (DefinedCardsRx.Match(core) is { Success: true } dm) return DefinedCardsAmount(core, dm, ctx);
        if (PlayerCountRx.Match(core) is { Success: true } pm) return PlayerCountAmount(core, pm, ctx);
        if (DefinedPlayerRx.Match(core) is { Success: true } dp)
        {
            var prop = PlayerProperty(dp.Groups[2].Value, ctx);
            if (prop is null) return Const(0);
            return dp.Groups[1].Value switch
            {
                "You" or "PlayerCountPropertyYou" => new(core, x => prop(x.Controller, x)),
                "TriggeredTarget" or "TriggeredPlayer" => new(core, x => x.Item.TriggeredPlayer is { } tp ? prop(tp, x) : 0),
                _ => new(core, x => Defined.Players("Targeted", x) is [var first, ..] ? prop(first, x) : 0),
            };
        }
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
        // "Count$Kicked.2.1": one number if it was kicked (bargained), another if not.
        if ((kind.StartsWith("Kicked.", StringComparison.Ordinal) || kind.StartsWith("Bargained.", StringComparison.Ordinal)) && kind.Split('.') is [var which, var k1, var k0]
            && int.TryParse(k1, out var paidValue) && int.TryParse(k0, out var unpaidValue))
        {
            var label = which == "Kicked" ? "Kicker" : "Bargain";
            return new(core, x => x.Host.PaidOptional?.Contains(label) ?? false ? paidValue : unpaidValue);
        }
        // "Count$Revolt.4.2": one number if a permanent you controlled left the battlefield this turn, another if not.
        if (kind.StartsWith("Revolt.", StringComparison.Ordinal) && kind.Split('.') is [_, var r1, var r0]
            && int.TryParse(r1, out var revoltValue) && int.TryParse(r0, out var noRevoltValue))
            return new(core, x => x.S.ThisTurn.PermanentLeft.Contains(x.Controller) ? revoltValue : noRevoltValue);
        // "Count$Monarch.7.2": one number if you're the monarch, another if not.
        if (kind.StartsWith("Monarch.", StringComparison.Ordinal) && kind.Split('.') is [_, var m1, var m0]
            && int.TryParse(m1, out var monarchValue) && int.TryParse(m0, out var notMonarchValue))
            return new(core, x => x.S.Monarch == x.Controller ? monarchValue : notMonarchValue);
        // "Count$Blessing.1.0": one number with the city's blessing, another without.
        if (kind.StartsWith("Blessing.", StringComparison.Ordinal) && kind.Split('.') is [_, var b1, var b0]
            && int.TryParse(b1, out var blessed) && int.TryParse(b0, out var unblessed))
            return new(core, x => x.S.Players[x.Controller].CityBlessing ? blessed : unblessed);
        // "Count$YourCountersExperience": counters of a kind on you.
        if (kind.StartsWith("YourCounters", StringComparison.Ordinal) && kind.Length > 12)
        {
            var playerCounter = kind[12..];
            if (playerCounter == "Poison") return new(core, x => x.S.Players[x.Controller].Poison);
            return new(core, x => x.S.Players[x.Controller].CounterCount(playerCounter));
        }
        // "Count$wasCastFromGraveyard.2.1": one number if this spell was cast from a graveyard, another if not.
        if (kind.StartsWith("wasCastFromGraveyard.", StringComparison.Ordinal) && kind.Split('.') is [_, var g1, var g0]
            && int.TryParse(g1, out var fromGrave) && int.TryParse(g0, out var notFromGrave))
            return new(core, x => x.Host.CastFrom == Zone.Graveyard ? fromGrave : notFromGrave);
        if (kind == "TimesKicked") return new(core, x => x.Host.PaidOptional?.Count(o => o.StartsWith("Kicker", StringComparison.Ordinal)) ?? 0);
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
            // "Domain": basic land types among lands you control.
            case "Domain": return new(core, x => TypeLine.BasicLandTypes.Count(t => x.S.BattlefieldCards.Any(c => c.Controller == x.Controller && c.Types.Is(CardType.Land) && c.Types.HasSubtype(t))));
            // "The damage dealt to your opponents this turn."
            case "BloodthirstAmount": return new(core, x => x.S.ThisTurn.DamageTaken.Where(kv => kv.Key != x.Controller).Sum(kv => kv.Value));
            case "YourLifeTotal": return new(core, x => x.S.Players[x.Controller].Life);
            case "YourStartingLife": return new(core, x => x.Game.Settings.StartingLife);
            case "RememberedSize": return new(core, x => (x.Host.Remembered?.Count ?? 0) + x.Host.RememberedUnknown);
            case "ThisTurnCast" when arg.Length == 0: return new(core, x => x.S.ThisTurn.SpellsCast);
            // "Raid": how many creatures attacked this turn.
            case "AttackersDeclared": return new(core, x => x.S.ThisTurn.AttackersDeclared);
            case "StormCount": return new(core, x => Math.Max(0, x.S.ThisTurn.SpellsCast - 1));
            case "InYourLibrary": return new(core, x => x.S.Players[x.Controller].Library.Count);
            case "Converge": return new(core, x => x.Host.ColorsSpent);
            // "You gain life equal to the damage dealt this way."
            case "TotalDamageDoneByThisTurn": return new(core, x => x.S.ThisTurn.DamageDealtBy.GetValueOrDefault(x.Host.Id));
        }
        // "Count$ThisTurnCast_Card.OppCtrl+Blue,Card.OppCtrl+Black": spells cast this turn that fit.
        if (kind.StartsWith("ThisTurnCast_", StringComparison.Ordinal))
        {
            var cast = Filter.Parse(kind[13..], ctx);
            return new(core, x => x.S.ThisTurn.Cast.Count(id => cast.MatchesCard(x.S[id], x)));
        }
        // "Count$ThisTurnEntered_Graveyard_from_Battlefield_Creature.YouCtrl": cards that moved there this turn (a creature died).
        if (kind.StartsWith("ThisTurnEntered_", StringComparison.Ordinal))
        {
            var parts = kind[16..].Split('_');
            if (parts.Length >= 2 && Enum.TryParse<Zone>(parts[0], out var into))
            {
                Zone? fromZone = null;
                var tail = parts.Skip(1).ToList();
                if (tail is ["from", var fz, ..] && Enum.TryParse<Zone>(fz, out var fromParsed)) { fromZone = fromParsed; tail = tail.Skip(2).ToList(); }
                var filterText = string.Join('_', tail) + (arg.Length > 0 ? " " + arg : "");
                if (filterText.Length > 0)
                {
                    var moved = Filter.Parse(filterText, ctx);
                    return new(core, x => x.S.ThisTurn.Moves.Count(m => m.To == into && (fromZone is null || m.From == fromZone) && moved.MatchesCard(x.S[m.CardId], x)));
                }
            }
        }
        if (kind == "Devotion.Chosen")
            return new(core, x => x.Host.ChosenColor is { } chosen && chosen != ManaColor.C ? Devotion(x, chosen.Mask()) : 0);
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
            "Command" => Zone.Command,
            "Stack" => Zone.Stack,
            _ => Zone.None,
        }).ToList() : null;
        if (zones is not null && !zones.Contains(Zone.None) && arg.Length > 0)
        {
            var dollar = arg.IndexOf('$');
            var filter = Filter.Parse(dollar < 0 ? arg : arg[..dollar], ctx);
            IEnumerable<Card> Matching(EffectContext x) => zones.SelectMany(z => x.Game.CardsIn(z)).Where(card => filter.MatchesCard(card, x));
            switch (dollar < 0 ? null : arg[(dollar + 1)..])
            {
                case null: return new(core, x => filter.CountIn(zones, x));
                case "GreatestCardPower": return new(core, x => Matching(x).Select(card => card.Power).DefaultIfEmpty(0).Max());
                case "GreatestCardToughness": return new(core, x => Matching(x).Select(card => card.Toughness).DefaultIfEmpty(0).Max());
                case "GreatestCMC": return new(core, x => Matching(x).Select(card => card.CurrentFace.ManaCost.ManaValue).DefaultIfEmpty(0).Max());
                case "CardPower": return new(core, x => Matching(x).Sum(card => card.Power));
                case "CardToughness": return new(core, x => Matching(x).Sum(card => card.Toughness));
                case "SumCMC": return new(core, x => Matching(x).Sum(card => card.CurrentFace.ManaCost.ManaValue));
                case "DifferentCardNames": return new(core, x => Matching(x).Select(card => card.Name).Distinct(StringComparer.Ordinal).Count());
                default: ctx.Need("Count:" + kind + "$" + arg[(dollar + 1)..]); return Const(0);
            }
        }
        ctx.Need("Count:" + kind);
        return Const(0);
    }

    private static readonly Regex PlayerCountRx = new(@"^PlayerCount(Players|Opponents|RegisteredOpponents)\$(Amount|Highest|Lowest)?(.*)$", RegexOptions.Compiled);
    private static readonly Regex DefinedPlayerRx = new(@"^(TargetedPlayer|You|TriggeredTarget|TriggeredPlayer|PlayerCountPropertyYou)\$(.+)$", RegexOptions.Compiled);

    /// <summary>"PlayerCountOpponents$Amount", "PlayerCountPlayers$HighestValidGraveyard Card.YouOwn": over a group of players, how many, the most, the least, or the total.</summary>
    private static Amount PlayerCountAmount(string core, Match m, CompileContext ctx)
    {
        var opponents = m.Groups[1].Value is "Opponents" or "RegisteredOpponents";
        IEnumerable<int> Players(EffectContext x) => x.S.Players.Where(p => !p.Lost && (!opponents || p.Index != x.Controller)).Select(p => p.Index);
        if (m.Groups[2].Value == "Amount")
        {
            if (m.Groups[3].Value.Length > 0) ctx.Need("Amount:" + core);
            return new(core, x => Players(x).Count());
        }
        var prop = PlayerProperty(m.Groups[3].Value, ctx);
        if (prop is null) return Const(0);
        return m.Groups[2].Value switch
        {
            "Highest" => new(core, x => Players(x).Select(p => prop(p, x)).DefaultIfEmpty(0).Max()),
            "Lowest" => new(core, x => Players(x).Select(p => prop(p, x)).DefaultIfEmpty(0).Min()),
            _ => new(core, x => Players(x).Sum(p => prop(p, x))),
        };
    }

    /// <summary>A number about one player: "CardsInHand", "LifeTotal", "ValidGraveyard Card.YouOwn" (with "you" being that player).</summary>
    private static Func<int, EffectContext, int>? PlayerProperty(string text, CompileContext ctx)
    {
        switch (text)
        {
            case "CardsInHand": return (p, x) => x.S.Players[p].HandCount;
            case "CardsInLibrary": return (p, x) => x.S.Players[p].LibraryCount;
            case "CardsInGraveyard": return (p, x) => x.S.Players[p].Graveyard.Count;
            case "LifeTotal": return (p, x) => x.S.Players[p].Life;
            case "PoisonCounters" or "Counters.Poison": return (p, x) => x.S.Players[p].Poison;
            case "LifeLostThisTurn": return (p, x) => x.S.ThisTurn.LifeLost.GetValueOrDefault(p);
            case "CardsDiscardedThisTurn": return (p, x) => x.S.ThisTurn.CardsDiscarded.GetValueOrDefault(p);
            case "LandsPlayed": return (p, x) => x.S.Players[p].LandsPlayedThisTurn;
        }
        var space = text.IndexOf(' ');
        if (text.StartsWith("Valid", StringComparison.Ordinal) && space > 0)
        {
            var zones = new List<Zone>();
            foreach (var w in text[5..space].Split(','))
                if (w is "" or "Battlefield") zones.Add(Zone.Battlefield);
                else if (Enum.TryParse<Zone>(w, out var z) && z is Zone.Graveyard or Zone.Hand or Zone.Library or Zone.Exile) zones.Add(z);
                else { ctx.Need("PlayerProperty:" + text); return null; }
            var filter = Filter.Parse(text[(space + 1)..], ctx);
            return (p, x) =>
            {
                var asPlayer = x.Controller == p ? x : new EffectContext
                {
                    Game = x.Game, Ability = x.Ability, FaceOverride = x.FaceOverride, ReplacedAmount = x.ReplacedAmount,
                    Item = new StackItem { SourceId = x.Item.SourceId, Ability = x.Item.Ability, Controller = p, X = x.Item.X, IteratedPlayer = x.Item.IteratedPlayer },
                };
                return filter.CountIn(zones, asPlayer);
            };
        }
        ctx.Need("PlayerProperty:" + text);
        return null;
    }

    private static readonly Regex DefinedCardsRx = new(@"^(Targeted|TargetedObjects|TriggeredCard|TriggeredSpellAbility|TriggeredSource|Remembered|RememberedLKI|Imprinted|Sacrificed|Exiled)\$(CardPower|CardToughness|CardManaCost|CardManaCostLKI|Amount|Valid|CardCounters\.\w+)(?: (.+))?$", RegexOptions.Compiled);

    /// <summary>"Targeted$CardPower", "Remembered$CardManaCost", "Imprinted$Valid Card.X": a property of defined cards (summed), or how many there are.</summary>
    private static Amount DefinedCardsAmount(string core, Match m, CompileContext ctx)
    {
        var which = m.Groups[1].Value;
        var prop = m.Groups[2].Value;
        var filter = prop == "Valid" ? Filter.Parse(m.Groups[3].Value, ctx) : null;
        if (prop != "Valid" && m.Groups[3].Success) ctx.Need("Amount:" + core);
        // "TargetedObjects$Amount": how many targets, players included.
        if (which == "TargetedObjects")
        {
            if (prop != "Amount") ctx.Need("Amount:" + core);
            return new(core, x => x.OwnOrParentTargets.Count);
        }
        List<Card> Cards(EffectContext x)
        {
            IEnumerable<int> ids = which switch
            {
                "Targeted" => x.OwnOrParentTargets.Where(t => t.CardId is not null).Select(t => t.CardId!.Value),
                "TriggeredCard" or "TriggeredSpellAbility" => x.Item.TriggeredCardId is { } id ? new[] { id } : Array.Empty<int>(),
                "TriggeredSource" => x.Item.TriggeredSourceId is { } sid ? new[] { sid } : Array.Empty<int>(),
                "Remembered" or "RememberedLKI" => x.Host.Remembered ?? new List<int>(),
                "Sacrificed" => x.Item.Sacrificed ?? new List<int>(),
                "Exiled" => x.Item.Exiled ?? new List<int>(),
                _ => x.Host.Imprinted ?? new List<int>(),
            };
            return ids.Select(id => x.S[id]).ToList();
        }
        return prop switch
        {
            _ when prop.StartsWith("CardCounters.", StringComparison.Ordinal) => new(core, x => Cards(x).Sum(c => c.CounterCount(prop[13..]))),
            "CardPower" => new(core, x => Cards(x).Sum(c => c.LastKnownPower)),
            "CardToughness" => new(core, x => Cards(x).Sum(c => c.LastKnownToughness)),
            "CardManaCost" or "CardManaCostLKI" => new(core, x => Cards(x).Sum(c => c.CurrentFace.ManaCost.ManaValue)),
            "Amount" => new(core, x => Cards(x).Count + (which is "Remembered" or "RememberedLKI" ? x.Host.RememberedUnknown : 0)),
            _ => new(core, x => Cards(x).Count(c => filter!.MatchesCard(c, x))
                + (which is "Remembered" or "RememberedLKI" && x.Host.RememberedUnknown > 0 && filter!.MatchesUnknownCard(Filter.SomeOpponent(x), x) ? x.Host.RememberedUnknown : 0)),
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
        "TriggeredCardOwner", "TriggeredSourceController", "TriggeredDefendingPlayer", "Spawner>TriggeredTarget",
        "RememberedOwner", "RememberedController", "DelayTriggerRemembered", "OppNonRememberedOwner", "TriggeredAttackerController",
    };

    private static readonly HashSet<string> CardRefs = new(StringComparer.Ordinal)
    {
        "Self", "Targeted", "ParentTarget", "Remembered", "TriggeredCard", "TriggeredCardLKICopy", "TriggeredNewCardLKICopy",
        "Enchanted", "Equipped", "Imprinted", "RememberedLKI", "RememberedCard", "Sacrificed", "SacrificedCards",
        "TriggeredAttacker", "TriggeredAttackerLKICopy", "TriggeredSource", "TriggeredSourceLKICopy", "DelayTriggerRemembered", "DelayTriggerRememberedLKI",
        "ReplacedCard", "TopOfLibrary", "TriggeredObject", "TriggeredObjectLKICopy", "ChosenCard", "Parent", "DirectRemembered",
    };

    /// <summary>Whether a Defined$ names players ("You", "Player.Opponent") rather than cards.</summary>
    /// <summary>Any player reference this file resolves, including ones that can also mean cards ("Remembered").</summary>
    public static bool IsKnownPlayerRef(string text) => PlayerRefs.Contains(text);

    public static bool IsPlayerRef(string text) =>
        text is not ("Targeted" or "ParentTarget" or "Remembered" or "DelayTriggerRemembered") && PlayerRefs.Contains(text) || text.StartsWith("Player.", StringComparison.Ordinal);

    public static void ValidatePlayers(string? text, CompileContext ctx)
    {
        if (text is null || PlayerRefs.Contains(text)) return;
        // "Player.Opponent+HasCardsInHand_Card_EQ0": every player that fits.
        if (text.StartsWith("Player.", StringComparison.Ordinal)) Filter.Parse(text, ctx);
        else ctx.Need("DefinedPlayer:" + text);
    }

    public static void ValidateCards(string? text, CompileContext ctx)
    {
        // "You & Valid Permanent.YouCtrl": each part; players among them aren't cards, so they add none.
        if (text is not null && text.Contains(" & ", StringComparison.Ordinal))
        {
            foreach (var part in text.Split(" & "))
                if (!IsPlayerRef(part)) ValidateCards(part, ctx);
            return;
        }
        // "Valid Creature.YouCtrl": every permanent matching the filter.
        if (text is not null && text.StartsWith("Valid ", StringComparison.Ordinal)) { Filter.Parse(text[6..], ctx); return; }
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
            "TriggeredCardController" or "TriggeredAttackerController" => x.Item.TriggeredCardId is { } tc ? new() { x.S[tc].Controller } : new(),
            "TriggeredCardOwner" => x.Item.TriggeredCardId is { } tco ? new() { x.S[tco].Owner } : new(),
            "TriggeredSourceController" => x.Item.TriggeredSourceId is { } ts ? new() { x.S[ts].Controller } : new(),
            "ChosenPlayer" => x.Host.ChosenPlayer is { } cp ? new() { cp } : new(),
            "Remembered" => x.Item.IteratedPlayer is { } ip ? new() { ip } : x.Host.RememberedPlayers is { } rp ? new(rp) : new(),
            "RememberedOwner" => (x.Host.Remembered ?? new()).Select(id => x.S[id].Owner).Distinct().ToList(),
            // "Each opponent who didn't [put a land onto the battlefield]": opponents who own none of the remembered cards.
            "OppNonRememberedOwner" => players.Where(p => !p.Lost && p.Index != x.Controller && !(x.Host.Remembered ?? new()).Any(id => x.S[id].Owner == p.Index))
                .Select(p => p.Index).ToList(),
            "RememberedController" => (x.Host.Remembered ?? new()).Select(id => x.S[id].Controller).Distinct().ToList(),
            // A delayed trigger's players, else the controllers of the cards it remembers.
            "DelayTriggerRemembered" => x.Item.DelayPlayers?.ToList() ?? (x.Item.DelayRemembered ?? new()).Select(id => x.S[id].Controller).Distinct().ToList(),
            "TargetedOwner" => x.OwnOrParentTargets.Where(t => t.CardId is not null).Select(t => x.S[t.CardId!.Value].Owner).Take(1).ToList(),
            "TriggeredTarget" or "TriggeredPlayer" or "TriggeredActivator" or "TriggeredDefendingPlayer" or "Spawner>TriggeredTarget" => x.Item.TriggeredPlayer is { } tp ? new() { tp } : new(),
            var f when f.StartsWith("Player.", StringComparison.Ordinal) =>
                players.Where(p => !p.Lost && Effect.FilterFor(x.SVarFace, f).MatchesPlayer(p.Index, x)).Select(p => p.Index).ToList(),
            _ => new() { x.Controller },
        };
    }

    public static List<int> Cards(string? text, EffectContext x, string fallback = "Self")
    {
        if (text is not null && text.Contains(" & ", StringComparison.Ordinal))
            return text.Split(" & ").Where(part => !IsPlayerRef(part)).SelectMany(part => Cards(part, x)).Distinct().ToList();
        if (text is not null && text.StartsWith("Valid ", StringComparison.Ordinal))
        {
            var filter = Effect.FilterFor(x.SVarFace, text[6..]);
            return x.S.BattlefieldCards.Where(c => filter.MatchesCard(c, x)).Select(c => c.Id).ToList();
        }
        return (text ?? fallback) switch
        {
            "Self" or "Parent" => new() { x.Item.SourceId },
            "Targeted" => x.OwnOrParentTargets.Where(t => t.CardId is not null).Select(t => t.CardId!.Value).ToList(),
            "ParentTarget" => x.ParentTargets.Where(t => t.CardId is not null).Select(t => t.CardId!.Value).ToList(),
            "Remembered" or "RememberedLKI" or "RememberedCard" => x.Host.Remembered?.ToList() ?? new(),
            "DelayTriggerRemembered" or "DelayTriggerRememberedLKI" => x.Item.DelayRemembered?.ToList() ?? new(),
            // What this object itself remembers: a delayed trigger's own objects, else its card's.
            "DirectRemembered" => (x.Item.DelayRemembered ?? x.Host.Remembered)?.ToList() ?? new(),
            "TriggeredCard" or "TriggeredCardLKICopy" or "TriggeredNewCardLKICopy" or "TriggeredAttacker" or "TriggeredAttackerLKICopy" =>
                x.Item.TriggeredCardId is { } id ? new() { id } : new(),
            "TriggeredSource" or "TriggeredSourceLKICopy" => x.Item.TriggeredSourceId is { } ts ? new() { ts } : new(),
            "Enchanted" or "Equipped" => x.Host.AttachedTo >= 0 ? new() { x.Host.AttachedTo } : new(),
            "Imprinted" => x.Host.Imprinted?.ToList() ?? new(),
            "TriggeredObject" or "TriggeredObjectLKICopy" => x.Item.TriggeredObjectId is { } obj ? new() { obj } : new(),
            "ChosenCard" => x.Host.ChosenCards?.ToList() ?? new(),
            "Sacrificed" or "SacrificedCards" => x.Item.Sacrificed?.ToList() ?? new(),
            // The card a replacement effect is replacing the event of: its own "as this enters" (Mox Diamond).
            "ReplacedCard" => new() { x.Item.SourceId },
            "TopOfLibrary" => x.S.Players[x.Controller].Library.Take(1).ToList(),
            _ => new(),
        };
    }
}
