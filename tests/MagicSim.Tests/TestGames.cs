using MagicSim.Core.Engine;
using MagicSim.Core.Players;

namespace MagicSim.Tests;

/// <summary>A decider that follows a test's wishes and otherwise passes / picks the first option.</summary>
internal sealed class TestDecider : ChoiceDecider
{
    public Func<Game, Decision, int?>? Choose { get; set; }

    protected override int Pick(Game g, Decision d) => Choose?.Invoke(g, d) ?? 0;

    public bool Quiet { get; set; }

    public override bool PassesQuietSteps => Quiet;

    /// <summary>Index of the first option that is a card (or target card) with this name.</summary>
    public static int Named(Game g, Decision d, string name) => d.Options.ToList().FindIndex(o => o switch
    {
        int id => g.State[id].Name == name,
        TargetRef { CardId: { } id } => g.State[id].Name == name,
        GameAction.Cast c => g.State[c.CardId].Name == name,
        GameAction.Activate a => g.State[a.CardId].Name == name,
        _ => false,
    });
}

/// <summary>Builds small hand-arranged games for rules tests.</summary>
internal static class TestGames
{
    /// <summary>A game in main phase 1 of turn 1 with the given hand, battlefield and library (top first).</summary>
    public static Game Setup(CardPool pool, string[] hand, string[]? battlefield = null, string[]? library = null, TestDecider? decider = null,
        string[]? commanders = null)
    {
        CardDef Def(string name) => pool.Resolve(name) ?? throw new ArgumentException(name);
        var lib = (library ?? Array.Empty<string>()).Concat(hand).Concat(battlefield ?? Array.Empty<string>()).Select(Def).ToList();
        var g = Game.NewGoldfish(lib, (commanders ?? Array.Empty<string>()).Select(Def).ToList(), new GameSettings(), new Rng(1), decider ?? new TestDecider());
        var s = g.State;
        var me = s.Players[Game.Me];
        // Rebuild zones deterministically instead of using the shuffle (commanders come first).
        me.Library.Clear();
        var next = commanders?.Length ?? 0;
        foreach (var _ in library ?? Array.Empty<string>()) { me.Library.Add(next); s[next].Zone = Zone.Library; next++; }
        foreach (var _ in hand) { me.Hand.Add(next); s[next].Zone = Zone.Hand; next++; }
        foreach (var _ in battlefield ?? Array.Empty<string>()) { s.Battlefield.Add(next); s[next].Zone = Zone.Battlefield; next++; }
        s.Turn = 1;
        s.Step = Step.Main1;
        return g;
    }

    public static int InHand(Game g, string name) => g.State.Players[Game.Me].Hand.First(id => g.State[id].Name == name || g.State[id].Def.Name == name);

    public static Card OnBattlefield(Game g, string name) => g.State.BattlefieldCards.First(c => c.Name == name);

    /// <summary>Resolves the whole stack, checking state-based actions and triggers like the priority loop does.</summary>
    public static void ResolveAll(Game g)
    {
        var d = (TestDecider)g.Decider;
        var saved = d.Choose;
        // Pass priority whenever asked, but let the test's other choices through.
        d.Choose = (game, dec) => dec.Kind == DecisionKind.Action ? 0 : saved?.Invoke(game, dec);
        g.PriorityLoop();
        d.Choose = saved;
    }
}
