using MagicSim.Core.Decks;
using MagicSim.Core.Engine;
using MagicSim.Core.Players;

namespace MagicSim.Core.Sim;

public sealed class RunOptions
{
    public int Games { get; init; } = 10000;
    public ulong Seed { get; init; } = 1;
    public int MaxTurn { get; init; } = 20;
    public IReadOnlyList<MulliganPolicy> Mulligans { get; init; } = new[] { MulliganPolicy.A, MulliganPolicy.B, MulliganPolicy.C };
    public IReadOnlyList<string> Seats { get; init; } = new[] { "play", "draw" };
    /// <summary>Table sizes: "1v1" (one goldfish opponent) and "1v1v1" (two).</summary>
    public IReadOnlyList<string> Tables { get; init; } = new[] { "1v1", "1v1v1" };
    public int Rollouts { get; init; } = 2;
    public int Threads { get; init; } = Environment.ProcessorCount;
}

/// <summary>Everything per-game output reports (CONTRACT.md §3, magicsim.game/1).</summary>
public sealed record GameResult
{
    public required string Variant { get; init; }
    public required int Game { get; init; }
    public required string Table { get; init; }
    public required string Mulligan { get; init; }
    public required string Seat { get; init; }
    public int Mulligans { get; init; }
    public int Kept { get; init; }
    public List<string> HandKept { get; init; } = new();
    public List<string> Bottomed { get; init; } = new();
    public bool Won { get; init; }
    public int? WinTurn { get; init; }
    public string? WinLine { get; init; }
    public string? FailReason { get; init; }
    public int LifeEnd { get; init; }
    public List<int> OpponentsLifeEnd { get; init; } = new();
    public int TurnsPlayed { get; init; }
    public List<int> ManaByTurn { get; init; } = new();
    public int LifePaid { get; init; }
    public int StormMax { get; init; }
    public List<string> Notes { get; init; } = new();
    public List<string> Seen { get; init; } = new();
    public List<string> Cast { get; init; } = new();
    public List<string> WinCards { get; init; } = new();
}

/// <summary>Plays many goldfish games of one deck. Game i depends only on (seed, i) and the settings.</summary>
public static class Runner
{
    public sealed record PreparedDeck(string Name, List<CardDef> Library, List<CardDef> Commanders, List<string> Unsupported, int CardCount);

    public static PreparedDeck Prepare(Deck deck, CardPool pool)
    {
        var unknown = new List<string>();
        var unsupported = new SortedSet<string>(StringComparer.Ordinal);
        CardDef Resolve(string name)
        {
            var def = pool.Resolve(name);
            if (def is null) { unknown.Add(name); return null!; }
            if (def.IsBlank) unsupported.Add(def.Name);
            return def;
        }
        var library = deck.MainboardCards().Select(Resolve).ToList();
        var commanders = deck.Commanders.SelectMany(e => Enumerable.Repeat(e.Name, e.Count)).Select(Resolve).ToList();
        if (unknown.Count > 0)
            throw new InvalidDataException("no Forge script for: " + string.Join(", ", unknown.Distinct()));
        return new PreparedDeck(deck.Name, library, commanders, unsupported.ToList(), deck.TotalCount);
    }

    public static int OpponentsAt(string table) => table switch
    {
        "1v1" => 1,
        "1v1v1" => 2,
        _ => throw new ArgumentException($"unknown table size {table}"),
    };

    public static GameResult PlayOne(PreparedDeck deck, RunOptions o, int gameIndex, MulliganPolicy mull, string seat,
        string table = "1v1v1", string variant = "base", List<string>? log = null)
    {
        var settings = new GameSettings { OnThePlay = seat == "play", MaxTurn = o.MaxTurn, Opponents = OpponentsAt(table) };
        var searchSeed = Rng.ForGame(o.Seed, gameIndex, salt: 1).NextULong();
        var decider = new SearchDecider(new SearchSettings { Rollouts = o.Rollouts, Mulligan = mull }, searchSeed);
        var g = Game.NewGoldfish(deck.Library, deck.Commanders, settings, Rng.ForGame(o.Seed, gameIndex), decider);
        g.Log = log;
        g.PlayGame();

        var s = g.State;
        var me = s.Players[Game.Me];
        var won = g.Won;
        var seen = s.Record.Seen.ToList();
        var blanksSeen = seen.Count(n => deck.Unsupported.Contains(n));
        string? fail = null;
        if (!won)
        {
            var maxMana = s.Record.ManaByTurn.DefaultIfEmpty(0).Max();
            fail = blanksSeen * 4 >= Math.Max(1, seen.Count) ? "unsupported_dead_cards"
                : maxMana < 3 ? "no_mana"
                : "other";
        }
        return new GameResult
        {
            Variant = variant, Game = gameIndex, Table = table, Mulligan = mull.ToString(), Seat = seat,
            Mulligans = g.MulligansTaken, Kept = g.KeptHand.Count, HandKept = g.KeptHand.ToList(), Bottomed = g.Bottomed.ToList(),
            Won = won, WinTurn = won ? s.Round : null, WinLine = won ? s.WinReason : null, FailReason = fail,
            LifeEnd = me.Life, OpponentsLifeEnd = s.Players.Where(p => p.Index != Game.Me).Select(p => p.Life).ToList(),
            TurnsPlayed = s.Round, ManaByTurn = s.Record.ManaByTurn.ToList(), LifePaid = me.LifePaidTotal,
            StormMax = s.Record.StormMax, Notes = s.Record.Notes.ToList(), Seen = seen, Cast = s.Record.Cast.ToList(),
            WinCards = won ? s.Record.WinCards.ToList() : new(),
        };
    }

    /// <summary>All games for every (table, mulligan, seat), in contract row order.</summary>
    public static List<GameResult> Run(PreparedDeck deck, RunOptions o, string variant = "base", IProgress<int>? progress = null)
    {
        var combos = o.Tables.SelectMany(t => o.Mulligans.SelectMany(m => o.Seats.Select(seat => (t, m, seat)))).ToList();
        var results = new GameResult[combos.Count * o.Games];
        var done = 0;
        Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, o.Threads) }, i =>
        {
            var (t, m, seat) = combos[i / o.Games];
            results[i] = PlayOne(deck, o, i % o.Games, m, seat, t, variant);
            var n = Interlocked.Increment(ref done);
            if (n % 100 == 0) progress?.Report(n);
        });
        return results.ToList();
    }
}
