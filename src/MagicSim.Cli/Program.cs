using System.Text.Json;
using MagicSim.Core.Cards;
using MagicSim.Core.Decks;
using MagicSim.Core.Forge;
using MagicSim.Core.Engine;
using MagicSim.Core.Players;
using MagicSim.Core.Sim;
using MagicSim.ForgeWrapper;

namespace MagicSim.Cli;

/// <summary>`sim` entry point. Exit codes per CONTRACT.md §3: 0 ok, 1 run error, 2 usage error.</summary>
public static class Program
{
    public const int Ok = 0, RunError = 1, UsageError = 2;

    private static readonly JsonSerializerOptions JsonOut = new() { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            return Usage(args.Length == 0 ? UsageError : Ok);

        try
        {
            var opts = Options.Parse(args[1..]);
            if (LowPriority.Requested(opts)) LowPriority.Apply();
            return args[0] switch
            {
                "sync-cards" => await SyncCards(opts),
                "check" => Check(opts),
                "sync-forge" => SyncForge(opts),
                "cards" => Cards(opts),
                "run" => Run(opts),
                "replay" => Replay(opts),
                "compare" => CompareCommand(opts),
                "batch" => Batch(opts),
                "report" => HtmlReportCommand(opts),
                "forge-setup" => await ForgeSetup(opts),
                "forge-gauntlet" => ForgeGauntlet(opts),
                _ => Fail(UsageError, $"unknown command: {args[0]}"),
            };
        }
        catch (UsageException e)
        {
            return Fail(UsageError, e.Message);
        }
        catch (Exception e)
        {
            // MAGICSIM_DEBUG=1 shows where it failed.
            return Fail(RunError, Environment.GetEnvironmentVariable("MAGICSIM_DEBUG") is { Length: > 0 } ? e.ToString() : e.Message);
        }
    }

    private static async Task<int> SyncCards(Options o)
    {
        o.RequirePositional(0);
        await ScryfallSync.RunAsync(o.Get("--out") ?? CardIndex.DefaultPath, Console.Error);
        return Ok;
    }

    private static int Check(Options o)
    {
        var deckPath = o.RequirePositional(1)[0];
        var deck = DeckReader.ReadFile(deckPath);
        var result = DeckCheck.Run(deck, CardIndex.Load(o.Get("--cards")));
        var json = JsonSerializer.Serialize(new
        {
            schema = "magicsim.check/1",
            deck = result.Deck,
            ok = result.Ok,
            card_count = result.CardCount,
            commanders = result.Commanders,
            unknown_names = result.UnknownNames,
            warnings = result.Warnings,
        }, JsonOut);
        Emit(o, json);
        return Ok;
    }

    private static int SyncForge(Options o)
    {
        o.RequirePositional(0);
        ForgeLibrary.Sync(o.Get("--dir") ?? ForgeLibrary.DefaultDir, Console.Error);
        return Ok;
    }

    private static int Cards(Options o)
    {
        o.RequirePositional(0);
        var forge = ForgeLibrary.Load(o.Get("--forge"));
        var index = CardIndex.Load(o.Get("--cards"));
        var cardPool = new CardPool(forge, index);
        (SupportTier, IReadOnlyCollection<string>) Classify(ForgeCard fc)
        {
            var def = cardPool.Compile(fc.FullName)!;
            return (def.Supported ? SupportTier.Scripted : SupportTier.Unsupported, def.Missing);
        }

        // The pool is every Commander-legal (or banned) card Forge has a script for.
        var pool = forge.Cards.Where(c => index.Find(c.FullName) is { CommanderLegality: "legal" or "banned" }).ToList();
        var top = o.Get("--top") is { } t ? int.TryParse(t, out var n) && n > 0 ? n : throw new UsageException("--top must be a positive integer") : 30;
        var report = Coverage.Analyze(pool, Classify, top);
        var popularPool = pool.Where(c => index.Find(c.FullName)?.EdhrecRank is <= PopularRank).ToList();
        var popular = Coverage.Analyze(popularPool, Classify, top);

        object? deckPart = null;
        if (o.Get("--deck") is { } deckPath)
        {
            var deck = DeckReader.ReadFile(deckPath);
            deckPart = new
            {
                name = deck.Name,
                cards = deck.Commanders.Concat(deck.Mainboard).Select(e =>
                {
                    var fc = forge.Find(e.Name);
                    if (fc is null) return new { name = e.Name, tier = "unsupported", missing = new List<string> { "NoForgeScript" } };
                    var (tier, missing) = Classify(fc);
                    return new { name = e.Name, tier = TierName(tier), missing = missing.ToList() };
                }).ToList(),
            };
        }

        Emit(o, JsonSerializer.Serialize(new
        {
            schema = "magicsim.cards/1",
            pool = new
            {
                description = "Commander-legal cards with a Forge script",
                cards = report.Cards,
                scripted = report.Scripted,
                hand_coded = report.HandCoded,
                supported_share = Math.Round(report.Share, 4),
                next_to_implement = report.TopBlockers.Select(b => new
                {
                    requirement = b.Requirement, cards_needing = b.CardsNeeding, cards_unlocked_alone = b.CardsUnlockedAlone,
                }),
            },
            popular = new
            {
                description = $"The {PopularRank} most-played Commander cards (EDHREC rank) with a Forge script",
                cards = popular.Cards,
                scripted = popular.Scripted,
                hand_coded = popular.HandCoded,
                supported_share = Math.Round(popular.Share, 4),
                next_to_implement = popular.TopBlockers.Select(b => new
                {
                    requirement = b.Requirement, cards_needing = b.CardsNeeding, cards_unlocked_alone = b.CardsUnlockedAlone,
                }),
            },
            deck = deckPart,
        }, JsonOut));
        return Ok;
    }

    /// <summary>"Popular" cards for the coverage report: EDHREC rank at or better than this.</summary>
    private const int PopularRank = 5000;

    private static RunOptions RunOptionsFrom(Options o, bool single = false)
    {
        int Int(string key, int fallback, int min = 1)
        {
            if (o.Get(key) is not { } v) return fallback;
            if (!int.TryParse(v, out var n) || n < min) throw new UsageException($"{key} must be an integer >= {min}");
            return n;
        }
        var mull = (o.Get("--mull") ?? (single ? "C" : "all")).ToUpperInvariant();
        var mulls = mull == "ALL" ? new[] { MulliganPolicy.A, MulliganPolicy.B, MulliganPolicy.C }
            : Enum.TryParse<MulliganPolicy>(mull, out var m) ? new[] { m } : throw new UsageException("--mull must be A, B, C or all");
        var seat = o.Get("--seat") ?? (single ? "play" : "both");
        var seats = seat switch
        {
            "both" when !single => new[] { "play", "draw" },
            "play" or "draw" => new[] { seat },
            _ => throw new UsageException(single ? "--seat must be play or draw" : "--seat must be play, draw or both"),
        };
        var table = o.Get("--table") ?? (single ? "1v1v1" : "both");
        var tables = table switch
        {
            "both" when !single => new[] { "1v1", "1v1v1" },
            "1v1" or "1v1v1" => new[] { table },
            _ => throw new UsageException(single ? "--table must be 1v1 or 1v1v1" : "--table must be 1v1, 1v1v1 or both"),
        };
        var seedText = o.Get("--seed") ?? "1";
        if (!ulong.TryParse(seedText, out var seed)) throw new UsageException("--seed must be a non-negative integer");
        return new RunOptions
        {
            Games = Int("--games", 10000), Seed = seed, MaxTurn = Int("--max-turn", 20), Mulligans = mulls, Seats = seats, Tables = tables,
            Rollouts = Int("--rollouts", 2, 0), Threads = LowPriority.Active ? OneCore(o, "--threads") : Int("--threads", Environment.ProcessorCount),
        };
    }

    private static (Runner.PreparedDeck Deck, string Path) LoadDeck(Options o)
    {
        var path = o.RequirePositional(1)[0];
        var deck = DeckReader.ReadFile(path);
        var pool = new CardPool(ForgeLibrary.Load(o.Get("--forge")), CardIndex.Load(o.Get("--cards")));
        return (Runner.Prepare(deck, pool), path);
    }

    /// <summary>
    /// The base deck against variants on the same shuffles: --swap "Out=>In" (repeatable, one variant together),
    /// --variant "Out=>In[;Out=>In]" (repeatable, one variant each), and any further deck files.
    /// </summary>
    private static int CompareCommand(Options o)
    {
        var opts = RunOptionsFrom(o);
        if (o.Positional.Count == 0) throw new UsageException("compare needs a base deck");
        var pool = new CardPool(ForgeLibrary.Load(o.Get("--forge")), CardIndex.Load(o.Get("--cards")));
        var basePath = o.Positional[0];
        var baseDeck = DeckReader.ReadFile(basePath);
        var variants = new List<Variant>();
        try
        {
            if (o.GetAll("--swap").Count > 0)
                variants.Add(Core.Sim.Compare.Apply(baseDeck, o.GetAll("--swap").SelectMany(Core.Sim.Compare.ParseSwaps).ToList(), pool));
            foreach (var spec in o.GetAll("--variant"))
                variants.Add(Core.Sim.Compare.Apply(baseDeck, Core.Sim.Compare.ParseSwaps(spec), pool));
        }
        catch (SwapException e)
        {
            throw new UsageException(e.Message);
        }
        foreach (var path in o.Positional.Skip(1))
            variants.Add(Core.Sim.Compare.FromDeck(baseDeck, DeckReader.ReadFile(path)));
        if (variants.Count == 0) throw new UsageException("compare needs --swap \"Out=>In\", --variant \"Out=>In\" or a second deck file");

        var basePrepared = Runner.Prepare(baseDeck, pool);
        var perVariant = opts.Games * opts.Tables.Count * opts.Mulligans.Count * opts.Seats.Count;
        var total = perVariant * (variants.Count + 1);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var done = 0;
        IProgress<int> Progress() => new Progress<int>(n => Console.Error.Write($"\r{done + n}/{total} games ({sw.Elapsed.TotalSeconds:F0}s)"));
        var tableSeconds = new Dictionary<string, double>();
        var baseGames = Runner.Run(basePrepared, opts, progress: Progress(), tableSeconds: tableSeconds);
        done += perVariant;
        var docs = new List<object>();
        var allGames = new List<GameResult>(baseGames);
        var summaries = new List<(Variant V, List<Dictionary<string, object?>> Rows)>();
        foreach (var v in variants)
        {
            var prepared = Runner.Prepare(v.Deck, pool);
            var games = Runner.Run(prepared, opts, variant: v.Label, progress: Progress());
            done += perVariant;
            var doc = Core.Sim.Compare.VariantDocument(v, basePrepared, prepared, opts, baseGames, games);
            docs.Add(doc);
            summaries.Add((v, Core.Sim.Compare.DeltaRows(opts, baseGames, games)));
            allGames.AddRange(games);
            foreach (var name in prepared.Unsupported.Except(basePrepared.Unsupported))
                Console.Error.WriteLine($"\rwarning: {v.Label} adds {name}, which the simulator plays as a blank, so its results are unreliable");
        }
        Console.Error.WriteLine($"\r{total}/{total} games in {sw.Elapsed.TotalSeconds:F1}s");
        if (o.Get("--per-game") is { } perGame)
            File.WriteAllLines(perGame, allGames.Select(Report.GameLine), new System.Text.UTF8Encoding(false));

        var json = JsonSerializer.Serialize(new
        {
            schema = "magicsim.compare/1",
            sim_version = Report.SimVersion,
            @base = Report.RunDocument(basePrepared, basePath, opts, baseGames, tableSeconds),
            variants = docs,
        }, Report.Json);
        Emit(o, json);
        // With --json the table goes to stdout; otherwise stdout carries the JSON, so the table goes to stderr.
        var table = o.Get("--json") is null ? Console.Error : Console.Out;
        foreach (var line in CompareTable(summaries, opts.MaxTurn)) table.WriteLine(line);
        return Ok;
    }

    /// <summary>A short readable summary: per variant and table, the headline (weighted, else only) rows.</summary>
    private static IEnumerable<string> CompareTable(List<(Variant V, List<Dictionary<string, object?>> Rows)> summaries, int maxTurn)
    {
        string F(double x) => x.ToString("+0.00;-0.00;0.00", System.Globalization.CultureInfo.InvariantCulture);
        string Pct(double x) => (x * 100).ToString("+0.0;-0.0;0.0", System.Globalization.CultureInfo.InvariantCulture);
        string P(double p) => p < 0.001 ? "<.001" : p.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        yield return "";
        yield return "Δ = variant minus base on the same shuffles. Capped mean: lower is faster. Won by Tn: points, with p (McNemar per seat, z-test when weighted).";
        foreach (var (v, rows) in summaries)
        {
            yield return "";
            yield return v.Label;
            var headline = rows.Where(r => (string)r["seat"]! == "weighted").ToList();
            if (headline.Count == 0) headline = rows;
            foreach (var r in headline)
            {
                var ci = (double[])r["delta_mean_turn_capped_ci95"]!;
                var cum = (Dictionary<string, double>)r["delta_cumulative_by_turn"]!;
                var pt = (Dictionary<string, double>)r["p_value_by_turn"]!;
                // The turns where the deck's results are changing most: the largest absolute deltas, in turn order.
                var turns = cum.OrderByDescending(kv => Math.Abs(kv.Value)).ThenBy(kv => int.Parse(kv.Key)).Take(4)
                    .Select(kv => int.Parse(kv.Key)).Order().ToList();
                var byTurn = string.Join("  ", turns.Select(t => $"T{t} {Pct(cum[t.ToString()])} (p {P(pt[t.ToString()])})"));
                yield return $"  {r["table"],-6} mull {r["mulligan"]} {r["seat"],-8} capped {F((double)r["delta_mean_turn_capped"]!)} [{F(ci[0])}, {F(ci[1])}] p {P((double)r["p_value_mean_turn_capped"]!)}   {byTurn}";
            }
        }
    }

    /// <summary>
    /// Every deck in the given folders or files: a run (with per-game results) each, optionally a Forge playtest each against
    /// evenly spread opponents from the same field, then the report with a page per deck. Files without a commander are skipped.
    /// </summary>
    private static int Batch(Options o)
    {
        if (o.Positional.Count == 0) throw new UsageException("batch needs deck folders or deck files");
        var outDir = o.Get("--out") ?? throw new UsageException("batch needs --out <folder>");
        var opts = RunOptionsFrom(o);
        int Int(string key, int fallback, int min)
        {
            if (o.Get(key) is not { } v) return fallback;
            return int.TryParse(v, out var n) && n >= min ? n : throw new UsageException($"{key} must be an integer >= {min}");
        }
        var forgeGames = Int("--forge-games", 0, 0);
        var forgeOpponents = Int("--forge-opponents", 6, 1);
        var forgeParallel = LowPriority.Active ? OneCore(o, "--forge-parallel") : Int("--forge-parallel", 1, 1);

        var decks = new List<string>();
        foreach (var p in o.Positional)
        {
            if (Directory.Exists(p))
                decks.AddRange(Directory.EnumerateFiles(p, "*.txt").Order(StringComparer.OrdinalIgnoreCase)
                    .Where(f => !Path.GetFileNameWithoutExtension(f).EndsWith("-moxfield", StringComparison.OrdinalIgnoreCase) && Gauntlet.IsDeckFile(f)));
            else if (File.Exists(p))
                decks.Add(Gauntlet.IsDeckFile(p) ? p : throw new UsageException($"{p} isn't a deck (no *CMDR* line)"));
            else throw new FileNotFoundException($"not found: {p}");
        }
        decks = decks.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (decks.Count == 0) throw new UsageException("no decks found (a deck is a .txt with a *CMDR* line)");
        Directory.CreateDirectory(outDir);
        var scripts = ForgeLibrary.Load(o.Get("--forge"));
        var pool = new CardPool(scripts, CardIndex.Load(o.Get("--cards")));
        Console.Error.WriteLine($"batch: {decks.Count} decks, {opts.Games} games per table, mulligan and seat" +
                                (forgeGames > 0 ? $", Forge {forgeGames} games against each of {Math.Min(forgeOpponents, decks.Count - 1)} opponents" : ""));

        var outputs = new List<string>();
        var total = System.Diagnostics.Stopwatch.StartNew();
        foreach (var path in decks)
        {
            var deck = Runner.Prepare(DeckReader.ReadFile(path), pool);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var tableSeconds = new Dictionary<string, double>();
            var games = Runner.Run(deck, opts, tableSeconds: tableSeconds);
            var json = Path.Combine(outDir, deck.Name + ".json");
            File.WriteAllText(json, JsonSerializer.Serialize(Report.RunDocument(deck, path, opts, games, tableSeconds), Report.Json) + "\n");
            File.WriteAllLines(Path.Combine(outDir, deck.Name + ".games.jsonl"), games.Select(Report.GameLine), new System.Text.UTF8Encoding(false));
            outputs.Add(json);
            Console.Error.WriteLine($"  {deck.Name}: {games.Count} games in {sw.Elapsed.TotalSeconds:F0}s");
        }
        Console.Error.WriteLine($"goldfish done in {total.Elapsed.TotalMinutes:F1} min");

        if (forgeGames > 0 && decks.Count > 1)
        {
            var app = ForgeApp.Load(o.Get("--forge-app"));
            var offsets = Gauntlet.SpreadOffsets(decks.Count, forgeOpponents);
            var gauntlet = new GauntletOptions
            {
                Games = forgeGames, Parallel = forgeParallel, TimeoutSeconds = Int("--timeout", 120, 1),
                Priority = LowPriority.Active ? System.Diagnostics.ProcessPriorityClass.Idle : System.Diagnostics.ProcessPriorityClass.BelowNormal,
            };
            var forgeStart = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < decks.Count; i++)
            {
                var opponents = offsets.Select(k => decks[(i + k) % decks.Count]).ToList();
                var result = Gauntlet.Run(app, decks[i], opponents, gauntlet, scripts, TextWriter.Null);
                var json = Path.Combine(outDir, result.DeckName + ".forge.json");
                File.WriteAllText(json, JsonSerializer.Serialize(GauntletReport.Document(result), JsonOut) + "\n");
                outputs.Add(json);
                var errors = result.Opponents.Count(m => m.Error is not null);
                Console.Error.WriteLine($"  Forge {result.DeckName}: {result.Opponents.Sum(m => m.Games.Count)} games in {result.ElapsedSeconds:F0}s" +
                                        (errors > 0 ? $", {errors} opponents failed" : ""));
            }
            Console.Error.WriteLine($"Forge done in {forgeStart.Elapsed.TotalMinutes:F1} min");
        }

        var reportArgs = outputs.Concat(new[] { "--out", Path.Combine(outDir, "report.html") }).ToArray();
        HtmlReportCommand(Options.Parse(reportArgs));
        Console.Error.WriteLine($"batch done in {total.Elapsed.TotalMinutes:F1} min");
        return Ok;
    }

    private static int Run(Options o)
    {
        var opts = RunOptionsFrom(o);
        var (deck, path) = LoadDeck(o);
        var total = opts.Games * opts.Tables.Count * opts.Mulligans.Count * opts.Seats.Count;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tableSeconds = new Dictionary<string, double>();
        var games = Runner.Run(deck, opts, progress: new Progress<int>(n =>
            Console.Error.Write($"\r{n}/{total} games ({sw.Elapsed.TotalSeconds:F0}s)")), tableSeconds: tableSeconds);
        var cpu = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
        Console.Error.WriteLine($"\r{total}/{total} games in {sw.Elapsed.TotalSeconds:F1}s (CPU {cpu.TotalSeconds:F1}s, allocated {GC.GetTotalAllocatedBytes() / 1e9:F1} GB)");
        if (o.Get("--per-game") is { } perGame)
            File.WriteAllLines(perGame, games.Select(Report.GameLine), new System.Text.UTF8Encoding(false));
        Emit(o, JsonSerializer.Serialize(Report.RunDocument(deck, path, opts, games, tableSeconds), Report.Json));
        return Ok;
    }

    private static int HtmlReportCommand(Options o)
    {
        if (o.Positional.Count == 0) throw new UsageException("report needs one or more run or forge JSON files");
        var runs = new List<JsonElement>();
        var runPaths = new List<string>();
        var forge = new List<JsonElement>();
        var forgePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in o.Positional)
        {
            var doc = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
            var schema = doc.TryGetProperty("schema", out var s) ? s.GetString() : null;
            if (schema == "magicsim.run/1") { runs.Add(doc); runPaths.Add(path); }
            else if (schema == GauntletReport.Schema && forgePaths.Add(Path.GetFullPath(path))) forge.Add(doc);
            else throw new UsageException($"{path} isn't a magicsim.run/1 or {GauntletReport.Schema} document");
        }
        // A deck's Forge playtest, when one was run: <deck name>.forge.json next to its run JSON.
        foreach (var (run, path) in runs.Zip(runPaths))
        {
            if (!run.TryGetProperty("deck", out var deck) || deck.GetProperty("name").GetString() is not { } name) continue;
            var candidate = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", name + ".forge.json"));
            if (File.Exists(candidate) && forgePaths.Add(candidate))
            {
                var doc = JsonDocument.Parse(File.ReadAllText(candidate)).RootElement;
                if (doc.TryGetProperty("schema", out var s) && s.GetString() == GauntletReport.Schema) forge.Add(doc);
            }
        }
        var outPath = o.Get("--out") ?? "report.html";
        var utf8 = new System.Text.UTF8Encoding(false);
        var now = DateTime.Now;

        // A page per deck in decks/ next to the report, with its fastest and worst games when its per-game file is there:
        // <run>.games.jsonl or <deck name>.games.jsonl next to its run JSON.
        var deckLinks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var deckDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".", "decks");
        foreach (var (run, path) in runs.Zip(runPaths))
        {
            var name = run.GetProperty("deck").GetProperty("name").GetString() ?? "deck";
            var dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
            var gamesPath = new[] { Path.ChangeExtension(Path.GetFullPath(path), ".games.jsonl"), Path.Combine(dir, name + ".games.jsonl") }
                .FirstOrDefault(File.Exists);
            var games = gamesPath is null ? new List<Core.Sim.GameRow>() : Core.Sim.GameRow.Read(File.ReadLines(gamesPath));
            JsonElement? deckForge = forge.Where(f => string.Equals(f.GetProperty("deck").GetProperty("name").GetString(), name,
                StringComparison.OrdinalIgnoreCase)).Select(f => (JsonElement?)f).FirstOrDefault();
            var file = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".html";
            Directory.CreateDirectory(deckDir);
            File.WriteAllText(Path.Combine(deckDir, file),
                Core.Sim.HtmlReport.BuildDeck(run, games, deckForge, now, "../" + Path.GetFileName(outPath)), utf8);
            deckLinks[name] = "decks/" + Uri.EscapeDataString(file);
        }

        File.WriteAllText(outPath, Core.Sim.HtmlReport.Build(runs, now, forge, deckLinks), utf8);
        Console.Error.WriteLine($"wrote {outPath}" + (deckLinks.Count > 0 ? $" and {deckLinks.Count} deck pages in {deckDir}" : ""));
        return Ok;
    }

    private static async Task<int> ForgeSetup(Options o)
    {
        o.RequirePositional(0);
        await ForgeApp.SetupAsync(o.Get("--dir") ?? ForgeApp.DefaultDir, o.Get("--version") ?? ForgeApp.DefaultVersion, Console.Error);
        return Ok;
    }

    private static int ForgeGauntlet(Options o)
    {
        var deckPath = o.RequirePositional(1)[0];
        if (!File.Exists(deckPath)) throw new FileNotFoundException($"deck not found: {deckPath}");
        int Int(string key, int fallback)
        {
            if (o.Get(key) is not { } v) return fallback;
            return int.TryParse(v, out var n) && n >= 1 ? n : throw new UsageException($"{key} must be an integer >= 1");
        }
        var options = new GauntletOptions { Games = Int("--games", 20), Parallel = LowPriority.Active ? OneCore(o, "--parallel") : Int("--parallel", 1),
            Priority = LowPriority.Active ? System.Diagnostics.ProcessPriorityClass.Idle : System.Diagnostics.ProcessPriorityClass.BelowNormal, TimeoutSeconds = Int("--timeout", 120) };
        var app = ForgeApp.Load(o.Get("--forge-app"));
        var opponents = Gauntlet.ResolveOpponents(deckPath, o.GetAll("--against"));
        if (opponents.Count == 0) throw new UsageException("no opponent decks found (a deck is a .txt with a *CMDR* line)");
        ForgeLibrary? scripts = null;
        try { scripts = ForgeLibrary.Load(o.Get("--forge")); }
        catch (FileNotFoundException) { Console.Error.WriteLine("Forge card scripts not found (`sim sync-forge`), so unknown cards aren't listed."); }
        Console.Error.WriteLine($"Forge {app.Version}: {Path.GetFileNameWithoutExtension(deckPath)} vs {opponents.Count} decks, " +
                                $"{options.Games} games each, {options.Parallel} at a time");
        var result = Gauntlet.Run(app, deckPath, opponents, options, scripts, Console.Error);
        foreach (var m in result.Opponents.Where(m => m.Error is not null)) Console.Error.WriteLine($"vs {m.Name}: {m.Error}");
        Emit(o, JsonSerializer.Serialize(GauntletReport.Document(result), JsonOut));
        return Ok;
    }

    /// <summary>--low runs everything on one core, so a larger --threads or --parallel is a mistake.</summary>
    private static int OneCore(Options o, string key) =>
        o.Get(key) is { } v && v != "1" ? throw new UsageException($"--low runs on one core, so {key} must be 1 (or left out)") : 1;

    private static int Replay(Options o)
    {
        var opts = RunOptionsFrom(o, single: true);
        if (o.Get("--game") is not { } gameText || !int.TryParse(gameText, out var game) || game < 0)
            throw new UsageException("replay needs --game <index>");
        var (deck, _) = LoadDeck(o);
        var log = new List<string>();
        var r = Runner.PlayOne(deck, opts, game, opts.Mulligans[0], opts.Seats[0], opts.Tables[0], log: log);
        foreach (var line in log) Console.WriteLine(line);
        Console.WriteLine();
        Console.WriteLine(r.Won ? $"WON on turn {r.WinTurn} ({r.WinLine})" : $"no win by turn {opts.MaxTurn} ({r.FailReason})");
        if (deck.Unsupported.Count > 0) Console.WriteLine($"unsupported (blank) cards in deck: {string.Join(", ", deck.Unsupported)}");
        return Ok;
    }

    private static string TierName(SupportTier t) => t switch
    {
        SupportTier.Scripted => "scripted",
        SupportTier.HandCoded => "hand_coded",
        _ => "unsupported",
    };

    private static void Emit(Options o, string json)
    {
        if (o.Get("--json") is { } path) File.WriteAllText(path, json + "\n");
        else Console.WriteLine(json);
    }

    private static int Fail(int code, string message)
    {
        Console.Error.WriteLine($"sim: {message}");
        return code;
    }

    private static int Usage(int code)
    {
        (code == Ok ? Console.Out : Console.Error).WriteLine("""
            usage: sim <command> [options]

              sync-cards [--out path]              download Scryfall oracle data into the card index
              check <deck.txt> [--cards path] [--json out.json]
                                                   resolve names and check the 100-card count

              sync-forge [--dir path]              download Forge card scripts (needs git)
              cards [--deck x.txt] [--top N] [--forge dir] [--cards path] [--json out.json]
                                                   card support: whole-pool coverage, per-deck tiers

              run <deck.txt> [--games N] [--seed S] [--mull A|B|C|all] [--seat play|draw|both]
                  [--table 1v1|1v1v1|both] [--max-turn T] [--rollouts R] [--threads N] [--json out.json] [--per-game games.jsonl]
              compare <base.txt> [--swap "Out=>In"]... [--variant "Out=>In[;Out=>In]"]... [<other.txt>...] [same options as run]
                                                   each variant vs the base on the same shuffles: deltas, intervals, McNemar
              batch <folder | deck.txt>... --out <folder> [same options as run] [--forge-games G] [--forge-opponents K]
                  [--forge-parallel P] [--timeout S]
                                                   every deck: runs with per-game results, optional Forge playtest, report + deck pages
              replay <deck.txt> --game I [--seed S] [--mull A|B|C] [--seat play|draw] [--table 1v1|1v1v1]
                  [--max-turn T]
              report <run.json | deck.forge.json> [...] [--out report.html]
                                                   HTML charts of run results, a page per deck (hands, plays) and Forge playtests

              forge-setup [--dir path] [--version V]   download Forge and Java 17 for the Forge playtest
              forge-gauntlet <deck.txt> [--against deck|dir|list.txt]... [--games N] [--parallel P]
                  [--timeout S] [--forge-app dir] [--json out.json]
                                                   play the deck against other decks with Forge's AI (1v1 Commander)

            Planned (see CONTRACT.md): compare.
            Any command: --low (or MAGICSIM_LOW=1) runs at idle priority on one core, one thread at a time.
            Card index path: --cards, else %MAGICSIM_CARDS%, else %LOCALAPPDATA%\MagicSim\oracle-cards.jsonl.gz
            """);
        return code;
    }
}
