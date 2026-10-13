using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using MagicSim.Core.Players;
using static MagicSim.Tests.TestGames;

namespace MagicSim.Tests;

/// <summary>
/// The monarch, "if you're the monarch", combat damage taking the crown, "when an opponent becomes the monarch" and
/// "can't cast more than one spell each turn", on hand-written cards in Forge's format.
/// </summary>
public class MonarchTests
{
    private static readonly string[] Scripts =
    {
        "Name:Filler\nManaCost:9\nTypes:Sorcery\n",
        "Name:Ping\nManaCost:0\nTypes:Sorcery\nA:SP$ GainLife | LifeAmount$ 1\n",
        "Name:Bear\nManaCost:0\nTypes:Creature Bear\nPT:2/2\n",
        "Name:Crown\nManaCost:0\nTypes:Enchantment\n" +
        "T:Mode$ ChangesZone | Origin$ Any | Destination$ Battlefield | ValidCard$ Card.Self | Execute$ TrigMonarch\n" +
        "SVar:TrigMonarch:DB$ BecomeMonarch\n",
        "Name:Court\nManaCost:0\nTypes:Enchantment\n" +
        "T:Mode$ Phase | Phase$ Upkeep | ValidPlayer$ You | TriggerZones$ Battlefield | Execute$ TrigHit\n" +
        "SVar:TrigHit:DB$ DealDamage | Defined$ Player.Opponent | NumDmg$ X\nSVar:X:Count$Monarch.7.2\n",
        "Name:Despot\nManaCost:0\nTypes:Enchantment\n" +
        "T:Mode$ Phase | Phase$ Upkeep | ValidPlayer$ You | TriggerZones$ Battlefield | CheckDefinedPlayer$ You.isMonarch | Execute$ TrigLife\n" +
        "SVar:TrigLife:DB$ GainLife | LifeAmount$ 5\n",
        "Name:Rival\nManaCost:0\nTypes:Enchantment\n" +
        "T:Mode$ Phase | Phase$ Upkeep | ValidPlayer$ You | TriggerZones$ Battlefield | CheckDefinedPlayer$ Opponent.isMonarch | Execute$ TrigLife\n" +
        "SVar:TrigLife:DB$ GainLife | LifeAmount$ 5\n",
        "Name:Jailer\nManaCost:0\nTypes:Creature Human Soldier\nPT:2/2\n" +
        "T:Mode$ ChangesZone | Origin$ Any | Destination$ Battlefield | ValidCard$ Card.Self | Execute$ TrigExile\n" +
        "SVar:TrigExile:DB$ ChangeZone | Origin$ Battlefield | Destination$ Exile | ValidTgts$ Creature.Bear | SubAbility$ DBEffect\n" +
        "SVar:DBEffect:DB$ Effect | Triggers$ ComeBack | RememberObjects$ You & Targeted | Duration$ Permanent\n" +
        "SVar:ComeBack:Mode$ BecomeMonarch | ValidPlayer$ Player.OpponentOf Remembered | Execute$ TrigReturn | TriggerZones$ Command | Static$ True\n" +
        "SVar:TrigReturn:DB$ ChangeZoneAll | Origin$ Exile | Destination$ Battlefield | ChangeType$ Card.IsRemembered\n",
        "Name:Ambition\nManaCost:0\nTypes:Enchantment\n" +
        "T:Mode$ Phase | Phase$ Upkeep | ValidPlayer$ You | TriggerZones$ Battlefield | Execute$ TrigRepeat\n" +
        "SVar:TrigRepeat:DB$ RepeatEach | RepeatPlayers$ Opponent | RepeatSubAbility$ DBDrain\n" +
        "SVar:DBDrain:DB$ LoseLife | Defined$ Player.IsRemembered | LifeAmount$ X | UnlessCost$ Discard<Y/Card> | UnlessPayer$ Player.IsRemembered\n" +
        "SVar:X:Count$Monarch.6.3\nSVar:Y:Count$Monarch.2.1\n",
        "Name:Archon\nManaCost:0\nTypes:Creature Archon\nPT:2/3\n" +
        "S:Mode$ CantBeCast | ValidCard$ Card | Caster$ Player | NumLimitEachTurn$ 1\n",
    };

    private static readonly CardPool Pool = new(ForgeLibrary.FromScripts(Scripts, new Dictionary<string, string>()), null);

    private static Game Setup(string[] hand, string[]? battlefield = null, string[]? library = null) =>
        TestGames.Setup(Pool, hand, battlefield, library);

    private static void Cast(Game g, string name)
    {
        var c = g.State[InHand(g, name)];
        g.CastSpell(c, c.Types.IsPermanent ? -1 : 0);
        ResolveAll(g);
    }

    private static void Upkeep(Game g)
    {
        g.State.Step = Step.Upkeep;
        g.FirePhase("Upkeep");
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
    public void TheMonarchDrawsAtTheirEndStep()
    {
        var g = Setup(new[] { "Crown" }, library: new[] { "Filler", "Filler" });
        Cast(g, "Crown");
        Assert.Equal(Game.Me, g.State.Monarch);
        g.ResumeTurn();
        Assert.Single(g.State.Players[Game.Me].Hand);
    }

    [Fact]
    public void MoreIfYouAreTheMonarch()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Court", "Despot", "Rival" });
        Upkeep(g);
        Assert.Equal(38, g.State.Players[1].Life);
        Assert.Equal(40, g.State.Players[Game.Me].Life);

        g.BecomeMonarch(Game.Me);
        Upkeep(g);
        Assert.Equal(31, g.State.Players[1].Life);
        Assert.Equal(45, g.State.Players[Game.Me].Life);

        g.BecomeMonarch(1);
        Upkeep(g);
        Assert.Equal(29, g.State.Players[1].Life);
        Assert.Equal(50, g.State.Players[Game.Me].Life);   // the rival's trigger now
    }

    [Fact]
    public void CombatDamageToTheMonarchTakesTheCrown()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Bear" }, library: new[] { "Filler", "Filler" });
        g.BecomeMonarch(1);
        g.State.Players[2].Life = 30;   // the bear attacks the opponent with the least life, so make that the monarch
        g.State.Players[1].Life = 20;
        g.ResumeTurn();
        Assert.Equal(18, g.State.Players[1].Life);
        Assert.Equal(Game.Me, g.State.Monarch);
        Assert.Single(g.State.Players[Game.Me].Hand);   // and drew at the end step
    }

    [Fact]
    public void ExiledUntilAnOpponentBecomesTheMonarch()
    {
        var g = Setup(new[] { "Jailer" }, battlefield: new[] { "Bear" });
        var bear = OnBattlefield(g, "Bear");
        Cast(g, "Jailer");
        Assert.Equal(Zone.Exile, bear.Zone);
        g.BecomeMonarch(Game.Me);
        ResolveAll(g);
        Assert.Equal(Zone.Exile, bear.Zone);
        g.BecomeMonarch(1);
        ResolveAll(g);
        Assert.Equal(Zone.Battlefield, bear.Zone);
    }

    [Fact]
    public void GoldfishOpponentsDiscardOnlyToSurvive()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Ambition" });
        g.BecomeMonarch(Game.Me);
        var hand = g.State.Players[1].AbstractHand;
        g.State.Players[2].Life = 6;
        Upkeep(g);
        Assert.Equal(34, g.State.Players[1].Life);              // takes the 6
        Assert.Equal(hand, g.State.Players[1].AbstractHand);
        Assert.Equal(6, g.State.Players[2].Life);               // 6 would kill, so they discard two
        Assert.Equal(hand - 2, g.State.Players[2].AbstractHand);

        g.State.Players[2].AbstractHand = 1;                     // can't discard two: they lose
        Upkeep(g);
        Assert.True(g.State.Players[2].Lost);
    }

    [Fact]
    public void OnlyOneSpellEachTurn()
    {
        var g = Setup(new[] { "Ping", "Ping" }, battlefield: new[] { "Archon" });
        Cast(g, "Ping");
        Assert.False(g.CanCast(g.State[InHand(g, "Ping")], 0));
        g.State.ThisTurn = new TurnInfo();
        Assert.True(g.CanCast(g.State[InHand(g, "Ping")], 0));
    }
}
