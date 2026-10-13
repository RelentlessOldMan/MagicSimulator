using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using MagicSim.Core.Players;
using static MagicSim.Tests.TestGames;

namespace MagicSim.Tests;

/// <summary>
/// Devotion to two colors, "target creature you control deals damage equal to its power", phase-restricted spells,
/// "other targets", and restricted mana from an activated ability with a cost, on hand-written cards in Forge's format.
/// </summary>
public class TimingAndDamageTests
{
    private static readonly string[] Scripts =
    {
        "Name:Peak\nTypes:Land\nA:AB$ Mana | Cost$ T | Produced$ R\n",
        "Name:Bear\nManaCost:2\nTypes:Creature Bear\nPT:2/2\n",
        "Name:Ember\nManaCost:R R\nTypes:Enchantment\n",
        "Name:Sprout\nManaCost:G\nTypes:Enchantment\n",
        "Name:Mixed\nManaCost:R/G\nTypes:Enchantment\n",
        "Name:Drop\nManaCost:U\nTypes:Enchantment\n",
        "Name:Count\nManaCost:0\nTypes:Sorcery\nA:SP$ GainLife | LifeAmount$ X\nSVar:X:Count$DevotionDual.Red.Green\n",
        "Name:Fling\nManaCost:0\nTypes:Instant\nA:SP$ Pump | ValidTgts$ Creature.YouCtrl | SubAbility$ Hurl\n" +
        "SVar:Hurl:DB$ DealDamage | ValidTgts$ Any | NumDmg$ X | DamageSource$ ParentTarget\nSVar:X:ParentTargeted$CardPower\n",
        "Name:Frenzy\nManaCost:0\nTypes:Instant\n" +
        "A:SP$ Pump | ValidTgts$ Creature | NumAtt$ +X | ActivationPhases$ Upkeep->Declare Blockers | ActivationFirstCombat$ True\nSVar:X:Targeted$CardPower\n",
        "Name:Breath\nManaCost:0\nTypes:Sorcery\nA:SP$ DealDamage | ValidTgts$ Any | NumDmg$ 4 | DamageMap$ True | SubAbility$ More\n" +
        "SVar:More:DB$ DealDamage | ValidTgts$ Any | TargetMin$ 0 | TargetMax$ 2 | TargetUnique$ True | NumDmg$ 3 | SubAbility$ Done\n" +
        "SVar:Done:DB$ DamageResolve\n",
        "Name:Keep\nTypes:Land\nA:AB$ Mana | Cost$ T | Produced$ G\n" +
        "A:AB$ Mana | Cost$ 1 T | Produced$ G | Amount$ 6 | RestrictValid$ Spell.Creature,Activated.Creature+inZoneBattlefield\n",
        "Name:Giant\nManaCost:6\nTypes:Creature Giant\nPT:6/6\n",
        "Name:Rite\nManaCost:6\nTypes:Sorcery\nA:SP$ GainLife | LifeAmount$ 1\n",
    };

    private static readonly CardPool Pool = new(ForgeLibrary.FromScripts(Scripts, new Dictionary<string, string>()), null);

    /// <summary>Targets the opponent when it can, else stops choosing (or takes the first option).</summary>
    private static TestDecider AtOpponent() => new()
    {
        Choose = (g, d) => d.Kind != DecisionKind.Target ? null
            : d.Options.ToList().FindIndex(o => o is TargetRef { Player: 1 }) is >= 0 and var i ? i
            : d.Options.ToList().FindIndex(o => o is null) is >= 0 and var done ? done : 0,
    };

    private static Game Setup(string[] hand, string[]? battlefield = null, TestDecider? decider = null) =>
        TestGames.Setup(Pool, hand, battlefield, decider: decider);

    private static int Life(Game g, int player = Game.Me) => g.State.Players[player].Life;

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
    public void DevotionToTwoColorsCountsHybridOnce()
    {
        var g = Setup(new[] { "Count" }, new[] { "Ember", "Sprout", "Mixed", "Drop" });
        Cast(g, "Count");
        Assert.Equal(44, Life(g));
    }

    [Fact]
    public void YourCreatureDealsDamageEqualToItsPower()
    {
        var g = Setup(new[] { "Fling" }, new[] { "Bear" }, AtOpponent());
        Cast(g, "Fling");
        Assert.Equal(38, Life(g, 1));
    }

    [Fact]
    public void CastOnlyBeforeTheFirstCombatDamageStep()
    {
        var g = Setup(new[] { "Frenzy" }, new[] { "Bear" });
        var frenzy = g.State[InHand(g, "Frenzy")];
        Assert.True(g.CanCast(frenzy, 0));
        g.State.Step = Step.BeginCombat;
        g.State.ThisTurn.Combats = 1;
        Assert.True(g.CanCast(frenzy, 0));
        g.State.Step = Step.Main2;
        Assert.False(g.CanCast(frenzy, 0));
        g.State.Step = Step.BeginCombat;
        g.State.ThisTurn.Combats = 2;   // an extra combat
        Assert.False(g.CanCast(frenzy, 0));
        g.State.ThisTurn.Combats = 0;
        g.State.Step = Step.End;
        Assert.False(g.CanCast(frenzy, 0));
    }

    [Fact]
    public void OtherTargetsCantBeOnesChosenEarlier()
    {
        var g = Setup(new[] { "Breath" }, decider: AtOpponent());
        Cast(g, "Breath");
        Assert.Equal(36, Life(g, 1));
        Assert.Equal(40, Life(g));
    }

    [Fact]
    public void RestrictedManaFromAnAbilityWithACost()
    {
        var g = Setup(new[] { "Giant", "Rite" }, new[] { "Keep", "Peak" });
        var keep = OnBattlefield(g, "Keep");
        Assert.Contains(g.LegalActions(Game.Me), a => a is GameAction.Activate act && act.CardId == keep.Id);
        g.ActivateAbility(keep, keep.Activated.ToList().FindIndex(a => a["Amount"] == "6"));
        Assert.False(g.CanCast(g.State[InHand(g, "Rite")], 0));
        Assert.True(g.CanCast(g.State[InHand(g, "Giant")], -1));
        Cast(g, "Giant");
        Assert.Equal(Zone.Battlefield, OnBattlefield(g, "Giant").Zone);
    }
}
