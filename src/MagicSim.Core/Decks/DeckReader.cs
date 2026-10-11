using System.Text.RegularExpressions;

namespace MagicSim.Core.Decks;

/// <summary>
/// Reads the shared MagicDecks .txt format (see CONTRACT.md §1). Lenient on input:
/// accepts set codes, "Nx" counts, comments, blank lines and untagged (Moxfield) lists.
/// </summary>
public static partial class DeckReader
{
    public const string CommanderTag = "*CMDR*";

    // "1 Name", "1x Name", optional " *CMDR*" at the end.
    [GeneratedRegex(@"^(\d+)x?\s+(.+?)$")]
    private static partial Regex LineRx();

    // Trailing " (SET) 123" / " (SET)" / " [SET]" plus Moxfield markers like *F* / *E*.
    [GeneratedRegex(@"\s+[\(\[][A-Za-z0-9]{2,8}[\)\]](\s+[A-Za-z0-9\-★]+)?$")]
    private static partial Regex SetSuffixRx();

    [GeneratedRegex(@"\s+\*[A-Z]\*$")]
    private static partial Regex MarkerRx();

    public static Deck ReadFile(string path) =>
        Parse(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));

    public static Deck Parse(string text, string name = "")
    {
        var deck = new Deck { Name = name };
        var lineNo = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            lineNo++;
            var line = rawLine.Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//"))
                continue;

            var isCommander = false;
            if (line.EndsWith(CommanderTag, StringComparison.Ordinal))
            {
                isCommander = true;
                line = line[..^CommanderTag.Length].TrimEnd();
            }

            var m = LineRx().Match(line);
            if (!m.Success)
            {
                deck.Warnings.Add($"line {lineNo}: not a \"N Cardname\" line: {rawLine.Trim()}");
                continue;
            }

            var count = int.Parse(m.Groups[1].Value);
            var cardName = m.Groups[2].Value;
            // Strip markers and set codes repeatedly; they can appear in either order.
            string prev;
            do
            {
                prev = cardName;
                cardName = MarkerRx().Replace(cardName, "");
                cardName = SetSuffixRx().Replace(cardName, "");
            } while (cardName != prev);
            cardName = cardName.Trim();

            if (count <= 0 || cardName.Length == 0)
            {
                deck.Warnings.Add($"line {lineNo}: empty count or name: {rawLine.Trim()}");
                continue;
            }

            Add(isCommander ? deck.Commanders : deck.Mainboard, count, cardName);
        }

        if (deck.Commanders.Count == 0)
            deck.Warnings.Add("no commander tagged with *CMDR*");
        return deck;
    }

    /// <summary>Merge duplicates so "1 Island" twice becomes "2 Island".</summary>
    private static void Add(List<DeckEntry> list, int count, string name)
    {
        var i = list.FindIndex(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) list[i] = list[i] with { Count = list[i].Count + count };
        else list.Add(new DeckEntry(count, name));
    }
}
