using System.Text.Json.Serialization;

namespace MagicSim.Core.Cards;

/// <summary>
/// One oracle card as stored in the shared index (CONTRACT.md §2). Field names are
/// snake_case on disk so Python readers (MagicDecks) can consume the file directly.
/// </summary>
public sealed record CardData
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("layout")] public string Layout { get; init; } = "";
    [JsonPropertyName("mana_cost")] public string ManaCost { get; init; } = "";
    [JsonPropertyName("cmc")] public double Cmc { get; init; }
    [JsonPropertyName("type_line")] public string TypeLine { get; init; } = "";
    [JsonPropertyName("oracle_text")] public string OracleText { get; init; } = "";
    [JsonPropertyName("colors")] public List<string> Colors { get; init; } = new();
    [JsonPropertyName("color_identity")] public List<string> ColorIdentity { get; init; } = new();
    [JsonPropertyName("produced_mana")] public List<string> ProducedMana { get; init; } = new();
    [JsonPropertyName("keywords")] public List<string> Keywords { get; init; } = new();
    [JsonPropertyName("power")] public string? Power { get; init; }
    [JsonPropertyName("toughness")] public string? Toughness { get; init; }
    [JsonPropertyName("loyalty")] public string? Loyalty { get; init; }
    [JsonPropertyName("commander_legality")] public string CommanderLegality { get; init; } = "not_legal";
    [JsonPropertyName("faces")] public List<CardFace>? Faces { get; init; }
    /// <summary>EDHREC popularity rank (1 = most played), or null if unranked.</summary>
    [JsonPropertyName("edhrec_rank")] public int? EdhrecRank { get; init; }
}

public sealed record CardFace
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("mana_cost")] public string ManaCost { get; init; } = "";
    [JsonPropertyName("type_line")] public string TypeLine { get; init; } = "";
    [JsonPropertyName("oracle_text")] public string OracleText { get; init; } = "";
    [JsonPropertyName("power")] public string? Power { get; init; }
    [JsonPropertyName("toughness")] public string? Toughness { get; init; }
}
