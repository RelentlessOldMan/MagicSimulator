using System.Text.Json;
using MagicSim.Core.Decks;
using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using MagicSim.Core.Players;
using MagicSim.Core.Sim;
using MagicSim.ForgeWrapper;

namespace MagicSim.Tests;

/// <summary>Per-deck report pages, the per-game rows they read, and how `sim batch` spreads Forge opponents.</summary>
public class DeckPageTests
{
    private static readonly string[] Scripts =
    {
        "Name:Bog\nTypes:Land Swamp\nA:AB$ Mana | Cost$ T | Produced$ B\n",
        "Name:Bear\nManaCost:1\nTypes:Creature Bear\nPT:2/2\n",
        "Name:Chief\nManaCost:1\nTypes:Legendary Creature Bear\nPT:1/1\n",
    };

    private static readonly CardPool Pool = new(ForgeLibrary.FromScripts(Scripts, new Dictionary<string, string>()), null);

    [Fact]
    public void ADeckPageShowsTheFastestHandsAndTheirPlays()
    {
        var deck = Runner.Prepare(DeckReader.Parse("1 Chief *CMDR*\n\n40 Bog\n59 Bear\n", "Bears"), Pool);
        var o = new RunOptions { Games = 12, MaxTurn = 8, Mulligans = new[] { MulliganPolicy.C }, Rollouts = 1, Threads = 1 };
        var games = Runner.Run(deck, o);
        Assert.All(games, g => Assert.Equal(g.Cast.Count, g.CastTurns.Count));
        Assert.Contains(games, g => g.CastTurns.Contains(1) && g.CastTurns.Contains(2));

        var rows = GameRow.Read(games.Select(Report.GameLine));
        Assert.Equal(games.Count, rows.Count);
        Assert.Equal(games[0].HandKept, rows[0].HandKept);
        Assert.Equal(games[0].CastTurns, rows[0].CastTurns);

        var run = JsonSerializer.SerializeToElement(Report.RunDocument(deck, "bears.txt", o, games), Report.Json);
        var html = HtmlReport.BuildDeck(run, rows, null, new DateTime(2026, 1, 1), "../report.html");
        Assert.Contains("<h1>Bears</h1>", html);
        Assert.Contains("<h2>Fastest wins</h2>", html);
        Assert.Contains("<h2>Worst games</h2>", html);
        Assert.Contains("<h2>Opening hand cards</h2>", html);
        Assert.Contains("<b>T1</b> Bog", html);
        Assert.Contains("sim replay &quot;bears.txt&quot; --game ", html);
        Assert.Contains("<span class=\"chip\">Bear</span>", html);
        Assert.Contains("href=\"../report.html\"", html);

        // Without per-game rows the page still has the curves, and says how to get the games.
        var bare = HtmlReport.BuildDeck(run, new List<GameRow>(), null, new DateTime(2026, 1, 1), null);
        Assert.Contains("--per-game", bare);
        Assert.DoesNotContain("Fastest wins", bare);

        var index = HtmlReport.Build(new[] { run }, new DateTime(2026, 1, 1), null, new Dictionary<string, string> { ["Bears"] = "decks/Bears.html" });
        Assert.Contains("<a href=\"decks/Bears.html\">Bears</a>", index);
    }

    [Fact]
    public void ForgeOpponentsAreSpreadEvenly()
    {
        Assert.Equal(new[] { 1, 7, 14, 21, 28, 35 }, Gauntlet.SpreadOffsets(42, 6));
        Assert.Equal(new[] { 1, 2, 3 }, Gauntlet.SpreadOffsets(4, 10));   // at most everyone else
        Assert.Empty(Gauntlet.SpreadOffsets(1, 3));
        foreach (var (n, k) in new[] { (5, 2), (17, 5), (42, 41) })
        {
            var offsets = Gauntlet.SpreadOffsets(n, k);
            Assert.Equal(k, offsets.Distinct().Count());
            Assert.All(offsets, x => Assert.InRange(x, 1, n - 1));
        }
    }
}
