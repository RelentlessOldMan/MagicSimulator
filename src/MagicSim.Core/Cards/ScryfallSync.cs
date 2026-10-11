using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MagicSim.Core.Cards;

/// <summary>Downloads Scryfall's oracle_cards bulk file and writes the shared card index.</summary>
public static class ScryfallSync
{
    private static readonly HashSet<string> JunkLayouts = new()
    {
        "art_series", "reversible_card", "token", "double_faced_token", "emblem",
        "planar", "scheme", "vanguard", "augment", "host",
    };

    public static async Task<int> RunAsync(string outPath, TextWriter log, CancellationToken ct = default)
    {
        using var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All });
        http.Timeout = TimeSpan.FromMinutes(10);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MagicSim/0.1 (personal deck simulator)");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        log.WriteLine("Fetching Scryfall bulk-data index ...");
        using var idx = JsonDocument.Parse(await http.GetStringAsync("https://api.scryfall.com/bulk-data", ct));
        var entry = idx.RootElement.GetProperty("data").EnumerateArray()
            .First(e => e.GetProperty("type").GetString() == "oracle_cards");
        // Scryfall has served both a JSON array (download_uri) and gzipped JSONL (jsonl_download_uri).
        var isJsonl = entry.TryGetProperty("jsonl_download_uri", out var jl) && jl.ValueKind == JsonValueKind.String;
        var uri = isJsonl ? jl.GetString()! : entry.GetProperty("download_uri").GetString()!;
        log.WriteLine($"Downloading {uri}");

        await using var body = await http.GetStreamAsync(uri, ct);
        await using var src = uri.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(body, CompressionMode.Decompress)
            : body;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        var tmp = outPath + ".tmp";
        var written = 0;
        await using (var file = File.Create(tmp))
        await using (var gz = new GZipStream(file, CompressionLevel.Optimal))
        await using (var w = new StreamWriter(gz, new System.Text.UTF8Encoding(false)) { NewLine = "\n" })
        {
            await foreach (var c in ReadCards(src, isJsonl, ct))
            {
                if (JunkLayouts.Contains(Str(c, "layout"))) continue;
                await w.WriteLineAsync(JsonSerializer.Serialize(Convert(c)));
                written++;
            }
        }
        File.Move(tmp, outPath, overwrite: true);
        log.WriteLine($"Wrote {written} cards to {outPath}");
        return written;
    }

    private static async IAsyncEnumerable<JsonElement> ReadCards(Stream src, bool isJsonl,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (!isJsonl)
        {
            await foreach (var c in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(src, cancellationToken: ct))
                yield return c;
            yield break;
        }
        using var reader = new StreamReader(src, System.Text.Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            yield return doc.RootElement.Clone();
        }
    }

    internal static CardData Convert(JsonElement c)
    {
        List<CardFace>? faces = null;
        if (c.TryGetProperty("card_faces", out var fs) && fs.ValueKind == JsonValueKind.Array)
            faces = fs.EnumerateArray().Select(f => new CardFace
            {
                Name = Str(f, "name"),
                ManaCost = Str(f, "mana_cost"),
                TypeLine = Str(f, "type_line"),
                OracleText = Str(f, "oracle_text"),
                Power = StrOrNull(f, "power"),
                Toughness = StrOrNull(f, "toughness"),
            }).ToList();

        // Multi-face cards keep cost/text on the faces; mirror the front face up top.
        var front = faces?.FirstOrDefault();
        var produced = Strs(c, "produced_mana");
        return new CardData
        {
            Name = Str(c, "name"),
            Layout = Str(c, "layout"),
            ManaCost = c.TryGetProperty("mana_cost", out _) ? Str(c, "mana_cost") : front?.ManaCost ?? "",
            Cmc = c.TryGetProperty("cmc", out var cmc) && cmc.ValueKind == JsonValueKind.Number ? cmc.GetDouble() : 0,
            TypeLine = Str(c, "type_line"),
            OracleText = c.TryGetProperty("oracle_text", out _)
                ? Str(c, "oracle_text")
                : string.Join("\n//\n", faces?.Select(f => f.OracleText) ?? Array.Empty<string>()),
            Colors = Strs(c, "colors"),
            ColorIdentity = Strs(c, "color_identity"),
            ProducedMana = produced,
            Keywords = Strs(c, "keywords"),
            Power = StrOrNull(c, "power") ?? front?.Power,
            Toughness = StrOrNull(c, "toughness") ?? front?.Toughness,
            Loyalty = StrOrNull(c, "loyalty"),
            CommanderLegality = c.TryGetProperty("legalities", out var leg) ? Str(leg, "commander") : "not_legal",
            Faces = faces,
            EdhrecRank = c.TryGetProperty("edhrec_rank", out var rank) && rank.ValueKind == JsonValueKind.Number ? rank.GetInt32() : null,
        };
    }

    private static string Str(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static string? StrOrNull(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static List<string> Strs(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
            : new List<string>();
}
