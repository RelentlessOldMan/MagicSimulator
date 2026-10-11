using MagicSim.Core.Cards;

namespace MagicSim.Core.Decks;

/// <summary>Resolves a deck's names against the card index and checks the 100-card count.</summary>
public sealed record DeckCheckResult(
    string Deck,
    int CardCount,
    IReadOnlyList<string> Commanders,
    IReadOnlyList<string> UnknownNames,
    IReadOnlyList<string> Warnings)
{
    public bool Ok => CardCount == 100 && UnknownNames.Count == 0 && Commanders.Count > 0;
}

public static class DeckCheck
{
    public static DeckCheckResult Run(Deck deck, CardIndex index)
    {
        var unknown = deck.Commanders.Concat(deck.Mainboard)
            .Where(e => index.Find(e.Name) is null)
            .Select(e => e.Name)
            .ToList();
        var warnings = new List<string>(deck.Warnings);
        if (deck.TotalCount != 100)
            warnings.Add($"deck has {deck.TotalCount} cards, expected 100");
        return new DeckCheckResult(deck.Name, deck.TotalCount,
            deck.Commanders.Select(c => c.Name).ToList(), unknown, warnings);
    }
}
