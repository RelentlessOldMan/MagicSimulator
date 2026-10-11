using System.Text;
using System.Text.Json;
using MagicSim.Core.Cards;
using MagicSim.Core.Decks;

namespace MagicSim.Tests;

public class CardIndexTests
{
    private static CardIndex Sample() => CardIndex.FromCards(new[]
    {
        new CardData { Name = "Sol Ring", CommanderLegality = "legal" },
        new CardData { Name = "Lim-Dûl's Vault", CommanderLegality = "legal" },
        new CardData
        {
            Name = "Expansion // Explosion", CommanderLegality = "legal",
            Faces = new() { new CardFace { Name = "Expansion" }, new CardFace { Name = "Explosion" } },
        },
    });

    [Theory]
    [InlineData("Sol Ring")]
    [InlineData("sol ring")]
    [InlineData("Lim-Dul's Vault")]
    [InlineData("Lim-Dûl's Vault")]
    [InlineData("Expansion // Explosion")]
    [InlineData("Expansion")]
    public void FindsByNormalizedOrFaceName(string name) => Assert.NotNull(Sample().Find(name));

    [Fact]
    public void ReadsJsonl()
    {
        var line = JsonSerializer.Serialize(new CardData { Name = "Dark Ritual", ManaCost = "{B}", Cmc = 1 });
        var index = CardIndex.FromJsonl(new MemoryStream(Encoding.UTF8.GetBytes(line + "\n\n")));
        Assert.Equal(1, index.Count);
        Assert.Equal("{B}", index.Find("dark ritual")!.ManaCost);
        Assert.Contains("\"mana_cost\"", line);
    }

    [Fact]
    public void ConvertsScryfallTransformCard()
    {
        using var doc = JsonDocument.Parse("""
            {"name":"Delver of Secrets // Insectile Aberration","layout":"transform","cmc":1,
             "type_line":"Creature — Human Wizard // Creature — Human Insect","color_identity":["U"],
             "legalities":{"commander":"legal"},
             "card_faces":[{"name":"Delver of Secrets","mana_cost":"{U}","type_line":"Creature — Human Wizard","oracle_text":"x","power":"1","toughness":"1"},
                           {"name":"Insectile Aberration","mana_cost":"","type_line":"Creature — Human Insect","oracle_text":"Flying","power":"3","toughness":"2"}]}
            """);
        var c = ScryfallSync.Convert(doc.RootElement);
        Assert.Equal("{U}", c.ManaCost);
        Assert.Equal("1", c.Power);
        Assert.Equal("legal", c.CommanderLegality);
        Assert.Equal(2, c.Faces!.Count);
    }

    [Fact]
    public void CheckReportsUnknownNames()
    {
        var deck = DeckReader.Parse("1 Sol Ring *CMDR*\n\n1 Not A Card\n");
        var r = DeckCheck.Run(deck, Sample());
        Assert.Equal(new[] { "Not A Card" }, r.UnknownNames);
        Assert.False(r.Ok);
    }
}
