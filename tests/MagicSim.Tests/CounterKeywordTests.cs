using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using MagicSim.Core.Players;
using static MagicSim.Tests.TestGames;

namespace MagicSim.Tests;

/// <summary>
/// Counter keywords (evolve, graft, outlast, adapt, monstrosity), counters moving between permanents, "whenever a counter
/// is put on this", more counters / proliferating twice, and costs that remove counters, on hand-written cards in Forge's format.
/// </summary>
public class CounterKeywordTests
{
    private static readonly string[] Scripts =
    {
        "Name:Peak\nTypes:Land Mountain\nA:AB$ Mana | Cost$ T | Produced$ R\n",
        "Name:Filler\nManaCost:9\nTypes:Sorcery\n",
        "Name:Imp\nManaCost:0\nTypes:Creature Imp\nPT:1/1\n",
        "Name:Ogre\nManaCost:0\nTypes:Creature Ogre\nPT:3/3\n",
        "Name:Pir\nManaCost:0\nTypes:Creature Human\nPT:1/1\n" +
        "R:Event$ AddCounter | ActiveZones$ Battlefield | ValidCard$ Permanent.YourTeamCtrl+inZoneBattlefield | ReplaceWith$ AddOne\n" +
        "SVar:AddOne:DB$ ReplaceCounter | ChooseCounter$ True | Amount$ X\nSVar:X:ReplaceCount$CounterNum/Plus.1\n",
        "Name:Biomancer\nManaCost:0\nTypes:Creature Elf\nPT:2/4\nK:ETBReplacement:Other:AddExtraCounter:Mandatory:Battlefield:Creature.YouCtrl+Other\n" +
        "SVar:AddExtraCounter:DB$ PutCounter | ETB$ True | Defined$ ReplacedCard | CounterType$ P1P1 | CounterNum$ X | SubAbility$ DBAnimate\n" +
        "SVar:DBAnimate:DB$ Animate | Types$ Mutant | Duration$ Permanent | Defined$ ReplacedCard\nSVar:X:Count$CardPower\n",
        "Name:Ozolith\nManaCost:1\nTypes:Legendary Artifact\n" +
        "T:Mode$ ChangesZone | Origin$ Battlefield | Destination$ Any | ValidCard$ Creature.YouCtrl+HasCounters | TriggerZones$ Battlefield | Execute$ TrigPutCounter\n" +
        "SVar:TrigPutCounter:DB$ PutCounter | Defined$ Self | CounterType$ EachFromSource | EachFromSource$ TriggeredCardLKICopy\n" +
        "A:AB$ MoveCounter | Cost$ 0 | Source$ Self | ValidTgts$ Creature | CounterType$ All | CounterNum$ All\n",
        "Name:Mage\nManaCost:0\nTypes:Creature Human\nPT:1/1\nK:Evolve\n" +
        "T:Mode$ CounterAdded | ValidCard$ Card.Self | TriggerZones$ Battlefield | CounterType$ P1P1 | Execute$ FathomDraw\n" +
        "SVar:FathomDraw:DB$ Draw | Defined$ You | NumCards$ 1\n",
        "Name:Zegana\nManaCost:0\nTypes:Creature Merfolk\nPT:4/4\nA:AB$ PutCounter | Cost$ 0 | Adapt$ 4\n",
        "Name:Broodmaster\nManaCost:0\nTypes:Creature Hydra\nPT:7/7\nA:AB$ PutCounter | Cost$ X | Monstrosity$ X\n" +
        "T:Mode$ BecomeMonstrous | ValidCard$ Card.Self | TriggerZones$ Battlefield | Execute$ TrigToken\n" +
        "SVar:TrigToken:DB$ Token | TokenAmount$ MonstrosityX | TokenScript$ g_x_x_hydra | TokenOwner$ You | TokenPower$ MonstrosityX | TokenToughness$ MonstrosityX\n" +
        "SVar:X:Count$xPaid\nSVar:MonstrosityX:TriggerCount$Amount\n",
        "Name:Ancient\nManaCost:0\nTypes:Creature Elemental\nPT:0/3\n" +
        "A:AB$ MoveCounter | Cost$ 0 | Source$ Self | ValidDefined$ Creature.Other | CounterType$ P1P1 | CounterNum$ Any\n",
        "Name:Tekuthal\nManaCost:0\nTypes:Creature Phyrexian\nPT:3/5\n" +
        "R:Event$ Proliferate | ActiveZones$ Battlefield | ValidPlayer$ You | ReplaceWith$ ProlifTwice\n" +
        "SVar:ProlifTwice:DB$ ReplaceEffect | VarName$ Num | VarValue$ ReplaceCount$Num/Twice\n" +
        "A:AB$ PutCounter | Cost$ RemoveAnyCounter<3/Any/Creature.Other/other creatures> | Defined$ Self | CounterType$ P1P1 | CounterNum$ 1\n",
        "Name:Spread\nManaCost:0\nTypes:Sorcery\nA:SP$ Proliferate\n",
        "Name:Rootkin\nManaCost:0\nTypes:Creature Elemental\nPT:0/0\nK:Graft:4\n",
        "Name:Sage\nManaCost:0\nTypes:Creature Human\nPT:1/1\n" +
        "A:AB$ AddTurn | Cost$ SubCounter<All/P1P1> | Defined$ You | NumTurns$ X\nSVar:X:SVar$CostCountersRemoved/DivideEvenlyDown.5\n",
        "Name:Captain\nManaCost:0\nTypes:Creature Rhino\nPT:2/3\nK:Outlast:R\n",
    };

    private static readonly Dictionary<string, string> Tokens = new()
    {
        ["g_x_x_hydra"] = "Name:Hydra Token\nManaCost:no cost\nColors:green\nTypes:Creature Hydra\nPT:*/*\n",
    };

    private static readonly CardPool Pool = new(ForgeLibrary.FromScripts(Scripts, Tokens), null);

    private static Game Setup(string[] hand, string[]? battlefield = null, string[]? library = null, TestDecider? decider = null) =>
        TestGames.Setup(Pool, hand, battlefield, library, decider);

    private static string[] Fillers(int n) => Enumerable.Repeat("Filler", n).ToArray();

    private static void Cast(Game g, string name)
    {
        var card = g.State[InHand(g, name)];
        g.CastSpell(card, card.CurrentFace.SpellAbilities.Count == 0 ? -1 : 0);
        ResolveAll(g);
    }

    private static void Activate(Game g, Card c, int index = 0)
    {
        g.ActivateAbility(c, index);
        ResolveAll(g);
    }

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
    public void OneMoreOfEachCounterOnYourPermanents()
    {
        var g = Setup(Array.Empty<string>(), new[] { "Pir", "Ogre" });
        g.PutCounters(OnBattlefield(g, "Ogre"), "P1P1", 2);
        Assert.Equal(3, OnBattlefield(g, "Ogre").CounterCount("P1P1"));
    }

    [Fact]
    public void OtherCreaturesEnterWithCountersEqualToItsPower()
    {
        var g = Setup(new[] { "Ogre" }, new[] { "Biomancer" });
        Cast(g, "Ogre");
        var ogre = OnBattlefield(g, "Ogre");
        Assert.Equal(2, ogre.CounterCount("P1P1"));
        Assert.True(ogre.Types.HasSubtype("Mutant"));
    }

    [Fact]
    public void CountersOfACreatureThatLeftGoOnTheOzolithAndMoveOn()
    {
        var g = Setup(Array.Empty<string>(), new[] { "Ozolith", "Imp", "Ogre" },
            decider: new TestDecider { Choose = (game, d) => d.Kind == DecisionKind.Target ? TestDecider.Named(game, d, "Ogre") : null });
        var imp = OnBattlefield(g, "Imp");
        g.PutCounters(imp, "P1P1", 2);
        g.PutCounters(imp, "FLYING", 1);
        g.MoveTo(imp, Zone.Graveyard);
        ResolveAll(g);
        var ozolith = OnBattlefield(g, "Ozolith");
        Assert.Equal(2, ozolith.CounterCount("P1P1"));
        Assert.Equal(1, ozolith.CounterCount("FLYING"));
        Activate(g, ozolith);
        Assert.Equal(2, OnBattlefield(g, "Ogre").CounterCount("P1P1"));
        Assert.Equal(0, ozolith.CounterCount("P1P1"));
    }

    [Fact]
    public void EvolveAndDrawForEachCounter()
    {
        var g = Setup(new[] { "Ogre", "Imp" }, new[] { "Mage" }, Fillers(5));
        Cast(g, "Ogre");
        Assert.Equal(1, OnBattlefield(g, "Mage").CounterCount("P1P1"));
        Assert.Equal(2, g.State.Players[Game.Me].Hand.Count);   // Imp and the card drawn
        Cast(g, "Imp");   // 1/1 isn't bigger than a 2/2
        Assert.Equal(1, OnBattlefield(g, "Mage").CounterCount("P1P1"));
    }

    [Fact]
    public void AdaptOnlyWithoutCounters()
    {
        var g = Setup(Array.Empty<string>(), new[] { "Zegana" });
        var zegana = OnBattlefield(g, "Zegana");
        Activate(g, zegana);
        Activate(g, zegana);
        Assert.Equal(4, zegana.CounterCount("P1P1"));
    }

    [Fact]
    public void MonstrosityOnceAndItsTrigger()
    {
        var g = Setup(Array.Empty<string>(), new[] { "Broodmaster", "Peak", "Peak" });
        var hydra = OnBattlefield(g, "Broodmaster");
        Activate(g, hydra);
        Assert.True(hydra.Monstrous);
        Assert.Equal(2, hydra.CounterCount("P1P1"));
        var tokens = g.State.BattlefieldCards.Where(c => c.Name == "Hydra Token").ToList();
        Assert.Equal(2, tokens.Count);
        Assert.All(tokens, t => Assert.Equal(2, t.Power));
    }

    [Fact]
    public void MoveAnyNumberOfCountersOntoAnotherCreature()
    {
        var g = Setup(Array.Empty<string>(), new[] { "Ancient", "Ogre" });
        var ancient = OnBattlefield(g, "Ancient");
        g.PutCounters(ancient, "P1P1", 3);
        Activate(g, ancient);
        Assert.Equal(3, OnBattlefield(g, "Ogre").CounterCount("P1P1"));
    }

    [Fact]
    public void ProliferateTwiceAndPayWithOtherCreaturesCounters()
    {
        var g = Setup(new[] { "Spread" }, new[] { "Tekuthal", "Ogre" });
        var ogre = OnBattlefield(g, "Ogre");
        g.PutCounters(ogre, "P1P1", 1);
        Cast(g, "Spread");
        Assert.Equal(3, ogre.CounterCount("P1P1"));
        var tekuthal = OnBattlefield(g, "Tekuthal");
        Activate(g, tekuthal);
        Assert.Equal(0, ogre.CounterCount("P1P1"));
        Assert.Equal(1, tekuthal.CounterCount("P1P1"));
    }

    [Fact]
    public void GraftMovesACounterToAnEnteringCreature()
    {
        var g = Setup(new[] { "Imp" }, new[] { "Rootkin" });
        var rootkin = OnBattlefield(g, "Rootkin");
        g.PutCounters(rootkin, "P1P1", 4);
        Cast(g, "Imp");
        Assert.Equal(3, rootkin.CounterCount("P1P1"));
        Assert.Equal(1, OnBattlefield(g, "Imp").CounterCount("P1P1"));
    }

    [Fact]
    public void AnExtraTurnForEachFiveCountersRemoved()
    {
        var g = Setup(Array.Empty<string>(), new[] { "Sage" });
        var sage = OnBattlefield(g, "Sage");
        g.PutCounters(sage, "P1P1", 11);
        Activate(g, sage);
        Assert.Equal(0, sage.CounterCount("P1P1"));
        Assert.Equal(2, g.State.ExtraTurns);
    }

    [Fact]
    public void OutlastAtSorcerySpeed()
    {
        var g = Setup(Array.Empty<string>(), new[] { "Captain", "Peak" });
        var captain = OnBattlefield(g, "Captain");
        var outlast = captain.Activated.ToList().FindIndex(a => a.Api == "PutCounter");
        Activate(g, captain, outlast);
        Assert.Equal(1, captain.CounterCount("P1P1"));
        Assert.True(captain.Tapped);
    }
}
