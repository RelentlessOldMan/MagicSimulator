namespace MagicSim.Core.Engine;

public sealed partial class Game
{
    public IEnumerable<Card> CardsIn(Zone zone) => zone switch
    {
        Zone.Battlefield => State.BattlefieldCards,
        // The spell that's resolving has left the stack list but is still on the stack ("if this spell was cast from your hand").
        Zone.Stack => State.Stack.Where(s => s.IsSpell).Select(s => State[s.SourceId])
            .Concat(_resolvingSpell is { } r && State[r].Zone == Zone.Stack ? new[] { State[r] } : Array.Empty<Card>()),
        _ => State.Players.SelectMany(p => State.ZoneList(p.Index, zone)).Select(id => State[id]),
    };

    public void RemoveFromZone(Card c)
    {
        switch (c.Zone)
        {
            case Zone.Battlefield: State.Battlefield.Remove(c.Id); break;
            case Zone.Stack: break;   // the stack item is removed by whoever resolves/counters it
            case Zone.None: break;
            case Zone.PhasedOut: State.PhasedOut.Remove(c.Id); break;
            case Zone.Library:
                var lib = State.Players[c.Owner].Library;
                var at = lib.IndexOf(c.Id);
                if (at >= 0 && at < State.Players[c.Owner].KnownTop) State.Players[c.Owner].KnownTop--;
                lib.RemoveAt(at);
                break;
            default: State.ZoneList(c.Owner, c.Zone).Remove(c.Id); break;
        }
        // What an effect gave a card in the graveyard (flashback) doesn't follow it out.
        if (c.Zone == Zone.Graveyard) c.TempKeywords = null;
    }

    /// <summary>
    /// Moves a card between zones. <paramref name="position"/> applies to the library:
    /// 0 = top, -1 = bottom. Fires zone-change triggers. A commander going to the graveyard
    /// or exile goes to the command zone instead (rule 903.9a; the owner always chooses to here).
    /// </summary>
    public void MoveTo(Card c, Zone to, int position = 0, bool tapped = false)
    {
        var from = c.Zone;
        to = Replacements.Destination(this, c, from, to, out var shuffleInto);
        if (c.IsCommander && to is Zone.Graveyard or Zone.Exile) to = Zone.Command;
        RemoveFromZone(c);
        // Leaving the stack keeps "it was cast" for the permanent it becomes; any other move forgets it.
        if (from != Zone.Stack) c.CastFrom = null;
        if (from == Zone.Exile) c.ForetoldTurn = -1;

        if (from == Zone.Battlefield)
        {
            State.ThisTurn.PermanentLeft.Add(c.Controller);
            if (c.AttachedTo >= 0) Fire(new TriggerEvent(TriggerMode.Unattached, CardId: c.Id, TargetCardId: c.AttachedTo));
            c.LeftTriggers = c.StaticTriggers;
            c.LeftPower = c.Power;
            c.LeftToughness = c.Toughness;
            c.LeftCounters = c.Counters;
            c.Tapped = false;
            c.Damage = 0;
            c.PowerBonus = 0;
            c.ToughnessBonus = 0;
            c.Counters = null;
            c.TempKeywords = null;
            c.AttachedTo = -1;
            c.Face = 0;
            c.CopyDef = null;
            c.CopyUntilCleanup = false;
            c.Animations = null;
            c.ClassLevel = 1;
            c.Controller = c.Owner;
            c.RegenShields = 0;
            c.ExileIfLeaves = false;
            // Soulbond: the pair ends when either leaves.
            if (c.PairedWith is { } partner && State[partner].PairedWith == c.Id) State[partner].PairedWith = null;
            c.PairedWith = null;
            c.PaidOptional = null;
            c.Escaped = false;
            c.ColorsSpent = 0;
            c.ClearStaticEffects();
            State.Attackers.Remove(c.Id);
        }
        // Off the battlefield and the stack a modal double-faced card is its front face again.
        if (to is not (Zone.Battlefield or Zone.Stack) && c.Def.Layout is "Modal" or "Adventure" or "Split") c.Face = 0;
        c.OnAdventure = false;
        // A face-down card is turned face up when it goes anywhere but the battlefield (rule 708.9).
        if (to is not (Zone.Battlefield or Zone.Stack)) c.FaceDown = false;
        if (c.IsToken && to != Zone.Battlefield)
        {
            c.Zone = Zone.None;   // tokens cease to exist outside the battlefield (rule 111.8)
            State.ThisTurn.Moves.Add((c.Id, from, to));
            Fire(new TriggerEvent(TriggerMode.ChangesZone, CardId: c.Id, From: from, To: to));
            return;
        }

        c.Zone = to;
        switch (to)
        {
            case Zone.Battlefield:
                c.Tapped = tapped;
                c.ControlledSinceTurn = State.Turn;
                c.EnteredTurn = State.Turn;
                // Replacement effects apply as it enters: it's on the battlefield but not yet counted there.
                ApplyEntering(c);
                if (c.EnterInstead is { } instead)
                {
                    // It never enters: it goes from where it was to where the replacement sends it.
                    c.EnterInstead = null;
                    c.Zone = from == Zone.Library ? Zone.None : from;
                    MoveTo(c, instead);
                    return;
                }
                State.Battlefield.Add(c.Id);
                // "As a Saga enters, add a lore counter" (rule 714.3a).
                if (c.CurrentFace.Chapters.Count > 0) PutCounters(c, "LORE", 1);
                break;
            case Zone.Library:
                var lib = State.Players[c.Owner].Library;
                if (position < 0 || position >= lib.Count) lib.Add(c.Id);
                else
                {
                    lib.Insert(position, c.Id);
                    var owner = State.Players[c.Owner];
                    if (position <= owner.KnownTop) owner.KnownTop++;   // we know what we put there
                }
                // "Shuffle it into its owner's library instead" (Blightsteel Colossus).
                if (shuffleInto) Shuffle(c.Owner);
                break;
            case Zone.Stack:
            case Zone.None:
                break;
            default:
                State.ZoneList(c.Owner, to).Add(c.Id);
                break;
        }
        if (to == Zone.Hand && c.Owner == Me) State.Record.Seen.Add(c.Def.Name);
        if (from != Zone.None || c.IsToken)
        {
            State.ThisTurn.Moves.Add((c.Id, from, to));
            Fire(new TriggerEvent(TriggerMode.ChangesZone, CardId: c.Id, From: from, To: to));
            _zoneBatch.Add((c.Id, from, to));
        }
    }

    /// <summary>Zone changes since triggers were last checked: "whenever one or more …" triggers see them together.</summary>
    private readonly List<(int CardId, Zone From, Zone To)> _zoneBatch = new();

    /// <summary>Damage dealt since triggers were last checked: (source, player or card dealt damage, amount, combat).</summary>
    private readonly List<(int Source, int? Player, int? Card, int Amount, bool Combat)> _damageBatch = new();

    /// <summary>"Whenever one or more [sources] deal damage to [a player / this]" (DamageDoneOnce): once per thing dealt damage.</summary>
    private void FireDamageBatch()
    {
        if (_damageBatch.Count == 0) return;
        var batch = _damageBatch.ToList();
        _damageBatch.Clear();
        foreach (var id in State.Battlefield.Concat(State.Players.SelectMany(p => p.Command)).ToList())
        {
            var src = State[id];
            if (src.IsBlank) continue;
            foreach (var t in src.Triggers)
            {
                if (t.Mode != "DamageDoneOnce") continue;
                var x = new EffectContext
                {
                    Game = this, Ability = t.Execute ?? PermanentSpell, FaceOverride = t.Face,
                    Item = new StackItem { SourceId = id, Ability = t.Execute ?? PermanentSpell, Controller = src.Controller },
                };
                var vs = t["ValidSource"] is { } s1 ? Effect.FilterFor(x.SVarFace, s1) : null;
                var vt = t["ValidTarget"] is { } s2 ? Effect.FilterFor(x.SVarFace, s2) : null;
                var matching = batch.Where(d => (t["CombatDamage"] is not { } cd || (cd == "True") == d.Combat)
                                                && (vs is null || vs.MatchesCard(State[d.Source], x))
                                                && (vt is null || (d.Player is { } p ? vt.MatchesPlayer(p, x) : vt.MatchesCard(State[d.Card!.Value], x))));
                foreach (var group in matching.GroupBy(d => (d.Player, d.Card)))
                {
                    if (!Presence.Hold(t.P, x) || !Triggers.UnderLimit(this, src, t)) continue;
                    var e = new TriggerEvent(TriggerMode.DamageDoneOnce, CardId: group.Key.Card, Player: group.Key.Player, Amount: group.Sum(d => d.Amount),
                        Combat: group.First().Combat);
                    _pending.Add(new PendingTrigger(id, t, e, src.Controller));
                }
            }
        }
    }

    /// <summary>
    /// How many extra times a trigger of <paramref name="source"/> caused by these zone changes triggers
    /// (Panharmonicon for entering, Teysa Karlov for dying; rule 603.2d).
    /// </summary>
    private int ExtraZoneTriggers(Card source, string mode, IEnumerable<(Card Moved, Zone From, Zone To)> moves)
    {
        var extra = 0;
        foreach (var host in State.BattlefieldCards)
        {
            if (host.IsBlank || host.Controller != source.Controller) continue;
            foreach (var st in host.Statics)
            {
                if (st.Mode != "Panharmonicon" || st["ValidMode"] is { } vm && !vm.Split(',').Contains(mode)) continue;
                var x = ContextFor(host);
                if (st["ValidCard"] is { } vc && !Effect.FilterFor(host.CurrentFace, vc).MatchesCard(source, x)) continue;
                var cause = st["ValidCause"] is { } vcause ? Effect.FilterFor(host.CurrentFace, vcause) : null;
                if (moves.Any(m => Triggers.ZoneMatches(st["Origin"], m.From) && Triggers.ZoneMatches(st["Destination"] ?? "Battlefield", m.To)
                                   && (cause is null || cause.MatchesCard(m.Moved, x))))
                    extra++;
            }
        }
        return extra;
    }

    /// <summary>
    /// How many extra times a trigger of <paramref name="source"/> caused by casting or copying a spell triggers
    /// (Veyran, Voice of Duality; rule 603.2d).
    /// </summary>
    private int ExtraSpellTriggers(Card source, string mode, TriggerEvent e)
    {
        var extra = 0;
        if (source.Zone != Zone.Battlefield || e.CardId is not { } spell) return 0;
        foreach (var host in State.BattlefieldCards)
        {
            if (host.IsBlank || host.Controller != source.Controller) continue;
            foreach (var st in host.Statics)
            {
                if (st.Mode != "Panharmonicon" || st["ValidMode"] is not { } vm || !vm.Split(',').Contains(mode)) continue;
                var x = ContextFor(host);
                if (st["ValidCard"] is { } vc && !Effect.FilterFor(host.CurrentFace, vc).MatchesCard(source, x)) continue;
                if (st["ValidCause"] is { } vcause && !Effect.FilterFor(host.CurrentFace, vcause).MatchesCard(State[spell], x)) continue;
                if (st["ValidActivator"] is { } va && !Effect.FilterFor(host.CurrentFace, va).MatchesPlayer(e.Player ?? -1, x)) continue;
                extra++;
            }
        }
        return extra;
    }

    /// <summary>Fires "one or more cards …" (ChangesZoneAll) triggers once for the zone changes that happened together.</summary>
    private void FireZoneBatch()
    {
        if (_zoneBatch.Count == 0) return;
        var batch = _zoneBatch.ToList();
        _zoneBatch.Clear();
        var sources = State.Battlefield.ToList();
        foreach (var p in State.Players) sources.AddRange(p.Command);
        foreach (var id in sources)
        {
            var src = State[id];
            if (src.IsBlank) continue;
            foreach (var t in src.Triggers)
            {
                if (t.Mode != "ChangesZoneAll") continue;
                var x = new EffectContext
                {
                    Game = this, Ability = t.Execute ?? PermanentSpell, FaceOverride = t.Face,
                    Item = new StackItem { SourceId = id, Ability = t.Execute ?? PermanentSpell, Controller = src.Controller },
                };
                var filter = t["ValidCards"] is { } vc ? Effect.FilterFor(x.SVarFace, vc) : null;
                var n = batch.Count(m => Triggers.ZoneMatches(t["Origin"], m.From) && Triggers.ZoneMatches(t["Destination"], m.To)
                                         && (filter is null || filter.MatchesCard(State[m.CardId], x)));
                if (n == 0 || !Presence.Hold(t.P, x) || !Triggers.UnderLimit(this, src, t)) continue;
                var times = 1 + ExtraZoneTriggers(src, "ChangesZoneAll", batch.Select(m => (State[m.CardId], m.From, m.To)));
                for (var i = 0; i < times; i++)
                    _pending.Add(new PendingTrigger(id, t, new TriggerEvent(TriggerMode.ChangesZoneAll, Amount: n), src.Controller));
            }
        }
    }

    /// <summary>"Enters with counters" and "as this enters" replacements (enters tapped, shocklands).</summary>
    private void ApplyEntering(Card c)
    {
        // A copy effect applies first: the rest are the replacements of whatever it entered as (rule 707.5).
        if (c.CurrentFace.EtbCopy is { } copy)
            RunChain(new StackItem { SourceId = c.Id, Ability = copy, Controller = c.Controller, X = c.XPaid, Description = c.Name + " enters" });
        var face = c.CurrentFace;
        if (face.Loyalty is { } loyalty && c.Types.Is(CardType.Planeswalker))
            AddCounters(c, "LOYALTY", Replacements.Amount(this, "AddCounter", loyalty, targetCard: c, counterType: "LOYALTY"));
        if (face.EtbCounters.Count == 0 && face.EtbReplacements.Count == 0) return;
        foreach (var (kind, amount, condition) in face.EtbCounters)
        {
            var x = new EffectContext { Game = this, Ability = PermanentSpell, Item = new StackItem { SourceId = c.Id, Ability = PermanentSpell, Controller = c.Controller, X = c.XPaid } };
            if (condition is not null && !Presence.Hold(condition, x)) continue;
            if (condition?["ValidCard"] is { } valid && !Effect.FilterFor(face, valid).MatchesCard(c, x)) continue;
            var n = Effect.EvalAmount(x, amount);
            AddCounters(c, kind, Replacements.Amount(this, "AddCounter", n, targetCard: c, counterType: kind));
        }
        foreach (var r in face.EtbReplacements)
            RunChain(new StackItem { SourceId = c.Id, Ability = r, Controller = c.Controller, X = c.XPaid, Description = c.Name + " enters" });
    }

    /// <summary>Creates an effect object (Forge "Effect") in its controller's command zone.</summary>
    public Card CreateEffect(CardDef def, int controller, Card host, string? duration, string? exileOnMoved, string? forgetOnMoved)
    {
        var c = new Card
        {
            Id = State.Cards.Count, Def = def, Owner = controller, Controller = controller, Zone = Zone.Command, IsEffect = true,
            EffectDuration = duration, EffectTurn = State.Turn, EffectHost = host.Id, ExileOnMoved = exileOnMoved, ForgetOnMoved = forgetOnMoved,
        };
        State.Cards.Add(c);
        State.Players[controller].Command.Add(c.Id);
        Note($"  effect: {def.Name}" + (duration is null ? " (this turn)" : $" ({duration})"));
        return c;
    }

    public void EndEffect(Card effect)
    {
        if (effect.Zone != Zone.Command) return;
        State.Players[effect.Controller].Command.Remove(effect.Id);
        effect.Zone = Zone.None;
    }

    /// <summary>Ends effects whose time is up. <paramref name="moment"/> is "cleanup" (end of turn) or "untap" (start of a turn).</summary>
    public void ExpireEffects(string moment)
    {
        foreach (var c in State.BattlefieldCards)
        {
            if (moment == "cleanup" && c.CopyUntilCleanup)
            {
                c.CopyDef = null;
                c.CopyUntilCleanup = false;
            }
            EndAnimations(c, moment);
        }
        if (moment == "cleanup") State.Delayed.RemoveAll(d => d.OnlyTurn is { } only ? only <= State.Turn : d.ThisTurn);
        foreach (var p in State.Players)
            foreach (var id in p.Command.ToList())
            {
                var e = State[id];
                if (!e.IsEffect) continue;
                var ends = (moment, e.EffectDuration) switch
                {
                    ("cleanup", null or "EndOfTurn" or "UntilHostLeavesPlayOrEOT" or "UntilEndOfCombat") => true,
                    ("endcombat", "UntilEndOfCombat") => true,
                    ("cleanup", "UntilTheEndOfYourNextTurn") => e.EffectTurn < State.Turn,
                    ("untap", "UntilYourNextTurn") => e.EffectTurn < State.Turn,
                    _ => false,
                };
                if (ends) EndEffect(e);
            }
        Statics.Apply(this);
    }

    private void EndAnimations(Card c, string moment)
    {
        if (c.Animations is null) return;
        c.Animations.RemoveAll(a => AnimateEffect.Ends(this, a, moment));
        if (c.Animations.Count == 0) c.Animations = null;
    }

    /// <summary>Effects tied to a card or to remembered cards end or forget when those move (state-based check).</summary>
    private void CheckEffectLinks()
    {
        foreach (var c in State.BattlefieldCards) EndAnimations(c, "links");
        // Earthbent lands come back tapped when they die or are exiled.
        foreach (var id in State.ReturnWhenDies.ToList())
        {
            var c = State[id];
            if (c.Zone == Zone.Battlefield) continue;
            State.ReturnWhenDies.Remove(id);
            if (c.Zone is Zone.Graveyard or Zone.Exile) MoveTo(c, Zone.Battlefield, tapped: true);
        }
        // Cards exiled until a permanent leaves come back when it does.
        foreach (var (card, host) in State.UntilHostLeaves.ToList())
        {
            if (State[host].Zone == Zone.Battlefield) continue;
            State.UntilHostLeaves.Remove((card, host));
            if (State[card].Zone == Zone.Exile) MoveTo(State[card], Zone.Battlefield);
        }
        foreach (var p in State.Players)
            foreach (var id in p.Command.ToList())
            {
                var e = State[id];
                if (!e.IsEffect) continue;
                if (e.EffectDuration is "UntilHostLeavesPlay" or "UntilHostLeavesPlayOrEOT" && State[e.EffectHost].Zone != Zone.Battlefield)
                {
                    EndEffect(e);
                    continue;
                }
                if (e.ForgetOnMoved is { } fz && e.Remembered is { } rem)
                    rem.RemoveAll(r => State[r].Zone.ToString() != fz);
                if (e.ExileOnMoved is { } ez && e.Remembered is { } rem2 && rem2.Any(r => State[r].Zone.ToString() != ez))
                    EndEffect(e);
            }
    }

    /// <summary>Taps a permanent, firing "whenever this becomes tapped" triggers.</summary>
    /// <summary>Puts a reflexive or other immediate trigger ("when you do, …") into the queue.</summary>
    /// <summary>A reflexive trigger ("when you do, …"); it keeps the player its spawner triggered on (Spawner>TriggeredTarget).</summary>
    public void AddImmediateTrigger(Card host, TriggerDef trigger, int controller, List<int>? remembered, int? triggeredPlayer = null) =>
        _pending.Add(new PendingTrigger(host.Id, trigger, new TriggerEvent(TriggerMode.Immediate, CardId: host.Id, Player: triggeredPlayer), controller, remembered));

    /// <summary>Sacrifices a permanent: it goes to its owner's graveyard and "whenever you sacrifice" triggers fire.</summary>
    public void Sacrifice(Card c)
    {
        if (c.Zone != Zone.Battlefield) return;
        var controller = c.Controller;
        MoveTo(c, Zone.Graveyard);
        Fire(new TriggerEvent(TriggerMode.Sacrificed, CardId: c.Id, Player: controller));
    }

    public void Tap(Card c)
    {
        if (c.Tapped) return;
        c.Tapped = true;
        Fire(new TriggerEvent(TriggerMode.Taps, CardId: c.Id, Player: c.Controller));
    }

    /// <summary>Draw replacements now replacing a draw (each applies once to its own event, rule 614.5).</summary>
    private readonly HashSet<ReplacementDef> _drawReplacing = new();

    public void Draw(int player, int n, bool record = true)
    {
        var p = State.Players[player];
        for (var i = 0; i < n; i++)
        {
            if (p.IsGoldfish)
            {
                p.AbstractLibrary = Math.Max(0, p.AbstractLibrary - 1);
                p.AbstractHand++;
                var nthUnknown = State.ThisTurn.CardsDrawn[player] = State.ThisTurn.CardsDrawn.GetValueOrDefault(player) + 1;
                if (record) Fire(new TriggerEvent(TriggerMode.Drawn, Player: player, Amount: nthUnknown));
                continue;
            }
            var firstInDrawStep = State.Step == Step.Draw && State.ThisTurn.CardsDrawn.GetValueOrDefault(player) == 0;
            if (record && Replacements.DrawReplacement(this, player, firstInDrawStep, _drawReplacing) is var (host, r) && r.With is { } instead)
            {
                Note($"{host.Name} replaces a draw");
                _drawReplacing.Add(r);
                try { RunChain(new StackItem { SourceId = host.Id, Ability = instead, Controller = host.Controller, Description = host.Name + " (instead of drawing)" }); }
                finally { _drawReplacing.Remove(r); }
                continue;
            }
            if (p.Library.Count == 0) { p.DrewFromEmpty = true; continue; }
            var c = State[p.Library[0]];
            MoveTo(c, Zone.Hand);
            State.ThisTurn.Drawn.Add(c.Id);
            var nth = State.ThisTurn.CardsDrawn[player] = State.ThisTurn.CardsDrawn.GetValueOrDefault(player) + 1;
            if (record) Note($"draw {c.Name}");
            if (record) Fire(new TriggerEvent(TriggerMode.Drawn, CardId: c.Id, Player: player, Amount: nth));
        }
    }

    public void Discard(Card c)
    {
        Note($"discard {c.Name}");
        MoveTo(c, Zone.Graveyard);
        State.ThisTurn.CardsDiscarded[c.Owner] = State.ThisTurn.CardsDiscarded.GetValueOrDefault(c.Owner) + 1;
        Fire(new TriggerEvent(TriggerMode.Discarded, CardId: c.Id, Player: c.Owner));
    }

    /// <summary>A goldfish opponent discards <paramref name="n"/> of the cards they hold; returns how many they had to discard.</summary>
    public int DiscardUnknown(int player, int n)
    {
        var p = State.Players[player];
        n = Math.Min(n, p.AbstractHand);
        if (n <= 0) return 0;
        p.AbstractHand -= n;
        State.ThisTurn.CardsDiscarded[player] = State.ThisTurn.CardsDiscarded.GetValueOrDefault(player) + n;
        Note($"opponent {player} discards {n} ({p.AbstractHand} left)");
        for (var i = 0; i < n; i++) Fire(new TriggerEvent(TriggerMode.Discarded, Player: player));
        return n;
    }

    /// <summary>Mills <paramref name="n"/> cards; returns the cards milled.</summary>
    public List<int> Mill(int player, int n)
    {
        var p = State.Players[player];
        var milled = new List<int>();
        if (p.IsGoldfish) { p.AbstractLibrary = Math.Max(0, p.AbstractLibrary - n); return milled; }
        for (var i = 0; i < n && p.Library.Count > 0; i++)
        {
            milled.Add(p.Library[0]);
            MoveTo(State[p.Library[0]], Zone.Graveyard);
        }
        return milled;
    }

    public void Shuffle(int player)
    {
        if (State.Players[player].IsGoldfish) return;
        State.Rng.Shuffle(State.Players[player].Library);
        State.Players[player].KnownTop = 0;
    }

    /// <summary>
    /// For search: re-shuffle the part of the library the player doesn't know, with the search's
    /// own random stream, so lookahead can't use the real draw order.
    /// </summary>
    public void Determinize(int player, ref Rng rng)
    {
        var p = State.Players[player];
        var lib = p.Library;
        var known = Math.Min(p.KnownTop, lib.Count);
        var unknown = lib.GetRange(known, lib.Count - known);
        rng.Shuffle(unknown);
        for (var i = 0; i < unknown.Count; i++) lib[known + i] = unknown[i];
        State.Rng = new Rng(rng.NextULong());   // later shuffles and random picks are unknown too
    }

    /// <summary>Deals damage to a player after replacement effects; returns the damage dealt.</summary>
    public int DamagePlayer(int player, int amount, Card? source, bool combat = false)
    {
        if (amount <= 0) return 0;
        amount = Replacements.Amount(this, "DamageDone", amount, source, targetPlayer: player, combat: combat);
        if (amount <= 0) return 0;
        if (source?.HasKeyword("Infect") ?? false)
        {
            // Infect: damage to players is poison counters (rule 702.90b).
            State.Players[player].Poison += amount;
            Note($"{source.Name} gives player {player} {amount} poison (total {State.Players[player].Poison})");
            if (source.HasKeyword("Lifelink")) GainLife(source.Controller, amount);
            NoteDamageTo(player, amount, source, combat);
            Fire(new TriggerEvent(TriggerMode.DamageDone, CardId: source.Id, Player: player, Amount: amount, Combat: combat));
            _damageBatch.Add((source.Id, player, null, amount, combat));
            return amount;
        }
        LifeLoss(player, Replacements.Amount(this, "LifeReduced", amount, source, targetPlayer: player, damage: true));
        NoteDamageTo(player, amount, source, combat);
        if (source is not null) State.ThisTurn.DamageDealtBy[source.Id] = State.ThisTurn.DamageDealtBy.GetValueOrDefault(source.Id) + amount;
        // Toxic N: combat damage to a player also gives N poison counters (rule 702.164).
        if (combat && source is not null)
            foreach (var k in source.CurrentFace.RawKeywords)
                if (k.StartsWith("Toxic:", StringComparison.Ordinal) && int.TryParse(k[6..], out var toxic)) State.Players[player].Poison += toxic;
        Note($"{source?.Name ?? "damage"} deals {amount} to player {player} (life {State.Players[player].Life})");
        if (source is null) return amount;
        if (player != Me) RememberWinCard(source);
        if (source.HasKeyword("Lifelink")) GainLife(source.Controller, amount);
        Fire(new TriggerEvent(TriggerMode.DamageDone, CardId: source.Id, Player: player, Amount: amount, Combat: combat));
        _damageBatch.Add((source.Id, player, null, amount, combat));
        return amount;
    }

    /// <summary>Damage a player took this turn (bloodthirst), and the creature types that dealt you combat damage to a player (prowl).</summary>
    private void NoteDamageTo(int player, int amount, Card? source, bool combat)
    {
        State.ThisTurn.DamageTaken[player] = State.ThisTurn.DamageTaken.GetValueOrDefault(player) + amount;
        if (!combat || source is null || source.Controller == player) return;
        if (source.Types.AllCreatureTypes) State.ThisTurn.CombatDamageTypes.Add("*");
        foreach (var t in source.Types.Subtypes)
            if (TypeLine.IsCreatureType(t)) State.ThisTurn.CombatDamageTypes.Add(t);
    }

    public void DamageCard(Card target, int amount, Card? source, bool combat = false)
    {
        if (amount <= 0 || target.Zone != Zone.Battlefield) return;
        amount = Replacements.Amount(this, "DamageDone", amount, source, targetCard: target, combat: combat);
        if (amount <= 0) return;
        // Infect and wither: damage to creatures is -1/-1 counters (rules 702.80, 702.90).
        if (source is not null && (source.HasKeyword("Infect") || source.HasKeyword("Wither"))) PutCounters(target, "M1M1", amount);
        else target.Damage += amount;
        if (source is null) return;
        State.ThisTurn.DamageDealtBy[source.Id] = State.ThisTurn.DamageDealtBy.GetValueOrDefault(source.Id) + amount;
        if (source.HasKeyword("Lifelink")) GainLife(source.Controller, amount);
        if (source.HasKeyword("Deathtouch") && target.Types.Is(CardType.Creature)) target.Damage = Math.Max(target.Damage, target.Toughness);
        Fire(new TriggerEvent(TriggerMode.DamageDone, CardId: source.Id, TargetCardId: target.Id, Amount: amount, Combat: combat));
        _damageBatch.Add((source.Id, null, target.Id, amount, combat));
    }

    public void LoseLife(int player, int amount, Card? source)
    {
        if (amount <= 0) return;
        amount = Replacements.Amount(this, "LifeReduced", amount, source, targetPlayer: player);
        LifeLoss(player, amount);
        Note($"player {player} loses {amount} life (life {State.Players[player].Life})");
        if (source is not null && player != Me) RememberWinCard(source);
    }

    /// <summary>Summoning sickness for {T} abilities, unless something lets it activate them as though it had haste.</summary>
    public bool IsSickForAbilities(Card c) => IsSick(c) && !Statics.ActivatesAsIfHaste(this, c);

    public void GainLife(int player, int amount)
    {
        if (Statics.CantGainLife(this, player)) return;
        amount = Replacements.Amount(this, "GainLife", amount, targetPlayer: player);
        if (amount > 0) State.ThisTurn.LifeGained[player] = State.ThisTurn.LifeGained.GetValueOrDefault(player) + amount;
        if (amount <= 0) return;
        State.Players[player].Life += amount;
        Fire(new TriggerEvent(TriggerMode.LifeGained, Player: player, Amount: amount));
    }

    public void PayLife(int player, int amount)
    {
        if (amount <= 0 || Statics.CantChangeLife(this, player)) return;
        LifeLoss(player, amount);
        State.Players[player].LifePaidTotal += amount;
    }

    /// <summary>A player loses life (from damage, paying it, or losing it): counted for the turn and "whenever … loses life" triggers.</summary>
    private void LifeLoss(int player, int amount)
    {
        if (amount <= 0 || Statics.CantChangeLife(this, player)) return;
        State.Players[player].Life -= amount;
        State.ThisTurn.LifeLost[player] = State.ThisTurn.LifeLost.GetValueOrDefault(player) + amount;
        Fire(new TriggerEvent(TriggerMode.LifeLost, Player: player, Amount: amount));
    }

    /// <summary>The cards that dealt the final blows / performed the win, for win_cards.</summary>
    public void RememberWinCard(Card c)
    {
        State.Record.LastHitBy = c.Name;
        if (!State.Record.WinCards.Contains(c.Def.Name)) State.Record.WinCards.Add(c.Def.Name);
    }
}

// ---- triggers ------------------------------------------------------------------------

public enum TriggerMode { ChangesZone, Phase, SpellCast, Attacks, DamageDone, AttackersDeclared, Drawn, LandPlayed, Discarded, LifeGained, Taps, Chapter, Sacrificed, ChangesZoneAll, DamageDoneOnce, CounterAddedOnce, TapsForMana, Immediate, ClassLevelGained, Cycled, SpellCopied, TurnFaceUp, Unattached, Exploited, Always, LifeLost, Countered, BecomeMonarch, AbilityCast }

public readonly record struct TriggerEvent(TriggerMode Mode, int? CardId = null, Zone From = Zone.None, Zone To = Zone.None,
    int? Player = null, string? Phase = null, int? TargetCardId = null, int Amount = 0, bool Combat = false, string? CounterType = null,
    StackItem? Activated = null);

public readonly record struct PendingTrigger(int SourceId, TriggerDef Trigger, TriggerEvent Event, int Controller, List<int>? DelayRemembered = null,
    List<int>? DelayPlayers = null, int DelayNumber = 0);

public sealed partial class Game
{
    public void FirePhase(string phase) => Fire(new TriggerEvent(TriggerMode.Phase, Player: State.ActivePlayer, Phase: phase));

    public void Fire(TriggerEvent e)
    {
        // Sources: permanents on the battlefield, plus the moved card itself (it "looks back"
        // for leaves-the-battlefield and "when this is put into a graveyard" triggers).
        var sources = State.Battlefield.ToList();
        foreach (var p in State.Players) sources.AddRange(p.Command);
        if (e.CardId is { } moved && !sources.Contains(moved)) sources.Add(moved);
        // Cards whose triggers work from the graveyard (Nether Traitor).
        foreach (var p in State.Players)
            foreach (var id in p.Graveyard)
                if (State[id].Triggers is { Count: > 0 } ts && ts.Any(t => t["TriggerZones"] == "Graveyard") && !sources.Contains(id)) sources.Add(id);
        foreach (var id in sources)
        {
            var src = State[id];
            // A card that just left the battlefield has the triggers it had there ("when this creature dies" given by a spell).
            var triggers = e.CardId == id && e.From == Zone.Battlefield && src.Zone != Zone.Battlefield && src.LeftTriggers is { } left ? left : src.Triggers;
            foreach (var t in triggers)
            {
                if (src.IsBlank || !Triggers.Matches(this, t, src, e) || !Triggers.UnderLimit(this, src, t)) continue;
                var times = 1 + (e.Mode == TriggerMode.ChangesZone && e.CardId is { } changed ? ExtraZoneTriggers(src, "ChangesZone", new[] { (State[changed], e.From, e.To) })
                    : e.Mode is TriggerMode.SpellCast or TriggerMode.SpellCopied ? ExtraSpellTriggers(src, t.Mode, e) : 0);
                // TriggerController$ TriggeredCardController: the creature's controller, not the granter's.
                var controller = t["TriggerController"] is not null && e.CardId is { } tcard ? State[tcard].Controller : src.Controller;
                for (var i = 0; i < times; i++) _pending.Add(new PendingTrigger(id, t, e, controller));
            }
        }
        // Delayed triggers trigger once, then are gone (rule 603.7c).
        for (var i = 0; i < State.Delayed.Count; i++)
        {
            var d = State.Delayed[i];
            if (d.NextTurn && d.Turn == State.Turn) continue;
            if (d.OnlyTurn is { } only && only != State.Turn) continue;
            if (!Triggers.MatchesDelayed(this, d, e)) continue;
            State.Delayed.RemoveAt(i--);
            _pending.Add(new PendingTrigger(d.HostId, d.Trigger, e, d.Controller, d.Remembered, d.Players, d.Number));
        }
    }

    private void PutTriggersOnStack()
    {
        var batch = _pending.ToList();
        _pending.Clear();
        foreach (var pt in batch)
        {
            if (pt.Trigger.Execute is not { } exec) continue;
            var src = State[pt.SourceId];
            if (pt.Trigger["OptionalDecider"] is not null && !HasTriggerCost(exec) && !Decider.ChooseYesNo(this, pt.Controller, src, $"use {src.Name}'s trigger?"))
                continue;
            if (pt.Trigger.Modes is { Count: > 1 } modes)
                exec = modes[Decider.ChooseOption(this, pt.Controller, src, modes.Select(m => m.Description).ToList())];
            var item = new StackItem
            {
                Id = State.NextStackId++, SourceId = pt.SourceId, Ability = exec, Controller = pt.Controller,
                TriggeredCardId = pt.Event.Mode == TriggerMode.DamageDone ? pt.Event.TargetCardId : pt.Event.CardId,
                Sacrificed = pt.Event.Mode == TriggerMode.Sacrificed && pt.Event.CardId is { } sac ? new() { sac } : null,
                TriggeredPlayer = pt.Event.Player, DelayRemembered = pt.DelayRemembered, DelayPlayers = pt.DelayPlayers, DelayNumber = pt.DelayNumber,
                TriggerAmount = pt.Event.Amount,
                TriggeredSourceId = pt.Event.Mode == TriggerMode.DamageDone ? pt.Event.CardId : null,
                TriggeredObjectId = pt.Event.Mode == TriggerMode.Unattached ? pt.Event.TargetCardId : null,
                TriggeredAbilityId = pt.Event.Activated?.Id,
                Description = $"{src.Name} trigger",
            };
            item.ChainTargets = ChooseChainTargets(src, exec, pt.Controller, trigger: item);
            // "Static" triggers (bookkeeping such as forgetting an imprinted card) happen at once, without the stack.
            if (pt.Trigger["Static"] == "True") { RunChain(item); continue; }
            State.Stack.Add(item);
            Note($"trigger: {src.Name}" + TargetText(item));
        }
    }

    public bool HasPendingTriggers => _pending.Count > 0;
}

// ---- costs and mana ------------------------------------------------------------------

/// <summary>A mana ability payment can use automatically. Penalty orders them: free taps first, sacrifices last.</summary>
public readonly record struct ManaSource(int CardId, AbilityDef Ability, ColorMask Options, int Amount, int LifeCost, int Penalty);

public sealed partial class Game
{
    /// <summary>
    /// A mana ability payment may use on its own: no mana in its cost, a fixed output, and
    /// costs limited to {T}, paying life, damage to yourself, or sacrificing/exiling the source
    /// itself (Lotus Petal, Simian Spirit Guide). Payment prefers the cheapest. Anything else
    /// (rituals, Lion's Eye Diamond, sacrificing other permanents) is an explicit choice.
    /// </summary>
    public static bool IsAutoManaAbility(AbilityDef ab) => ab.AutoMana ??= ComputeAutoMana(ab);

    private static bool ComputeAutoMana(AbilityDef ab)
    {
        if (!ab.IsManaAbility || !ab.Cost.Mana.IsZero || ab.Cost.Untap) return false;
        if (!ab.P.Values.Keys.All(k => k is "AB" or "Cost" or "Produced" or "Amount" or "SpellDescription" or "AILogic"
                or "AIPreference" or "SubAbility" or "ActivationZone" or "ColorOrType" or "Valid" or "ReflectProperty" or "RestrictValid"
                or "PrecostDesc" or "Activation" or "AddsKeywords" or "AddsKeywordsValid" or "TriggersWhenSpent" || Presence.Params.Contains(k))) return false;
        if (ab.Api == "Mana" && ProducedMask(ab["Produced"]) is null && ab["Produced"] is not ("Combo ColorIdentity" or "Chosen")) return false;
        if (ab["Amount"] is not null && !int.TryParse(ab["Amount"], out _)) return false;
        foreach (var part in ab.Cost.Parts)
            if (!(part.Kind == CostPartKind.PayLife && int.TryParse(part.Amount, out _)
                  || part.Kind == CostPartKind.Sacrifice && part.Valid == "CARDNAME"
                  || part.Kind == CostPartKind.ExileSelf))
                return false;
        for (var sub = ab.Sub; sub is not null; sub = sub.Sub)
            if (sub.Api is not ("DealDamage" or "LoseLife") || sub["Defined"] != "You" || SelfLife(sub) is null) return false;
        return ab.Cost.Tap || ab.Cost.Parts.Count > 0;
    }

    private static int? SelfLife(AbilityDef sub) =>
        int.TryParse(sub["NumDmg"] ?? sub["LifeAmount"] ?? "1", out var n) ? n : null;

    public static ColorMask? ProducedMask(string? produced)
    {
        if (produced is null) return null;
        if (produced == "Any") return ColorMask.AllColors;
        var words = produced.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return null;
        var start = words[0] == "Combo" ? 1 : 0;
        if (start == 0 && words.Length > 1) return null;   // "B B" style multi-mana isn't a single choice
        ColorMask m = 0;
        for (var i = start; i < words.Length; i++)
        {
            if (words[i] == "Any") { m |= ColorMask.AllColors; continue; }
            if (words[i].Length != 1 || ManaColors.FromLetter(words[i][0]) is not { } c) return null;
            m |= c.Mask();
        }
        return m;
    }

    /// <summary>The colors a ManaReflected ability can make right now (see ManaReflectedEffect).</summary>
    public static ColorMask ReflectedMask(Game g, Card source, AbilityDef ab)
    {
        var valid = ab["Valid"] ?? "Land.YouCtrl";
        var x = g.ContextFor(source);
        IEnumerable<Card> cards;
        if (valid == "Defined.Imprinted") cards = (source.Imprinted ?? new List<int>()).Select(id => g.State[id]);
        else
        {
            var filter = Effect.FilterFor(source.CurrentFace, valid);
            if (valid.Contains("OppCtrl", StringComparison.Ordinal) && g.State.Players.Where(p => p.Index != source.Controller).All(p => p.IsGoldfish))
                return ab["ColorOrType"] == "Type" ? ColorMask.AllColors | ManaColor.C.Mask() : ColorMask.AllColors;
            cards = g.State.BattlefieldCards.Where(c => c.Id != source.Id && filter.MatchesCard(c, x));
        }
        ColorMask mask = 0;
        foreach (var c in cards)
        {
            if (ab["ReflectProperty"] == "Is") { mask |= c.CurrentFace.Colors; continue; }
            foreach (var other in c.Activated)
            {
                if (other.Api != "Mana") continue;   // reflecting other reflectors could loop
                mask |= other["Produced"] == "Combo ColorIdentity" ? g.CommanderIdentity(c.Controller)
                    : ProducedMask(other["Produced"]) ?? ManaEffect.FixedMana(other["Produced"])?.Aggregate((ColorMask)0, (m, col) => m | col.Mask()) ?? 0;
            }
        }
        if (ab["ColorOrType"] != "Type") mask &= ColorMask.AllColors;
        return mask;
    }

    public List<ManaSource> AutoSources(int player, int excludeId)
    {
        // Within LegalActions the same sources come up for every card: reuse them, minus the card being paid for.
        // Not when convoke or delve, or a restricted source, makes them depend on what's being paid for.
        var wanted = excludeId;
        var cacheable = _autoSourceCache is not null && !(_payingForSpell && _payingFor is { } paying
            && (paying.HasKeyword("Convoke") || paying.HasKeyword("Delve") || paying.HasKeyword("Improvise")));
        if (cacheable)
        {
            if (_autoSourceCache!.TryGetValue(player, out var cached)) return Without(cached, wanted);
            excludeId = -1;
        }
        var restricted = false;
        var list = new List<ManaSource>(State.Battlefield.Count + 8);
        var p = State.Players[player];
        IEnumerable<Card> candidates = State.BattlefieldCards.Where(c => c.Controller == player)
            .Concat(p.Hand.Select(id => State[id]));
        foreach (var c in candidates)
        {
            if (c.Id == excludeId || c.IsBlank) continue;
            ManaSource? best = null;
            foreach (var ab in c.Activated)
            {
                if (!IsAutoManaAbility(ab)) continue;
                var fromHand = ab["ActivationZone"] == "Hand";
                if (fromHand != (c.Zone == Zone.Hand)) continue;
                if (ab.Cost.Tap && (c.Tapped || IsSickForAbilities(c))) continue;
                if (ab["RestrictValid"] is { } rv && (restricted = true) && !ManaRestriction.Allows(this, c, rv, _payingFor, _payingForSpell)) continue;
                if (ab.P.Values.Keys.Any(Presence.Params.Contains) && !Presence.Hold(ab.P, ContextFor(c))) continue;
                if (ab["Activation"] is { } activation && !Presence.ConditionHolds(activation, ContextFor(c))) continue;
                var mask = ab.Api == "ManaReflected" ? ReflectedMask(this, c, ab)
                    : ab["Produced"] == "Combo ColorIdentity" ? CommanderIdentity(player)
                    : ab["Produced"] == "Chosen" ? c.ChosenColor?.Mask() ?? 0
                    : ProducedMask(ab["Produced"])!.Value;
                if (mask == 0) continue;
                var life = ab.Cost.Parts.Where(q => q.Kind == CostPartKind.PayLife).Sum(q => int.Parse(q.Amount));
                for (var sub = ab.Sub; sub is not null; sub = sub.Sub) life += SelfLife(sub)!.Value;
                // "Whenever this becomes tapped, it deals 1 damage to you" (City of Brass) costs like a painland.
                if (c.Triggers.Any(t => t.Mode == "Taps")) life += 1;
                var penalty = life * 2
                    + (ab.Cost.Parts.Any(q => q.Kind == CostPartKind.Sacrifice) ? 20 : 0)
                    + (ab.Cost.Parts.Any(q => q.Kind == CostPartKind.ExileSelf) ? 30 : 0);
                // One auto ability per card: the cheapest, then the most flexible (a Swamp given "add any color").
                var source = new ManaSource(c.Id, ab, mask, ab["Amount"] is { } a ? int.Parse(a) : 1, life, penalty);
                if (best is not { } b || penalty < b.Penalty
                    || penalty == b.Penalty && System.Numerics.BitOperations.PopCount((uint)mask) > System.Numerics.BitOperations.PopCount((uint)b.Options))
                    best = source;
            }
            if (best is { } chosen) list.Add(chosen);
        }
        // Convoke: each untapped creature can pay for {1} or one mana of its color (rule 702.51).
        if (_payingForSpell && _payingFor is { } spell && spell.HasKeyword("Convoke"))
            foreach (var c in State.BattlefieldCards)
                if (c.Controller == player && c.Types.Is(CardType.Creature) && !c.Tapped && c.Id != excludeId && !list.Any(m => m.CardId == c.Id))
                    list.Add(new ManaSource(c.Id, ConvokeAbility, c.Colors == 0 ? ManaColor.C.Mask() : c.Colors | ManaColor.C.Mask(), 1, 0, 8));
        // Improvise: each untapped artifact can pay for {1} (rule 702.126).
        if (_payingForSpell && _payingFor is { } improviser && improviser.HasKeyword("Improvise"))
            foreach (var c in State.BattlefieldCards)
                if (c.Controller == player && c.Types.Is(CardType.Artifact) && !c.Tapped && c.Id != excludeId && !list.Any(m => m.CardId == c.Id))
                    list.Add(new ManaSource(c.Id, ConvokeAbility, ManaColor.C.Mask(), 1, 0, 8));
        // Delve: each card exiled from your graveyard pays for {1} (rule 702.66). Preferred to tapping lands.
        if (_payingForSpell && _payingFor is { } delver && delver.HasKeyword("Delve"))
            foreach (var id in p.Graveyard)
                if (id != excludeId && id != delver.Id) list.Add(new ManaSource(id, DelveAbility, ManaColor.C.Mask(), 1, 0, -1));
        // Cheapest first; among equals, the least flexible so flexible sources stay available.
        list.Sort((a, b) =>
        {
            if (a.Penalty != b.Penalty) return a.Penalty.CompareTo(b.Penalty);
            var fa = System.Numerics.BitOperations.PopCount((uint)a.Options);
            var fb = System.Numerics.BitOperations.PopCount((uint)b.Options);
            return fa != fb ? fa.CompareTo(fb) : a.CardId.CompareTo(b.CardId);
        });
        if (!cacheable) return list;
        if (!restricted) _autoSourceCache![player] = list;
        return Without(list, wanted);
    }

    private static List<ManaSource> Without(List<ManaSource> sources, int cardId) =>
        cardId >= 0 && sources.Exists(s => s.CardId == cardId) ? sources.FindAll(s => s.CardId != cardId) : sources;

    /// <summary>
    /// Finds sources to use so the pool can pay <paramref name="cost"/>. Returns the sources and
    /// colors (empty if the pool alone suffices), or null if it can't be paid. Life spent on
    /// sources and phyrexian symbols must leave the player above 0.
    /// </summary>
    public List<(ManaSource Source, ManaColor Color)>? PlanManaPayment(int player, ManaCost cost, int x, int excludeId, int lifeAvailable)
    {
        var pool = State.Players[player].Pool;
        foreach (var r in UsableRestrictedMana(player)) pool.Add(r.Color, r.Amount);
        var probe = pool;
        var spendable = lifeAvailable - 1;
        if (ManaPayment.TryPay(ref probe, cost, x, out var life0) && life0 <= spendable) return new();

        var sources = AutoSources(player, excludeId);
        var needed = cost.ManaValue + cost.XCount * x - cost.Shards.Count(s => s.Phyrexian);
        if (pool.Total + sources.Sum(s => s.Amount) < needed) return null;

        var chosen = new List<(ManaSource, ManaColor)>();
        var budget = 20000;
        return Search(0, pool, 0) ? chosen : null;

        bool Search(int i, ManaPool current, int lifeSpent)
        {
            if (--budget < 0) return false;
            var test = current;
            if (ManaPayment.TryPay(ref test, cost, x, out var life, allowLifeForPhyrexian: true) && life + lifeSpent <= spendable) return true;
            if (i >= sources.Count) return false;
            var s = sources[i];
            if (lifeSpent + s.LifeCost <= spendable)
                foreach (var color in ManaColors.All)
                {
                    if ((s.Options & color.Mask()) == 0) continue;
                    var next = current;
                    next.Add(color, s.Amount);
                    chosen.Add((s, color));
                    if (Search(i + 1, next, lifeSpent + s.LifeCost)) return true;
                    chosen.RemoveAt(chosen.Count - 1);
                }
            return Search(i + 1, current, lifeSpent);
        }
    }

    /// <summary>Activates an automatic mana source for a payment: pays its costs and adds the mana.</summary>
    /// <summary>Stands for tapping a creature to convoke.</summary>
    private static readonly AbilityDef ConvokeAbility = new()
    {
        P = new Forge.ForgeParams("AB$ Mana | Cost$ T | Produced$ C"), Api = "Mana", Kind = AbilityKind.Activated, Cost = new Cost { Tap = true },
    };

    /// <summary>Stands for exiling a card from the graveyard to delve.</summary>
    private static readonly AbilityDef DelveAbility = new()
    {
        P = new Forge.ForgeParams("AB$ Mana | Produced$ C"), Api = "Mana", Kind = AbilityKind.Activated,
    };

    private void UseManaSource(ManaSource src, ManaColor color, int player)
    {
        var card = State[src.CardId];
        var ab = src.Ability;
        if (ab == DelveAbility)
        {
            MoveTo(card, Zone.Exile);
            State.Players[player].Pool.Add(ManaColor.C, 1);
            Note($"  delve {card.Name}");
            return;
        }
        if (ab.Cost.Tap) Tap(card);
        foreach (var part in ab.Cost.Parts)
            switch (part.Kind)
            {
                case CostPartKind.PayLife: PayLife(player, int.Parse(part.Amount)); break;
                case CostPartKind.Sacrifice: Sacrifice(card); break;
                case CostPartKind.ExileSelf: MoveTo(card, Zone.Exile); break;
            }
        State.Players[player].Pool.Add(color, src.Amount);
        // "If that mana is spent on a creature spell, it gains haste."
        if (ab["AddsKeywords"] is { } adds && _payingForSpell && _payingFor is { } spell
            && (ab["AddsKeywordsValid"] is not { } addsValid || Effect.FilterFor(card.CurrentFace, addsValid).MatchesCard(spell, ContextFor(card))))
        {
            spell.TempKeywords ??= new HashSet<string>(StringComparer.Ordinal);
            foreach (var k in adds.Split('&', StringSplitOptions.TrimEntries)) spell.TempKeywords.Add(k);
        }
        // "When that mana is spent to cast a creature spell that shares a creature type with your commander": mana made
        // while paying is spent on that spell.
        if (ab.WhenSpent is { } spent && _payingForSpell && _payingFor is { } paidFor
            && (spent["ValidCard"] is not { } spentValid || Effect.FilterFor(spent.Face ?? card.CurrentFace, spentValid).MatchesCard(paidFor, ContextFor(card))))
            _pending.Add(new PendingTrigger(card.Id, spent, new TriggerEvent(TriggerMode.SpellCast, CardId: paidFor.Id, Player: player), player));
        if (ab.Cost.Tap && ab != ConvokeAbility) Fire(new TriggerEvent(TriggerMode.TapsForMana, CardId: card.Id, Player: player));
        for (var sub = ab.Sub; sub is not null; sub = sub.Sub)
        {
            var n = SelfLife(sub)!.Value;
            if (sub.Api == "DealDamage") DamagePlayer(player, n, card); else LoseLife(player, n, card);
        }
        if (ab.Cost.Parts.Count > 0 || ab.Sub is not null) Note($"  use {card.Name} for {{{color}}}");
    }

    // What the current payment is for: restricted mana ("spend this only on creature spells") checks it.
    private Card? _payingFor;
    private bool _payingForSpell;

    public bool CanPayCost(Cost cost, Card source, int player, int x, bool excludeSourceFromMana)
    {
        var saved = _payingFor;
        _payingFor = source;
        try { return CanPayCostCore(cost, source, player, x, excludeSourceFromMana); }
        finally { _payingFor = saved; }
    }

    private bool CanPayCostCore(Cost cost, Card source, int player, int x, bool excludeSourceFromMana)
    {
        var p = State.Players[player];
        if (cost.Tap && (source.Tapped || source.Zone != Zone.Battlefield || IsSickForAbilities(source))) return false;
        // {Q}: untap this permanent (rule 107.6), which it can't if it's untapped or summoning sick.
        if (cost.Untap && (!source.Tapped || source.Zone != Zone.Battlefield || IsSickForAbilities(source))) return false;
        var lifeNeeded = 0;
        foreach (var part in cost.Parts)
            switch (part.Kind)
            {
                case CostPartKind.PayLife:
                    lifeNeeded += AmountOf(part, x);
                    // "Your life total can't change": you can't pay life (rule 119.8).
                    if (AmountOf(part, x) > 0 && Statics.CantChangeLife(this, player)) return false;
                    break;
                // "Draw two cards" as a cost (Sylvan Library): not from a library too small to draw from.
                case CostPartKind.Draw:
                    if (p.Library.Count < AmountOf(part, x)) return false;
                    break;
                case CostPartKind.Sacrifice:
                    if (part.Valid == "CARDNAME") { if (source.Zone != Zone.Battlefield) return false; }
                    else if (CostCandidates(part, source, player).Count < AmountOf(part, x)) return false;
                    break;
                case CostPartKind.Discard:
                    if (part.Valid == "Hand") break;
                    if (part.Valid == "CARDNAME") { if (source.Zone != Zone.Hand) return false; break; }
                    if (CostCandidates(part, source, player).Count < AmountOf(part, x)) return false;
                    break;
                case CostPartKind.ExileFromGraveyard:
                case CostPartKind.ExileFromBattlefield:
                case CostPartKind.TapUntapped:
                case CostPartKind.ExileFromHand:
                case CostPartKind.ReturnToHand:
                    if (CostCandidates(part, source, player).Count < AmountOf(part, x)) return false;
                    break;
                case CostPartKind.ExileSelf:
                    if (source.Zone != Zone.Hand) return false;
                    break;
                case CostPartKind.RemoveCounter:
                    if (source.CounterCount(part.Valid) < AmountOf(part, x)) return false;
                    break;
                case CostPartKind.Reveal:
                    if (CostCandidates(part, source, player).Count < AmountOf(part, x)) return false;
                    break;
                case CostPartKind.ExileSource:
                    if (source.Zone is not (Zone.Battlefield or Zone.Graveyard or Zone.Hand)) return false;
                    break;
                case CostPartKind.TapPower:
                    if (CostCandidates(part, source, player).Sum(id => Math.Max(0, State[id].Power)) < AmountOf(part, x)) return false;
                    break;
            }
        // Life can be paid only if the player has at least that much (rule 119.4).
        if (lifeNeeded > p.Life) return false;
        return PlanManaPayment(player, cost.Mana, x, excludeSourceFromMana ? source.Id : -1, Math.Max(0, p.Life - lifeNeeded)) is not null;
    }

    /// <summary>How many a cost part asks for: its number, or X ("pay X life") once X is chosen.</summary>
    /// <summary>Restricted floating mana that what's being paid for now may use.</summary>
    private List<RestrictedMana> UsableRestrictedMana(int player) =>
        State.Players[player].RestrictedPool is { Count: > 0 } rp
            ? rp.Where(r => ManaRestriction.Allows(this, State[r.SourceId], r.Restriction, _payingFor, _payingForSpell)).ToList()
            : new();

    /// <summary>Takes what a payment spent out of the pools, restricted mana first.</summary>
    private static void SpendPools(Player p, List<RestrictedMana> usable, ManaPool before, ManaPool after)
    {
        foreach (var k in ManaColors.All)
        {
            var spent = before[k] - after[k];
            foreach (var r in usable.Where(r => r.Color == k))
            {
                if (spent <= 0) break;
                var take = Math.Min(spent, r.Amount);
                spent -= take;
                var i = p.RestrictedPool!.IndexOf(r);
                if (take == r.Amount) p.RestrictedPool.RemoveAt(i);
                else p.RestrictedPool[i] = r with { Amount = r.Amount - take };
            }
            p.Pool[k] -= spent;
        }
    }

    private static int AmountOf(CostPart part, int x = 1) => int.TryParse(part.Amount, out var n) ? n : part.Amount == "X" ? x : 1;

    private List<int> CostCandidates(CostPart part, Card source, int player)
    {
        var filter = part.Valid is "Card" or "" || part.Kind == CostPartKind.TapPower ? null : Effect.FilterFor(source.CurrentFace, part.Valid);
        var x = new EffectContext { Game = this, Ability = PermanentSpell, Item = new StackItem { SourceId = source.Id, Ability = PermanentSpell, Controller = player } };
        IEnumerable<Card> pool = part.Kind switch
        {
            CostPartKind.Sacrifice => State.BattlefieldCards.Where(c => c.Controller == player),
            CostPartKind.TapUntapped => State.BattlefieldCards.Where(c => c.Controller == player && !c.Tapped && c.Id != source.Id),
            CostPartKind.Discard => State.Players[player].Hand.Select(id => State[id]).Where(c => c.Id != source.Id),
            CostPartKind.ExileFromGraveyard => State.Players[player].Graveyard.Select(id => State[id]).Where(c => c.Id != source.Id),
            CostPartKind.ExileFromHand => State.Players[player].Hand.Select(id => State[id]).Where(c => c.Id != source.Id),
            CostPartKind.ReturnToHand or CostPartKind.ExileFromBattlefield => State.BattlefieldCards.Where(c => c.Controller == player),
            CostPartKind.Reveal => State.Players[player].Hand.Select(id => State[id]).Where(c => c.Id != source.Id),
            CostPartKind.TapPower => State.BattlefieldCards.Where(c => c.Controller == player && !c.Tapped && c.Id != source.Id && c.Types.Is(CardType.Creature)),
            _ => Enumerable.Empty<Card>(),
        };
        return pool.Where(c => filter is null || filter.MatchesCard(c, x)).Select(c => c.Id).ToList();
    }

    public int MaxAffordableX(Cost cost, Card source, int player)
    {
        var x = 0;
        while (x < 100 && CanPayCost(cost, source, player, x + 1, excludeSourceFromMana: true)) x++;
        return x;
    }

    public bool PayCost(Cost cost, Card source, int player, int x, bool excludeSourceFromMana)
    {
        var saved = _payingFor;
        _payingFor = source;
        try { return PayCostCore(cost, source, player, x, excludeSourceFromMana); }
        finally { _payingFor = saved; }
    }

    /// <summary>How many colors of mana paid the last cost (converge).</summary>
    public int LastColorsSpent { get; private set; }

    /// <summary>Permanents sacrificed to pay the last cost paid ("Sacrificed$CardPower").</summary>
    public List<int> LastSacrificed { get; } = new();
    /// <summary>Cards exiled to pay the last cost ("Exiled$CardManaCost").</summary>
    public List<int> LastExiled { get; } = new();

    private bool PayCostCore(Cost cost, Card source, int player, int x, bool excludeSourceFromMana)
    {
        LastSacrificed.Clear();
        LastExiled.Clear();
        var p = State.Players[player];
        var lifeNeeded = cost.Parts.Where(q => q.Kind == CostPartKind.PayLife).Sum(q => AmountOf(q, x));
        var plan = PlanManaPayment(player, cost.Mana, x, excludeSourceFromMana ? source.Id : -1, Math.Max(0, p.Life - lifeNeeded));
        if (plan is null) return false;

        foreach (var (src, color) in plan) UseManaSource(src, color, player);
        var usable = UsableRestrictedMana(player);
        var before = p.Pool;
        foreach (var r in usable) before.Add(r.Color, r.Amount);
        var after = before;
        if (!ManaPayment.TryPay(ref after, cost.Mana, x, out var phyrexianLife)) return false;
        LastColorsSpent = ManaColors.All.Count(k => k != ManaColor.C && after[k] < before[k]);
        SpendPools(p, usable, before, after);
        if (phyrexianLife > 0) PayLife(player, phyrexianLife);

        if (cost.Tap) Tap(source);
        if (cost.Untap) source.Tapped = false;
        foreach (var part in cost.Parts)
        {
            var n = AmountOf(part, x);
            switch (part.Kind)
            {
                case CostPartKind.PayLife:
                    PayLife(player, n);
                    break;
                case CostPartKind.Draw:
                    Draw(player, n);
                    break;
                case CostPartKind.Sacrifice:
                    if (part.Valid == "CARDNAME") { LastSacrificed.Add(source.Id); Sacrifice(source); }
                    else
                        foreach (var id in Decider.ChooseCards(this, player, Choice.Sacrifice, source, CostCandidates(part, source, player), n, n))
                        {
                            LastSacrificed.Add(id);
                            Sacrifice(State[id]);
                        }
                    break;
                case CostPartKind.Discard:
                    if (part.Valid == "Hand")
                        foreach (var id in p.Hand.ToList()) Discard(State[id]);
                    else if (part.Valid == "CARDNAME") Discard(source);
                    else if (part.Valid == "Random")
                    {
                        var hand = p.Hand.Where(id => id != source.Id).ToList();
                        for (var i = 0; i < n && hand.Count > 0; i++)
                        {
                            var pick = hand[State.Rng.Next(hand.Count)];
                            hand.Remove(pick);
                            Discard(State[pick]);
                        }
                    }
                    else
                        foreach (var id in Decider.ChooseCards(this, player, Choice.Discard, source, CostCandidates(part, source, player), n, n))
                            Discard(State[id]);
                    break;
                case CostPartKind.ExileFromGraveyard:
                    foreach (var id in Decider.ChooseCards(this, player, Choice.ExileFromGraveyard, source, CostCandidates(part, source, player), n, n))
                    {
                        LastExiled.Add(id);
                        MoveTo(State[id], Zone.Exile);
                    }
                    break;
                case CostPartKind.ExileFromBattlefield:
                    foreach (var id in Decider.ChooseCards(this, player, Choice.Sacrifice, source, CostCandidates(part, source, player), n, n))
                    {
                        LastExiled.Add(id);
                        MoveTo(State[id], Zone.Exile);
                    }
                    break;
                case CostPartKind.TapUntapped:
                    foreach (var id in Decider.ChooseCards(this, player, Choice.TapForCost, source, CostCandidates(part, source, player), n, n))
                        Tap(State[id]);
                    break;
                case CostPartKind.ExileSelf:
                    MoveTo(source, Zone.Exile);
                    break;
                case CostPartKind.ExileFromHand:
                    foreach (var id in Decider.ChooseCards(this, player, Choice.ExileFromGraveyard, source, CostCandidates(part, source, player), n, n))
                        MoveTo(State[id], Zone.Exile);
                    break;
                case CostPartKind.ReturnToHand:
                    foreach (var id in Decider.ChooseCards(this, player, Choice.Sacrifice, source, CostCandidates(part, source, player), n, n))
                        MoveTo(State[id], Zone.Hand);
                    break;
                case CostPartKind.RemoveCounter:
                    source.Counters![part.Valid] -= n;
                    break;
                case CostPartKind.Reveal:
                    break;   // revealing changes nothing in a goldfish game
                case CostPartKind.ExileSource:
                    MoveTo(source, Zone.Exile);
                    break;
                case CostPartKind.TapPower:
                    // Crew with the fewest, weakest creatures that reach the power needed.
                    var power = 0;
                    foreach (var id in CostCandidates(part, source, player).OrderBy(id => State[id].Power).ToList())
                    {
                        if (power >= n) break;
                        if (State[id].Power <= 0) continue;
                        Tap(State[id]);
                        power += State[id].Power;
                    }
                    break;
                case CostPartKind.AddCounter:
                    source.Counters ??= new();
                    source.Counters[part.Valid] = source.CounterCount(part.Valid) + n;
                    break;
            }
        }
        return true;
    }
}
