using System.Text;

namespace MagicSim.Core.Decks;

/// <summary>Writes the canonical MagicDecks .txt form (CONTRACT.md §1).</summary>
public static class DeckWriter
{
    public static string Format(Deck deck)
    {
        var sb = new StringBuilder();
        foreach (var e in deck.Commanders)
            sb.Append(e.Count).Append(' ').Append(e.Name).Append(' ').Append(DeckReader.CommanderTag).Append('\n');
        if (deck.Commanders.Count > 0)
            sb.Append('\n');
        foreach (var e in deck.Mainboard)
            sb.Append(e.Count).Append(' ').Append(e.Name).Append('\n');
        return sb.ToString();
    }

    /// <summary>UTF-8 without BOM, LF line ends, trailing newline.</summary>
    public static void WriteFile(Deck deck, string path) =>
        File.WriteAllText(path, Format(deck), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
}
