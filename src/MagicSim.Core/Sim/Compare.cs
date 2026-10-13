using MagicSim.Core.Decks;
using MagicSim.Core.Engine;

namespace MagicSim.Core.Sim;

public sealed record Swap(string Out, string In);

/// <summary>A deck to compare against the base: its label, the swaps that made it (or the card difference), and the deck.</summary>
public sealed record Variant(string Label, List<Swap> Swaps, Deck Deck);

/// <summary>Thrown for a swap that can't be made: the card to take out isn't in the deck, or the card to put in is unknown.</summary>
public sealed class SwapException(string message) : Exception(message);

/// <summary>
/// `sim compare` (CONTRACT.md §3, magicsim.compare/1): every variant plays the same shuffles as the base deck, so game i of
/// the base and game i of a variant form a pair, and the deltas and tests below are paired.
/// </summary>
public static class Compare
{
    /// <summary>"Out=>In" or several joined with ';'.</summary>
    public static List<Swap> ParseSwaps(string spec)
    {
        var swaps = new List<Swap>();
        foreach (var part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var arrow = part.IndexOf("=>", StringComparison.Ordinal);
            if (arrow <= 0 || arrow + 2 >= part.Length) throw new SwapException($"a swap is \"Out=>In\", got \"{part}\"");
            swaps.Add(new Swap(part[..arrow].Trim(), part[(arrow + 2)..].Trim()));
        }
        if (swaps.Count == 0) throw new SwapException("empty swap");
        return swaps;
    }

    public static string Label(IEnumerable<Swap> swaps) => string.Join("; ", swaps.Select(s => $"{s.Out}=>{s.In}"));

    /// <summary>
    /// The base deck with one copy of each Out replaced by one In. Names match the deck's spelling or the card's full
    /// name, ignoring case; the In card must have a Forge script.
    /// </summary>
    public static Variant Apply(Deck deck, IReadOnlyList<Swap> swaps, CardPool pool, string? label = null)
    {
        var copy = new Deck { Name = deck.Name };
        copy.Commanders.AddRange(deck.Commanders);
        copy.Mainboard.AddRange(deck.Mainboard);
        var applied = new List<Swap>();
        foreach (var (outName, inName) in swaps)
        {
            var outFull = pool.Compile(outName)?.Name;
            var i = copy.Mainboard.FindIndex(e => Same(e.Name, outName) || (outFull is not null && Same(pool.Compile(e.Name)?.Name, outFull)));
            if (i < 0)
                throw new SwapException(copy.Commanders.Any(e => Same(e.Name, outName))
                    ? $"\"{outName}\" is a commander; swaps change the main deck only"
                    : $"\"{outName}\" isn't in {deck.Name}'s main deck");
            var entry = copy.Mainboard[i];
            if (entry.Count == 1) copy.Mainboard.RemoveAt(i);
            else copy.Mainboard[i] = entry with { Count = entry.Count - 1 };

            var inDef = pool.Compile(inName) ?? throw new SwapException($"\"{inName}\" isn't a known card (no Forge script by that name)");
            var j = copy.Mainboard.FindIndex(e => Same(e.Name, inDef.Name));
            if (j >= 0) copy.Mainboard[j] = copy.Mainboard[j] with { Count = copy.Mainboard[j].Count + 1 };
            else copy.Mainboard.Add(new DeckEntry(1, inDef.Name));
            applied.Add(new Swap(entry.Name, inDef.Name));
        }
        return new Variant(label ?? Label(applied), applied, copy);
    }

    /// <summary>A whole other deck as a variant; its swaps are the card difference, for information.</summary>
    public static Variant FromDeck(Deck baseDeck, Deck other)
    {
        static Dictionary<string, int> Counts(Deck d)
        {
            var c = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in d.Commanders.Concat(d.Mainboard)) c[e.Name] = c.GetValueOrDefault(e.Name) + e.Count;
            return c;
        }
        var a = Counts(baseDeck);
        var b = Counts(other);
        var outs = a.SelectMany(kv => Enumerable.Repeat(kv.Key, Math.Max(0, kv.Value - b.GetValueOrDefault(kv.Key)))).Order(StringComparer.Ordinal).ToList();
        var ins = b.SelectMany(kv => Enumerable.Repeat(kv.Key, Math.Max(0, kv.Value - a.GetValueOrDefault(kv.Key)))).Order(StringComparer.Ordinal).ToList();
        var swaps = Enumerable.Range(0, Math.Max(outs.Count, ins.Count))
            .Select(k => new Swap(k < outs.Count ? outs[k] : "", k < ins.Count ? ins[k] : "")).ToList();
        return new Variant(other.Name, swaps, other);
    }

    private static bool Same(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>One variant's entry in the compare document.</summary>
    public static object VariantDocument(Variant v, Runner.PreparedDeck baseDeck, Runner.PreparedDeck deck, RunOptions o,
        IReadOnlyList<GameResult> baseGames, IReadOnlyList<GameResult> games)
    {
        var added = deck.Unsupported.Except(baseDeck.Unsupported, StringComparer.Ordinal).ToList();
        return new
        {
            label = v.Label,
            swaps = v.Swaps.Select(s => new { @out = s.Out, @in = s.In }).ToList(),
            unreliable = added.Count > 0,
            unsupported_added = added,
            results = DeltaRows(o, baseGames, games),
        };
    }

    /// <summary>Per (table, mulligan, seat) and weighted: the variant's numbers minus the base's, with paired intervals and tests.</summary>
    public static List<Dictionary<string, object?>> DeltaRows(RunOptions o, IReadOnlyList<GameResult> baseGames, IReadOnlyList<GameResult> games)
    {
        var baseRows = Report.ResultRows(o, baseGames);
        var rows = Report.ResultRows(o, games);
        var pairs = Pairs(baseGames, games);
        var result = new List<Dictionary<string, object?>>();
        foreach (var (b, v) in baseRows.Zip(rows))
        {
            var table = (string)b["table"]!;
            var mull = (string)b["mulligan"]!;
            var seat = (string)b["seat"]!;
            // Groups of pairs and their weights: one seat, or play and draw weighted by your share of being first.
            var groups = seat == "weighted"
                ? new[] { (pairs[(table, mull, "play")], Report.PlayWeight(table)), (pairs[(table, mull, "draw")], 1 - Report.PlayWeight(table)) }
                : new[] { (pairs[(table, mull, seat)], 1.0) };

            var bCum = (Dictionary<string, double>)b["cumulative_by_turn"]!;
            var vCum = (Dictionary<string, double>)v["cumulative_by_turn"]!;
            var deltaCum = new Dictionary<string, double>();
            var deltaCumCi = new Dictionary<string, double[]>();
            var pByTurn = new Dictionary<string, double>();
            for (var t = 1; t <= o.MaxTurn; t++)
            {
                var key = t.ToString();
                var turn = t;
                var (d, ci, p) = Paired(groups, g => WonBy(g, turn) ? 1 : 0, exactMcNemar: seat != "weighted");
                deltaCum[key] = Math.Round(vCum[key] - bCum[key], 4);
                deltaCumCi[key] = ci;
                pByTurn[key] = p;
            }
            var (_, t1Ci, t1P) = Paired(groups, g => WonBy(g, 1) ? 1 : 0, exactMcNemar: seat != "weighted");
            var (dCapped, cappedCi, cappedP) = Paired(groups, g => g.WinTurn ?? o.MaxTurn + 1, exactMcNemar: false);

            result.Add(new Dictionary<string, object?>
            {
                ["table"] = table,
                ["mulligan"] = mull,
                ["seat"] = seat,
                ["games"] = v["games"],
                ["t1_rate"] = vCum["1"],
                ["delta_t1"] = deltaCum["1"],
                ["delta_t1_ci95"] = t1Ci,
                ["p_value"] = t1P,
                ["delta_cumulative_by_turn"] = deltaCum,
                ["delta_cumulative_by_turn_ci95"] = deltaCumCi,
                ["p_value_by_turn"] = pByTurn,
                ["delta_mean_turn"] = v["mean_turn"] is double vm && b["mean_turn"] is double bm ? Math.Round(vm - bm, 3) : null,
                ["delta_mean_turn_capped"] = Math.Round(dCapped, 3),
                ["delta_mean_turn_capped_ci95"] = cappedCi,
                ["p_value_mean_turn_capped"] = cappedP,
            });
        }
        return result;
    }

    private static bool WonBy(GameResult g, int turn) => g.Won && g.WinTurn <= turn;

    /// <summary>Base and variant games paired by game index, for each (table, mulligan, seat).</summary>
    private static Dictionary<(string, string, string), List<(GameResult Base, GameResult Variant)>> Pairs(
        IReadOnlyList<GameResult> baseGames, IReadOnlyList<GameResult> games)
    {
        var byKey = games.ToDictionary(g => (g.Table, g.Mulligan, g.Seat, g.Game));
        return baseGames.GroupBy(g => (g.Table, g.Mulligan, g.Seat))
            .ToDictionary(grp => grp.Key, grp => grp.Select(g => (g, byKey[(g.Table, g.Mulligan, g.Seat, g.Game)])).ToList());
    }

    /// <summary>
    /// The weighted mean of variant-minus-base differences of <paramref name="f"/>, its 95% interval, and a two-sided
    /// p-value: McNemar's exact test for a single seat's 0/1 outcome, else a normal test on the paired differences.
    /// </summary>
    public static (double Delta, double[] Ci, double P) Paired(
        IEnumerable<(List<(GameResult Base, GameResult Variant)> Pairs, double Weight)> groups, Func<GameResult, double> f, bool exactMcNemar)
    {
        double delta = 0, variance = 0;
        int better = 0, worse = 0, groupCount = 0;
        foreach (var (pairs, w) in groups)
        {
            groupCount++;
            var n = pairs.Count;
            if (n == 0) continue;
            var d = pairs.Select(p => f(p.Variant) - f(p.Base)).ToList();
            var mean = d.Average();
            var s2 = n > 1 ? d.Sum(x => (x - mean) * (x - mean)) / (n - 1) : 0;
            delta += w * mean;
            variance += w * w * s2 / n;
            better += d.Count(x => x > 0);
            worse += d.Count(x => x < 0);
        }
        var se = Math.Sqrt(variance);
        var ci = new[] { Math.Round(delta - 1.96 * se, 4), Math.Round(delta + 1.96 * se, 4) };
        double p;
        if (exactMcNemar && groupCount == 1) p = McNemar(better, worse);
        else if (se == 0) p = delta == 0 ? 1 : 0;
        else p = 2 * (1 - NormalCdf(Math.Abs(delta) / se));
        return (delta, ci, Math.Round(p, 4));
    }

    /// <summary>McNemar's exact test: two-sided binomial p for b vs c discordant pairs.</summary>
    public static double McNemar(int b, int c)
    {
        var n = b + c;
        if (n == 0) return 1;
        var k = Math.Min(b, c);
        // P(X <= k) for X ~ Binomial(n, 1/2), summed in log space so large n doesn't underflow the first term.
        double logTerm = -n * Math.Log(2), sum = 0;
        for (var i = 0; i <= k; i++)
        {
            if (i > 0) logTerm += Math.Log(n - i + 1) - Math.Log(i);
            sum += Math.Exp(logTerm);
        }
        return Math.Min(1, 2 * sum);
    }

    /// <summary>Standard normal CDF (Abramowitz and Stegun 7.1.26, error below 1.5e-7).</summary>
    public static double NormalCdf(double z)
    {
        var x = Math.Abs(z) / Math.Sqrt(2);
        var t = 1 / (1 + 0.3275911 * x);
        var erf = 1 - ((((1.061405429 * t - 1.453152027) * t + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
        return z >= 0 ? (1 + erf) / 2 : (1 - erf) / 2;
    }
}
