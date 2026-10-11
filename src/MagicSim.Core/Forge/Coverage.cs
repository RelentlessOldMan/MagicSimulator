namespace MagicSim.Core.Forge;

public enum SupportTier { Scripted, HandCoded, Unsupported }

/// <summary>What the engine can run. Filled in as effect types, triggers, etc. are implemented.</summary>
public sealed class EngineSupport
{
    public HashSet<string> Requirements { get; } = new(StringComparer.Ordinal);
    public HashSet<string> HandCodedCards { get; } = new(StringComparer.OrdinalIgnoreCase);

    public SupportTier TierOf(ForgeCard card, out List<string> missing)
    {
        missing = new List<string>();
        if (HandCodedCards.Contains(card.FullName)) return SupportTier.HandCoded;
        foreach (var r in ForgeRequirements.For(card))
            if (!Requirements.Contains(r)) missing.Add(r);
        return missing.Count == 0 ? SupportTier.Scripted : SupportTier.Unsupported;
    }
}

public sealed record Blocker(string Requirement, int CardsNeeding, int CardsUnlockedAlone);

public sealed record CoverageReport(int Cards, int Scripted, int HandCoded, IReadOnlyList<Blocker> TopBlockers)
{
    public int Supported => Scripted + HandCoded;
    public double Share => Cards == 0 ? 0 : (double)Supported / Cards;
}

public static class Coverage
{
    /// <summary>
    /// Coverage over a card pool. TopBlockers ranks missing requirements by how many cards
    /// they alone block (implementing that one thing would make the card supported), then by
    /// how many cards need them at all — i.e. what to implement next.
    /// </summary>
    public static CoverageReport Analyze(IEnumerable<ForgeCard> pool, EngineSupport support, int top = 30)
    {
        int cards = 0, scripted = 0, hand = 0;
        var needing = new Dictionary<string, int>();
        var alone = new Dictionary<string, int>();
        foreach (var card in pool)
        {
            cards++;
            switch (support.TierOf(card, out var missing))
            {
                case SupportTier.Scripted: scripted++; break;
                case SupportTier.HandCoded: hand++; break;
                default:
                    foreach (var m in missing) needing[m] = needing.GetValueOrDefault(m) + 1;
                    if (missing.Count == 1) alone[missing[0]] = alone.GetValueOrDefault(missing[0]) + 1;
                    break;
            }
        }
        var blockers = needing
            .Select(kv => new Blocker(kv.Key, kv.Value, alone.GetValueOrDefault(kv.Key)))
            .OrderByDescending(b => b.CardsUnlockedAlone).ThenByDescending(b => b.CardsNeeding).ThenBy(b => b.Requirement, StringComparer.Ordinal)
            .Take(top)
            .ToList();
        return new CoverageReport(cards, scripted, hand, blockers);
    }
}
