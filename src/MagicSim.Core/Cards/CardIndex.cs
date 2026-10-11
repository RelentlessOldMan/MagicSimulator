using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace MagicSim.Core.Cards;

/// <summary>
/// Name lookup over the shared card file: gzip JSONL, one <see cref="CardData"/> per line.
/// Names match case-insensitively with accents stripped, and a two-faced or split card
/// also matches on each face name.
/// </summary>
public sealed class CardIndex
{
    public const string PathEnvVar = "MAGICSIM_CARDS";

    private readonly Dictionary<string, CardData> _byName = new();

    public int Count { get; private set; }

    /// <summary>%MAGICSIM_CARDS%, else %LOCALAPPDATA%\MagicSim\oracle-cards.jsonl.gz.</summary>
    public static string DefaultPath =>
        Environment.GetEnvironmentVariable(PathEnvVar) is { Length: > 0 } p
            ? p
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MagicSim", "oracle-cards.jsonl.gz");

    public static CardIndex Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
            throw new FileNotFoundException($"Card index not found at {path}. Run `sim sync-cards` first.", path);
        using var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        return FromJsonl(gz);
    }

    public static CardIndex FromJsonl(Stream stream)
    {
        var index = new CardIndex();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            index.Add(JsonSerializer.Deserialize<CardData>(line)!);
        }
        return index;
    }

    public static CardIndex FromCards(IEnumerable<CardData> cards)
    {
        var index = new CardIndex();
        foreach (var c in cards) index.Add(c);
        return index;
    }

    private void Add(CardData card)
    {
        Count++;
        AddKey(card.Name, card);
        if (card.Faces is not null)
            foreach (var f in card.Faces)
                AddKey(f.Name, card);
    }

    private void AddKey(string name, CardData card)
    {
        var key = Normalize(name);
        // A real Commander card wins over a same-named not_legal oddity.
        if (_byName.TryGetValue(key, out var old) && old.CommanderLegality != "not_legal" && card.CommanderLegality == "not_legal")
            return;
        _byName[key] = card;
    }

    public CardData? Find(string name) =>
        _byName.TryGetValue(Normalize(name), out var c) ? c : null;

    /// <summary>Lowercase, accents stripped, whitespace trimmed (matches MagicDecks' norm()).</summary>
    public static string Normalize(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        return sb.ToString().Trim().ToLowerInvariant();
    }
}
