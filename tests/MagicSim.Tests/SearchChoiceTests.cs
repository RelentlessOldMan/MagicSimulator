using MagicSim.Core.Players;

namespace MagicSim.Tests;

/// <summary>How the search picks among options from their rollout scores (successive halving).</summary>
public class SearchChoiceTests
{
    private static Func<int, int, double> Scripted(double[][] scores) => (i, k) => scores[i][k];

    [Fact]
    public void ASurvivorThatLosesLaterIsNotChosenOverAnOptionDroppedEarly()
    {
        // Pass looks slightly worse after one rollout and is dropped; both survivors then lose their second rollout.
        var scores = new[]
        {
            new[] { 158.0, 158.0 },                     // pass
            new[] { 166.0, -SearchDecider.WinScore },   // Vampiric Tutor
            new[] { 165.0, -SearchDecider.WinScore },   // Brainstorm
            new[] { 100.0, 100.0 },
        };
        var (best, _, count) = SearchDecider.Choose(4, 2, Scripted(scores));
        Assert.Equal(0, best);
        Assert.Equal(2, count[0]);   // the chosen option had every rollout
    }

    [Fact]
    public void TheBestOptionStillWins()
    {
        var scores = new[] { new[] { 10.0, 12.0 }, new[] { 50.0, 48.0 }, new[] { 30.0, 31.0 } };
        Assert.Equal(1, SearchDecider.Choose(3, 2, Scripted(scores)).Best);
    }

    [Fact]
    public void AnOptionThatWinsEveryRolloutIsTakenAtOnce()
    {
        var calls = 0;
        var (best, _, count) = SearchDecider.Choose(5, 3, (i, k) => { calls++; return i == 1 ? SearchDecider.WinScore : 0; });
        Assert.Equal(1, best);
        Assert.Equal(3, count[1]);
        Assert.Equal(4, calls);   // option 0 once, then option 1 three times
    }

    [Fact]
    public void AWinThatDoesNotRepeatIsNotASureThing()
    {
        var scores = new[] { new[] { SearchDecider.WinScore, -SearchDecider.WinScore }, new[] { 20.0, 20.0 } };
        Assert.Equal(1, SearchDecider.Choose(2, 2, Scripted(scores)).Best);
    }
}
