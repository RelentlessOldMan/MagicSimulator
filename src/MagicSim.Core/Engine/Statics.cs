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
        "PlayerTurn", "NotPlayerTurn", "Threshold", "Hellbent", "Metalcraft", "Delirium", "Kicked", "Bargain", "Revolt", "Blessing",
    };

    public static bool IsKnownCondition(string cond) => KnownConditions.Contains(cond);

    /// <summary>A named condition (Condition$, Activation$): Metalcraft, Threshold, "if it was kicked", …</summary>
    public static bool ConditionHolds(string cond, EffectContext x)
    {
        var g = x.Game;
        var you = x.Controller;
        var me = g.State.Players[you];
        return cond switch
        {
            "PlayerTurn" => g.State.ActivePlayer == you,
            "NotPlayerTurn" => g.State.ActivePlayer != you,
            "Threshold" => me.Graveyard.Count >= 7,
            "Hellbent" => me.Hand.Count == 0,
            "Metalcraft" => g.State.BattlefieldCards.Count(c => c.Controller == you && c.Types.Is(CardType.Artifact)) >= 3,
            "Delirium" => CardTypesIn(g, me.Graveyard) >= 4,
            "Kicked" => x.Host.PaidOptional?.Contains("Kicker") ?? false,
            "Bargain" => x.Host.PaidOptional?.Contains("Bargain") ?? false,
            // "If a permanent you controlled left the battlefield this turn."
            "Revolt" => g.State.ThisTurn.PermanentLeft.Contains(you),
            "Blessing" => me.CityBlessing,
            _ => false,
        };
    }

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
            var filter = Effect.FilterFor(x.SVarFace, present);
            IEnumerable<Card> pool = p["PresentDefined"] == "Self"
                ? new[] { x.Host }
                : (p["PresentZone"] ?? "Battlefield").Split(',').SelectMany(z => g.CardsIn(Enum.Parse<Zone>(z.Trim())));
            var count = pool.Count(c => filter.MatchesCard(c, x));
            if (!CompareTo(count, p["PresentCompare"] ?? "GE1", x)) return false;
        }
        if (p["CheckSVar"] is { } sv && !CompareTo(Effect.EvalAmount(x, sv), p["SVarCompare"] ?? "GE1", x)) return false;
        if (p["Condition"] is { } cond && !ConditionHolds(cond, x)) return false;
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
        // Untapping during other players' untap steps: the simulation only plays your turns.
        "UntapOtherPlayer",
    };

    private static readonly HashSet<string> CommonParams = new(StringComparer.Ordinal) { "Mode", "Description", "Secondary" };

    private static readonly HashSet<string> ContinuousParams = new(StringComparer.Ordinal)
    {
        "Affected", "AffectedDefined", "AddPower", "AddToughness", "AddKeyword", "SetPower", "SetToughness",
        "CharacteristicDefining", "EffectZone", "AffectedZone", "AdjustLandPlays", "SetMaxHandSize", "MayPlay", "MayPlayWithoutManaCost",
        "AddType", "RemoveCardTypes", "RemoveCreatureTypes", "AddAllCreatureTypes", "SetColor", "AddColor", "AddAbility", "AddTrigger",
        "RemoveType", "RemoveAllAbilities", "MayLookAt", "MayPlayAltManaCost", "MayPlayIgnoreType",
        // Granted abilities keep using the SVars of the card that grants them, so handing over SVars is not needed.
        "AddSVar",
        // Goaded creatures must attack someone else: only opponents' creatures get goaded, and they never attack.
        "Goad",
    };

    private static readonly HashSet<string> ReduceCostParams = new(StringComparer.Ordinal)
    {
        "Amount", "ValidCard", "Type", "EffectZone", "Activator",
    };

    private static readonly HashSet<string> CantAttackParams = new(StringComparer.Ordinal) { "ValidCard", "EffectZone" };
    private static readonly HashSet<string> ValidPlayerParams = new(StringComparer.Ordinal) { "ValidPlayer", "EffectZone" };
    private static readonly HashSet<string> CastWithFlashParams = new(StringComparer.Ordinal) { "ValidCard", "ValidSA", "EffectZone", "Caster" };
    private static readonly HashSet<string> PanharmoniconParams = new(StringComparer.Ordinal) { "ValidMode", "ValidCard", "ValidCause", "Origin", "Destination" };

    private static readonly HashSet<string> AlternativeCostParams = new(StringComparer.Ordinal)
    {
        "ValidSA", "ValidCard", "Cost", "EffectZone", "ValidPlayer", "StackDescription", "CostDesc",
    };

    public static void Validate(StaticDef s, CompileContext ctx)
    {
        if (NoEffectInGoldfish.Contains(s.Mode)) return;
        // "Your opponents can't cast spells / activate abilities": goldfish opponents never do anyway.
        if (s.Mode is "CantBeCast" or "CantBeActivated" && (s["Caster"] ?? s["Activator"]) == "Opponent") return;
        // "Creatures can't attack you unless …" (Ghostly Prison), "can't attack you": goldfish opponents never attack.
        if (s.Mode is "CantAttackUnless" or "CantAttack" && s["Target"] is "You" or "You,Planeswalker.YouCtrl") return;
        HashSet<string> own;
        // "Can't attack or block": blocking never comes up against goldfish opponents.
        switch (s.Mode is "CantAttack,CantBlock" or "CantBlock,CantAttack" ? "CantAttack" : s.Mode)
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
            case "CastWithFlash":
                own = CastWithFlashParams;
                if (s["ValidSA"] is { } vsa && vsa != "Spell") ctx.Need("CastWithFlash.ValidSA:" + vsa);
                if (s["Caster"] is { } caster && caster is not ("You" or "Player")) ctx.Need("CastWithFlash.Caster:" + caster);
                if (s["EffectZone"] is { } fez && fez is not ("Battlefield" or "Command" or "All")) ctx.Need("EffectZone:" + fez);
                if (s["ValidCard"] is { } fvc) Filter.Parse(fvc, ctx);
                break;
            case "CantGainLife":
                own = ValidPlayerParams;
                if (s["ValidPlayer"] is { } gvp) Filter.Parse(gvp, ctx);
                break;
            case "ActivateAbilityAsIfHaste":
                own = CantAttackParams;
                if (s["ValidCard"] is { } hvc) Filter.Parse(hvc, ctx);
                break;
            case "CantAttack":
                own = CantAttackParams;
                if (s["ValidCard"] is { } cvc) Filter.Parse(cvc, ctx);
                break;
            case "Panharmonicon":
                own = PanharmoniconParams;
                foreach (var zk in new[] { "Origin", "Destination" })
                    if (s[zk] is { } zone && !zone.Split(',').All(z => Enum.TryParse<Zone>(z.Trim(), out _))) ctx.Need($"Panharmonicon.{zk}:" + zone);
                if (s["ValidCard"] is { } pvc) Filter.Parse(pvc, ctx);
                if (s["ValidCause"] is { } pc) Filter.Parse(pc, ctx);
                break;
            case "RaiseCost" when s["Activator"] is "Opponent" or "Player.Opponent":
                return;   // goldfish opponents cast nothing
            case "RaiseCost":
                own = ReduceCostParams;
                if (s["Type"] is { } rtype && rtype != "Spell") ctx.Need("RaiseCost.Type:" + rtype);
                if (s["Activator"] is { } ract && ract != "You") ctx.Need("RaiseCost.Activator:" + ract);
                if (s["EffectZone"] is { } rez && rez is not ("All" or "Battlefield")) ctx.Need("EffectZone:" + rez);
                if (s["ValidCard"] is { } rvc) Filter.Parse(rvc, ctx);
                if (s["Cost"] is not null) ctx.Need("RaiseCost.Cost");
                Amount.Parse(s["Amount"] ?? "1", ctx);
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
            // "If you cast a spell this way, pay life equal to its mana value rather than pay its mana cost" (Bolas's Citadel).
            if (s["MayPlayAltManaCost"] is { } alt) CardCompiler.ParseCost(alt.Replace("ConvertedManaCost", "1"), ctx);
        }
        else if (s["MayPlayAltManaCost"] is not null) ctx.Need("MayPlayAltManaCost+NoMayPlay");
        if (s["MayLookAt"] is { } look && (look is not ("You" or "Player") || s["AffectedZone"] != "Library" || s["MayPlay"] is null && s.P.Values.Keys.Any(k => k is not ("Mode" or "Affected" or "AffectedZone" or "MayLookAt" or "Description"))))
            ctx.Need("MayLookAt:" + look + "+" + s["AffectedZone"]);
        if (s["MayPlay"] is null && s["MayLookAt"] is null && s["AffectedZone"] is { } az && az != "Battlefield" && !IsEscapeGrant(s))
            ctx.Need("AffectedZone:" + az);
        if (s["EffectZone"] is { } ez && ez is not ("Battlefield" or "All" or "Command" or "Graveyard")) ctx.Need("EffectZone:" + ez);
        foreach (var key in new[] { "AddPower", "AddToughness", "SetPower", "SetToughness", "AdjustLandPlays" })
            if (s[key] is { } v && v != "Unlimited") Amount.Parse(v, ctx);
        if (s["SetMaxHandSize"] is { } mh && mh != "Unlimited") Amount.Parse(mh, ctx);
        if (s["AddKeyword"] is { } kws)
            foreach (var k in kws.Split('&', StringSplitOptions.TrimEntries))
                if (!Keywords.Supported.Contains(ForgeRequirements.KeywordName(k))) ctx.Need("Keyword:" + ForgeRequirements.KeywordName(k));
        TypeChange.Validate(s["AddType"], ctx, "AddType");
        foreach (var key in new[] { "SetColor", "AddColor" })
            if (s[key] is { } col && col != "ChosenColor" && AnimateEffect.ColorsOf(col) is null) ctx.Need($"{key}:{col}");
        s.Grants = Grants.Compile(s.P, ctx, "AddAbility", "AddTrigger", null);
    }

    /// <summary>"Each nonland card in your graveyard has escape" (Underworld Breach): gives cards in the graveyard an escape cost.</summary>
    private static bool IsEscapeGrant(StaticDef s) =>
        s["AffectedZone"] == "Graveyard" && s["AddKeyword"] is { } k && k.StartsWith("Escape:", StringComparison.Ordinal) && !k.Contains('&')
        && s.P.Values.Keys.All(key => key is "Mode" or "Affected" or "AffectedZone" or "AddKeyword" or "Description" or "EffectZone" || Presence.Params.Contains(key));

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Cost> CostCache = new();

    private static Cost CachedCost(string text, FaceDef face) =>
        CostCache.GetOrAdd(text, t => CardCompiler.ParseCost(t, new CompileContext { Face = face, Missing = new() }));

    /// <summary>The cost to cast <paramref name="c"/> from its owner's graveyard with escape (its own, or given by a static), or null.</summary>
    public static Cost? EscapeCost(Game g, Card c)
    {
        var text = c.CurrentFace.EscapeCost;
        if (text is null)
            foreach (var (source, st) in Pairs(g))
            {
                if (source.Controller != c.Owner) continue;
                if (st.Mode != "Continuous" || !IsEscapeGrant(st) || !WorksFrom(source, st)) continue;
                var x = ContextFor(g, source, st);
                if (st["Affected"] is { } a && !Effect.FilterFor(source.CurrentFace, a).MatchesCard(c, x)) continue;
                if (!Presence.Hold(st.P, x)) continue;
                text = st["AddKeyword"]![7..];
                break;
            }
        if (text is null) return null;
        var cost = CachedCost(text.Replace("CardManaCost", "").Trim(), c.CurrentFace);
        return text.Contains("CardManaCost", StringComparison.Ordinal) ? new Cost { Mana = c.CurrentFace.ManaCost }.Plus(cost) : cost;
    }

    /// <summary>Whether anything <paramref name="player"/> controls could give cards escape (a cheap check before <see cref="EscapeCost"/>).</summary>
    public static bool GrantsEscape(Game g, int player)
    {
        foreach (var (source, st) in Pairs(g))
            if (source.Controller == player && st.Mode == "Continuous" && IsEscapeGrant(st)) return true;
        return false;
    }

    private static EffectContext ContextFor(Game g, Card source, StaticDef? st = null) => new()
    {
        Game = g, Ability = Game.PermanentSpell, FaceOverride = st?.Face,
        Item = new StackItem { SourceId = source.Id, Ability = Game.PermanentSpell, Controller = source.Controller },
    };

    /// <summary>
    /// Recomputes every continuous effect in layer order (rule 613): types (4), colors (5),
    /// abilities (6), then power/toughness — characteristic-defining and "set" (7a/7b) before
    /// modifications (7c). Within a layer, statics apply before "becomes" effects, which are newer.
    /// </summary>
    public static void Apply(Game g)
    {
        var s = g.State;
        var active = new List<(Card Source, StaticDef Static, EffectContext X)>();
        foreach (var c in Sources(g))
            foreach (var st in c.Statics)
            {
                if (st.Mode != "Continuous" || !WorksFrom(c, st)) continue;
                // "You may look at the top card of your library any time": that card is known.
                if (st["MayLookAt"] is not null && c.Zone == Zone.Battlefield && s.Players[c.Controller] is { IsGoldfish: false } looker && looker.Library.Count > 0)
                    looker.KnownTop = Math.Max(looker.KnownTop, 1);
                // Effects on cards in other zones (may play, escape) are looked up where they matter.
                if (st["MayPlay"] is null && st["MayLookAt"] is null && st["AffectedZone"] is null or "Battlefield") active.Add((c, st, ContextFor(g, c, st)));
            }
        var animated = new List<Card>();
        var leveled = new List<Card>();
        foreach (var c in s.BattlefieldCards)
        {
            if (c.Animations is not null) animated.Add(c);
            if (c.ClassLevel > 1) leveled.Add(c);
        }
        if (active.Count == 0 && animated.Count == 0 && leveled.Count == 0 && !g.StaticsApplied) return;
        g.StaticsApplied = active.Count > 0 || animated.Count > 0 || leveled.Count > 0;

        foreach (var c in s.BattlefieldCards) c.ClearStaticEffects();
        foreach (var p in s.Players) { p.StaticLandDrops = 0; p.StaticMaxHandSize = null; }

        var holding = active.Where(a => Presence.Hold(a.Static.P, a.X)).ToList();
        // What an effect applies to is fixed when it first applies (rule 613.6): an effect that makes
        // noncreature artifacts into creatures still sets their power and toughness afterwards.
        var affected = new List<Card>?[holding.Count];
        List<Card> Affected(int i) => affected[i] ??= AffectedCards(g, holding[i].Source, holding[i].Static, holding[i].X).ToList();

        // Layer 4: types.
        for (var i = 0; i < holding.Count; i++)
        {
            var (source, st, x) = holding[i];
            if (st["AddType"] is null && st["RemoveCardTypes"] is null && st["RemoveCreatureTypes"] is null && st["AddAllCreatureTypes"] is null
                && st["RemoveType"] is null) continue;
            var words = st["AddType"] is { } at ? TypeChange.Words(at, source) : null;
            foreach (var card in Affected(i))
                card.StaticTypes = TypeChange.Apply(card.Types, words, st["RemoveCardTypes"] == "True", st["RemoveCreatureTypes"] == "True",
                    false, st["AddAllCreatureTypes"] == "True", st["RemoveType"]);
        }
        foreach (var card in animated)
            foreach (var a in card.Animations!)
                if (a.AddTypes is not null || a.Ability["RemoveCardTypes"] is not null || a.Ability["RemoveCreatureTypes"] is not null
                    || a.Ability["RemoveSuperTypes"] is not null || a.Ability["AddAllCreatureTypes"] is not null)
                    card.StaticTypes = TypeChange.Apply(card.Types, a.AddTypes, a.Ability["RemoveCardTypes"] == "True",
                        a.Ability["RemoveCreatureTypes"] == "True", a.Ability["RemoveSuperTypes"] == "True", a.Ability["AddAllCreatureTypes"] == "True");

        // Layer 5: colors.
        for (var i = 0; i < holding.Count; i++)
        {
            var (source, st, x) = holding[i];
            var set = st["SetColor"] is { } sc ? ColorOf(sc, source) : null;
            var add = st["AddColor"] is { } ac ? ColorOf(ac, source) : null;
            if (set is null && add is null) continue;
            foreach (var card in Affected(i))
                card.StaticColors = (set ?? card.Colors) | (add ?? 0);
        }
        foreach (var card in animated)
            foreach (var a in card.Animations!)
                if (a.Colors is { } col)
                    card.StaticColors = a.Ability["OverwriteColors"] == "True" ? col : card.Colors | col;

        // Layer 6: abilities.
        foreach (var card in animated)
            foreach (var a in card.Animations!)
            {
                if (a.Ability["RemoveAllAbilities"] == "True")
                {
                    card.LostAbilities = true;
                    card.StaticActivated = new();
                    card.StaticTriggers = new();
                    card.StaticKeywords = null;
                }
                if (a.Ability["Keywords"] is { } kws)
                    (card.StaticKeywords ??= new HashSet<string>(StringComparer.Ordinal))
                        .UnionWith(kws.Split('&', StringSplitOptions.TrimEntries).Select(ForgeRequirements.KeywordName));
                Grant(card, a.Ability.Grants);
            }
        // "Loses all abilities" from a static: activated and triggered abilities and keywords. The affected
        // cards' own static abilities keep applying (removing them would change what this computes).
        for (var i = 0; i < holding.Count; i++)
            if (holding[i].Static["RemoveAllAbilities"] == "True")
                foreach (var card in Affected(i))
                {
                    card.LostAbilities = true;
                    card.StaticActivated = new();
                    card.StaticTriggers = new();
                    card.StaticKeywords = null;
                }
        foreach (var card in leveled)
            foreach (var (level, grants) in card.CurrentFace.ClassLevels)
                if (level <= card.ClassLevel) Grant(card, grants);
        for (var i = 0; i < holding.Count; i++)
            if (holding[i].Static.Grants is { } grants)
                foreach (var card in Affected(i)) Grant(card, grants);
        // A land that gains a basic land type gains its mana ability (rule 305.7).
        foreach (var card in s.BattlefieldCards)
            if (card.StaticTypes is { } t && t.Is(CardType.Land))
                foreach (var sub in t.Subtypes)
                    if (CardCompiler.BasicLandMana.TryGetValue(sub, out var mana) && !card.Activated.Contains(mana))
                        (card.StaticActivated ??= new(card.CurrentFace.Activated)).Add(mana);

        // Layer 7a/7b: characteristic-defining abilities and effects that set power/toughness.
        for (var i = 0; i < holding.Count; i++)
        {
            var (source, st, x) = holding[i];
            if (st["SetPower"] is null && st["SetToughness"] is null) continue;
            foreach (var card in Affected(i))
            {
                var cx = ContextFor(g, st["CharacteristicDefining"] == "True" ? card : source, st);
                if (st["SetPower"] is { } sp) card.SetPower = Effect.EvalAmount(cx, sp);
                if (st["SetToughness"] is { } stt) card.SetToughness = Effect.EvalAmount(cx, stt);
            }
        }
        foreach (var card in animated)
            foreach (var a in card.Animations!)
            {
                if (a.Power is { } pw) card.SetPower = pw;
                if (a.Toughness is { } tg) card.SetToughness = tg;
            }
        // Layer 6 keywords and 7c modifications; players' rule changes.
        for (var i = 0; i < holding.Count; i++)
        {
            var (source, st, x) = holding[i];
            var addPower = st["AddPower"] is { } ap ? Effect.EvalAmount(x, ap) : 0;
            var addToughness = st["AddToughness"] is { } at ? Effect.EvalAmount(x, at) : 0;
            var keywords = st["AddKeyword"]?.Split('&', StringSplitOptions.TrimEntries).Select(ForgeRequirements.KeywordName).ToList();
            if (addPower != 0 || addToughness != 0 || keywords is not null)
                foreach (var card in Affected(i))
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

    private static ColorMask? ColorOf(string text, Card source) =>
        text == "ChosenColor" ? source.ChosenColor?.Mask() ?? 0 : AnimateEffect.ColorsOf(text);

    private static void Grant(Card card, Grants? grants)
    {
        if (grants is null) return;
        if (grants.Activated.Count > 0) (card.StaticActivated ??= new(card.CurrentFace.Activated)).AddRange(grants.Activated);
        if (grants.Triggers.Count > 0) (card.StaticTriggers ??= new(card.CurrentFace.Triggers)).AddRange(grants.Triggers);
    }

    /// <summary>Whether a static ability works from where its card is: the battlefield, or its EffectZone$.</summary>
    private static bool WorksFrom(Card c, StaticDef st) => c.Zone switch
    {
        Zone.Battlefield => st["EffectZone"] is null or "Battlefield" or "All",
        Zone.Graveyard => st["EffectZone"] == "Graveyard",
        _ => true,
    };

    /// <summary>
    /// Every static ability of every <see cref="Sources"/> card, in that order. Asked for once per card in hand while
    /// working out the legal actions, when nothing changes, so it is found once then.
    /// </summary>
    internal static List<(Card Source, StaticDef Static)> Pairs(Game g)
    {
        if (g.StaticPairs is { } cached) return cached;
        var pairs = new List<(Card, StaticDef)>();
        foreach (var source in Sources(g))
            foreach (var st in source.Statics)
                pairs.Add((source, st));
        if (g.CachesStaticPairs) g.StaticPairs = pairs;
        return pairs;
    }

    /// <summary>Where static abilities work from: the battlefield, effects and emblems in command zones, and cards whose statics work from the graveyard.</summary>
    public static IEnumerable<Card> Sources(Game g)
    {
        foreach (var p in g.State.Players)
            foreach (var id in p.Graveyard)
                if (g.State[id] is { IsBlank: false } gc && gc.CurrentFace.HasGraveyardStatic) yield return gc;
        foreach (var c in g.State.BattlefieldCards)
            if (!c.IsBlank && (c.CurrentFace.Statics.Count > 0 || c.Animations is not null || c.ClassLevel > 1)) yield return c;
        foreach (var p in g.State.Players)
            foreach (var id in p.Command)
                if (g.State[id].IsEffect) yield return g.State[id];
    }

    /// <summary>How a card outside the hand may be played: free, or for another cost instead of its mana cost.</summary>
    public readonly record struct MayPlayGrant(bool Free, string? AltManaCost, bool AnyTypeOfMana = false);

    /// <summary>Cards outside the hand that <paramref name="player"/> may play now, and how.</summary>
    public static Dictionary<int, MayPlayGrant> MayPlay(Game g, int player)
    {
        var result = new Dictionary<int, MayPlayGrant>();
        foreach (var (source, st) in Pairs(g))
        {
            if (source.Controller != player) continue;
            if (st.Mode != "Continuous" || st["MayPlay"] != "True" || !WorksFrom(source, st)) continue;
            var x = ContextFor(g, source, st);
            if (!Presence.Hold(st.P, x)) continue;
            var zone = Enum.Parse<Zone>(st["AffectedZone"]!);
            var filter = st["Affected"] is { } a ? Effect.FilterFor(source.CurrentFace, a) : null;
            foreach (var c in g.CardsIn(zone))
                if (c.Owner == player && !c.IsEffect && (filter is null || filter.MatchesCard(c, x)))
                {
                    var grant = new MayPlayGrant(st["MayPlayWithoutManaCost"] == "True", st["MayPlayAltManaCost"], st["MayPlayIgnoreType"] == "True");
                    // The cheapest way wins: free, then an alternative cost, then the normal cost.
                    result[c.Id] = result.TryGetValue(c.Id, out var had) && (had.Free || had.AltManaCost is not null && !grant.Free) ? had : grant;
                }
        }
        return result;
    }

    /// <summary>The cost of a card played with a "may play … rather than pay its mana cost" grant (e.g. PayLife&lt;ConvertedManaCost&gt;).</summary>
    public static Cost AltManaCost(Card c, string text) =>
        CachedCost(text.Replace("ConvertedManaCost", c.CurrentFace.ManaCost.ManaValue.ToString()), c.CurrentFace);

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
        var filter = Effect.FilterFor(x.SVarFace, a);
        return g.State.Players.Where(p => !p.Lost && filter.MatchesPlayer(p.Index, x));
    }

    /// <summary>"[This creature] can't attack" (Pacifism-style auras, "CARDNAME can't attack").</summary>
    /// <summary>"[Players] can't gain life" (Erebos).</summary>
    public static bool CantGainLife(Game g, int player)
    {
        foreach (var source in Sources(g))
            foreach (var st in source.Statics)
            {
                if (st.Mode != "CantGainLife" || !WorksFrom(source, st)) continue;
                var x = ContextFor(g, source, st);
                if ((st["ValidPlayer"] is not { } vp || Effect.FilterFor(x.SVarFace, vp).MatchesPlayer(player, x)) && Presence.Hold(st.P, x)) return true;
            }
        return false;
    }

    /// <summary>"You may activate abilities of creatures you control as though they had haste" (Thousand-Year Elixir).</summary>
    public static bool ActivatesAsIfHaste(Game g, Card creature)
    {
        foreach (var source in Sources(g))
            foreach (var st in source.Statics)
            {
                if (st.Mode != "ActivateAbilityAsIfHaste" || !WorksFrom(source, st)) continue;
                var x = ContextFor(g, source, st);
                if ((st["ValidCard"] is not { } vc || Effect.FilterFor(x.SVarFace, vc).MatchesCard(creature, x)) && Presence.Hold(st.P, x)) return true;
            }
        return false;
    }

    public static bool CantAttack(Game g, Card creature)
    {
        foreach (var source in Sources(g))
            foreach (var st in source.Statics)
            {
                if (!st.Mode.Split(',').Contains("CantAttack") || st["Target"] is not null || !WorksFrom(source, st)) continue;
                var x = ContextFor(g, source, st);
                if ((st["ValidCard"] is not { } vc || Effect.FilterFor(x.SVarFace, vc).MatchesCard(creature, x)) && Presence.Hold(st.P, x)) return true;
            }
        return false;
    }

    /// <summary>"You may cast [spells] as though they had flash."</summary>
    public static bool CastWithFlash(Game g, Card spell, int caster)
    {
        foreach (var (source, st) in Pairs(g))
            if (Grants(source, st)) return true;
        foreach (var st in spell.Statics)
            if (Grants(spell, st)) return true;
        return false;

        bool Grants(Card source, StaticDef st)
        {
            if (st.Mode != "CastWithFlash" || source.Id != spell.Id && !WorksFrom(source, st)) return false;
            var self = source.Id == spell.Id;
            if (self != (st["EffectZone"] == "All")) return false;
            if (st["Caster"] != "Player" && source.Controller != caster) return false;
            var x = ContextFor(g, source, st);
            if (st["ValidCard"] is { } vc && !Effect.FilterFor(x.SVarFace, vc).MatchesCard(spell, x)) return false;
            return Presence.Hold(st.P, x);
        }
    }

    /// <summary>Net generic-cost change for casting <paramref name="spell"/>: "cost {N} less" minus "cost {N} more".</summary>
    public static int CostReduction(Game g, Card spell, int caster) => CostChange(g, spell, caster, "ReduceCost") - CostChange(g, spell, caster, "RaiseCost");

    private static int CostChange(Game g, Card spell, int caster, string mode)
    {
        // Battlefield sources (the only ones with statics are among Pairs), then the spell itself.
        var total = 0;
        foreach (var (source, st) in Pairs(g))
            if (source.Zone == Zone.Battlefield) total += Change(source, st);
        if (!spell.IsBlank)
            foreach (var st in spell.Statics)
                total += Change(spell, st);
        return total;

        int Change(Card source, StaticDef st)
        {
            if (st.Mode != mode || mode == "RaiseCost" && st["Activator"] == "Opponent") return 0;
            // A card's own "this spell costs less" works from anywhere (EffectZone$ All); others from the battlefield.
            var self = source.Id == spell.Id;
            if (self != (st["EffectZone"] == "All")) return 0;
            if (!self && source.Controller != caster) return 0;
            var x = ContextFor(g, source, st);
            if (st["ValidCard"] is { } vc && !Effect.FilterFor(source.CurrentFace, vc).MatchesCard(spell, x)) return 0;
            if (!Presence.Hold(st.P, x)) return 0;
            return Math.Max(0, Effect.EvalAmount(x, st["Amount"] ?? "1"));
        }
    }
}
