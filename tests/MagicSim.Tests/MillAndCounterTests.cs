using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using MagicSim.Core.Players;
using static MagicSim.Tests.TestGames;

namespace MagicSim.Tests;

/// <summary>
/// A color for every card everywhere and milling until two cards don't share a color, mana from a countered spell's mana
/// value, delayed draws for the countered spell's controller, improvise and shuffling a graveyard back, on hand-written
/// cards in Forge's format.
/// </summary>
public class MillAndCounterTests
{
    private static readonly string[] Scripts =
    {
        "Name:Bog\nTypes:Land\nA:AB$ Mana | Cost$ T | Produced$ B\n",
        "Name:Filler\nManaCost:9\nTypes:Sorcery\n",
        "Name:Widget\nManaCost:0\nTypes:Artifact\n",
        "Name:Big\nManaCost:3\nTypes:Sorcery\nA:SP$ Draw | NumCards$ 1\n",
        "Name:Dud\nManaCost:0\nTypes:Sorcery\nA:SP$ Draw | NumCards$ 1\n",
        "Name:Servant\nManaCost:2\nTypes:Artifact Creature Scarecrow\nPT:1/3\n" +
        "K:ETBReplacement:Other:ChooseColor\nSVar:ChooseColor:DB$ ChooseColor | Defined$ You\n" +
        "S:Mode$ Continuous | Affected$ Card | AddColor$ ChosenColor | AffectedZone$ All\n",
        "Name:Millstone\nManaCost:1\nTypes:Artifact\n" +
        "A:AB$ Repeat | Cost$ 3 T | ValidTgts$ Player | RepeatSubAbility$ DBCleanAndGrind | MaxRepeat$ MaxRepeats | RepeatCheckSVar$ MilledSharesColor | RepeatSVarCompare$ GE2 | SubAbility$ DBCleanup\n" +
        "SVar:DBCleanAndGrind:DB$ Cleanup | ClearRemembered$ True | SubAbility$ DBGrind\n" +
        "SVar:DBGrind:DB$ Mill | NumCards$ 2 | Defined$ Targeted | RememberMilled$ True\n" +
        "SVar:MaxRepeats:TargetedPlayer$CardsInLibrary\n" +
        "SVar:MilledSharesColor:Remembered$Valid Card.SharesColorWithOther Remembered\n" +
        "SVar:DBCleanup:DB$ Cleanup | ClearRemembered$ True\n",
        "Name:Drain\nManaCost:0\nTypes:Instant\n" +
        "A:SP$ Counter | TargetType$ Spell | RememberCounteredCMC$ True | ValidTgts$ Card | SubAbility$ DBDelTrig\n" +
        "SVar:DBDelTrig:DB$ DelayedTrigger | Mode$ Phase | Phase$ Main1,Main2 | ValidPlayer$ You | Execute$ AddMana | RememberNumber$ True | SubAbility$ DBCleanup\n" +
        "SVar:DBCleanup:DB$ Cleanup | ClearRemembered$ True\n" +
        "SVar:AddMana:DB$ Mana | Produced$ C | Amount$ X\nSVar:X:Count$TriggerRememberAmount\n",
        "Name:Denial\nManaCost:0\nTypes:Instant\n" +
        "A:SP$ Counter | TargetType$ Spell | RememberTargets$ True | ValidTgts$ Card | SubAbility$ DelTrigSlowtrip\n" +
        "SVar:DelTrigSlowtrip:DB$ DelayedTrigger | NextTurn$ True | Mode$ Phase | Phase$ Upkeep | ValidPlayer$ Player | Execute$ DrawSlowtrip | SubAbility$ DelTrigDrawTwo\n" +
        "SVar:DrawSlowtrip:DB$ Draw | Defined$ You\n" +
        "SVar:DelTrigDrawTwo:DB$ DelayedTrigger | NextTurn$ True | Mode$ Phase | Phase$ Upkeep | ValidPlayer$ Player | RememberObjects$ RememberedController | Execute$ DrawTwo | SubAbility$ DBCleanup\n" +
        "SVar:DrawTwo:DB$ Draw | NumCards$ 2 | Defined$ DelayTriggerRemembered | Upto$ True\n" +
        "SVar:DBCleanup:DB$ Cleanup | ClearRemembered$ True\n",
        "Name:Confluence\nManaCost:0\nTypes:Instant\n" +
        "A:SP$ Charm | Choices$ DBCounter,DBDraw | CharmNum$ 3 | CanRepeatModes$ True\n" +
        "SVar:DBCounter:DB$ Counter | TargetType$ Spell | ValidTgts$ Card | UnlessCost$ 3 | UnlessPayer$ ThisTargetedController\n" +
        "SVar:DBDraw:DB$ Draw | Defined$ You | NumCards$ 1\n",
        "Name:Invention\nManaCost:3\nTypes:Instant\nK:Improvise\nA:SP$ GainLife | LifeAmount$ 5\n",
        "Name:Elixir\nManaCost:1\nTypes:Artifact\n" +
        "A:AB$ GainLife | Cost$ 2 T | LifeAmount$ 5 | SubAbility$ DBShuffleElixir\n" +
        "SVar:DBShuffleElixir:DB$ ChangeZone | Origin$ Battlefield | Destination$ Library | Shuffle$ False | RememberChanged$ True | Defined$ Self | SubAbility$ DBShuffleGrave\n" +
        "SVar:DBShuffleGrave:DB$ ChangeZoneAll | Origin$ Graveyard | Destination$ Library | Shuffle$ False | RememberChanged$ True | ChangeType$ Card.YouCtrl | SubAbility$ DBShuffleChanged\n" +
        "SVar:DBShuffleChanged:DB$ Shuffle | Defined$ RememberedOwner | SubAbility$ DBCleanup\n" +
        "SVar:DBCleanup:DB$ Cleanup | ClearRemembered$ True\n",
    };

    private static readonly CardPool Pool = new(ForgeLibrary.FromScripts(Scripts, new Dictionary<string, string>()), null);

    private static Game Setup(string[] hand, string[]? battlefield = null, string[]? library = null, TestDecider? decider = null) =>
        TestGames.Setup(Pool, hand, battlefield, library, decider);

    private static Card Named(Game g, string name) => g.State.Cards.First(c => c.Name == name);

    private static int Life(Game g) => g.State.Players[Game.Me].Life;

    /// <summary>Targets the first opponent when a player is targeted.</summary>
    private static TestDecider TargetOpponent() => new()
    {
        Choose = (g, d) => d.Options.ToList().FindIndex(o => o is TargetRef { Player: 1 }) is var i && i >= 0 ? i : null,
    };

    [Fact]
    public void EveryTestCardCompiles()
    {
        foreach (var s in Scripts)
        {
            var name = s[5..s.IndexOf('\n')];
            Assert.Equal(name + ": ", name + ": " + string.Join("; ", Pool.Resolve(name)!.Missing));
        }
    }

    [Theory]
    [InlineData(false, 97)]   // two unknown cards: they don't share a color as far as anyone knows
    [InlineData(true, 0)]     // every card is blue: the whole library
    public void MillUntilTwoCardsDontShareAColor(bool painter, int left)
    {
        var battlefield = new List<string> { "Millstone", "Bog", "Bog", "Bog" };
        if (painter) battlefield.Add("Servant");
        var g = Setup(Array.Empty<string>(), battlefield: battlefield.ToArray(), decider: TargetOpponent());
        if (painter) OnBattlefield(g, "Servant").ChosenColor = ManaColor.U;
        g.ActivateAbility(OnBattlefield(g, "Millstone"), 0);
        ResolveAll(g);
        Assert.Equal(left, g.State.Players[1].LibraryCount);
    }

    [Fact]
    public void EveryCardEverywhereIsTheChosenColor()
    {
        var g = Setup(new[] { "Filler" }, battlefield: new[] { "Servant" });
        var servant = OnBattlefield(g, "Servant");
        servant.ChosenColor = ManaColor.U;
        Statics.Apply(g);
        Assert.Equal(ManaColor.U.Mask(), Named(g, "Filler").Colors);
        Assert.Equal(ManaColor.U.Mask(), servant.Colors);
        g.MoveTo(servant, Zone.Graveyard);
        Statics.Apply(g);
        Assert.Equal((ColorMask)0, Named(g, "Filler").Colors);
    }

    [Fact]
    public void ManaEqualToTheCounteredSpellsManaValueAtYourNextMainPhase()
    {
        var g = Setup(new[] { "Big", "Drain" }, battlefield: new[] { "Bog", "Bog", "Bog" }, library: new[] { "Filler" });
        g.CastSpell(g.State[InHand(g, "Big")], 0);
        g.CastSpell(g.State[InHand(g, "Drain")], 0);
        ResolveAll(g);
        Assert.Equal(Zone.Graveyard, Named(g, "Big").Zone);
        Assert.Equal(0, g.State.Players[Game.Me].Pool[ManaColor.C]);
        g.State.Step = Step.Main2;
        g.FirePhase("Main2");
        ResolveAll(g);
        Assert.Equal(3, g.State.Players[Game.Me].Pool[ManaColor.C]);
    }

    [Fact]
    public void TheCounteredSpellsControllerDrawsUpToTwoNextUpkeep()
    {
        var g = Setup(new[] { "Dud", "Denial" }, library: new[] { "Filler", "Filler", "Filler", "Filler", "Filler" });
        g.CastSpell(g.State[InHand(g, "Dud")], 0);
        g.CastSpell(g.State[InHand(g, "Denial")], 0);
        ResolveAll(g);
        Assert.Empty(g.State.Players[Game.Me].Hand);
        g.FirePhase("Upkeep");   // still this turn: nothing yet
        ResolveAll(g);
        Assert.Empty(g.State.Players[Game.Me].Hand);
        g.State.Turn++;
        g.FirePhase("Upkeep");
        ResolveAll(g);
        Assert.Equal(3, g.State.Players[Game.Me].Hand.Count);
    }

    [Fact]
    public void ImproviseTapsArtifactsForGenericMana()
    {
        var g = Setup(new[] { "Invention" }, battlefield: new[] { "Widget", "Widget", "Bog" });
        var spell = g.State[InHand(g, "Invention")];
        Assert.True(g.CanCast(spell, 0));
        g.CastSpell(spell, 0);
        ResolveAll(g);
        Assert.Equal(45, Life(g));
        Assert.All(g.State.BattlefieldCards, c => Assert.True(c.Tapped));

        var h = Setup(new[] { "Invention" }, battlefield: new[] { "Widget", "Bog" });
        Assert.False(h.CanCast(h.State[InHand(h, "Invention")], 0));
    }

    [Fact]
    public void ShuffleItAndYourGraveyardIntoYourLibrary()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Elixir", "Bog", "Bog" }, library: new[] { "Filler", "Dud" });
        g.MoveTo(Named(g, "Dud"), Zone.Graveyard);
        g.ActivateAbility(OnBattlefield(g, "Elixir"), 0);
        ResolveAll(g);
        Assert.Equal(45, Life(g));
        Assert.Empty(g.State.Players[Game.Me].Graveyard);
        Assert.Equal(new[] { "Dud", "Elixir", "Filler" }, g.State.Players[Game.Me].Library.Select(id => g.State[id].Name).Order());
    }
}
