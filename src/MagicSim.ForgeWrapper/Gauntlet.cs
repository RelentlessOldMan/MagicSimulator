using System.Collections.Concurrent;
using System.Diagnostics;
using MagicSim.Core.Decks;
using MagicSim.Core.Forge;

namespace MagicSim.ForgeWrapper;

public sealed class GauntletOptions
{
    public int Games { get; init; } = 20;
    public int Parallel { get; init; } = 1;
    /// <summary>Priority of each Forge process.</summary>
    public ProcessPriorityClass Priority { get; init; } = ProcessPriorityClass.BelowNormal;
    public int TimeoutSeconds { get; init; } = 120;
}

public sealed record MatchupResult(string Name, string Path, List<string> UnknownCards, List<ForgeGame> Games, string? Error);

public sealed record GauntletResult(string DeckName, string DeckPath, List<string> UnknownCards, string ForgeVersion,
    GauntletOptions Options, List<MatchupResult> Opponents, double ElapsedSeconds);

/// <summary>
/// Plays one deck against a list of opponents in Forge's AI-vs-AI simulator, 1v1 Commander, one Java process per
/// batch of games (CONTRACT.md §3, "Forge playtest"). Our deck is always player 1.
/// </summary>
public static class Gauntlet
{
    /// <summary>The fewest games worth a process: Forge takes about half a minute to start.</summary>
    private const int MinGamesPerProcess = 5;

    /// <summary>A Commander deck: a .txt with a *CMDR* line. Strategy notes and list files aren't.</summary>
    /// <summary>
    /// For a field of <paramref name="decks"/> decks, the offsets (1 to decks-1, spread evenly) of the opponents each deck
    /// plays: deck i plays decks i+offset. Every deck then meets the same number of opponents and is met that many times.
    /// </summary>
    public static List<int> SpreadOffsets(int decks, int opponents)
    {
        opponents = Math.Clamp(opponents, 0, Math.Max(0, decks - 1));
        return Enumerable.Range(0, opponents).Select(j => 1 + j * (decks - 1) / opponents).ToList();
    }

    public static bool IsDeckFile(string path) =>
        path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && DeckReader.ReadFile(path).Commanders.Count > 0;

    /// <summary>
    /// Every opponent deck named by <paramref name="against"/>: deck files, folders (their decks except -moxfield
    /// copies) and list files (one path per line, relative to the list). With none, the deck's own folder.
    /// </summary>
    public static List<string> ResolveOpponents(string deckPath, IReadOnlyList<string> against)
    {
        var self = Path.GetFullPath(deckPath);
        var sources = against.Count > 0 ? against : new[] { Path.GetDirectoryName(self)! };
        var result = new List<string>();
        void Add(string path, int depth)
        {
            path = Path.GetFullPath(path);
            if (Directory.Exists(path))
            {
                foreach (var f in Directory.EnumerateFiles(path, "*.txt").Order(StringComparer.OrdinalIgnoreCase))
                    if (!Path.GetFileNameWithoutExtension(f).EndsWith("-moxfield", StringComparison.OrdinalIgnoreCase) && IsDeckFile(f))
                        result.Add(f);
            }
            else if (!File.Exists(path)) throw new FileNotFoundException($"opponent not found: {path}", path);
            else if (IsDeckFile(path)) result.Add(path);
            else if (depth < 4)
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length > 0 && !line.StartsWith('#')) Add(Path.Combine(Path.GetDirectoryName(path)!, line), depth + 1);
                }
        }
        foreach (var s in sources) Add(s, 0);
        return result.Where(p => !string.Equals(p, self, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Card names the Forge card scripts don't have (Forge leaves them out of the deck); empty without the scripts.</summary>
    public static List<string> UnknownCards(Deck deck, ForgeLibrary? forge) =>
        forge is null ? new() : deck.Commanders.Concat(deck.Mainboard).Select(e => e.Name).Where(n => forge.Find(n) is null).Distinct().ToList();

    /// <summary>How many processes play each opponent's games, so that <paramref name="parallel"/> processes stay busy.</summary>
    public static int[] Batches(int games, int opponents, int parallel)
    {
        var perOpponent = opponents >= parallel ? 1 : (parallel + opponents - 1) / opponents;
        var count = Math.Clamp(Math.Min(perOpponent, games / MinGamesPerProcess), 1, games);
        return Enumerable.Range(0, count).Select(i => games / count + (i < games % count ? 1 : 0)).ToArray();
    }

    public static GauntletResult Run(ForgeApp app, string deckPath, IReadOnlyList<string> opponentPaths, GauntletOptions options,
        ForgeLibrary? forge, TextWriter log)
    {
        var sw = Stopwatch.StartNew();
        app.EnsureInitialized(log);
        var deck = DeckReader.ReadFile(deckPath);
        var opponents = opponentPaths.Select(DeckReader.ReadFile).ToList();
        var work = Path.Combine(app.Dir, "runs", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(work);
        try
        {
            File.WriteAllText(Path.Combine(work, "deck.dck"), DckWriter.Write(deck));
            for (var i = 0; i < opponents.Count; i++)
                File.WriteAllText(Path.Combine(work, $"opp{i}.dck"), DckWriter.Write(opponents[i]));

            var jobs = opponents.SelectMany((_, i) => Batches(options.Games, opponents.Count, options.Parallel).Select(n => (Opponent: i, Games: n))).ToList();
            var games = opponents.Select(_ => new ConcurrentBag<ForgeGame>()).ToArray();
            var errors = new ConcurrentDictionary<int, string>();
            var total = jobs.Sum(j => j.Games);
            var done = 0;
            Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = options.Parallel }, job =>
            {
                var error = RunProcess(app, work, $"opp{job.Opponent}.dck", job.Games, options.TimeoutSeconds, options.Priority, g =>
                {
                    games[job.Opponent].Add(g);
                    var n = Interlocked.Increment(ref done);
                    var outcome = g.Winner is null ? (g.TimedOut ? "draw (timeout)" : "draw") : g.Winner == 1 ? "win" : "loss";
                    var round = Round(g) is { } r ? $", round {r}" : "";
                    lock (log) log.WriteLine($"[{n}/{total}] vs {opponents[job.Opponent].Name}: {outcome}{round}, {g.Ms / 1000.0:F0}s");
                });
                if (error is not null) errors.TryAdd(job.Opponent, error);
            });

            var matchups = opponents.Select((o, i) => new MatchupResult(o.Name, opponentPaths[i], UnknownCards(o, forge),
                games[i].OrderBy(g => g.Game).ToList(), errors.TryGetValue(i, out var e) ? e : null)).ToList();
            return new GauntletResult(deck.Name, Path.GetFullPath(deckPath), UnknownCards(deck, forge), app.Version, options, matchups,
                sw.Elapsed.TotalSeconds);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (IOException) { }
        }
    }

    /// <summary>Forge counts every player's turn; this is the round the game ended in, at 2 players.</summary>
    public static int? Round(ForgeGame g) => g.Turn is { } t ? (t + 1) / 2 : null;

    /// <summary>Runs one Forge process; returns why it failed, or null.</summary>
    private static string? RunProcess(ForgeApp app, string work, string opponentFile, int games, int timeoutSeconds,
        ProcessPriorityClass priority, Action<ForgeGame> onGame)
    {
        var psi = app.StartInfo("sim", "-d", "deck.dck", opponentFile, "-D", work.Replace('\\', '/') + "/", "-n", games.ToString(),
            "-f", "Commander", "-c", timeoutSeconds.ToString(), "-q");
        psi.RedirectStandardOutput = psi.RedirectStandardError = true;

        var parser = new ForgeOutputParser();
        var played = 0;
        var stderr = new List<string>();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null || parser.Read(e.Data) is not { } g) return;
            played++;
            onGame(g);
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            // Forge warns a lot on stderr; this keeps enough to find the exception if it fails.
            lock (stderr)
                if (stderr.Count < 2000) stderr.Add(e.Data);
        };
        p.Start();
        try { p.PriorityClass = priority; } catch (InvalidOperationException) { }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        // Forge ends a slow game itself after the timeout; this only catches a process that stops responding.
        var limit = TimeSpan.FromSeconds(180 + games * (timeoutSeconds + 30.0));
        if (!p.WaitForExit(limit))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            p.WaitForExit();
            return $"Forge stopped responding after {played} of {games} games";
        }
        p.WaitForExit();   // drains the output events
        if (parser.LoadError is { } load) return load.Replace(opponentFile, "the opponent deck").Replace("deck.dck", "the deck");
        if (played < games)
        {
            string error;
            lock (stderr) error = ForgeApp.FirstError(stderr);
            return $"Forge exited (code {p.ExitCode}) after {played} of {games} games: {error}";
        }
        return null;
    }
}
