namespace MagicSim.Core.Engine;

public sealed class GameSettings
{
    public int StartingLife { get; init; } = 40;
    public int Opponents { get; init; } = 2;
    public int OpponentLibrary { get; init; } = 99;
    public bool OnThePlay { get; init; } = true;
    public int MaxTurn { get; init; } = 20;
    public int StartingHand { get; init; } = 7;
    public bool FreeFirstMulligan { get; init; } = true;
    public int MaxHandSize { get; init; } = 7;
    /// <summary>Safety valve: a turn with more actions than this is cut short (loops).</summary>
    public int MaxActionsPerTurn { get; init; } = 400;
}

/// <summary>
/// The rules engine. One simulated player (index 0) plays against goldfish opponents that
/// never act. Every choice goes through <see cref="Decider"/>. <see cref="Clone"/> copies the
/// whole game for search.
/// </summary>
public sealed partial class Game
{
    public const int Me = 0;

    public GameState State { get; private set; }
    public GameSettings Settings { get; }
    public IDecider Decider { get; set; }
    /// <summary>Turn-by-turn log for `sim replay`; null when not wanted (search clones, bulk runs).</summary>
    public List<string>? Log { get; set; }

    private readonly List<PendingTrigger> _pending = new();

    /// <summary>
    /// True while carrying out the action just chosen in the priority loop (casting, paying,
    /// resolving). Search only explores sub-decisions made in this window; it can reproduce them
    /// by replaying from the snapshot taken at that ChooseAction.
    /// </summary>
    public bool InPriorityAction { get; private set; }

    /// <summary>
    /// Called when the player could stop doing things this turn (main phase, empty stack).
    /// Search rollouts use it to score the best point they reached.
    /// </summary>
    public Action<Game>? OnCheckpoint { get; set; }
    private int _actionsThisTurn;
    /// <summary>Whether continuous effects were in force at the last <see cref="Statics.Apply"/> (lets it skip work when there are none).</summary>
    public bool StaticsApplied { get; set; }
    /// <summary>A static gave cards off the battlefield a color (Painter's Servant): cleared when statics are applied again.</summary>
    public bool OffFieldColors { get; set; }

    /// <summary>Actions taken so far this turn (capped by <see cref="GameSettings.MaxActionsPerTurn"/> and <see cref="ActionLimit"/>).</summary>
    public int ActionsThisTurn => _actionsThisTurn;

    /// <summary>Stop acting this turn once <see cref="ActionsThisTurn"/> reaches this (search rollouts use it so a loop can't run on).</summary>
    public int ActionLimit { get; set; } = int.MaxValue;

    /// <summary>
    /// Past the action limit: the player only passes, and declines anything optional ("you may"), the way a
    /// player stops a loop that isn't getting anywhere.
    /// </summary>
    public bool PastActionLimit => _actionsThisTurn >= Math.Min(Settings.MaxActionsPerTurn, ActionLimit);

    /// <summary>Resolving goes on this long past the action limit only when nothing can stop it: a mandatory loop, a draw (rule 104.4b).</summary>
    private const int MandatoryLoopActions = 200;

    public Game(GameState state, GameSettings settings, IDecider decider)
    {
        State = state;
        Settings = settings;
        Decider = decider;
    }

    /// <param name="keepRecord">False for search copies, which don't report what happened.</param>
    /// <summary>A copy made for search: nothing about it is reported, so bookkeeping for reports is skipped.</summary>
    public bool IsSearchCopy { get; private init; }

    public Game Clone(IDecider? decider = null, bool keepRecord = true)
    {
        var g = new Game(State.Clone(keepRecord), Settings, decider ?? Decider)
        {
            _actionsThisTurn = _actionsThisTurn, MulligansTaken = MulligansTaken, StaticsApplied = StaticsApplied, OffFieldColors = OffFieldColors, IsSearchCopy = !keepRecord,
        };
        g._pending.AddRange(_pending);
        return g;
    }

    public void Note(string text) => Log?.Add(State.Turn == 0 ? $"pregame: {text}" : $"T{State.Turn} {State.Step,-16} {text}");

    // ---- setup ------------------------------------------------------------------------

    /// <summary>A new goldfish game: shuffled library, commanders in the command zone, no hand yet.</summary>
    public static Game NewGoldfish(IReadOnlyList<CardDef> library, IReadOnlyList<CardDef> commanders, GameSettings settings, Rng rng, IDecider decider)
    {
        var s = new GameState { Rng = rng, Turn = 0, Step = Step.Untap };
        var players = new List<Player> { new() { Index = Me, Life = settings.StartingLife } };
        for (var i = 1; i <= settings.Opponents; i++)
            players.Add(new Player { Index = i, Life = settings.StartingLife, IsGoldfish = true, AbstractLibrary = settings.OpponentLibrary,
                AbstractHand = Player.GoldfishStartingHand });
        s.Players = players.ToArray();

        foreach (var def in commanders)
        {
            var c = new Card { Id = s.Cards.Count, Def = def, Owner = Me, Controller = Me, Zone = Zone.Command, IsCommander = true };
            s.Cards.Add(c);
            s.Players[Me].Command.Add(c.Id);
        }
        foreach (var def in library)
        {
            var c = new Card { Id = s.Cards.Count, Def = def, Owner = Me, Controller = Me, Zone = Zone.Library };
            s.Cards.Add(c);
            s.Players[Me].Library.Add(c.Id);
        }
        s.Rng.Shuffle(s.Players[Me].Library);
        return new Game(s, settings, decider);
    }

    // ---- the whole game ------------------------------------------------------------------

    public int MulligansTaken { get; private set; }
    public List<string> KeptHand { get; } = new();
    public List<string> Bottomed { get; } = new();

    /// <summary>London mulligan (rule 103.5), first one free in multiplayer (103.5c).</summary>
    public void Mulligans()
    {
        var me = State.Players[Me];
        while (true)
        {
            Draw(Me, Settings.StartingHand, record: false);
            var mustKeep = Settings.StartingHand - BottomCount(MulligansTaken + 1) <= 0;
            if (mustKeep || Decider.KeepHand(this, Me, MulligansTaken)) break;
            MulligansTaken++;
            Note($"mulligan #{MulligansTaken}");
            foreach (var id in me.Hand.ToList()) MoveTo(State[id], Zone.Library, position: -1);
            State.Rng.Shuffle(me.Library);
        }
        KeepCurrentHand();
    }

    /// <summary>Keeps the hand in hand: bottoms cards for the mulligans taken, records what was kept.</summary>
    public void KeepCurrentHand()
    {
        var me = State.Players[Me];
        var bottom = BottomCount(MulligansTaken);
        if (bottom > 0)
        {
            var chosen = Decider.ChooseCards(this, Me, Choice.Bottom, null, me.Hand.ToList(), bottom, bottom);
            foreach (var id in chosen)
            {
                Bottomed.Add(State[id].Name);
                MoveTo(State[id], Zone.Library, position: -1);
            }
        }
        foreach (var id in me.Hand)
        {
            KeptHand.Add(State[id].Name);
            State.Record.Seen.Add(State[id].Def.Name);
        }
        Note($"kept {me.Hand.Count}: {string.Join(", ", KeptHand)}" + (Bottomed.Count > 0 ? $" (bottomed {string.Join(", ", Bottomed)})" : ""));
        // "If this card is in your opening hand, you may begin the game with it on the battlefield" (Leylines).
        foreach (var id in me.Hand.ToList())
            if (State[id].CurrentFace.OpeningHand is { } opening && !State[id].IsBlank
                && Decider.ChooseYesNo(this, Me, State[id], $"use {State[id].Name} from your opening hand?"))
                RunChain(new StackItem { SourceId = id, Ability = opening, Controller = Me, Description = State[id].Name + " (opening hand)" });
    }

    private int BottomCount(int mulligans) => Math.Max(0, Settings.FreeFirstMulligan ? mulligans - 1 : mulligans);

    /// <summary>Plays turns until someone wins or <see cref="GameSettings.MaxTurn"/> is reached.</summary>
    public void PlayGame()
    {
        Mulligans();
        while (!State.GameOver && State.Round < Settings.MaxTurn)
        {
            RunTurn();
            RunExtraTurns();
            RunOpponentTurns();
        }
    }

    /// <summary>
    /// Each goldfish opponent's turn, in which they do nothing but still have one (CONTRACT.md §3): "at the beginning
    /// of each opponent's upkeep" and "each end step" trigger, they draw a card, and they discard down to 7 at cleanup.
    /// The simulated player doesn't act in them; their triggers resolve.
    /// </summary>
    public void RunOpponentTurns()
    {
        foreach (var p in State.Players)
        {
            if (State.GameOver) break;
            if (p.Index == Me || p.Lost) continue;
            State.ActivePlayer = p.Index;
            State.ThisTurn = new TurnInfo();
            State.Attackers.Clear();
            Log?.Add($"--- opponent {p.Index}'s turn ---");
            foreach (var step in new[] { Step.Upkeep, Step.Draw, Step.End })
            {
                SetStep(step);
                if (step == Step.Draw) Draw(p.Index, 1);
                if (step == Step.End && State.Monarch == p.Index) Draw(p.Index, 1);
                FirePhase(step == Step.End ? "End of Turn" : step.ToString());
                ResolveWithoutActing();
                if (State.GameOver) break;
            }
            SetStep(Step.Cleanup);
            p.AbstractHand = Math.Min(p.AbstractHand, p.MaxHandSizeWith(Settings.MaxHandSize));
            EndOfTurnCleanup();
            FirePhase("Cleanup");
            ResolveWithoutActing();
        }
        State.ActivePlayer = Me;
    }

    /// <summary>Triggers go on the stack and everything resolves, with the simulated player passing.</summary>
    private void ResolveWithoutActing()
    {
        for (var n = 0; !State.GameOver; n++)
        {
            CheckStateBasedActions();
            if (State.GameOver) return;
            if (n >= Settings.MaxActionsPerTurn)
            {
                Note("the stack never empties: a mandatory loop, so the game is a draw");
                State.GameOver = true;
                State.Winner = null;
                State.WinReason = null;
                return;
            }
            if (_pending.Count > 0) PutTriggersOnStack();
            else if (State.Stack.Count > 0) ResolveTop();
            else return;
        }
    }

    /// <summary>Steps of a turn in order. Each runs its start-of-step actions, then priority (except untap and cleanup).</summary>
    private static readonly Step[] TurnSteps =
    {
        Step.Untap, Step.Upkeep, Step.Draw, Step.Main1, Step.BeginCombat, Step.DeclareAttackers, Step.CombatDamage,
        Step.EndCombat, Step.Main2, Step.End, Step.Cleanup,
    };

    /// <summary>Takes any extra turns due: right after this one, before the opponents' turns, in the same round.</summary>
    public void RunExtraTurns()
    {
        while (!State.GameOver && State.ExtraTurns > 0)
        {
            State.ExtraTurns--;
            State.ExtraTurnsTaken++;
            RunTurn();
        }
    }

    public void RunTurn()
    {
        State.Turn++;
        State.ThisTurn = new TurnInfo();
        State.Players[Me].LandsPlayedThisTurn = 0;
        State.Attackers.Clear();
        _actionsThisTurn = 0;
        State.Step = Step.Untap;
        Log?.Add($"--- turn {State.Turn} ---");
        RunStepsFrom(0);
    }

    /// <summary>
    /// Finishes the current turn from inside a priority loop: used by search on a cloned game
    /// that was snapshotted mid-step.
    /// </summary>
    public void ResumeTurn()
    {
        PriorityLoop();
        var current = State.Step;
        RunExtraPhasesAfter(current);
        RunStepsFrom(Array.IndexOf(TurnSteps, current) + 1);
    }

    private void RunStepsFrom(int index)
    {
        for (var i = index; i < TurnSteps.Length && !State.GameOver; i++)
        {
            if (State.ThisTurn.Ended && TurnSteps[i] != Step.Cleanup) continue;
            ExecuteStep(TurnSteps[i]);
            RunExtraPhasesAfter(TurnSteps[i]);
        }
        if (State.Step == Step.Cleanup || State.GameOver) EndTurnBookkeeping();
    }

    private void EndTurnBookkeeping()
    {
        if (State.Record.ManaByTurn.Count >= State.Turn) return;
        State.Record.ManaByTurn.Add(State.ThisTurn.MaxManaSeen);
        State.Record.StormMax = Math.Max(State.Record.StormMax, State.ThisTurn.SpellsCast);
    }

    /// <summary>Additional phases set up to follow <paramref name="after"/> (extra combats, rule 505.1a).</summary>
    private void RunExtraPhasesAfter(Step after)
    {
        while (!State.GameOver && State.ThisTurn.ExtraPhases.FindIndex(p => p.After == after) is var i and >= 0)
        {
            var steps = State.ThisTurn.ExtraPhases[i].Steps;
            State.ThisTurn.ExtraPhases.RemoveAt(i);
            Note("additional phase: " + string.Join(", ", steps));
            foreach (var step in steps)
            {
                if (State.GameOver || State.ThisTurn.Ended) break;
                ExecuteStep(step);
                RunExtraPhasesAfter(step);
            }
        }
    }

    private void ExecuteStep(Step step)
    {
        SetStep(step);
        // "Skip your draw step" (Necropotence).
        if (Replacements.SkipsStep(this, State.ActivePlayer, step))
        {
            Note($"skip {step}");
            return;
        }
        switch (step)
        {
            case Step.Untap:
                PhaseIn();
                ExpireEffects("untap");
                HashSet<int>? held = null;
                foreach (var c in State.BattlefieldCards)
                    if (c.AttachedTo >= 0 && c.CurrentFace.AttachedDoesntUntap) (held ??= new()).Add(c.AttachedTo);
                var untapping = new List<Card>();
                foreach (var c in State.BattlefieldCards)
                    if (c.Controller == Me && !c.CurrentFace.DoesntUntap && !(held?.Contains(c.Id) ?? false))
                    {
                        if (c.SkipNextUntap) c.SkipNextUntap = false;
                        // "You may choose not to untap": kept tapped while that keeps control of something it took.
                        else if (c.HasKeyword(MayNotUntap) && State.ControlLinks.Any(l => l.HostId == c.Id && l.Until.Contains("Untap"))) continue;
                        else if (c.Tapped) untapping.Add(c);
                    }
                foreach (var c in LimitUntaps(untapping)) c.Tapped = false;
                return;   // no priority in the untap step
            case Step.Upkeep:
                State.Players[State.ActivePlayer].PrevUpkeepTurn = State.Players[State.ActivePlayer].UpkeepTurn;
                State.Players[State.ActivePlayer].UpkeepTurn = State.Turn;
                State.ThisTurn.Upkeeps++;
                Suspended();
                FirePhase("Upkeep");
                break;
            case Step.Draw:
                if (!(State.Turn == 1 && Settings.OnThePlay)) Draw(Me, 1);
                FirePhase("Draw");
                break;
            case Step.Main1:
                // "After your draw step, add a lore counter" (rule 714.3b).
                foreach (var saga in State.BattlefieldCards.Where(c => c.Controller == State.ActivePlayer && c.CurrentFace.Chapters.Count > 0).ToList())
                    PutCounters(saga, "LORE", 1);
                FirePhase("Main1");
                break;
            case Step.BeginCombat:
                State.ThisTurn.Combats++;
                FirePhase("BeginCombat");
                break;
            case Step.DeclareAttackers: DeclareAttackers(); break;
            case Step.CombatDamage: CombatDamage(); break;
            case Step.EndCombat:
                FirePhase("EndCombat");
                State.Attackers.Clear();
                ExpireEffects("endcombat");   // "this combat" is over once attacks and damage are done
                break;
            case Step.Main2: FirePhase("Main2"); break;
            case Step.End:
                AtBeginningOfEndStep();
                FirePhase("End of Turn");
                break;
            case Step.Cleanup: Cleanup(); return;   // no priority unless something triggers
        }
        if (!State.GameOver) PriorityLoop();
    }

    /// <summary>Your phased-out permanents phase in before you untap (rule 502.1); nothing enters, so nothing triggers.</summary>
    private void PhaseIn()
    {
        foreach (var id in State.PhasedOut.ToList())
        {
            var c = State[id];
            if (c.Controller != Me) continue;
            State.PhasedOut.Remove(id);
            State.Battlefield.Add(id);
            c.Zone = Zone.Battlefield;
            Note($"{c.Name} phases in");
        }
    }

    /// <summary>Phases a permanent out, with what's attached to it (rule 702.26g).</summary>
    public void PhaseOut(Card c)
    {
        if (c.Zone != Zone.Battlefield) return;
        foreach (var attached in State.BattlefieldCards.Where(a => a.AttachedTo == c.Id).ToList()) PhaseOut(attached);
        State.Battlefield.Remove(c.Id);
        State.PhasedOut.Add(c.Id);
        c.Zone = Zone.PhasedOut;
        Note($"{c.Name} phases out");
    }

    /// <summary>"At the beginning of the next end step" actions (tokens exiled or sacrificed at end of turn).</summary>
    /// <summary>
    /// "Players can't untap more than N [type] during their untap steps" (Winter Orb, Static Orb): the ones untapped are
    /// those that make mana first, then the most expensive.
    /// </summary>
    private IEnumerable<Card> LimitUntaps(List<Card> untapping)
    {
        if (State.Players[Me].StaticUntapLimits is not { } limits) return untapping;
        var result = untapping.OrderByDescending(c => c.Activated.Any(a => a.IsManaAbility || a.Api == "Mana")).ThenByDescending(c => c.ManaValue).ToList();
        foreach (var (valid, max, sourceId) in limits)
        {
            var x = ContextFor(State[sourceId]);
            var filter = Effect.FilterFor(State[sourceId].CurrentFace, valid);
            var kept = 0;
            result.RemoveAll(c => filter.MatchesCard(c, x) && ++kept > max);
        }
        return result;
    }

    /// <summary>
    /// "End the turn" (rule 723): every spell and ability on the stack is exiled, and the turn goes on from its cleanup
    /// step. Abilities that triggered are dropped too.
    /// </summary>
    public void EndTheTurn()
    {
        Note("  the turn ends");
        foreach (var item in State.Stack.ToList())
            if (item.IsSpell && State[item.SourceId].Zone == Zone.Stack) MoveTo(State[item.SourceId], Zone.Exile);
        State.Stack.Clear();
        if (_resolvingSpell is { } r && State[r].Zone == Zone.Stack) MoveTo(State[r], Zone.Exile);
        _pending.Clear();
        State.ThisTurn.ExtraPhases.Clear();
        State.ThisTurn.Ended = true;
    }

    /// <summary>Rule 724: one player at a time is the monarch.</summary>
    public void BecomeMonarch(int player)
    {
        if (State.Players[player].Lost) return;
        if (State.Monarch == player) return;
        State.Monarch = player;
        Note($"  player {player} becomes the monarch");
        Fire(new TriggerEvent(TriggerMode.BecomeMonarch, Player: player));
    }

    private void AtBeginningOfEndStep()
    {
        if (State.Monarch == State.ActivePlayer)
        {
            Note("the monarch draws");
            Draw(State.ActivePlayer, 1);
        }
        foreach (var (id, action) in State.ThisTurn.AtEndStep)
        {
            var c = State[id];
            if (c.Zone != Zone.Battlefield) continue;
            Note($"{action.ToLowerInvariant()} {c.Name} (end of turn)");
            if (action == "Exile") MoveTo(c, Zone.Exile);
            else if (action == "Hand") MoveTo(c, Zone.Hand);
            else Sacrifice(c);
        }
        State.ThisTurn.AtEndStep.Clear();
    }

    private void SetStep(Step step)
    {
        State.Step = step;
        foreach (var p in State.Players)
        {
            // Mana empties between steps (rule 500.4), except mana that lasts until end of turn (spent last).
            if (step == Step.Untap) p.PersistentPool.Clear();
            var kept = new ManaPool();
            foreach (var c in ManaColors.All) kept[c] = Math.Min(p.Pool[c], p.PersistentPool[c]);
            p.PersistentPool = kept;
            p.Pool = kept;
            p.RestrictedPool = null;
        }
    }

    /// <summary>
    /// Suspend (rule 702.62): at the beginning of your upkeep remove a time counter from each of your
    /// suspended cards; when the last is removed, cast it without paying its mana cost.
    /// </summary>
    private void Suspended()
    {
        foreach (var c in State.Players[State.ActivePlayer].Exile.Select(id => State[id]).Where(c => c.Suspended).ToList())
        {
            if (c.CounterCount("TIME") > 0) RemoveCounters(c, "TIME", 1);
            Note($"{c.Name}: {c.CounterCount("TIME")} time counter(s) left");
            if (c.CounterCount("TIME") > 0) continue;
            c.Suspended = false;
            c.Counters = null;
            if (!CanCastFromEffect(c, free: true)) continue;
            CastFromEffect(c, free: true, exileAfter: false);
            if (c.Types.Is(CardType.Creature)) (c.TempKeywords ??= new HashSet<string>(StringComparer.Ordinal)).Add("Haste");
        }
    }

    private void DeclareAttackers()
    {
        Statics.Apply(this);
        var candidates = State.BattlefieldCards
            .Where(c => c.Controller == Me && c.Types.Is(CardType.Creature) && !c.Tapped && !IsSick(c) && !c.HasKeyword("Defender") && !Statics.CantAttack(this, c))
            .Select(c => c.Id).ToList();
        var attackers = candidates.Count == 0 ? new List<int>() : Decider.ChooseAttackers(this, Me, candidates);
        // "No more than one creature can attack each combat" (Silent Arbiter): the strongest of those chosen.
        if (Statics.MaxAttackers(this) is { } most && attackers.Count > most)
            attackers = attackers.OrderByDescending(id => State[id].Power).Take(most).ToList();
        if (attackers.Count == 0) return;
        State.Attackers.AddRange(attackers);
        State.ThisTurn.AttackersDeclared += attackers.Count;
        foreach (var id in attackers) State.ThisTurn.Attacks[id] = State.ThisTurn.Attacks.GetValueOrDefault(id) + 1;
        // Goldfish opponents never block: everything attacks the opponent with the least life left.
        var defender = State.Players.Where(p => p.Index != Me && !p.Lost).OrderBy(p => p.Life).ThenBy(p => p.Index).FirstOrDefault()?.Index;
        State.ThisTurn.DefendingPlayer = defender;
        foreach (var id in attackers)
        {
            var a = State[id];
            if (!a.HasKeyword("Vigilance")) Tap(a);
            Fire(new TriggerEvent(TriggerMode.Attacks, CardId: id, Player: defender));
            // "You may exert it as it attacks. When you do, …": it won't untap during your next untap step (rule 701.39).
            foreach (var st in a.Statics)
                if (st.Mode == "OptionalAttackCost" && st.Reflexive is { } reflexive && Presence.Hold(st.P, ContextFor(a))
                    && Decider.ChooseYesNo(this, Me, a, $"exert {a.Name}?"))
                {
                    a.ExertedTurn = State.Turn;
                    a.SkipNextUntap = true;
                    Note($"  exert {a.Name}");
                    _pending.Add(new PendingTrigger(a.Id, reflexive, new TriggerEvent(TriggerMode.Immediate, CardId: a.Id), a.Controller));
                }
        }
        Fire(new TriggerEvent(TriggerMode.AttackersDeclared, Player: Me));
        Note("attack with " + string.Join(", ", attackers.Select(id => State[id].Name)));
    }

    private void CombatDamage()
    {
        Statics.Apply(this);
        if (State.ThisTurn.NoCombatDamage) { Note("  combat damage prevented"); return; }
        // Attackers hit the player they attacked; if that player has lost, the next with the least life.
        var defender = State.ThisTurn.DefendingPlayer is { } d && !State.Players[d].Lost ? State.Players[d]
            : State.Players.Where(p => p.Index != Me && !p.Lost).OrderBy(p => p.Life).ThenBy(p => p.Index).FirstOrDefault();
        if (defender is null) return;
        List<int>? hitPlayers = null;
        foreach (var id in State.Attackers)
        {
            var a = State[id];
            if (a.Zone != Zone.Battlefield) continue;
            // Double strike deals combat damage twice: each is its own damage event.
            var dealt = 0;
            for (var hit = a.HasKeyword("Double Strike") ? 2 : 1; hit > 0; hit--)
                dealt += DamagePlayer(defender.Index, Math.Max(0, a.Power), a, combat: true);
            if (dealt > 0 && a.IsCommander)
                defender.CommanderDamageTaken[a.Id] = defender.CommanderDamageTaken.GetValueOrDefault(a.Id) + dealt;
            // Combat damage to the monarch makes the creature's controller the monarch.
            if (dealt > 0 && State.Monarch == defender.Index) BecomeMonarch(a.Controller);
            if (dealt > 0) (hitPlayers ??= new()).Add(a.Id);
        }
        // Cipher (rule 702.99): "whenever this creature deals combat damage to a player, its controller may cast a copy
        // of the encoded card without paying its mana cost."
        foreach (var attacker in hitPlayers ?? new())
            foreach (var p in State.Players)
                foreach (var id in p.Exile.ToList())
                    if (State[id].EncodedOn == attacker && State[attacker].Zone == Zone.Battlefield)
                        CastCopyOf(State[id], State[attacker].Controller);
    }

    /// <summary>Casts a copy of a card (it isn't moved); the copy ceases to exist when it leaves the stack (rule 707.12).</summary>
    public void CastCopyOf(Card original, int controller)
    {
        var copy = new Card { Id = State.Cards.Count, Def = original.Def, Owner = controller, Controller = controller, Zone = Zone.None, IsToken = true };
        State.Cards.Add(copy);
        if (CastableFromEffect(copy, free: true).Count == 0 || !Decider.ChooseYesNo(this, controller, original, $"cast a copy of {original.Name}?"))
        {
            State.Cards.RemoveAt(State.Cards.Count - 1);
            return;
        }
        Note($"  cast a copy of {original.Name}");
        CastFromEffect(copy, free: true, exileAfter: false);
    }

    private void Cleanup()
    {
        DiscardToHandSize();
        EndOfTurnCleanup();
        // "At the beginning of the cleanup step": if anything triggers, it resolves and there's another cleanup (rule 514.3a).
        FirePhase("Cleanup");
        if (!HasPendingTriggers) return;
        PriorityLoop();
        DiscardToHandSize();
        EndOfTurnCleanup();
    }

    private void DiscardToHandSize()
    {
        var me = State.Players[Me];
        var excess = me.Hand.Count - me.MaxHandSizeWith(Settings.MaxHandSize);
        if (excess > 0)
            foreach (var id in Decider.ChooseCards(this, Me, Choice.Discard, null, me.Hand.ToList(), excess, excess))
                Discard(State[id]);
    }

    /// <summary>The cleanup step after the active player's discard: "until end of turn" ends and damage wears off.</summary>
    private void EndOfTurnCleanup()
    {
        // "Gain control until end of turn" ends.
        foreach (var (id, controller) in State.ThisTurn.ControlUntilEot)
            if (State[id].Zone == Zone.Battlefield) State[id].Controller = controller;
        State.ThisTurn.ControlUntilEot.Clear();
        foreach (var c in State.BattlefieldCards)
        {
            c.Damage = 0;
            c.PowerBonus = 0;
            c.ToughnessBonus = 0;
            c.TempKeywords = null;
            c.RegenShields = 0;
        }
        // "Gains flashback until end of turn" ends.
        foreach (var p in State.Players)
            foreach (var id in p.Graveyard)
                State[id].TempKeywords = null;
        ExpireEffects("cleanup");
    }

    // ---- priority ---------------------------------------------------------------------

    /// <summary>
    /// Gives the simulated player priority until they pass with an empty stack. Goldfish
    /// opponents always pass, so passing with a non-empty stack resolves its top item.
    /// </summary>
    public void PriorityLoop()
    {
        while (!State.GameOver && !State.ThisTurn.Ended)
        {
            CheckStateBasedActions();
            if (State.GameOver) return;
            if (_pending.Count > 0) { PutTriggersOnStack(); continue; }
            // Rollouts don't act outside the main phases unless something is on the stack.
            if (State.Stack.Count == 0 && State.Step is not (Step.Main1 or Step.Main2) && Decider.PassesQuietSteps) return;
            TrackMana();
            if (OnCheckpoint is not null && State.Stack.Count == 0 && State.Step is Step.Main1 or Step.Main2)
                OnCheckpoint(this);

            if (_actionsThisTurn >= Math.Min(Settings.MaxActionsPerTurn, ActionLimit) + MandatoryLoopActions)
            {
                Note("the stack never empties: a mandatory loop, so the game is a draw");
                State.GameOver = true;
                State.Winner = null;
                State.WinReason = null;
                return;
            }
            GameAction action = PastActionLimit ? GameAction.PassAction
                : Decider.ChoosesActionsLazily ? ChooseActionLazily(Me)
                : Decider.ChooseAction(this, Me, LegalActions(Me));
            _actionsThisTurn++;
            if (action is GameAction.Pass && State.Stack.Count == 0) return;
            InPriorityAction = true;
            try
            {
                if (action is GameAction.Pass) ResolveTop();
                else Perform(action);
            }
            finally { InPriorityAction = false; }
        }
    }

    public void Perform(GameAction action)
    {
        switch (action)
        {
            case GameAction.PlayLand pl:
                State[pl.CardId].Face = pl.Face;
                PlayLand(State[pl.CardId]);
                break;
            case GameAction.Cast cast:
                State[cast.CardId].Face = cast.Face;
                CastSpell(State[cast.CardId], cast.SpellIndex);
                break;
            case GameAction.Activate act: ActivateAbility(State[act.CardId], act.AbilityIndex); break;
        }
    }

    private void TrackMana()
    {
        if (IsSearchCopy) return;   // only reported (mana_by_turn)
        var available = State.Players[Me].Pool.Total + AutoSources(Me, -1).Where(s => State[s.CardId].Zone == Zone.Battlefield).Sum(s => s.Amount);
        State.ThisTurn.MaxManaSeen = Math.Max(State.ThisTurn.MaxManaSeen, available);
    }

    public bool IsSick(Card c) =>
        c.Types.Is(CardType.Creature) && c.ControlledSinceTurn >= State.Turn && !c.HasKeyword("Haste");

    private bool IsMainPhaseEmptyStack(int player) =>
        State.ActivePlayer == player && State.Stack.Count == 0 && State.Step is Step.Main1 or Step.Main2;

    // ---- legal actions ----------------------------------------------------------------

    // Mana sources found while working out the legal actions; the game doesn't change during that.
    private Dictionary<int, List<ManaSource>>? _autoSourceCache;

    /// <summary>While working out the legal actions: every static ability in play, found once (see <see cref="Statics.Pairs"/>).</summary>
    internal List<(Card Source, StaticDef Static)>? StaticPairs;
    private bool _cacheStaticPairs;
    internal bool CachesStaticPairs => _cacheStaticPairs;

    public List<GameAction> LegalActions(int player) => WithLegalActionCaches(() => LegalActionsCore(player, check: true));

    /// <summary>
    /// Lets the decider choose from the actions that might be legal, checking costs and targets only for those it
    /// considers: those checks are most of the work of finding the legal actions.
    /// </summary>
    private GameAction ChooseActionLazily(int player) => WithLegalActionCaches(() =>
    {
        var sorcerySpeed = IsMainPhaseEmptyStack(player);
        var candidates = LegalActionsCore(player, check: false);
        return Decider.ChooseActionLazily(this, player, candidates, a => IsLegal(a, sorcerySpeed));
    });

    /// <summary>Checks the costs and targets of an action <see cref="LegalActionsCore"/> found without checking them.</summary>
    private bool IsLegal(GameAction action, bool sorcerySpeed)
    {
        switch (action)
        {
            case GameAction.Cast cast:
                var c = State[cast.CardId];
                var face = c.Face;
                c.Face = cast.Face;
                try { return CanCast(c, cast.SpellIndex); }
                finally { c.Face = face; }
            case GameAction.Activate act:
                var source = State[act.CardId];
                return CanActivate(source, source.Activated[act.AbilityIndex], sorcerySpeed);
            default:
                return true;
        }
    }

    // The game doesn't change while the legal actions are worked out, so mana sources and static abilities are found once.
    private T WithLegalActionCaches<T>(Func<T> f)
    {
        var saved = _autoSourceCache;
        var (savedPairs, savedCaching) = (StaticPairs, _cacheStaticPairs);
        _autoSourceCache = new();
        (StaticPairs, _cacheStaticPairs) = (null, true);
        try { return f(); }
        finally
        {
            _autoSourceCache = saved;
            (StaticPairs, _cacheStaticPairs) = (savedPairs, savedCaching);
        }
    }

    /// <param name="check">False: leave out the cost and target checks (see <see cref="IsLegal"/>).</param>
    private List<GameAction> LegalActionsCore(int player, bool check)
    {
        var result = new List<GameAction> { GameAction.PassAction };
        var p = State.Players[player];
        var sorcerySpeed = IsMainPhaseEmptyStack(player);

        // Identical cards are interchangeable; offer each name once (per place it's played from). The lists are
        // short, so a scan beats hashing each name.
        var seenNames = new List<string>();
        foreach (var id in p.Hand.Concat(p.Command))
        {
            var c = State[id];
            if (c.Zone == Zone.Hand && !AddName(seenNames, c.Name)) continue;
            if (c.IsBlank || c.IsEffect) continue;
            AddPlayOrCast(result, c, p, sorcerySpeed, check);

            // Abilities usable from hand (e.g. "exile this from your hand: add {R}").
            if (c.Zone == Zone.Hand)
                for (var i = 0; i < c.Activated.Count; i++)
                {
                    var ab = c.Activated[i];
                    if (ab["ActivationZone"] == "Hand" && !IsAutoManaAbility(ab) && (!check || CanActivate(c, ab, sorcerySpeed))) result.Add(new GameAction.Activate(id, i));
                }
        }

        // The creature half of a card on an adventure.
        List<string>? seenAdventures = null;
        foreach (var id in p.Exile)
        {
            var c = State[id];
            if (c.OnAdventure && !c.IsBlank && AddName(seenAdventures ??= new(), c.Name)) AddPlayOrCast(result, c, p, sorcerySpeed, check);
        }

        // Cards an effect lets you play from exile, the graveyard or the top of your library.
        List<string>? seenMayPlay = null;
        foreach (var id in Statics.MayPlay(this, player).Keys)
        {
            var c = State[id];
            if (c.Zone is Zone.Hand or Zone.Stack || c.IsBlank || !AddName(seenMayPlay ??= new(), c.Name)) continue;
            AddPlayOrCast(result, c, p, sorcerySpeed, check);
        }

        // Abilities activated from the graveyard ("Exile this card from your graveyard: …").
        List<string>? seenGraveyardAbilities = null;
        foreach (var id in p.Graveyard)
        {
            var c = State[id];
            if (c.IsBlank || !HasAbility(c, ab => ab["ActivationZone"] == "Graveyard") || !AddName(seenGraveyardAbilities ??= new(), c.Name)) continue;
            for (var i = 0; i < c.Activated.Count; i++)
            {
                var ab = c.Activated[i];
                if (ab["ActivationZone"] == "Graveyard" && (!check || CanActivate(c, ab, sorcerySpeed))) result.Add(new GameAction.Activate(id, i));
            }
        }

        // Flashback and escape: cast from the graveyard.
        bool? escapeGranted = null;
        List<string>? seenGraveyard = null;
        foreach (var id in p.Graveyard)
        {
            var c = State[id];
            if (c.IsBlank) continue;
            if (c.FlashbackCost is null && c.CurrentFace.EscapeCost is null)
            {
                escapeGranted ??= Statics.GrantsEscape(this, player);
                if (escapeGranted == false || Statics.EscapeCost(this, c) is null) continue;
            }
            if (!AddName(seenGraveyard ??= new(), c.Name)) continue;
            if (!(c.Types.Is(CardType.Instant) || c.HasKeyword("Flash") || sorcerySpeed)) continue;
            if (c.Types.IsPermanent && c.CurrentFace.SpellAbilities.Count == 0)
            {
                if (!check || CanCast(c, -1)) result.Add(new GameAction.Cast(id, -1));
            }
            else
                for (var i = 0; i < c.CurrentFace.SpellAbilities.Count; i++)
                    if (!check || CanCast(c, i)) result.Add(new GameAction.Cast(id, i));
        }

        // Foretold cards: cast from exile for their foretell cost on a later turn (rule 702.143).
        List<string>? seenForetold = null;
        foreach (var id in p.Exile)
        {
            var c = State[id];
            if (c.IsBlank || c.ForetoldTurn < 0 || c.ForetoldTurn >= State.Turn || c.CurrentFace.Foretell is null || !AddName(seenForetold ??= new(), c.Name)) continue;
            if (!(c.Types.Is(CardType.Instant) || c.HasKeyword("Flash") || sorcerySpeed)) continue;
            if (c.Types.IsPermanent && c.CurrentFace.SpellAbilities.Count == 0)
            {
                if (!check || CanCast(c, -1)) result.Add(new GameAction.Cast(id, -1));
            }
            else
                for (var i = 0; i < c.CurrentFace.SpellAbilities.Count; i++)
                    if (c.CurrentFace.SpellAbilities[i].AltCost is null && (!check || CanCast(c, i))) result.Add(new GameAction.Cast(id, i));
        }

        List<(string Name, bool FaceDown, bool Tapped, bool Sick)>? seenPermanents = null;
        foreach (var c in State.BattlefieldCards)
        {
            if (c.Controller != player || c.IsBlank || !HasAbility(c, Offerable)) continue;
            if (c.Counters is null && c.Animations is null && c.StaticActivated is null)
            {
                // Untouched permanents with the same name, tapped or not and sick or not, have the same abilities.
                var key = (c.FaceDown ? c.Def.Name : c.Name, c.FaceDown, c.Tapped, IsSick(c));
                seenPermanents ??= new();
                if (seenPermanents.Contains(key)) continue;
                seenPermanents.Add(key);
            }
            for (var i = 0; i < c.Activated.Count; i++)
                if (Offerable(c.Activated[i]) && (!check || CanActivate(c, c.Activated[i], sorcerySpeed))) result.Add(new GameAction.Activate(c.Id, i));
        }
        return result;
    }

    /// <summary>Adds <paramref name="name"/> unless it's already there; false if it was.</summary>
    private static bool AddName(List<string> seen, string name)
    {
        foreach (var n in seen)
            if (string.Equals(n, name, StringComparison.Ordinal)) return false;
        seen.Add(name);
        return true;
    }

    private static bool HasAbility(Card c, Func<AbilityDef, bool> pred)
    {
        foreach (var ab in c.Activated)
            if (pred(ab)) return true;
        return false;
    }

    /// <summary>A battlefield ability offered as an action (mana abilities are used automatically when paying).</summary>
    private static bool Offerable(AbilityDef ab) => ab["ActivationZone"] is null && !IsAutoManaAbility(ab);

    /// <summary>Adds playing <paramref name="c"/> as a land, or casting it with each of its spell abilities, if allowed now.</summary>
    private void AddPlayOrCast(List<GameAction> result, Card c, Player p, bool sorcerySpeed, bool check)
    {
        // A modal double-faced card can be played as either face (rule 712.12), a split card cast as either half
        // (rule 709.3); an adventurer card can be cast as its Adventure from hand (rule 715.3).
        var faces = c.Def.Layout is "Modal" or "Split" || c.Def.Layout == "Adventure" && c.Zone == Zone.Hand ? c.Def.Faces.Count : 1;
        for (var f = 0; f < faces; f++)
        {
            c.Face = f;
            AddPlayOrCastFace(result, c, p, sorcerySpeed, f, check);
        }
        c.Face = 0;
    }

    private void AddPlayOrCastFace(List<GameAction> result, Card c, Player p, bool sorcerySpeed, int face, bool check)
    {
        if (c.Types.Is(CardType.Land))
        {
            if (c.Zone != Zone.Command && sorcerySpeed && p.LandsPlayedThisTurn < p.LandDropsPerTurn + p.StaticLandDrops && !Statics.CantPlayLand(this, p.Index))
                result.Add(new GameAction.PlayLand(c.Id, face));
            return;
        }
        if (!(c.Types.Is(CardType.Instant) || c.HasKeyword("Flash") || c.HasKeyword("MayFlashSac") || sorcerySpeed || Statics.CastWithFlash(this, c, p.Index))) return;
        if (c.Types.IsPermanent && c.CurrentFace.SpellAbilities.Count == 0)
        {
            if (!check || CanCast(c, -1)) result.Add(new GameAction.Cast(c.Id, -1, face));
        }
        else
            for (var i = 0; i < c.CurrentFace.SpellAbilities.Count; i++)
                if (!check || CanCast(c, i)) result.Add(new GameAction.Cast(c.Id, i, face));
    }

    /// <summary>"Upkeep->Declare Blockers,Main2": the steps an ability may be used in, or null if a name isn't known.</summary>
    public static List<(Step From, Step To)>? StepRanges(string phases)
    {
        var ranges = new List<(Step, Step)>();
        foreach (var part in phases.Split(','))
        {
            var ends = part.Split("->");
            if (ends.Length > 2 || StepNamed(ends[0].Trim()) is not { } from || StepNamed(ends[^1].Trim()) is not { } to) return null;
            ranges.Add((from, to));
        }
        return ranges;
    }

    // Nobody blocks a goldfish, so the declare blockers step is part of declaring attackers here.
    private static Step? StepNamed(string name) => name switch
    {
        "Untap" => Step.Untap, "Upkeep" => Step.Upkeep, "Draw" => Step.Draw, "Main1" => Step.Main1, "BeginCombat" => Step.BeginCombat,
        "Declare Attackers" or "Declare Blockers" => Step.DeclareAttackers, "FirstStrikeDamage" or "Combat Damage" or "CombatDamage" => Step.CombatDamage,
        "EndCombat" => Step.EndCombat, "Main2" => Step.Main2, "End of Turn" or "End Of Turn" => Step.End, "Cleanup" => Step.Cleanup,
        _ => null,
    };

    /// <summary>ActivationPhases$ and ActivationFirstCombat$ ("only before the combat damage step").</summary>
    private bool TimingAllows(AbilityDef ab)
    {
        if (ab["ActivationPhases"] is { } phases && StepRanges(phases) is { } ranges && !ranges.Any(r => State.Step >= r.From && State.Step <= r.To))
            return false;
        return ab["ActivationFirstCombat"] != "True" || State.ThisTurn.Combats == 0
            || State.ThisTurn.Combats == 1 && State.Step is Step.BeginCombat or Step.DeclareAttackers;
    }

    private bool CanActivate(Card c, AbilityDef ab, bool sorcerySpeed)
    {
        if (ab["SorcerySpeed"] == "True" && !sorcerySpeed) return false;
        if (!TimingAllows(ab)) return false;
        if (Statics.CantBeActivated(this, c, ab)) return false;
        // "Activate only once each turn".
        if (ab["ActivationLimit"] is { } limit && int.TryParse(limit, out var most) && c.ActivationUses?.GetValueOrDefault(ab) is var (turn, times)
            && turn == State.Turn && times >= most) return false;
        if (ab.Kind == AbilityKind.Activated && ab.P.Values.Keys.Any(Presence.Params.Contains) && !Presence.Hold(ab.P, ContextFor(c))) return false;
        if (ab["Activation"] is { } activation && !Presence.ConditionHolds(activation, ContextFor(c))) return false;
        // "Commander only" abilities (War Room): this is a Commander game if the player has a commander.
        if (ab["ActivationGameTypes"] is { } gameTypes
            && !(gameTypes.Split(',').Contains("Commander") && State.Cards.Any(k => k.IsCommander && k.Owner == c.Controller))) return false;
        if (ab.CharmNumExpr is { } charmNum && ab.CharmCount > Effect.EvalAmount(ContextFor(c), charmNum)) return false;
        // Ninjutsu: only while you have an attacking creature (the goldfish never blocks, so attackers are unblocked).
        if (ab.Api == "Ninjutsu" && State.Step is not (Step.DeclareAttackers or Step.CombatDamage)) return false;
        // A Class gains its levels in order (rule 716.2a).
        if (ab.Api == "ClassLevelUp" && c.ClassLevel != int.Parse(ab["ClassLevel"]!) - 1) return false;
        // Loyalty abilities: sorcery speed, one per planeswalker per turn (rule 606.3).
        if (ab["Planeswalker"] == "True" && (!sorcerySpeed || c.LoyaltyActivatedTurn == State.Turn)) return false;
        if (!CanPayCost(AbilityCost(c, ab), c, c.Controller, 0, excludeSourceFromMana: ab.Cost.Tap)) return false;
        return HasLegalTargets(c, ab);
    }

    /// <summary>An activated ability's cost, less any "costs {1} less for each …" (ReduceCost$).</summary>
    private Cost AbilityCost(Card c, AbilityDef ab)
    {
        if (ab["ReduceCost"] is not { } reduce) return ab.Cost;
        var n = Effect.EvalAmount(ContextFor(c), reduce);
        return n <= 0 ? ab.Cost : new Cost { Mana = ab.Cost.Mana.WithExtraGeneric(-n), Tap = ab.Cost.Tap, Untap = ab.Cost.Untap, Parts = ab.Cost.Parts };
    }

    private bool HasLegalTargets(Card source, AbilityDef head)
    {
        foreach (var ab in head.Chain())
            if (ab.TargetFilter is not null && TargetBounds(source, ab, source.Controller, 0).Min is var min and > 0
                && TargetCandidates(source, ab, source.Controller).Count < min)
                return false;
        return true;
    }

    // ---- lands, casting, activating ---------------------------------------------------

    public void PlayLand(Card land)
    {
        State.Players[land.Controller].LandsPlayedThisTurn++;
        Note($"play {land.Name}");
        State.Record.Cast.Add(land.Def.Name);
        var from = land.Zone;
        MoveTo(land, Zone.Battlefield);
        Fire(new TriggerEvent(TriggerMode.LandPlayed, CardId: land.Id, From: from, Player: land.Controller));
    }

    /// <summary>The spell's cost: its spell ability's Cost$ (which includes any mana) or the face's mana cost, plus commander tax.</summary>
    /// <summary>The once-each-turn static whose other cost <see cref="SpellCost"/> last used, if any.</summary>
    private (Card Source, StaticDef Static)? _altCostGrant;

    private Cost SpellCost(Card c, int spellIndex)
    {
        Cost cost;
        _altCostGrant = null;
        var spell = spellIndex >= 0 ? c.CurrentFace.SpellAbilities[spellIndex] : null;
        if (c.Zone == Zone.Graveyard && c.FlashbackCost is { } flashback)
            cost = flashback;
        else if (c.Zone == Zone.Exile && c.ForetoldTurn >= 0 && c.CurrentFace.Foretell is { } foretold)
            cost = foretold;
        else if (c.Zone == Zone.Graveyard && Statics.EscapeCost(this, c) is { } escape)
            cost = escape;
        else if (spell?.AltCost is { } alt)
            cost = alt;
        else if (spellIndex >= 0 && c.CurrentFace.SpellAbilities[spellIndex].Cost is { } abCost && c.CurrentFace.SpellAbilities[spellIndex]["Cost"] is not null)
            cost = abCost;
        else
        {
            cost = new Cost { Mana = c.CurrentFace.ManaCost };
            // "You may pay {0} rather than pay the mana cost" (As Foretold): taken whenever it's offered.
            _altCostGrant = Statics.AltCostFor(this, c);
            if (_altCostGrant is { } offered) cost = Statics.AltManaCost(c, offered.Static["MayPlayAltManaCost"]!);
        }
        // "You may cast it without paying its mana cost" from an effect, or for another cost instead (Bolas's Citadel).
        var grant = c.Zone is Zone.Exile or Zone.Graveyard or Zone.Library ? Statics.MayPlay(this, c.Controller).GetValueOrDefault(c.Id) : default;
        if (c.Id == _freeCast || grant.Free)
            cost = new Cost { Mana = ManaCost.Zero, Tap = cost.Tap, Untap = cost.Untap, Parts = cost.Parts };
        else if (grant.AltManaCost is { } alt && c.Zone == Zone.Library)
            cost = new Cost { Tap = cost.Tap, Untap = cost.Untap, Parts = cost.Parts }.Plus(Statics.AltManaCost(c, alt));
        // "Mana of any type can be spent to cast that spell": its colored symbols take any mana.
        if (grant.AnyTypeOfMana)
            cost = new Cost { Mana = new ManaCost { Generic = cost.Mana.ManaValue, XCount = cost.Mana.XCount }, Tap = cost.Tap, Untap = cost.Untap, Parts = cost.Parts };
        // Optional additional costs chosen for this cast (kicker, bargain).
        if (spell?.ExtraCost is { } extraCost) cost = cost.Plus(extraCost);
        var extra = c.Zone == Zone.Command ? 2 * c.CommandCasts : 0;
        extra -= Statics.CostReduction(this, c, c.Controller);
        // Affinity for [type]: {1} less for each one you control (rule 702.41).
        foreach (var k in c.CurrentFace.RawKeywords)
            if (k.StartsWith("Affinity:", StringComparison.Ordinal))
            {
                var filter = Effect.FilterFor(c.CurrentFace, k[9..]);
                var x = ContextFor(c);
                extra -= State.BattlefieldCards.Count(p => p.Controller == c.Controller && filter.MatchesCard(p, x));
            }
        if (extra != 0)
            cost = new Cost { Mana = cost.Mana.WithExtraGeneric(extra), Tap = cost.Tap, Untap = cost.Untap, Parts = cost.Parts };
        return cost;
    }

    public bool CanCast(Card c, int spellIndex)
    {
        _payingForSpell = true;
        try { return CanCastCore(c, spellIndex); }
        finally { _payingForSpell = false; }
    }

    private bool CanCastCore(Card c, int spellIndex)
    {
        var cost = SpellCost(c, spellIndex);
        if (cost.Mana.NoCost) return false;
        if (spellIndex >= 0 && c.CurrentFace.SpellAbilities[spellIndex].AltCostOf is { } altStatic && !Presence.Hold(altStatic.P, ContextFor(c)))
            return false;
        if (spellIndex >= 0 && c.CurrentFace.SpellAbilities[spellIndex].AltCondition is { } condition && !AltConditionHolds(c, condition))
            return false;
        if (spellIndex >= 0 && !TimingAllows(c.CurrentFace.SpellAbilities[spellIndex])) return false;
        if (spellIndex >= 0 && c.CurrentFace.SpellAbilities[spellIndex] is { CharmNumExpr: { } charmNum } modal
            && modal.CharmCount > Effect.EvalAmount(ContextFor(c), charmNum))
            return false;
        if (Statics.CantBeCast(this, c, c.Controller)) return false;
        if (!CanPayCost(cost, c, c.Controller, 0, excludeSourceFromMana: true)) return false;
        var ability = spellIndex >= 0 ? c.CurrentFace.SpellAbilities[spellIndex] : c.CurrentFace.AuraSpell;
        return ability is null || HasLegalTargets(c, ability);
    }

    /// <summary>Prowl: a creature sharing a creature type with it dealt combat damage to a player this turn. Spectacle: an opponent lost life.</summary>
    private bool AltConditionHolds(Card c, string condition) => condition switch
    {
        "Prowl" => State.ThisTurn.CombatDamageTypes.Contains("*") && c.Types.Subtypes.Any(TypeLine.IsCreatureType)
                   || c.Types.AllCreatureTypes && State.ThisTurn.CombatDamageTypes.Count > 0
                   || c.Types.Subtypes.Any(t => TypeLine.IsCreatureType(t) && State.ThisTurn.CombatDamageTypes.Contains(t)),
        "Spectacle" => State.ThisTurn.LifeLost.Any(kv => kv.Key != c.Controller && kv.Value > 0),
        // Surge: you've cast another spell this turn.
        "Surge" => State.ThisTurn.SpellsCast > 0,
        "Miracle" => c.Id == _miracleCast,
        _ => false,
    };

    public void CastSpell(Card c, int spellIndex)
    {
        var player = c.Controller;
        var from = c.Zone;
        var cost = SpellCost(c, spellIndex);
        var altGrant = _altCostGrant;
        var ability = spellIndex >= 0 ? c.CurrentFace.SpellAbilities[spellIndex] : c.CurrentFace.AuraSpell ?? PermanentSpell;

        var x = 0;
        if (HasX(cost))
            x = Decider.ChooseX(this, player, c, MaxAffordableX(cost, c, player));
        var spliced = ChooseSplices(c, player, x, ref cost);
        // Strive: as many extra targets as there's mana for.
        _striveExtra = 0;
        if (c.CurrentFace.Strive is { } strive)
            for (var more = cost.Plus(new Cost { Mana = strive }); _striveExtra < 20 && CanPayCost(more, c, player, x, excludeSourceFromMana: true);
                 more = more.Plus(new Cost { Mana = strive }))
                _striveExtra++;
        var escaped = from == Zone.Graveyard && c.FlashbackCost is null;
        c.CastFrom = from;

        // Move to the stack first (rule 601.2a), then choose targets and pay.
        RemoveFromZone(c);
        c.Zone = Zone.Stack;
        c.PaidOptional = ability.OptionalCosts;
        var item = new StackItem
        {
            Id = State.NextStackId++, SourceId = c.Id, Ability = ability, Controller = player, IsSpell = true, X = x,
            ChainTargets = ChooseChainTargets(c, ability, player, x),
            Description = c.Name + (from != Zone.Graveyard ? "" : escaped ? " (escape)" : c.CurrentFace.Retrace ? " (retrace)" : " (flashback)") + (ability.OptionalCosts is { } oc ? $" ({string.Join(", ", oc).ToLowerInvariant()})" : ""),
            ExileAfter = from == Zone.Graveyard && !escaped && !c.CurrentFace.Retrace || _castExileAfter, Spliced = spliced, CastFrom = from,
        };
        if (c.CurrentFace.Strive is { } striveCost)
            for (var i = 1; i < item.ChainTargets.FirstOrDefault()?.Count; i++) cost = cost.Plus(new Cost { Mana = striveCost });
        _payingForSpell = true;
        var paid = PayCost(cost, c, player, x, excludeSourceFromMana: true);
        _payingForSpell = false;
        if (!paid) throw new InvalidOperationException($"could not pay for {c.Name} after checking it was castable");
        if (altGrant is { } grant) Statics.UseOnce(this, grant.Source, grant.Static);
        c.ColorsSpent = LastColorsSpent;
        c.CastInOwnMainPhase = State.ActivePlayer == player && State.Step is Step.Main1 or Step.Main2;
        c.ManaSpentToCast = LastManaSpent;
        c.TreasureManaSpent = LastTreasureManaSpent;
        if (LastSacrificed.Count > 0) item.Sacrificed = LastSacrificed.ToList();
        if (LastExiled.Count > 0) item.Exiled = LastExiled.ToList();
        if (from == Zone.Command) c.CommandCasts++;
        c.XPaid = x;
        State.Stack.Add(item);
        State.ThisTurn.SpellsCast++;
        State.ThisTurn.Cast.Add(c.Id);
        State.Record.Cast.Add(c.Def.Name);
        Note($"cast {c.Name}" + (ability.CastFaceDown ? " face down" : "") + (x > 0 ? $" (X={x})" : "") + TargetText(item) + $" — pool {State.Players[player].Pool}");
        c.FaceDown = ability.CastFaceDown;
        c.Escaped = escaped;
        // "If you cast it any time a sorcery couldn't have been cast, sacrifice it at the end of the turn" (MayFlashSac).
        c.FlashSac = c.HasKeyword("MayFlashSac") && !(State.ActivePlayer == player && State.Stack.Count == 1 && State.Step is Step.Main1 or Step.Main2);

        if (c.HasKeyword("Storm"))
        {
            var copies = State.ThisTurn.SpellsCast - 1;
            for (var i = 0; i < copies; i++)
                State.Stack.Add(new StackItem
                {
                    Id = State.NextStackId++, SourceId = c.Id, Ability = ability, Controller = player, IsSpell = false, X = x,
                    ChainTargets = ChooseChainTargets(c, ability, player), Description = c.Name + " (storm copy)",
                });
            if (copies > 0) Note($"storm: {copies} cop{(copies == 1 ? "y" : "ies")}");
        }
        Fire(new TriggerEvent(TriggerMode.SpellCast, CardId: c.Id, Player: player));
        FireBecomesTarget(item);
    }

    /// <summary>Whether a cost has an X to choose: in its mana ({X}) or another part ("pay X life").</summary>
    private static bool HasX(Cost cost) => cost.Mana.XCount > 0 || cost.Parts.Any(p => p.Amount == "X");

    /// <summary>
    /// Splice (rule 702.47): as an Arcane spell is cast, other cards in hand with "splice onto Arcane"
    /// may be revealed and their splice costs paid to add their effects. Cards whose effects target aren't offered.
    /// </summary>
    private List<int>? ChooseSplices(Card spell, int player, int x, ref Cost cost)
    {
        List<int>? spliced = null;
        foreach (var id in State.Players[player].Hand.ToList())
        {
            var other = State[id];
            if (id == spell.Id || other.IsBlank || other.CurrentFace.Splice is not { } splice || !spell.Types.HasSubtype(splice.Onto)) continue;
            if (other.CurrentFace.SpellAbilities.Count == 0 || other.CurrentFace.SpellAbilities[0].Chain().Any(a => a.TargetFilter is not null)) continue;
            var withSplice = cost.Plus(splice.Cost);
            _payingForSpell = true;
            var affordable = CanPayCost(withSplice, spell, player, x, excludeSourceFromMana: true);
            _payingForSpell = false;
            if (!affordable || !Decider.ChooseYesNo(this, player, other, $"splice {other.Name} onto {spell.Name}?")) continue;
            cost = withSplice;
            (spliced ??= new()).Add(id);
            Note($"  splice {other.Name} onto {spell.Name}");
        }
        return spliced;
    }

    // A cast an effect allows mid-resolution ("cast it without paying its mana cost"): which card, and whether it's exiled afterwards.
    private int _freeCast = -1;
    private bool _castExileAfter;
    /// <summary>Strive: how many targets beyond the first the spell being cast can pay for.</summary>
    private int _striveExtra;

    /// <summary>The spell abilities an effect could cast <paramref name="c"/> with now (-1 = as a permanent spell), ignoring timing.</summary>
    private List<int> CastableFromEffect(Card c, bool free)
    {
        var saved = _freeCast;
        _freeCast = free ? c.Id : -1;
        try
        {
            var options = c.Types.IsPermanent && c.CurrentFace.SpellAbilities.Count == 0
                ? new List<int> { -1 } : Enumerable.Range(0, c.CurrentFace.SpellAbilities.Count).ToList();
            return options.Where(i => CanCast(c, i)).ToList();
        }
        finally { _freeCast = saved; }
    }

    public bool CanCastFromEffect(Card c, bool free) => CastableFromEffect(c, free).Count > 0;

    // Miracle (rule 702.94): while its trigger resolves, the card may be cast for its miracle cost.
    private int _miracleCast = -1;

    private List<int> MiracleOptions(Card c)
    {
        var saved = _miracleCast;
        _miracleCast = c.Id;
        try
        {
            return Enumerable.Range(0, c.CurrentFace.SpellAbilities.Count)
                .Where(i => c.CurrentFace.SpellAbilities[i].AltCondition == "Miracle" && CanCast(c, i)).ToList();
        }
        finally { _miracleCast = saved; }
    }

    public bool CanCastMiracle(Card c) => c.Zone == Zone.Hand && MiracleOptions(c).Count > 0;

    public void CastMiracle(Card c)
    {
        var options = MiracleOptions(c);
        if (options.Count == 0) return;
        var index = options.Count == 1 ? options[0]
            : options[Decider.ChooseOption(this, c.Controller, c, options.Select(i => c.CurrentFace.SpellAbilities[i].Description).ToList())];
        _miracleCast = c.Id;
        try { CastSpell(c, index); }
        finally { _miracleCast = -1; }
    }

    /// <summary>Casts a card as part of an effect's resolution (Play$), optionally without paying its mana cost.</summary>
    public void CastFromEffect(Card c, bool free, bool exileAfter)
    {
        var options = CastableFromEffect(c, free);
        if (options.Count == 0) return;
        var index = options.Count == 1 ? options[0]
            : options[Decider.ChooseOption(this, c.Controller, c, options.Select(i => i < 0 ? c.Name : c.CurrentFace.SpellAbilities[i].Description).ToList())];
        _freeCast = free ? c.Id : -1;
        _castExileAfter = exileAfter;
        try { CastSpell(c, index); }
        finally { _freeCast = -1; _castExileAfter = false; }
    }

    /// <summary>The creature to encode a cipher card on: the one most likely to connect (evasive, then biggest), or none.</summary>
    private int? ChooseCipherCreature(Card card, int player)
    {
        var creatures = State.BattlefieldCards.Where(c => c.Controller == player && c.Types.Is(CardType.Creature)).ToList();
        if (creatures.Count == 0 || !Decider.ChooseYesNo(this, player, card, $"encode {card.Name} on a creature?")) return null;
        return creatures.OrderByDescending(c => c.HasKeyword("Flying") || c.HasKeyword("Unblockable")).ThenByDescending(c => c.Power).First().Id;
    }

    /// <summary>Puts a copy of a spell on the stack (rule 707.10), with new targets if allowed.</summary>
    public void CopySpell(StackItem original, int controller, bool newTargets, List<List<TargetRef>>? givenTargets = null)
    {
        var source = State[original.SourceId];
        var targets = givenTargets ?? original.ChainTargets.Select(t => new List<TargetRef>(t)).ToList();
        // "You may choose new targets for the copy" (rule 707.10c): where none are chosen, it keeps the original's.
        if (newTargets)
        {
            var chosen = ChooseChainTargets(source, original.Ability, controller);
            for (var i = 0; i < chosen.Count; i++)
                if (chosen[i].Count > 0)
                {
                    while (targets.Count <= i) targets.Add(new());
                    targets[i] = chosen[i];
                }
        }
        // A copied triggered ability keeps what it triggered on (Strionic Resonator).
        var item = original.IsTriggered
            ? original.Clone(State.NextStackId++, controller)
            : new StackItem
            {
                Id = State.NextStackId++, SourceId = original.SourceId, Ability = original.Ability, Controller = controller, IsSpell = false, X = original.X,
                Description = source.Name + " (copy)",
            };
        item.ChainTargets = targets;
        State.Stack.Add(item);
        Note($"copy {item.Description}" + TargetText(item));
        if (original.Ability.Kind == AbilityKind.Spell) Fire(new TriggerEvent(TriggerMode.SpellCopied, CardId: source.Id, Player: controller));
    }

    public void ActivateAbility(Card c, int abilityIndex)
    {
        var ab = c.Activated[abilityIndex];
        var player = c.Controller;
        var cost = AbilityCost(c, ab);
        var x = 0;
        if (HasX(cost)) x = Decider.ChooseX(this, player, c, MaxAffordableX(cost, c, player));
        var targets = ChooseChainTargets(c, ab, player, x);
        if (ab["Planeswalker"] == "True") c.LoyaltyActivatedTurn = State.Turn;
        if (ab["ActivationLimit"] is not null)
        {
            c.ActivationUses ??= new();
            var (usedTurn, used) = c.ActivationUses.GetValueOrDefault(ab);
            c.ActivationUses[ab] = (State.Turn, usedTurn == State.Turn ? used + 1 : 1);
        }
        if (ab.IsCycling) Fire(new TriggerEvent(TriggerMode.Cycled, CardId: c.Id, Player: player));
        if (!PayCost(cost, c, player, x, excludeSourceFromMana: ab.Cost.Tap))
            throw new InvalidOperationException($"could not pay for {c.Name}'s ability after checking it was activatable");
        var item = new StackItem
        {
            Id = State.NextStackId++, SourceId = c.Id, Ability = ab, Controller = player, X = x, ChainTargets = targets,
            Description = $"{c.Name}: {ab.Description}", Sacrificed = LastSacrificed.Count > 0 ? LastSacrificed.ToList() : null,
            Exiled = LastExiled.Count > 0 ? LastExiled.ToList() : null,
        };
        Note($"activate {c.Name}: {ab.Description}" + TargetText(item));
        if (!ab.IsSpecialAction) Fire(new TriggerEvent(TriggerMode.AbilityCast, CardId: c.Id, Player: player, Activated: item));
        if (!ab.IsSpecialAction && !ab.IsManaAbility) FireBecomesTarget(item);
        if (ab.IsSpecialAction) RunChain(item);   // turning a permanent face up doesn't use the stack (rule 116.2b)
        else if (ab.IsManaAbility)
        {
            RunChain(item);   // mana abilities don't use the stack (rule 605.3)
            if (ab.Cost.Tap) Fire(new TriggerEvent(TriggerMode.TapsForMana, CardId: c.Id, Player: player));
            Note($"  pool {State.Players[player].Pool}");
        }
        else State.Stack.Add(item);
    }

    private static string TargetText(StackItem item)
    {
        var all = item.ChainTargets.SelectMany(t => t).ToList();
        return all.Count == 0 ? "" : " → " + string.Join(", ", all);
    }

    /// <summary>"When this enters, if its evoke cost was paid, its controller sacrifices it."</summary>
    private static readonly TriggerDef EvokeTrigger = new()
    {
        P = new Forge.ForgeParams("Mode$ Evoked"), Mode = "Evoked",
        Execute = new AbilityDef { P = new Forge.ForgeParams("DB$ Sacrifice"), Api = "Sacrifice", Kind = AbilityKind.TriggerEffect },
    };

    /// <summary>A permanent spell's "ability": resolving puts the card onto the battlefield.</summary>
    public static readonly AbilityDef PermanentSpell = new()
    {
        P = new Forge.ForgeParams("SP$ PermanentSpell"), Api = "PermanentSpell", Kind = AbilityKind.Spell,
    };

    // ---- targets ----------------------------------------------------------------------

    public List<TargetRef> TargetCandidates(Card source, AbilityDef ab, int controller, StackItem? trigger = null)
    {
        var list = new List<TargetRef>();
        if (ab.TargetFilter is not { } f) return list;
        var x = new EffectContext
        {
            Game = this, Ability = ab,
            Item = new StackItem
            {
                SourceId = source.Id, Ability = ab, Controller = controller, TriggeredPlayer = trigger?.TriggeredPlayer,
                TriggeredCardId = trigger?.TriggeredCardId, TriggeredSourceId = trigger?.TriggeredSourceId,
            },
        };
        if (f.CanTargetPlayers)
            foreach (var p in State.Players)
                if (!p.Lost && f.MatchesPlayer(p.Index, x)) list.Add(TargetRef.OfPlayer(p.Index));
        // "Target triggered ability you control": a triggered ability on the stack, named by its source.
        if (ab["TargetType"] is { } tt && tt.StartsWith("Triggered", StringComparison.Ordinal))
        {
            foreach (var item in State.Stack)
                if (item.IsTriggered && (tt != "Triggered.YouCtrl" || item.Controller == controller) && f.MatchesCard(State[item.SourceId], x)
                    && !list.Any(t => t.CardId == item.SourceId))
                    list.Add(TargetRef.OfCard(item.SourceId));
            return list;
        }
        if (f.CanTargetCards)
        {
            foreach (var zone in TargetZones(ab))
                foreach (var c in CardsIn(zone))
                    if (f.MatchesCard(c, x) && (ab["TargetType"] != "Spell.singleTarget" || SingleTarget(c))) list.Add(TargetRef.OfCard(c.Id));
        }
        return list;
    }

    /// <summary>A spell on the stack with exactly one target ("target spell with a single target").</summary>
    private bool SingleTarget(Card spell) =>
        State.Stack.LastOrDefault(i => i.IsSpell && i.SourceId == spell.Id) is { } item && item.ChainTargets.Sum(t => t.Count) == 1;

    /// <summary>
    /// Where an ability's target cards are: TgtZone$, or for moving a card from somewhere other than the
    /// battlefield ("return target creature card from a graveyard"), where it moves from.
    /// </summary>
    private static IEnumerable<Zone> TargetZones(AbilityDef ab)
    {
        var spec = ab["TgtZone"] ?? (ab.Api == "ChangeZone" ? ab["Origin"] : null);
        if (spec is null) return new[] { ab["TargetType"]?.StartsWith("Spell", StringComparison.Ordinal) ?? false ? Zone.Stack : Zone.Battlefield };
        var zones = new List<Zone>();
        foreach (var w in spec.Split(','))
            if (Enum.TryParse<Zone>(w.Trim(), out var z) && z is Zone.Graveyard or Zone.Exile or Zone.Hand or Zone.Stack or Zone.Battlefield) zones.Add(z);
        return zones.Count > 0 ? zones : new[] { Zone.Battlefield };
    }

    /// <summary>How many targets an ability takes: its TargetMin$/TargetMax$, worked out now if they're expressions.</summary>
    public (int Min, int Max) TargetBounds(Card source, AbilityDef ab, int controller, int x)
    {
        if (ab.TargetMinExpr is null && ab.TargetMaxExpr is null) return (ab.TargetMin, ab.TargetMax);
        var ctx = new EffectContext { Game = this, Ability = ab, Item = new StackItem { SourceId = source.Id, Ability = ab, Controller = controller, X = x } };
        int Eval(string e) => e == "OneEach" ? State.Players.Count(p => !p.Lost) : Math.Max(0, Effect.EvalAmount(ctx, e));
        return (ab.TargetMinExpr is { } mn ? Eval(mn) : ab.TargetMin, ab.TargetMaxExpr is { } mx ? Eval(mx) : ab.TargetMax);
    }

    /// <summary>
    /// Targets for each link of a chain. A trigger's targets can depend on what it triggered on ("target creature that
    /// player controls"): <paramref name="trigger"/> is that stack item, before it's put on the stack.
    /// </summary>
    private List<List<TargetRef>> ChooseChainTargets(Card source, AbilityDef head, int controller, int x = 0, StackItem? trigger = null)
    {
        var result = new List<List<TargetRef>>();
        foreach (var ab in head.Chain())
        {
            if (ab.TargetFilter is null) { result.Add(new()); continue; }
            var candidates = TargetCandidates(source, ab, controller, trigger);
            // "With an equal or lesser mana value" than the target chosen before it (Puca's Mischief).
            if (ab["TargetsWithRelatedProperty"] == "LECMC" && result.LastOrDefault(r => r.Count > 0)?.FirstOrDefault().CardId is { } related)
                candidates = candidates.Where(t => t.CardId is { } id && State[id].ManaValue <= State[related].ManaValue).ToList();
            // "Each of up to two other targets": none chosen earlier in the chain.
            if (ab["TargetUnique"] == "True") candidates = candidates.Where(t => !result.Any(earlier => earlier.Contains(t))).ToList();
            var (tmin, tmax) = TargetBounds(source, ab, controller, x);
            // "For each opponent, … target permanent that player controls": one target among each player's.
            if (ab["TargetsForEachPlayer"] == "True")
            {
                var each = new List<TargetRef>();
                foreach (var group in candidates.Where(t => t.CardId is not null).GroupBy(t => State[t.CardId!.Value].Controller))
                    each.AddRange(Decider.ChooseTargets(this, controller, source, ab, group.ToList(), 1, 1));
                result.Add(each);
                continue;
            }
            // "This spell costs {1} more to cast for each target beyond the first" (Fireball): the cost was paid for one
            // target, and dividing damage evenly among more deals no more in all, so it takes exactly one.
            if (source.Statics.Any(st => st.Mode == "RaiseCost" && st["Relative"] == "True")) tmin = tmax = Math.Min(tmax, 1);
            if (source.Zone == Zone.Stack && source.CurrentFace.Strive is not null && ab == head) tmax = Math.Min(tmax, 1 + _striveExtra);
            var max = Math.Min(tmax, candidates.Count);
            var min = Math.Min(tmin, max);
            result.Add(max == 0 ? new() : Decider.ChooseTargets(this, controller, source, ab, candidates, min, max));
        }
        return result;
    }

    // ---- resolving --------------------------------------------------------------------

    private int? _resolvingSpell;

    public void ResolveTop()
    {
        var item = State.Stack[^1];
        State.Stack.RemoveAt(State.Stack.Count - 1);
        var outer = _resolvingSpell;
        _resolvingSpell = item.IsSpell ? item.SourceId : null;
        try { Resolve(item); }
        finally { _resolvingSpell = outer; }
    }

    private void Resolve(StackItem item)
    {
        var source = State[item.SourceId];
        Note($"resolve {item.Description}");

        var afterwards = item.ExileAfter ? Zone.Exile : Zone.Graveyard;
        Statics.Apply(this);
        if (item.IsSpell && item.Ability.Api == "PermanentSpell")
        {
            if (source.Zone != Zone.Stack) return;
            if (item.Ability.TargetFilter is not null)   // an Aura: attach to its target, or fizzle
            {
                var target = item.ChainTargets.FirstOrDefault()?.FirstOrDefault();
                var targetZone = item.Ability["TgtZone"] == "Graveyard" ? Zone.Graveyard : Zone.Battlefield;
                if (target?.Player is { } enchanted && !State.Players[enchanted].Lost)
                {
                    source.EnchantedPlayer = enchanted;   // "Enchant player" (curses)
                    MoveTo(source, Zone.Battlefield);
                    return;
                }
                if (target?.CardId is not { } to || State[to].Zone != targetZone)
                {
                    Note($"  {item.Description} fizzles (no legal targets)");
                    MoveTo(source, afterwards);
                    return;
                }
                source.AttachedTo = to;
            }
            MoveTo(source, Zone.Battlefield);
            if (source.FlashSac && source.Zone == Zone.Battlefield) State.ThisTurn.AtEndStep.Add((source.Id, "Sacrifice"));
            source.FlashSac = false;
            if (item.Ability.Evoke && source.Zone == Zone.Battlefield)
                _pending.Add(new PendingTrigger(source.Id, EvokeTrigger, new TriggerEvent(TriggerMode.Immediate, CardId: source.Id), source.Controller));
            return;
        }
        if (TargetsAllGone(item))
        {
            Note($"  {item.Description} fizzles (no legal targets)");
            if (item.IsSpell && source.Zone == Zone.Stack) MoveTo(source, afterwards);
            return;
        }
        // Ascend on an instant or sorcery (rule 702.131a): checked as it resolves.
        if (item.IsSpell && source.HasKeyword("Ascend")) GrantBlessingIfTen(item.Controller);
        RunChain(item);
        // Spliced cards' effects, in the order they were spliced (rule 702.47b).
        foreach (var id in item.Spliced ?? new List<int>())
            if (!State.GameOver && State[id].CurrentFace.SpellAbilities.Count > 0) RunLinks(item, State[id].CurrentFace.SpellAbilities[0]);
        // Rebound: cast from hand, it's exiled and may be cast again at your next upkeep (rule 702.88).
        if (item.IsSpell && source.Zone == Zone.Stack && item.CastFrom == Zone.Hand && !item.ExileAfter && source.CurrentFace.Rebound is { } rebound)
        {
            MoveTo(source, Zone.Exile);
            if (source.Zone == Zone.Exile)
                State.Delayed.Add(new DelayedTrigger(rebound, source.Id, item.Controller, new List<int>(), State.Turn, ThisTurn: false, NextTurn: true));
            return;
        }
        if (item.IsSpell && source.Zone == Zone.Stack)
        {
            // An Adventure cast from hand goes on an adventure: exiled, castable later as the creature.
            var adventure = source.Def.Layout == "Adventure" && source.Face == 1 && !item.ExileAfter;
            // Buyback (rule 702.27): paid, it goes back to its owner's hand.
            var bought = !item.ExileAfter && (source.PaidOptional?.Contains("Buyback") ?? false);
            // Cipher: "then you may exile this spell card encoded on a creature you control" (rule 702.99a).
            if (!source.IsToken && source.HasKeyword("Cipher") && ChooseCipherCreature(source, item.Controller) is { } host)
            {
                MoveTo(source, Zone.Exile);
                source.EncodedOn = host;
                Note($"  {source.Name} is encoded on {State[host].Name}");
                return;
            }
            MoveTo(source, adventure ? Zone.Exile : bought ? Zone.Hand : afterwards);
            if (adventure && source.Zone == Zone.Exile) source.OnAdventure = true;
        }
    }

    /// <summary>Ascend on a permanent (rule 702.131b): with ten permanents, its controller gets the city's blessing for good.</summary>
    private void CheckAscend()
    {
        foreach (var p in State.Players)
            if (!p.CityBlessing && State.BattlefieldCards.Any(c => c.Controller == p.Index && c.HasKeyword("Ascend")))
                GrantBlessingIfTen(p.Index);
    }

    private void GrantBlessingIfTen(int player)
    {
        if (State.BattlefieldCards.Count(c => c.Controller == player) < 10) return;
        State.Players[player].CityBlessing = true;
        Note($"player {player} gets the city's blessing");
    }

    /// <summary>
    /// State triggers ("when this has no ice counters on it", rule 603.8): trigger whenever the condition holds,
    /// but not again while the ability is waiting or on the stack.
    /// </summary>
    private void CheckStateTriggers()
    {
        foreach (var c in State.BattlefieldCards.ToList())
        {
            if (c.IsBlank) continue;
            foreach (var t in c.Triggers)
            {
                if (t.Mode != "Always" || t.Execute is null) continue;
                if (_pending.Any(p => p.SourceId == c.Id && p.Trigger == t) || State.Stack.Any(i => i.SourceId == c.Id && i.Ability == t.Execute)) continue;
                var x = new EffectContext { Game = this, Ability = t.Execute, FaceOverride = t.Face, Item = new StackItem { SourceId = c.Id, Ability = t.Execute, Controller = c.Controller } };
                if (Presence.Hold(t.P, x)) _pending.Add(new PendingTrigger(c.Id, t, new TriggerEvent(TriggerMode.Always, CardId: c.Id), c.Controller));
            }
        }
    }

    private bool TargetsAllGone(StackItem item)
    {
        var all = item.ChainTargets.SelectMany(t => t).ToList();
        if (all.Count == 0) return false;
        return all.All(t => t.Player is { } p ? State.Players[p].Lost : State[t.CardId!.Value].Zone is Zone.None);
    }

    public void RunChain(StackItem item) => RunLinks(item, item.Ability);

    /// <summary>Runs a chain an effect refers to by name (coin flip outcomes, repeated steps) for the same stack item.</summary>
    public void RunSideChain(EffectContext x, AbilityDef head) => RunLinks(x.Item, head);

    /// <summary>A triggered ability whose effect has a cost: "you may pay {B}. If you do, …" (ImmediateTrigger pays its own).</summary>
    private static bool HasTriggerCost(AbilityDef exec) =>
        exec.Kind == AbilityKind.TriggerEffect && exec["Cost"] is not null && exec.Api != "ImmediateTrigger";

    private void RunLinks(StackItem item, AbilityDef head)
    {
        // "Whenever …, you may pay {B}. If you do, …": paid as it resolves, or nothing happens.
        if (HasTriggerCost(head))
        {
            var host = State[item.SourceId];
            if (!CanPayCost(head.Cost, host, item.Controller, item.X, excludeSourceFromMana: false)
                || !head.Cost.Mandatory && !Decider.ChooseYesNo(this, item.Controller, host, $"pay {string.Join(" ", head.Cost.Parts.Select(q => q.Raw).Prepend(head.Cost.Mana.ToString()))} for {host.Name}?")
                || !PayCost(head.Cost, host, item.Controller, item.X, excludeSourceFromMana: false))
                return;
        }
        for (var ab = head; ab is not null && !State.GameOver; ab = ab.Sub)
        {
            var ctx = new EffectContext { Game = this, Item = item, Ability = ab };
            if (!Conditions.Hold(ctx)) continue;
            if (ab["OptionalDecider"] is not null && !DecidesYes(ctx)) continue;
            var handler = Effects.Get(ab.Api) ?? throw new InvalidOperationException($"no handler for {ab.Api}");
            if (ab.UnlessCost is { } unless)
            {
                if (ab.UnlessCostAmount is { } amount)
                    unless = new Cost { Mana = ManaCost.Zero.WithExtraGeneric(Math.Max(0, Effect.EvalAmount(ctx, amount))) };
                var paid = PaysUnlessCost(ctx, unless);
                // UnlessSwitched$: it happens only if the cost is paid ("you may pay {X}. If you do, draw X cards").
                if (ab["UnlessSwitched"] == "True") paid = !paid;
                // UnlessResolveSubs$ WhenPaid: paying skips this effect and goes on; not paying does it and stops.
                if (ab["UnlessResolveSubs"] == "WhenPaid")
                {
                    if (paid) continue;
                    handler.Resolve(ctx);
                    break;
                }
                if (paid) continue;
            }
            handler.Resolve(ctx);
        }
    }

    /// <summary>A context for a card's own expressions outside any stack item (statics, cost checks).</summary>
    public EffectContext ContextFor(Card c) => new()
    {
        Game = this, Ability = PermanentSpell,
        Item = new StackItem { SourceId = c.Id, Ability = PermanentSpell, Controller = c.Controller, X = c.XPaid },
    };

    /// <summary>"You may …" (OptionalDecider$): whoever decides is asked; goldfish opponents always decline.</summary>
    private bool DecidesYes(EffectContext x)
    {
        var who = PlayerFor(x, x.Ability["OptionalDecider"]);
        return who is { } p && !State.Players[p].IsGoldfish && Decider.ChooseYesNo(this, p, x.Host, x.Ability.Description + "?");
    }

    private int? PlayerFor(EffectContext x, string? spec) => spec switch
    {
        "You" => x.Controller,
        null or "TargetedController" or "ThisTargetedController" or "Targeted" => x.OwnOrParentTargets.Select(t => t.Player ?? State[t.CardId!.Value].Controller).Cast<int?>().FirstOrDefault(),
        "TriggeredActivator" or "TriggeredPlayer" => x.Item.TriggeredPlayer,
        "TriggeredCardController" => x.Item.TriggeredCardId is { } tc ? State[tc].Controller : null,
        "Remembered" or "Player.IsRemembered" => x.Item.IteratedPlayer,
        _ => null,   // "Opponent"/"Player": only goldfish opponents would act, and they never do
    };

    /// <summary>
    /// "… unless [payer] pays [cost]": true if the payer pays, so the effect doesn't happen.
    /// Goldfish opponents pay only to survive.
    /// </summary>
    private bool PaysUnlessCost(EffectContext x, Cost cost)
    {
        if (PlayerFor(x, x.Ability["UnlessPayer"]) is not { } p) return false;
        // Paying to make something happen ("may sacrifice a land; if they do, copy this"): a goldfish never does.
        if (State.Players[p].IsGoldfish) return x.Ability["UnlessSwitched"] != "True" && GoldfishPaysToSurvive(x, cost, p);
        if (!CanPayCost(cost, x.Host, p, x.X, excludeSourceFromMana: false)) return false;
        var what = x.Ability["UnlessResolveSubs"] == "WhenPaid" ? "to go on"
            : x.Ability["UnlessSwitched"] == "True" ? "to " + x.Ability.Description : "to avoid " + x.Ability.Description;
        if (!Decider.ChooseYesNo(this, p, x.Host, $"pay {(cost.HasNonManaParts ? string.Join(" ", cost.Parts.Select(q => q.Raw)) : cost.Mana)} {what}?")) return false;
        PayCost(cost, x.Host, p, x.X, excludeSourceFromMana: false);
        Note($"  paid to avoid: {x.Ability.Description}");
        return true;
    }

    /// <summary>
    /// A goldfish opponent takes the life loss or damage unless it would kill them; then they pay if they can, with
    /// cards from their hand or life (CONTRACT.md §3). They have no mana or permanents to pay with.
    /// </summary>
    private bool GoldfishPaysToSurvive(EffectContext x, Cost cost, int p)
    {
        var ab = x.Ability;
        var player = State.Players[p];
        var hit = ab.Api switch
        {
            "LoseLife" when DrawEffect.Players(x).Contains(p) => Effect.EvalAmount(x, ab["LifeAmount"] ?? "1"),
            "DealDamage" when (ab["Defined"] is { } d ? Defined.Players(d, x) : x.Targets.Where(t => t.Player is not null).Select(t => t.Player!.Value)).Contains(p)
                => Effect.EvalAmount(x, ab["NumDmg"] ?? "1"),
            _ => 0,
        };
        if (hit < player.Life || cost.Mana.ManaValue > 0) return false;
        int discard = 0, life = 0;
        foreach (var part in cost.Parts)
        {
            var n = Effect.EvalAmount(x, part.Amount);
            if (part.Kind == CostPartKind.Discard) discard += n;   // any kind of card: assume they have it
            else if (part.Kind == CostPartKind.PayLife) life += n;
            else return false;
        }
        if (discard > player.AbstractHand || life >= player.Life) return false;
        if (discard > 0) DiscardUnknown(p, discard);
        if (life > 0) PayLife(p, life);
        Note($"  opponent {p} pays to avoid: {ab.Description}");
        return true;
    }

    // ---- state-based actions & game end -----------------------------------------------

    public void CheckStateBasedActions()
    {
        if (State.GameOver) return;
        Statics.Apply(this);
        foreach (var p in State.Players)
        {
            if (p.Lost) continue;
            if (p.Life <= 0) Lose(p, "life");
            else if (p.Poison >= 10) Lose(p, "poison");
            else if (p.DrewFromEmpty) Lose(p, "drew from an empty library");
            else if (p.CommanderDamageTaken.Values.Any(d => d >= 21)) Lose(p, "commander damage");
            else if (p.IsGoldfish && p.AbstractLibrary <= 0) Lose(p, "decked (loses at their next draw)");
        }
        foreach (var c in State.BattlefieldCards.ToList())
        {
            if (c.Types.Is(CardType.Creature))
            {
                if (c.Toughness <= 0) MoveTo(c, Zone.Graveyard);
                else if (c.Damage >= c.Toughness) Destroy(c, regenerate: true);
            }
            if (c.Zone == Zone.Battlefield && c.Types.Is(CardType.Planeswalker) && c.CounterCount("LOYALTY") <= 0) MoveTo(c, Zone.Graveyard);   // rule 704.5i
            // Auras attached to nothing go to the graveyard; Equipment just falls off (rules 704.5m-n).
            if (c.Zone != Zone.Battlefield) continue;
            var host = c.AttachedTo >= 0 ? State[c.AttachedTo] : null;
            // "Enchant creature card in a graveyard" (Animate Dead) holds until it changes what it enchants.
            var attachedOk = c.EnchantedPlayer >= 0 && !State.Players[c.EnchantedPlayer].Lost || host is not null && (host.Zone == Zone.Battlefield
                || host.Zone == Zone.Graveyard && c.CurrentFace.AuraSpell?["TgtZone"] == "Graveyard"
                   && !(c.Animations?.Any(a => a.Ability["RemoveKeywords"] is not null) ?? false));
            if (c.Types.HasSubtype("Aura") && !attachedOk) MoveTo(c, Zone.Graveyard);
            else if (c.AttachedTo >= 0 && (!attachedOk || c.Types.HasSubtype("Equipment") && !host!.Types.Is(CardType.Creature)))
            {
                Fire(new TriggerEvent(TriggerMode.Unattached, CardId: c.Id, TargetCardId: c.AttachedTo));
                c.AttachedTo = -1;
            }
        }
        CheckEffectLinks();
        CheckStateTriggers();
        CheckAscend();
        FireZoneBatch();
        FireDamageBatch();
        // A Saga with all its chapters done and none still waiting to resolve is sacrificed (rule 714.4).
        foreach (var saga in State.BattlefieldCards.Where(c => c.CurrentFace.Chapters.Count > 0).ToList())
            if (saga.CounterCount("LORE") >= saga.CurrentFace.Chapters.Count && !_pending.Any(t => t.SourceId == saga.Id)
                && !State.Stack.Any(i => i.SourceId == saga.Id && saga.CurrentFace.Chapters.Any(t => t.Execute == i.Ability || (t.Modes?.Contains(i.Ability) ?? false))))
                Sacrifice(saga);
        // Legend rule (704.5j): the controller keeps one (offered newest first; a copy may be the one worth keeping).
        var legends = State.BattlefieldCards.Where(c => c.Types.Has(SuperType.Legendary)).GroupBy(c => (c.Controller, c.Name)).Where(g => g.Count() > 1)
            .Where(g => !g.Any(c => Statics.IgnoresLegendRule(this, c)));
        foreach (var grp in legends.ToList())
        {
            var ordered = grp.OrderByDescending(c => c.ControlledSinceTurn).ThenByDescending(c => c.Id).ToList();
            var keep = ordered.Count == 1 || State.Players[grp.Key.Controller].IsGoldfish ? 0
                : Decider.ChooseOption(this, grp.Key.Controller, ordered[0], ordered.Select(c => "keep " + c.Name + (c.CopyDef is not null ? $" (copy: {c.Def.Name})" : "")).ToList());
            foreach (var old in ordered.Where((_, i) => i != keep).ToList())
                MoveTo(old, Zone.Graveyard);
        }

        if (!State.GameOver && State.Players.Where(p => p.Index != Me).All(p => p.Lost))
            Win(Me, State.Record.LastHitBy ?? "opponents eliminated");
    }

    private void Lose(Player p, string reason)
    {
        if (Replacements.CantHappen(this, "GameLoss", p.Index)) return;
        p.Lost = true;
        p.LossReason = reason;
        Note($"player {p.Index} loses ({reason})");
        if (p.Index == Me)
        {
            State.GameOver = true;
            State.Winner = null;
            State.WinReason = null;
        }
    }

    public void Win(int player, string reason)
    {
        if (State.GameOver) return;
        State.GameOver = true;
        State.Winner = player;
        State.WinReason = reason;
        Note($"player {player} wins ({reason})");
    }

    public bool Won => State.GameOver && State.Winner == Me;

    /// <summary>Destroys a permanent unless it's indestructible or regenerates. True if it was destroyed.</summary>
    public bool Destroy(Card c, bool regenerate)
    {
        if (c.Zone != Zone.Battlefield || c.HasKeyword("Indestructible")) return false;
        if (regenerate && c.RegenShields > 0)
        {
            c.RegenShields--;
            c.Tapped = true;
            c.Damage = 0;
            State.Attackers.Remove(c.Id);
            Note($"  {c.Name} regenerates");
            return false;
        }
        MoveTo(c, Zone.Graveyard);
        return true;
    }

    /// <summary>Removes up to <paramref name="n"/> counters of a kind, each one a "counter removed" event.</summary>
    public int RemoveCounters(Card c, string kind, int n)
    {
        var removed = Math.Min(n, c.CounterCount(kind));
        if (removed <= 0) return 0;
        c.Counters![kind] -= removed;
        for (var i = 0; i < removed; i++) Fire(new TriggerEvent(TriggerMode.CounterRemoved, CardId: c.Id, Player: c.Controller, CounterType: kind, Amount: 1));
        return removed;
    }

    public static void AddCounters(Card c, string kind, int n)
    {
        if (n <= 0) return;
        c.Counters ??= new();
        c.Counters[kind] = c.CounterCount(kind) + n;
    }

    /// <summary>Puts counters on a permanent; lore counters on a Saga trigger its chapters (rule 714.2b).</summary>
    public void PutCounters(Card c, string kind, int n)
    {
        n = Replacements.Amount(this, "AddCounter", n, targetCard: c, counterType: kind);
        var before = c.CounterCount(kind);
        AddCounters(c, kind, n);
        if (n > 0) Fire(new TriggerEvent(TriggerMode.CounterAddedOnce, CardId: c.Id, Player: c.Controller, Amount: n, CounterType: kind));
        if (kind != "LORE" || c.Zone != Zone.Battlefield) return;
        var chapters = c.CurrentFace.Chapters;
        for (var k = before + 1; k <= Math.Min(c.CounterCount(kind), chapters.Count); k++)
            _pending.Add(new PendingTrigger(c.Id, chapters[k - 1], new TriggerEvent(TriggerMode.Chapter, CardId: c.Id), c.Controller));
    }

    /// <summary>How many tokens an effect creating <paramref name="n"/> of <paramref name="def"/> creates after replacements (Doubling Season).</summary>
    public int TokenCount(CardDef def, int owner, int n)
    {
        var probe = new Card { Id = State.Cards.Count, Def = def, Owner = owner, Controller = owner, Zone = Zone.Battlefield, IsToken = true };
        return Replacements.Amount(this, "CreateToken", n, targetCard: probe, targetPlayer: owner);
    }

    /// <summary>Creates a token on the battlefield under its owner's control.</summary>
    public Card CreateToken(CardDef def, int owner, bool tapped, int? power = null, int? toughness = null)
    {
        var c = new Card
        {
            Id = State.Cards.Count, Def = def, Owner = owner, Controller = owner, Zone = Zone.None, IsToken = true,
            TokenPower = power, TokenToughness = toughness,
        };
        State.Cards.Add(c);
        Note($"  create {def.Name}" + (power is not null ? $" {c.Power}/{c.Toughness}" : ""));
        MoveTo(c, Zone.Battlefield, tapped: tapped);
        return c;
    }

    /// <summary>The combined color identity of a player's commanders (for "Combo ColorIdentity" mana).</summary>
    public ColorMask CommanderIdentity(int player)
    {
        ColorMask m = 0;
        foreach (var c in State.Cards)
            if (c.IsCommander && c.Owner == player) m |= c.Def.ColorIdentity;
        return m;
    }
}
