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
    };
}

/// <summary>Trigger modes the engine fires, and how each one matches an event.</summary>
public static class Triggers
{
    public static readonly HashSet<string> Supported = new(StringComparer.Ordinal) { "ChangesZone", "Phase", "SpellCast", "Attacks" };

    private static readonly HashSet<string> CommonParams = new(StringComparer.Ordinal)
    {
        "Mode", "Execute", "TriggerDescription", "TriggerZones", "OptionalDecider", "Secondary", "ValidCard",
    };

    private static readonly Dictionary<string, HashSet<string>> ModeParams = new()
    {
        ["ChangesZone"] = new(StringComparer.Ordinal) { "Origin", "Destination" },
        ["Phase"] = new(StringComparer.Ordinal) { "Phase", "ValidPlayer" },
        ["SpellCast"] = new(StringComparer.Ordinal) { "ValidActivatingPlayer" },
        ["Attacks"] = new(StringComparer.Ordinal) { },
    };

    public static void Validate(TriggerDef t, CompileContext ctx)
    {
        if (!ModeParams.TryGetValue(t.Mode, out var own)) return;   // unsupported mode already reported
        foreach (var key in t.P.Values.Keys)
            if (!CommonParams.Contains(key) && !own.Contains(key))
                ctx.Need($"TriggerParam:{t.Mode}.{key}");
        if (t["ValidCard"] is { } vc) Filter.Parse(vc, ctx);
        if (t["TriggerZones"] is { } tz && tz != "Battlefield") ctx.Need("TriggerZones:" + tz);
        if (t["Phase"] is { } ph && ph is not ("Upkeep" or "Draw" or "Main1" or "Main2" or "End of Turn" or "BeginCombat"))
            ctx.Need("TriggerPhase:" + ph);
    }

    public static bool Matches(Game g, TriggerDef t, Card source, TriggerEvent e)
    {
        var x = new EffectContext
        {
            Game = g, Ability = t.Execute ?? Game.PermanentSpell,
            Item = new StackItem { SourceId = source.Id, Ability = t.Execute ?? Game.PermanentSpell, Controller = source.Controller },
        };
        // Triggers work from the battlefield, except the moved card's own "look back" triggers.
        var sourceActive = source.Zone == Zone.Battlefield
            || (e.Mode == TriggerMode.ChangesZone && e.CardId == source.Id && e.From == Zone.Battlefield);
        if (!sourceActive) return false;

        switch (t.Mode)
        {
            case "ChangesZone" when e.Mode == TriggerMode.ChangesZone:
                if (!ZoneMatches(t["Origin"], e.From) || !ZoneMatches(t["Destination"], e.To)) return false;
                return CardMatches(g, t, e.CardId, x, source);
            case "Phase" when e.Mode == TriggerMode.Phase:
                if (t["Phase"] != e.Phase) return false;
                return PlayerMatches(t["ValidPlayer"], e.Player, source.Controller);
            case "SpellCast" when e.Mode == TriggerMode.SpellCast:
                return PlayerMatches(t["ValidActivatingPlayer"], e.Player, source.Controller) && CardMatches(g, t, e.CardId, x, source);
            case "Attacks" when e.Mode == TriggerMode.Attacks:
                return CardMatches(g, t, e.CardId, x, source);
            default:
                return false;
        }
    }

    private static bool CardMatches(Game g, TriggerDef t, int? cardId, EffectContext x, Card source)
    {
        if (t["ValidCard"] is not { } vc) return true;
        if (cardId is not { } id) return false;
        var ctx = new CompileContext { Face = source.CurrentFace, Missing = new() };
        return Filter.Parse(vc, ctx).MatchesCard(g.State[id], x);
    }

    private static bool ZoneMatches(string? spec, Zone z) =>
        spec is null or "Any" || spec.Split(',').Any(s => Enum.TryParse<Zone>(s.Trim(), out var want) && want == z);

    private static bool PlayerMatches(string? spec, int? player, int controller) => spec switch
    {
        null or "Player" => true,
        "You" => player == controller,
        "Opponent" => player != controller,
        _ => false,
    };
}

/// <summary>Static abilities. None are implemented yet; every mode is reported as missing.</summary>
public static class Statics
{
    public static readonly HashSet<string> Supported = new(StringComparer.Ordinal);

    public static void Validate(StaticDef s, CompileContext ctx)
    {
        if (!Supported.Contains(s.Mode)) ctx.Need("Static:" + s.Mode);
    }
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
        var ctx = new CompileContext { Face = x.Host.CurrentFace, Missing = new() };
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
