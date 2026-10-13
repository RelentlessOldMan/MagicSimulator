using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using MagicSim.Core.Players;
using static MagicSim.Tests.TestGames;

namespace MagicSim.Tests;

/// <summary>
/// Voting, gaining control of one permanent per opponent or of everything, opponents who didn't do something, and a
/// spell that wins the second time it's cast from hand, on hand-written cards in Forge's format.
/// </summary>
public class VoteAndControlTests
{
    private static readonly string[] Scripts =
    {
        "Name:Filler\nManaCost:9\nTypes:Sorcery\n",
        "Name:Bear\nManaCost:0\nTypes:Creature Bear\nPT:2/2\n",
        "Name:Council\nManaCost:0\nTypes:Sorcery\n" +
        "A:SP$ Vote | Defined$ Player | Choices$ DBLife,DBDraw\n" +
        "SVar:DBLife:DB$ GainLife | LifeAmount$ 5 | Defined$ You | SpellDescription$ Life\n" +
        "SVar:DBDraw:DB$ Draw | Defined$ You | NumCards$ 1 | SpellDescription$ Draw\n",
        "Name:Each Council\nManaCost:0\nTypes:Sorcery\n" +
        "A:SP$ Vote | Defined$ Player | Choices$ DBLife,DBDraw | EachVote$ True\n" +
        "SVar:DBLife:DB$ GainLife | LifeAmount$ 5 | Defined$ You | SpellDescription$ Life\n" +
        "SVar:DBDraw:DB$ Draw | Defined$ You | NumCards$ 1 | SpellDescription$ Draw\n",
        "Name:Heist\nManaCost:0\nTypes:Sorcery\n" +
        "A:SP$ GainControl | ValidTgts$ Permanent.OppCtrl | TgtPrompt$ Select target permanent | TargetMin$ 0 | TargetMax$ OneEach | TargetsForEachPlayer$ True\n" +
        "SVar:OneEach:PlayerCountOpponents$Amount\n",
        "Name:Uprising\nManaCost:0\nTypes:Sorcery\n" +
        "A:SP$ GainControl | AllValid$ Creature | Untap$ True | LoseControl$ EOT | AddKWs$ Haste\n",
        "Name:Gift\nManaCost:0\nTypes:Sorcery\nA:SP$ Draw | Defined$ OppNonRememberedOwner | NumCards$ 1\n",
        "Name:Approach of the Second Sun\nManaCost:0\nTypes:Sorcery\n" +
        "T:Mode$ SpellCast | ValidCard$ Card.Self | Static$ True | Execute$ Mark\n" +
        "SVar:Mark:DB$ Effect | Name$ SunMark | Duration$ Permanent\n" +
        "A:SP$ Branch | BranchConditionSVar$ X | BranchConditionSVarCompare$ EQ3 | TrueSubAbility$ Win | FalseSubAbility$ Life\n" +
        "SVar:Win:DB$ WinsGame | Defined$ You\n" +
        "SVar:Life:DB$ GainLife | LifeAmount$ 7 | SubAbility$ Back\n" +
        "SVar:Back:DB$ ChangeZone | Origin$ Stack | Destination$ Library | LibraryPosition$ 6 | Defined$ Parent\n" +
        "SVar:Marks:Count$ValidCommand Effect.YouCtrl+namedSunMark/LimitMax.2\n" +
        "SVar:FromHand:Count$ValidStack Card.wasCastFromYourHand+Self/LimitMax.1\n" +
        "SVar:X:SVar$Marks/Plus.FromHand\n",
    };

    private static readonly CardPool Pool = new(ForgeLibrary.FromScripts(Scripts, new Dictionary<string, string>()), null);

    private static Game Setup(string[] hand, string[]? battlefield = null, string[]? library = null) =>
        TestGames.Setup(Pool, hand, battlefield, library);

    private static void Cast(Game g, string name)
    {
        g.CastSpell(g.State[InHand(g, name)], 0);
        ResolveAll(g);
    }

    private static int Life(Game g) => g.State.Players[Game.Me].Life;

    private static string[] Fillers(int n) => Enumerable.Repeat("Filler", n).ToArray();

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
    public void GoldfishOpponentsVoteAgainstYou()
    {
        // You vote for life; both goldfish opponents vote for the other choice and win the vote.
        var g = Setup(new[] { "Council" }, library: Fillers(3));
        Cast(g, "Council");
        Assert.Equal(40, Life(g));
        Assert.Single(g.State.Players[Game.Me].Hand);
    }

    [Fact]
    public void EachVoteHappensOncePerVoter()
    {
        var g = Setup(new[] { "Each Council" }, library: Fillers(3));
        Cast(g, "Each Council");
        Assert.Equal(45, Life(g));                                  // your vote
        Assert.Equal(2, g.State.Players[Game.Me].Hand.Count);       // one card per opponent's vote
    }

    [Fact]
    public void OnePermanentFromEachOpponent()
    {
        var g = Setup(new[] { "Heist" }, battlefield: new[] { "Bear", "Bear", "Bear" });
        var bears = g.State.BattlefieldCards.Where(c => c.Name == "Bear").ToList();
        bears[0].Controller = 1;
        bears[1].Controller = 1;
        bears[2].Controller = 2;
        Cast(g, "Heist");
        Assert.Equal(new[] { Game.Me, Game.Me, 1 }.Order(), bears.Select(b => b.Controller).Order());
    }

    [Fact]
    public void GainControlOfAllCreaturesUntilEndOfTurn()
    {
        var g = Setup(new[] { "Uprising" }, battlefield: new[] { "Bear", "Bear" });
        var bears = g.State.BattlefieldCards.Where(c => c.Name == "Bear").ToList();
        bears[0].Controller = 1;
        bears[0].Tapped = true;
        bears[1].Controller = 2;
        Cast(g, "Uprising");
        Assert.All(bears, b => Assert.Equal(Game.Me, b.Controller));
        Assert.False(bears[0].Tapped);
    }

    [Fact]
    public void OpponentsWhoDidntDoItDraw()
    {
        var g = Setup(new[] { "Gift" });
        var before = g.State.Players[1].HandCount;
        Cast(g, "Gift");
        Assert.Equal(before + 1, g.State.Players[1].HandCount);
        Assert.Equal(before + 1, g.State.Players[2].HandCount);
    }

    [Fact]
    public void TheSecondApproachFromHandWins()
    {
        var g = Setup(new[] { "Approach of the Second Sun" }, library: Fillers(8));
        Cast(g, "Approach of the Second Sun");
        Assert.Equal(47, Life(g));
        var me = g.State.Players[Game.Me];
        Assert.Equal("Approach of the Second Sun", g.State[me.Library[6]].Name);
        Assert.False(g.State.GameOver);

        g.MoveTo(g.State[me.Library[6]], Zone.Hand);
        Cast(g, "Approach of the Second Sun");
        Assert.True(g.State.GameOver);
        Assert.Equal(Game.Me, g.State.Winner);
    }
}
