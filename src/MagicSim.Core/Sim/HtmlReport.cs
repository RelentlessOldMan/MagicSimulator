using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace MagicSim.Core.Sim;

/// <summary>
/// A self-contained HTML page from one or more magicsim.run/1 documents: cumulative win curves per table size,
/// a per-turn histogram per deck, and a summary table, plus each deck's Forge playtest (magicsim.forge/1) when one
/// was run. For people to read; not a stable format.
/// </summary>
public static class HtmlReport
{
    private sealed record Series(string Deck, string Label, string Table, int Games, int MaxTurn, double[] ByTurn, double[] Cumulative,
        double NoWin, double? Median, double? Mean, double Capped, double[] CappedCi, int Unsupported);

    private static readonly string[] Tables = { "1v1", "1v1v1" };
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Build(IReadOnlyList<JsonElement> runs, DateTime generated, IReadOnlyList<JsonElement>? forge = null)
    {
        forge ??= Array.Empty<JsonElement>();
        var series = runs.SelectMany(Read).ToList();
        if (series.Count == 0 && forge.Count == 0) throw new InvalidDataException("no result rows in the run files");
        var labels = series.Select(s => s.Label).Distinct().ToList();
        var color = labels.Select((l, i) => (l, i)).ToDictionary(x => x.l, x => $"var(--c{x.i % 8})");
        var maxTurn = series.Count == 0 ? 0 : series.Max(s => s.MaxTurn);
        var settings = runs.Select(r => r.GetProperty("settings")).ToList();

        var h = new StringBuilder();
        h.Append($$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>MagicSim Report</title>
            <style>
            :root {
              --bg: #fbfaf7; --fg: #1d1c1a; --muted: #6b6760; --grid: #e4e0d8; --card: #ffffff; --border: #ddd8ce;
              --c0: #2f6fdf; --c1: #d9480f; --c2: #2b8a3e; --c3: #ae3ec9; --c4: #e8a400; --c5: #0c8599; --c6: #c2255c; --c7: #5c5f66;
            }
            @media (prefers-color-scheme: dark) {
              :root:not([data-theme="light"]) {
                --bg: #161615; --fg: #ecebe8; --muted: #9c988f; --grid: #2e2d2a; --card: #1f1e1c; --border: #34322e;
                --c0: #6ea0ff; --c1: #ff8a4c; --c2: #63c174; --c3: #d58cf0; --c4: #ffc94a; --c5: #4fc6d8; --c6: #f27aa6; --c7: #a5a9b0;
              }
            }
            :root[data-theme="dark"] {
              --bg: #161615; --fg: #ecebe8; --muted: #9c988f; --grid: #2e2d2a; --card: #1f1e1c; --border: #34322e;
              --c0: #6ea0ff; --c1: #ff8a4c; --c2: #63c174; --c3: #d58cf0; --c4: #ffc94a; --c5: #4fc6d8; --c6: #f27aa6; --c7: #a5a9b0;
            }
            * { box-sizing: border-box; }
            body { margin: 0; background: var(--bg); color: var(--fg); font: 15px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif; }
            main { max-width: 1100px; margin: 0 auto; padding: 24px 16px 48px; }
            h1 { font-size: 1.6rem; margin: 0 0 4px; }
            h2 { font-size: 1.15rem; margin: 32px 0 12px; }
            h3 { font-size: 1rem; margin: 0 0 8px; }
            .muted { color: var(--muted); }
            .grid2 { display: grid; grid-template-columns: repeat(auto-fit, minmax(320px, 1fr)); gap: 16px; }
            .card { background: var(--card); border: 1px solid var(--border); border-radius: 10px; padding: 16px; min-width: 0; }
            svg { width: 100%; height: auto; display: block; }
            svg text { fill: var(--muted); font-size: 12px; }
            .hist svg text { font-size: 17px; }
            .legend { display: flex; flex-wrap: wrap; gap: 6px 16px; margin-top: 8px; font-size: 13px; }
            .sw { display: inline-block; width: 12px; height: 12px; border-radius: 3px; margin-right: 6px; vertical-align: -1px; }
            .tablewrap { overflow-x: auto; }
            table { border-collapse: collapse; width: 100%; font-size: 14px; font-variant-numeric: tabular-nums; }
            th, td { text-align: right; padding: 6px 10px; border-bottom: 1px solid var(--border); white-space: nowrap; }
            th:first-child, td:first-child, th:nth-child(2), td:nth-child(2) { text-align: left; }
            th { color: var(--muted); font-weight: 600; }
            .forge td:nth-child(2) { text-align: right; }
            .warn { color: var(--c1); }
            </style>
            </head>
            <body>
            <main>
            <h1>MagicSim report</h1>
            <p class="muted">Goldfish games against opponents that never interact. Win rates are weighted for play/draw at each table size
            (1/2 on the play at 1v1, 1/3 at 1v1v1). Generated {{Esc(generated.ToString("yyyy-MM-dd HH:mm", Inv))}}.
            {{Esc(SettingsText(settings))}}</p>
            """);
        if (series.Count > 0) Goldfish(h, series, labels, color, maxTurn);
        if (forge.Count > 0) Forge(h, forge);
        RunTimes(h, runs, forge);
        h.Append("</main></body></html>\n");
        return h.ToString();
    }

    private static void Goldfish(StringBuilder h, List<Series> series, List<string> labels, Dictionary<string, string> color, int maxTurn)
    {

        // Cumulative curves, one chart per table size.
        h.Append("<h2>Win rate by turn (cumulative)</h2><div class=\"grid2\">");
        foreach (var table in Tables)
        {
            var rows = series.Where(s => s.Table == table).ToList();
            if (rows.Count == 0) continue;
            h.Append($"<div class=\"card\"><h3>{table}</h3>");
            h.Append(LineChart(rows, color, maxTurn));
            h.Append("</div>");
        }
        h.Append("</div>");
        h.Append(Legend(labels, color));

        // Summary table.
        h.Append($"""
            <h2>Summary</h2>
            <div class="card tablewrap"><table>
            <thead><tr><th>Deck</th><th>Table</th><th>Games</th><th>Won by T{maxTurn}</th><th>Median win turn</th>
            <th>Mean win turn</th><th>Capped mean</th><th>Capped 95% CI</th><th>Unsupported</th></tr></thead><tbody>
            """);
        foreach (var label in labels)
            foreach (var s in series.Where(s => s.Label == label).OrderBy(s => Array.IndexOf(Tables, s.Table)))
                h.Append($"<tr><td><span class=\"sw\" style=\"background:{color[label]}\"></span>{Esc(label)}</td><td>{s.Table}</td>" +
                         $"<td>{s.Games:N0}</td><td>{Pct(1 - s.NoWin)}</td><td>{Num(s.Median)}</td><td>{Num(s.Mean)}</td>" +
                         $"<td>{Num(s.Capped)}</td><td>{Num(s.CappedCi[0])}–{Num(s.CappedCi[1])}</td><td>{s.Unsupported}</td></tr>");
        h.Append($"""
            </tbody></table>
            <p class="muted">Capped mean counts a game without a win as turn {maxTurn + 1}, so it ranks decks fairly; mean and median use won games only.</p>
            </div>
            """);

        // Per-deck histograms: wins on each turn, both table sizes side by side.
        h.Append("<h2>Wins on each turn</h2><div class=\"grid2 hist\">");
        foreach (var label in labels)
        {
            var rows = series.Where(s => s.Label == label).OrderBy(s => Array.IndexOf(Tables, s.Table)).ToList();
            h.Append($"<div class=\"card\"><h3>{Esc(label)}</h3>{BarChart(rows, maxTurn)}");
            h.Append("<div class=\"legend\">" + string.Join("", rows.Select((r, i) =>
                $"<span><span class=\"sw\" style=\"background:{TableColor(i)}\"></span>{r.Table}</span>")) + "</div></div>");
        }
        h.Append("</div>");
    }

    /// <summary>One card per Forge playtest: the deck against each opponent, with Forge's AI playing both.</summary>
    private static void Forge(StringBuilder h, IReadOnlyList<JsonElement> docs)
    {
        h.Append("""
            <h2>Forge playtest</h2>
            <p class="muted">Real 1v1 Commander games in Forge's simulator, with Forge's AI playing both decks. Forge's AI doesn't look
            ahead the way the goldfish search does, so read these as a rough check against real opponents, with their intervals.
            Rounds count each player's turn once, like the goldfish turns.</p>
            """);
        foreach (var doc in docs)
        {
            var deck = doc.GetProperty("deck");
            var totals = doc.GetProperty("totals");
            var settings = doc.GetProperty("settings");
            h.Append($"<div class=\"card tablewrap forge\"><h3>{Esc(deck.GetProperty("name").GetString() ?? "?")}</h3>");
            h.Append($"<p>Won {totals.GetProperty("wins").GetInt32()} of {totals.GetProperty("games").GetInt32()} " +
                     $"({Rate(totals)}, 95% CI {Ci(totals)}); {totals.GetProperty("draws").GetInt32()} draws. " +
                     $"<span class=\"muted\">Forge {Esc(doc.GetProperty("forge").GetProperty("version").GetString() ?? "?")}, " +
                     $"{settings.GetProperty("games").GetInt32()} games per opponent, a draw after {settings.GetProperty("timeout_s").GetInt32()}s, " +
                     $"took {doc.GetProperty("elapsed_s").GetDouble() / 60:0} min.</span></p>");
            var unknown = deck.GetProperty("unknown_cards");
            if (unknown.GetArrayLength() > 0)
                h.Append($"<p class=\"warn\">Forge doesn't know these cards, so they were left out: {Esc(string.Join(", ", unknown.EnumerateArray().Select(x => x.GetString())))}</p>");
            h.Append("""
                <table><thead><tr><th>Opponent</th><th>Games</th><th>Won</th><th>Lost</th><th>Draws</th><th>Win rate</th><th>95% CI</th>
                <th>Win round</th><th>Loss round</th><th>Note</th></tr></thead><tbody>
                """);
            foreach (var o in doc.GetProperty("opponents").EnumerateArray().OrderByDescending(o => NullableDouble(o, "win_rate") ?? -1))
            {
                var unknownCards = o.GetProperty("unknown_cards").GetArrayLength();
                var note = o.GetProperty("error").GetString() ?? (unknownCards switch { 0 => "", 1 => "1 card unknown to Forge", _ => $"{unknownCards} cards unknown to Forge" });
                h.Append($"<tr><td>{Esc(o.GetProperty("name").GetString() ?? "?")}</td><td>{o.GetProperty("games").GetInt32()}</td>" +
                         $"<td>{o.GetProperty("wins").GetInt32()}</td><td>{o.GetProperty("losses").GetInt32()}</td><td>{o.GetProperty("draws").GetInt32()}</td>" +
                         $"<td>{Rate(o)}</td><td>{Ci(o)}</td><td>{Num(NullableDouble(o, "mean_win_round"))}</td>" +
                         $"<td>{Num(NullableDouble(o, "mean_loss_round"))}</td><td class=\"muted\">{Esc(note)}</td></tr>");
            }
            h.Append("</tbody></table></div>");
        }
    }

    /// <summary>How long each deck's goldfish games (per table size) and Forge playtest took, with totals.</summary>
    private static void RunTimes(StringBuilder h, IReadOnlyList<JsonElement> runs, IReadOnlyList<JsonElement> forge)
    {
        var rows = new List<(string Deck, double?[] Seconds)>();   // 1v1, 1v1v1, Forge
        (string, double?[]) Row(string deck)
        {
            var i = rows.FindIndex(r => r.Deck == deck);
            if (i < 0) { rows.Add((deck, new double?[3])); i = rows.Count - 1; }
            return rows[i];
        }
        foreach (var run in runs)
        {
            if (!run.TryGetProperty("timing", out var timing) || timing.ValueKind != JsonValueKind.Object) continue;
            var (_, s) = Row(run.GetProperty("deck").GetProperty("name").GetString() ?? "?");
            foreach (var t in timing.GetProperty("by_table_s").EnumerateObject())
                if (Array.IndexOf(Tables, t.Name) is var k and >= 0) s[k] = (s[k] ?? 0) + t.Value.GetDouble();
        }
        foreach (var doc in forge)
        {
            var (_, s) = Row(doc.GetProperty("deck").GetProperty("name").GetString() ?? "?");
            s[2] = (s[2] ?? 0) + doc.GetProperty("elapsed_s").GetDouble();
        }
        if (rows.Count == 0) return;

        static double? Sum(IEnumerable<double?> xs) => xs.Any(x => x is not null) ? xs.Sum(x => x ?? 0) : null;
        var totals = Enumerable.Range(0, 3).Select(k => Sum(rows.Select(r => r.Seconds[k]))).ToArray();
        h.Append("""
            <h2>Run time</h2>
            <div class="card tablewrap"><table>
            <thead><tr><th>Deck</th><th></th><th>1v1</th><th>1v1v1</th><th>Forge</th><th>Total</th></tr></thead><tbody>
            """);
        string Cells(double?[] s) => string.Concat(s.Select(x => $"<td>{Duration(x)}</td>")) + $"<td>{Duration(Sum(s))}</td>";
        foreach (var (deck, s) in rows)
            h.Append($"<tr><td>{Esc(deck)}</td><td></td>{Cells(s)}</tr>");
        h.Append($"<tr><th>Total</th><th></th>{Cells(totals)}</tr>");
        h.Append("""
            </tbody></table>
            <p class="muted">Wall-clock time on the machine that ran them, with whatever else it was doing at the time. Runs that went
            side by side overlap, so the total is the work done, not the time from start to finish.</p>
            </div>
            """);
    }

    private static string Duration(double? seconds) => seconds switch
    {
        null => "–",
        < 60 => $"{seconds.Value:0}s",
        < 3600 => $"{(int)(seconds.Value / 60)}m {(int)(seconds.Value % 60):00}s",
        _ => $"{(int)(seconds.Value / 3600)}h {(int)(seconds.Value % 3600 / 60):00}m",
    };

    private static string Rate(JsonElement e) => NullableDouble(e, "win_rate") is { } r ? Pct(r) : "–";

    private static string Ci(JsonElement e) =>
        e.TryGetProperty("win_rate_ci95", out var ci) && ci.ValueKind == JsonValueKind.Array
            ? $"{Pct(ci[0].GetDouble(), 0)}–{Pct(ci[1].GetDouble(), 0)}" : "–";

    private static IEnumerable<Series> Read(JsonElement run)
    {
        var deck = run.GetProperty("deck");
        var name = deck.GetProperty("name").GetString() ?? "?";
        var unsupported = deck.GetProperty("unsupported_cards").GetArrayLength();
        var maxTurn = run.GetProperty("settings").GetProperty("max_turn").GetInt32();
        var rows = run.GetProperty("results").EnumerateArray().ToList();
        string Table(JsonElement r) => r.TryGetProperty("table", out var t) ? t.GetString()! : "1v1v1";   // before v0.7: two opponents
        string Mull(JsonElement r) => r.GetProperty("mulligan").GetString()!;
        var mulls = rows.Select(Mull).Distinct().ToList();
        foreach (var grp in rows.GroupBy(r => (Table(r), Mull(r))))
        {
            // The play/draw-weighted row when there is one, else the only seat played.
            var r = grp.FirstOrDefault(x => x.GetProperty("seat").GetString() == "weighted");
            if (r.ValueKind == JsonValueKind.Undefined) r = grp.First();
            var byTurn = new double[maxTurn];
            var cumulative = new double[maxTurn];
            foreach (var p in r.GetProperty("win_by_turn").EnumerateObject()) byTurn[int.Parse(p.Name, Inv) - 1] = p.Value.GetDouble();
            foreach (var p in r.GetProperty("cumulative_by_turn").EnumerateObject()) cumulative[int.Parse(p.Name, Inv) - 1] = p.Value.GetDouble();
            var label = mulls.Count > 1 ? $"{name} (mulligan {grp.Key.Item2})" : name;
            var seat = r.GetProperty("seat").GetString();
            yield return new Series(name, label, grp.Key.Item1,
                seat == "weighted" ? grp.Where(x => x.GetProperty("seat").GetString() != "weighted").Sum(x => x.GetProperty("games").GetInt32()) : r.GetProperty("games").GetInt32(),
                maxTurn, byTurn, cumulative, r.GetProperty("no_win").GetDouble(), NullableDouble(r, "median_turn"), NullableDouble(r, "mean_turn"),
                r.GetProperty("mean_turn_capped").GetDouble(), r.GetProperty("mean_turn_capped_ci95").EnumerateArray().Select(x => x.GetDouble()).ToArray(),
                unsupported);
        }
    }

    private static double? NullableDouble(JsonElement r, string key) =>
        r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private const int W = 560, H = 300, Left = 44, Right = 12, Top = 12, Bottom = 34;

    private static string LineChart(List<Series> rows, Dictionary<string, string> color, int maxTurn)
    {
        var sb = new StringBuilder($"<svg viewBox=\"0 0 {W} {H}\" role=\"img\" aria-label=\"Cumulative win rate by turn\">");
        Axes(sb, maxTurn, 1.0, band: false);
        foreach (var s in rows)
        {
            var pts = Enumerable.Range(1, s.MaxTurn).Select(t => (X(t, maxTurn, false), Y(s.Cumulative[t - 1], 1.0), t, s.Cumulative[t - 1])).ToList();
            sb.Append($"<polyline fill=\"none\" stroke=\"{color[s.Label]}\" stroke-width=\"2.5\" stroke-linejoin=\"round\" points=\"{string.Join(" ", pts.Select(p => F(p.Item1) + "," + F(p.Item2)))}\"/>");
            foreach (var (x, y, t, v) in pts)
                sb.Append($"<circle cx=\"{F(x)}\" cy=\"{F(y)}\" r=\"3.5\" fill=\"{color[s.Label]}\"><title>{Esc(s.Label)}: {Pct(v)} by turn {t}</title></circle>");
        }
        return sb.Append("</svg>").ToString();
    }

    private static string BarChart(List<Series> rows, int maxTurn)
    {
        var top = Math.Max(0.05, rows.SelectMany(r => r.ByTurn).DefaultIfEmpty(0).Max());
        top = Math.Ceiling(top * 20) / 20;   // round up to 5%
        var sb = new StringBuilder($"<svg viewBox=\"0 0 {W} {H}\" role=\"img\" aria-label=\"Wins on each turn\">");
        Axes(sb, maxTurn, top, band: true);
        var slot = (W - Left - Right) / (double)maxTurn;
        var barW = slot * 0.8 / Math.Max(1, rows.Count);
        for (var i = 0; i < rows.Count; i++)
            for (var t = 1; t <= rows[i].MaxTurn; t++)
            {
                var v = rows[i].ByTurn[t - 1];
                if (v <= 0) continue;
                var x = Left + slot * (t - 1) + slot * 0.1 + barW * i;
                var y = Y(v, top);
                sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(barW - 1)}\" height=\"{F(H - Bottom - y)}\" rx=\"2\" fill=\"{TableColor(i)}\">" +
                          $"<title>{rows[i].Table}: {Pct(v)} won on turn {t}</title></rect>");
            }
        return sb.Append("</svg>").ToString();
    }

    private static void Axes(StringBuilder sb, int maxTurn, double top, bool band)
    {
        for (var k = 0; k <= 4; k++)
        {
            var v = top * k / 4;
            var y = Y(v, top);
            sb.Append($"<line x1=\"{Left}\" x2=\"{W - Right}\" y1=\"{F(y)}\" y2=\"{F(y)}\" stroke=\"var(--grid)\"/>");
            sb.Append($"<text x=\"{Left - 6}\" y=\"{F(y + 4)}\" text-anchor=\"end\">{Pct(v, 0)}</text>");
        }
        for (var t = 1; t <= maxTurn; t++)
            sb.Append($"<text x=\"{F(X(t, maxTurn, band))}\" y=\"{H - Bottom + 16}\" text-anchor=\"middle\">{t}</text>");
        sb.Append($"<text x=\"{(Left + W - Right) / 2}\" y=\"{H - 4}\" text-anchor=\"middle\">turn</text>");
    }

    private static double X(int turn, int maxTurn, bool band)
    {
        var span = W - Left - Right;
        return band ? Left + span * (turn - 0.5) / maxTurn
            : Left + 8 + (span - 16) * (maxTurn == 1 ? 0.5 : (turn - 1) / (double)(maxTurn - 1));
    }

    private static double Y(double v, double top) => Top + (H - Top - Bottom) * (1 - v / top);

    private static string TableColor(int i) => i == 0 ? "var(--c0)" : "var(--c1)";

    private static string Legend(List<string> labels, Dictionary<string, string> color) =>
        "<div class=\"legend\">" + string.Join("", labels.Select(l => $"<span><span class=\"sw\" style=\"background:{color[l]}\"></span>{Esc(l)}</span>")) + "</div>";

    private static string SettingsText(List<JsonElement> settings)
    {
        string Values(string key) => string.Join("/", settings.Select(s => s.TryGetProperty(key, out var v) ? v.ToString() : "?").Distinct());
        return $"Games per seat and table: {Values("games")}. Turn cap: {Values("max_turn")}. Search rollouts: {Values("rollouts")}. Seed: {Values("seed")}.";
    }

    private static string F(double v) => v.ToString("0.#", Inv);
    private static string Pct(double v, int digits = 1) => (v * 100).ToString(digits == 0 ? "0" : "0.0", Inv) + "%";
    private static string Num(double? v) => v is { } x ? x.ToString("0.0#", Inv) : "–";
    private static string Esc(string s) => WebUtility.HtmlEncode(s);
}
