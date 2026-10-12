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
            return args[0] switch
            {
                "sync-cards" => await SyncCards(opts),
                "check" => Check(opts),
                "sync-forge" => SyncForge(opts),
                "cards" => Cards(opts),
                "run" => Run(opts),
                "replay" => Replay(opts),
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
            return Fail(RunError, e.Message);
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
            Rollouts = Int("--rollouts", 2, 0), Threads = Int("--threads", Environment.ProcessorCount),
        };
    }

    private static (Runner.PreparedDeck Deck, string Path) LoadDeck(Options o)
    {
        var path = o.RequirePositional(1)[0];
        var deck = DeckReader.ReadFile(path);
        var pool = new CardPool(ForgeLibrary.Load(o.Get("--forge")), CardIndex.Load(o.Get("--cards")));
        return (Runner.Prepare(deck, pool), path);
    }

    private static int Run(Options o)
    {
        var opts = RunOptionsFrom(o);
        var (deck, path) = LoadDeck(o);
        var total = opts.Games * opts.Tables.Count * opts.Mulligans.Count * opts.Seats.Count;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var games = Runner.Run(deck, opts, progress: new Progress<int>(n =>
            Console.Error.Write($"\r{n}/{total} games ({sw.Elapsed.TotalSeconds:F0}s)")));
        var cpu = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
        Console.Error.WriteLine($"\r{total}/{total} games in {sw.Elapsed.TotalSeconds:F1}s (CPU {cpu.TotalSeconds:F1}s, allocated {GC.GetTotalAllocatedBytes() / 1e9:F1} GB)");
        if (o.Get("--per-game") is { } perGame)
            File.WriteAllLines(perGame, games.Select(Report.GameLine), new System.Text.UTF8Encoding(false));
        Emit(o, JsonSerializer.Serialize(Report.RunDocument(deck, path, opts, games), Report.Json));
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
        var html = Core.Sim.HtmlReport.Build(runs, DateTime.Now, forge);
        var outPath = o.Get("--out") ?? "report.html";
        File.WriteAllText(outPath, html, new System.Text.UTF8Encoding(false));
        Console.Error.WriteLine($"wrote {outPath}");
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
        var options = new GauntletOptions { Games = Int("--games", 20), Parallel = Int("--parallel", 1), TimeoutSeconds = Int("--timeout", 120) };
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
              replay <deck.txt> --game I [--seed S] [--mull A|B|C] [--seat play|draw] [--table 1v1|1v1v1]
                  [--max-turn T]
              report <run.json | deck.forge.json> [...] [--out report.html]
                                                   HTML charts of run results, plus Forge playtests if any

              forge-setup [--dir path] [--version V]   download Forge and Java 17 for the Forge playtest
              forge-gauntlet <deck.txt> [--against deck|dir|list.txt]... [--games N] [--parallel P]
                  [--timeout S] [--forge-app dir] [--json out.json]
                                                   play the deck against other decks with Forge's AI (1v1 Commander)

            Planned (see CONTRACT.md): compare.
            Card index path: --cards, else %MAGICSIM_CARDS%, else %LOCALAPPDATA%\MagicSim\oracle-cards.jsonl.gz
            """);
        return code;
    }
}
