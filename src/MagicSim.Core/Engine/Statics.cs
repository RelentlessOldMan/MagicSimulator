using MagicSim.Core.Forge;

namespace MagicSim.Core.Engine;

/// <summary>
/// "Only if" checks shared by statics, triggers and cost reductions: IsPresent$ (with
/// PresentCompare$/PresentZone$/PresentDefined$), CheckSVar$ (with SVarCompare$) and Condition$.
/// </summary>
public static class Presence
{
    public static readonly HashSet<string> Params = new(StringComparer.Ordinal)
    {
        "IsPresent", "PresentCompare", "PresentZone", "PresentDefined", "CheckSVar", "SVarCompare", "Condition",
    };

    private static readonly HashSet<string> KnownConditions = new(StringComparer.Ordinal)
    {
        "PlayerTurn", "NotPlayerTurn", "Threshold", "Hellbent", "Metalcraft", "Delirium",
    };

    public static void Validate(ForgeParams p, CompileContext ctx)
    {
        if (p["IsPresent"] is { } present) Filter.Parse(present, ctx);
        if (p["PresentCompare"] is { } pc) ValidateCompare(pc, ctx);
        if (p["PresentZone"] is { } pz && !pz.Split(',').All(z => z.Trim() is "Battlefield" or "Graveyard" or "Hand" or "Exile" or "Library"))
            ctx.Need("PresentZone:" + pz);
        if (p["PresentDefined"] is { } pd && pd != "Self") ctx.Need("PresentDefined:" + pd);
        if (p["CheckSVar"] is { } sv)
        {
            Amount.Parse(sv, ctx);
            ValidateCompare(p["SVarCompare"] ?? "GE1", ctx);
        }
        if (p["Condition"] is { } cond && !KnownConditions.Contains(cond)) ctx.Need("Condition:" + cond);
    }

    private static void ValidateCompare(string cmp, CompileContext ctx)
    {
        if (cmp.Length < 3) ctx.Need("Compare:" + cmp);
        else Amount.Parse(cmp[2..], ctx);
    }

    public static bool Hold(ForgeParams p, EffectContext x)
    {
        var g = x.Game;
        var you = x.Controller;
        if (p["IsPresent"] is { } present)
        {
            var filter = Effect.FilterFor(x.Host.CurrentFace, present);
            IEnumerable<Card> pool = p["PresentDefined"] == "Self"
                ? new[] { x.Host }
                : (p["PresentZone"] ?? "Battlefield").Split(',').SelectMany(z => g.CardsIn(Enum.Parse<Zone>(z.Trim())));
            var count = pool.Count(c => filter.MatchesCard(c, x));
            if (!CompareTo(count, p["PresentCompare"] ?? "GE1", x)) return false;
        }
        if (p["CheckSVar"] is { } sv && !CompareTo(Effect.EvalAmount(x, sv), p["SVarCompare"] ?? "GE1", x)) return false;
        if (p["Condition"] is { } cond)
        {
            var me = g.State.Players[you];
            var ok = cond switch
            {
                "PlayerTurn" => g.State.ActivePlayer == you,
                "NotPlayerTurn" => g.State.ActivePlayer != you,
                "Threshold" => me.Graveyard.Count >= 7,
                "Hellbent" => me.Hand.Count == 0,
                "Metalcraft" => g.State.BattlefieldCards.Count(c => c.Controller == you && c.Types.Is(CardType.Artifact)) >= 3,
                "Delirium" => CardTypesIn(g, me.Graveyard) >= 4,
                _ => false,
            };
            if (!ok) return false;
        }
        return true;
    }

    private static bool CompareTo(int value, string cmp, EffectContext x) =>
        Filter.Compare(value, cmp[..2], Effect.EvalAmount(x, cmp[2..]));

    public static int CardTypesIn(Game g, IEnumerable<int> ids)
    {
        CardType all = 0;
        foreach (var id in ids) all |= g.State[id].Types.Types;
        return System.Numerics.BitOperations.PopCount((uint)all);
    }
}

/// <summary>
/// Static abilities (rule 604). Continuous effects are recomputed from scratch by
/// <see cref="Apply"/> whenever the engine needs current characteristics; cost reductions are
/// applied when a spell's cost is worked out. Modes that only restrict blocking do nothing
/// against goldfish opponents, who never block.
/// </summary>
public static class Statics
{
    private static readonly HashSet<string> NoEffectInGoldfish = new(StringComparer.Ordinal)
    {
        "CantBlockBy", "CantBlock", "MinMaxBlocker", "MustBlock", "CantBlockUnless", "BlockRestrict", "CanBlockIfShadow",
        "IgnoreLandWalk", "MustAttack",
    };

    private static readonly HashSet<string> CommonParams = new(StringComparer.Ordinal) { "Mode", "Description", "Secondary" };

    private static readonly HashSet<string> ContinuousParams = new(StringComparer.Ordinal)
    {
        "Affected", "AffectedDefined", "AddPower", "AddToughness", "AddKeyword", "SetPower", "SetToughness",
        "CharacteristicDefining", "EffectZone", "AffectedZone", "AdjustLandPlays", "SetMaxHandSize", "MayPlay", "MayPlayWithoutManaCost",
    };

    private static readonly HashSet<string> ReduceCostParams = new(StringComparer.Ordinal)
    {
        "Amount", "ValidCard", "Type", "EffectZone", "Activator",
    };

    private static readonly HashSet<string> AlternativeCostParams = new(StringComparer.Ordinal)
    {
        "ValidSA", "ValidCard", "Cost", "EffectZone", "ValidPlayer", "StackDescription", "CostDesc",
    };

    public static void Validate(StaticDef s, CompileContext ctx)
    {
        if (NoEffectInGoldfish.Contains(s.Mode)) return;
        // "Your opponents can't cast spells / activate abilities": goldfish opponents never do anyway.
        if (s.Mode is "CantBeCast" or "CantBeActivated" && (s["Caster"] ?? s["Activator"]) == "Opponent") return;
        HashSet<string> own;
        switch (s.Mode)
        {
            case "AlternativeCost":   // the compiler adds the alternative-cost spell abilities
                own = AlternativeCostParams;
                if (s["EffectZone"] is { } aez && aez != "All") ctx.Need("AlternativeCost.EffectZone:" + aez);
                if (s["ValidPlayer"] is { } vp && vp != "You") ctx.Need("AlternativeCost.ValidPlayer:" + vp);
                break;
            case "Continuous":
                own = ContinuousParams;
                ValidateContinuous(s, ctx);
                break;
            case "ReduceCost":
                own = ReduceCostParams;
                if (s["Type"] is { } type && type != "Spell") ctx.Need("ReduceCost.Type:" + type);
                if (s["Activator"] is { } act && act != "You") ctx.Need("ReduceCost.Activator:" + act);
                if (s["EffectZone"] is { } ez && ez is not ("All" or "Battlefield")) ctx.Need("EffectZone:" + ez);
                if (s["ValidCard"] is { } vc) Filter.Parse(vc, ctx);
                Amount.Parse(s["Amount"] ?? "1", ctx);
                break;
            default:
                ctx.Need("Static:" + s.Mode);
                return;
        }
        foreach (var key in s.P.Values.Keys)
            if (!CommonParams.Contains(key) && !own.Contains(key) && !Presence.Params.Contains(key))
                ctx.Need($"StaticParam:{s.Mode}.{key}");
        Presence.Validate(s.P, ctx);
    }

    private static void ValidateContinuous(StaticDef s, CompileContext ctx)
    {
        if (s["AffectedDefined"] is { } ad && ad is not ("Self" or "Enchanted" or "Equipped")) ctx.Need("AffectedDefined:" + ad);
        if (s["Affected"] is { } a) Filter.Parse(a, ctx);
        if (s["MayPlay"] is { } mp)
        {
            // "You may play/cast [cards] from [zone]": impulse draw, Future Sight, Bolas's Citadel-style.
            if (mp != "True") ctx.Need("MayPlay:" + mp);
            if (s["AffectedZone"] is not ("Exile" or "Graveyard" or "Library" or "Command")) ctx.Need("MayPlay.AffectedZone:" + s["AffectedZone"]);
            if (s.P.Values.Keys.Any(k => k is "AddPower" or "AddToughness" or "AddKeyword" or "SetPower" or "SetToughness")) ctx.Need("MayPlay+Modify");
        }
        else if (s["AffectedZone"] is { } az && az != "Battlefield") ctx.Need("AffectedZone:" + az);
        if (s["EffectZone"] is { } ez && ez is not ("Battlefield" or "All" or "Command")) ctx.Need("EffectZone:" + ez);
        foreach (var key in new[] { "AddPower", "AddToughness", "SetPower", "SetToughness", "AdjustLandPlays" })
            if (s[key] is { } v && v != "Unlimited") Amount.Parse(v, ctx);
        if (s["SetMaxHandSize"] is { } mh && mh != "Unlimited") Amount.Parse(mh, ctx);
        if (s["AddKeyword"] is { } kws)
            foreach (var k in kws.Split('&', StringSplitOptions.TrimEntries))
                if (!Keywords.Supported.Contains(ForgeRequirements.KeywordName(k))) ctx.Need("Keyword:" + ForgeRequirements.KeywordName(k));
    }

    private static EffectContext ContextFor(Game g, Card source) => new()
    {
        Game = g, Ability = Game.PermanentSpell,
        Item = new StackItem { SourceId = source.Id, Ability = Game.PermanentSpell, Controller = source.Controller },
    };

    /// <summary>Recomputes every continuous effect: characteristic-defining and "set" P/T first, then modifications (rule 613).</summary>
    public static void Apply(Game g)
    {
        var s = g.State;
        var active = new List<(Card Source, StaticDef Static, EffectContext X)>();
        foreach (var c in Sources(g))
            foreach (var st in c.CurrentFace.Statics)
                if (st.Mode == "Continuous" && st["MayPlay"] is null) active.Add((c, st, ContextFor(g, c)));
        if (active.Count == 0 && !g.StaticsApplied) return;
        g.StaticsApplied = active.Count > 0;

        foreach (var c in s.BattlefieldCards) c.ClearStaticEffects();
        foreach (var p in s.Players) { p.StaticLandDrops = 0; p.StaticMaxHandSize = null; }

        var holding = active.Where(a => Presence.Hold(a.Static.P, a.X)).ToList();
        // Layer 7a/7b: characteristic-defining abilities and effects that set power/toughness.
        foreach (var (source, st, x) in holding)
        {
            if (st["SetPower"] is null && st["SetToughness"] is null) continue;
            foreach (var card in AffectedCards(g, source, st, x))
            {
                var cx = ContextFor(g, st["CharacteristicDefining"] == "True" ? card : source);
                if (st["SetPower"] is { } sp) card.SetPower = Effect.EvalAmount(cx, sp);
                if (st["SetToughness"] is { } stt) card.SetToughness = Effect.EvalAmount(cx, stt);
            }
        }
        // Layer 6 (abilities) and 7c (modifications); players' rule changes.
        foreach (var (source, st, x) in holding)
        {
            var addPower = st["AddPower"] is { } ap ? Effect.EvalAmount(x, ap) : 0;
            var addToughness = st["AddToughness"] is { } at ? Effect.EvalAmount(x, at) : 0;
            var keywords = st["AddKeyword"]?.Split('&', StringSplitOptions.TrimEntries).Select(ForgeRequirements.KeywordName).ToList();
            if (addPower != 0 || addToughness != 0 || keywords is not null)
                foreach (var card in AffectedCards(g, source, st, x))
                {
                    card.StaticPower += addPower;
                    card.StaticToughness += addToughness;
                    if (keywords is null) continue;
                    card.StaticKeywords ??= new HashSet<string>(StringComparer.Ordinal);
                    card.StaticKeywords.UnionWith(keywords);
                }
            if (st["AdjustLandPlays"] is null && st["SetMaxHandSize"] is null) continue;
            foreach (var p in AffectedPlayers(g, st, x))
            {
                if (st["AdjustLandPlays"] is { } lp) p.StaticLandDrops += lp == "Unlimited" ? 1000 : Effect.EvalAmount(x, lp);
                if (st["SetMaxHandSize"] is { } mh) p.StaticMaxHandSize = mh == "Unlimited" ? int.MaxValue : Effect.EvalAmount(x, mh);
            }
        }
    }

    /// <summary>Where static abilities work from: the battlefield, plus effects and emblems in command zones.</summary>
    public static IEnumerable<Card> Sources(Game g)
    {
        foreach (var c in g.State.BattlefieldCards)
            if (!c.Def.IsBlank && c.CurrentFace.Statics.Count > 0) yield return c;
        foreach (var p in g.State.Players)
            foreach (var id in p.Command)
                if (g.State[id].IsEffect) yield return g.State[id];
    }

    /// <summary>Cards outside the hand that <paramref name="player"/> may play now, and whether without paying their mana cost.</summary>
    public static Dictionary<int, bool> MayPlay(Game g, int player)
    {
        var result = new Dictionary<int, bool>();
        foreach (var source in Sources(g))
        {
            if (source.Controller != player) continue;
            foreach (var st in source.CurrentFace.Statics)
            {
                if (st.Mode != "Continuous" || st["MayPlay"] != "True") continue;
                var x = ContextFor(g, source);
                if (!Presence.Hold(st.P, x)) continue;
                var zone = Enum.Parse<Zone>(st["AffectedZone"]!);
                var filter = st["Affected"] is { } a ? Effect.FilterFor(source.CurrentFace, a) : null;
                foreach (var c in g.CardsIn(zone))
                    if (c.Owner == player && !c.IsEffect && (filter is null || filter.MatchesCard(c, x)))
                        result[c.Id] = result.GetValueOrDefault(c.Id) || st["MayPlayWithoutManaCost"] == "True";
            }
        }
        return result;
    }

    private static IEnumerable<Card> AffectedCards(Game g, Card source, StaticDef st, EffectContext x)
    {
        var filter = st["Affected"] is { } a ? Effect.FilterFor(source.CurrentFace, a) : null;
        IEnumerable<Card> cards;
        switch (st["AffectedDefined"])
        {
            case "Self":
                cards = new[] { source };
                break;
            case "Enchanted" or "Equipped":
                cards = source.AttachedTo >= 0 && g.State[source.AttachedTo].Zone == Zone.Battlefield
                    ? new[] { g.State[source.AttachedTo] } : Array.Empty<Card>();
                break;
            default:
                if (filter is null)
                    return st["CharacteristicDefining"] == "True" ? new[] { source } : Array.Empty<Card>();
                cards = g.State.BattlefieldCards;
                break;
        }
        return cards.Where(c => filter is null || filter.MatchesCard(c, x)).ToList();
    }

    private static IEnumerable<Player> AffectedPlayers(Game g, StaticDef st, EffectContext x)
    {
        if (st["Affected"] is not { } a) return Array.Empty<Player>();
        var filter = Effect.FilterFor(x.Host.CurrentFace, a);
        return g.State.Players.Where(p => !p.Lost && filter.MatchesPlayer(p.Index, x));
    }

    /// <summary>Total generic-cost reduction from "spells cost {N} less" statics for casting <paramref name="spell"/>.</summary>
    public static int CostReduction(Game g, Card spell, int caster)
    {
        var total = 0;
        foreach (var source in g.State.BattlefieldCards.Append(spell))
        {
            if (source.Def.IsBlank) continue;
            var self = source.Id == spell.Id;
            foreach (var st in source.CurrentFace.Statics)
            {
                if (st.Mode != "ReduceCost") continue;
                // A card's own "this spell costs less" works from anywhere (EffectZone$ All); others from the battlefield.
                if (self != (st["EffectZone"] == "All")) continue;
                if (!self && source.Controller != caster) continue;
                var x = ContextFor(g, source);
                if (st["ValidCard"] is { } vc && !Effect.FilterFor(source.CurrentFace, vc).MatchesCard(spell, x)) continue;
                if (!Presence.Hold(st.P, x)) continue;
                total += Math.Max(0, Effect.EvalAmount(x, st["Amount"] ?? "1"));
            }
        }
        return total;
    }
}
