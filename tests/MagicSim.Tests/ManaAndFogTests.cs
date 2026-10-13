using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using MagicSim.Core.Players;
using static MagicSim.Tests.TestGames;

namespace MagicSim.Tests;

/// <summary>
/// "Produces three times as much mana", fog, tribute, "greatest power", "doesn't share a name", basic land types and
/// optional fights, on hand-written cards in Forge's format.
/// </summary>
public class ManaAndFogTests
{
    private static readonly string[] Scripts =
    {
        "Name:Peak\nTypes:Land Mountain\nA:AB$ Mana | Cost$ T | Produced$ R\n",
        "Name:Waste\nTypes:Land\nA:AB$ Mana | Cost$ T | Produced$ C\n",
        "Name:Filler\nManaCost:9\nTypes:Sorcery\n",
        "Name:Bear\nManaCost:0\nTypes:Creature Bear\nPT:2/2\n",
        "Name:Giant\nManaCost:6\nTypes:Creature Giant\nPT:6/6\n",
        "Name:Brute\nManaCost:0\nTypes:Creature Ogre\nPT:5/5\n",
        "Name:Bloom\nManaCost:7\nTypes:Creature Elemental\nPT:7/7\n" +
        "R:Event$ ProduceMana | ActiveZones$ Battlefield | ValidActivator$ You | ValidCard$ Permanent | ReplaceWith$ Thrice\n" +
        "SVar:Thrice:DB$ ReplaceMana | ReplaceAmount$ 3\n",
        "Name:Grove\nTypes:Land\nA:AB$ Mana | Cost$ 1 T | Produced$ G | Amount$ 2\n",
        "Name:Mist\nManaCost:0\nTypes:Instant\nA:SP$ Fog\n",
        "Name:Ravager\nManaCost:0\nTypes:Creature Hydra\nPT:2/2\nK:Tribute:2\nSVar:TrigNotTribute:DB$ GainLife | LifeAmount$ 3\n",
        "Name:Heart\nManaCost:0\nTypes:Creature Elf\nPT:2/2\n" +
        "T:Mode$ ChangesZone | Origin$ Any | Destination$ Battlefield | ValidCard$ Creature.Other | TriggerZones$ Battlefield | Execute$ TrigDraw\n" +
        "SVar:TrigDraw:DB$ Draw | OptionalDecider$ TriggeredCardController | Defined$ TriggeredCardController | NumCards$ 1 | ConditionCheckSVar$ Z | ConditionSVarCompare$ EQY\n" +
        "SVar:Z:TriggeredCard$Valid Card.greatestPower\nSVar:Y:Count$Valid Card.greatestPower\n",
        "Name:Project\nManaCost:4\nTypes:Enchantment\n" +
        "T:Mode$ ChangesZone | ValidCard$ Creature.!token+YouCtrl+doesNotShareNameWith YourGraveyard+doesNotShareNameWith OtherYourBattlefield | Origin$ Any | Destination$ Battlefield | Execute$ TrigDraw | TriggerZones$ Battlefield\n" +
        "SVar:TrigDraw:DB$ Draw | Defined$ You | NumCards$ 1\n",
        "Name:Seek\nManaCost:0\nTypes:Sorcery\nA:SP$ ChangeZone | Origin$ Library | Destination$ Hand | ChangeType$ Land.hasABasicLandType | ChangeNum$ 1\n",
        "Name:Brawler\nManaCost:0\nTypes:Creature Ogre\nPT:5/5\n" +
        "T:Mode$ ChangesZone | Origin$ Any | Destination$ Battlefield | ValidCard$ Card.Self | Execute$ TrigFight\n" +
        "SVar:TrigFight:DB$ Fight | Optional$ True | Defined$ Self | ValidTgts$ Creature.Other\n",
        "Name:Twins\nManaCost:4 R R\nTypes:Creature Giant\nPT:6/6\n",
    };

    private static readonly CardPool Pool = new(ForgeLibrary.FromScripts(Scripts, new Dictionary<string, string>()), null);

    private static Game Setup(string[] hand, string[]? battlefield = null, string[]? library = null, TestDecider? decider = null) =>
        TestGames.Setup(Pool, hand, battlefield, library, decider);

    private static string[] Fillers(int n) => Enumerable.Repeat("Filler", n).ToArray();

    private static int Hand(Game g) => g.State.Players[Game.Me].Hand.Count;

    private static void Cast(Game g, string name)
    {
        var card = g.State[InHand(g, name)];
        g.CastSpell(card, card.CurrentFace.SpellAbilities.Count == 0 ? -1 : 0);
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
    public void PermanentsTappedForManaMakeThreeTimesAsMuch()
    {
        var g = Setup(new[] { "Giant" }, new[] { "Bloom", "Peak", "Peak" });
        Assert.True(g.CanCast(g.State[InHand(g, "Giant")], -1));
        Cast(g, "Giant");
        Assert.Equal(Zone.Battlefield, OnBattlefield(g, "Giant").Zone);

        var h = Setup(Array.Empty<string>(), new[] { "Bloom", "Peak", "Grove" });
        var grove = OnBattlefield(h, "Grove");
        h.ActivateAbility(grove, grove.Activated.ToList().FindIndex(a => a["Amount"] == "2"));
        Assert.Equal(6, h.State.Players[Game.Me].Pool[ManaColor.G]);
        Assert.Equal(2, h.State.Players[Game.Me].Pool[ManaColor.R]);   // the Peak made 3 and paid 1
    }

    [Fact]
    public void FogPreventsCombatDamage()
    {
        var g = Setup(new[] { "Mist" }, new[] { "Bear" }, Fillers(5));
        Cast(g, "Mist");
        g.ResumeTurn();
        Assert.Equal(40, g.State.Players[1].Life);
    }

    [Fact]
    public void TributeIsNeverPaid()
    {
        var g = Setup(new[] { "Ravager" });
        Cast(g, "Ravager");
        Assert.Equal(43, g.State.Players[Game.Me].Life);
        Assert.Equal(0, OnBattlefield(g, "Ravager").CounterCount("P1P1"));
    }

    [Fact]
    public void DrawOnlyForTheCreatureWithTheGreatestPower()
    {
        var g = Setup(new[] { "Brute", "Bear" }, new[] { "Heart" }, Fillers(5));
        Cast(g, "Brute");
        Assert.Equal(2, Hand(g));   // Bear and the card drawn
        Cast(g, "Bear");
        Assert.Equal(1, Hand(g));
    }

    [Fact]
    public void DrawForACreatureWithANewName()
    {
        var g = Setup(new[] { "Bear", "Bear", "Brute" }, new[] { "Project" }, Fillers(5));
        Cast(g, "Bear");
        Assert.Equal(3, Hand(g));
        Cast(g, "Bear");
        Assert.Equal(2, Hand(g));   // another Bear is on the battlefield
        g.MoveTo(g.State.BattlefieldCards.First(c => c.Name == "Bear"), Zone.Graveyard);
        Cast(g, "Brute");
        Assert.Equal(2, Hand(g));
    }

    [Fact]
    public void SearchForALandWithABasicLandType()
    {
        var g = Setup(new[] { "Seek" }, library: new[] { "Waste", "Peak" });
        Cast(g, "Seek");
        Assert.Contains(g.State.Players[Game.Me].Hand, id => g.State[id].Name == "Peak");
    }

    [Fact]
    public void YouMayHaveItFight()
    {
        var g = Setup(new[] { "Brawler" }, new[] { "Bear" });
        Cast(g, "Brawler");
        Assert.Equal(Zone.Graveyard, g.State.Cards.First(c => c.Name == "Bear").Zone);

        var h = Setup(new[] { "Brawler" }, new[] { "Bear" }, decider: new TestDecider { Choose = (game, d) => d.Kind == DecisionKind.YesNo ? 1 : null });
        Cast(h, "Brawler");
        Assert.Equal(Zone.Battlefield, h.State.Cards.First(c => c.Name == "Bear").Zone);
    }

    [Fact]
    public void PlentyOfManaButNotTheColorsNeeded()
    {
        var wastes = Enumerable.Repeat("Waste", 24);
        var g = Setup(new[] { "Twins" }, wastes.Append("Peak").ToArray());
        Assert.False(g.CanCast(g.State[InHand(g, "Twins")], -1));
        var h = Setup(new[] { "Twins" }, new[] { "Peak" }.Concat(wastes).Append("Peak").ToArray());
        Assert.True(h.CanCast(h.State[InHand(h, "Twins")], -1));
    }
}
