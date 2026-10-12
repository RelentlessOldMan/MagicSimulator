using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using MagicSim.Core.Players;
using MagicSim.Core.Sim;

namespace MagicSim.Tests;

/// <summary>
/// Rules tests on a tiny hand-written card pool (Forge script format; nothing copied from Forge).
/// </summary>
public class EngineTests
{
    private static readonly string[] Scripts =
    {
        "Name:Bog\nManaCost:no cost\nTypes:Basic Land Swamp\n",
        "Name:Isle\nManaCost:no cost\nTypes:Basic Land Island\n",
        "Name:Rock\nManaCost:1\nTypes:Artifact\nA:AB$ Mana | Cost$ T | Produced$ C | Amount$ 2\n",
        "Name:Petal\nManaCost:0\nTypes:Artifact\nA:AB$ Mana | Cost$ T Sac<1/CARDNAME> | Produced$ Any\n",
        "Name:Ritual\nManaCost:B\nTypes:Instant\nA:SP$ Mana | Produced$ B | Amount$ 3\n",
        "Name:Seek\nManaCost:1 B\nTypes:Sorcery\nA:SP$ ChangeZone | Origin$ Library | Destination$ Hand | ChangeType$ Card | ChangeNum$ 1 | Mandatory$ True\n",
        "Name:Filler\nManaCost:9\nTypes:Sorcery\nA:SP$ Draw | NumCards$ 1\n",
        // Exile your library until the named card (or everything, if it isn't there).
        "Name:Consult\nManaCost:B\nTypes:Instant\nA:SP$ NameCard | Defined$ You | SubAbility$ Dig6\n" +
        "SVar:Dig6:DB$ Dig | Defined$ You | DigNum$ 6 | ChangeNum$ All | DestinationZone$ Exile | SubAbility$ Until\n" +
        "SVar:Until:DB$ DigUntil | Valid$ Card.NamedCard | FoundDestination$ Hand | RevealedDestination$ Exile\n",
        // When it enters, win if devotion to blue ≥ library size.
        "Name:Seer\nManaCost:U U\nTypes:Creature Merfolk Wizard\nPT:1/3\n" +
        "T:Mode$ ChangesZone | ValidCard$ Card.Self | Origin$ Any | Destination$ Battlefield | Execute$ TrigWin\n" +
        "SVar:TrigWin:DB$ WinsGame | Defined$ You | ConditionCheckSVar$ Lib | ConditionSVarCompare$ LEDev\n" +
        "SVar:Lib:Count$ValidLibrary Card.YouOwn\nSVar:Dev:Count$Devotion.Blue\n",
        "Name:Drain\nManaCost:2 B B\nTypes:Sorcery\nK:Storm\n" +
        "A:SP$ LoseLife | ValidTgts$ Player | LifeAmount$ 2 | SubAbility$ Gain\nSVar:Gain:DB$ GainLife | Defined$ You | LifeAmount$ 2\n",
        "Name:Bear\nManaCost:2\nTypes:Creature Bear\nPT:2/2\nK:Haste\n",
        "Name:Chant\nManaCost:0\nTypes:Artifact\nA:AB$ GainLife | Cost$ 0 | Defined$ You | LifeAmount$ 1\n",
        // Gaining life makes it gain more: a loop nothing can stop.
        "Name:Echo Spring\nManaCost:0\nTypes:Enchantment\n" +
        "T:Mode$ LifeGained | ValidPlayer$ You | TriggerZones$ Battlefield | Execute$ TrigGain\nSVar:TrigGain:DB$ GainLife | Defined$ You | LifeAmount$ 1\n",
    };

    private static readonly ForgeLibrary Lib = ForgeLibrary.FromScripts(Scripts);
    private static readonly CardPool Pool = new(Lib, null);

    private static CardDef Def(string name) => Pool.Resolve(name) ?? throw new ArgumentException(name);

    private static Game Setup(string[] hand, string[]? battlefield = null, string[]? library = null, TestDecider? decider = null) =>
        TestGames.Setup(Pool, hand, battlefield, library, decider);

    private static int InHand(Game g, string name) => TestGames.InHand(g, name);

    [Fact]
    public void ManaPaymentHandlesGenericColoredHybridAndPhyrexian()
    {
        var pool = new ManaPool();
        pool.Add(ManaColor.B, 2);
        pool.Add(ManaColor.U, 1);
        Assert.True(ManaPayment.TryPay(ref pool, ManaCost.Parse("1 B")!, 0, out _));
        Assert.Equal(1, pool.Total);

        var p2 = new ManaPool();
        p2.Add(ManaColor.R);
        Assert.True(ManaPayment.TryPay(ref p2, ManaCost.Parse("UR")!, 0, out _));    // hybrid
        var p3 = new ManaPool();
        Assert.True(ManaPayment.TryPay(ref p3, ManaCost.Parse("UP")!, 0, out var life));  // phyrexian with life
        Assert.Equal(2, life);
        var p4 = new ManaPool();
        p4.Add(ManaColor.G);
        Assert.False(ManaPayment.TryPay(ref p4, ManaCost.Parse("B")!, 0, out _));
        Assert.Equal(4, ManaCost.Parse("X U U R R")!.ManaValue);
    }

    [Fact]
    public void AutoPayTapsLandsBeforeSacrificingPetal()
    {
        var g = Setup(new[] { "Seek" }, battlefield: new[] { "Bog", "Bog", "Petal" }, library: new[] { "Filler" });
        g.CastSpell(g.State[InHand(g, "Seek")], 0);
        Assert.Contains(g.State.BattlefieldCards, c => c.Name == "Petal");
        Assert.All(g.State.BattlefieldCards.Where(c => c.Name == "Bog"), c => Assert.True(c.Tapped));
    }

    [Fact]
    public void RitualThenTutorFindsTheCard()
    {
        var decider = new TestDecider();
        var g = Setup(new[] { "Ritual", "Seek" }, battlefield: new[] { "Bog" }, library: new[] { "Filler", "Seer", "Filler" }, decider);
        g.CastSpell(g.State[InHand(g, "Ritual")], 0);
        g.ResolveTop();
        Assert.Equal(3, g.State.Players[Game.Me].Pool[ManaColor.B]);
        decider.Choose = (game, d) => d.Kind == DecisionKind.Card
            ? d.Options.ToList().FindIndex(o => o is int id && game.State[id].Name == "Seer")
            : null;
        g.CastSpell(g.State[InHand(g, "Seek")], 0);
        g.ResolveTop();
        Assert.Contains(g.State.Players[Game.Me].Hand, id => g.State[id].Name == "Seer");
        Assert.Equal(1, g.State.Players[Game.Me].Pool.Total);   // BBB - {1}{B} = 1 left
    }

    [Fact]
    public void ConsultationInResponseToOracleTriggerWins()
    {
        // Seer on the stack resolves; its enter trigger goes on the stack; in response,
        // Consult naming a card not in the deck exiles the library; then the trigger wins.
        var g = Setup(new[] { "Seer", "Consult" }, battlefield: new[] { "Isle", "Isle", "Bog" },
            library: new[] { "Filler", "Filler", "Filler", "Filler", "Filler", "Filler", "Filler", "Filler" });
        g.CastSpell(g.State[InHand(g, "Seer")], -1);
        g.ResolveTop();                         // Seer enters; trigger pending
        g.CheckStateBasedActions();
        Assert.True(g.HasPendingTriggers);

        var decider = (TestDecider)g.Decider;
        var step = 0;
        decider.Choose = (game, d) =>
        {
            if (d.Kind != DecisionKind.Action) return null;
            step++;
            // First: cast Consult in response. Then pass until everything resolves.
            if (step == 1) return d.Options.ToList().FindIndex(o => o is GameAction.Cast c && game.State[c.CardId].Name == "Consult");
            return 0;
        };
        g.PriorityLoop();
        Assert.Empty(g.State.Players[Game.Me].Library);
        Assert.True(g.Won);
        Assert.Equal("Seer", g.State.WinReason);
    }

    [Fact]
    public void SeerWithoutEmptyLibraryDoesNotWin()
    {
        var g = Setup(new[] { "Seer" }, battlefield: new[] { "Isle", "Isle" }, library: new[] { "Filler", "Filler", "Filler" });
        g.CastSpell(g.State[InHand(g, "Seer")], -1);
        g.PriorityLoop();
        Assert.False(g.State.GameOver);
    }

    [Fact]
    public void StormCopiesForEachEarlierSpell()
    {
        var decider = new TestDecider
        {
            // Drain and its copies target an opponent.
            Choose = (game, d) => d.Kind == DecisionKind.Target ? d.Options.ToList().FindIndex(o => o is TargetRef { Player: 1 }) : null,
        };
        var g = Setup(new[] { "Ritual", "Ritual", "Drain" }, battlefield: new[] { "Bog", "Bog" }, decider: decider);
        foreach (var _ in new[] { 0, 1 })
        {
            g.CastSpell(g.State[InHand(g, "Ritual")], 0);
            g.ResolveTop();
        }
        g.CastSpell(g.State[InHand(g, "Drain")], 0);
        Assert.Equal(3, g.State.Stack.Count);   // original + 2 copies
        while (g.State.Stack.Count > 0) g.ResolveTop();
        Assert.Equal(40 - 6, g.State.Players[1].Life);
        Assert.Equal(40 + 6, g.State.Players[Game.Me].Life);
    }

    [Fact]
    public void LondonMulliganFirstOneIsFree()
    {
        var lib = Enumerable.Repeat("Bog", 60).Select(Def).ToList();
        var mulls = 0;
        var decider = new TestDecider { Choose = (g, d) => d.Kind == DecisionKind.Keep ? (mulls++ < 2 ? 1 : 0) : null };
        var game = Game.NewGoldfish(lib, Array.Empty<CardDef>(), new GameSettings(), new Rng(7), decider);
        game.Mulligans();
        Assert.Equal(2, game.MulligansTaken);
        Assert.Equal(6, game.State.Players[Game.Me].Hand.Count);   // 7 - (2 - 1 free)
        Assert.Single(game.Bottomed);
    }

    [Fact]
    public void HasteCreatureAttacksTheWeakestOpponent()
    {
        var g = Setup(new[] { "Bear" }, battlefield: new[] { "Bog", "Bog" });
        g.State.Players[2].Life = 10;
        g.CastSpell(g.State[InHand(g, "Bear")], -1);
        g.ResolveTop();
        g.ResumeTurn();   // finish the turn: combat happens
        Assert.Equal(8, g.State.Players[2].Life);
        Assert.Equal(40, g.State.Players[1].Life);
    }

    [Fact]
    public void DeterminizeKeepsKnownTopAndShufflesTheRest()
    {
        var g = Setup(Array.Empty<string>(), library: Enumerable.Repeat("Filler", 30).ToArray());
        var me = g.State.Players[Game.Me];
        me.KnownTop = 3;
        var before = me.Library.ToList();
        var rng = new Rng(99);
        g.Determinize(Game.Me, ref rng);
        Assert.Equal(before.Take(3), me.Library.Take(3));
        Assert.NotEqual(before.Skip(3), me.Library.Skip(3));
        Assert.Equal(before.Order(), me.Library.Order());
    }

    [Fact]
    public void SameSeedSameGame()
    {
        var deck = Enumerable.Repeat("Bog", 30).Concat(Enumerable.Repeat("Bear", 30)).Select(Def).ToList();
        string Play()
        {
            var g = Game.NewGoldfish(deck, Array.Empty<CardDef>(), new GameSettings { MaxTurn = 5 }, Rng.ForGame(5, 3),
                new SearchDecider(new SearchSettings { Rollouts = 2 }, 11));
            g.Log = new List<string>();
            g.PlayGame();
            return string.Join("\n", g.Log);
        }
        Assert.Equal(Play(), Play());
    }

    [Fact]
    public void UnsupportedCardsBecomeBlanks()
    {
        var lib = ForgeLibrary.FromScripts(new[] { "Name:Weird\nManaCost:1\nTypes:Artifact\nA:AB$ Frobnicate | Cost$ T\n" });
        var pool = new CardPool(lib, null);
        Assert.Contains("Api:Frobnicate", pool.Compile("Weird")!.Missing);
        Assert.True(pool.Resolve("Weird")!.IsBlank);
    }

    [Fact]
    public void RunsBothTableSizesOnTheSameShuffles()
    {
        var library = Enumerable.Repeat(Def("Bog"), 50).Concat(Enumerable.Repeat(Def("Bear"), 49)).ToList();
        var deck = new Runner.PreparedDeck("Bears", library, new List<CardDef>(), new List<string>(), 99);
        var o = new RunOptions { Games = 3, MaxTurn = 3, Mulligans = new[] { MulliganPolicy.C }, Rollouts = 1, Threads = 1 };
        var games = Runner.Run(deck, o);
        Assert.Equal(new[] { "1v1", "1v1v1" }, games.Select(g => g.Table).Distinct());
        Assert.All(games.Where(g => g.Table == "1v1"), g => Assert.Single(g.OpponentsLifeEnd));
        Assert.All(games.Where(g => g.Table == "1v1v1"), g => Assert.Equal(2, g.OpponentsLifeEnd.Count));
        // Same shuffle, same opening hand at both sizes.
        foreach (var g in games.Where(g => g.Table == "1v1"))
            Assert.Equal(g.HandKept, games.Single(h => h.Table == "1v1v1" && h.Game == g.Game && h.Seat == g.Seat).HandKept);

        var doc = System.Text.Json.JsonSerializer.SerializeToElement(Report.RunDocument(deck, "bears.txt", o, games), Report.Json);
        var rows = doc.GetProperty("results").EnumerateArray().Select(r => (r.GetProperty("table").GetString()!, r.GetProperty("seat").GetString()!)).ToList();
        Assert.Equal(new[] { ("1v1", "play"), ("1v1", "draw"), ("1v1", "weighted"), ("1v1v1", "play"), ("1v1v1", "draw"), ("1v1v1", "weighted") }, rows);
        Assert.Equal(0.5, Report.PlayWeight("1v1"));
        Assert.Equal(1.0 / 3, Report.PlayWeight("1v1v1"));

        var html = HtmlReport.Build(new[] { doc }, new DateTime(2026, 1, 1));
        Assert.Contains("<h3>1v1</h3>", html);
        Assert.Contains("<h3>1v1v1</h3>", html);
        Assert.Contains("</span>Bears</td>", html);
        Assert.DoesNotContain("Forge playtest", html);
    }

    [Fact]
    public void QuietDecidersOnlyActInMainPhasesOrWithAStack()
    {
        foreach (var quiet in new[] { false, true })
        {
            var steps = new HashSet<Step>();
            var d = new TestDecider { Quiet = quiet, Choose = (game, dec) => { if (dec.Kind == DecisionKind.Action) steps.Add(game.State.Step); return null; } };
            var g = Setup(new[] { "Ritual" }, battlefield: new[] { "Bog" }, decider: d);
            g.ResumeTurn();
            if (quiet) Assert.Equal(new[] { Step.Main1, Step.Main2 }, steps.Order());
            else Assert.Contains(Step.End, steps);   // the instant could be cast at the end step
        }
    }

    [Fact]
    public void ActionLimitStopsALoop()
    {
        var d = new TestDecider { Choose = (game, dec) => dec.Kind == DecisionKind.Action && TestDecider.Named(game, dec, "Chant") is var i and >= 0 ? i : null };
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Chant" }, decider: d);
        g.ActionLimit = 10;
        g.ResumeTurn();
        Assert.InRange(g.State.Players[Game.Me].Life - g.Settings.StartingLife, 1, 10);
    }

    [Fact]
    public void PastTheActionLimitOptionalThingsAreDeclined()
    {
        var d = new TestDecider { Choose = (_, _) => 0 };   // always "yes"
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Chant" }, decider: d);
        Assert.True(d.ChooseYesNo(g, Game.Me, null, "?"));
        g.ActionLimit = 0;
        Assert.False(d.ChooseYesNo(g, Game.Me, null, "?"));
    }

    [Fact]
    public void AMandatoryLoopEndsTheGameAsADraw()
    {
        var activated = false;
        var d = new TestDecider
        {
            Choose = (game, dec) =>
            {
                if (dec.Kind != DecisionKind.Action || activated || TestDecider.Named(game, dec, "Chant") is not (var i and >= 0)) return null;
                activated = true;
                return i;
            },
        };
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Chant", "Echo Spring" }, decider: d);
        g.ActionLimit = 20;
        g.ResumeTurn();
        Assert.True(g.State.GameOver);
        Assert.Null(g.State.Winner);
    }

    [Fact]
    public void ALazyRolloutChoicePicksOnlyLegalActionsInProportionToTheirWeights()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Chant" });
        var chant = g.State.BattlefieldCards.Single().Id;
        // Pass weighs 1 and an ability 3; the land (weight 20) fails its check.
        var candidates = new GameAction[] { GameAction.PassAction, new GameAction.Activate(chant, 0), new GameAction.PlayLand(chant) };
        var policy = new RolloutPolicy(7);
        var activations = 0;
        for (var i = 0; i < 4000; i++)
        {
            var a = policy.ChooseActionLazily(g, Game.Me, candidates, x => x is not GameAction.PlayLand);
            Assert.IsNotType<GameAction.PlayLand>(a);
            if (a is GameAction.Activate) activations++;
        }
        Assert.InRange(activations / 4000.0, 0.70, 0.80);
    }
}
