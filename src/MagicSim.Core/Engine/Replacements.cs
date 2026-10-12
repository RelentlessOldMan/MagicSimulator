using MagicSim.Core.Forge;

namespace MagicSim.Core.Engine;

public enum ReplacementKind
{
    /// <summary>Changes how much: damage dealt, tokens created, counters put, life gained.</summary>
    Amount,
    /// <summary>"If a card would be put into a graveyard, exile it instead" (Rest in Peace).</summary>
    ExileInstead,
    /// <summary>The event doesn't happen; an ability happens instead (draw replacements: Laboratory Maniac).</summary>
    Instead,
}

/// <summary>A replacement effect (rule 614) of a permanent or effect object, other than its own "as this enters".</summary>
public sealed class ReplacementDef
{
    public required ForgeParams P { get; init; }
    public required string Event { get; init; }
    public required ReplacementKind Kind { get; init; }
    public required FaceDef Face { get; init; }
    /// <summary>For an amount replacement: the new amount, given the old one (ReplaceCount$…).</summary>
    public string? NewAmount { get; init; }
    /// <summary>For an "instead" replacement: what happens instead.</summary>
    public AbilityDef? With { get; init; }
    public string? this[string key] => P[key];
}

/// <summary>Compiles and applies replacement effects that modify events (rule 614).</summary>
public static class Replacements
{
    private static readonly HashSet<string> Common = new(StringComparer.Ordinal)
    {
        "Event", "Description", "ActiveZones", "ReplaceWith", "Secondary", "AICheckSVar", "AILogic",
    };

    private static readonly Dictionary<string, HashSet<string>> MatchParams = new()
    {
        ["DamageDone"] = new(StringComparer.Ordinal) { "ValidSource", "ValidTarget", "IsCombat" },
        ["CreateToken"] = new(StringComparer.Ordinal) { "ValidToken", "ValidPlayer", "EffectOnly" },
        ["AddCounter"] = new(StringComparer.Ordinal) { "ValidCard", "ValidCounterType", "EffectOnly", "ValidObject" },
        ["GainLife"] = new(StringComparer.Ordinal) { "ValidPlayer" },
        ["Moved"] = new(StringComparer.Ordinal) { "ValidCard", "Origin", "Destination", "ValidLKI" },
        ["Draw"] = new(StringComparer.Ordinal) { "ValidPlayer", "NotFirstCardInDrawStep" },
    };

    /// <summary>Compiles a non-self replacement into <paramref name="face"/>. False if it isn't one this knows.</summary>
    public static bool TryCompile(ForgeParams r, FaceDef face, CompileContext ctx)
    {
        var evt = r["Event"] ?? "?";
        // "This spell / creature spells can't be countered": goldfish opponents never counter anything.
        if (evt == "Counter" && r["Layer"] == "CantHappen") return true;
        if (r["ActiveZones"] is { } az && az is not ("Battlefield" or "Command")) return false;

        // Preventing damage dealt to you or your permanents: goldfish opponents never deal any.
        if (evt == "DamageDone" && (r["Prevent"] == "True" || r["PreventionEffect"] == "True"))
            return r["ValidTarget"] is { } vt && vt.Split(',').All(AboutYou);
        // Effects on what opponents' cards do: goldfish opponents have none.
        foreach (var key in new[] { "ValidCard", "ValidLKI" })
            if (r[key] is { } ovc && ovc.Split(',').All(t => t.Contains("OppCtrl", StringComparison.Ordinal) || t.Contains("OppOwn", StringComparison.Ordinal)))
                return true;

        if (!MatchParams.TryGetValue(evt, out var own)) return false;
        if (r.Values.Keys.Any(k => !Common.Contains(k) && !own.Contains(k) && !Presence.Params.Contains(k))) return false;
        if (r["ReplaceWith"] is not { } with || !face.SVars.TryGetValue(with, out var body)) return false;
        var w = new ForgeParams(body);

        ReplacementKind kind;
        string? newAmount = null;
        AbilityDef? instead = null;
        switch (evt)
        {
            case "Draw":
                kind = ReplacementKind.Instead;
                instead = CardCompiler.CompileAbility(w, ctx, AbilityKind.Sub, 0);
                break;
            case "DamageDone" when w.Api == "ReplaceEffect" && w["VarName"] == "DamageAmount" && OnlyKeys(w, "VarName", "VarValue"):
            case "GainLife" when w.Api == "ReplaceEffect" && w["VarName"] == "LifeGained" && OnlyKeys(w, "VarName", "VarValue"):
                kind = ReplacementKind.Amount;
                newAmount = w["VarValue"];
                break;
            case "CreateToken" when w.Api == "ReplaceToken" && w["Type"] == "Amount" && OnlyKeys(w, "Type", "Amount"):
                kind = ReplacementKind.Amount;
                newAmount = "ReplaceCount$Amount/" + (w["Amount"] ?? "Twice");
                if (newAmount.EndsWith("Thrice")) newAmount = newAmount[..^"Thrice".Length] + "Times.3";
                break;
            case "AddCounter" when w.Api == "ReplaceCounter" && OnlyKeys(w, "Amount", "ValidCounterType", "ChooseCounter"):
                if (w["ValidCounterType"] is { } wct && r["ValidCounterType"] is { } rct && wct != rct) return false;
                kind = ReplacementKind.Amount;
                newAmount = w["Amount"];
                break;
            case "Moved" when r["Destination"] == "Graveyard" && w.Api == "ChangeZone" && w["Destination"] == "Exile"
                              && w["Defined"] == "ReplacedCard" && OnlyKeys(w, "Destination", "Defined", "Origin", "Hidden"):
                kind = ReplacementKind.ExileInstead;
                break;
            default:
                return false;
        }
        if (newAmount is not null) Engine.Amount.Parse(newAmount, ctx);
        foreach (var key in new[] { "ValidSource", "ValidTarget", "ValidToken", "ValidCard", "ValidObject", "ValidLKI" })
            if (r[key] is { } f) Filter.Parse(WithoutEmblems(f), ctx);
        if (r["ValidPlayer"] is { } vp) Filter.Parse(vp, ctx);
        Presence.Validate(r, ctx);
        face.Replacements.Add(new ReplacementDef { P = r, Event = evt, Kind = kind, Face = face, NewAmount = newAmount, With = instead });
        return true;
    }

    private static bool OnlyKeys(ForgeParams p, params string[] keys) =>
        p.Values.Keys.All(k => k is "DB" or "SpellDescription" or "StackDescription" || keys.Contains(k));

    private static bool AboutYou(string term)
    {
        term = term.Trim();
        return term is "You" or "Card.Self" or "Creature.Self" or "Permanent.Self" || term.Contains("YouCtrl", StringComparison.Ordinal)
            || term.Contains("EquippedBy", StringComparison.Ordinal) || term.Contains("EnchantedBy", StringComparison.Ordinal)
            || term.Contains("AttachedBy", StringComparison.Ordinal);
    }

    /// <summary>Emblems are effects in the command zone; a source filter's emblem terms are dropped.</summary>
    private static string WithoutEmblems(string filter)
    {
        var terms = filter.Split(',').Where(t => !t.Trim().StartsWith("Emblem", StringComparison.Ordinal)).ToList();
        return terms.Count == 0 ? "Card" : string.Join(',', terms);
    }

    /// <summary>Permanents and effect objects whose replacement effects apply now.</summary>
    private static IEnumerable<(Card Host, ReplacementDef R)> Active(Game g, string evt)
    {
        foreach (var c in g.State.BattlefieldCards)
            if (!c.IsBlank && !c.LostAbilities)
                foreach (var r in c.CurrentFace.Replacements)
                    if (r.Event == evt && r["ActiveZones"] != "Command") yield return (c, r);
        foreach (var p in g.State.Players)
            foreach (var id in p.Command)
            {
                var c = g.State[id];
                foreach (var r in c.CurrentFace.Replacements)
                    if (r.Event == evt && (c.IsEffect || r["ActiveZones"] == "Command")) yield return (c, r);
            }
    }

    private static EffectContext ContextFor(Game g, Card host, ReplacementDef r, int replaced) => new()
    {
        Game = g, Ability = Game.PermanentSpell, FaceOverride = r.Face, ReplacedAmount = replaced,
        Item = new StackItem { SourceId = host.Id, Ability = Game.PermanentSpell, Controller = host.Controller },
    };

    private static bool CardOk(ReplacementDef r, string key, Card? c, EffectContext x) =>
        r[key] is not { } f || c is not null && Effect.FilterFor(r.Face, WithoutEmblems(f)).MatchesCard(c, x);

    private static bool PlayerOk(ReplacementDef r, string key, int? p, EffectContext x) =>
        r[key] is not { } f || p is { } pl && Effect.FilterFor(r.Face, f).MatchesPlayer(pl, x);

    /// <summary>
    /// The amount after every applicable amount replacement. When several apply, the affected
    /// player picks the order (rule 616.1); the engine picks the order that helps the simulated player.
    /// </summary>
    public static int Amount(Game g, string evt, int amount, Card? source = null, Card? targetCard = null, int? targetPlayer = null,
        bool combat = false, string? counterType = null)
    {
        if (amount <= 0) return amount;
        var applicable = new List<(Card Host, ReplacementDef R)>();
        foreach (var (host, r) in Active(g, evt))
        {
            if (r.Kind != ReplacementKind.Amount) continue;
            var x = ContextFor(g, host, r, amount);
            if (!Presence.Hold(r.P, x)) continue;
            var ok = evt switch
            {
                "DamageDone" => CardOk(r, "ValidSource", source, x)
                    && (r["ValidTarget"] is null || (targetCard is not null ? CardOk(r, "ValidTarget", targetCard, x) : PlayerOk(r, "ValidTarget", targetPlayer, x)))
                    && (r["IsCombat"] is not { } ic || (ic == "True") == combat),
                "CreateToken" => CardOk(r, "ValidToken", targetCard, x) && PlayerOk(r, "ValidPlayer", targetPlayer, x),
                "AddCounter" => CardOk(r, "ValidCard", targetCard, x) && (r["ValidObject"] is null || CardOk(r, "ValidObject", targetCard, x))
                    && (r["ValidCounterType"] is not { } ct || ct == counterType),
                "GainLife" => PlayerOk(r, "ValidPlayer", targetPlayer, x),
                _ => false,
            };
            if (ok) applicable.Add((host, r));
        }
        if (applicable.Count == 0) return amount;
        var hurtsMe = evt == "DamageDone" && (targetPlayer == Game.Me || targetCard?.Controller == Game.Me)
            || evt == "AddCounter" && targetCard?.Controller != Game.Me;
        var best = amount;
        var first = true;
        foreach (var order in Orders(applicable.Count))
        {
            var n = amount;
            foreach (var i in order)
                n = Math.Max(0, Effect.EvalAmount(ContextFor(g, applicable[i].Host, applicable[i].R, n), applicable[i].R.NewAmount!));
            if (first || (hurtsMe ? n < best : n > best)) best = n;
            first = false;
        }
        return best;
    }

    private static IEnumerable<int[]> Orders(int n)
    {
        if (n > 4) { yield return Enumerable.Range(0, n).ToArray(); yield break; }
        foreach (var p in Permute(Enumerable.Range(0, n).ToList())) yield return p;
    }

    private static IEnumerable<int[]> Permute(List<int> items)
    {
        if (items.Count <= 1) { yield return items.ToArray(); yield break; }
        for (var i = 0; i < items.Count; i++)
        {
            var rest = items.Where((_, j) => j != i).ToList();
            foreach (var p in Permute(rest)) yield return new[] { items[i] }.Concat(p).ToArray();
        }
    }

    /// <summary>
    /// A replacement for a player's draw, if one applies: its host and what happens instead. One that is
    /// already replacing a draw doesn't apply to the draws its own ability makes.
    /// </summary>
    public static (Card Host, ReplacementDef R)? DrawReplacement(Game g, int player, bool firstInDrawStep, ISet<ReplacementDef> applying)
    {
        foreach (var (host, r) in Active(g, "Draw"))
        {
            if (r.Kind != ReplacementKind.Instead || applying.Contains(r)) continue;
            var x = ContextFor(g, host, r, 0);
            if (!PlayerOk(r, "ValidPlayer", player, x)) continue;
            if (r["NotFirstCardInDrawStep"] == "True" && firstInDrawStep) continue;
            if (!Presence.Hold(r.P, x)) continue;
            return (host, r);
        }
        return null;
    }

    /// <summary>Where a card moving to <paramref name="to"/> actually goes (Rest in Peace sends graveyard-bound cards to exile).</summary>
    public static Zone Destination(Game g, Card moving, Zone from, Zone to)
    {
        if (to != Zone.Graveyard) return to;
        foreach (var (host, r) in Active(g, "Moved"))
        {
            if (r.Kind != ReplacementKind.ExileInstead) continue;
            var x = ContextFor(g, host, r, 0);
            if (r["Origin"] is { } o && !o.Split(',').Any(z => z.Trim() == from.ToString())) continue;
            if (!CardOk(r, "ValidCard", moving, x) || !CardOk(r, "ValidLKI", moving, x) || !Presence.Hold(r.P, x)) continue;
            return Zone.Exile;
        }
        return to;
    }
}
