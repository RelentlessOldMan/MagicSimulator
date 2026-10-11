using System.Text.Json;
using MagicSim.Core.Cards;
using MagicSim.Core.Decks;

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

            Planned (see CONTRACT.md): run, compare, cards.
            Card index path: --cards, else %MAGICSIM_CARDS%, else %LOCALAPPDATA%\MagicSim\oracle-cards.jsonl.gz
            """);
        return code;
    }
}
