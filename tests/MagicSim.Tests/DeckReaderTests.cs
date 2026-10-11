using MagicSim.Core.Decks;

namespace MagicSim.Tests;

public class DeckReaderTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void ReadsForTheLulz()
    {
        var deck = DeckReader.ReadFile(Fixture("For-The-Lulz.txt"));

        Assert.Equal("For-The-Lulz", deck.Name);
        Assert.Equal(new[] { "Rograkh, Son of Rohgahh", "Silas Renn, Seeker Adept" },
            deck.Commanders.Select(c => c.Name));
        Assert.Equal(100, deck.TotalCount);
        Assert.Equal(2, deck.Mainboard.Single(e => e.Name == "Island").Count);
        Assert.Empty(deck.Warnings);
    }

    [Theory]
    [InlineData("1 Sol Ring (LTC) 273", "Sol Ring")]
    [InlineData("1 Sol Ring (LTC)", "Sol Ring")]
    [InlineData("1x Sol Ring", "Sol Ring")]
    [InlineData("1 Sol Ring *F*", "Sol Ring")]
    [InlineData("1 Sol Ring (C21) 263 *F*", "Sol Ring")]
    [InlineData("1 Expansion // Explosion", "Expansion // Explosion")]
    [InlineData("1 Lim-Dul's Vault", "Lim-Dul's Vault")]
    public void StripsSetCodesAndMarkers(string line, string expected)
    {
        var deck = DeckReader.Parse(line);
        Assert.Equal(expected, deck.Mainboard.Single().Name);
    }

    [Fact]
    public void SkipsCommentsAndMergesDuplicates()
    {
        var deck = DeckReader.Parse("""
            # a comment
            // another
            1 Atraxa, Praetors' Voice *CMDR*

            1 Island
            1 Island (LEA) 288
            """);
        Assert.Single(deck.Commanders);
        Assert.Equal(new DeckEntry(2, "Island"), deck.Mainboard.Single());
        Assert.Empty(deck.Warnings);
    }

    [Fact]
    public void UntaggedListWarnsAboutMissingCommander()
    {
        var deck = DeckReader.Parse("1 Sol Ring\n1 Island\n");
        Assert.Empty(deck.Commanders);
        Assert.Contains(deck.Warnings, w => w.Contains("*CMDR*"));
    }

    [Fact]
    public void BadLinesBecomeWarnings()
    {
        var deck = DeckReader.Parse("1 Sol Ring *CMDR*\nSideboard\n1 Island");
        Assert.Single(deck.Warnings);
        Assert.Contains("line 2", deck.Warnings[0]);
    }

    [Fact]
    public void WriterRoundTripsCanonicalForm()
    {
        var text = File.ReadAllText(Fixture("For-The-Lulz.txt")).Replace("\r\n", "\n");
        var deck = DeckReader.Parse(text);
        Assert.Equal(text, DeckWriter.Format(deck));
    }
}
