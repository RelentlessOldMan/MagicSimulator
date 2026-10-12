using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using MagicSim.Core.Players;
using static MagicSim.Tests.TestGames;

namespace MagicSim.Tests;

/// <summary>
/// Granted flashback, unearth, two piles, raid, "if you played a land this turn", the defending player, hand size
/// reduction, mass reanimation and Auras that hold a creature down, on hand-written cards in Forge's script format.
/// </summary>
public class GraveyardTests
{
    private static readonly string[] Scripts =
    {
        "Name:Bog\nTypes:Land\nA:AB$ Mana | Cost$ T | Produced$ B\n",
        "Name:Isle\nTypes:Land\nA:AB$ Mana | Cost$ T | Produced$ U\n",
        "Name:Filler\nManaCost:9\nTypes:Sorcery\n",
        "Name:Bear\nManaCost:1 G\nTypes:Creature Bear\nPT:2/2\n",
        "Name:Peek\nManaCost:U\nTypes:Sorcery\nA:SP$ Draw | Defined$ You | NumCards$ 1\n",
        "Name:Echo Mage\nManaCost:0\nTypes:Creature Human Wizard\nPT:2/1\n" +
        "T:Mode$ ChangesZone | Origin$ Any | Destination$ Battlefield | ValidCard$ Card.Self | Execute$ TrigFlashback\n" +
        "SVar:TrigFlashback:DB$ Pump | ValidTgts$ Instant.YouCtrl,Sorcery.YouCtrl | TgtZone$ Graveyard | KW$ Flashback | PumpZone$ Graveyard\n",
        "Name:Ghoul\nManaCost:4 B\nTypes:Creature Zombie\nPT:3/3\nK:Unearth:B\n",
        "Name:Mull\nManaCost:0\nTypes:Instant\n" +
        "A:SP$ PeekAndReveal | PeekAmount$ 3 | RememberRevealed$ True | NoPeek$ True | SubAbility$ Split\n" +
        "SVar:Split:DB$ TwoPiles | Defined$ You | DefinedCards$ Remembered | Separator$ Opponent | ChosenPile$ ToHand | UnchosenPile$ ToGrave\n" +
        "SVar:ToHand:DB$ ChangeZone | Defined$ Remembered | Origin$ Library | Destination$ Hand\n" +
        "SVar:ToGrave:DB$ ChangeZone | Defined$ Remembered | Origin$ Library | Destination$ Graveyard\n",
        "Name:Plot\nManaCost:0\nTypes:Sorcery\nA:SP$ Draw | NumCards$ 2 | SubAbility$ Toss\n" +
        "SVar:Toss:DB$ Discard | Mode$ TgtChoose | NumCards$ 1 | ConditionCheckSVar$ Raided | ConditionSVarCompare$ EQ0\nSVar:Raided:Count$AttackersDeclared\n",
        "Name:Delta\nTypes:Land\n" +
        "A:AB$ Mana | Cost$ T | Produced$ U | ConditionCheckSVar$ X | ConditionSVarCompare$ EQ0 | SubAbility$ ManaB\n" +
        "SVar:ManaB:DB$ Mana | Produced$ B | ConditionCheckSVar$ X | ConditionSVarCompare$ GE1\nSVar:X:PlayerCountPropertyYou$LandsPlayed\n",
        "Name:Devourer\nManaCost:0\nTypes:Creature Eldrazi\nPT:1/1\n" +
        "T:Mode$ Attacks | ValidCard$ Card.Self | Execute$ TrigExile\n" +
        "SVar:TrigExile:DB$ Dig | Defined$ TriggeredDefendingPlayer | DestinationZone$ Exile | DigNum$ 20 | ChangeNum$ All\n",
        "Name:Augur\nManaCost:0\nTypes:Creature Praetor\nPT:5/4\nS:Mode$ Continuous | Affected$ Opponent | RaiseMaxHandSize$ -7\n",
        "Name:Uprising\nManaCost:0\nTypes:Sorcery\nA:SP$ ChangeZoneAll | ChangeType$ Creature | Origin$ Graveyard | Destination$ Battlefield | GainControl$ True\n",
        "Name:Shackle\nManaCost:0\nTypes:Enchantment Aura\nK:Enchant:Creature\nA:SP$ Attach | ValidTgts$ Creature\n" +
        "R:Event$ Untap | ValidCard$ Creature.EnchantedBy | ValidStepTurnToController$ You | Layer$ CantHappen | Secondary$ True\n" +
        "T:Mode$ Phase | Phase$ Upkeep | ValidPlayer$ Player.EnchantedController | TriggerZones$ Battlefield | Execute$ TrigLife\n" +
        "SVar:TrigLife:DB$ GainLife | Defined$ You | LifeAmount$ 1\n" +
        "T:Mode$ ChangesZone | Origin$ Any | Destination$ Battlefield | ValidCard$ Card.Self | Execute$ Note\n" +
        "SVar:Note:DB$ Pump | RememberObjects$ Enchanted\n" +
        "A:AB$ SacrificeAll | Cost$ 0 | Defined$ DirectRemembered\n",
    };

    private static readonly CardPool Pool = new(ForgeLibrary.FromScripts(Scripts, new Dictionary<string, string>()), null);

    private static Game Setup(string[] hand, string[]? battlefield = null, string[]? library = null, TestDecider? decider = null) =>
        TestGames.Setup(Pool, hand, battlefield, library, decider);

    private static void Cast(Game g, string name)
    {
        var c = g.State[InHand(g, name)];
        g.CastSpell(c, c.Types.IsPermanent ? -1 : 0);
        ResolveAll(g);
    }

    private static Card Named(Game g, string name) => g.State.Cards.First(c => c.Name == name);

    private static List<string> Hand(Game g) => g.State.Players[Game.Me].Hand.Select(id => g.State[id].Name).ToList();

    [Fact]
    public void EveryTestCardCompiles()
    {
        foreach (var s in Scripts)
        {
            var name = s[5..s.IndexOf('\n')];
            Assert.Equal(name + ": ", name + ": " + string.Join("; ", Pool.Resolve(name)!.Missing));
        }
    }

    [Fact]
    public void AnInstantOrSorceryGainsFlashbackUntilEndOfTurn()
    {
        var g = Setup(new[] { "Echo Mage" }, battlefield: new[] { "Isle" }, library: new[] { "Peek", "Filler", "Filler" });
        g.Mill(Game.Me, 1);
        var peek = Named(g, "Peek");
        Assert.DoesNotContain(g.LegalActions(Game.Me).OfType<GameAction.Cast>(), a => a.CardId == peek.Id);
        Cast(g, "Echo Mage");
        // Its flashback cost is its mana cost: {U}.
        var cast = g.LegalActions(Game.Me).OfType<GameAction.Cast>().Single(a => a.CardId == peek.Id);
        g.Perform(cast);
        ResolveAll(g);
        Assert.Equal(Zone.Exile, peek.Zone);
        Assert.Single(Hand(g));
    }

    [Fact]
    public void GrantedFlashbackEndsAtCleanup()
    {
        var g = Setup(new[] { "Echo Mage" }, battlefield: new[] { "Isle" }, library: new[] { "Peek", "Filler", "Filler" });
        g.Mill(Game.Me, 1);
        Cast(g, "Echo Mage");
        Assert.NotNull(Named(g, "Peek").FlashbackCost);
        g.ResumeTurn();
        Assert.Null(Named(g, "Peek").FlashbackCost);
    }

    [Fact]
    public void UnearthReturnsAHastyCreatureThatIsExiledAtTheEndStep()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Bog" }, library: new[] { "Ghoul", "Filler" });
        g.Mill(Game.Me, 1);
        var ghoul = Named(g, "Ghoul");
        var unearth = g.LegalActions(Game.Me).OfType<GameAction.Activate>().Single(a => a.CardId == ghoul.Id);
        g.Perform(unearth);
        ResolveAll(g);
        Assert.Equal(Zone.Battlefield, ghoul.Zone);
        Assert.True(ghoul.HasKeyword("Haste"));
        g.ResumeTurn();
        Assert.Equal(37, g.State.Players[1].Life);   // it attacked
        Assert.Equal(Zone.Exile, ghoul.Zone);
    }

    [Fact]
    public void AnUnearthedCreatureIsExiledIfItWouldLeave()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Bog" }, library: new[] { "Ghoul", "Filler" });
        g.Mill(Game.Me, 1);
        var ghoul = Named(g, "Ghoul");
        g.Perform(g.LegalActions(Game.Me).OfType<GameAction.Activate>().Single(a => a.CardId == ghoul.Id));
        ResolveAll(g);
        g.Destroy(ghoul, regenerate: false);
        Assert.Equal(Zone.Exile, ghoul.Zone);
        Assert.False(ghoul.ExileIfLeaves);
    }

    [Fact]
    public void UnearthIsOnlyAsASorcery()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Bog" }, library: new[] { "Ghoul", "Filler" });
        g.Mill(Game.Me, 1);
        g.State.Step = Step.Upkeep;
        Assert.DoesNotContain(g.LegalActions(Game.Me).OfType<GameAction.Activate>(), a => a.CardId == Named(g, "Ghoul").Id);
    }

    [Theory]
    [InlineData(0, new[] { "Filler" }, 2)]
    [InlineData(1, new[] { "Bear", "Peek" }, 1)]
    public void AnOpponentSeparatesTheBiggestCardFromTheRest(int pile, string[] taken, int milled)
    {
        var d = new TestDecider { Choose = (_, dec) => dec.Kind == DecisionKind.Option ? pile : null };
        var g = Setup(new[] { "Mull" }, library: new[] { "Bear", "Filler", "Peek", "Bog" }, decider: d);
        Cast(g, "Mull");
        Assert.Equal(taken.Order(), Hand(g).Order());
        Assert.Equal(milled, g.State.Players[Game.Me].Graveyard.Count(id => g.State[id].Name != "Mull"));
        Assert.Equal("Bog", g.State[g.State.Players[Game.Me].Library[0]].Name);
    }

    [Fact]
    public void RaidChecksWhetherYouAttackedThisTurn()
    {
        var g = Setup(new[] { "Plot" }, library: new[] { "Filler", "Filler", "Filler", "Filler" });
        Cast(g, "Plot");
        Assert.Equal(1, Hand(g).Count);

        var h = Setup(new[] { "Plot" }, battlefield: new[] { "Bear" }, library: new[] { "Filler", "Filler", "Filler", "Filler" });
        h.ResumeTurn();
        Assert.Equal(1, h.State.ThisTurn.AttackersDeclared);
        h.State.Step = Step.Main2;
        Cast(h, "Plot");
        Assert.Equal(2, h.State.Players[Game.Me].Hand.Count);
    }

    [Fact]
    public void ALandMakesOtherManaIfYouPlayedALandThisTurn()
    {
        var g = Setup(new[] { "Bog" }, battlefield: new[] { "Delta", "Delta" });
        var deltas = g.State.BattlefieldCards.Where(c => c.Name == "Delta").ToList();
        g.ActivateAbility(deltas[0], 0);
        ResolveAll(g);
        Assert.Equal(1, g.State.Players[Game.Me].Pool[ManaColor.U]);
        g.PlayLand(g.State[InHand(g, "Bog")]);
        g.ActivateAbility(deltas[1], 0);
        ResolveAll(g);
        Assert.Equal(1, g.State.Players[Game.Me].Pool[ManaColor.U]);
        Assert.Equal(1, g.State.Players[Game.Me].Pool[ManaColor.B]);
    }

    [Fact]
    public void TheDefendingPlayerIsTheOneAttacked()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Devourer" });
        g.State.Players[2].Life = 30;
        g.ResumeTurn();
        Assert.Equal(79, g.State.Players[2].LibraryCount);
        Assert.Equal(29, g.State.Players[2].Life);
        Assert.Equal(99, g.State.Players[1].LibraryCount);
        Assert.Equal(40, g.State.Players[1].Life);
    }

    [Fact]
    public void OpponentsMaximumHandSizeIsReducedBySeven()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Augur" });
        Statics.Apply(g);
        Assert.Equal(7, g.State.Players[Game.Me].MaxHandSizeWith(7));
        g.RunOpponentTurns();
        Assert.Equal(0, g.State.Players[1].HandCount);
        Assert.Equal(0, g.State.Players[2].HandCount);
    }

    [Fact]
    public void CreaturesFromGraveyardsComeBackUnderYourControl()
    {
        var g = Setup(new[] { "Uprising" }, library: new[] { "Bear", "Ghoul", "Peek", "Filler" });
        g.Mill(Game.Me, 3);
        Cast(g, "Uprising");
        Assert.Equal(new[] { "Bear", "Ghoul" }, g.State.BattlefieldCards.Where(c => c.Controller == Game.Me).Select(c => c.Name).Order());
        Assert.Equal(Zone.Graveyard, Named(g, "Peek").Zone);
    }

    [Fact]
    public void AnAuraKeepsItsCreatureTappedAndTriggersInItsControllersUpkeep()
    {
        var g = Setup(new[] { "Shackle" }, battlefield: new[] { "Bear", "Bog" }, library: new[] { "Filler", "Filler" });
        Cast(g, "Shackle");
        var bear = OnBattlefield(g, "Bear");
        var bog = OnBattlefield(g, "Bog");
        bear.Tapped = bog.Tapped = true;
        g.RunTurn();
        Assert.True(bear.Tapped);
        Assert.False(bog.Tapped);
        Assert.Equal(41, g.State.Players[Game.Me].Life);
        g.RunOpponentTurns();
        Assert.Equal(41, g.State.Players[Game.Me].Life);
    }

    [Fact]
    public void DirectRememberedIsWhatThisCardRemembers()
    {
        var g = Setup(new[] { "Shackle" }, battlefield: new[] { "Bear" });
        Cast(g, "Shackle");
        g.ActivateAbility(OnBattlefield(g, "Shackle"), 0);
        ResolveAll(g);
        Assert.Equal(Zone.Graveyard, Named(g, "Bear").Zone);
    }
}
