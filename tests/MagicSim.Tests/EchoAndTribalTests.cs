using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using MagicSim.Core.Players;
using static MagicSim.Tests.TestGames;

namespace MagicSim.Tests;

/// <summary>
/// Echo, battle cry, surge, creature-type sharing, and copying a spell for each other creature it could target,
/// on hand-written cards in Forge's format.
/// </summary>
public class EchoAndTribalTests
{
    private static readonly string[] Scripts =
    {
        "Name:Peak\nTypes:Land Mountain\nA:AB$ Mana | Cost$ T | Produced$ R\n",
        "Name:Filler\nManaCost:9\nTypes:Sorcery\n",
        "Name:Imp\nManaCost:0\nTypes:Creature Imp\nPT:1/1\n",
        "Name:Gob\nManaCost:0\nTypes:Creature Goblin\nPT:1/1\n",
        "Name:Marshal\nManaCost:0\nTypes:Creature Goblin\nPT:1/1\nK:Echo:1\n",
        "Name:Pest\nManaCost:0\nTypes:Artifact Creature Pest\nPT:0/1\nK:Battle cry\n",
        "Name:Ping\nManaCost:0\nTypes:Sorcery\nA:SP$ GainLife | LifeAmount$ 1\n",
        "Name:Bush\nManaCost:9\nTypes:Creature Goblin\nPT:2/1\nK:Surge:R\n" +
        "T:Mode$ ChangesZone | Origin$ Any | Destination$ Battlefield | ValidCard$ Card.Self+surged | Execute$ TrigGain\n" +
        "SVar:TrigGain:DB$ GainLife | LifeAmount$ 2\n",
        "Name:Tactics\nManaCost:0\nTypes:Instant\nA:SP$ Pump | ValidTgts$ Creature | KW$ HIDDEN CARDNAME can't block. | IsCurse$ True\n",
        "Name:Animosity\nManaCost:3\nTypes:Enchantment\n" +
        "T:Mode$ Attacks | ValidCard$ Creature.YouCtrl | TriggerZones$ Battlefield | Execute$ AnimosityPump\n" +
        "SVar:AnimosityPump:DB$ Pump | Defined$ TriggeredAttackerLKICopy | NumAtt$ +X\n" +
        "SVar:X:Count$Valid Creature.attacking+!TriggeredAttacker+sharesCreatureTypeWith TriggeredAttacker\n",
        "Name:Coat\nManaCost:5\nTypes:Artifact\n" +
        "S:Mode$ Continuous | Affected$ Creature | AddPower$ AffectedX | AddToughness$ AffectedX\nSVar:AffectedX:Count$Valid Creature.sharesCreatureTypeWith+Other\n",
        "Name:Zada\nManaCost:4\nTypes:Creature Goblin\nPT:3/3\n" +
        "T:Mode$ SpellCast | ValidCard$ Instant,Sorcery | ValidActivatingPlayer$ You | IsSingleTarget$ True | TargetsValid$ Card.Self | Execute$ TrigCopy | TriggerZones$ Battlefield\n" +
        "SVar:TrigCopy:DB$ CopySpellAbility | Defined$ TriggeredSpellAbility | Controller$ You | CopyForEachCanTarget$ Creature.YouCtrl\n",
        "Name:Boost\nManaCost:0\nTypes:Instant\nA:SP$ Pump | ValidTgts$ Creature | NumAtt$ +1\n",
    };

    private static readonly CardPool Pool = new(ForgeLibrary.FromScripts(Scripts, new Dictionary<string, string>()), null);

    private static Game Setup(string[] hand, string[]? battlefield = null, string[]? library = null, TestDecider? decider = null) =>
        TestGames.Setup(Pool, hand, battlefield, library, decider);

    private static string[] Fillers(int n) => Enumerable.Repeat("Filler", n).ToArray();

    private static void Cast(Game g, string name, int index = 0)
    {
        var card = g.State[InHand(g, name)];
        g.CastSpell(card, card.CurrentFace.SpellAbilities.Count == 0 ? -1 : index);
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
    public void PayEchoOnceOrSacrificeIt()
    {
        var g = Setup(new[] { "Marshal" }, new[] { "Peak" }, Fillers(10));
        Cast(g, "Marshal");
        g.ResumeTurn();
        g.RunTurn();   // paid in the next upkeep
        var marshal = OnBattlefield(g, "Marshal");
        g.RunTurn();   // and not again
        Assert.Equal(Zone.Battlefield, marshal.Zone);

        var h = Setup(new[] { "Marshal" }, new[] { "Peak" }, Fillers(10), new TestDecider { Choose = (game, d) => d.Kind == DecisionKind.YesNo ? 1 : null });
        Cast(h, "Marshal");
        h.ResumeTurn();
        h.RunTurn();
        Assert.Equal(Zone.Graveyard, h.State.Cards.First(c => c.Name == "Marshal").Zone);
    }

    [Fact]
    public void BattleCryPumpsTheOtherAttackers()
    {
        var g = Setup(Array.Empty<string>(), new[] { "Pest", "Imp" }, Fillers(5));
        g.ResumeTurn();
        Assert.Equal(38, g.State.Players[1].Life);
    }

    [Fact]
    public void SurgeAfterAnotherSpell()
    {
        var g = Setup(new[] { "Ping", "Bush" }, new[] { "Peak" });
        var bush = g.State[InHand(g, "Bush")];
        var surge = bush.CurrentFace.SpellAbilities.ToList().FindIndex(a => a.AltCondition == "Surge");
        Assert.False(g.CanCast(bush, surge));
        Cast(g, "Ping");
        Assert.True(g.CanCast(bush, surge));
        Cast(g, "Bush", surge);
        Assert.Equal(43, g.State.Players[Game.Me].Life);
    }

    [Fact]
    public void AttackersGetMoreForEachOtherAttackerSharingAType()
    {
        var g = Setup(Array.Empty<string>(), new[] { "Animosity", "Gob", "Gob", "Imp" }, Fillers(5));
        g.ResumeTurn();
        Assert.Equal(40 - 2 - 2 - 1, g.State.Players[1].Life);
    }

    [Fact]
    public void BiggerForEachOtherCreatureSharingAType()
    {
        var g = Setup(Array.Empty<string>(), new[] { "Coat", "Gob", "Gob", "Imp" });
        Statics.Apply(g);
        Assert.All(g.State.BattlefieldCards.Where(c => c.Name == "Gob"), c => Assert.Equal(2, c.Power));
        Assert.Equal(1, OnBattlefield(g, "Imp").Power);
    }

    [Fact]
    public void ASpellTargetingOnlyItIsCopiedForEachOtherCreature()
    {
        var g = Setup(new[] { "Boost" }, new[] { "Zada", "Imp", "Gob" },
            decider: new TestDecider { Choose = (game, d) => d.Kind == DecisionKind.Target ? TestDecider.Named(game, d, "Zada") : null });
        Cast(g, "Boost");
        Assert.Equal(4, OnBattlefield(g, "Zada").Power);
        Assert.Equal(2, OnBattlefield(g, "Imp").Power);
        Assert.Equal(2, OnBattlefield(g, "Gob").Power);
    }
}
