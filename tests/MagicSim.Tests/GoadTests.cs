using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using MagicSim.Core.Players;
using static MagicSim.Tests.TestGames;

namespace MagicSim.Tests;

/// <summary>
/// Goad, each player choosing a creature they control, targets limited to a player's creatures, and "whenever creatures
/// attack one of your opponents", on hand-written cards in Forge's format.
/// </summary>
public class GoadTests
{
    private static readonly string[] Scripts =
    {
        "Name:Filler\nManaCost:9\nTypes:Sorcery\n",
        "Name:Imp\nManaCost:1\nTypes:Creature Imp\nPT:1/1\n",
        "Name:Ogre\nManaCost:2\nTypes:Creature Ogre\nPT:3/3\n",
        "Name:Ancestor\nManaCost:0\nTypes:Creature Spirit\nPT:3/2\n" +
        "T:Mode$ ChangesZone | Origin$ Any | Destination$ Battlefield | ValidCard$ Card.Self | Execute$ TrigGoad\n" +
        "SVar:TrigGoad:DB$ Goad | ValidTgts$ Creature\n" +
        "T:Mode$ Attacks | ValidCard$ Creature.IsGoaded | Execute$ TrigDamage | TriggerZones$ Battlefield\n" +
        "SVar:TrigDamage:DB$ DealDamage | Defined$ TriggeredAttackerController | DamageSource$ TriggeredAttackerLKICopy | NumDmg$ 1\n",
        "Name:Ant\nManaCost:2\nTypes:Creature Insect\nPT:1/1\n" +
        "T:Mode$ Phase | Phase$ End of Turn | ValidPlayer$ You | TriggerZones$ Battlefield | Execute$ DBChoice\n" +
        "SVar:DBChoice:DB$ ChooseCard | Defined$ Player | Amount$ 1 | MinAmount$ 0 | Choices$ Creature | ControlledByPlayer$ Chooser | SubAbility$ DBPutCounter\n" +
        "SVar:DBPutCounter:DB$ PutCounterAll | Placer$ Controller | ValidCards$ Creature.ChosenCard | CounterType$ P1P1 | CounterNum$ 2 | RememberPut$ True | SubAbility$ DBGoad\n" +
        "SVar:DBGoad:DB$ Goad | Defined$ Remembered | SubAbility$ DBCleanup\n" +
        "SVar:DBCleanup:DB$ Cleanup | ClearRemembered$ True | ClearChosenCard$ True\n",
        "Name:Grenzo\nManaCost:2 R\nTypes:Creature Goblin\nPT:2/2\n" +
        "T:Mode$ DamageDone | ValidSource$ Creature.YouCtrl | ValidTarget$ Player | CombatDamage$ True | Execute$ TrigGoad | TriggerZones$ Battlefield\n" +
        "SVar:TrigGoad:DB$ Goad | ValidTgts$ Creature | TargetsWithDefinedController$ TriggeredTarget\n",
        "Name:Warmonger\nManaCost:3 R\nTypes:Creature Human\nPT:3/3\n" +
        "T:Mode$ AttackersDeclaredOneTarget | AttackedTarget$ Opponent,Planeswalker.OppCtrl | Execute$ TrigPumpAll | TriggerZones$ Battlefield\n" +
        "SVar:TrigPumpAll:DB$ Pump | Defined$ TriggeredAttackers | KW$ Menace\n",
        "Name:Decorum\nManaCost:0\nTypes:Sorcery\nA:SP$ Goad | Defined$ Valid Creature.YouDontCtrl\n",
        "Name:Fumiko\nManaCost:2 R\nTypes:Creature Human\nPT:3/2\n" +
        "S:Mode$ Continuous | AffectedDefined$ Self | AddKeyword$ Bushido:X\nSVar:X:Count$Valid Creature.attacking\n" +
        "S:Mode$ MustAttack | ValidCreature$ Creature.OppCtrl\n",
    };

    private static readonly CardPool Pool = new(ForgeLibrary.FromScripts(Scripts, new Dictionary<string, string>()), null);

    private static Game Setup(string[] hand, string[]? battlefield = null, string[]? library = null, TestDecider? decider = null) =>
        TestGames.Setup(Pool, hand, battlefield, library, decider);

    private static string[] Fillers(int n) => Enumerable.Repeat("Filler", n).ToArray();

    private static int Life(Game g, int player = Game.Me) => g.State.Players[player].Life;

    private static void Cast(Game g, string name)
    {
        var card = g.State[InHand(g, name)];
        g.CastSpell(card, card.CurrentFace.SpellAbilities.Count == 0 ? -1 : 0);
        ResolveAll(g);
    }

    /// <summary>Never chooses to attack: only what's forced to attacks.</summary>
    private static TestDecider NoAttacks(string? target = null) => new()
    {
        Choose = (g, d) => d.Kind == DecisionKind.Attackers ? d.Options.ToList().FindIndex(o => o is IReadOnlyList<int> { Count: 0 } or null)
            : d.Kind == DecisionKind.Target && target is not null ? TestDecider.Named(g, d, target) : null,
    };

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
    public void AGoadedCreatureMustAttackAndHurtsItsController()
    {
        var g = Setup(new[] { "Ancestor" }, new[] { "Ogre" }, Fillers(5), NoAttacks("Ogre"));
        Cast(g, "Ancestor");
        var ogre = OnBattlefield(g, "Ogre");
        Assert.Equal(g.State.Turn, ogre.GoadedTurn);
        g.ResumeTurn();
        Assert.Equal(37, Life(g, 1));   // only the goaded Ogre attacked
        Assert.Equal(39, Life(g));      // and it hurt its controller
    }

    [Fact]
    public void EachPlayerPutsCountersOnACreatureTheyControl()
    {
        var g = Setup(Array.Empty<string>(), new[] { "Ant", "Ogre" }, Fillers(5));
        g.ResumeTurn();
        Assert.Equal(2, g.State.BattlefieldCards.Sum(c => c.CounterCount("P1P1")));
    }

    [Fact]
    public void GoadOnlyACreatureThePlayerHitControls()
    {
        var g = Setup(Array.Empty<string>(), new[] { "Grenzo", "Imp", "Ogre" }, Fillers(5));
        var ogre = OnBattlefield(g, "Ogre");
        ogre.Controller = 1;
        ogre.Tapped = true;   // so the goad doesn't make it attack for us
        g.ResumeTurn();
        Assert.Equal(g.State.Turn, ogre.GoadedTurn);
        Assert.Equal(-1, OnBattlefield(g, "Imp").GoadedTurn);
        Assert.Equal(-1, OnBattlefield(g, "Grenzo").GoadedTurn);
    }

    [Fact]
    public void AttackersOfAnOpponentGainMenace()
    {
        var g = Setup(Array.Empty<string>(), new[] { "Warmonger", "Imp" }, Fillers(5));
        g.Log = new();
        g.ResumeTurn();
        Assert.Contains(g.Log, l => l.Contains("Warmonger trigger"));
    }

    [Fact]
    public void GoadEverythingYouDontControl()
    {
        var g = Setup(new[] { "Decorum" }, new[] { "Ogre", "Imp" });
        var imp = OnBattlefield(g, "Imp");
        imp.Controller = 1;
        Cast(g, "Decorum");
        Assert.Equal(g.State.Turn, imp.GoadedTurn);
        Assert.Equal(-1, OnBattlefield(g, "Ogre").GoadedTurn);
    }
}
