using System.Text;
using System.Text.Json;

namespace MagicSim.Core.Sim;

/// <summary>One game from a magicsim.game/1 line, as much as the deck page needs.</summary>
public sealed record GameRow(string Table, string Mulligan, string Seat, int Game, bool Won, int? WinTurn, string? WinLine,
    string? FailReason, int Mulligans, List<string> HandKept, List<string> Bottomed, List<string> Cast, List<int>? CastTurns)
{
    /// <summary>The base deck's games from per-game JSONL lines (compare variants are left out).</summary>
    public static List<GameRow> Read(IEnumerable<string> lines)
    {
        var rows = new List<GameRow>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var e = JsonDocument.Parse(line).RootElement;
            if (e.TryGetProperty("variant", out var v) && v.GetString() != "base") continue;
            static List<string> Strings(JsonElement e, string key) =>
                e.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new();
            int? Int(string key) => e.TryGetProperty(key, out var x) && x.ValueKind == JsonValueKind.Number ? x.GetInt32() : null;
            string? Str(string key) => e.TryGetProperty(key, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
            rows.Add(new GameRow(Str("table") ?? "1v1v1", Str("mulligan") ?? "?", Str("seat") ?? "?", Int("game") ?? 0,
                e.GetProperty("won").GetBoolean(), Int("win_turn"), Str("win_line"), Str("fail_reason"), Int("mulligans") ?? 0,
                Strings(e, "hand_kept"), Strings(e, "bottomed"), Strings(e, "cast"),
                e.TryGetProperty("cast_turns", out var ct) && ct.ValueKind == JsonValueKind.Array ? ct.EnumerateArray().Select(x => x.GetInt32()).ToList() : null));
        }
        return rows;
    }
}

public static partial class HtmlReport
{
    private const int GamesShown = 8;

    /// <summary>
    /// One deck's page: its curves and summary, its fastest and worst games with the hands kept and the plays made, which
    /// opening-hand cards go with faster or slower games, and its Forge playtest. The game sections need its per-game rows.
    /// </summary>
    public static string BuildDeck(JsonElement run, IReadOnlyList<GameRow> games, JsonElement? forge, DateTime generated, string? indexHref)
    {
        var deck = run.GetProperty("deck");
        var name = deck.GetProperty("name").GetString() ?? "?";
        var settings = run.GetProperty("settings");
        var maxTurn = settings.GetProperty("max_turn").GetInt32();
        var seed = settings.TryGetProperty("seed", out var sd) ? sd.ToString() : "1";
        var deckPath = deck.TryGetProperty("path", out var p) ? p.GetString() : null;

        var h = new StringBuilder();
        Head(h, name);
        h.Append("<body><main>");
        if (indexHref is not null) h.Append($"<p><a href=\"{Esc(indexHref)}\">← All decks</a></p>");
        h.Append($"<h1>{Esc(name)}</h1><p class=\"muted\">");
        var commanders = deck.GetProperty("commanders").EnumerateArray().Select(c => c.GetString() ?? "").ToList();
        if (commanders.Count > 0) h.Append($"Commander: {Esc(string.Join(" + ", commanders))}. ");
        h.Append($"Goldfish games against opponents that never interact. Generated {Esc(generated.ToString("yyyy-MM-dd HH:mm", Inv))}. " +
                 $"{Esc(SettingsText(new List<JsonElement> { settings }))}</p>");
        var unsupported = deck.GetProperty("unsupported_cards").EnumerateArray().Select(c => c.GetString() ?? "").ToList();
        if (unsupported.Count > 0)
            h.Append($"<p class=\"warn\">Played as blanks (not supported yet): {Esc(string.Join(", ", unsupported))}</p>");

        var series = Read(run).ToList();
        if (series.Count > 0)
        {
            var labels = series.Select(s => s.Label).Distinct().ToList();
            var color = labels.Select((l, i) => (l, i)).ToDictionary(x => x.l, x => $"var(--c{x.i % 8})");
            Goldfish(h, series, labels, color, maxTurn);
        }

        if (games.Count == 0)
            h.Append("<p class=\"muted\">No per-game results were found for this deck, so the hands and plays aren't shown. " +
                     "Run it with <code>--per-game</code> (or use <code>sim batch</code>) to get them.</p>");
        else
        {
            var mulls = games.Select(g => g.Mulligan).Distinct().Count();
            string Replay(GameRow g) =>
                $"sim replay \"{deckPath ?? name + ".txt"}\" --game {g.Game} --seed {seed} --mull {g.Mulligan} --seat {g.Seat} --table {g.Table} --max-turn {maxTurn}";
            FastestGames(h, games, Replay, mulls > 1);
            WorstGames(h, games, Replay, mulls > 1, maxTurn);
            OpeningHandCards(h, games, maxTurn);
            MulliganOutcomes(h, games, maxTurn);
        }
        CardTable(h, run);
        if (forge is { } f) Forge(h, new[] { f });
        h.Append("</main></body></html>\n");
        return h.ToString();
    }

    private static IEnumerable<IGrouping<string, GameRow>> ByTable(IEnumerable<GameRow> games) =>
        games.GroupBy(g => g.Table).OrderBy(t => Array.IndexOf(Tables, t.Key));

    private static void FastestGames(StringBuilder h, IReadOnlyList<GameRow> games, Func<GameRow, string> replay, bool showMull)
    {
        h.Append("<h2>Fastest wins</h2><p class=\"muted\">The quickest games at each table size: the hand kept (cards put on the " +
                 "bottom are struck through) and what was played each turn. Each one can be watched move by move with its replay command.</p>");
        foreach (var table in ByTable(games))
        {
            var fastest = table.Where(g => g.Won).OrderBy(g => g.WinTurn).ThenBy(g => g.Mulligans).ThenBy(g => g.Game).Take(GamesShown).ToList();
            if (fastest.Count == 0) continue;
            GameTable(h, $"{table.Key}", fastest, replay, showMull, g => $"won T{g.WinTurn}", g => g.WinLine ?? "");
        }
    }

    private static void WorstGames(StringBuilder h, IReadOnlyList<GameRow> games, Func<GameRow, string> replay, bool showMull, int maxTurn)
    {
        h.Append($"<h2>Worst games</h2><p class=\"muted\">Games with no win by turn {maxTurn}, then the slowest wins, at each table size.</p>");
        foreach (var table in ByTable(games))
        {
            var worst = table.Where(g => !g.Won).OrderByDescending(g => g.Mulligans).ThenBy(g => g.Game)
                .Concat(table.Where(g => g.Won).OrderByDescending(g => g.WinTurn).ThenByDescending(g => g.Mulligans).ThenBy(g => g.Game))
                .Take(GamesShown).ToList();
            if (worst.Count == 0) continue;
            GameTable(h, table.Key, worst, replay, showMull, g => g.Won ? $"won T{g.WinTurn}" : "no win",
                g => g.Won ? g.WinLine ?? "" : FailText(g.FailReason));
        }
    }

    private static string FailText(string? reason) => reason switch
    {
        "no_mana" => "never had 3 mana",
        "unsupported_dead_cards" => "stuck with unsupported cards",
        null => "",
        _ => "ran out of time",
    };

    private static void GameTable(StringBuilder h, string title, List<GameRow> rows, Func<GameRow, string> replay, bool showMull,
        Func<GameRow, string> outcome, Func<GameRow, string> how)
    {
        h.Append($"<div class=\"card tablewrap\" style=\"margin-bottom:16px\"><h3>{Esc(title)}</h3><table><thead><tr><th>Result</th><th>Seat</th>" +
                 (showMull ? "<th>Policy</th>" : "") +
                 "<th>Mulligans</th><th style=\"text-align:left\">Hand kept</th><th style=\"text-align:left\">Plays</th><th>How</th></tr></thead><tbody>");
        foreach (var g in rows)
        {
            h.Append($"<tr><td>{Esc(outcome(g))}</td><td>{g.Seat}</td>" + (showMull ? $"<td>{g.Mulligan}</td>" : "") + $"<td>{g.Mulligans}</td>");
            h.Append("<td class=\"wrap\"><div class=\"chips\">" + string.Concat(g.HandKept.Select(c => $"<span class=\"chip\">{Esc(c)}</span>")) +
                     string.Concat(g.Bottomed.Select(c => $"<span class=\"chip out\" title=\"put on the bottom\">{Esc(c)}</span>")) + "</div></td>");
            h.Append($"<td class=\"play\">{Plays(g)}<br><code>{Esc(replay(g))}</code></td><td class=\"wrap\">{Esc(how(g))}</td></tr>");
        }
        h.Append("</tbody></table></div>");
    }

    /// <summary>"T1 Swamp, Dark Ritual · T2 …" when the cast turns were recorded, else the cards in order.</summary>
    private static string Plays(GameRow g)
    {
        if (g.Cast.Count == 0) return "<span class=\"muted\">nothing cast</span>";
        if (g.CastTurns is not { } turns || turns.Count != g.Cast.Count) return Esc(string.Join(", ", g.Cast));
        return string.Join(" · ", g.Cast.Zip(turns).GroupBy(x => x.Second)
            .Select(t => $"<b>T{t.Key}</b> {Esc(string.Join(", ", t.Select(x => x.First)))}"));
    }

    /// <summary>For each card kept in opening hands: the capped mean win turn with it and without it, per table size.</summary>
    private static void OpeningHandCards(StringBuilder h, IReadOnlyList<GameRow> games, int maxTurn)
    {
        h.Append($"<h2>Opening hand cards</h2><p class=\"muted\">How fast games went when a card was in the hand kept versus when it " +
                 $"wasn't (capped mean win turn, where no win counts as turn {maxTurn + 1}; lower is faster). Seats and mulligan policies " +
                 "are pooled. This shows association, not cause: a card can look good because the hands that keep it are good.</p><div class=\"grid2\">");
        foreach (var table in ByTable(games))
        {
            var all = table.ToList();
            double Capped(GameRow g) => g.WinTurn ?? maxTurn + 1;
            var minGames = Math.Max(10, all.Count / 50);
            var total = all.Sum(Capped);
            var stats = all.SelectMany(g => g.HandKept.Distinct().Select(c => (Card: c, Game: g)))
                .GroupBy(x => x.Card)
                .Select(grp =>
                {
                    var n = grp.Count();
                    var sum = grp.Sum(x => Capped(x.Game));
                    return (Card: grp.Key, N: n, With: sum / n, Without: n == all.Count ? double.NaN : (total - sum) / (all.Count - n));
                })
                .Where(s => s.N >= minGames && !double.IsNaN(s.Without))
                .Select(s => (s.Card, s.N, s.With, s.Without, Delta: s.With - s.Without))
                .ToList();
            if (stats.Count == 0) continue;
            h.Append($"<div class=\"card tablewrap\"><h3>{table.Key}</h3><table><thead><tr><th>Card</th><th></th><th>Hands</th>" +
                     "<th>With</th><th>Without</th><th>Δ turns</th></tr></thead><tbody>");
            void Rows(IEnumerable<(string Card, int N, double With, double Without, double Delta)> rows, string cls)
            {
                foreach (var s in rows)
                    h.Append($"<tr><td>{Esc(s.Card)}</td><td></td><td>{s.N}</td><td>{s.With.ToString("0.00", Inv)}</td><td>{s.Without.ToString("0.00", Inv)}</td>" +
                             $"<td class=\"{cls}\">{s.Delta.ToString("+0.00;-0.00;0.00", Inv)}</td></tr>");
            }
            var best = stats.OrderBy(s => s.Delta).Take(10).ToList();
            Rows(best, "good");
            var worst = stats.OrderByDescending(s => s.Delta).Take(10).Where(s => !best.Contains(s)).Reverse().ToList();
            if (worst.Count > 0)
            {
                h.Append("<tr><td colspan=\"6\" class=\"muted\">…</td></tr>");
                Rows(worst, "warn");
            }
            h.Append($"</tbody></table><p class=\"muted\">Cards in at least {minGames} kept hands.</p></div>");
        }
        h.Append("</div>");
    }

    private static void MulliganOutcomes(StringBuilder h, IReadOnlyList<GameRow> games, int maxTurn)
    {
        h.Append("<h2>Mulligans</h2><div class=\"card tablewrap\"><table><thead><tr><th>Table</th><th>Mulligans</th><th>Games</th><th>Share</th>" +
                 $"<th>Won by T{maxTurn}</th><th>Capped mean</th></tr></thead><tbody>");
        foreach (var table in ByTable(games))
        {
            var n = table.Count();
            foreach (var grp in table.GroupBy(g => Math.Min(g.Mulligans, 3)).OrderBy(g => g.Key))
                h.Append($"<tr><td>{table.Key}</td><td>{(grp.Key == 3 ? "3+" : grp.Key.ToString())}</td><td>{grp.Count()}</td>" +
                         $"<td>{Pct(grp.Count() / (double)n)}</td><td>{Pct(grp.Count(g => g.Won) / (double)grp.Count())}</td>" +
                         $"<td>{grp.Average(g => g.WinTurn ?? maxTurn + 1).ToString("0.00", Inv)}</td></tr>");
        }
        h.Append("</tbody></table><p class=\"muted\">The first mulligan is free.</p></div>");
    }

    private static void CardTable(StringBuilder h, JsonElement run)
    {
        if (!run.TryGetProperty("cards", out var cards) || cards.GetArrayLength() == 0) return;
        h.Append("<h2>Cards</h2><div class=\"card tablewrap\"><table><thead><tr><th>Card</th><th></th><th>Drawn</th><th>Cast</th>" +
                 "<th>Cast in a win</th></tr></thead><tbody>");
        foreach (var c in cards.EnumerateArray().OrderByDescending(c => c.GetProperty("in_win").GetDouble()).ThenByDescending(c => c.GetProperty("cast").GetDouble()))
            h.Append($"<tr><td>{Esc(c.GetProperty("name").GetString() ?? "?")}</td><td></td><td>{Pct(c.GetProperty("drawn").GetDouble())}</td>" +
                     $"<td>{Pct(c.GetProperty("cast").GetDouble())}</td><td>{Pct(c.GetProperty("in_win").GetDouble())}</td></tr>");
        h.Append("</tbody></table><p class=\"muted\">Shares of all games, both table sizes together. Drawn means seen in hand.</p></div>");
    }
}
