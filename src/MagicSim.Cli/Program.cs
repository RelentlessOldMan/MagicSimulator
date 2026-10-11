using System.Text.Json;
using MagicSim.Core.Cards;
using MagicSim.Core.Decks;
using MagicSim.Core.Forge;
using MagicSim.Core.Engine;

namespace MagicSim.Cli;

/// <summary>`sim` entry point. Exit codes per CONTRACT.md §3: 0 ok, 1 run error, 2 usage error.</summary>
public static class Program
{
    public const int Ok = 0, RunError = 1, UsageError = 2;

    private static readonly JsonSerializerOptions JsonOut = new() { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
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
        var support = Support.Current;

        // The pool is every Commander-legal (or banned) card Forge has a script for.
        var pool = forge.Cards.Where(c => index.Find(c.FullName) is { CommanderLegality: "legal" or "banned" }).ToList();
        var report = Coverage.Analyze(pool, support);

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
                    var tier = support.TierOf(fc, out var missing);
                    return new { name = e.Name, tier = TierName(tier), missing };
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
            deck = deckPart,
        }, JsonOut));
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
              cards [--deck x.txt] [--forge dir] [--cards path] [--json out.json]
                                                   card support: whole-pool coverage, per-deck tiers

            Planned (see CONTRACT.md): run, compare, replay.
            Card index path: --cards, else %MAGICSIM_CARDS%, else %LOCALAPPDATA%\MagicSim\oracle-cards.jsonl.gz
            """);
        return code;
    }
}
