namespace MagicSim.ForgeWrapper;

/// <summary>Builds the magicsim.forge/1 document (CONTRACT.md §3, "Forge playtest").</summary>
public static class GauntletReport
{
    public const string Schema = "magicsim.forge/1";

    public static object Document(GauntletResult r)
    {
        var all = r.Opponents.SelectMany(o => o.Games).ToList();
        return new
        {
            schema = Schema,
            deck = new { name = r.DeckName, path = r.DeckPath.Replace('\\', '/'), unknown_cards = r.UnknownCards },
            forge = new { version = r.ForgeVersion, format = "Commander", players = 2 },
            settings = new { games = r.Options.Games, timeout_s = r.Options.TimeoutSeconds },
            totals = Counts(all),
            opponents = r.Opponents.Select(o => new
            {
                name = o.Name,
                path = o.Path.Replace('\\', '/'),
                unknown_cards = o.UnknownCards,
                games = o.Games.Count,
                wins = o.Games.Count(g => g.Winner == 1),
                losses = o.Games.Count(g => g.Winner is > 1),
                draws = o.Games.Count(g => g.Winner is null),
                timeouts = o.Games.Count(g => g.TimedOut),
                win_rate = Rate(o.Games),
                win_rate_ci95 = Wilson(o.Games.Count(g => g.Winner == 1), o.Games.Count),
                mean_win_round = MeanRound(o.Games.Where(g => g.Winner == 1)),
                mean_loss_round = MeanRound(o.Games.Where(g => g.Winner is > 1)),
                mean_game_ms = o.Games.Count == 0 ? (double?)null : Math.Round(o.Games.Average(g => g.Ms)),
                error = o.Error,
            }).ToList(),
            elapsed_s = Math.Round(r.ElapsedSeconds, 1),
        };
    }

    private static object Counts(List<ForgeGame> games) => new
    {
        games = games.Count,
        wins = games.Count(g => g.Winner == 1),
        losses = games.Count(g => g.Winner is > 1),
        draws = games.Count(g => g.Winner is null),
        win_rate = Rate(games),
        win_rate_ci95 = Wilson(games.Count(g => g.Winner == 1), games.Count),
    };

    private static double? Rate(List<ForgeGame> games) =>
        games.Count == 0 ? null : Math.Round(games.Count(g => g.Winner == 1) / (double)games.Count, 4);

    private static double? MeanRound(IEnumerable<ForgeGame> games)
    {
        var rounds = games.Select(Gauntlet.Round).OfType<int>().ToList();
        return rounds.Count == 0 ? null : Math.Round(rounds.Average(), 2);
    }

    /// <summary>Wilson 95% interval for a win rate; null with no games.</summary>
    public static double[]? Wilson(int wins, int games)
    {
        if (games == 0) return null;
        const double z = 1.959964;
        var p = wins / (double)games;
        var denom = 1 + z * z / games;
        var centre = (p + z * z / (2 * games)) / denom;
        var half = z * Math.Sqrt(p * (1 - p) / games + z * z / (4.0 * games * games)) / denom;
        return new[] { Math.Round(Math.Max(0, centre - half), 4), Math.Round(Math.Min(1, centre + half), 4) };
    }
}
