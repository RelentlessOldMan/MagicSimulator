namespace MagicSim.Core.Engine;

public sealed class GameSettings
{
    public int StartingLife { get; init; } = 40;
    public int Opponents { get; init; } = 2;
    public int OpponentLibrary { get; init; } = 99;
    public bool OnThePlay { get; init; } = true;
    public int MaxTurn { get; init; } = 10;
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

    public Game(GameState state, GameSettings settings, IDecider decider)
    {
        State = state;
        Settings = settings;
        Decider = decider;
    }

    public Game Clone(IDecider? decider = null)
    {
        var g = new Game(State.Clone(), Settings, decider ?? Decider) { _actionsThisTurn = _actionsThisTurn, MulligansTaken = MulligansTaken, StaticsApplied = StaticsApplied };
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
            players.Add(new Player { Index = i, Life = settings.StartingLife, IsGoldfish = true, AbstractLibrary = settings.OpponentLibrary });
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
            State.Record.Seen.Add(State[id].Name);
        }
        Note($"kept {me.Hand.Count}: {string.Join(", ", KeptHand)}" + (Bottomed.Count > 0 ? $" (bottomed {string.Join(", ", Bottomed)})" : ""));
    }

    private int BottomCount(int mulligans) => Math.Max(0, Settings.FreeFirstMulligan ? mulligans - 1 : mulligans);

    /// <summary>Plays turns until someone wins or <see cref="GameSettings.MaxTurn"/> is reached.</summary>
    public void PlayGame()
    {
        Mulligans();
        while (!State.GameOver && State.Turn < Settings.MaxTurn)
            RunTurn();
    }

    /// <summary>Steps of a turn in order. Each runs its start-of-step actions, then priority (except untap and cleanup).</summary>
    private static readonly Step[] TurnSteps =
    {
        Step.Untap, Step.Upkeep, Step.Draw, Step.Main1, Step.BeginCombat, Step.DeclareAttackers, Step.CombatDamage,
        Step.EndCombat, Step.Main2, Step.End, Step.Cleanup,
    };

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
        RunStepsFrom(Array.IndexOf(TurnSteps, State.Step) + 1);
    }

    private void RunStepsFrom(int index)
    {
        for (var i = index; i < TurnSteps.Length && !State.GameOver; i++)
            ExecuteStep(TurnSteps[i]);
        if (State.Step == Step.Cleanup || State.GameOver) EndTurnBookkeeping();
    }

    private void EndTurnBookkeeping()
    {
        if (State.Record.ManaByTurn.Count >= State.Turn) return;
        State.Record.ManaByTurn.Add(State.ThisTurn.MaxManaSeen);
        State.Record.StormMax = Math.Max(State.Record.StormMax, State.ThisTurn.SpellsCast);
    }

    private void ExecuteStep(Step step)
    {
        SetStep(step);
        switch (step)
        {
            case Step.Untap:
                ExpireEffects("untap");
                foreach (var c in State.BattlefieldCards)
                    if (c.Controller == Me && !c.CurrentFace.DoesntUntap) c.Tapped = false;
                return;   // no priority in the untap step
            case Step.Upkeep: FirePhase("Upkeep"); break;
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
            case Step.BeginCombat: FirePhase("BeginCombat"); break;
            case Step.DeclareAttackers: DeclareAttackers(); break;
            case Step.CombatDamage: CombatDamage(); break;
            case Step.EndCombat:
                FirePhase("EndCombat");
                State.Attackers.Clear();
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

    /// <summary>"At the beginning of the next end step" actions (tokens exiled or sacrificed at end of turn).</summary>
    private void AtBeginningOfEndStep()
    {
        foreach (var (id, action) in State.ThisTurn.AtEndStep)
        {
            var c = State[id];
            if (c.Zone != Zone.Battlefield) continue;
            Note($"{action.ToLowerInvariant()} {c.Name} (end of turn)");
            if (action == "Exile") MoveTo(c, Zone.Exile); else Sacrifice(c);
        }
        State.ThisTurn.AtEndStep.Clear();
    }

    private void SetStep(Step step)
    {
        State.Step = step;
        foreach (var p in State.Players) p.Pool.Clear();   // mana empties between steps (rule 500.4)
    }

    private void DeclareAttackers()
    {
        Statics.Apply(this);
        var candidates = State.BattlefieldCards
            .Where(c => c.Controller == Me && c.Types.Is(CardType.Creature) && !c.Tapped && !IsSick(c) && !c.HasKeyword("Defender"))
            .Select(c => c.Id).ToList();
        var attackers = candidates.Count == 0 ? new List<int>() : Decider.ChooseAttackers(this, Me, candidates);
        if (attackers.Count == 0) return;
        State.Attackers.AddRange(attackers);
        foreach (var id in attackers)
        {
            var a = State[id];
            if (!a.HasKeyword("Vigilance")) Tap(a);
            Fire(new TriggerEvent(TriggerMode.Attacks, CardId: id));
        }
        Fire(new TriggerEvent(TriggerMode.AttackersDeclared, Player: Me));
        Note("attack with " + string.Join(", ", attackers.Select(id => State[id].Name)));
    }

    private void CombatDamage()
    {
        Statics.Apply(this);
        // Goldfish opponents never block: everything hits the opponent with the least life left.
        var defender = State.Players.Where(p => p.Index != Me && !p.Lost).OrderBy(p => p.Life).ThenBy(p => p.Index).FirstOrDefault();
        if (defender is null) return;
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
        }
    }

    private void Cleanup()
    {
        var me = State.Players[Me];
        var excess = me.Hand.Count - (me.StaticMaxHandSize ?? Settings.MaxHandSize);
        if (excess > 0)
            foreach (var id in Decider.ChooseCards(this, Me, Choice.Discard, null, me.Hand.ToList(), excess, excess))
                Discard(State[id]);
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
        ExpireEffects("cleanup");
    }

    // ---- priority ---------------------------------------------------------------------

    /// <summary>
    /// Gives the simulated player priority until they pass with an empty stack. Goldfish
    /// opponents always pass, so passing with a non-empty stack resolves its top item.
    /// </summary>
    public void PriorityLoop()
    {
        while (!State.GameOver)
        {
            CheckStateBasedActions();
            if (State.GameOver) return;
            if (_pending.Count > 0) { PutTriggersOnStack(); continue; }
            TrackMana();
            if (OnCheckpoint is not null && State.Stack.Count == 0 && State.Step is Step.Main1 or Step.Main2)
                OnCheckpoint(this);

            var legal = LegalActions(Me);
            GameAction action = _actionsThisTurn >= Settings.MaxActionsPerTurn
                ? GameAction.PassAction
                : Decider.ChooseAction(this, Me, legal);
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
        var available = State.Players[Me].Pool.Total + AutoSources(Me, -1).Where(s => State[s.CardId].Zone == Zone.Battlefield).Sum(s => s.Amount);
        State.ThisTurn.MaxManaSeen = Math.Max(State.ThisTurn.MaxManaSeen, available);
    }

    public bool IsSick(Card c) =>
        c.Types.Is(CardType.Creature) && c.ControlledSinceTurn >= State.Turn && !c.HasKeyword("Haste");

    private bool IsMainPhaseEmptyStack(int player) =>
        State.ActivePlayer == player && State.Stack.Count == 0 && State.Step is Step.Main1 or Step.Main2;

    // ---- legal actions ----------------------------------------------------------------

    public List<GameAction> LegalActions(int player)
    {
        var result = new List<GameAction> { GameAction.PassAction };
        var p = State.Players[player];
        var sorcerySpeed = IsMainPhaseEmptyStack(player);

        // Identical cards in hand are interchangeable; offer each name once.
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in p.Hand.Concat(p.Command))
        {
            var c = State[id];
            if (c.Zone == Zone.Hand && !seenNames.Add(c.Name)) continue;
            if (c.IsBlank || c.IsEffect) continue;
            AddPlayOrCast(result, c, p, sorcerySpeed);

            // Abilities usable from hand (e.g. "exile this from your hand: add {R}").
            if (c.Zone == Zone.Hand)
                for (var i = 0; i < c.Activated.Count; i++)
                {
                    var ab = c.Activated[i];
                    if (ab["ActivationZone"] == "Hand" && !IsAutoManaAbility(ab) && CanActivate(c, ab, sorcerySpeed)) result.Add(new GameAction.Activate(id, i));
                }
        }

        // Cards an effect lets you play from exile, the graveyard or the top of your library.
        foreach (var id in Statics.MayPlay(this, player).Keys)
        {
            var c = State[id];
            if (c.Zone is Zone.Hand or Zone.Stack || c.IsBlank || !seenNames.Add("mayplay:" + c.Name)) continue;
            AddPlayOrCast(result, c, p, sorcerySpeed);
        }

        // Flashback: cast from the graveyard.
        foreach (var id in p.Graveyard)
        {
            var c = State[id];
            if (c.IsBlank || c.CurrentFace.Flashback is null || !seenNames.Add("graveyard:" + c.Name)) continue;
            if (!(c.Types.Is(CardType.Instant) || c.HasKeyword("Flash") || sorcerySpeed)) continue;
            for (var i = 0; i < c.CurrentFace.SpellAbilities.Count; i++)
                if (CanCast(c, i)) result.Add(new GameAction.Cast(id, i));
        }

        var seenPermanents = new HashSet<(string, bool, bool)>();
        foreach (var c in State.BattlefieldCards)
        {
            if (c.Controller != player || c.IsBlank) continue;
            if (c.Counters is null && c.Animations is null && c.StaticActivated is null && !seenPermanents.Add((c.Name, c.Tapped, IsSick(c)))) continue;
            for (var i = 0; i < c.Activated.Count; i++)
            {
                var ab = c.Activated[i];
                if (ab["ActivationZone"] is not null) continue;
                if (IsAutoManaAbility(ab) || ab["RestrictValid"] is not null) continue;   // used automatically when paying
                if (CanActivate(c, ab, sorcerySpeed)) result.Add(new GameAction.Activate(c.Id, i));
            }
        }
        return result;
    }

    /// <summary>Adds playing <paramref name="c"/> as a land, or casting it with each of its spell abilities, if allowed now.</summary>
    private void AddPlayOrCast(List<GameAction> result, Card c, Player p, bool sorcerySpeed)
    {
        // A modal double-faced card can be played as either face (rule 712.12).
        var faces = c.Def.Layout == "Modal" ? c.Def.Faces.Count : 1;
        for (var f = 0; f < faces; f++)
        {
            c.Face = f;
            AddPlayOrCastFace(result, c, p, sorcerySpeed, f);
        }
        c.Face = 0;
    }

    private void AddPlayOrCastFace(List<GameAction> result, Card c, Player p, bool sorcerySpeed, int face)
    {
        if (c.Types.Is(CardType.Land))
        {
            if (c.Zone != Zone.Command && sorcerySpeed && p.LandsPlayedThisTurn < p.LandDropsPerTurn + p.StaticLandDrops)
                result.Add(new GameAction.PlayLand(c.Id, face));
            return;
        }
        if (!(c.Types.Is(CardType.Instant) || c.HasKeyword("Flash") || sorcerySpeed || Statics.CastWithFlash(this, c, p.Index))) return;
        if (c.Types.IsPermanent && c.CurrentFace.SpellAbilities.Count == 0)
        {
            if (CanCast(c, -1)) result.Add(new GameAction.Cast(c.Id, -1, face));
        }
        else
            for (var i = 0; i < c.CurrentFace.SpellAbilities.Count; i++)
                if (CanCast(c, i)) result.Add(new GameAction.Cast(c.Id, i, face));
    }

    private bool CanActivate(Card c, AbilityDef ab, bool sorcerySpeed)
    {
        if (ab["SorcerySpeed"] == "True" && !sorcerySpeed) return false;
        // Loyalty abilities: sorcery speed, one per planeswalker per turn (rule 606.3).
        if (ab["Planeswalker"] == "True" && (!sorcerySpeed || c.LoyaltyActivatedTurn == State.Turn)) return false;
        if (!CanPayCost(ab.Cost, c, c.Controller, 0, excludeSourceFromMana: ab.Cost.Tap)) return false;
        return HasLegalTargets(c, ab);
    }

    private bool HasLegalTargets(Card source, AbilityDef head)
    {
        foreach (var ab in head.Chain())
            if (ab.TargetFilter is not null && ab.TargetMin > 0 && TargetCandidates(source, ab, source.Controller).Count < ab.TargetMin)
                return false;
        return true;
    }

    // ---- lands, casting, activating ---------------------------------------------------

    public void PlayLand(Card land)
    {
        State.Players[land.Controller].LandsPlayedThisTurn++;
        Note($"play {land.Name}");
        State.Record.Cast.Add(land.Name);
        MoveTo(land, Zone.Battlefield);
        Fire(new TriggerEvent(TriggerMode.LandPlayed, CardId: land.Id, Player: land.Controller));
    }

    /// <summary>The spell's cost: its spell ability's Cost$ (which includes any mana) or the face's mana cost, plus commander tax.</summary>
    private Cost SpellCost(Card c, int spellIndex)
    {
        Cost cost;
        var spell = spellIndex >= 0 ? c.CurrentFace.SpellAbilities[spellIndex] : null;
        if (c.Zone == Zone.Graveyard && c.CurrentFace.Flashback is { } flashback)
            cost = flashback;
        else if (spell?.AltCost is { } alt)
            cost = alt;
        else if (spellIndex >= 0 && c.CurrentFace.SpellAbilities[spellIndex].Cost is { } abCost && c.CurrentFace.SpellAbilities[spellIndex]["Cost"] is not null)
            cost = abCost;
        else
            cost = new Cost { Mana = c.CurrentFace.ManaCost };
        // "You may cast it without paying its mana cost" from an effect.
        if (c.Id == _freeCast || c.Zone is Zone.Exile or Zone.Graveyard or Zone.Library && Statics.MayPlay(this, c.Controller).GetValueOrDefault(c.Id))
            cost = new Cost { Mana = ManaCost.Zero, Tap = cost.Tap, Untap = cost.Untap, Parts = cost.Parts };
        var extra = c.Zone == Zone.Command ? 2 * c.CommandCasts : 0;
        extra -= Statics.CostReduction(this, c, c.Controller);
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
        if (!CanPayCost(cost, c, c.Controller, 0, excludeSourceFromMana: true)) return false;
        var ability = spellIndex >= 0 ? c.CurrentFace.SpellAbilities[spellIndex] : c.CurrentFace.AuraSpell;
        return ability is null || HasLegalTargets(c, ability);
    }

    public void CastSpell(Card c, int spellIndex)
    {
        var player = c.Controller;
        var from = c.Zone;
        var cost = SpellCost(c, spellIndex);
        var ability = spellIndex >= 0 ? c.CurrentFace.SpellAbilities[spellIndex] : c.CurrentFace.AuraSpell ?? PermanentSpell;

        var x = 0;
        if (cost.Mana.XCount > 0)
            x = Decider.ChooseX(this, player, c, MaxAffordableX(cost, c, player));

        // Move to the stack first (rule 601.2a), then choose targets and pay.
        RemoveFromZone(c);
        c.Zone = Zone.Stack;
        var item = new StackItem
        {
            Id = State.NextStackId++, SourceId = c.Id, Ability = ability, Controller = player, IsSpell = true, X = x,
            ChainTargets = ChooseChainTargets(c, ability, player), Description = c.Name + (from == Zone.Graveyard ? " (flashback)" : ""),
            ExileAfter = from == Zone.Graveyard || _castExileAfter,
        };
        _payingForSpell = true;
        var paid = PayCost(cost, c, player, x, excludeSourceFromMana: true);
        _payingForSpell = false;
        if (!paid) throw new InvalidOperationException($"could not pay for {c.Name} after checking it was castable");
        if (LastSacrificed.Count > 0) item.Sacrificed = LastSacrificed.ToList();
        if (from == Zone.Command) c.CommandCasts++;
        c.XPaid = x;
        State.Stack.Add(item);
        State.ThisTurn.SpellsCast++;
        State.Record.Cast.Add(c.Name);
        Note($"cast {c.Name}" + (x > 0 ? $" (X={x})" : "") + TargetText(item) + $" — pool {State.Players[player].Pool}");

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
    }

    // A cast an effect allows mid-resolution ("cast it without paying its mana cost"): which card, and whether it's exiled afterwards.
    private int _freeCast = -1;
    private bool _castExileAfter;

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

    /// <summary>Puts a copy of a spell on the stack (rule 707.10), with new targets if allowed.</summary>
    public void CopySpell(StackItem original, int controller, bool newTargets)
    {
        var source = State[original.SourceId];
        var item = new StackItem
        {
            Id = State.NextStackId++, SourceId = original.SourceId, Ability = original.Ability, Controller = controller, IsSpell = false, X = original.X,
            ChainTargets = newTargets ? ChooseChainTargets(source, original.Ability, controller) : original.ChainTargets.Select(t => new List<TargetRef>(t)).ToList(),
            Description = source.Name + " (copy)",
        };
        State.Stack.Add(item);
        Note($"copy {source.Name}" + TargetText(item));
    }

    public void ActivateAbility(Card c, int abilityIndex)
    {
        var ab = c.Activated[abilityIndex];
        var player = c.Controller;
        var x = 0;
        if (ab.Cost.Mana.XCount > 0) x = Decider.ChooseX(this, player, c, MaxAffordableX(ab.Cost, c, player));
        var targets = ChooseChainTargets(c, ab, player);
        if (ab["Planeswalker"] == "True") c.LoyaltyActivatedTurn = State.Turn;
        if (!PayCost(ab.Cost, c, player, x, excludeSourceFromMana: ab.Cost.Tap))
            throw new InvalidOperationException($"could not pay for {c.Name}'s ability after checking it was activatable");
        var item = new StackItem
        {
            Id = State.NextStackId++, SourceId = c.Id, Ability = ab, Controller = player, X = x, ChainTargets = targets,
            Description = $"{c.Name}: {ab.Description}", Sacrificed = LastSacrificed.Count > 0 ? LastSacrificed.ToList() : null,
        };
        Note($"activate {c.Name}: {ab.Description}" + TargetText(item));
        if (ab.IsManaAbility)
        {
            RunChain(item);   // mana abilities don't use the stack (rule 605.3)
            Note($"  pool {State.Players[player].Pool}");
        }
        else State.Stack.Add(item);
    }

    private static string TargetText(StackItem item)
    {
        var all = item.ChainTargets.SelectMany(t => t).ToList();
        return all.Count == 0 ? "" : " → " + string.Join(", ", all);
    }

    /// <summary>A permanent spell's "ability": resolving puts the card onto the battlefield.</summary>
    public static readonly AbilityDef PermanentSpell = new()
    {
        P = new Forge.ForgeParams("SP$ PermanentSpell"), Api = "PermanentSpell", Kind = AbilityKind.Spell,
    };

    // ---- targets ----------------------------------------------------------------------

    public List<TargetRef> TargetCandidates(Card source, AbilityDef ab, int controller)
    {
        var list = new List<TargetRef>();
        if (ab.TargetFilter is not { } f) return list;
        var x = new EffectContext
        {
            Game = this, Ability = ab,
            Item = new StackItem { SourceId = source.Id, Ability = ab, Controller = controller },
        };
        if (f.CanTargetPlayers)
            foreach (var p in State.Players)
                if (!p.Lost && f.MatchesPlayer(p.Index, x)) list.Add(TargetRef.OfPlayer(p.Index));
        if (f.CanTargetCards)
        {
            var zone = ab["TgtZone"] switch
            {
                "Graveyard" => Zone.Graveyard,
                "Stack" => Zone.Stack,
                "Exile" => Zone.Exile,
                "Hand" => Zone.Hand,
                _ => ab["TargetType"] == "Spell" ? Zone.Stack : Zone.Battlefield,
            };
            foreach (var c in CardsIn(zone))
                if (f.MatchesCard(c, x)) list.Add(TargetRef.OfCard(c.Id));
        }
        return list;
    }

    private List<List<TargetRef>> ChooseChainTargets(Card source, AbilityDef head, int controller)
    {
        var result = new List<List<TargetRef>>();
        foreach (var ab in head.Chain())
        {
            if (ab.TargetFilter is null) { result.Add(new()); continue; }
            var candidates = TargetCandidates(source, ab, controller);
            var max = Math.Min(ab.TargetMax, candidates.Count);
            var min = Math.Min(ab.TargetMin, max);
            result.Add(max == 0 ? new() : Decider.ChooseTargets(this, controller, source, ab, candidates, min, max));
        }
        return result;
    }

    // ---- resolving --------------------------------------------------------------------

    public void ResolveTop()
    {
        var item = State.Stack[^1];
        State.Stack.RemoveAt(State.Stack.Count - 1);
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
                if (target?.CardId is not { } to || State[to].Zone != Zone.Battlefield)
                {
                    Note($"  {item.Description} fizzles (no legal targets)");
                    MoveTo(source, afterwards);
                    return;
                }
                source.AttachedTo = to;
            }
            MoveTo(source, Zone.Battlefield);
            return;
        }
        if (TargetsAllGone(item))
        {
            Note($"  {item.Description} fizzles (no legal targets)");
            if (item.IsSpell && source.Zone == Zone.Stack) MoveTo(source, afterwards);
            return;
        }
        RunChain(item);
        if (item.IsSpell && source.Zone == Zone.Stack) MoveTo(source, afterwards);
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

    private void RunLinks(StackItem item, AbilityDef head)
    {
        for (var ab = head; ab is not null && !State.GameOver; ab = ab.Sub)
        {
            var ctx = new EffectContext { Game = this, Item = item, Ability = ab };
            if (!Conditions.Hold(ctx)) continue;
            if (ab["OptionalDecider"] is not null && !DecidesYes(ctx)) continue;
            if (ab.UnlessCost is { } unless && PaysUnlessCost(ctx, unless)) continue;
            var handler = Effects.Get(ab.Api) ?? throw new InvalidOperationException($"no handler for {ab.Api}");
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
        null or "TargetedController" or "Targeted" => x.Targets.Select(t => t.Player ?? State[t.CardId!.Value].Controller).Cast<int?>().FirstOrDefault(),
        "TriggeredActivator" => x.Item.TriggeredPlayer,
        "TriggeredCardController" => x.Item.TriggeredCardId is { } tc ? State[tc].Controller : null,
        _ => null,   // "Opponent"/"Player": only goldfish opponents would act, and they never do
    };

    /// <summary>
    /// "… unless [payer] pays [cost]": true if the payer pays, so the effect doesn't happen.
    /// Goldfish opponents never pay.
    /// </summary>
    private bool PaysUnlessCost(EffectContext x, Cost cost)
    {
        if (PlayerFor(x, x.Ability["UnlessPayer"]) is not { } p || State.Players[p].IsGoldfish) return false;
        if (!CanPayCost(cost, x.Host, p, x.X, excludeSourceFromMana: false)) return false;
        if (!Decider.ChooseYesNo(this, p, x.Host, $"pay {cost.Mana} to avoid {x.Ability.Description}?")) return false;
        PayCost(cost, x.Host, p, x.X, excludeSourceFromMana: false);
        Note($"  paid to avoid: {x.Ability.Description}");
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
            var attachedOk = host is not null && host.Zone == Zone.Battlefield;
            if (c.Types.HasSubtype("Aura") && !attachedOk) MoveTo(c, Zone.Graveyard);
            else if (c.AttachedTo >= 0 && (!attachedOk || c.Types.HasSubtype("Equipment") && !host!.Types.Is(CardType.Creature))) c.AttachedTo = -1;
        }
        CheckEffectLinks();
        FireZoneBatch();
        FireDamageBatch();
        // A Saga with all its chapters done and none still waiting to resolve is sacrificed (rule 714.4).
        foreach (var saga in State.BattlefieldCards.Where(c => c.CurrentFace.Chapters.Count > 0).ToList())
            if (saga.CounterCount("LORE") >= saga.CurrentFace.Chapters.Count && !_pending.Any(t => t.SourceId == saga.Id)
                && !State.Stack.Any(i => i.SourceId == saga.Id && saga.CurrentFace.Chapters.Any(t => t.Execute == i.Ability || (t.Modes?.Contains(i.Ability) ?? false))))
                Sacrifice(saga);
        // Legend rule (704.5j): keep the newest.
        var legends = State.BattlefieldCards.Where(c => c.Types.Has(SuperType.Legendary)).GroupBy(c => (c.Controller, c.Name)).Where(g => g.Count() > 1);
        foreach (var grp in legends.ToList())
            foreach (var old in grp.OrderBy(c => c.ControlledSinceTurn).ThenBy(c => c.Id).SkipLast(1).ToList())
                MoveTo(old, Zone.Graveyard);

        if (!State.GameOver && State.Players.Where(p => p.Index != Me).All(p => p.Lost))
            Win(Me, State.Record.LastHitBy ?? "opponents eliminated");
    }

    private void Lose(Player p, string reason)
    {
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
