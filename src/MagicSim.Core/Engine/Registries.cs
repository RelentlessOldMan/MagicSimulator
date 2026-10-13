namespace MagicSim.Core.Engine;

/// <summary>Keywords the engine handles (CONTRACT.md §6). Several only matter against real opponents.</summary>
public static class Keywords
{
    public static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
    {
        // Implemented in combat / timing / casting.
        "Haste", "Vigilance", "Double Strike", "Lifelink", "Defender", "Flash", "Storm", "Indestructible",
        // No effect in goldfish: nobody blocks, targets your stuff, or deals you combat damage.
        "Flying", "Trample", "First Strike", "Menace", "Reach", "Deathtouch", "Hexproof", "Shroud", "Ward", "Afflict", "Bushido",
        "Protection", "Landwalk", "Intimidate", "Fear", "Shadow", "Horsemanship", "Skulk",
        // Deck-building only.
        "Partner", "Partner with", "Friends forever", "Choose a Background", "Doctor's companion",
        // Handled where they matter: Devoid makes the card colorless.
        "Devoid", "Changeling", "Infect", "Wither", "Convoke", "Toxic", "Affinity", "Annihilator", "Myriad", "Class", "TypeCycling", "MayEffectFromOpeningHand", "Suspend", "Evoke", "Mobilize", "Cascade", "Ninjutsu", "Persist", "Undying",
        "Flashback", "Unearth", "Cumulative upkeep", "Prowl", "Spectacle", "Delve", "Escape", "Splice", "Kicker", "Bargain", "Transmute", "Morph", "Megamorph", "Fabricate", "Rebound", "Extort", "Exploit", "MayFlashSac", "Overload", "Entwine", "Ascend", "Jump-start", "Multikicker", "Soulbond", "Improvise", "Buyback", "Miracle", "Retrace", "Split second", "Cipher",
        // An Aura's own enchant restriction (Animate Dead swapping its "enchant" ability).
        "Enchant",
        Game.MayNotUntap,
        // Blocking never comes up against goldfish opponents.
        "HIDDEN CARDNAME can't block.",
    };
}

/// <summary>Trigger modes the engine fires, and how each one matches an event.</summary>
public static class Triggers
{
    /// <summary>Triggers on events that never happen against goldfish opponents (blocking, other game variants).</summary>
    private static readonly HashSet<string> NeverInGoldfish = new(StringComparer.Ordinal)
    {
        "Blocks", "AttackerBlocked", "AttackerBlockedByCreature", "ChaosEnsues", "SetInMotion", "PlaneswalkedTo",
    };

    private static readonly HashSet<string> CommonParams = new(StringComparer.Ordinal)
    {
        "Mode", "Execute", "TriggerDescription", "TriggerZones", "OptionalDecider", "Secondary", "ValidCard", "Static", "ActivationLimit", "ResolvedLimit", "TriggerController",
        // "If an opponent is the monarch": some player fits.
        "CheckDefinedPlayer",
        // Conditions aren't checked again on resolution here anyway.
        "NoResolvingCheck", "AILogic",
        // "If it's your main phase" on a trigger of another kind (Moraug's landfall).
        "PlayerTurn", "Phase",
        // "If you have 40 or more life" (Felidar Sovereign): LifeTotal$ You | LifeAmount$ GE40.
        "LifeTotal", "LifeAmount",
    };

    private static readonly Dictionary<string, HashSet<string>> ModeParams = new()
    {
        ["ChangesZone"] = new(StringComparer.Ordinal) { "Origin", "Destination" },
        // "At the beginning of enchanted player's first upkeep each turn" (Paradox Haze): FirstUpkeep$ True.
        ["Phase"] = new(StringComparer.Ordinal) { "Phase", "ValidPlayer", "FirstUpkeep" },
        // "Whenever a time counter is removed from this while it's exiled" (Aeon Chronicler).
        ["CounterRemoved"] = new(StringComparer.Ordinal) { "CounterType" },
        // "Whenever a +1/+1 counter is put on this" (Fathom Mage); "when this becomes monstrous".
        ["CounterAdded"] = new(StringComparer.Ordinal) { "CounterType" },
        ["BecomeMonstrous"] = new(StringComparer.Ordinal) { },
        // "Whenever this becomes untapped" (Key to the City).
        ["Untaps"] = new(StringComparer.Ordinal) { },
        // "Whenever you cast a spell that targets only [this]" (Zada): IsSingleTarget$, TargetsValid$.
        ["SpellCast"] = new(StringComparer.Ordinal) { "ValidActivatingPlayer", "ActivatorThisTurnCast", "IsSingleTarget", "TargetsValid" },
        ["CounterAddedOnce"] = new(StringComparer.Ordinal) { "CounterType", "ValidSource" },
        ["Attacks"] = new(StringComparer.Ordinal) { "Attacked", "FirstAttack" },
        ["BecomeMonarch"] = new(StringComparer.Ordinal) { "ValidPlayer" },
        ["Sacrificed"] = new(StringComparer.Ordinal) { "ValidPlayer" },
        ["ChangesZoneAll"] = new(StringComparer.Ordinal) { "ValidCards", "Origin", "Destination" },
        ["DamageDoneOnce"] = new(StringComparer.Ordinal) { "ValidSource", "ValidTarget", "CombatDamage" },
        ["TapsForMana"] = new(StringComparer.Ordinal) { "Activator" },
        ["ClassLevelGained"] = new(StringComparer.Ordinal) { "ClassLevel" },
        ["Cycled"] = new(StringComparer.Ordinal) { },
        ["SpellCastOrCopy"] = new(StringComparer.Ordinal) { "ValidActivatingPlayer" },
        ["SpellCopy"] = new(StringComparer.Ordinal) { "ValidActivatingPlayer" },
        ["AbilityCast"] = new(StringComparer.Ordinal) { "ValidActivatingPlayer", "ValidSA" },
        ["DamageDone"] = new(StringComparer.Ordinal) { "ValidSource", "ValidTarget", "CombatDamage" },
        ["AttackersDeclared"] = new(StringComparer.Ordinal) { "AttackingPlayer", "ValidAttackers", "AttackedTarget" },
        // "Whenever one or more creatures attack one of your opponents" (Frontier Warmonger): per player attacked.
        ["AttackersDeclaredOneTarget"] = new(StringComparer.Ordinal) { "AttackingPlayer", "AttackedTarget" },
        ["Drawn"] = new(StringComparer.Ordinal) { "Number", "FirstCardInDrawStep" },
        ["LandPlayed"] = new(StringComparer.Ordinal) { "Origin" },
        ["Discarded"] = new(StringComparer.Ordinal) { },
        ["LifeGained"] = new(StringComparer.Ordinal) { "ValidPlayer" },
        ["Taps"] = new(StringComparer.Ordinal) { },
        ["TurnFaceUp"] = new(StringComparer.Ordinal) { },
        ["Unattached"] = new(StringComparer.Ordinal) { "ValidAttachment", "ValidObject" },
        ["Exploited"] = new(StringComparer.Ordinal) { "ValidSource" },
        ["Always"] = new(StringComparer.Ordinal) { },
        ["LifeLost"] = new(StringComparer.Ordinal) { "ValidPlayer" },
        ["Countered"] = new(StringComparer.Ordinal) { "ValidCause", "ValidSA" },
        // "Whenever this becomes the target of a spell or ability [you control for the first time each turn]".
        ["BecomesTarget"] = new(StringComparer.Ordinal) { "ValidSource", "ValidTarget", "Valiant", "FirstTime" },
    };

    public static readonly HashSet<string> Supported = new(ModeParams.Keys.Concat(NeverInGoldfish), StringComparer.Ordinal);

    /// <summary>
    /// A trigger that can't fire against goldfish opponents: blocking and the like, or "whenever a spell or ability an
    /// opponent controls destroys …" (Karmic Justice). What it would do never matters.
    /// </summary>
    public static bool NeverFires(Forge.ForgeParams t) =>
        NeverInGoldfish.Contains(t["Mode"] ?? "") || t["Mode"] == "Destroyed" && t["ValidCauser"] == "Player.Opponent"
        // "Whenever an opponent casts a spell": goldfish opponents cast nothing.
        || t["Mode"] is "SpellCast" or "AbilityCast" or "SpellCastOrCopy" && t["ValidActivatingPlayer"] == "Opponent";

    public static void Validate(TriggerDef t, CompileContext ctx)
    {
        if (!ModeParams.TryGetValue(t.Mode, out var own)) return;   // unsupported (already reported) or never fires
        foreach (var key in t.P.Values.Keys)
            if (!CommonParams.Contains(key) && !own.Contains(key) && !Presence.Params.Contains(key))
                ctx.Need($"TriggerParam:{t.Mode}.{key}");
        Presence.Validate(t.P, ctx);
        if (t["ValidCard"] is { } vc) Filter.Parse(vc, ctx);
        if (t["ValidSource"] is { } vs)
        {
            if (t.Mode == "BecomesTarget")
                foreach (var part in vs.Split(','))
                {
                    if (TargetingSource(part.Trim()).Card is { } cardFilter) Filter.Parse(cardFilter, ctx);
                }
            else Filter.Parse(vs, ctx);
        }
        if (t["ValidTarget"] is { } vt) Filter.Parse(vt, ctx);
        if (t["TargetsValid"] is { } tvs) Filter.Parse(tvs, ctx);
        if (t["ValidAttachment"] is { } va) Filter.Parse(va, ctx);
        if (t["ValidObject"] is { } vo) Filter.Parse(vo, ctx);
        // "Whenever a spell or ability you control counters a spell": whose spell or ability countered it.
        if (t["ValidCause"] is { } cause && cause is not ("SpellAbility.YouCtrl" or "SpellAbility.OppCtrl")) ctx.Need("Countered.ValidCause:" + cause);
        if (t["ValidSA"] is { } sa)
        {
            if (t.Mode == "AbilityCast") ValidateActivated(sa, ctx);
            else Filter.Parse(sa, ctx);
        }
        // A granted "when this creature dies" trigger is controlled by that creature's controller.
        if (t["TriggerController"] is { } tc && tc != "TriggeredCardController") ctx.Need("TriggerController:" + tc);
        if (t["AttackingPlayer"] is { } ap && ap != "You") ctx.Need("AttackingPlayer:" + ap);
        if (t["ValidPlayer"] is { } vp && vp is not ("You" or "Player" or "Opponent"))
        {
            if (vp.StartsWith("Player.", StringComparison.Ordinal)) Filter.Parse(vp, ctx);
            else ctx.Need("TriggerValidPlayer:" + vp);
        }
        if (t["Number"] is { } num && !int.TryParse(num, out _)) ctx.Need("TriggerNumber:" + num);
        if (t["ActivatorThisTurnCast"] is { } atc && (atc.Length < 3 || !int.TryParse(atc[2..], out _))) ctx.Need("ActivatorThisTurnCast:" + atc);
        if (t["ValidSource"] is { } vsr && t.Mode == "CounterAddedOnce" && vsr != "You") ctx.Need("CounterAddedOnce.ValidSource:" + vsr);
        // Graveyard triggers ("whenever …, return this from your graveyard") fire from Game.Fire's graveyard sources.
        if (t["TriggerZones"] is { } tz && tz is not ("Battlefield" or "Command" or "Exile") && !(tz == "Stack" && t.Mode == "SpellCast")
            && !(tz == "Graveyard" && t.Mode is not ("ChangesZoneAll" or "DamageDoneOnce" or "CounterAddedOnce")))
            ctx.Need("TriggerZones:" + tz);
        if (t["CheckDefinedPlayer"] is { } cdp) Filter.Parse(cdp, ctx);
        if (t["AttackedTarget"] is { } atg) Filter.Parse(atg, ctx);
        if (t["LifeTotal"] is { } lt)
        {
            Defined.ValidatePlayers(lt, ctx);
            if (t["LifeAmount"] is not { Length: > 2 } la) ctx.Need("LifeAmount:" + t["LifeAmount"]);
            else Amount.Parse(la[2..], ctx);
        }
        if (t["Phase"] is { } ph && ph.Split(',').Any(one => one.Trim() is not ("Upkeep" or "Draw" or "Main1" or "Main2" or "End of Turn" or "End Of Turn" or "BeginCombat" or "EndCombat" or "Cleanup")))
            ctx.Need("TriggerPhase:" + ph);
    }

    /// <summary>Properties of an activated ability a trigger can check ("SpellAbility.!ManaAbility", "Activated.Loyalty").</summary>
    private static readonly Dictionary<string, Func<AbilityDef, bool>> ActivatedProps = new(StringComparer.Ordinal)
    {
        ["ManaAbility"] = a => a.IsManaAbility || a.Api == "Mana",
        ["hasTapCost"] = a => a.Cost.Tap,
        ["Loyalty"] = a => a["Planeswalker"] == "True",
        ["Ninjutsu"] = a => a.Api == "Ninjutsu",
        ["Equip"] = a => a.IsEquip,
        // Only you activate abilities here.
        ["YouCtrl"] = a => true,
    };

    public static void ValidateActivated(string valid, CompileContext ctx)
    {
        foreach (var term in valid.Split(','))
        {
            var parts = term.Trim().Split('.', 2);
            if (parts[0] is not ("SpellAbility" or "Activated")) { ctx.Need("ValidSA:" + term); continue; }
            if (parts.Length > 1)
                foreach (var p in parts[1].Split('+'))
                    if (!ActivatedProps.ContainsKey(p.TrimStart('!'))) ctx.Need("ValidSA:" + p);
        }
    }

    public static bool ActivatedMatches(string valid, AbilityDef ab) =>
        valid.Split(',').Any(term => term.Trim().Split('.', 2) is var parts
            && (parts.Length == 1 || parts[1].Split('+').All(p => ActivatedProps[p.TrimStart('!')](ab) != p.StartsWith('!'))));

    public static bool Matches(Game g, TriggerDef t, Card source, TriggerEvent e)
    {
        var x = new EffectContext
        {
            Game = g, Ability = t.Execute ?? Game.PermanentSpell,
            Item = new StackItem { SourceId = source.Id, Ability = t.Execute ?? Game.PermanentSpell, Controller = source.Controller },
            FaceOverride = t.Face,
        };
        // A graveyard trigger works only from the graveyard.
        if (t["TriggerZones"] == "Graveyard") return source.Zone == Zone.Graveyard && ModeMatches(g, t, source, e, x) && Presence.Hold(t.P, x);
        if (t["TriggerZones"] == "Exile") return source.Zone == Zone.Exile && ModeMatches(g, t, source, e, x) && Presence.Hold(t.P, x);
        // Triggers work from the battlefield, except the moved card's own "look back" triggers.
        // Effects and emblems (and "eminence" commanders) trigger from the command zone.
        var sourceActive = source.Zone == Zone.Battlefield
            || (e.Mode == TriggerMode.ChangesZone && e.CardId == source.Id && e.From == Zone.Battlefield)
            || source.Zone == Zone.Command && (source.IsEffect || t["TriggerZones"] == "Command")
            // "When you cast this spell" (cascade, casualty, …) triggers from the stack.
            || source.Zone == Zone.Stack && t["TriggerZones"] == "Stack"
            // "When you draw this card" (miracle) from the hand it was drawn into.
            || e.Mode == TriggerMode.Drawn && e.CardId == source.Id && source.Zone == Zone.Hand && t["TriggerZones"] is null
            // "As you cast this spell" (Static$, no zone): Approach of the Second Sun noting that it was cast.
            || source.Zone == Zone.Stack && e.Mode == TriggerMode.SpellCast && e.CardId == source.Id && t["Static"] == "True" && t["TriggerZones"] is null
            // An Equipment that leaves the battlefield becomes unattached as it goes.
            || e.Mode == TriggerMode.Unattached && e.CardId == source.Id
            // "When this exploits a creature" even when it exploits itself.
            || e.Mode == TriggerMode.Exploited && e.TargetCardId == source.Id
            // "When you cycle this card" triggers from wherever cycling put it.
            || e.Mode == TriggerMode.Cycled && e.CardId == source.Id && t["TriggerZones"] is null;
        if (!sourceActive) return false;
        if (t["CheckDefinedPlayer"] is { } check && !g.State.Players.Any(p => !p.Lost && FilterOf(x, check).MatchesPlayer(p.Index, x))) return false;
        if (!ModeMatches(g, t, source, e, x)) return false;
        if (t.Mode != "Phase" && t["Phase"] is { } phases && !phases.Split(',').Any(ph => Conditions.PhaseIs(g.State.Step, ph.Trim()))) return false;
        if (t["PlayerTurn"] == "True" && g.State.ActivePlayer != source.Controller) return false;
        if (t["LifeTotal"] is { } whose && t["LifeAmount"] is { } life
            && !Defined.Players(whose, x).All(p => Filter.Compare(g.State.Players[p].Life, life[..2], Effect.EvalAmount(x, life[2..])))) return false;
        return Presence.Hold(t.P, x);
    }

    /// <summary>Whether a delayed trigger's event has happened. Its source need not be anywhere in particular.</summary>
    public static bool MatchesDelayed(Game g, DelayedTrigger d, TriggerEvent e)
    {
        var t = d.Trigger;
        var host = g.State[d.HostId];
        var x = new EffectContext
        {
            Game = g, Ability = t.Execute ?? Game.PermanentSpell, FaceOverride = t.Face,
            Item = new StackItem { SourceId = d.HostId, Ability = t.Execute ?? Game.PermanentSpell, Controller = d.Controller, DelayRemembered = d.Remembered },
        };
        if (t.Mode == "Phase" && e.Mode == TriggerMode.Phase)
            return PhaseMatches(t["Phase"], e.Phase) && PlayerMatches(t["ValidPlayer"], e.Player, d.Controller, x, t.Face)
                && (d.ForPlayers is not { } whose || e.Player is { } active && whose.Contains(active))
                && Presence.Hold(t.P, x);
        return ModeMatches(g, t, host, e, x) && Presence.Hold(t.P, x);
    }

    private static bool ModeMatches(Game g, TriggerDef t, Card source, TriggerEvent e, EffectContext x)
    {
        switch (t.Mode)
        {
            case "ChangesZone" when e.Mode == TriggerMode.ChangesZone:
                if (!ZoneMatches(t["Origin"], e.From) || !ZoneMatches(t["Destination"], e.To)) return false;
                return CardMatches(g, t, e.CardId, x, source);
            case "CounterAdded" when e.Mode == TriggerMode.CounterAdded:
                return (t["CounterType"] is not { } addedKind || addedKind == e.CounterType) && CardMatches(g, t, e.CardId, x, source);
            case "Untaps" when e.Mode == TriggerMode.Untaps:
                return CardMatches(g, t, e.CardId, x, source);
            case "BecomeMonstrous" when e.Mode == TriggerMode.BecomeMonstrous:
                return CardMatches(g, t, e.CardId, x, source);
            case "CounterRemoved" when e.Mode == TriggerMode.CounterRemoved:
                return (t["CounterType"] is not { } removedKind || removedKind == e.CounterType) && CardMatches(g, t, e.CardId, x, source);
            case "Phase" when e.Mode == TriggerMode.Phase:
                if (t["FirstUpkeep"] == "True" && (e.Phase != "Upkeep" || g.State.ThisTurn.Upkeeps != 1)) return false;
                if (!PhaseMatches(t["Phase"] ?? (t["FirstUpkeep"] == "True" ? "Upkeep" : null), e.Phase)) return false;
                return PlayerMatches(t["ValidPlayer"], e.Player, source.Controller, x, t.Face);
            case "TurnFaceUp" when e.Mode == TriggerMode.TurnFaceUp:
                return CardMatches(g, t, e.CardId, x, source);
            case "Countered" when e.Mode == TriggerMode.Countered:
                if (t["ValidCause"] is { } cause && (cause == "SpellAbility.YouCtrl") != (e.Player == source.Controller)) return false;
                if (t["ValidSA"] is { } sa && !FilterOf(x, sa).MatchesCard(g.State[e.CardId!.Value], x)) return false;
                return CardMatches(g, t, e.CardId, x, source);
            case "LifeLost" when e.Mode == TriggerMode.LifeLost:
                return PlayerMatches(t["ValidPlayer"], e.Player, source.Controller, x, t.Face);
            case "Exploited" when e.Mode == TriggerMode.Exploited:
                return (t["ValidSource"] is not { } exploiter || Effect.FilterFor(t.Face ?? x.SVarFace, exploiter).MatchesCard(g.State[e.TargetCardId!.Value], x))
                    && CardMatches(g, t, e.CardId, x, source);
            case "Unattached" when e.Mode == TriggerMode.Unattached:
                return (t["ValidAttachment"] is not { } attachment || Effect.FilterFor(t.Face ?? x.SVarFace, attachment).MatchesCard(g.State[e.CardId!.Value], x))
                    && (t["ValidObject"] is not { } attachedTo || Effect.FilterFor(t.Face ?? x.SVarFace, attachedTo).MatchesCard(g.State[e.TargetCardId!.Value], x));
            case "SpellCast" when e.Mode == TriggerMode.SpellCast && t["ActivatorThisTurnCast"] is { } nth:
                // "Whenever you cast your second spell each turn": this is that spell.
                if (!Filter.Compare(g.State.ThisTurn.SpellsCast, nth[..2], int.Parse(nth[2..]))) return false;
                return PlayerMatches(t["ValidActivatingPlayer"], e.Player, source.Controller, x, t.Face) && CardMatches(g, t, e.CardId, x, source);
            case "Cycled" when e.Mode == TriggerMode.Cycled:
                return CardMatches(g, t, e.CardId, x, source);
            case "SpellCastOrCopy" when e.Mode is TriggerMode.SpellCast or TriggerMode.SpellCopied:
            case "AbilityCast" when e.Mode == TriggerMode.AbilityCast:
                return PlayerMatches(t["ValidActivatingPlayer"], e.Player, source.Controller, x, t.Face)
                       && (t["ValidSA"] is not { } vsa || ActivatedMatches(vsa, e.Activated!.Ability))
                       && CardMatches(g, t, e.CardId, x, source);
            case "SpellCopy" when e.Mode == TriggerMode.SpellCopied:
                return PlayerMatches(t["ValidActivatingPlayer"], e.Player, source.Controller, x, t.Face) && CardMatches(g, t, e.CardId, x, source);
            case "ClassLevelGained" when e.Mode == TriggerMode.ClassLevelGained:
                return (t["ClassLevel"] is not { } lvl || lvl == e.Amount.ToString()) && CardMatches(g, t, e.CardId, x, source);
            case "TapsForMana" when e.Mode == TriggerMode.TapsForMana:
                return PlayerMatches(t["Activator"], e.Player, source.Controller, x, t.Face) && CardMatches(g, t, e.CardId, x, source);
            case "CounterAddedOnce" when e.Mode == TriggerMode.CounterAddedOnce:
                if (t["CounterType"] is { } ct && ct != e.CounterType) return false;
                if (t["ValidSource"] is { } vsrc && vsrc != "You") return false;
                return CardMatches(g, t, e.CardId, x, source);
            case "SpellCast" when e.Mode == TriggerMode.SpellCast && (t["IsSingleTarget"] is not null || t["TargetsValid"] is not null):
            {
                var spell = g.State.Stack.LastOrDefault(i => i.IsSpell && i.SourceId == e.CardId);
                var targets = spell?.ChainTargets.SelectMany(ts => ts).Distinct().ToList() ?? new();
                if (t["IsSingleTarget"] == "True" && targets.Count != 1) return false;
                if (t["TargetsValid"] is { } tv && (targets.Count == 0
                        || !targets.All(tr => tr.Player is { } tp ? FilterOf(x, tv).MatchesPlayer(tp, x) : FilterOf(x, tv).MatchesCard(g.State[tr.CardId!.Value], x))))
                    return false;
                return PlayerMatches(t["ValidActivatingPlayer"], e.Player, source.Controller, x, t.Face) && CardMatches(g, t, e.CardId, x, source);
            }
            case "SpellCast" when e.Mode == TriggerMode.SpellCast:
                return PlayerMatches(t["ValidActivatingPlayer"], e.Player, source.Controller, x, t.Face) && CardMatches(g, t, e.CardId, x, source);
            case "Attacks" when e.Mode == TriggerMode.Attacks && t["FirstAttack"] == "True" && g.State.ThisTurn.Attacks.GetValueOrDefault(e.CardId!.Value) != 1:
                return false;   // "attacks for the first time each turn"
            case "Attacks" when e.Mode == TriggerMode.Attacks:
                // "Attacks you or a planeswalker you control": the player part (planeswalkers are never attacked here).
                if (t["Attacked"] is { } attacked && !attacked.Split(',').Any(part => !part.StartsWith("Planeswalker", StringComparison.Ordinal)
                        && !part.StartsWith("Battle", StringComparison.Ordinal) && PlayerMatches(part, e.Player, source.Controller, x, t.Face)))
                    return false;
                return CardMatches(g, t, e.CardId, x, source);
            case "BecomeMonarch" when e.Mode == TriggerMode.BecomeMonarch:
                return PlayerMatches(t["ValidPlayer"], e.Player, source.Controller, x, t.Face);
            case "DamageDone" when e.Mode == TriggerMode.DamageDone:
                if (t["CombatDamage"] is { } cd && (cd == "True") != e.Combat) return false;
                if (t["ValidSource"] is { } vs && (e.CardId is not { } src || !FilterOf(x, vs).MatchesCard(g.State[src], x))) return false;
                if (t["ValidTarget"] is { } vt)
                {
                    var f = FilterOf(x, vt);
                    if (e.Player is { } p ? !f.MatchesPlayer(p, x) : e.TargetCardId is not { } tc || !f.MatchesCard(g.State[tc], x)) return false;
                }
                return true;
            case "BecomesTarget" when e.Mode == TriggerMode.BecomesTarget:
                if (t["ValidSource"] is { } targeter && !targeter.Split(',').Any(part => TargetingSource(part.Trim()) is var (spellOnly, filter)
                        && (!spellOnly || e.Activated!.IsSpell) && (filter is null || FilterOf(x, filter).MatchesCard(g.State[e.CardId!.Value], x))))
                    return false;
                if (t["ValidTarget"] is { } targeted)
                {
                    var f = FilterOf(x, targeted);
                    if (e.Player is { } p ? !f.MatchesPlayer(p, x) : e.TargetCardId is not { } tc || !f.MatchesCard(g.State[tc], x)) return false;
                }
                return true;
            case "AttackersDeclaredOneTarget" when e.Mode == TriggerMode.AttackersDeclared:
                if (t["AttackingPlayer"] == "You" && e.Player != source.Controller) return false;
                return t["AttackedTarget"] is not { } oneTarget || g.State.ThisTurn.DefendingPlayer is { } attackedPlayer && FilterOf(x, oneTarget).MatchesPlayer(attackedPlayer, x);
            case "AttackersDeclared" when e.Mode == TriggerMode.AttackersDeclared:
                if (e.Player != source.Controller) return false;
                // "Whenever enchanted player is attacked": everything attacks one player here.
                if (t["AttackedTarget"] is { } attackedTarget && !(g.State.ThisTurn.DefendingPlayer is { } dp && FilterOf(x, attackedTarget).MatchesPlayer(dp, x))) return false;
                return t["ValidAttackers"] is not { } va || g.State.Attackers.Any(a => FilterOf(x, va).MatchesCard(g.State[a], x));
            case "Drawn" when e.Mode == TriggerMode.Drawn && t["Number"] is { } number:
                return int.TryParse(number, out var nthCard) && e.Amount == nthCard && CardMatches(g, t, e.CardId, x, source, e.Player);
            case "Drawn" when e.Mode == TriggerMode.Drawn && t["FirstCardInDrawStep"] is { } first:
            {
                var isFirst = e.Amount == 1 && g.State.Step == Step.Draw && g.State.ActivePlayer == e.Player;
                return isFirst == (first == "True") && CardMatches(g, t, e.CardId, x, source, e.Player);
            }
            case "Drawn" when e.Mode == TriggerMode.Drawn:
            case "LandPlayed" when e.Mode == TriggerMode.LandPlayed && t["Origin"] is { } landFrom && !ZoneMatches(landFrom, e.From):
                return false;   // "whenever you play a land from exile"
            case "LandPlayed" when e.Mode == TriggerMode.LandPlayed:
            case "Discarded" when e.Mode == TriggerMode.Discarded:
            case "Taps" when e.Mode == TriggerMode.Taps:
                return CardMatches(g, t, e.CardId, x, source, e.Player);
            case "Sacrificed" when e.Mode == TriggerMode.Sacrificed:
                return PlayerMatches(t["ValidPlayer"], e.Player, source.Controller, x, t.Face) && CardMatches(g, t, e.CardId, x, source);
            case "LifeGained" when e.Mode == TriggerMode.LifeGained:
                return PlayerMatches(t["ValidPlayer"], e.Player, source.Controller, x, t.Face);
            default:
                return false;
        }
    }

    private static Filter FilterOf(EffectContext x, string text) => Effect.FilterFor(x.SVarFace, text);

    /// <summary>"Phase$ Main1,Main2": any of these phases.</summary>
    private static bool PhaseMatches(string? spec, string? phase) =>
        spec is not null && spec.Split(',').Any(one => string.Equals(one.Trim(), phase, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether the event's card fits ValidCard$. A card from a goldfish opponent's hand (drawn or discarded) isn't a
    /// real card: it fits only filters about whose it is (<see cref="Filter.MatchesUnknownCard"/>).
    /// </summary>
    private static bool CardMatches(Game g, TriggerDef t, int? cardId, EffectContext x, Card source, int? unknownOwner = null)
    {
        if (t["ValidCard"] is not { } vc) return true;
        if (cardId is not { } id) return unknownOwner is { } owner && FilterOf(x, vc).MatchesUnknownCard(owner, x);
        return FilterOf(x, vc).MatchesCard(g.State[id], x);
    }

    /// <summary>
    /// What may target for a BecomesTarget trigger: "Spell" / "Spell.Aura" (spells only), "SpellAbility.OppCtrl" (spells
    /// or abilities), or a card filter for spells ("Instant,Sorcery").
    /// </summary>
    private static (bool SpellOnly, string? Card) TargetingSource(string part)
    {
        var dot = part.IndexOf('.');
        var head = dot < 0 ? part : part[..dot];
        var rest = dot < 0 ? null : "Card" + part[dot..];
        return head switch
        {
            "Spell" => (true, rest),
            "SpellAbility" => (false, rest),
            _ => (true, part),
        };
    }

    /// <summary>"This ability triggers only once each turn" (ActivationLimit$): counts this triggering if allowed.</summary>
    public static bool UnderLimit(Game g, Card source, TriggerDef t)
    {
        // "Do this only once each turn" (ResolvedLimit$) is counted the same way, when it triggers, and so is
        // "becomes the target … for the first time each turn" (Valiant$, FirstTime$).
        var lim = t["ActivationLimit"] ?? t["ResolvedLimit"] ?? (t["Valiant"] == "True" || t["FirstTime"] == "True" ? "1" : null);
        if (lim is null || !int.TryParse(lim, out var limit)) return true;
        source.TriggerUses ??= new();
        var (turn, count) = source.TriggerUses.GetValueOrDefault(t);
        if (turn != g.State.Turn) count = 0;
        if (count >= limit) return false;
        source.TriggerUses[t] = (g.State.Turn, count + 1);
        return true;
    }

    public static bool ZoneMatches(string? spec, Zone z) =>
        spec is null or "Any" || spec.Split(',').Any(s => Enum.TryParse<Zone>(s.Trim(), out var want) && want == z);

    private static bool PlayerMatches(string? spec, int? player, int controller, EffectContext x, FaceDef? face) => spec switch
    {
        null or "Player" => true,
        "You" => player == controller,
        "Opponent" => player != controller,
        // "Player.Chosen": the player chosen as this entered (The Rack).
        _ => player is { } p && Effect.FilterFor(face ?? x.SVarFace, spec).MatchesPlayer(p, x),
    };
}

/// <summary>"Condition…$" parameters that decide whether one link of an ability chain happens.</summary>
public static class Conditions
{
    /// <summary>Forge phase names: Main1, Main2, BeginCombat, …, "End of Turn".</summary>
    public static bool PhaseIs(Step step, string name) => name switch
    {
        "Main1" => step == Step.Main1,
        "Main2" => step == Step.Main2,
        "Untap" => step == Step.Untap,
        "Upkeep" => step == Step.Upkeep,
        "Draw" => step == Step.Draw,
        "BeginCombat" => step == Step.BeginCombat,
        "Declare Attackers" or "DeclareAttackers" => step == Step.DeclareAttackers,
        "CombatDamage" or "Combat Damage" => step == Step.CombatDamage,
        "EndCombat" => step == Step.EndCombat,
        "Cleanup" => step == Step.Cleanup,
        "End of Turn" or "End Of Turn" => step == Step.End,
        _ => false,
    };

    public static readonly HashSet<string> Params = new(StringComparer.Ordinal)
    {
        "ConditionCheckSVar", "ConditionSVarCompare", "ConditionDefined", "ConditionPresent", "ConditionCompare", "ConditionDescription",
        "ConditionPhases", "ConditionPlayerTurn", "ConditionFirstCombat", "ConditionZone", "Condition", "ConditionOpponentTurn",
    };

    public static void Validate(AbilityDef ab, CompileContext ctx)
    {
        foreach (var key in ab.P.Values.Keys)
            if (key.StartsWith("Condition", StringComparison.Ordinal) && !Params.Contains(key))
                ctx.Need("Condition:" + key);
        if (ab["ConditionCheckSVar"] is { } sv)
        {
            Amount.Parse(sv, ctx);
            var cmp = ab["ConditionSVarCompare"] ?? "GE1";
            if (cmp.Length < 3) ctx.Need("ConditionCompare:" + cmp);
            else Amount.Parse(cmp[2..], ctx);
        }
        if (ab["ConditionPresent"] is { } present) Filter.Parse(present, ctx);
        if (ab["ConditionDefined"] is { } def) Defined.ValidateCards(def, ctx);
        if (ab["ConditionZone"] is { } zone && !Enum.TryParse<Zone>(zone, out _)) ctx.Need("ConditionZone:" + zone);
        if (ab["Condition"] is { } cond && !Presence.IsKnownCondition(cond)) ctx.Need("Condition:" + cond);
    }

    public static bool Hold(EffectContext x)
    {
        var ab = x.Ability;
        if (ab["ConditionPhases"] is { } phases && !phases.Split(',').Any(ph => PhaseIs(x.S.Step, ph.Trim()))) return false;
        if (ab["ConditionPlayerTurn"] == "True" && x.S.ActivePlayer != x.Controller) return false;
        if (ab["ConditionOpponentTurn"] == "True" && x.S.ActivePlayer == x.Controller) return false;
        if (ab["ConditionFirstCombat"] == "True" && x.S.ThisTurn.Combats > 1) return false;
        if (ab["Condition"] is { } cond && !Presence.ConditionHolds(cond, x)) return false;
        var ctx = new CompileContext { Face = x.SVarFace, Missing = new() };
        if (ab["ConditionCheckSVar"] is { } sv)
        {
            var cmp = ab["ConditionSVarCompare"] ?? "GE1";
            var left = Amount.Parse(sv, ctx).Eval(x);
            var right = Amount.Parse(cmp[2..], ctx).Eval(x);
            if (!Filter.Compare(left, cmp[..2], right)) return false;
        }
        if (ab["ConditionPresent"] is { } present)
        {
            var filter = Filter.Parse(present, ctx);
            var cards = ab["ConditionDefined"] is { } def
                ? Defined.Cards(def, x).Select(id => x.S[id])
                : x.Game.CardsIn(ab["ConditionZone"] is { } zone ? Enum.Parse<Zone>(zone) : Zone.Battlefield);
            var count = cards.Count(c => filter.MatchesCard(c, x));
            var cmp = ab["ConditionCompare"] ?? "GE1";
            if (!Filter.Compare(count, cmp[..2], int.Parse(cmp[2..]))) return false;
        }
        return true;
    }
}
