namespace MagicSim.Core.Engine;

/// <summary>Keywords the engine handles (CONTRACT.md §6). Several only matter against real opponents.</summary>
public static class Keywords
{
    public static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
    {
        // Implemented in combat / timing / casting.
        "Haste", "Vigilance", "Double Strike", "Lifelink", "Defender", "Flash", "Storm", "Indestructible",
        // No effect in goldfish: nobody blocks, targets your stuff, or deals you combat damage.
        "Flying", "Trample", "First Strike", "Menace", "Reach", "Deathtouch", "Hexproof", "Shroud", "Ward",
        "Protection", "Landwalk", "Intimidate", "Fear", "Shadow", "Horsemanship", "Skulk",
        // Deck-building only.
        "Partner", "Partner with", "Friends forever", "Choose a Background", "Doctor's companion",
        // Handled where they matter: Devoid makes the card colorless.
        "Devoid", "Changeling", "Infect", "Wither", "Convoke",
    };
}

/// <summary>Trigger modes the engine fires, and how each one matches an event.</summary>
public static class Triggers
{
    /// <summary>Triggers on events that never happen against goldfish opponents (blocking, other game variants).</summary>
    private static readonly HashSet<string> NeverInGoldfish = new(StringComparer.Ordinal)
    {
        "Blocks", "AttackerBlocked", "AttackerBlockedByCreature", "ChaosEnsues", "SetInMotion", "PlaneswalkedTo",
    };

    private static readonly HashSet<string> CommonParams = new(StringComparer.Ordinal)
    {
        "Mode", "Execute", "TriggerDescription", "TriggerZones", "OptionalDecider", "Secondary", "ValidCard", "Static", "ActivationLimit",
    };

    private static readonly Dictionary<string, HashSet<string>> ModeParams = new()
    {
        ["ChangesZone"] = new(StringComparer.Ordinal) { "Origin", "Destination" },
        ["Phase"] = new(StringComparer.Ordinal) { "Phase", "ValidPlayer" },
        ["SpellCast"] = new(StringComparer.Ordinal) { "ValidActivatingPlayer" },
        ["Attacks"] = new(StringComparer.Ordinal) { },
        ["Sacrificed"] = new(StringComparer.Ordinal) { "ValidPlayer" },
        ["ChangesZoneAll"] = new(StringComparer.Ordinal) { "ValidCards", "Origin", "Destination" },
        ["DamageDoneOnce"] = new(StringComparer.Ordinal) { "ValidSource", "ValidTarget", "CombatDamage" },
        ["DamageDone"] = new(StringComparer.Ordinal) { "ValidSource", "ValidTarget", "CombatDamage" },
        ["AttackersDeclared"] = new(StringComparer.Ordinal) { "AttackingPlayer" },
        ["Drawn"] = new(StringComparer.Ordinal) { },
        ["LandPlayed"] = new(StringComparer.Ordinal) { },
        ["Discarded"] = new(StringComparer.Ordinal) { },
        ["LifeGained"] = new(StringComparer.Ordinal) { "ValidPlayer" },
        ["Taps"] = new(StringComparer.Ordinal) { },
    };

    public static readonly HashSet<string> Supported = new(ModeParams.Keys.Concat(NeverInGoldfish), StringComparer.Ordinal);

    public static void Validate(TriggerDef t, CompileContext ctx)
    {
        if (!ModeParams.TryGetValue(t.Mode, out var own)) return;   // unsupported (already reported) or never fires
        foreach (var key in t.P.Values.Keys)
            if (!CommonParams.Contains(key) && !own.Contains(key) && !Presence.Params.Contains(key))
                ctx.Need($"TriggerParam:{t.Mode}.{key}");
        Presence.Validate(t.P, ctx);
        if (t["ValidCard"] is { } vc) Filter.Parse(vc, ctx);
        if (t["ValidSource"] is { } vs) Filter.Parse(vs, ctx);
        if (t["ValidTarget"] is { } vt) Filter.Parse(vt, ctx);
        if (t["AttackingPlayer"] is { } ap && ap != "You") ctx.Need("AttackingPlayer:" + ap);
        if (t["ValidPlayer"] is { } vp && vp is not ("You" or "Player" or "Opponent")) ctx.Need("TriggerValidPlayer:" + vp);
        if (t["TriggerZones"] is { } tz && tz is not ("Battlefield" or "Command")) ctx.Need("TriggerZones:" + tz);
        if (t["Phase"] is { } ph && ph is not ("Upkeep" or "Draw" or "Main1" or "Main2" or "End of Turn" or "End Of Turn" or "BeginCombat" or "EndCombat"))
            ctx.Need("TriggerPhase:" + ph);
    }

    public static bool Matches(Game g, TriggerDef t, Card source, TriggerEvent e)
    {
        var x = new EffectContext
        {
            Game = g, Ability = t.Execute ?? Game.PermanentSpell,
            Item = new StackItem { SourceId = source.Id, Ability = t.Execute ?? Game.PermanentSpell, Controller = source.Controller },
            FaceOverride = t.Face,
        };
        // Triggers work from the battlefield, except the moved card's own "look back" triggers.
        // Effects and emblems (and "eminence" commanders) trigger from the command zone.
        var sourceActive = source.Zone == Zone.Battlefield
            || (e.Mode == TriggerMode.ChangesZone && e.CardId == source.Id && e.From == Zone.Battlefield)
            || source.Zone == Zone.Command && (source.IsEffect || t["TriggerZones"] == "Command");
        if (!sourceActive) return false;
        if (!ModeMatches(g, t, source, e, x)) return false;
        return Presence.Hold(t.P, x);
    }

    /// <summary>Whether a delayed trigger's event has happened. Its source need not be anywhere in particular.</summary>
    public static bool MatchesDelayed(Game g, DelayedTrigger d, TriggerEvent e)
    {
        var t = d.Trigger;
        var host = g.State[d.HostId];
        var x = new EffectContext
        {
            Game = g, Ability = t.Execute ?? Game.PermanentSpell, FaceOverride = t.Face,
            Item = new StackItem { SourceId = d.HostId, Ability = t.Execute ?? Game.PermanentSpell, Controller = d.Controller, DelayRemembered = d.Remembered },
        };
        if (t.Mode == "Phase" && e.Mode == TriggerMode.Phase)
            return string.Equals(t["Phase"], e.Phase, StringComparison.OrdinalIgnoreCase) && PlayerMatches(t["ValidPlayer"], e.Player, d.Controller)
                && Presence.Hold(t.P, x);
        return ModeMatches(g, t, host, e, x) && Presence.Hold(t.P, x);
    }

    private static bool ModeMatches(Game g, TriggerDef t, Card source, TriggerEvent e, EffectContext x)
    {
        switch (t.Mode)
        {
            case "ChangesZone" when e.Mode == TriggerMode.ChangesZone:
                if (!ZoneMatches(t["Origin"], e.From) || !ZoneMatches(t["Destination"], e.To)) return false;
                return CardMatches(g, t, e.CardId, x, source);
            case "Phase" when e.Mode == TriggerMode.Phase:
                if (!string.Equals(t["Phase"], e.Phase, StringComparison.OrdinalIgnoreCase)) return false;
                return PlayerMatches(t["ValidPlayer"], e.Player, source.Controller);
            case "SpellCast" when e.Mode == TriggerMode.SpellCast:
                return PlayerMatches(t["ValidActivatingPlayer"], e.Player, source.Controller) && CardMatches(g, t, e.CardId, x, source);
            case "Attacks" when e.Mode == TriggerMode.Attacks:
                return CardMatches(g, t, e.CardId, x, source);
            case "DamageDone" when e.Mode == TriggerMode.DamageDone:
                if (t["CombatDamage"] is { } cd && (cd == "True") != e.Combat) return false;
                if (t["ValidSource"] is { } vs && (e.CardId is not { } src || !FilterOf(x, vs).MatchesCard(g.State[src], x))) return false;
                if (t["ValidTarget"] is { } vt)
                {
                    var f = FilterOf(x, vt);
                    if (e.Player is { } p ? !f.MatchesPlayer(p, x) : e.TargetCardId is not { } tc || !f.MatchesCard(g.State[tc], x)) return false;
                }
                return true;
            case "AttackersDeclared" when e.Mode == TriggerMode.AttackersDeclared:
                return e.Player == source.Controller;
            case "Drawn" when e.Mode == TriggerMode.Drawn:
            case "LandPlayed" when e.Mode == TriggerMode.LandPlayed:
            case "Discarded" when e.Mode == TriggerMode.Discarded:
            case "Taps" when e.Mode == TriggerMode.Taps:
                return CardMatches(g, t, e.CardId, x, source);
            case "Sacrificed" when e.Mode == TriggerMode.Sacrificed:
                return PlayerMatches(t["ValidPlayer"], e.Player, source.Controller) && CardMatches(g, t, e.CardId, x, source);
            case "LifeGained" when e.Mode == TriggerMode.LifeGained:
                return PlayerMatches(t["ValidPlayer"], e.Player, source.Controller);
            default:
                return false;
        }
    }

    private static Filter FilterOf(EffectContext x, string text) => Effect.FilterFor(x.SVarFace, text);

    private static bool CardMatches(Game g, TriggerDef t, int? cardId, EffectContext x, Card source)
    {
        if (t["ValidCard"] is not { } vc) return true;
        if (cardId is not { } id) return false;
        return FilterOf(x, vc).MatchesCard(g.State[id], x);
    }

    /// <summary>"This ability triggers only once each turn" (ActivationLimit$): counts this triggering if allowed.</summary>
    public static bool UnderLimit(Game g, Card source, TriggerDef t)
    {
        if (t["ActivationLimit"] is not { } lim || !int.TryParse(lim, out var limit)) return true;
        source.TriggerUses ??= new();
        var (turn, count) = source.TriggerUses.GetValueOrDefault(t);
        if (turn != g.State.Turn) count = 0;
        if (count >= limit) return false;
        source.TriggerUses[t] = (g.State.Turn, count + 1);
        return true;
    }

    public static bool ZoneMatches(string? spec, Zone z) =>
        spec is null or "Any" || spec.Split(',').Any(s => Enum.TryParse<Zone>(s.Trim(), out var want) && want == z);

    private static bool PlayerMatches(string? spec, int? player, int controller) => spec switch
    {
        null or "Player" => true,
        "You" => player == controller,
        "Opponent" => player != controller,
        _ => false,
    };
}

/// <summary>"Condition…$" parameters that decide whether one link of an ability chain happens.</summary>
public static class Conditions
{
    public static readonly HashSet<string> Params = new(StringComparer.Ordinal)
    {
        "ConditionCheckSVar", "ConditionSVarCompare", "ConditionDefined", "ConditionPresent", "ConditionCompare", "ConditionDescription",
    };

    public static void Validate(AbilityDef ab, CompileContext ctx)
    {
        foreach (var key in ab.P.Values.Keys)
            if (key.StartsWith("Condition", StringComparison.Ordinal) && !Params.Contains(key))
                ctx.Need("Condition:" + key);
        if (ab["ConditionCheckSVar"] is { } sv)
        {
            Amount.Parse(sv, ctx);
            var cmp = ab["ConditionSVarCompare"] ?? "GE1";
            if (cmp.Length < 3) ctx.Need("ConditionCompare:" + cmp);
            else Amount.Parse(cmp[2..], ctx);
        }
        if (ab["ConditionPresent"] is { } present) Filter.Parse(present, ctx);
        if (ab["ConditionDefined"] is { } def) Defined.ValidateCards(def, ctx);
    }

    public static bool Hold(EffectContext x)
    {
        var ab = x.Ability;
        var ctx = new CompileContext { Face = x.SVarFace, Missing = new() };
        if (ab["ConditionCheckSVar"] is { } sv)
        {
            var cmp = ab["ConditionSVarCompare"] ?? "GE1";
            var left = Amount.Parse(sv, ctx).Eval(x);
            var right = Amount.Parse(cmp[2..], ctx).Eval(x);
            if (!Filter.Compare(left, cmp[..2], right)) return false;
        }
        if (ab["ConditionPresent"] is { } present)
        {
            var filter = Filter.Parse(present, ctx);
            var cards = ab["ConditionDefined"] is { } def
                ? Defined.Cards(def, x).Select(id => x.S[id])
                : x.Game.CardsIn(Zone.Battlefield);
            var count = cards.Count(c => filter.MatchesCard(c, x));
            var cmp = ab["ConditionCompare"] ?? "GE1";
            if (!Filter.Compare(count, cmp[..2], int.Parse(cmp[2..]))) return false;
        }
        return true;
    }
}
