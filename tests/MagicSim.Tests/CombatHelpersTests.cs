using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using MagicSim.Core.Players;
using static MagicSim.Tests.TestGames;

namespace MagicSim.Tests;

/// <summary>
/// Digging a creature onto the battlefield attacking, tokens attacking "that player", equipped creatures, removing a
/// creature from combat, fuse, and the most cards sharing a name, on hand-written cards in Forge's format.
/// </summary>
public class CombatHelpersTests
{
    private static readonly string[] Scripts =
    {
        "Name:Peak\nTypes:Land Mountain\nA:AB$ Mana | Cost$ T | Produced$ R\n",
        "Name:Waste\nTypes:Land\nA:AB$ Mana | Cost$ T | Produced$ C\n",
        "Name:Prism\nTypes:Land\nA:AB$ Mana | Cost$ T | Produced$ Any\n",
        "Name:Filler\nManaCost:9\nTypes:Sorcery\n",
        "Name:Imp\nManaCost:1\nTypes:Creature Imp\nPT:1/1\n",
        "Name:Knight\nManaCost:2\nTypes:Creature Human Knight\nPT:2/2\n",
        "Name:Winota\nManaCost:2 R W\nTypes:Legendary Creature Human Warrior\nPT:4/4\n" +
        "T:Mode$ Attacks | ValidCard$ Creature.nonHuman+YouCtrl | TriggerZones$ Battlefield | Execute$ TrigDig\n" +
        "SVar:TrigDig:DB$ Dig | DigNum$ 6 | ChangeNum$ 1 | Optional$ True | ChangeValid$ Creature.Human | DestinationZone$ Battlefield | DestinationZone2$ Library | LibraryPosition$ -1 | RestRandomOrder$ True | Tapped$ True | Attacking$ True\n",
        "Name:Adeline\nManaCost:1 W W\nTypes:Legendary Creature Human Knight\nPT:0/4\n" +
        "T:Mode$ AttackersDeclared | AttackingPlayer$ You | Execute$ DBRepeat | TriggerZones$ Battlefield\n" +
        "SVar:DBRepeat:DB$ RepeatEach | RepeatPlayers$ Opponent | RepeatSubAbility$ DBToken\n" +
        "SVar:DBToken:DB$ Token | TokenScript$ w_1_1_human | TokenTapped$ True | TokenAttacking$ RememberedPlayer & Valid Planeswalker.ControlledBy Remembered | TokenOwner$ You\n",
        "Name:Sword\nManaCost:1\nTypes:Artifact Equipment\nK:Equip:1\n",
        "Name:Reyav\nManaCost:2 R\nTypes:Creature Dwarf\nPT:2/2\n" +
        "T:Mode$ Attacks | ValidCard$ Creature.YouCtrl+equipped | Execute$ TrigPump | TriggerZones$ Battlefield\n" +
        "SVar:TrigPump:DB$ Pump | Defined$ TriggeredAttackerLKICopy | KW$ Double Strike\n",
        "Name:Recon\nManaCost:W\nTypes:Enchantment\n" +
        "A:AB$ RemoveFromCombat | Cost$ 0 | ValidTgts$ Creature.attacking+YouCtrl | SubAbility$ DBUntap\nSVar:DBUntap:DB$ Untap | Defined$ Targeted\n",
        "Name:Wear\nManaCost:1 R\nTypes:Instant\nA:SP$ GainLife | LifeAmount$ 2\nK:Fuse\nAlternateMode:Split\n\nALTERNATE\n\n" +
        "Name:Tear\nManaCost:W\nTypes:Instant\nA:SP$ GainLife | LifeAmount$ 3\nK:Fuse\n",
        "Name:Atlas\nManaCost:2\nTypes:Artifact\nA:AB$ Draw | Cost$ T | CheckSVar$ X | SVarCompare$ GE3\nSVar:X:Count$MostCardName Land.YouCtrl\n",
        "Name:Servant\nManaCost:1\nTypes:Artifact Creature Golem\nPT:1/1\n" +
        "A:AB$ Mana | Cost$ T Sac<1/CARDNAME> | Produced$ C | Amount$ 2 | AddsKeywords$ Haste | AddsKeywordsValid$ Spell.Creature | AddsKeywordsUntil$ UntilEOT\n",
    };

    private static readonly Dictionary<string, string> Tokens = new()
    {
        ["w_1_1_human"] = "Name:Human Token\nManaCost:no cost\nColors:white\nTypes:Creature Human\nPT:1/1\n",
    };

    private static readonly CardPool Pool = new(ForgeLibrary.FromScripts(Scripts, Tokens), null);

    private static Game Setup(string[] hand, string[]? battlefield = null, string[]? library = null, TestDecider? decider = null) =>
        TestGames.Setup(Pool, hand, battlefield, library, decider);

    private static string[] Fillers(int n) => Enumerable.Repeat("Filler", n).ToArray();

    private static int Life(Game g, int player = Game.Me) => g.State.Players[player].Life;

    [Fact]
    public void EveryTestCardCompiles()
    {
        foreach (var s in Scripts)
        {
            var name = s[5..s.IndexOf('\n')];
            var full = name == "Wear" ? "Wear // Tear" : name;
            Assert.Equal(full + ": ", full + ": " + string.Join("; ", Pool.Compile(full)!.Missing));
        }
    }

    [Fact]
    public void PutAHumanOntoTheBattlefieldAttacking()
    {
        var g = Setup(Array.Empty<string>(), new[] { "Winota", "Imp" }, new[] { "Filler", "Knight", "Filler", "Filler", "Filler", "Filler", "Filler" });
        g.ResumeTurn();
        Assert.Equal(40 - 4 - 1 - 2, Life(g, 1));
        Assert.Equal(Zone.Battlefield, OnBattlefield(g, "Knight").Zone);
    }

    [Fact]
    public void ATokenAttackingForEachOpponent()
    {
        var g = Setup(Array.Empty<string>(), new[] { "Adeline", "Imp" }, Fillers(5));
        var opponents = g.State.Players.Count(p => p.Index != Game.Me);
        g.ResumeTurn();
        Assert.Equal(opponents, g.State.BattlefieldCards.Count(c => c.Name == "Human Token"));
        Assert.Equal(40 - 1 - opponents, Life(g, 1));
    }

    [Fact]
    public void AnEquippedCreatureGainsDoubleStrike()
    {
        var g = Setup(Array.Empty<string>(), new[] { "Reyav", "Imp", "Sword", "Peak" }, Fillers(5),
            new TestDecider { Choose = (game, d) => d.Kind == DecisionKind.Target ? TestDecider.Named(game, d, "Imp") : null });
        var sword = OnBattlefield(g, "Sword");
        g.ActivateAbility(sword, sword.Activated.ToList().FindIndex(a => a.Api == "Attach"));
        ResolveAll(g);
        g.ResumeTurn();
        Assert.Equal(40 - 2 - 1 - 1, Life(g, 1));
    }

    [Fact]
    public void RemoveFromCombatAndUntap()
    {
        var g = Setup(Array.Empty<string>(), new[] { "Recon", "Imp" });
        var imp = OnBattlefield(g, "Imp");
        g.State.Step = Step.DeclareAttackers;
        g.State.Attackers.Add(imp.Id);
        imp.Tapped = true;
        g.ActivateAbility(OnBattlefield(g, "Recon"), 0);
        ResolveAll(g);
        Assert.Empty(g.State.Attackers);
        Assert.False(imp.Tapped);
    }

    [Fact]
    public void CastBothHalvesFused()
    {
        var g = Setup(new[] { "Wear // Tear" }, new[] { "Prism", "Prism", "Prism" });
        var card = g.State[InHand(g, "Wear // Tear")];
        var fused = card.Def.Faces[0].SpellAbilities.ToList().FindIndex(a => a.AltCondition == "Fuse");
        Assert.True(fused >= 0);
        Assert.Contains(g.LegalActions(Game.Me), a => a is GameAction.Cast cast && cast.CardId == card.Id && cast.SpellIndex == fused);
        g.CastSpell(card, fused);
        ResolveAll(g);
        Assert.Equal(45, Life(g));
        Assert.All(g.State.BattlefieldCards.Where(c => c.Name == "Prism"), c => Assert.True(c.Tapped));
    }

    [Fact]
    public void ActivateOnlyWithThreeLandsOfTheSameName()
    {
        var g = Setup(Array.Empty<string>(), new[] { "Atlas", "Peak", "Peak", "Waste" }, Fillers(3));
        var atlas = OnBattlefield(g, "Atlas");
        Assert.DoesNotContain(g.LegalActions(Game.Me), a => a is GameAction.Activate act && act.CardId == atlas.Id);
        var h = Setup(Array.Empty<string>(), new[] { "Atlas", "Peak", "Peak", "Peak" }, Fillers(3));
        Assert.Contains(h.LegalActions(Game.Me), a => a is GameAction.Activate act && act.CardId == OnBattlefield(h, "Atlas").Id);
    }
}
