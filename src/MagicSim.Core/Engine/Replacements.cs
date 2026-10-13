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
    /// <summary>Creating tokens also creates others (Chatterfang).</summary>
    AddTokens,
    /// <summary>Creating tokens creates others instead (Divine Visitation, Academy Manufactor).</summary>
    ReplaceTokens,
    /// <summary>A step is skipped ("skip your draw step": Necropotence).</summary>
    SkipStep,
    /// <summary>"If it would be put into a graveyard, shuffle it into its owner's library instead" (Blightsteel Colossus).</summary>
    ShuffleInstead,
    /// <summary>"As you cascade, you may put a land card from among the exiled cards onto the battlefield tapped" (Averna).</summary>
    AsCascade,
    /// <summary>"You can't lose the game this turn" (Angel's Grace): the event doesn't happen.</summary>
    CantHappen,
    /// <summary>"Other creatures you control enter with additional +1/+1 counters" (Kalain): runs as they enter.</summary>
    EntersWithCounters,
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
    /// <summary>For a token replacement: the tokens created instead or as well.</summary>
    public List<CardDef>? Tokens { get; init; }
    public string? this[string key] => P[key];
}

/// <summary>Compiles and applies replacement effects that modify events (rule 614).</summary>
public static class Replacements
{
    private static readonly HashSet<string> Common = new(StringComparer.Ordinal)
    {
        "Event", "Description", "ActiveZones", "ReplaceWith", "Secondary", "AICheckSVar", "AILogic", "PlayerTurn",
    };

    private static readonly Dictionary<string, HashSet<string>> MatchParams = new()
    {
        ["DamageDone"] = new(StringComparer.Ordinal) { "ValidSource", "ValidTarget", "IsCombat" },
        ["CreateToken"] = new(StringComparer.Ordinal) { "ValidToken", "ValidPlayer", "EffectOnly" },
        ["AddCounter"] = new(StringComparer.Ordinal) { "ValidCard", "ValidCounterType", "EffectOnly", "ValidObject" },
        ["GainLife"] = new(StringComparer.Ordinal) { "ValidPlayer" },
        // "Damage that would reduce your life total to less than 1 reduces it to 1 instead" (Angel's Grace).
        ["LifeReduced"] = new(StringComparer.Ordinal) { "ValidPlayer", "IsDamage", "Result" },
        ["Moved"] = new(StringComparer.Ordinal) { "ValidCard", "Origin", "Destination", "ValidLKI", "ReplacementResult" },
        ["Draw"] = new(StringComparer.Ordinal) { "ValidPlayer", "NotFirstCardInDrawStep" },
        // "If you tap a permanent for mana, it produces three times as much of that mana instead" (Nyxbloom Ancient).
        ["ProduceMana"] = new(StringComparer.Ordinal) { "ValidCard", "ValidActivator" },
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
        // "If a player would gain life, that player gains no life instead" (Sulfuric Vortex).
        if (evt == "GainLife" && r["Prevent"] == "True" && r.Values.Keys.All(k => Common.Contains(k) || k is "ValidPlayer" or "Prevent" || Presence.Params.Contains(k)))
        {
            if (r["ValidPlayer"] is { } gvp) Filter.Parse(gvp, ctx);
            face.Replacements.Add(new ReplacementDef { P = r, Event = evt, Kind = ReplacementKind.Amount, Face = face, NewAmount = "0" });
            return true;
        }
        // Effects on what opponents' cards do: goldfish opponents have none.
        foreach (var key in new[] { "ValidCard", "ValidLKI" })
            if (r[key] is { } ovc && ovc.Split(',').All(t => t.Contains("OppCtrl", StringComparison.Ordinal) || t.Contains("OppOwn", StringComparison.Ordinal)))
                return true;

        // "You can't lose the game"; "your opponents can't win the game" (goldfish opponents never win anyway).
        if (evt is "GameLoss" or "GameWin" && r["Layer"] == "CantHappen" && r.Values.Keys.All(k => Common.Contains(k) || k is "ValidPlayer" or "Layer"))
        {
            if (evt == "GameWin") return r["ValidPlayer"] == "Opponent";
            if (r["ValidPlayer"] is { } lvp) Filter.Parse(lvp, ctx);
            face.Replacements.Add(new ReplacementDef { P = r, Event = evt, Kind = ReplacementKind.CantHappen, Face = face });
            return true;
        }
        // "Skip your [step]": BeginPhase with Skip$ True.
        if (evt == "BeginPhase" && r["Skip"] == "True" && r["Phase"] is { } phase && phase.Split(',').All(ph => Enum.GetValues<Step>().Any(st => Conditions.PhaseIs(st, ph.Trim())))
            && r.Values.Keys.All(k => Common.Contains(k) || k is "ValidPlayer" or "Phase" or "Skip" || Presence.Params.Contains(k)))
        {
            if (r["ValidPlayer"] is { } svp) Filter.Parse(svp, ctx);
            Presence.Validate(r, ctx);
            face.Replacements.Add(new ReplacementDef { P = r, Event = evt, Kind = ReplacementKind.SkipStep, Face = face });
            return true;
        }
        // "As you cascade, you may put a land card from among the exiled cards onto the battlefield tapped."
        if (evt == "Cascade" && r.Values.Keys.All(k => Common.Contains(k) || k is "ValidPlayer" or "ReplacementResult")
            && r["ReplaceWith"] is { } cw && face.SVars.TryGetValue(cw, out var cascadeBody) && new ForgeParams(cascadeBody) is var cp
            && cp.Api == "ChangeZone" && cp["Origin"] == "Exile" && cp["Destination"] is "Battlefield" or "Hand"
            && cp["ChooseFromDefined"] is { } cfd && cfd.StartsWith("ReplacedCards.", StringComparison.Ordinal)
            && OnlyKeys(cp, "Origin", "Destination", "ChooseFromDefined", "ChangeNum", "Hidden", "Tapped"))
        {
            Filter.Parse(cfd["ReplacedCards.".Length..], ctx);
            if (r["ValidPlayer"] is { } cvp) Filter.Parse(cvp, ctx);
            if (cp["ChangeNum"] is { } ccn && !int.TryParse(ccn, out _)) return false;
            face.Replacements.Add(new ReplacementDef { P = r, Event = evt, Kind = ReplacementKind.AsCascade, Face = face,
                With = new AbilityDef { P = cp, Api = "ChangeZone", Kind = AbilityKind.Sub } });
            return true;
        }
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
            case "LifeReduced" when w.Api == "ReplaceEffect" && w["VarName"] == "Amount" && OnlyKeys(w, "VarName", "VarValue"):
                kind = ReplacementKind.Amount;
                newAmount = w["VarValue"];
                break;
            case "CreateToken" when w.Api == "ReplaceToken" && w["Type"] is "AddToken" or "ReplaceToken" && OnlyKeys(w, "Type", "Amount", "TokenScript"):
            {
                // "Also create …" (AddToken) or "create … instead" (ReplaceToken).
                kind = w["Type"] == "AddToken" ? ReplacementKind.AddTokens : ReplacementKind.ReplaceTokens;
                var defs = new List<CardDef>();
                foreach (var script in (w["TokenScript"] ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    if (ctx.Tokens?.Invoke(script) is { } td) defs.Add(td); else ctx.Need("TokenScript:" + script);
                if (defs.Count == 0) return false;
                newAmount = w["Amount"] ?? "1";
                face.Replacements.Add(new ReplacementDef { P = r, Event = evt, Kind = kind, Face = face, NewAmount = newAmount, Tokens = defs });
                if (r["ValidToken"] is { } vtok) Filter.Parse(vtok, ctx);
                Engine.Amount.Parse(newAmount, ctx);
                return true;
            }
            case "CreateToken" when w.Api == "ReplaceToken" && w["Type"] == "Amount" && OnlyKeys(w, "Type", "Amount"):
                kind = ReplacementKind.Amount;
                newAmount = "ReplaceCount$Amount/" + (w["Amount"] ?? "Twice");
                if (newAmount.EndsWith("Thrice")) newAmount = newAmount[..^"Thrice".Length] + "Times.3";
                break;
            case "ProduceMana" when w.Api == "ReplaceMana" && int.TryParse(w["ReplaceAmount"], out var times) && OnlyKeys(w, "ReplaceAmount"):
                kind = ReplacementKind.Amount;
                newAmount = "ReplaceCount$Amount/Times." + times;
                if (r["ValidActivator"] is { } va) Filter.Parse(va, ctx);
                break;
            case "AddCounter" when w.Api == "ReplaceCounter" && OnlyKeys(w, "Amount", "ValidCounterType", "ChooseCounter"):
                if (w["ValidCounterType"] is { } wct && r["ValidCounterType"] is { } rct && wct != rct) return false;
                kind = ReplacementKind.Amount;
                newAmount = w["Amount"];
                break;
            // "[It] enters with additional counters", from another permanent or an effect (Kalain, Spark Double).
            case "Moved" when r["Destination"] == "Battlefield" && r["Origin"] is null && w.Api == "PutCounter" && w["ETB"] == "True"
                              && w["Defined"] is "ReplacedCard" or "ReplacedNewCard" or "ReplacedNewCard.Creature"
                              && OnlyKeys(w, "ETB", "Defined", "CounterType", "CounterNum", "SubAbility"):
                kind = ReplacementKind.EntersWithCounters;
                instead = CardCompiler.CompileAbility(w, ctx, AbilityKind.Sub, 0);
                break;
            case "Moved" when r["Destination"] == "Graveyard" && w.Api == "ChangeZone" && w["Destination"] == "Exile"
                              && w["Defined"] == "ReplacedCard" && OnlyKeys(w, "Destination", "Defined", "Origin", "Hidden"):
                kind = ReplacementKind.ExileInstead;
                break;
            case "Moved" when r["Destination"] == "Graveyard" && w.Api == "ChangeZone" && w["Destination"] == "Library" && w["Shuffle"] == "True"
                              && w["Defined"] == "ReplacedCard" && OnlyKeys(w, "Destination", "Defined", "Origin", "Hidden", "Reveal", "Shuffle"):
                kind = ReplacementKind.ShuffleInstead;
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

    /// <summary>"As you cascade": each such replacement may take cards from among those exiled, before the cast.</summary>
    public static void AsCascade(Game g, int player, List<Card> exiled)
    {
        foreach (var (host, r) in Active(g, "Cascade").ToList())
        {
            var x = ContextFor(g, host, r, 0);
            if (host.Controller != player || !PlayerOk(r, "ValidPlayer", player, x)) continue;
            var w = r.With!;
            var filter = Effect.FilterFor(r.Face, w["ChooseFromDefined"]!["ReplacedCards.".Length..]);
            var candidates = exiled.Where(c => c.Zone == Zone.Exile && filter.MatchesCard(c, x)).Select(c => c.Id).ToList();
            if (candidates.Count == 0) continue;
            var n = int.TryParse(w["ChangeNum"], out var cn) ? cn : 1;
            var dest = Enum.Parse<Zone>(w["Destination"]!);
            foreach (var id in g.Decider.ChooseCards(g, player, Choice.ChooseFromRevealed, host, candidates, 0, Math.Min(n, candidates.Count)))
            {
                g.Note($"  {host.Name}: {g.State[id].Name} to {dest.ToString().ToLowerInvariant()}");
                g.MoveTo(g.State[id], dest, tapped: w["Tapped"] == "True");
            }
        }
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

    /// <summary>Another permanent's or effect's "[creatures] enter with additional counters" for <paramref name="entering"/>.</summary>
    public static IEnumerable<(Card Host, AbilityDef With)> EntersWithCounters(Game g, Card entering)
    {
        foreach (var (host, r) in Active(g, "Moved"))
        {
            if (r.Kind != ReplacementKind.EntersWithCounters || host.Id == entering.Id) continue;
            var x = ContextFor(g, host, r, 0);
            if (CardOk(r, "ValidCard", entering, x) && Presence.Hold(r.P, x)) yield return (host, r.With!);
        }
    }

    /// <summary>Permanents and effect objects whose replacement effects apply now.</summary>
    /// <summary>Whether any replacement of this event applies now (a quick check before working out amounts).</summary>
    public static bool AnyActive(Game g, string evt) => Active(g, evt).Any();

    private static IEnumerable<(Card Host, ReplacementDef R)> Active(Game g, string evt)
    {
        foreach (var c in g.State.BattlefieldCards)
            if (!c.IsBlank && !c.LostAbilities)
                foreach (var r in c.CurrentFace.Replacements)
                    if (r.Event == evt && r["ActiveZones"] != "Command" && OnItsTurn(g, c, r)) yield return (c, r);
        foreach (var p in g.State.Players)
            foreach (var id in p.Command)
            {
                var c = g.State[id];
                foreach (var r in c.CurrentFace.Replacements)
                    if (r.Event == evt && (c.IsEffect || r["ActiveZones"] == "Command") && OnItsTurn(g, c, r)) yield return (c, r);
            }
    }

    /// <summary>"During your turn" (PlayerTurn$ True).</summary>
    private static bool OnItsTurn(Game g, Card host, ReplacementDef r) => r["PlayerTurn"] != "True" || g.State.ActivePlayer == host.Controller;

    private static EffectContext ContextFor(Game g, Card host, ReplacementDef r, int replaced) => new()
    {
        Game = g, Ability = Game.PermanentSpell, FaceOverride = r.Face, ReplacedAmount = replaced,
        Item = new StackItem { SourceId = host.Id, Ability = Game.PermanentSpell, Controller = host.Controller },
    };

    private static bool CardOk(ReplacementDef r, string key, Card? c, EffectContext x) =>
        r[key] is not { } f || c is not null && Effect.FilterFor(r.Face, WithoutEmblems(f)).MatchesCard(c, x);

    private static bool PlayerOk(ReplacementDef r, string key, int? p, EffectContext x) =>
        r[key] is not { } f || p is { } pl && Effect.FilterFor(r.Face, f).MatchesPlayer(pl, x);

    /// <summary>Whether a "can't happen" replacement stops this event for this player ("you can't lose the game this turn").</summary>
    public static bool CantHappen(Game g, string evt, int player)
    {
        foreach (var (host, r) in Active(g, evt))
            if (r.Kind == ReplacementKind.CantHappen && PlayerOk(r, "ValidPlayer", player, ContextFor(g, host, r, 0)) && Presence.Hold(r.P, ContextFor(g, host, r, 0)))
                return true;
        return false;
    }

    /// <summary>
    /// The amount after every applicable amount replacement. When several apply, the affected
    /// player picks the order (rule 616.1); the engine picks the order that helps the simulated player.
    /// </summary>
    public static int Amount(Game g, string evt, int amount, Card? source = null, Card? targetCard = null, int? targetPlayer = null,
        bool combat = false, string? counterType = null, bool damage = false)
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
                "ProduceMana" => CardOk(r, "ValidCard", source, x) && PlayerOk(r, "ValidActivator", targetPlayer, x),
                "LifeReduced" => PlayerOk(r, "ValidPlayer", targetPlayer, x) && (r["IsDamage"] != "True" || damage)
                    && (r["Result"] is not { } res || Filter.Compare(g.State.Players[targetPlayer!.Value].Life - amount, res[..2], int.Parse(res[2..]))),
                _ => false,
            };
            if (ok) applicable.Add((host, r));
        }
        if (applicable.Count == 0) return amount;
        var hurtsMe = evt == "DamageDone" && (targetPlayer == Game.Me || targetCard?.Controller == Game.Me)
            || evt == "AddCounter" && targetCard?.Controller != Game.Me || evt == "LifeReduced" && targetPlayer == Game.Me;
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

    /// <summary>
    /// The tokens an effect creating <paramref name="n"/> of <paramref name="def"/> really creates: replacing
    /// (Divine Visitation) and adding (Chatterfang) token replacements, then amount ones (Doubling Season).
    /// </summary>
    public static List<(CardDef Def, int Count)> Tokens(Game g, CardDef def, int owner, int n)
    {
        var probe = new Card { Id = g.State.Cards.Count, Def = def, Owner = owner, Controller = owner, Zone = Zone.Battlefield, IsToken = true };
        var result = new List<(CardDef, int)> { (def, n) };
        foreach (var (host, r) in Active(g, "CreateToken"))
        {
            if (r.Kind is not (ReplacementKind.AddTokens or ReplacementKind.ReplaceTokens)) continue;
            var x = ContextFor(g, host, r, n);
            if (!CardOk(r, "ValidToken", probe, x) || !PlayerOk(r, "ValidPlayer", owner, x) || !Presence.Hold(r.P, x)) continue;
            var extra = r.Kind == ReplacementKind.AddTokens ? Effect.EvalAmount(x, r.NewAmount!) : n;
            if (r.Kind == ReplacementKind.ReplaceTokens) result.Clear();
            foreach (var t in r.Tokens!) result.Add((t, extra));
        }
        return result.Select(t => (t.Item1, Amount(g, "CreateToken", t.Item2,
            targetCard: new Card { Id = g.State.Cards.Count, Def = t.Item1, Owner = owner, Controller = owner, Zone = Zone.Battlefield, IsToken = true },
            targetPlayer: owner))).ToList();
    }

    /// <summary>Whether <paramref name="player"/> skips <paramref name="step"/> of their turn.</summary>
    public static bool SkipsStep(Game g, int player, Step step)
    {
        foreach (var (host, r) in Active(g, "BeginPhase"))
        {
            if (r.Kind != ReplacementKind.SkipStep || !r["Phase"]!.Split(',').Any(ph => Conditions.PhaseIs(step, ph.Trim()))) continue;
            var x = ContextFor(g, host, r, 0);
            if (PlayerOk(r, "ValidPlayer", player, x) && Presence.Hold(r.P, x)) return true;
        }
        return false;
    }

    /// <summary>Where a card moving to <paramref name="to"/> actually goes (Rest in Peace sends graveyard-bound cards to exile).</summary>
    public static Zone Destination(Game g, Card moving, Zone from, Zone to, out bool shuffle)
    {
        shuffle = false;
        if (from == Zone.Battlefield && moving.ExileIfLeaves && to != Zone.Exile) return Zone.Exile;
        if (to != Zone.Graveyard) return to;
        // A card's own "shuffle it into its owner's library instead" works from anywhere.
        if (!moving.IsBlank)
            foreach (var r in moving.CurrentFace.Replacements)
                if (r.Kind == ReplacementKind.ShuffleInstead && r["ValidCard"] == "Card.Self")
                {
                    shuffle = true;
                    return Zone.Library;
                }
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
