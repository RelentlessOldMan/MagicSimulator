namespace MagicSim.Core.Decks;

/// <summary>One line of a decklist: a count and a card name as written.</summary>
public sealed record DeckEntry(int Count, string Name);

/// <summary>A Commander deck: tagged commanders plus the rest of the list.</summary>
public sealed class Deck
{
    public string Name { get; init; } = "";
    public List<DeckEntry> Commanders { get; } = new();
    public List<DeckEntry> Mainboard { get; } = new();

    /// <summary>Non-fatal problems found while reading (unparseable lines, etc.).</summary>
    public List<string> Warnings { get; } = new();

    public int CommanderCount => Commanders.Sum(e => e.Count);
    public int MainboardCount => Mainboard.Sum(e => e.Count);
    public int TotalCount => CommanderCount + MainboardCount;

    /// <summary>Mainboard expanded to one name per physical card.</summary>
    public IEnumerable<string> MainboardCards() =>
        Mainboard.SelectMany(e => Enumerable.Repeat(e.Name, e.Count));
}
