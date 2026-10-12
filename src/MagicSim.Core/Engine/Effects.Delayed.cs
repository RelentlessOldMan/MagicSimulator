using MagicSim.Core.Forge;

namespace MagicSim.Core.Engine;

/// <summary>A delayed triggered ability waiting for its event (rule 603.7); it triggers once.</summary>
public sealed record DelayedTrigger(TriggerDef Trigger, int HostId, int Controller, List<int> Remembered, int Turn, bool ThisTurn, bool NextTurn,
    List<int>? Players = null, int Number = 0, int? OnlyTurn = null);

/// <summary>"At the beginning of the next end step, …", "When you next cast …": sets up a delayed trigger.</summary>
sealed class DelayedTriggerEffect : Effect
{
    public override string Api => "DelayedTrigger";

    /// <summary>The effect's own parameters; the rest describe the trigger.</summary>
    private static readonly string[] Own = { "RememberObjects", "ThisTurn", "NextTurn", "RememberNumber" };

    private static readonly string[] TriggerKeys =
    {
        "Mode", "Phase", "ValidPlayer", "Execute", "TriggerDescription", "ValidCard", "Origin", "Destination", "Static",
        "ValidActivatingPlayer", "ValidSource", "ValidTarget", "CombatDamage", "OptionalDecider",
    };

    public override IReadOnlySet<string> Params { get; } = P(Own.Concat(TriggerKeys).ToArray());

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        var keys = TriggerKeys.Where(k => ab[k] is not null);
        var p = new ForgeParams(string.Join(" | ", keys.Select(k => $"{k}$ {ab[k]}")));
        ab.Delayed = CardCompiler.CompileTrigger(p, ctx);
        if (ab.Delayed.Execute is null) ctx.Need("DelayedTrigger:NoExecute");
        if (ab["RememberObjects"] is { } ro && Defined.IsPlayerRef(ro)) Defined.ValidatePlayers(ro, ctx);
        else Defined.ValidateCards(ab["RememberObjects"], ctx);
    }

    public override void Resolve(EffectContext x)
    {
        var ab = x.Ability;
        var ro = ab["RememberObjects"];
        var players = ro is not null && Defined.IsPlayerRef(ro);
        var remembered = ro is not null && !players ? Defined.Cards(ro, x) : new List<int>();
        // "RememberNumber$ True": the number the card remembers (Mana Drain: the countered spell's mana value).
        var number = ab["RememberNumber"] == "True" ? x.Host.StoredVars?.GetValueOrDefault(Card.RememberedNumberKey) ?? 0 : 0;
        x.S.Delayed.Add(new DelayedTrigger(ab.Delayed!, x.Host.Id, x.Controller, remembered, x.S.Turn, ab["ThisTurn"] == "True", ab["NextTurn"] == "True",
            players ? Defined.Players(ro, x) : null, number));
        x.Game.Note($"  delayed trigger: {ab.Delayed!["TriggerDescription"] ?? ab.Delayed.Mode}");
    }
}

/// <summary>"For each player / each card, …": runs RepeatSubAbility$ once per player or card, remembering it.</summary>
sealed class RepeatEachEffect : Effect
{
    public override string Api => "RepeatEach";
    public override IReadOnlySet<string> Params { get; } = P(
        "RepeatSubAbility", "RepeatPlayers", "RepeatCards", "DefinedCards", "Zone", "StartingWith", "ClearRememberedBeforeLoop",
        "UseImprinted", "ChangeZoneTable", "DamageMap",
        // The order only matters when it puts cards on top of the library; they go in the order chosen.
        "ChooseOrder");

    private static readonly HashSet<string> Zones = new(StringComparer.Ordinal) { "Battlefield", "Graveyard", "Exile", "Hand", "Library" };

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        if (CardCompiler.CompileSideChain(ab, "RepeatSubAbility", ctx) is null) ctx.Need("RepeatEach:NoSubAbility");
        if (ab["RepeatPlayers"] is { } rp) Filter.Parse(rp, ctx);
        else if (ab["RepeatCards"] is { } rc) Filter.Parse(rc, ctx);
        else if (ab["DefinedCards"] is { } dc) Defined.ValidateCards(dc, ctx);
        else ctx.Need("RepeatEach:NothingToRepeat");
        if (ab["Zone"] is { } z && !Zones.Contains(z)) ctx.Need("RepeatEach.Zone:" + z);
        if (ab["StartingWith"] is { } sw && sw != "You") ctx.Need("RepeatEach.StartingWith:" + sw);
    }

    public override void Resolve(EffectContext x)
    {
        var ab = x.Ability;
        var chain = ab.SideChains!["RepeatSubAbility"];
        if (ab["ClearRememberedBeforeLoop"] == "True") x.Host.Remembered = null;
        if (ab["RepeatPlayers"] is { } rp)
        {
            var filter = FilterFor(x.SVarFace, rp);
            var n = x.S.Players.Length;
            var saved = x.Item.IteratedPlayer;
            // Turn order, starting with the controller.
            foreach (var p in Enumerable.Range(0, n).Select(i => (x.Controller + i) % n).ToList())
            {
                if (x.S.Players[p].Lost || !filter.MatchesPlayer(p, x)) continue;
                x.Item.IteratedPlayer = p;
                x.Game.RunSideChain(x, chain);
                if (x.S.GameOver) break;
            }
            x.Item.IteratedPlayer = saved;
            return;
        }
        List<int> cards;
        if (ab["RepeatCards"] is { } rc)
        {
            var filter = FilterFor(x.SVarFace, rc);
            var zone = ab["Zone"] is { } z ? Enum.Parse<Zone>(z) : Zone.Battlefield;
            cards = x.Game.CardsIn(zone).Where(c => filter.MatchesCard(c, x)).Select(c => c.Id).ToList();
        }
        else cards = Defined.Cards(ab["DefinedCards"], x);
        var imprint = ab["UseImprinted"] == "True";
        foreach (var id in cards)
        {
            // Each card is "remembered" (or imprinted) while its iteration runs.
            var saved = imprint ? x.Host.Imprinted : x.Host.Remembered;
            if (imprint) x.Host.Imprinted = new() { id }; else x.Host.Remembered = new() { id };
            x.Game.RunSideChain(x, chain);
            // Keep whatever the iteration remembered besides the card itself.
            var after = (imprint ? x.Host.Imprinted : x.Host.Remembered) ?? new();
            var merged = (saved ?? new()).Concat(after.Where(r => r != id)).Distinct().ToList();
            if (imprint) x.Host.Imprinted = merged.Count > 0 ? merged : null;
            else x.Host.Remembered = merged.Count > 0 ? merged : null;
            if (x.S.GameOver) break;
        }
    }
}

/// <summary>
/// "An opponent separates those cards into two piles. Put one pile into your hand and the other into your graveyard"
/// (Fact or Fiction): ChosenPile$ runs with the chosen pile remembered, UnchosenPile$ with the other. A goldfish
/// opponent separates the card with the highest mana value from the rest; when you separate, you choose any pile.
/// </summary>
sealed class TwoPilesEffect : Effect
{
    public override string Api => "TwoPiles";
    public override IReadOnlySet<string> Params { get; } = P("Defined", "DefinedCards", "Separator", "Chooser", "ChosenPile", "UnchosenPile", "AILogic");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        if (ab["Defined"] is { } d && d != "You") ctx.Need("TwoPiles.Defined:" + d);
        if (ab["Chooser"] is { } ch && ch != "You") ctx.Need("TwoPiles.Chooser:" + ch);
        if (ab["Separator"] is { } sep && sep is not ("You" or "Opponent")) ctx.Need("TwoPiles.Separator:" + sep);
        if (ab["DefinedCards"] is not { } dc) ctx.Need("TwoPiles:NoDefinedCards");
        else Defined.ValidateCards(dc, ctx);
        if (ab["ChosenPile"] is not null && CardCompiler.CompileSideChain(ab, "ChosenPile", ctx) is null) ctx.Need("TwoPiles:NoChosenPile");
        if (ab["UnchosenPile"] is not null && CardCompiler.CompileSideChain(ab, "UnchosenPile", ctx) is null) ctx.Need("TwoPiles:NoUnchosenPile");
    }

    public override void Resolve(EffectContext x)
    {
        var ab = x.Ability;
        var cards = Defined.Cards(ab["DefinedCards"], x);
        if (cards.Count == 0) return;
        List<int> chosen;
        if (ab["Separator"] == "Opponent")
        {
            var alone = cards.OrderByDescending(id => x.S[id].Types.Is(CardType.Land) ? -1 : x.S[id].ManaValue).First();
            var rest = cards.Where(id => id != alone).ToList();
            string Pile(IEnumerable<int> ids) => ids.Any() ? string.Join(", ", ids.Select(id => x.S[id].Name)) : "nothing";
            var pick = x.Game.Decider.ChooseOption(x.Game, x.Controller, x.Host, new[] { Pile(new[] { alone }), Pile(rest) });
            chosen = pick == 0 ? new() { alone } : rest;
        }
        else chosen = x.Game.Decider.ChooseCards(x.Game, x.Controller, Choice.ChooseFromRevealed, x.Host, cards, 0, cards.Count);
        var unchosen = cards.Where(id => !chosen.Contains(id)).ToList();
        x.Game.Note($"  piles: take {string.Join(", ", chosen.Select(id => x.S[id].Name))}");
        foreach (var (key, pile) in new[] { ("ChosenPile", chosen), ("UnchosenPile", unchosen) })
        {
            if (ab.SideChains?.GetValueOrDefault(key) is not { } chain) continue;
            x.Host.Remembered = pile.Count > 0 ? new List<int>(pile) : null;
            x.Game.RunSideChain(x, chain);
            if (x.S.GameOver) return;
        }
    }
}

/// <summary>"When you do, …" (reflexive trigger, rule 603.12): optionally pays Cost$, then triggers Execute$ at once.</summary>
sealed class ImmediateTriggerEffect : Effect
{
    public override string Api => "ImmediateTrigger";
    public override IReadOnlySet<string> Params { get; } = P("Execute", "TriggerDescription", "RememberObjects", "OptionalDecider");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        if (ab["Execute"] is not { } name || !ctx.Face.SVars.TryGetValue(name, out var body)) { ctx.Need("ImmediateTrigger:NoExecute"); return; }
        var exec = CardCompiler.CompileAbility(new ForgeParams(body), ctx, AbilityKind.TriggerEffect, 0);
        var modes = exec is null ? null : CardCompiler.ExpandModes(exec, ctx);
        ab.Delayed = new TriggerDef { P = new ForgeParams("Mode$ Immediate"), Mode = "Immediate", Execute = modes?.FirstOrDefault(), Modes = modes, Face = ctx.Face };
        Defined.ValidateCards(ab["RememberObjects"], ctx);
    }

    public override void Resolve(EffectContext x)
    {
        var ab = x.Ability;
        // "You may pay …. When you do, …" (on a spell or activated ability, Cost$ is what it cost to use)
        if (ab["Cost"] is not null && ab.Kind is AbilityKind.Sub or AbilityKind.TriggerEffect)
        {
            if (!x.Game.CanPayCost(ab.Cost, x.Host, x.Controller, 0, excludeSourceFromMana: false)) return;
            if (!x.Game.Decider.ChooseYesNo(x.Game, x.Controller, x.Host, $"pay for {x.Host.Name}?")) return;
            if (!x.Game.PayCost(ab.Cost, x.Host, x.Controller, 0, excludeSourceFromMana: false)) return;
        }
        var remembered = ab["RememberObjects"] is { } ro ? Defined.Cards(ro, x) : null;
        x.Game.AddImmediateTrigger(x.Host, ab.Delayed!, x.Controller, remembered, x.Item.TriggeredPlayer);
    }
}

/// <summary>Suspends the card from hand: exiled with N time counters (rule 702.62).</summary>
sealed class SuspendEffect : Effect
{
    public override string Api => "Suspend";
    public override IReadOnlySet<string> Params { get; } = P("TimeCounters");

    public override void Resolve(EffectContext x)
    {
        var c = x.Host;
        if (c.Zone != Zone.Hand) return;
        x.Game.MoveTo(c, Zone.Exile);
        Game.AddCounters(c, "TIME", int.Parse(x.Ability["TimeCounters"]!));
        c.Suspended = true;
        x.Game.Note($"  suspend {c.Name} ({c.CounterCount("TIME")} time counters)");
    }
}
