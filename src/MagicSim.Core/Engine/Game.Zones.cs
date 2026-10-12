namespace MagicSim.Core.Engine;

public sealed partial class Game
{
    public IEnumerable<Card> CardsIn(Zone zone) => zone switch
    {
        Zone.Battlefield => State.BattlefieldCards,
        Zone.Stack => State.Stack.Where(s => s.IsSpell).Select(s => State[s.SourceId]),
        _ => State.Players.SelectMany(p => State.ZoneList(p.Index, zone)).Select(id => State[id]),
    };

    public void RemoveFromZone(Card c)
    {
        switch (c.Zone)
        {
            case Zone.Battlefield: State.Battlefield.Remove(c.Id); break;
            case Zone.Stack: break;   // the stack item is removed by whoever resolves/counters it
            case Zone.None: break;
            case Zone.Library:
                var lib = State.Players[c.Owner].Library;
                var at = lib.IndexOf(c.Id);
                if (at >= 0 && at < State.Players[c.Owner].KnownTop) State.Players[c.Owner].KnownTop--;
                lib.RemoveAt(at);
                break;
            default: State.ZoneList(c.Owner, c.Zone).Remove(c.Id); break;
        }
    }

    /// <summary>
    /// Moves a card between zones. <paramref name="position"/> applies to the library:
    /// 0 = top, -1 = bottom. Fires zone-change triggers. A commander going to the graveyard
    /// or exile goes to the command zone instead (rule 903.9a; the owner always chooses to here).
    /// </summary>
    public void MoveTo(Card c, Zone to, int position = 0, bool tapped = false)
    {
        var from = c.Zone;
        if (c.IsCommander && to is Zone.Graveyard or Zone.Exile) to = Zone.Command;
        RemoveFromZone(c);

        if (from == Zone.Battlefield)
        {
            c.LeftPower = c.Power;
            c.LeftToughness = c.Toughness;
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
            c.Controller = c.Owner;
            c.RegenShields = 0;
            c.ClearStaticEffects();
            State.Attackers.Remove(c.Id);
        }
        // Off the battlefield and the stack a modal double-faced card is its front face again.
        if (to is not (Zone.Battlefield or Zone.Stack) && c.Def.Layout == "Modal") c.Face = 0;
        if (c.IsToken && to != Zone.Battlefield)
        {
            c.Zone = Zone.None;   // tokens cease to exist outside the battlefield (rule 111.8)
            Fire(new TriggerEvent(TriggerMode.ChangesZone, CardId: c.Id, From: from, To: to));
            return;
        }

        c.Zone = to;
        switch (to)
        {
            case Zone.Battlefield:
                c.Tapped = tapped;
                c.ControlledSinceTurn = State.Turn;
                // Replacement effects apply as it enters: it's on the battlefield but not yet counted there.
                ApplyEntering(c);
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
                break;
            case Zone.Stack:
            case Zone.None:
                break;
            default:
                State.ZoneList(c.Owner, to).Add(c.Id);
                break;
        }
        if (to == Zone.Hand && c.Owner == Me) State.Record.Seen.Add(c.Name);
        if (from != Zone.None || c.IsToken) Fire(new TriggerEvent(TriggerMode.ChangesZone, CardId: c.Id, From: from, To: to));
    }

    /// <summary>"Enters with counters" and "as this enters" replacements (enters tapped, shocklands).</summary>
    private void ApplyEntering(Card c)
    {
        // A copy effect applies first: the rest are the replacements of whatever it entered as (rule 707.5).
        if (c.CurrentFace.EtbCopy is { } copy)
            RunChain(new StackItem { SourceId = c.Id, Ability = copy, Controller = c.Controller, X = c.XPaid, Description = c.Name + " enters" });
        var face = c.CurrentFace;
        if (face.EtbCounters.Count == 0 && face.EtbReplacements.Count == 0) return;
        foreach (var (kind, amount) in face.EtbCounters)
        {
            var x = new EffectContext { Game = this, Ability = PermanentSpell, Item = new StackItem { SourceId = c.Id, Ability = PermanentSpell, Controller = c.Controller, X = c.XPaid } };
            AddCounters(c, kind, Effect.EvalAmount(x, amount));
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
        foreach (var p in State.Players)
            foreach (var id in p.Command.ToList())
            {
                var e = State[id];
                if (!e.IsEffect) continue;
                var ends = (moment, e.EffectDuration) switch
                {
                    ("cleanup", null or "EndOfTurn" or "UntilHostLeavesPlayOrEOT") => true,
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
    public void Tap(Card c)
    {
        if (c.Tapped) return;
        c.Tapped = true;
        Fire(new TriggerEvent(TriggerMode.Taps, CardId: c.Id, Player: c.Controller));
    }

    public void Draw(int player, int n, bool record = true)
    {
        var p = State.Players[player];
        for (var i = 0; i < n; i++)
        {
            if (p.IsGoldfish) { p.AbstractLibrary = Math.Max(0, p.AbstractLibrary - 1); continue; }
            if (p.Library.Count == 0) { p.DrewFromEmpty = true; continue; }
            var c = State[p.Library[0]];
            MoveTo(c, Zone.Hand);
            if (record) Note($"draw {c.Name}");
            if (record) Fire(new TriggerEvent(TriggerMode.Drawn, CardId: c.Id, Player: player));
        }
    }

    public void Discard(Card c)
    {
        Note($"discard {c.Name}");
        MoveTo(c, Zone.Graveyard);
        Fire(new TriggerEvent(TriggerMode.Discarded, CardId: c.Id, Player: c.Owner));
    }

    public void Mill(int player, int n)
    {
        var p = State.Players[player];
        if (p.IsGoldfish) { p.AbstractLibrary = Math.Max(0, p.AbstractLibrary - n); return; }
        for (var i = 0; i < n && p.Library.Count > 0; i++) MoveTo(State[p.Library[0]], Zone.Graveyard);
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

    public void DamagePlayer(int player, int amount, Card? source, bool combat = false)
    {
        if (amount <= 0) return;
        State.Players[player].Life -= amount;
        Note($"{source?.Name ?? "damage"} deals {amount} to player {player} (life {State.Players[player].Life})");
        if (source is null) return;
        if (player != Me) RememberWinCard(source);
        if (source.HasKeyword("Lifelink")) GainLife(source.Controller, amount);
        Fire(new TriggerEvent(TriggerMode.DamageDone, CardId: source.Id, Player: player, Amount: amount, Combat: combat));
    }

    public void DamageCard(Card target, int amount, Card? source, bool combat = false)
    {
        if (amount <= 0 || target.Zone != Zone.Battlefield) return;
        target.Damage += amount;
        if (source is null) return;
        if (source.HasKeyword("Lifelink")) GainLife(source.Controller, amount);
        if (source.HasKeyword("Deathtouch") && target.Types.Is(CardType.Creature)) target.Damage = Math.Max(target.Damage, target.Toughness);
        Fire(new TriggerEvent(TriggerMode.DamageDone, CardId: source.Id, TargetCardId: target.Id, Amount: amount, Combat: combat));
    }

    public void LoseLife(int player, int amount, Card? source)
    {
        if (amount <= 0) return;
        State.Players[player].Life -= amount;
        Note($"player {player} loses {amount} life (life {State.Players[player].Life})");
        if (source is not null && player != Me) RememberWinCard(source);
    }

    public void GainLife(int player, int amount)
    {
        if (amount <= 0) return;
        State.Players[player].Life += amount;
        Fire(new TriggerEvent(TriggerMode.LifeGained, Player: player, Amount: amount));
    }

    public void PayLife(int player, int amount)
    {
        if (amount <= 0) return;
        State.Players[player].Life -= amount;
        State.Players[player].LifePaidTotal += amount;
    }

    /// <summary>The cards that dealt the final blows / performed the win, for win_cards.</summary>
    public void RememberWinCard(Card c)
    {
        State.Record.LastHitBy = c.Name;
        if (!State.Record.WinCards.Contains(c.Name)) State.Record.WinCards.Add(c.Name);
    }
}

// ---- triggers ------------------------------------------------------------------------

public enum TriggerMode { ChangesZone, Phase, SpellCast, Attacks, DamageDone, AttackersDeclared, Drawn, LandPlayed, Discarded, LifeGained, Taps, Chapter }

public readonly record struct TriggerEvent(TriggerMode Mode, int? CardId = null, Zone From = Zone.None, Zone To = Zone.None,
    int? Player = null, string? Phase = null, int? TargetCardId = null, int Amount = 0, bool Combat = false);

public readonly record struct PendingTrigger(int SourceId, TriggerDef Trigger, TriggerEvent Event, int Controller);

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
        foreach (var id in sources)
        {
            var src = State[id];
            foreach (var t in src.Triggers)
            {
                if (src.IsBlank || !Triggers.Matches(this, t, src, e)) continue;
                _pending.Add(new PendingTrigger(id, t, e, src.Controller));
            }
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
            if (pt.Trigger["OptionalDecider"] is not null && !Decider.ChooseYesNo(this, pt.Controller, src, $"use {src.Name}'s trigger?"))
                continue;
            if (pt.Trigger.Modes is { Count: > 1 } modes)
                exec = modes[Decider.ChooseOption(this, pt.Controller, src, modes.Select(m => m.Description).ToList())];
            var item = new StackItem
            {
                Id = State.NextStackId++, SourceId = pt.SourceId, Ability = exec, Controller = pt.Controller,
                TriggeredCardId = pt.Event.Mode == TriggerMode.DamageDone ? pt.Event.TargetCardId : pt.Event.CardId,
                TriggeredPlayer = pt.Event.Player, ChainTargets = ChooseChainTargets(src, exec, pt.Controller),
                Description = $"{src.Name} trigger",
            };
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
    public static bool IsAutoManaAbility(AbilityDef ab)
    {
        if (!ab.IsManaAbility || !ab.Cost.Mana.IsZero || ab.Cost.Untap) return false;
        if (!ab.P.Values.Keys.All(k => k is "AB" or "Cost" or "Produced" or "Amount" or "SpellDescription" or "AILogic"
                or "AIPreference" or "SubAbility" or "ActivationZone" or "ColorOrType" or "Valid" or "ReflectProperty" or "RestrictValid"
                or "PrecostDesc" || Presence.Params.Contains(k))) return false;
        if (ab.Api == "Mana" && ProducedMask(ab["Produced"]) is null && ab["Produced"] != "Combo ColorIdentity") return false;
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
        var list = new List<ManaSource>();
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
                if (ab.Cost.Tap && (c.Tapped || IsSick(c))) continue;
                if (ab["RestrictValid"] is { } rv && !ManaRestriction.Allows(this, c, rv, _payingFor, _payingForSpell)) continue;
                if (ab.P.Values.Keys.Any(Presence.Params.Contains) && !Presence.Hold(ab.P, ContextFor(c))) continue;
                var mask = ab.Api == "ManaReflected" ? ReflectedMask(this, c, ab)
                    : ab["Produced"] == "Combo ColorIdentity" ? CommanderIdentity(player) : ProducedMask(ab["Produced"])!.Value;
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
        // Cheapest first; among equals, the least flexible so flexible sources stay available.
        list.Sort((a, b) =>
        {
            if (a.Penalty != b.Penalty) return a.Penalty.CompareTo(b.Penalty);
            var fa = System.Numerics.BitOperations.PopCount((uint)a.Options);
            var fb = System.Numerics.BitOperations.PopCount((uint)b.Options);
            return fa != fb ? fa.CompareTo(fb) : a.CardId.CompareTo(b.CardId);
        });
        return list;
    }

    /// <summary>
    /// Finds sources to use so the pool can pay <paramref name="cost"/>. Returns the sources and
    /// colors (empty if the pool alone suffices), or null if it can't be paid. Life spent on
    /// sources and phyrexian symbols must leave the player above 0.
    /// </summary>
    public List<(ManaSource Source, ManaColor Color)>? PlanManaPayment(int player, ManaCost cost, int x, int excludeId, int lifeAvailable)
    {
        var pool = State.Players[player].Pool;
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
    private void UseManaSource(ManaSource src, ManaColor color, int player)
    {
        var card = State[src.CardId];
        var ab = src.Ability;
        if (ab.Cost.Tap) Tap(card);
        foreach (var part in ab.Cost.Parts)
            switch (part.Kind)
            {
                case CostPartKind.PayLife: PayLife(player, int.Parse(part.Amount)); break;
                case CostPartKind.Sacrifice: MoveTo(card, Zone.Graveyard); break;
                case CostPartKind.ExileSelf: MoveTo(card, Zone.Exile); break;
            }
        State.Players[player].Pool.Add(color, src.Amount);
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
        if (cost.Tap && (source.Tapped || source.Zone != Zone.Battlefield || IsSick(source))) return false;
        var lifeNeeded = 0;
        foreach (var part in cost.Parts)
            switch (part.Kind)
            {
                case CostPartKind.PayLife:
                    lifeNeeded += int.TryParse(part.Amount, out var l) ? l : 0;
                    break;
                case CostPartKind.Sacrifice:
                    if (part.Valid == "CARDNAME") { if (source.Zone != Zone.Battlefield) return false; }
                    else if (CostCandidates(part, source, player).Count < AmountOf(part)) return false;
                    break;
                case CostPartKind.Discard:
                    if (part.Valid == "Hand") break;
                    if (part.Valid == "CARDNAME") { if (source.Zone != Zone.Hand) return false; break; }
                    if (CostCandidates(part, source, player).Count < AmountOf(part)) return false;
                    break;
                case CostPartKind.ExileFromGraveyard:
                case CostPartKind.TapUntapped:
                case CostPartKind.ExileFromHand:
                case CostPartKind.ReturnToHand:
                    if (CostCandidates(part, source, player).Count < AmountOf(part)) return false;
                    break;
                case CostPartKind.ExileSelf:
                    if (source.Zone != Zone.Hand) return false;
                    break;
                case CostPartKind.RemoveCounter:
                    if (source.CounterCount(part.Valid) < AmountOf(part)) return false;
                    break;
            }
        // Life can be paid only if the player has at least that much (rule 119.4).
        if (lifeNeeded > p.Life) return false;
        return PlanManaPayment(player, cost.Mana, x, excludeSourceFromMana ? source.Id : -1, Math.Max(0, p.Life - lifeNeeded)) is not null;
    }

    private static int AmountOf(CostPart part) => int.TryParse(part.Amount, out var n) ? n : 1;

    private List<int> CostCandidates(CostPart part, Card source, int player)
    {
        var ctx = new CompileContext { Face = source.CurrentFace, Missing = new() };
        var filter = part.Valid is "Card" or "" ? null : Filter.Parse(part.Valid, ctx);
        var x = new EffectContext { Game = this, Ability = PermanentSpell, Item = new StackItem { SourceId = source.Id, Ability = PermanentSpell, Controller = player } };
        IEnumerable<Card> pool = part.Kind switch
        {
            CostPartKind.Sacrifice => State.BattlefieldCards.Where(c => c.Controller == player),
            CostPartKind.TapUntapped => State.BattlefieldCards.Where(c => c.Controller == player && !c.Tapped && c.Id != source.Id),
            CostPartKind.Discard => State.Players[player].Hand.Select(id => State[id]).Where(c => c.Id != source.Id),
            CostPartKind.ExileFromGraveyard => State.Players[player].Graveyard.Select(id => State[id]).Where(c => c.Id != source.Id),
            CostPartKind.ExileFromHand => State.Players[player].Hand.Select(id => State[id]).Where(c => c.Id != source.Id),
            CostPartKind.ReturnToHand => State.BattlefieldCards.Where(c => c.Controller == player),
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

    private bool PayCostCore(Cost cost, Card source, int player, int x, bool excludeSourceFromMana)
    {
        var p = State.Players[player];
        var lifeNeeded = cost.Parts.Where(q => q.Kind == CostPartKind.PayLife).Sum(AmountOf);
        var plan = PlanManaPayment(player, cost.Mana, x, excludeSourceFromMana ? source.Id : -1, Math.Max(0, p.Life - lifeNeeded));
        if (plan is null) return false;

        foreach (var (src, color) in plan) UseManaSource(src, color, player);
        if (!ManaPayment.TryPay(ref p.Pool, cost.Mana, x, out var phyrexianLife)) return false;
        if (phyrexianLife > 0) PayLife(player, phyrexianLife);

        if (cost.Tap) Tap(source);
        foreach (var part in cost.Parts)
        {
            var n = AmountOf(part);
            switch (part.Kind)
            {
                case CostPartKind.PayLife:
                    PayLife(player, n);
                    break;
                case CostPartKind.Sacrifice:
                    if (part.Valid == "CARDNAME") MoveTo(source, Zone.Graveyard);
                    else
                        foreach (var id in Decider.ChooseCards(this, player, Choice.Sacrifice, source, CostCandidates(part, source, player), n, n))
                            MoveTo(State[id], Zone.Graveyard);
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
                        MoveTo(State[id], Zone.Exile);
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
                case CostPartKind.AddCounter:
                    source.Counters ??= new();
                    source.Counters[part.Valid] = source.CounterCount(part.Valid) + n;
                    break;
            }
        }
        return true;
    }
}
