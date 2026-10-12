using MagicSim.Core.Engine;

namespace MagicSim.Core.Players;

public enum MulliganPolicy { A, B, C }

/// <summary>
/// The fast, randomized policy used inside search rollouts (and for decisions search leaves
/// alone). It picks options at random, weighted toward sensible play.
/// </summary>
public sealed class RolloutPolicy : ChoiceDecider
{
    private Rng _rng;
    public MulliganPolicy Mulligan { get; init; } = MulliganPolicy.C;

    public RolloutPolicy(ulong seed) => _rng = new Rng(seed);

    private static readonly HashSet<string> Harmful = new(StringComparer.Ordinal)
    {
        "DealDamage", "LoseLife", "Mill", "Discard", "Destroy", "Sacrifice", "Tap", "RevealHand", "LosesGame",
    };

    protected override int Pick(Game g, Decision d)
    {
        switch (d.Kind)
        {
            case DecisionKind.Keep: return KeepHand(g) ? 0 : 1;
            case DecisionKind.Attackers: return 0;
            case DecisionKind.Bottom: return BottomPick(g, d);
            case DecisionKind.Card when d.CardChoice == Choice.Discard && d.Source is null: return BottomPick(g, d);
        }
        var weights = new double[d.Options.Count];
        for (var i = 0; i < weights.Length; i++) weights[i] = Weight(g, d, d.Options[i]);
        return WeightedPick(weights);
    }

    private int WeightedPick(double[] w)
    {
        var total = w.Sum();
        if (total <= 0) return _rng.Next(w.Length);
        var r = _rng.NextDouble() * total;
        for (var i = 0; i < w.Length; i++)
        {
            r -= w[i];
            if (r < 0) return i;
        }
        return w.Length - 1;
    }

    private double Weight(Game g, Decision d, object? option)
    {
        var s = g.State;
        switch (d.Kind)
        {
            case DecisionKind.Action:
                return option switch
                {
                    GameAction.Pass => s.Stack.Count > 0 ? 6 : 1,
                    GameAction.PlayLand => 20,
                    GameAction.Cast c => CastWeight(s[c.CardId], c.SpellIndex),
                    GameAction.Activate a => ActivateWeight(s[a.CardId].Activated[a.AbilityIndex]),
                    _ => 1,
                };
            case DecisionKind.Card:
                if (option is null) return 0.3;
                var card = s[(int)option];
                return d.CardChoice switch
                {
                    Choice.Discard or Choice.Sacrifice or Choice.PutOnBottom or Choice.ExileFromGraveyard => card.Types.Is(CardType.Land) ? 2 : 1,
                    Choice.Copy => 1 + card.CurrentFace.ManaCost.ManaValue + (card.IsBlank ? 0 : 3),
                    _ => card.Types.Is(CardType.Land) ? 0.5 : 1,
                };
            case DecisionKind.Target:
                if (option is null) return 0.2;
                var t = (TargetRef)option;
                var harmful = d.Ability is not null && Harmful.Contains(d.Ability.Api);
                var mine = t.Player is { } p ? p == d.Player : s[t.CardId!.Value].Controller == d.Player;
                return harmful == mine ? 0.1 : 5;
            case DecisionKind.X:
                return (int)option! == (int)d.Options[0]! ? 4 : 1;
            case DecisionKind.YesNo:
                return (bool)option! ? 3 : 1;
            case DecisionKind.Name:
                return (string)option! == NotInDeck ? d.Options.Count : 1;
            case DecisionKind.Color:
                return 1 + ColorDemand(g, d.Player, (ManaColor)option!);
            default:
                return 1;
        }
    }

    private static double CastWeight(Card c, int spellIndex)
    {
        var face = c.CurrentFace;
        if (spellIndex < 0)
            return face.Activated.Any(a => a.Api == "Mana") ? 8 : 4;
        var api = face.SpellAbilities[spellIndex].Api;
        return api switch
        {
            "Mana" => 3,
            "Draw" or "Dig" or "ChangeZone" or "DigUntil" => 5,
            _ => 4,
        };
    }

    private static double ActivateWeight(AbilityDef ab)
    {
        if (ab.Api == "Mana") return ab.Cost.Parts.Count > 0 ? 1.5 : 3;
        if (ab.Api == "ChangeZone" && ab["Origin"] == "Library") return 10;   // fetchlands and the like
        return 3;
    }

    /// <summary>How many colored symbols of this color the hand's spells need.</summary>
    private static int ColorDemand(Game g, int player, ManaColor color)
    {
        var n = 0;
        foreach (var id in g.State.Players[player].Hand)
            foreach (var shard in g.State[id].CurrentFace.ManaCost.Shards)
                if ((shard.Colors & color.Mask()) != 0) n++;
        return n;
    }

    private int BottomPick(Game g, Decision d)
    {
        // Bottom the most expensive spell, or a land when the hand is land-heavy.
        var hand = g.State.Players[d.Player].Hand.Except(d.Picked).Select(id => g.State[id]).ToList();
        var lands = hand.Count(c => c.Types.Is(CardType.Land));
        var best = 0;
        var bestScore = double.MinValue;
        for (var i = 0; i < d.Options.Count; i++)
        {
            if (d.Options[i] is not int id) continue;
            var c = g.State[id];
            var score = c.Types.Is(CardType.Land) ? (lands > 3 ? 10 : -10) : c.CurrentFace.ManaCost.ManaValue + (c.IsBlank ? 5 : 0);
            if (score > bestScore) { bestScore = score; best = i; }
        }
        return best;
    }

    /// <summary>Mulligan policies B and C (A needs search; see <see cref="SearchDecider"/>).</summary>
    private bool KeepHand(Game g)
    {
        if (Mulligan == MulliganPolicy.C) return true;
        return SensibleHand(g);
    }

    /// <summary>
    /// Policy B: at least two mana sources and something to do with them: a tutor, a draw
    /// spell, or a spell it could cast.
    /// </summary>
    public static bool SensibleHand(Game g)
    {
        var hand = g.State.Players[Game.Me].Hand.Select(id => g.State[id]).ToList();
        if (hand.Count <= 5) return true;
        var mana = hand.Count(c => c.Types.Is(CardType.Land) || ProducesMana(c));
        var action = hand.Count(c => !c.Types.Is(CardType.Land) && !ProducesMana(c) && !c.IsBlank);
        return mana >= 2 && mana <= 5 && action >= 1;
    }

    public static bool ProducesMana(Card c) =>
        c.Activated.Any(a => a.Api == "Mana") || c.CurrentFace.SpellAbilities.Any(a => a.Api == "Mana");
}

/// <summary>Follows a fixed list of option indices, then hands over to another decider.</summary>
public sealed class ReplayDecider : ChoiceDecider
{
    private readonly IReadOnlyList<int> _script;
    private readonly ChoiceDecider _after;
    private readonly Action<Game>? _onLast;
    private int _next;

    /// <param name="onLast">Called just before the last scripted choice is returned (search uses it to determinize).</param>
    public ReplayDecider(IReadOnlyList<int> script, ChoiceDecider after, Action<Game>? onLast)
    {
        _script = script;
        _after = after;
        _onLast = onLast;
    }

    public bool Exhausted => _next >= _script.Count;

    protected override int Pick(Game g, Decision d)
    {
        if (_next < _script.Count)
        {
            var i = _script[_next++];
            if (_next == _script.Count) _onLast?.Invoke(g);
            return Math.Min(i, d.Options.Count - 1);
        }
        return _after.PickPublic(g, d);
    }
}
