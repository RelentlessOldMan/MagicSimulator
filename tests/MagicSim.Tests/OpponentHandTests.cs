using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using static MagicSim.Tests.TestGames;

namespace MagicSim.Tests;

/// <summary>
/// Goldfish opponents' hands (a count: 7 to start, a draw in their turn, back to 7 at their cleanup) and their
/// do-nothing turns, on hand-written cards in Forge's script format (nothing copied from Forge).
/// </summary>
public class OpponentHandTests
{
    private static readonly string[] Scripts =
    {
        "Name:Filler\nManaCost:9\nTypes:Sorcery\n",
        "Name:Trinket\nManaCost:1\nTypes:Artifact\n",
        "Name:Squeeze\nManaCost:0\nTypes:Sorcery\nA:SP$ Discard | Defined$ Player.Opponent | NumCards$ 2 | Mode$ TgtChoose\n",
        "Name:Gut\nManaCost:0\nTypes:Sorcery\nA:SP$ Discard | Defined$ Player.Opponent | NumCards$ 1 | DiscardValid$ Creature | Mode$ TgtChoose\n",
        "Name:Wrench\nManaCost:0\nTypes:Sorcery\nA:SP$ Discard | Defined$ Player | NumCards$ 2 | UnlessType$ Artifact | Mode$ TgtChoose\n",
        "Name:Tally\nManaCost:0\nTypes:Enchantment\n" +
        "T:Mode$ Discarded | ValidCard$ Card.OppOwn | TriggerZones$ Battlefield | Execute$ TrigCounter\n" +
        "SVar:TrigCounter:DB$ PutCounter | Defined$ Self | CounterType$ QUEST | CounterNum$ 1\n",
        "Name:Collector\nManaCost:0\nTypes:Creature Rogue\nPT:1/1\n" +
        "T:Mode$ Phase | Phase$ End of Turn | TriggerZones$ Battlefield | CheckSVar$ X | Execute$ TrigGain\n" +
        "SVar:TrigGain:DB$ GainLife | Defined$ You | LifeAmount$ 1\n" +
        "SVar:X:PlayerCountOpponents$CardsDiscardedThisTurn\n" +
        "A:AB$ LoseLife | Cost$ 0 | Defined$ Player.Opponent+HasCardsInHand_Card_EQ0 | LifeAmount$ 10\n",
        "Name:Screws\nManaCost:0\nTypes:Artifact\n" +
        "K:ETBReplacement:Other:ChooseP\nSVar:ChooseP:DB$ ChoosePlayer | Defined$ You | Choices$ Player.Opponent\n" +
        "T:Mode$ Phase | Phase$ Upkeep | ValidPlayer$ Player.Chosen | TriggerZones$ Battlefield | Execute$ TrigDamage\n" +
        "SVar:TrigDamage:DB$ DealDamage | Defined$ ChosenPlayer | NumDmg$ X\n" +
        "SVar:X:Count$ValidHand Card.ChosenCtrl/NMinus.8\n",
        "Name:Wail\nManaCost:0\nTypes:Enchantment\n" +
        "T:Mode$ Phase | Phase$ Upkeep | ValidPlayer$ Opponent | TriggerZones$ Battlefield | CheckSVar$ Held | SVarCompare$ LE7 | Execute$ TrigLose\n" +
        "SVar:TrigLose:DB$ LoseLife | Defined$ TriggeredPlayer | LifeAmount$ 3\n" +
        "SVar:Held:Count$ValidHand Card.ActivePlayerCtrl\n",
        "Name:Shade\nManaCost:0\nTypes:Creature Shade\nPT:9/9\n" +
        "K:ETBReplacement:Other:ChooseP\nSVar:ChooseP:DB$ ChoosePlayer | Defined$ You | Choices$ Player.Opponent\n" +
        "S:Mode$ Continuous | Affected$ Card.Self | AddPower$ -X | AddToughness$ -X\n" +
        "SVar:X:Count$ValidHand Card.ChosenCtrl\n",
        "Name:Swap\nManaCost:0\nTypes:Sorcery\nA:SP$ RepeatEach | RepeatPlayers$ Player | RepeatSubAbility$ DBDiscard\n" +
        "SVar:DBDiscard:DB$ Discard | Defined$ Player.IsRemembered | Mode$ Hand | RememberDiscarded$ True | SubAbility$ DBDraw\n" +
        "SVar:DBDraw:DB$ Draw | NumCards$ X | Defined$ Player.IsRemembered | SubAbility$ DBClean\n" +
        "SVar:DBClean:DB$ Cleanup | ClearRemembered$ True\nSVar:X:Remembered$Amount/Minus.1\n",
        "Name:Waste\nManaCost:0\nTypes:Enchantment\n" +
        "A:AB$ Effect | Cost$ 0 | ReplacementEffects$ Instead\n" +
        "SVar:Instead:Event$ Draw | ValidPlayer$ You | ReplaceWith$ TheyDiscard\n" +
        "SVar:TheyDiscard:DB$ Discard | Defined$ Player.Opponent | NumCards$ 1 | Mode$ TgtChoose | SubAbility$ EndIt\n" +
        "SVar:EndIt:DB$ ChangeZone | Defined$ Self | Origin$ Command | Destination$ Exile\n",
    };

    private static readonly CardPool Pool = new(ForgeLibrary.FromScripts(Scripts, new Dictionary<string, string>()), null);

    private static Game Setup(string[] hand, string[]? battlefield = null, string[]? library = null) =>
        TestGames.Setup(Pool, hand, battlefield, library);

    private static Player Opp(Game g, int i = 1) => g.State.Players[i];

    private static void Cast(Game g, string name)
    {
        var c = g.State[InHand(g, name)];
        g.CastSpell(c, c.Types.IsPermanent ? -1 : 0);
        ResolveAll(g);
    }

    [Fact]
    public void OpponentsHoldSevenDrawInTheirTurnAndDiscardDownToSeven()
    {
        var g = Setup(new[] { "Squeeze" });
        Assert.Equal(7, Opp(g).HandCount);
        g.RunOpponentTurns();
        Assert.Equal(7, Opp(g).HandCount);
        Assert.Equal(98, Opp(g).LibraryCount);
        Assert.Equal(Game.Me, g.State.ActivePlayer);

        g.State.ActivePlayer = Game.Me;
        g.State.Step = Step.Main1;
        Cast(g, "Squeeze");
        Assert.Equal(5, Opp(g, 1).HandCount);
        Assert.Equal(5, Opp(g, 2).HandCount);
        g.RunOpponentTurns();
        Assert.Equal(6, Opp(g).HandCount);
    }

    [Fact]
    public void DiscardsFromAnOpponentsHandTriggerAndCount()
    {
        var g = Setup(new[] { "Squeeze" }, battlefield: new[] { "Tally", "Collector" });
        Cast(g, "Squeeze");
        Assert.Equal(4, OnBattlefield(g, "Tally").CounterCount("QUEST"));
        Assert.Equal(2, g.State.ThisTurn.CardsDiscarded[1]);
        g.State.Step = Step.End;
        g.FirePhase("End of Turn");
        ResolveAll(g);
        Assert.Equal(41, g.State.Players[Game.Me].Life);
    }

    [Fact]
    public void AFilteredDiscardFindsNothingInAnUnseenHand()
    {
        var g = Setup(new[] { "Gut" }, battlefield: new[] { "Tally" });
        Cast(g, "Gut");
        Assert.Equal(7, Opp(g).HandCount);
        Assert.Equal(0, OnBattlefield(g, "Tally").CounterCount("QUEST"));
    }

    [Fact]
    public void DiscardTwoUnlessAnArtifact()
    {
        var g = Setup(new[] { "Wrench", "Trinket", "Filler", "Filler" });
        Cast(g, "Wrench");
        // An opponent is assumed to have an artifact to discard; you discard yours rather than two cards.
        Assert.Equal(6, Opp(g).HandCount);
        Assert.Equal(new[] { "Filler", "Filler" }, g.State.Players[Game.Me].Hand.Select(id => g.State[id].Name));
    }

    [Fact]
    public void OnlyOpponentsWithEmptyHandsLoseLife()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Collector" });
        Opp(g, 2).AbstractHand = 0;
        g.ActivateAbility(OnBattlefield(g, "Collector"), 0);
        ResolveAll(g);
        Assert.Equal(40, Opp(g, 1).Life);
        Assert.Equal(30, Opp(g, 2).Life);
    }

    [Fact]
    public void TheChosenPlayersUpkeepAndHand()
    {
        var g = Setup(new[] { "Screws", "Shade" });
        Cast(g, "Screws");
        Cast(g, "Shade");
        Assert.Equal(1, OnBattlefield(g, "Screws").ChosenPlayer);
        Assert.Equal(2, OnBattlefield(g, "Shade").Power);   // 9/9 with 7 cards in the chosen player's hand
        Opp(g, 1).AbstractHand = 5;
        g.RunOpponentTurns();
        Assert.Equal(37, Opp(g, 1).Life);   // 8 minus 5 cards at their upkeep, before their draw
        Assert.Equal(40, Opp(g, 2).Life);
    }

    [Fact]
    public void EachOpponentsUpkeepChecksTheActivePlayersHand()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Wail" });
        Opp(g, 2).AbstractHand = 9;
        g.RunOpponentTurns();
        Assert.Equal(37, Opp(g, 1).Life);
        Assert.Equal(40, Opp(g, 2).Life);
        Assert.Equal(40, g.State.Players[Game.Me].Life);
    }

    [Fact]
    public void EachPlayerDiscardsTheirHandAndDrawsOneFewer()
    {
        var g = Setup(new[] { "Swap", "Filler", "Filler", "Filler" }, library: new[] { "Filler", "Filler", "Filler" });
        Cast(g, "Swap");
        Assert.Equal(2, g.State.Players[Game.Me].Hand.Count);
        Assert.Equal(6, Opp(g, 1).HandCount);
        Assert.Equal(6, Opp(g, 2).HandCount);
    }

    [Fact]
    public void TheNextDrawMakesOpponentsDiscardInstead()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Waste" }, library: new[] { "Filler", "Filler" });
        g.ActivateAbility(OnBattlefield(g, "Waste"), 0);
        ResolveAll(g);
        g.Draw(Game.Me, 1);
        ResolveAll(g);
        Assert.Empty(g.State.Players[Game.Me].Hand);
        Assert.Equal(6, Opp(g).HandCount);
        g.Draw(Game.Me, 1);
        Assert.Single(g.State.Players[Game.Me].Hand);
        Assert.Equal(6, Opp(g).HandCount);
    }
}
