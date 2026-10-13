using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using MagicSim.Core.Players;
using static MagicSim.Tests.TestGames;

namespace MagicSim.Tests;

/// <summary>
/// "That player" in triggers and reflexive triggers, the source of a damage trigger, domain and an upkeep cost worked out
/// when it resolves, prowl, spectacle and the damage dealt to opponents this turn, on hand-written cards in Forge's format.
/// </summary>
public class CombatDamageTests
{
    private static readonly string[] Scripts =
    {
        "Name:Bog\nTypes:Land\nA:AB$ Mana | Cost$ T | Produced$ B\n",
        "Name:Woods\nTypes:Land Forest\n",
        "Name:Field\nTypes:Land Plains\n",
        "Name:Filler\nManaCost:9\nTypes:Sorcery\n",
        "Name:Bear\nManaCost:1 G\nTypes:Creature Bear\nPT:2/2\n",
        "Name:Sneak\nManaCost:B\nTypes:Creature Human Rogue\nPT:1/1\n",
        "Name:Spite\nManaCost:0\nTypes:Enchantment\n" +
        "T:Mode$ LifeGained | ValidPlayer$ You | TriggerZones$ Battlefield | Execute$ TrigKill\n" +
        "SVar:TrigKill:DB$ Destroy | ValidTgts$ Creature.ControlledBy TriggeredTarget\n",
        "Name:Echo\nManaCost:0\nTypes:Enchantment\n" +
        "T:Mode$ LifeGained | ValidPlayer$ You | TriggerZones$ Battlefield | Execute$ TrigNow\n" +
        "SVar:TrigNow:DB$ ImmediateTrigger | Execute$ TrigLose\n" +
        "SVar:TrigLose:DB$ LoseLife | Defined$ Spawner>TriggeredTarget | LifeAmount$ 3\n",
        "Name:Hacker\nManaCost:U\nTypes:Creature Human Ninja\nPT:2/1\nK:Haste\n" +
        "T:Mode$ DamageDone | ValidSource$ Card.Self | ValidTarget$ Player | CombatDamage$ True | Execute$ TrigDraw\n" +
        "SVar:TrigDraw:AB$ Discard | Defined$ You | Mode$ TgtChoose | NumCards$ X | Cost$ Draw<1/You>\n" +
        "SVar:X:TriggeredSource$Valid Card.!ThisTurnEntered\n",
        "Name:Wyrm\nManaCost:16\nTypes:Artifact Creature Dragon\nPT:9/9\n" +
        "T:Mode$ Phase | Phase$ Upkeep | ValidPlayer$ You | TriggerZones$ Battlefield | Execute$ TrigSac\n" +
        "SVar:TrigSac:DB$ Sacrifice | UnlessCost$ UpkeepX | UnlessPayer$ You\n" +
        "SVar:X:Count$Domain/Twice\nSVar:UpkeepX:Number$10/Minus.X\n",
        "Name:Mob\nManaCost:9\nTypes:Kindred Sorcery Rogue\nK:Prowl:0\n" +
        "A:SP$ Draw | NumCards$ 1 | SubAbility$ DBLife\n" +
        "SVar:DBLife:DB$ GainLife | LifeAmount$ 5 | ConditionDefined$ Self | ConditionPresent$ Card.prowled\n",
        "Name:Stage\nManaCost:9\nTypes:Sorcery\nK:Spectacle:0\nA:SP$ Draw | NumCards$ 1\n",
        "Name:Thirst\nManaCost:0\nTypes:Sorcery\nA:SP$ GainLife | LifeAmount$ X\nSVar:X:Count$BloodthirstAmount\n",
    };

    private static readonly CardPool Pool = new(ForgeLibrary.FromScripts(Scripts, new Dictionary<string, string>()), null);

    private static Game Setup(string[] hand, string[]? battlefield = null, string[]? library = null) =>
        TestGames.Setup(Pool, hand, battlefield, library);

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
    public void ACreatureThatPlayerControls()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Spite", "Bear" });
        g.GainLife(Game.Me, 1);
        ResolveAll(g);
        Assert.DoesNotContain(g.State.BattlefieldCards, c => c.Name == "Bear");
    }

    [Fact]
    public void AReflexiveTriggerKeepsThePlayerItsSpawnerTriggeredOn()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Echo" });
        g.GainLife(Game.Me, 1);
        ResolveAll(g);
        Assert.Equal(38, Life(g));
    }

    [Theory]
    [InlineData(false, 0)]   // drew one, discarded one
    [InlineData(true, 1)]    // it entered this turn: no discard
    public void DrawThenDiscardUnlessItEnteredThisTurn(bool enteredThisTurn, int hand)
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Hacker" }, library: new[] { "Filler", "Filler" });
        if (enteredThisTurn) OnBattlefield(g, "Hacker").EnteredTurn = g.State.Turn;
        g.ResumeTurn();
        Assert.Equal(38, g.State.Players[1].Life);
        Assert.Equal(hand, g.State.Players[Game.Me].Hand.Count);
    }

    [Theory]
    [InlineData(new[] { "Woods", "Field", "Bog", "Bog", "Bog", "Bog" }, Zone.Battlefield)]   // {10} - 2×2 = {6}
    [InlineData(new[] { "Woods", "Field", "Bog", "Bog", "Bog" }, Zone.Graveyard)]
    public void DomainLowersTheUpkeepCost(string[] lands, Zone after)
    {
        var g = Setup(Array.Empty<string>(), battlefield: lands.Prepend("Wyrm").ToArray());
        g.State.Step = Step.Upkeep;
        g.FirePhase("Upkeep");
        ResolveAll(g);
        Assert.Equal(after, g.State.Cards.First(c => c.Name == "Wyrm").Zone);
    }

    [Fact]
    public void ProwlAfterARogueDealtCombatDamage()
    {
        var g = Setup(new[] { "Mob" }, battlefield: new[] { "Sneak" }, library: new[] { "Filler", "Filler" });
        var mob = g.State[InHand(g, "Mob")];
        Assert.False(g.CanCast(mob, 1));
        g.ResumeTurn();
        g.State.Step = Step.Main2;
        Assert.True(g.CanCast(mob, 1));
        g.CastSpell(mob, 1);
        ResolveAll(g);
        Assert.Equal(45, Life(g));
    }

    [Fact]
    public void SpectacleAfterAnOpponentLostLife()
    {
        var g = Setup(new[] { "Stage" });
        var stage = g.State[InHand(g, "Stage")];
        Assert.False(g.CanCast(stage, 1));
        g.LoseLife(1, 1, null);
        Assert.True(g.CanCast(stage, 1));
    }

    [Fact]
    public void TheDamageDealtToOpponentsThisTurn()
    {
        var g = Setup(new[] { "Thirst" }, battlefield: new[] { "Bear" }, library: new[] { "Filler", "Filler" });
        g.ResumeTurn();
        g.State.Step = Step.Main2;
        g.CastSpell(g.State[InHand(g, "Thirst")], 0);
        ResolveAll(g);
        Assert.Equal(42, Life(g));
    }
}
