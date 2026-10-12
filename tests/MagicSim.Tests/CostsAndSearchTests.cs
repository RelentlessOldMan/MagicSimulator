using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using MagicSim.Core.Players;
using static MagicSim.Tests.TestGames;

namespace MagicSim.Tests;

/// <summary>
/// Searching for a total mana value, a spell exiling itself, borrowed activated abilities, drawing as a cost,
/// cumulative upkeep, counting spells cast this turn and "AttachedBy Self", on hand-written cards in Forge's format.
/// </summary>
public class CostsAndSearchTests
{
    private static readonly string[] Scripts =
    {
        "Name:Bog\nTypes:Land\nA:AB$ Mana | Cost$ T | Produced$ B\n",
        "Name:Isle\nTypes:Land\nA:AB$ Mana | Cost$ T | Produced$ U\n",
        "Name:Filler\nManaCost:9\nTypes:Sorcery\n",
        "Name:Imp\nManaCost:B\nTypes:Creature Imp\nPT:1/1\n",
        "Name:Bear\nManaCost:1 G\nTypes:Creature Bear\nPT:2/2\n",
        "Name:Troll\nManaCost:3 G\nTypes:Creature Troll\nPT:4/4\n",
        "Name:Ogre\nManaCost:4 G\nTypes:Creature Ogre\nPT:5/5\n",
        "Name:Peek\nManaCost:U\nTypes:Sorcery\nA:SP$ Draw | Defined$ You | NumCards$ 1\n",
        "Name:Behemoth\nManaCost:0\nTypes:Creature Beast\nPT:6/6\n" +
        "T:Mode$ ChangesZone | ValidCard$ Card.Self | Origin$ Battlefield | Destination$ Graveyard | Execute$ TrigFetch\n" +
        "SVar:TrigFetch:DB$ ChangeZone | Origin$ Library | Destination$ Battlefield | ChangeNum$ X | WithTotalCMC$ 6 | ChangeType$ Creature.YouCtrl\n" +
        "SVar:X:Count$ValidLibrary Creature.YouCtrl\n",
        "Name:Evolve\nManaCost:0\nTypes:Sorcery\n" +
        "A:SP$ ChangeZone | Cost$ Sac<1/Creature> | Origin$ Library | Destination$ Battlefield | ChangeType$ Creature.cmcLEX | ChangeNum$ 1 | SubAbility$ DBExile\n" +
        "SVar:DBExile:DB$ ChangeZone | Origin$ Stack | Destination$ Exile\nSVar:X:Sacrificed$CardManaCost/Plus.2\n",
        "Name:Grower\nManaCost:G\nTypes:Creature Elf\nPT:1/1\nA:AB$ PutCounter | Cost$ T | Defined$ Self | CounterType$ P1P1 | CounterNum$ 1\n",
        "Name:Ghoul\nManaCost:4 B\nTypes:Creature Zombie\nPT:3/3\nK:Unearth:B\n",
        "Name:Mimic Ooze\nManaCost:0\nTypes:Creature Ooze\nPT:4/3\n" +
        "S:Mode$ Continuous | AffectedDefined$ Self | GainsAbilitiesOf$ Creature | GainsAbilitiesOfZones$ Graveyard\n",
        "Name:Grove\nManaCost:0\nTypes:Enchantment\n" +
        "T:Mode$ Phase | Phase$ Draw | ValidPlayer$ You | TriggerZones$ Battlefield | Execute$ TrigDraw\n" +
        "SVar:TrigDraw:AB$ ChooseCard | ChoiceZone$ Hand | Choices$ Card.YouOwn+DrawnThisTurn | Cost$ Draw<2/You> | Amount$ 2 | Mandatory$ True | SubAbility$ DBEach\n" +
        "SVar:DBEach:DB$ RepeatEach | RepeatCards$ Card.ChosenCard | Zone$ Hand | RepeatSubAbility$ DBBack | SubAbility$ DBClean | ChooseOrder$ True\n" +
        "SVar:DBBack:DB$ ChangeZone | Origin$ Hand | Destination$ Library | ChangeType$ Card.ChosenCard | UnlessCost$ PayLife<4> | Mandatory$ True | UnlessPayer$ TriggeredPlayer\n" +
        "SVar:DBClean:DB$ Cleanup | ClearChosenCard$ True\n",
        "Name:Leech\nManaCost:0\nTypes:Enchantment\nK:Cumulative upkeep:1\n",
        "Name:Shroud\nManaCost:0\nTypes:Instant\n" +
        "A:SP$ Draw | Defined$ You | ConditionCheckSVar$ X | SubAbility$ DBPump\n" +
        "SVar:DBPump:DB$ Pump | Defined$ You & Valid Permanent.YouCtrl | KW$ Hexproof\nSVar:X:Count$ThisTurnCast_Card.YouCtrl+Blue\n",
        "Name:Rebirth\nManaCost:0\nTypes:Enchantment Aura\nK:Enchant:Creature\n" +
        "T:Mode$ ChangesZone | Origin$ Battlefield | Destination$ Graveyard | ValidCard$ Card.AttachedBy | Execute$ TrigSearch\n" +
        "SVar:TrigSearch:DB$ ChangeZone | Optional$ True | DefinedPlayer$ TriggeredCardController | ChangeType$ Creature | ChangeNum$ 1 | Hidden$ True | Origin$ Library | Destination$ Battlefield\n" +
        "S:Mode$ Continuous | AffectedDefined$ AttachedBy Self | Affected$ Creature | AddSVar$ Sac\nSVar:Sac:SVar:SacMe:5\n",
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

    private static List<string> Mine(Game g) =>
        g.State.BattlefieldCards.Where(c => c.Controller == Game.Me).Select(c => c.Name).Order().ToList();

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
    public void SearchesForCreaturesWithATotalManaValue()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Behemoth" }, library: new[] { "Ogre", "Bear", "Bear", "Imp", "Filler" });
        g.Sacrifice(OnBattlefield(g, "Behemoth"));
        ResolveAll(g);
        // Ogre (5) leaves room only for Imp (1).
        Assert.Equal(new[] { "Imp", "Ogre" }, Mine(g));
    }

    [Fact]
    public void ASpellExilesItselfAfterSearchingForABiggerCreature()
    {
        var g = Setup(new[] { "Evolve" }, battlefield: new[] { "Bear" }, library: new[] { "Ogre", "Troll", "Filler" });
        Cast(g, "Evolve");
        Assert.Equal(new[] { "Troll" }, Mine(g));   // 2 + 2: Ogre (5) is too big
        Assert.Equal(Zone.Exile, Named(g, "Evolve").Zone);
    }

    [Fact]
    public void ItHasTheActivatedAbilitiesOfCreatureCardsInGraveyards()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Mimic Ooze" }, library: new[] { "Grower", "Ghoul", "Filler" });
        var ooze = OnBattlefield(g, "Mimic Ooze");
        Statics.Apply(g);
        Assert.Empty(ooze.Activated);
        g.Mill(Game.Me, 2);
        Statics.Apply(g);
        // Grower's tap ability, but not Ghoul's unearth (it works only from the graveyard).
        Assert.Single(ooze.Activated);
        g.ActivateAbility(ooze, 0);
        ResolveAll(g);
        Assert.Equal(1, ooze.CounterCount("P1P1"));
        Assert.True(ooze.Tapped);
    }

    [Theory]
    [InlineData(false, 1, 40)]
    [InlineData(true, 3, 32)]
    public void DrawTwoMoreThenPayLifeOrPutBack(bool payLife, int handAfter, int lifeAfter)
    {
        var asked = 0;
        // The first question is whether to draw two more; the others whether to pay 4 life for a card.
        var d = new TestDecider { Choose = (_, dec) => dec.Kind == DecisionKind.YesNo ? (asked++ == 0 || payLife ? 0 : 1) : null };
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Grove" }, library: new[] { "Imp", "Bear", "Troll", "Filler" }, decider: d);
        g.State.Step = Step.Draw;
        g.Draw(Game.Me, 1);
        g.FirePhase("Draw");
        ResolveAll(g);
        var me = g.State.Players[Game.Me];
        Assert.Equal(handAfter, me.Hand.Count);
        Assert.Equal(lifeAfter, me.Life);
        Assert.Equal(4 - handAfter, me.Library.Count);   // three drawn, the ones not paid for put back
    }

    [Fact]
    public void CumulativeUpkeepGrowsUntilItCantBePaid()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Leech", "Bog", "Bog" }, library: new[] { "Filler", "Filler", "Filler", "Filler" });
        var leech = OnBattlefield(g, "Leech");
        g.RunTurn();
        Assert.Equal(1, leech.CounterCount("AGE"));
        Assert.Equal(Zone.Battlefield, leech.Zone);
        g.RunTurn();
        Assert.Equal(2, leech.CounterCount("AGE"));
        Assert.Equal(Zone.Battlefield, leech.Zone);
        g.RunTurn();   // {3} with two lands
        Assert.Equal(Zone.Graveyard, leech.Zone);
    }

    [Fact]
    public void CountsSpellsCastThisTurnThatFit()
    {
        var g = Setup(new[] { "Shroud" }, battlefield: new[] { "Bear" }, library: new[] { "Filler", "Filler" });
        Cast(g, "Shroud");
        Assert.Empty(g.State.Players[Game.Me].Hand);
        Assert.True(OnBattlefield(g, "Bear").HasKeyword("Hexproof"));

        var h = Setup(new[] { "Peek", "Shroud" }, battlefield: new[] { "Isle" }, library: new[] { "Filler", "Filler" });
        Cast(h, "Peek");
        Cast(h, "Shroud");
        Assert.Equal(2, h.State.Players[Game.Me].Hand.Count);
    }

    [Fact]
    public void WhenTheEnchantedCreatureDiesSearchForACreature()
    {
        var g = Setup(new[] { "Rebirth" }, battlefield: new[] { "Bear" }, library: new[] { "Ogre", "Filler" });
        Cast(g, "Rebirth");
        g.Sacrifice(OnBattlefield(g, "Bear"));
        ResolveAll(g);
        Assert.Equal(new[] { "Ogre" }, Mine(g));
    }
}
