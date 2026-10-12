using MagicSim.Core.Forge;

namespace MagicSim.Core.Engine;

/// <summary>A delayed triggered ability waiting for its event (rule 603.7); it triggers once.</summary>
public sealed record DelayedTrigger(TriggerDef Trigger, int HostId, int Controller, List<int> Remembered, int Turn, bool ThisTurn, bool NextTurn);

/// <summary>"At the beginning of the next end step, …", "When you next cast …": sets up a delayed trigger.</summary>
sealed class DelayedTriggerEffect : Effect
{
    public override string Api => "DelayedTrigger";

    /// <summary>The effect's own parameters; the rest describe the trigger.</summary>
    private static readonly string[] Own = { "RememberObjects", "ThisTurn", "NextTurn" };

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
        Defined.ValidateCards(ab["RememberObjects"], ctx);
    }

    public override void Resolve(EffectContext x)
    {
        var ab = x.Ability;
        var remembered = ab["RememberObjects"] is { } ro ? Defined.Cards(ro, x) : new List<int>();
        x.S.Delayed.Add(new DelayedTrigger(ab.Delayed!, x.Host.Id, x.Controller, remembered, x.S.Turn, ab["ThisTurn"] == "True", ab["NextTurn"] == "True"));
        x.Game.Note($"  delayed trigger: {ab.Delayed!["TriggerDescription"] ?? ab.Delayed.Mode}");
    }
}

/// <summary>"For each player / each card, …": runs RepeatSubAbility$ once per player or card, remembering it.</summary>
sealed class RepeatEachEffect : Effect
{
    public override string Api => "RepeatEach";
    public override IReadOnlySet<string> Params { get; } = P(
        "RepeatSubAbility", "RepeatPlayers", "RepeatCards", "DefinedCards", "Zone", "StartingWith", "ClearRememberedBeforeLoop",
        "UseImprinted", "ChangeZoneTable", "DamageMap");

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
