using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using MagicSim.Core.Players;
using static MagicSim.Tests.TestGames;

namespace MagicSim.Tests;

/// <summary>
/// Damage to a defined card, "if you cast it", once-each-turn triggers, phasing and "your life total can't change",
/// searching for one card of each kind, revealed cards to the bottom, and mana that triggers when it's spent, on
/// hand-written cards in Forge's script format.
/// </summary>
public class PhasingAndCastTests
{
    private static readonly string[] Scripts =
    {
        "Name:Bog\nTypes:Land\nA:AB$ Mana | Cost$ T | Produced$ B\n",
        "Name:Woods\nTypes:Land Forest\n",
        "Name:Field\nTypes:Land Plains\n",
        "Name:Filler\nManaCost:9\nTypes:Sorcery\n",
        "Name:Junk\nManaCost:9\nTypes:Sorcery\n",
        "Name:Last\nManaCost:9\nTypes:Sorcery\n",
        "Name:Bear\nManaCost:1 G\nTypes:Creature Bear\nPT:2/2\n",
        "Name:Pup\nManaCost:0\nTypes:Creature Dog\nPT:1/1\n",
        "Name:Rex\nManaCost:0\nTypes:Creature Dinosaur\nPT:3/3\n",
        "Name:Raptorling\nManaCost:G\nTypes:Creature Dinosaur\nPT:2/2\n",
        "Name:Puppy\nManaCost:G\nTypes:Creature Dog\nPT:1/1\n",
        "Name:Chief\nManaCost:4 G\nTypes:Legendary Creature Dinosaur\nPT:5/5\n",
        "Name:Biter\nManaCost:0\nTypes:Creature Dinosaur\nPT:2/3\n" +
        "T:Mode$ ChangesZone | Origin$ Any | Destination$ Battlefield | ValidCard$ Creature.Other+YouCtrl | TriggerZones$ Battlefield | Execute$ TrigDmg\n" +
        "SVar:TrigDmg:DB$ DealDamage | Defined$ TriggeredCardLKICopy | NumDmg$ 2 | RememberDamaged$ True | SubAbility$ DBPump\n" +
        "SVar:DBPump:DB$ Pump | Defined$ Self | NumAtt$ +2 | ConditionDefined$ Remembered | ConditionPresent$ Creature.Dinosaur | ConditionCompare$ EQ1 | SubAbility$ DBCleanup\n" +
        "SVar:DBCleanup:DB$ Cleanup | ClearRemembered$ True\n",
        "Name:Sunlord\nManaCost:0\nTypes:Creature Dinosaur\nPT:5/5\n" +
        "T:Mode$ ChangesZone | ValidCard$ Card.wasCastFromYourHandByYou+Self | Destination$ Battlefield | Execute$ TrigWipe\n" +
        "SVar:TrigWipe:DB$ DestroyAll | ValidCards$ Creature.nonDinosaur\n",
        "Name:Seeker\nManaCost:0\nTypes:Creature Dinosaur\nPT:1/1\n" +
        "T:Mode$ ChangesZone | Origin$ Any | Destination$ Battlefield | ValidCard$ Dinosaur.YouCtrl+Other | ResolvedLimit$ 1 | TriggerZones$ Battlefield | Execute$ TrigGain\n" +
        "SVar:TrigGain:DB$ GainLife | Defined$ You | LifeAmount$ 1\n",
        "Name:Charm\nManaCost:0\nTypes:Enchantment Aura\nK:Enchant:Creature\n",
        "Name:Haven\nManaCost:0\nTypes:Instant\n" +
        "A:SP$ Effect | Duration$ UntilYourNextTurn | StaticAbilities$ Lock | SubAbility$ DBPump\n" +
        "SVar:Lock:Mode$ CantChangeLife | ValidPlayer$ You\n" +
        "SVar:DBPump:DB$ Pump | Defined$ You | Duration$ UntilYourNextTurn | KW$ Protection from everything | SubAbility$ DBPhase\n" +
        "SVar:DBPhase:DB$ Phases | Defined$ Valid Permanent.YouCtrl | SubAbility$ DBExile\n" +
        "SVar:DBExile:DB$ ChangeZone | Origin$ Stack | Destination$ Exile\n",
        "Name:Toll\nManaCost:0\nTypes:Sorcery\nA:SP$ Draw | Cost$ PayLife<2> | NumCards$ 1\n",
        "Name:Verge\nTypes:Land\n" +
        "A:AB$ ChangeZone | Cost$ T | Origin$ Library | Destination$ Battlefield | ChangeType$ EACH Forest & Plains | Tapped$ True\n",
        "Name:Summons\nManaCost:0\nTypes:Instant\n" +
        "A:SP$ DigUntil | Amount$ 1 | Valid$ Creature | RevealedDestination$ Library | RevealedLibraryPosition$ -1 | FoundDestination$ Battlefield\n",
        "Name:Trail\nTypes:Land\nA:AB$ Mana | Cost$ T | Produced$ G | TriggersWhenSpent$ TrigLife\n" +
        "SVar:TrigLife:Mode$ SpellCast | ValidCard$ Creature.sharesCreatureTypeWith Commander | Execute$ DBLife\n" +
        "SVar:DBLife:DB$ GainLife | Defined$ You | LifeAmount$ 1\n",
    };

    private static readonly CardPool Pool = new(ForgeLibrary.FromScripts(Scripts, new Dictionary<string, string>()), null);

    private static Game Setup(string[] hand, string[]? battlefield = null, string[]? library = null, string[]? commanders = null) =>
        TestGames.Setup(Pool, hand, battlefield, library, commanders: commanders);

    private static void Cast(Game g, string name)
    {
        var c = g.State[InHand(g, name)];
        g.CastSpell(c, c.Types.IsPermanent ? -1 : 0);
        ResolveAll(g);
    }

    private static Card Named(Game g, string name) => g.State.Cards.First(c => c.Name == name);

    private static int Life(Game g) => g.State.Players[Game.Me].Life;

    [Fact]
    public void EveryTestCardCompiles()
    {
        foreach (var s in Scripts)
        {
            var name = s[5..s.IndexOf('\n')];
            Assert.Equal(name + ": ", name + ": " + string.Join("; ", Pool.Compile(name)!.Missing));
        }
    }

    [Fact]
    public void ItDealsDamageToTheCreatureThatEnteredAndGrowsIfItWasADinosaur()
    {
        var g = Setup(new[] { "Pup", "Rex" }, battlefield: new[] { "Biter" });
        var biter = OnBattlefield(g, "Biter");
        Cast(g, "Pup");
        Assert.Equal(Zone.Graveyard, Named(g, "Pup").Zone);
        Assert.Equal(2, biter.Power);
        Cast(g, "Rex");
        Assert.Equal(2, Named(g, "Rex").Damage);
        Assert.Equal(4, biter.Power);
    }

    [Fact]
    public void OnlyWhenCastFromYourHand()
    {
        var g = Setup(new[] { "Sunlord" }, battlefield: new[] { "Pup", "Rex" });
        Cast(g, "Sunlord");
        Assert.Equal(Zone.Graveyard, Named(g, "Pup").Zone);
        Assert.Equal(Zone.Battlefield, Named(g, "Rex").Zone);

        var h = Setup(Array.Empty<string>(), battlefield: new[] { "Pup" }, library: new[] { "Sunlord" });
        h.MoveTo(Named(h, "Sunlord"), Zone.Battlefield);
        ResolveAll(h);
        Assert.Equal(Zone.Battlefield, Named(h, "Pup").Zone);
        Assert.Null(Named(h, "Sunlord").CastFrom);
    }

    [Fact]
    public void ATriggerThatResolvesOnlyOnceEachTurn()
    {
        var g = Setup(new[] { "Rex", "Rex" }, battlefield: new[] { "Seeker" }, library: new[] { "Filler", "Filler" });
        Cast(g, "Rex");
        Cast(g, "Rex");
        Assert.Equal(41, Life(g));
    }

    [Fact]
    public void PermanentsPhaseOutUntilYourNextTurnAndYourLifeCantChange()
    {
        var g = Setup(new[] { "Haven", "Toll" }, battlefield: new[] { "Bear", "Charm", "Bog" }, library: new[] { "Filler", "Filler", "Filler" });
        var bear = OnBattlefield(g, "Bear");
        var charm = OnBattlefield(g, "Charm");
        charm.AttachedTo = bear.Id;
        Cast(g, "Haven");
        Assert.Equal(Zone.Exile, Named(g, "Haven").Zone);
        Assert.Empty(g.State.BattlefieldCards);
        Assert.Equal(Zone.PhasedOut, charm.Zone);
        g.LoseLife(Game.Me, 5, null);
        g.GainLife(Game.Me, 5);
        Assert.Equal(40, Life(g));
        Assert.False(g.CanCast(g.State[InHand(g, "Toll")], 0));   // can't pay life

        g.RunOpponentTurns();
        g.RunTurn();
        Assert.Equal(new[] { "Bear", "Bog", "Charm" }, g.State.BattlefieldCards.Select(c => c.Name).Order());
        Assert.Equal(bear.Id, charm.AttachedTo);
        g.LoseLife(Game.Me, 5, null);
        Assert.Equal(35, Life(g));
    }

    [Fact]
    public void SearchForOneCardOfEachKind()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Verge" }, library: new[] { "Woods", "Woods", "Field", "Filler" });
        g.ActivateAbility(OnBattlefield(g, "Verge"), 0);
        ResolveAll(g);
        var fetched = g.State.BattlefieldCards.Where(c => c.Name != "Verge").ToList();
        Assert.Equal(new[] { "Field", "Woods" }, fetched.Select(c => c.Name).Order());
        Assert.All(fetched, c => Assert.True(c.Tapped));
    }

    [Fact]
    public void RevealedCardsGoToTheBottom()
    {
        var g = Setup(new[] { "Summons" }, library: new[] { "Junk", "Filler", "Rex", "Last" });
        Cast(g, "Summons");
        Assert.Equal(Zone.Battlefield, Named(g, "Rex").Zone);
        Assert.Equal(new[] { "Last", "Junk", "Filler" }, g.State.Players[Game.Me].Library.Select(id => g.State[id].Name));
    }

    [Theory]
    [InlineData("Raptorling", 41)]
    [InlineData("Puppy", 40)]
    public void ManaThatTriggersWhenSpentOnACreatureSharingATypeWithYourCommander(string creature, int life)
    {
        var g = Setup(new[] { creature }, battlefield: new[] { "Trail" }, commanders: new[] { "Chief" });
        Cast(g, creature);
        Assert.Equal(Zone.Battlefield, Named(g, creature).Zone);
        Assert.Equal(life, Life(g));
    }
}
