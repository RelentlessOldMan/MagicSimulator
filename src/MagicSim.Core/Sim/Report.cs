using System.Text.Json;
using System.Text.Json.Serialization;

namespace MagicSim.Core.Sim;

/// <summary>Builds the magicsim.run/1 and magicsim.game/1 documents (CONTRACT.md §3).</summary>
public static class Report
{
    public const string SimVersion = "0.1.0";
    /// <summary>Your share of being first: 1/2 at a 2-player table, 1/3 at a 3-player one.</summary>
    public static double PlayWeight(string table) => 1.0 / (Runner.OpponentsAt(table) + 1);

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly JsonSerializerOptions JsonLine = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <param name="tableSeconds">Each table size's wall-clock time, for the <c>timing</c> block; left out without it.</param>
    public static object RunDocument(Runner.PreparedDeck deck, string deckPath, RunOptions o, IReadOnlyList<GameResult> games,
        IReadOnlyDictionary<string, double>? tableSeconds = null)
    {
        var rows = ResultRows(o, games);
        return new
        {
            schema = "magicsim.run/1",
            sim_version = SimVersion,
            deck = new
            {
                name = deck.Name, path = deckPath, card_count = deck.CardCount,
                commanders = deck.Commanders.Select(c => c.Name).ToList(),
                unsupported_cards = deck.Unsupported,
            },
            settings = new { games = o.Games, seed = o.Seed, max_turn = o.MaxTurn, rollouts = o.Rollouts },
            results = rows,
            cards = CardStats(deck, games),
            timing = tableSeconds is null ? null : new
            {
                threads = o.Threads,
                elapsed_s = Math.Round(tableSeconds.Values.Sum(), 1),
                by_table_s = tableSeconds.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 1)),
            },
        };
    }

    /// <summary>The <c>results</c> rows: one per (table, mulligan, seat), plus weighted rows when both seats were played.</summary>
    public static List<Dictionary<string, object?>> ResultRows(RunOptions o, IReadOnlyList<GameResult> games)
    {
        var rows = new List<Dictionary<string, object?>>();
        foreach (var table in games.GroupBy(g => g.Table))
        {
            foreach (var grp in table.GroupBy(g => (g.Mulligan, g.Seat)))
                rows.Add(Row(table.Key, grp.Key.Mulligan, grp.Key.Seat, grp.Select(g => (g, 1.0)).ToList(), o.MaxTurn));
            if (!o.Seats.Contains("play") || !o.Seats.Contains("draw")) continue;
            var w = PlayWeight(table.Key);
            foreach (var grp in table.GroupBy(g => g.Mulligan))
            {
                var play = grp.Where(g => g.Seat == "play").ToList();
                var draw = grp.Where(g => g.Seat == "draw").ToList();
                var weighted = play.Select(g => (g, w / play.Count))
                    .Concat(draw.Select(g => (g, (1 - w) / draw.Count))).ToList();
                rows.Add(Row(table.Key, grp.Key, "weighted", weighted, o.MaxTurn));
            }
        }
        return rows;
    }

    /// <summary>One results row from weighted games (weights sum to 1 for weighted rows, else 1 each).</summary>
    private static Dictionary<string, object?> Row(string table, string mulligan, string seat, List<(GameResult G, double W)> games, int maxTurn)
    {
        var totalW = games.Sum(x => x.W);
        double Frac(Func<GameResult, bool> f) => Math.Round(games.Where(x => f(x.G)).Sum(x => x.W) / totalW, 4);

        var winByTurn = new SortedDictionary<int, double>();
        var cumulative = new SortedDictionary<int, double>();
        double cum = 0;
        for (var t = 1; t <= maxTurn; t++)
        {
            var f = Frac(g => g.Won && g.WinTurn == t);
            if (f > 0) winByTurn[t] = f;
            cum += f;
            cumulative[t] = Math.Round(cum, 4);
        }
        var wins = games.Where(x => x.G.Won).ToList();
        var (mean, ci) = MeanCi(wins.Select(x => ((double)x.G.WinTurn!.Value, x.W)).ToList());
        var (capped, cappedCi) = MeanCi(games.Select(x => ((double)(x.G.WinTurn ?? maxTurn + 1), x.W)).ToList());
        var lines = wins.GroupBy(x => x.G.WinLine ?? "?")
            .OrderByDescending(gr => gr.Sum(x => x.W))
            .ToDictionary(gr => gr.Key, gr => Math.Round(gr.Sum(x => x.W) / Math.Max(1e-12, wins.Sum(x => x.W)), 4));
        var kept = games.GroupBy(x => x.G.Kept).OrderByDescending(gr => gr.Key)
            .ToDictionary(gr => gr.Key.ToString(), gr => Math.Round(gr.Sum(x => x.W) / totalW, 4));

        return new Dictionary<string, object?>
        {
            ["table"] = table,
            ["mulligan"] = mulligan,
            ["seat"] = seat,
            ["games"] = seat == "weighted" ? games.Count : (int)totalW,
            ["win_by_turn"] = winByTurn.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            ["cumulative_by_turn"] = cumulative.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            ["no_win"] = Frac(g => !g.Won),
            ["mean_turn"] = wins.Count == 0 ? null : Math.Round(mean, 3),
            ["mean_turn_ci95"] = wins.Count == 0 ? null : ci,
            ["median_turn"] = wins.Count == 0 ? null : WeightedMedian(wins.Select(x => ((double)x.G.WinTurn!.Value, x.W)).ToList()),
            ["mean_turn_capped"] = Math.Round(capped, 3),
            ["mean_turn_capped_ci95"] = cappedCi,
            ["hand_size_kept"] = kept,
            ["win_lines"] = lines,
        };
    }

    /// <summary>Weighted mean with a normal-approximation 95% interval (Kish effective sample size).</summary>
    public static (double Mean, double[] Ci) MeanCi(List<(double V, double W)> xs)
    {
        if (xs.Count == 0) return (0, new[] { 0.0, 0.0 });
        var w = xs.Sum(x => x.W);
        var mean = xs.Sum(x => x.V * x.W) / w;
        var variance = xs.Sum(x => x.W * (x.V - mean) * (x.V - mean)) / w;
        var nEff = w * w / xs.Sum(x => x.W * x.W);
        var half = nEff > 1 ? 1.96 * Math.Sqrt(variance * nEff / (nEff - 1) / nEff) : 0;
        return (mean, new[] { Math.Round(mean - half, 3), Math.Round(mean + half, 3) });
    }

    private static double WeightedMedian(List<(double V, double W)> xs)
    {
        var sorted = xs.OrderBy(x => x.V).ToList();
        var half = sorted.Sum(x => x.W) / 2;
        double acc = 0;
        foreach (var (v, w) in sorted)
        {
            acc += w;
            if (acc >= half) return v;
        }
        return sorted[^1].V;
    }

    private static List<object> CardStats(Runner.PreparedDeck deck, IReadOnlyList<GameResult> games)
    {
        var names = deck.Commanders.Select(c => c.Name).Concat(deck.Library.Select(c => c.Name)).Distinct(StringComparer.Ordinal);
        var n = Math.Max(1, games.Count);
        return names.Select(name => (object)new
        {
            name,
            drawn = Math.Round(games.Count(g => g.Seen.Contains(name)) / (double)n, 4),
            cast = Math.Round(games.Count(g => g.Cast.Contains(name)) / (double)n, 4),
            in_win = Math.Round(games.Count(g => g.Won && g.Cast.Contains(name)) / (double)n, 4),
        }).ToList();
    }

    public static string GameLine(GameResult g) => JsonSerializer.Serialize(new
    {
        schema = "magicsim.game/1",
        variant = g.Variant,
        game = g.Game,
        table = g.Table,
        mulligan = g.Mulligan,
        seat = g.Seat,
        mulligans = g.Mulligans,
        kept = g.Kept,
        hand_kept = g.HandKept,
        bottomed = g.Bottomed,
        won = g.Won,
        win_turn = g.WinTurn,
        win_line = g.WinLine,
        fail_reason = g.FailReason,
        life_end = new { you = g.LifeEnd, opponents = g.OpponentsLifeEnd },
        turns_played = g.TurnsPlayed,
        mana_by_turn = g.ManaByTurn,
        life_paid = g.LifePaid,
        storm_max = g.StormMax,
        notes = g.Notes,
        seen = g.Seen,
        cast = g.Cast,
        win_cards = g.Won ? g.WinCards : null,
    }, JsonLine);
}
