using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using MagicSim.Core.Players;
using static MagicSim.Tests.TestGames;

namespace MagicSim.Tests;

/// <summary>
/// "Players can't gain life", main-phase bonuses, life lost by each opponent, doubling damage between two chosen
/// players, red sources, attack limits and "exile it if it would leave", on hand-written cards in Forge's format.
/// </summary>
public class DamageAndLifeTests
{
    private static readonly string[] Scripts =
    {
        "Name:Filler\nManaCost:9\nTypes:Sorcery\n",
        "Name:Imp\nManaCost:B\nTypes:Creature Imp\nPT:1/1\n",
        "Name:Ogre\nManaCost:R\nTypes:Creature Ogre\nPT:3/3\n",
        "Name:Salve\nManaCost:0\nTypes:Instant\nA:SP$ GainLife | LifeAmount$ 3\n",
        "Name:Vortex\nManaCost:3\nTypes:Enchantment\nR:Event$ GainLife | ActiveZones$ Battlefield | Prevent$ True\n",
        "Name:Blast\nManaCost:0\nTypes:Instant\nA:SP$ DealDamage | ValidTgts$ Opponent | NumDmg$ X\nSVar:X:Count$IfCastInOwnMainPhase.3.2\n",
        "Name:Reflection\nManaCost:4\nTypes:Enchantment\n" +
        "T:Mode$ Phase | Phase$ End of Turn | TriggerZones$ Battlefield | Execute$ RepeatOpps\n" +
        "SVar:RepeatOpps:DB$ RepeatEach | RepeatPlayers$ Player.Opponent | RepeatSubAbility$ TrigLoseLife\n" +
        "SVar:TrigLoseLife:DB$ LoseLife | Defined$ Remembered | LifeAmount$ X\nSVar:X:PlayerCountRemembered$LifeLostThisTurn\n",
        "Name:Feud\nManaCost:0\nTypes:Enchantment\nK:ETBReplacement:Other:ChooseP\n" +
        "SVar:ChooseP:DB$ ChoosePlayer | Defined$ You | Choices$ Player | RememberChosen$ True | SubAbility$ ChoosePTwo\n" +
        "SVar:ChoosePTwo:DB$ ChoosePlayer | Defined$ You | Choices$ NonChosenPlayer\n" +
        "R:Event$ DamageDone | ActiveZones$ Battlefield | ValidSource$ Card.RememberedPlayerCtrl,Emblem.RememberedPlayerCtrl | ValidTarget$ Permanent.ChosenCtrl,Player.Chosen | ReplaceWith$ DmgTwice\n" +
        "R:Event$ DamageDone | ActiveZones$ Battlefield | ValidSource$ Card.ChosenCtrl,Emblem.ChosenCtrl | ValidTarget$ Permanent.RememberedPlayerCtrl,Player.IsRemembered | ReplaceWith$ DmgTwice | Secondary$ True\n" +
        "SVar:DmgTwice:DB$ ReplaceEffect | VarName$ DamageAmount | VarValue$ X\nSVar:X:ReplaceCount$DamageAmount/Twice\n",
        "Name:Zap\nManaCost:0\nTypes:Sorcery\nA:SP$ DealDamage | ValidTgts$ Opponent | NumDmg$ 2\n",
        "Name:Torbran\nManaCost:1 R R R\nTypes:Legendary Creature Dwarf\nPT:2/4\n" +
        "R:Event$ DamageDone | ActiveZones$ Battlefield | ValidSource$ Card.RedSource+YouCtrl | ValidTarget$ Player.Opponent,Permanent.OppCtrl | ReplaceWith$ DmgPlus2\n" +
        "SVar:DmgPlus2:DB$ ReplaceEffect | VarName$ DamageAmount | VarValue$ X\nSVar:X:ReplaceCount$DamageAmount/Plus.2\n",
        "Name:Arbiter\nManaCost:4\nTypes:Artifact Creature Construct\nPT:1/5\n" +
        "S:Mode$ AttackRestrict | MaxAttackers$ 1\nS:Mode$ BlockRestrict | MaxBlockers$ 1\n",
        "Name:Whip\nManaCost:2 B B\nTypes:Legendary Enchantment Artifact\n" +
        "A:AB$ ChangeZone | Cost$ T | ValidTgts$ Creature.YouOwn | Origin$ Graveyard | Destination$ Battlefield | RememberChanged$ True | SubAbility$ DBUnearthed\n" +
        "SVar:DBUnearthed:DB$ Animate | Defined$ Remembered | Keywords$ Haste | LeaveBattlefield$ Exile | Duration$ Permanent | AtEOT$ Exile | SubAbility$ DBCleanup\n" +
        "SVar:DBCleanup:DB$ Cleanup | ClearRemembered$ True\n",
        "Name:Neheb\nManaCost:3 R\nTypes:Creature Zombie\nPT:4/6\nK:Afflict:3\n",
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
    public void NoOneGainsLife()
    {
        var g = Setup(new[] { "Salve" }, new[] { "Vortex" });
        Cast(g, "Salve");
        Assert.Equal(40, Life(g));
    }

    [Fact]
    public void MoreDamageInYourMainPhase()
    {
        var g = Setup(new[] { "Blast", "Blast" });
        Cast(g, "Blast");
        Assert.Equal(37, Life(g, 1));
        g.State.Step = Step.End;
        Cast(g, "Blast");
        Assert.Equal(35, Life(g, 1));
    }

    [Fact]
    public void EachOpponentLosesTheLifeTheyLostThisTurn()
    {
        var g = Setup(new[] { "Zap" }, new[] { "Reflection" }, Fillers(5));
        Cast(g, "Zap");
        g.ResumeTurn();
        Assert.Equal(36, Life(g, 1));
    }

    [Fact]
    public void DamageBetweenTheTwoChosenPlayersIsDoubled()
    {
        var g = Setup(new[] { "Feud", "Zap" });
        Cast(g, "Feud");
        var feud = OnBattlefield(g, "Feud");
        Assert.Equal(new[] { Game.Me }, feud.RememberedPlayers);
        Assert.Equal(1, feud.ChosenPlayer);
        Cast(g, "Zap");
        Assert.Equal(36, Life(g, 1));
    }

    [Fact]
    public void RedSourcesDealTwoMore()
    {
        var g = Setup(Array.Empty<string>(), new[] { "Torbran", "Ogre", "Imp" }, Fillers(5));
        g.ResumeTurn();
        Assert.Equal(40 - (2 + 2) - (3 + 2) - 1, Life(g, 1));
    }

    [Fact]
    public void OnlyOneCreatureCanAttack()
    {
        var g = Setup(Array.Empty<string>(), new[] { "Arbiter", "Ogre", "Imp" }, Fillers(5));
        g.ResumeTurn();
        Assert.Equal(37, Life(g, 1));
    }

    [Fact]
    public void ReturnedCreatureIsExiledIfItWouldLeave()
    {
        var g = Setup(Array.Empty<string>(), new[] { "Whip", "Ogre" });
        var ogre = OnBattlefield(g, "Ogre");
        g.MoveTo(ogre, Zone.Graveyard);
        g.ActivateAbility(OnBattlefield(g, "Whip"), 0);
        ResolveAll(g);
        Assert.Equal(Zone.Battlefield, ogre.Zone);
        Assert.True(ogre.HasKeyword("Haste"));
        g.MoveTo(ogre, Zone.Graveyard);
        Assert.Equal(Zone.Exile, ogre.Zone);
    }
}
