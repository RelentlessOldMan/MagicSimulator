using System.Text.Json;
using MagicSim.Core.Decks;
using MagicSim.Core.Sim;
using MagicSim.ForgeWrapper;

namespace MagicSim.Tests;

/// <summary>
/// The Forge playtest wrapper without running Forge: its output lines (written here in the format Forge prints),
/// the .dck files it reads, opponent lists, and the JSON and report built from the results.
/// </summary>
public class ForgeWrapperTests
{
    [Fact]
    public void ForgeOutputGivesEachGamesWinnerTurnAndTime()
    {
        var games = ForgeOutputParser.Parse(new[]
        {
            "Simulation mode",
            "Ai(1)-Alpha vs Ai(2)-Beta - three games of Commander",
            "Game Outcome: Turn 9",
            "Game Outcome: Ai(1)-Alpha has won because all opponents have lost",
            "",
            "Game Result: Game 1 ended in 9669 ms. Ai(1)-Alpha has won!",
            "Game Outcome: Turn 12",
            "Game Result: Game 2 ended in 7030 ms. Ai(2)-Beta (2) has won!",
            "Stopping slow match as draw",
            "Game Result: Game 3 ended in a Draw! Took 120004 ms.",
        });
        Assert.Equal(new[]
        {
            new ForgeGame(1, 1, 9, 9669, false),
            new ForgeGame(2, 2, 12, 7030, false),
            new ForgeGame(3, null, null, 120004, true),
        }, games);
        Assert.Equal(5, Gauntlet.Round(games[0]));
        Assert.Equal(6, Gauntlet.Round(games[1]));
    }

    [Fact]
    public void ForgeRefusingADeckIsReported()
    {
        var parser = new ForgeOutputParser();
        Assert.Null(parser.Read("Could not load deck - opp3.dck, match cannot start"));
        Assert.Equal("Could not load deck - opp3.dck, match cannot start", parser.LoadError);
    }

    [Fact]
    public void AFailedForgeStartIsReportedByItsException()
    {
        Assert.Equal("java.lang.RuntimeException: cannot create profile directory: C:\\x\\cache\\db\\", ForgeApp.FirstError(new[]
        {
            "Stomping Ground Did not have activator set in SpellAbility_Condition.checkConditions()",
            "main > java.lang.RuntimeException: cannot create profile directory: C:\\x\\cache\\db\\",
            "\tat forge.model.FModel.initialize(FModel.java:244)",
            "\tat forge.view.Main.main(Main.java:104)",
        }));
        Assert.Equal("a | b | c", ForgeApp.FirstError(new[] { "z", "a", "", "b", "c" }));
        Assert.Equal("no output", ForgeApp.FirstError(Array.Empty<string>()));
    }

    [Fact]
    public void DecksAreWrittenInForgesFormat()
    {
        var deck = DeckReader.Parse("1 Krenko, Mob Boss *CMDR*\n1 Sol Ring\n30 Mountain\n", "Goblins");
        Assert.Equal("[metadata]\nName=Goblins\n[Commander]\n1 Krenko, Mob Boss\n[Main]\n1 Sol Ring\n30 Mountain\n", DckWriter.Write(deck));
    }

    [Theory]
    [InlineData(20, 10, 1, new[] { 20 })]
    [InlineData(20, 1, 4, new[] { 5, 5, 5, 5 })]
    [InlineData(12, 2, 4, new[] { 6, 6 })]
    [InlineData(7, 1, 4, new[] { 7 })]
    [InlineData(11, 1, 2, new[] { 6, 5 })]
    public void GamesAreSplitSoEveryProcessIsBusyButNotTooSmall(int games, int opponents, int parallel, int[] expected) =>
        Assert.Equal(expected, Gauntlet.Batches(games, opponents, parallel));

    [Fact]
    public void OpponentsAreDecksFromFoldersAndListsButNotNotesOrMoxfieldCopies()
    {
        var dir = Path.Combine(Path.GetTempPath(), "magicsim-gauntlet-" + Guid.NewGuid().ToString("N"));
        var other = Path.Combine(dir, "other");
        Directory.CreateDirectory(other);
        try
        {
            const string deck = "1 Krenko, Mob Boss *CMDR*\n99 Mountain\n";
            foreach (var name in new[] { "Me", "Alpha", "Beta", "Beta-moxfield" }) File.WriteAllText(Path.Combine(dir, name + ".txt"), deck);
            File.WriteAllText(Path.Combine(dir, "Alpha-strategy.txt"), "Win with goblins.\n1 thing to remember\n");
            File.WriteAllText(Path.Combine(other, "Gamma.txt"), deck);
            File.WriteAllText(Path.Combine(dir, "gauntlet.txt"), "# friends' decks\nother/Gamma.txt\nAlpha.txt\n");

            string Names(IEnumerable<string> paths) => string.Join(",", paths.Select(Path.GetFileNameWithoutExtension));
            var me = Path.Combine(dir, "Me.txt");
            Assert.Equal("Alpha,Beta", Names(Gauntlet.ResolveOpponents(me, Array.Empty<string>())));
            Assert.Equal("Gamma,Alpha", Names(Gauntlet.ResolveOpponents(me, new[] { Path.Combine(dir, "gauntlet.txt") })));
            Assert.Equal("Gamma,Beta", Names(Gauntlet.ResolveOpponents(me, new[] { other, Path.Combine(dir, "Beta.txt"), me })));
            Assert.Throws<FileNotFoundException>(() => Gauntlet.ResolveOpponents(me, new[] { Path.Combine(dir, "nope.txt") }));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void WilsonIntervals()
    {
        Assert.Null(GauntletReport.Wilson(0, 0));
        Assert.Equal(new[] { 0.2366, 0.7634 }, GauntletReport.Wilson(5, 10));
        Assert.Equal(new[] { 0.0, 0.2775 }, GauntletReport.Wilson(0, 10));
    }

    private static JsonElement ForgeDocument()
    {
        var result = new GauntletResult("Goblins", @"C:\decks\Goblins.txt", new List<string>(), "2.0.15", new GauntletOptions { Games = 3 },
            new List<MatchupResult>
            {
                new("Alpha", @"C:\decks\Alpha.txt", new List<string> { "Made Up Card" }, new List<ForgeGame>
                {
                    new(1, 1, 9, 9000, false), new(2, 2, 12, 7000, false), new(3, null, null, 120000, true),
                }, null),
                new("Beta", @"C:\decks\Beta.txt", new List<string>(), new List<ForgeGame>(), "Could not load deck - the opponent deck, match cannot start"),
            }, 61.5);
        return JsonDocument.Parse(JsonSerializer.Serialize(GauntletReport.Document(result))).RootElement;
    }

    [Fact]
    public void TheForgeDocumentCountsWinsLossesDrawsAndRounds()
    {
        var doc = ForgeDocument();
        Assert.Equal("magicsim.forge/1", doc.GetProperty("schema").GetString());
        Assert.Equal("C:/decks/Goblins.txt", doc.GetProperty("deck").GetProperty("path").GetString());
        var totals = doc.GetProperty("totals");
        Assert.Equal((3, 1, 1, 1), (totals.GetProperty("games").GetInt32(), totals.GetProperty("wins").GetInt32(),
            totals.GetProperty("losses").GetInt32(), totals.GetProperty("draws").GetInt32()));
        var alpha = doc.GetProperty("opponents")[0];
        Assert.Equal(0.3333, alpha.GetProperty("win_rate").GetDouble());
        Assert.Equal(5, alpha.GetProperty("mean_win_round").GetDouble());
        Assert.Equal(6, alpha.GetProperty("mean_loss_round").GetDouble());
        Assert.Equal(1, alpha.GetProperty("timeouts").GetInt32());
        Assert.Equal(JsonValueKind.Null, alpha.GetProperty("error").ValueKind);
        var beta = doc.GetProperty("opponents")[1];
        Assert.Equal(0, beta.GetProperty("games").GetInt32());
        Assert.Equal(JsonValueKind.Null, beta.GetProperty("win_rate").ValueKind);
        Assert.Equal(JsonValueKind.Null, beta.GetProperty("mean_win_round").ValueKind);
        Assert.StartsWith("Could not load deck", beta.GetProperty("error").GetString());
    }

    [Fact]
    public void TheReportShowsForgeResultsEvenWithoutGoldfishRuns()
    {
        var html = HtmlReport.Build(Array.Empty<JsonElement>(), new DateTime(2026, 10, 12), new[] { ForgeDocument() });
        Assert.Contains("Forge playtest", html);
        Assert.Contains("<td>Alpha</td><td>3</td><td>1</td><td>1</td><td>1</td><td>33.3%</td>", html);
        Assert.Contains("1 card unknown to Forge", html);
        Assert.Contains("Could not load deck", html);
        // A run without Forge results has no Forge section: RunsBothTableSizesOnTheSameShuffles checks that.
    }
}
