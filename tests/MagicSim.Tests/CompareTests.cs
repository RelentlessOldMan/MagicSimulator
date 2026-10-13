using MagicSim.Core.Decks;
using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using MagicSim.Core.Players;
using MagicSim.Core.Sim;

namespace MagicSim.Tests;

/// <summary>`sim compare`: swaps, pairing by shuffle, and the paired statistics, on hand-written cards in Forge's format.</summary>
public class CompareTests
{
    private static readonly string[] Scripts =
    {
        "Name:Bog\nTypes:Land Swamp\nA:AB$ Mana | Cost$ T | Produced$ B\n",
        "Name:Bear\nManaCost:1\nTypes:Creature Bear\nPT:2/2\n",
        "Name:Giant\nManaCost:1\nTypes:Creature Giant\nPT:9/9\n",
        "Name:Lump\nManaCost:9\nTypes:Sorcery\n",
        "Name:Chief\nManaCost:1\nTypes:Legendary Creature Bear\nPT:1/1\n",
    };

    private static readonly CardPool Pool = new(ForgeLibrary.FromScripts(Scripts, new Dictionary<string, string>()), null);

    private static Deck Bears()
    {
        var d = DeckReader.Parse("1 Chief *CMDR*\n\n40 Bog\n58 Bear\n1 Lump\n", "Bears");
        return d;
    }

    private static readonly RunOptions Options = new()
    {
        Games = 40, MaxTurn = 8, Mulligans = new[] { MulliganPolicy.C }, Tables = new[] { "1v1" }, Rollouts = 1, Threads = 1,
    };

    [Fact]
    public void SwapsReplaceOneCopy()
    {
        var v = Compare.Apply(Bears(), Compare.ParseSwaps("bear=>Giant; Lump => Giant"), Pool);
        Assert.Equal("Bear=>Giant; Lump=>Giant", v.Label);
        Assert.Equal(57, v.Deck.Mainboard.Single(e => e.Name == "Bear").Count);
        Assert.Equal(2, v.Deck.Mainboard.Single(e => e.Name == "Giant").Count);
        Assert.DoesNotContain(v.Deck.Mainboard, e => e.Name == "Lump");
        Assert.Equal(58, Bears().Mainboard.Single(e => e.Name == "Bear").Count);   // the base deck is untouched
    }

    [Fact]
    public void BadSwapsAreRefused()
    {
        Assert.Contains("isn't in Bears's main deck", Assert.Throws<SwapException>(() => Compare.Apply(Bears(), Compare.ParseSwaps("Giant=>Bear"), Pool)).Message);
        Assert.Contains("isn't a known card", Assert.Throws<SwapException>(() => Compare.Apply(Bears(), Compare.ParseSwaps("Bear=>Nothing"), Pool)).Message);
        Assert.Contains("commander", Assert.Throws<SwapException>(() => Compare.Apply(Bears(), Compare.ParseSwaps("Chief=>Bear"), Pool)).Message);
        Assert.Throws<SwapException>(() => Compare.ParseSwaps("Bear->Giant"));
    }

    [Fact]
    public void ADeckFileVariantListsTheCardDifference()
    {
        var other = DeckReader.Parse("1 Chief *CMDR*\n\n40 Bog\n56 Bear\n3 Giant\n", "Giants");
        var v = Compare.FromDeck(Bears(), other);
        Assert.Equal("Giants", v.Label);
        Assert.Equal(new[] { ("Bear", "Giant"), ("Bear", "Giant"), ("Lump", "Giant") }, v.Swaps.Select(s => (s.Out, s.In)));
    }

    [Fact]
    public void TheSameDeckComparesAsNoChange()
    {
        var deck = Runner.Prepare(Bears(), Pool);
        var a = Runner.Run(deck, Options);
        var b = Runner.Run(deck, Options, variant: "same");
        var row = Assert.Single(Compare.DeltaRows(Options, a, b), r => (string)r["seat"]! == "weighted");
        Assert.Equal(0.0, row["delta_mean_turn_capped"]);
        Assert.Equal(1.0, row["p_value_mean_turn_capped"]);
        Assert.All(((Dictionary<string, double>)row["p_value_by_turn"]!).Values, p => Assert.Equal(1.0, p));
    }

    [Fact]
    public void BiggerCreaturesWinFasterOnTheSameShuffles()
    {
        var baseDeck = Runner.Prepare(Bears(), Pool);
        var giants = Compare.Apply(Bears(), Enumerable.Repeat(new Swap("Bear", "Giant"), 20).ToList(), Pool);
        var a = Runner.Run(baseDeck, Options);
        var b = Runner.Run(Runner.Prepare(giants.Deck, Pool), Options, variant: giants.Label);
        var row = Compare.DeltaRows(Options, a, b).Single(r => (string)r["seat"]! == "weighted");
        Assert.True((double)row["delta_mean_turn_capped"]! < -0.5);
        var ci = (double[])row["delta_mean_turn_capped_ci95"]!;
        Assert.True(ci[1] < 0);
        Assert.True((double)row["p_value_mean_turn_capped"]! < 0.01);
    }

    [Fact]
    public void McNemarMatchesTheExactBinomial()
    {
        Assert.Equal(1.0, Compare.McNemar(0, 0));
        Assert.Equal(0.125, Compare.McNemar(4, 0), 6);           // 2 × (1/2)^4
        Assert.Equal(0.125, Compare.McNemar(1, 6), 6);           // 2 × (1 + 7) / 128
        Assert.Equal(0.2890625, Compare.McNemar(2, 6), 6);       // 2 × (1 + 8 + 28) / 256
        Assert.Equal(1.0, Compare.McNemar(5, 5));
        Assert.InRange(Compare.McNemar(5000, 4700), 0.0005, 0.0025);   // large n doesn't underflow
    }

    [Fact]
    public void NormalCdfIsAccurate()
    {
        Assert.Equal(0.5, Compare.NormalCdf(0), 6);
        Assert.Equal(0.975, Compare.NormalCdf(1.959964), 5);
        Assert.Equal(0.025, Compare.NormalCdf(-1.959964), 5);
    }
}
