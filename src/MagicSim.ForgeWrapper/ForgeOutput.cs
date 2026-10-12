using System.Text.RegularExpressions;
using MagicSim.Core.Decks;

namespace MagicSim.ForgeWrapper;

/// <summary>One game from Forge's sim output. <see cref="Winner"/> is the 1-based player (the order of -d), null for a draw.</summary>
public sealed record ForgeGame(int Game, int? Winner, int? Turn, long Ms, bool TimedOut);

/// <summary>Reads the quiet (-q) output of `forge sim`, line by line as it arrives.</summary>
public sealed partial class ForgeOutputParser
{
    // Game Result: Game 3 ended in 9669 ms. Ai(1)-Gary has won!
    [GeneratedRegex(@"^Game Result: Game (\d+) ended in (\d+) ms\. Ai\((\d+)\)-.* has won!")]
    private static partial Regex WonRx();

    // Game Result: Game 3 ended in a Draw! Took 120000 ms.
    [GeneratedRegex(@"^Game Result: Game (\d+) ended in a Draw! Took (\d+) ms\.")]
    private static partial Regex DrawRx();

    [GeneratedRegex(@"^Game Outcome: Turn (\d+)$")]
    private static partial Regex TurnRx();

    private int? _turn;
    private bool _timedOut;

    /// <summary>Set when Forge refused a deck ("Could not load deck - x, match cannot start").</summary>
    public string? LoadError { get; private set; }

    /// <summary>The finished game, when this line ends one.</summary>
    public ForgeGame? Read(string line)
    {
        line = line.Trim();
        if (line.StartsWith("Could not load deck", StringComparison.Ordinal)) LoadError = line;
        else if (line == "Stopping slow match as draw") _timedOut = true;
        else if (TurnRx().Match(line) is { Success: true } t) _turn = int.Parse(t.Groups[1].Value);
        else if (WonRx().Match(line) is { Success: true } w) return End(w, int.Parse(w.Groups[3].Value));
        else if (DrawRx().Match(line) is { Success: true } d) return End(d, null);
        return null;
    }

    private ForgeGame End(Match m, int? winner)
    {
        var game = new ForgeGame(int.Parse(m.Groups[1].Value), winner, _turn, long.Parse(m.Groups[2].Value), _timedOut);
        (_turn, _timedOut) = (null, false);
        return game;
    }

    public static List<ForgeGame> Parse(IEnumerable<string> lines)
    {
        var parser = new ForgeOutputParser();
        return lines.Select(parser.Read).OfType<ForgeGame>().ToList();
    }
}

/// <summary>Writes a deck in Forge's .dck format.</summary>
public static class DckWriter
{
    public static string Write(Deck deck)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("[metadata]\nName=").Append(deck.Name).Append("\n[Commander]\n");
        foreach (var e in deck.Commanders) sb.Append(e.Count).Append(' ').Append(e.Name).Append('\n');
        sb.Append("[Main]\n");
        foreach (var e in deck.Mainboard) sb.Append(e.Count).Append(' ').Append(e.Name).Append('\n');
        return sb.ToString();
    }
}
