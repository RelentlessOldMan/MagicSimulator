using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using static MagicSim.Tests.TestGames;

namespace MagicSim.Tests;

/// <summary>"Whenever this becomes the target of a spell [or ability]" (and valiant), on hand-written cards in Forge's format.</summary>
public class TargetTriggerTests
{
    private static readonly string[] Scripts =
    {
        "Name:Hoard\nManaCost:5\nTypes:Creature Dragon\nPT:4/4\n" +
        "T:Mode$ BecomesTarget | ValidTarget$ Card.Self | ValidSource$ Spell | TriggerZones$ Battlefield | Execute$ TrigGain\n" +
        "SVar:TrigGain:DB$ GainLife | LifeAmount$ 1\n",
        "Name:Squire\nManaCost:1\nTypes:Creature Human\nPT:1/1\n" +
        "T:Mode$ BecomesTarget | ValidTarget$ Card.Self | ValidSource$ SpellAbility.YouCtrl | TriggerZones$ Battlefield | Valiant$ True | Execute$ TrigGain\n" +
        "SVar:TrigGain:DB$ GainLife | LifeAmount$ 2\n",
        "Name:Boost\nManaCost:0\nTypes:Instant\nA:SP$ Pump | ValidTgts$ Creature | NumAtt$ +1\n",
        "Name:Rod\nTypes:Artifact\nA:AB$ Pump | Cost$ T | ValidTgts$ Creature | NumAtt$ +1\n",
    };

    private static readonly CardPool Pool = new(ForgeLibrary.FromScripts(Scripts, new Dictionary<string, string>()), null);

    private static Game Setup(string[] hand, string[] battlefield) => TestGames.Setup(Pool, hand, battlefield);

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
    public void TriggersWhenTargetedByASpellButNotAnAbility()
    {
        var g = Setup(new[] { "Boost" }, new[] { "Hoard", "Rod" });
        g.ActivateAbility(OnBattlefield(g, "Rod"), 0);
        ResolveAll(g);
        Assert.Equal(40, Life(g));
        g.CastSpell(g.State[InHand(g, "Boost")], 0);
        ResolveAll(g);
        Assert.Equal(41, Life(g));
        Assert.Equal(6, OnBattlefield(g, "Hoard").Power);
    }

    [Fact]
    public void ValiantTriggersOnlyTheFirstTimeEachTurn()
    {
        var g = Setup(new[] { "Boost", "Boost" }, new[] { "Squire", "Rod" });
        g.ActivateAbility(OnBattlefield(g, "Rod"), 0);
        ResolveAll(g);
        Assert.Equal(42, Life(g));
        g.CastSpell(g.State[InHand(g, "Boost")], 0);
        ResolveAll(g);
        Assert.Equal(42, Life(g));
        g.State.Turn++;
        g.CastSpell(g.State[InHand(g, "Boost")], 0);
        ResolveAll(g);
        Assert.Equal(44, Life(g));
    }
}
