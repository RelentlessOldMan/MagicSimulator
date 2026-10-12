using MagicSim.Core.Engine;

namespace MagicSim.Core.Players;

public sealed class SearchSettings
{
    /// <summary>Rollouts per option at each searched decision.</summary>
    public int Rollouts { get; init; } = 2;
    /// <summary>Outside the main phases, with nothing on the stack, just pass (a goldfish has nothing to answer there).</summary>
    public bool QuietSteps { get; init; } = true;
    /// <summary>Mulligan policy A: how many determinized turn-1 tries a hand gets.</summary>
    public int KeepTrials { get; init; } = 3;
    public MulliganPolicy Mulligan { get; init; } = MulliganPolicy.C;
    /// <summary>Policy A keeps going down to this hand size, then keeps.</summary>
    public int MulliganFloor { get; init; } = 5;
}

/// <summary>
/// Chooses by lookahead without peeking at hidden information (CONTRACT.md Â§3, "No cheating").
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

    public override bool PassesQuietSteps => _settings.QuietSteps;

    protected override int Pick(Game g, Decision d)
    {
        if (d.Kind == DecisionKind.Keep) return KeepHand(g) ? 0 : 1;

        if (d.Kind == DecisionKind.Action)
        {
            _snapshot = g.Clone(keepRecord: false);
            _snapshot.Log = null;
            _prefix.Clear();
        }
        else if (!g.InPriorityAction || _snapshot is null)
        {
            return _fallback.PickPublic(g, d);   // outside a replayable window
        }

        int choice;
        // Deep into a turn the game is going round a loop: searching every step of it costs more and more as the board fills.
        if (!d.Searchable || d.Options.Count == 1 || _settings.Rollouts == 0 || g.ActionsThisTurn >= RolloutActions) choice = _fallback.PickPublic(g, d);
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
        var n = d.Options.Count;
        var total = new double[n];
        var count = new int[n];
        double Score(int i) => count[i] == 0 ? double.NegativeInfinity : total[i] / count[i] + Bias(d.Options[i]);
        bool Won(int i)
        {
            var r = Rollout(i, seeds[count[i]].Policy, seeds[count[i]].World);
            total[i] += r;
            count[i]++;
            return r >= WinScore;
        }
        // Nothing beats winning in every future tried: an option that wins its first rollout gets the rest at once.
        bool SureWin(int i)
        {
            while (count[i] < seeds.Length)
                if (!Won(i)) return false;
            return true;
        }

        // Successive halving: every option gets one rollout, then only the better half goes on to the next,
        // so the rollouts go to the close calls rather than to options that are clearly behind.
        var alive = Enumerable.Range(0, n).ToList();
        var best = -1;
        for (var k = 0; k < seeds.Length && best < 0; k++)
        {
            foreach (var i in alive)
                if (count[i] == k && Won(i) && SureWin(i)) { best = i; break; }
            if (best < 0 && k < seeds.Length - 1)
                alive = alive.OrderByDescending(Score).ThenBy(i => i).Take(Math.Max(2, (alive.Count + 1) / 2)).ToList();
        }
        if (best < 0) best = alive.OrderByDescending(Score).ThenBy(i => i).First();
        if (g.Log is not null)
            g.Note("  search " + d.Kind + ": " + string.Join(" | ", Enumerable.Range(0, n).Where(i => count[i] > 0)
                .OrderByDescending(i => i == best).ThenByDescending(Score).Take(6).Select(i => $"{Describe(g, d.Options[i])} {Score(i):F1}")));
        return best;
    }

    public static string Describe(Game g, object? option) => option switch
    {
        null => "done",
        GameAction.Pass => "pass",
        GameAction.PlayLand pl => "play " + g.State[pl.CardId].Def.Faces[pl.Face].Name,
        GameAction.Cast c => "cast " + g.State[c.CardId].Def.Faces[c.Face].Name,
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
        var policy = new RolloutPolicy(rolloutSeed) { QuietStepsPass = true };
        var replay = new ReplayDecider(script, policy, game => game.Determinize(Game.Me, ref determinizeRng));
        var sim = _snapshot!.Clone(replay, keepRecord: false);
        sim.ActionLimit = sim.ActionsThisTurn + RolloutActions;
        // The player could stop at any main-phase checkpoint, so a rollout is worth the best
        // checkpoint it reached (after its scripted choices), not just where random play ended up.
        var best = double.NegativeInfinity;
        sim.OnCheckpoint = game => { if (replay.Exhausted) best = Math.Max(best, Evaluate(game)); };
        sim.ResumeTurn();
        return Math.Max(best, Evaluate(sim));
    }

    private const double WinScore = 1e6;

    /// <summary>
    /// How many actions a rollout may take. Random play that runs this long is almost always going round a loop
    /// (sacrifice, return, sacrifice…), and every step of it costs more as the board fills with tokens.
    /// </summary>
    private const int RolloutActions = 100;

    /// <summary>End-of-turn score: winning dwarfs everything; otherwise resources and damage.</summary>
    public static double Evaluate(Game g)
    {
        var s = g.State;
        if (s.GameOver) return s.Winner == Game.Me ? WinScore : -WinScore;
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
            else
            {
                // A card working on the battlefield is worth more than the same card in hand.
                otherPermanents++;
                if (c.Types.Is(CardType.Creature)) power += Math.Max(0, c.Power);
            }
        }
        var hand = me.Hand.Sum(id => s[id].IsBlank ? 0.3 : 1.0);
        var deckRisk = me.Library.Count < 5 ? (5 - me.Library.Count) * 20 : 0;
        // An extra turn still to come is worth about a turn's worth of development.
        var extraTurns = 30 * s.ExtraTurns;
        return 4 * oppDamage + 12 * manaSources + 5 * hand + 3 * power + 7 * otherPermanents + 0.5 * Math.Min(me.Life, 40) - deckRisk + extraTurns;
    }

    private static bool IsOneShotMana(Card c) =>
        c.Activated.Where(ab => ab.Api == "Mana").All(ab => ab.Cost.Parts.Any(p => p.Kind == CostPartKind.Sacrifice && p.Valid == "CARDNAME"));

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
            var sim = g.Clone(inner, keepRecord: false);
            sim.Log = null;
            sim.Determinize(Game.Me, ref trialRng);
            sim.KeepCurrentHand();
            sim.RunTurn();
            if (sim.Won) return true;
        }
        return false;
    }
}
