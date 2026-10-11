using MagicSim.Core.Engine;

namespace MagicSim.Core.Players;

public sealed class SearchSettings
{
    /// <summary>Rollouts per option at each searched decision.</summary>
    public int Rollouts { get; init; } = 6;
    /// <summary>Mulligan policy A: how many determinized turn-1 tries a hand gets.</summary>
    public int KeepTrials { get; init; } = 3;
    public MulliganPolicy Mulligan { get; init; } = MulliganPolicy.C;
    /// <summary>Policy A keeps going down to this hand size, then keeps.</summary>
    public int MulliganFloor { get; init; } = 5;
}

/// <summary>
/// Chooses by lookahead without peeking at hidden information (CONTRACT.md §3, "No cheating").
///
/// At each searched decision it scores every option by rollouts: it clones the game as it was
/// when the current priority action was chosen, replays the real choices made since then to
/// reach this exact decision, re-shuffles the unknown part of the library with its own random
/// stream, takes the option, and plays out the rest of the turn with <see cref="RolloutPolicy"/>.
/// The option with the best average end-of-turn score wins.
/// </summary>
public sealed class SearchDecider : ChoiceDecider
{
    private readonly SearchSettings _settings;
    private readonly RolloutPolicy _fallback;
    private Rng _rng;
    private Game? _snapshot;
    private readonly List<int> _prefix = new();

    public SearchDecider(SearchSettings settings, ulong seed)
    {
        _settings = settings;
        _rng = new Rng(seed);
        _fallback = new RolloutPolicy(_rng.NextULong()) { Mulligan = settings.Mulligan };
    }

    protected override int Pick(Game g, Decision d)
    {
        if (d.Kind == DecisionKind.Keep) return KeepHand(g) ? 0 : 1;

        if (d.Kind == DecisionKind.Action)
        {
            _snapshot = g.Clone();
            _snapshot.Log = null;
            _prefix.Clear();
        }
        else if (!g.InPriorityAction || _snapshot is null)
        {
            return _fallback.PickPublic(g, d);   // outside a replayable window
        }

        int choice;
        if (!d.Searchable || d.Options.Count == 1) choice = _fallback.PickPublic(g, d);
        else choice = Best(g, d);
        _prefix.Add(choice);
        return choice;
    }

    private int Best(Game g, Decision d)
    {
        // Every option is tried against the same K random futures (paired), so differences
        // between options come from the options, not from luck.
        var seeds = new (ulong Policy, ulong World)[_settings.Rollouts];
        for (var k = 0; k < seeds.Length; k++) seeds[k] = (_rng.NextULong(), _rng.NextULong());
        var best = 0;
        var bestScore = double.NegativeInfinity;
        var scores = new double[d.Options.Count];
        for (var i = 0; i < d.Options.Count; i++)
        {
            var total = 0.0;
            foreach (var (policySeed, worldSeed) in seeds) total += Rollout(i, policySeed, worldSeed);
            var score = scores[i] = total / seeds.Length + Bias(d.Options[i]);
            if (score > bestScore) { bestScore = score; best = i; }
        }
        if (g.Log is not null)
            g.Note("  search " + d.Kind + ": " + string.Join(" | ", Enumerable.Range(0, scores.Length)
                .OrderByDescending(i => scores[i]).Take(6).Select(i => $"{Describe(g, d.Options[i])} {scores[i]:F1}")));
        return best;
    }

    public static string Describe(Game g, object? option) => option switch
    {
        null => "done",
        GameAction.Pass => "pass",
        GameAction.PlayLand pl => "play " + g.State[pl.CardId].Name,
        GameAction.Cast c => "cast " + g.State[c.CardId].Name,
        GameAction.Activate a => "activate " + g.State[a.CardId].Name,
        int id when id >= 0 && id < g.State.Cards.Count => g.State[id].Name,
        TargetRef t => t.ToString(),
        _ => option.ToString() ?? "?",
    };

    /// <summary>Small tie-breakers: do useful things now rather than later in the turn.</summary>
    private static double Bias(object? option) => option switch
    {
        GameAction.PlayLand => 0.5,
        GameAction.Pass => -0.1,
        _ => 0,
    };

    private double Rollout(int option, ulong rolloutSeed, ulong worldSeed)
    {
        var script = new List<int>(_prefix) { option };
        var determinizeRng = new Rng(worldSeed);
        var policy = new RolloutPolicy(rolloutSeed);
        var replay = new ReplayDecider(script, policy, game => game.Determinize(Game.Me, ref determinizeRng));
        var sim = _snapshot!.Clone(replay);
        // The player could stop at any main-phase checkpoint, so a rollout is worth the best
        // checkpoint it reached (after its scripted choices), not just where random play ended up.
        var best = double.NegativeInfinity;
        sim.OnCheckpoint = game => { if (replay.Exhausted) best = Math.Max(best, Evaluate(game)); };
        sim.ResumeTurn();
        return Math.Max(best, Evaluate(sim));
    }

    /// <summary>End-of-turn score: winning dwarfs everything; otherwise resources and damage.</summary>
    public static double Evaluate(Game g)
    {
        var s = g.State;
        if (s.GameOver) return s.Winner == Game.Me ? 1e6 : -1e6;
        var me = s.Players[Game.Me];
        double oppDamage = 0;
        foreach (var p in s.Players)
            if (p.Index != Game.Me)
                oppDamage += p.Lost ? g.Settings.StartingLife * 1.5 : g.Settings.StartingLife - Math.Max(0, p.Life);
        double manaSources = 0, power = 0, otherPermanents = 0;
        foreach (var c in s.BattlefieldCards)
        {
            if (c.Controller != Game.Me) continue;
            if (RolloutPolicy.ProducesMana(c) || c.Types.Is(CardType.Land))
                manaSources += IsOneShotMana(c) ? 0.35 : 1;   // Lotus Petal-style sources are spent once
            else if (c.Types.Is(CardType.Creature)) power += Math.Max(0, c.Power);
            else otherPermanents++;
        }
        var hand = me.Hand.Sum(id => s[id].Def.IsBlank ? 0.3 : 1.0);
        var deckRisk = me.Library.Count < 5 ? (5 - me.Library.Count) * 20 : 0;
        return 4 * oppDamage + 12 * manaSources + 6 * hand + 3 * power + 4 * otherPermanents + 0.5 * Math.Min(me.Life, 40) - deckRisk;
    }

    private static bool IsOneShotMana(Card c) =>
        c.CurrentFace.Activated.Where(ab => ab.Api == "Mana").All(ab => ab.Cost.Parts.Any(p => p.Kind == CostPartKind.Sacrifice && p.Valid == "CARDNAME"));

    /// <summary>
    /// Mulligan policy A (T1-or-bust): keep only if, in at least one of a few determinized tries,
    /// search wins on turn 1 with this hand. Policies B and C use the rollout policy's rules.
    /// </summary>
    private bool KeepHand(Game g)
    {
        if (_settings.Mulligan != MulliganPolicy.A) return _fallback.PickPublic(g, new Decision { Kind = DecisionKind.Keep, Options = new object?[] { true, false } }) == 0;
        if (g.State.Players[Game.Me].Hand.Count - Math.Max(0, g.MulligansTaken - (g.Settings.FreeFirstMulligan ? 1 : 0)) <= _settings.MulliganFloor)
            return true;
        for (var t = 0; t < _settings.KeepTrials; t++)
        {
            var trialRng = new Rng(_rng.NextULong());
            var inner = new SearchDecider(new SearchSettings { Rollouts = Math.Max(2, _settings.Rollouts / 2), Mulligan = MulliganPolicy.C }, _rng.NextULong());
            var sim = g.Clone(inner);
            sim.Log = null;
            sim.Determinize(Game.Me, ref trialRng);
            sim.KeepCurrentHand();
            sim.RunTurn();
            if (sim.Won) return true;
        }
        return false;
    }
}
