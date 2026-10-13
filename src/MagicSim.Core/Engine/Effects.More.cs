namespace MagicSim.Core.Engine;

// ---- modal spells ----------------------------------------------------------------------

/// <summary>
/// "Choose one —": the compiler expands each allowed combination of modes into its own ability
/// chain headed by this effect, so resolving the head itself does nothing.
/// </summary>
sealed class CharmEffect : Effect
{
    public override string Api => "Charm";
    public override IReadOnlySet<string> Params { get; } = P("Choices", "CharmNum", "MinCharmNum", "AdditionalDescription", "CanRepeatModes");
    public override void Resolve(EffectContext x) { }
}

// ---- tokens and attachments --------------------------------------------------------------

sealed class TokenEffect : Effect
{
    public override string Api => "Token";
    public override IReadOnlySet<string> Params { get; } = P(
        "TokenScript", "TokenOwner", "TokenAmount", "TokenTapped", "RememberTokens", "TokenPower", "TokenToughness",
        "TokenAttacking", "PumpKeywords", "PumpDuration", "AtEOT", "AttachedTo");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        if (ab["TokenScript"] is not { } scripts) { ctx.Need("Token:NoScript"); return; }
        ab.Tokens = new();
        foreach (var name in scripts.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var def = ctx.Tokens?.Invoke(name);
            if (def is null) { ctx.Need("TokenScript:" + name); continue; }
            foreach (var m in def.Missing) ctx.Need(m);
            ab.Tokens.Add(def);
        }
        Defined.ValidatePlayers(ab["TokenOwner"], ctx);
        ValidateAmount(ab, "TokenAmount", ctx);
        ValidateAmount(ab, "TokenPower", ctx);
        ValidateAmount(ab, "TokenToughness", ctx);
        if (ab["TokenAttacking"] is { } att && att != "True") ctx.Need("Token.TokenAttacking:" + att);
        if (ab["PumpDuration"] is { } pd && pd is not ("EOT" or "EndOfTurn")) ctx.Need("Token.PumpDuration:" + pd);
        if (ab["PumpKeywords"] is { } kws)
            foreach (var k in kws.Split('&', StringSplitOptions.TrimEntries))
                if (!Keywords.Supported.Contains(Forge.ForgeRequirements.KeywordName(k))) ctx.Need("Keyword:" + k);
        if (ab["AtEOT"] is { } eot && eot is not ("Exile" or "Sacrifice")) ctx.Need("Token.AtEOT:" + eot);
        Defined.ValidateCards(ab["AttachedTo"], ctx);
    }

    public override void Resolve(EffectContext x)
    {
        var ab = x.Ability;
        var n = Amount(x, ab["TokenAmount"], 1);
        var tapped = ab["TokenTapped"] == "True";
        int? power = ab["TokenPower"] is { } tp ? Amount(x, tp, 0) : null;
        int? toughness = ab["TokenToughness"] is { } tt ? Amount(x, tt, 0) : null;
        foreach (var owner in Defined.Players(ab["TokenOwner"], x))
            foreach (var (def, count) in ab.Tokens!.SelectMany(d => Replacements.Tokens(x.Game, d, owner, n)).ToList())
                for (var i = count; i > 0; i--)
                {
                    var token = x.Game.CreateToken(def, owner, tapped, power, toughness);
                    if (ab["RememberTokens"] == "True") (x.Host.Remembered ??= new()).Add(token.Id);
                    if (ab["PumpKeywords"] is { } kws)
                    {
                        token.TempKeywords ??= new HashSet<string>(StringComparer.Ordinal);
                        foreach (var k in kws.Split('&', StringSplitOptions.TrimEntries)) token.TempKeywords.Add(Forge.ForgeRequirements.KeywordName(k));
                    }
                    if (ab["TokenAttacking"] == "True" && x.S.Step == Step.DeclareAttackers) x.S.Attackers.Add(token.Id);
                    if (ab["AtEOT"] is { } eot) x.S.ThisTurn.AtEndStep.Add((token.Id, eot));
                    // An Aura token created attached to something (a Role on a creature).
                    if (ab["AttachedTo"] is { } at && Defined.Cards(at, x).Select(id => x.S[id]).FirstOrDefault(c => c.Zone == Zone.Battlefield) is { } host)
                        token.AttachedTo = host.Id;
                }
    }
}

/// <summary>Attaches an Aura or Equipment (Object$, default the source) to the target or Defined$ card.</summary>
sealed class AttachEffect : Effect
{
    public override string Api => "Attach";
    public override IReadOnlySet<string> Params { get; } = P("Defined", "Object");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        Defined.ValidateCards(ab["Defined"], ctx);
        Defined.ValidateCards(ab["Object"], ctx);
    }

    public override void Resolve(EffectContext x)
    {
        var to = (x.Ability["Defined"] is { } d ? Defined.Cards(d, x) : Defined.Cards("Targeted", x))
            .Select(id => x.S[id]).FirstOrDefault(c => c.Zone == Zone.Battlefield);
        if (to is null) return;
        foreach (var id in Defined.Cards(x.Ability["Object"], x))
        {
            var obj = x.S[id];
            if (obj.Zone != Zone.Battlefield || obj.Id == to.Id) continue;
            if (obj.AttachedTo >= 0 && obj.AttachedTo != to.Id)
                x.Game.Fire(new TriggerEvent(TriggerMode.Unattached, CardId: obj.Id, TargetCardId: obj.AttachedTo));
            obj.AttachedTo = to.Id;
            x.Game.Note($"  {obj.Name} attached to {to.Name}");
        }
    }
}

// ---- mass effects ---------------------------------------------------------------------

sealed class ChangeZoneAllEffect : Effect
{
    public override string Api => "ChangeZoneAll";
    public override IReadOnlySet<string> Params { get; } = P(
        "Origin", "Destination", "ChangeType", "Defined", "Shuffle", "LibraryPosition", "Tapped", "RememberChanged", "RandomOrder",
        "UseAllOriginZones", "GainControl", "DefinedPlayer", "ForgetOtherRemembered",
        // Face down only hides the cards from opponents, which goldfish opponents don't care about.
        "ExileFaceDown");

    private static readonly HashSet<string> Zones = new() { "Library", "Hand", "Battlefield", "Graveyard", "Exile" };

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        if (ab["GainControl"] is { } gc && (gc != "True" || ab["Destination"] != "Battlefield")) ctx.Need("ChangeZoneAll.GainControl:" + ab["Destination"]);
        foreach (var key in new[] { "Origin", "Destination" })
            if (ab[key] is not { } z || !z.Split(',').All(s => Zones.Contains(s.Trim()))) ctx.Need($"ChangeZoneAll.{key}:{ab[key]}");
        if (ab["ChangeType"] is { } ct) Filter.Parse(ct, ctx);
        Defined.ValidatePlayers(ab["Defined"], ctx);
        Defined.ValidatePlayers(ab["DefinedPlayer"], ctx);
    }

    public override void Resolve(EffectContext x)
    {
        var ab = x.Ability;
        var origins = ab["Origin"]!.Split(',').Select(s => Enum.Parse<Zone>(s.Trim())).ToList();
        var dest = Enum.Parse<Zone>(ab["Destination"]!);
        var filter = ab["ChangeType"] is { } ct ? FilterFor(x.SVarFace, ct) : null;
        // Whose zones: Defined$ or targeted players, else everyone's.
        var players = (ab["Defined"] ?? ab["DefinedPlayer"]) is { } d ? Defined.Players(d, x)
            : ab.TargetFilter is not null ? Defined.Players("Targeted", x)
            : x.S.Players.Select(p => p.Index).ToList();
        if (ab["ForgetOtherRemembered"] == "True") { x.Host.Remembered = null; x.Host.RememberedUnknown = 0; }
        // A goldfish opponent's hand is a count: the whole hand moves (to their library count, or out of the game).
        if (origins.Contains(Zone.Hand) && dest != Zone.Hand)
            foreach (var p in players.Select(i => x.S.Players[i]).Where(p => p.IsGoldfish && p.AbstractHand > 0))
            {
                if (filter is not null && !filter.MatchesUnknownCard(p.Index, x)) continue;
                var n = p.AbstractHand;
                p.AbstractHand = 0;
                if (dest == Zone.Library) p.AbstractLibrary += n;
                if (ab["RememberChanged"] == "True") x.Host.RememberedUnknown += n;
                x.Game.Note($"  opponent {p.Index}'s hand of {n} to {dest}");
            }
        var cards = new List<int>();
        foreach (var z in origins)
            cards.AddRange(z == Zone.Battlefield
                ? x.S.Battlefield.Where(id => players.Contains(x.S[id].Controller))
                : players.SelectMany(p => x.S.ZoneList(p, z)));
        cards = cards.Where(id => filter is null || filter.MatchesCard(x.S[id], x)).ToList();
        if (ab["RandomOrder"] == "True") x.S.Rng.Shuffle(cards);
        var position = ab["LibraryPosition"] is { } lp ? int.Parse(lp) : 0;
        var tapped = ab["Tapped"] == "True";
        foreach (var id in cards)
        {
            // "Put all creature cards from all graveyards onto the battlefield under your control."
            if (ab["GainControl"] == "True" && x.S[id].Zone != Zone.Battlefield) x.S[id].Controller = x.Controller;
            x.Game.MoveTo(x.S[id], dest, position, tapped);
            if (ab["RememberChanged"] == "True") (x.Host.Remembered ??= new()).Add(id);
        }
        if (ab["Shuffle"] == "True")
            foreach (var p in players) x.Game.Shuffle(p);
        x.Game.Note($"  moved {cards.Count} card(s) to {dest}");
    }
}

sealed class DestroyAllEffect : Effect
{
    public override string Api => "DestroyAll";
    // Hidden$ only says the choice of what's destroyed isn't shown.
    public override IReadOnlySet<string> Params { get; } = P("ValidCards", "NoRegen", "RememberDestroyed", "Hidden");
    public override void Validate(AbilityDef ab, CompileContext ctx) => Filter.Parse(ab["ValidCards"] ?? "Card", ctx);

    public override void Resolve(EffectContext x)
    {
        var filter = FilterFor(x.SVarFace, x.Ability["ValidCards"] ?? "Card");
        foreach (var c in x.S.BattlefieldCards.Where(c => filter.MatchesCard(c, x)).ToList())
            if (x.Game.Destroy(c, regenerate: x.Ability["NoRegen"] != "True") && x.Ability["RememberDestroyed"] == "True")
                (x.Host.Remembered ??= new()).Add(c.Id);
    }
}

/// <summary>"Sacrifice all …" / "that creature's controller sacrifices it": each card is sacrificed by its controller.</summary>
sealed class SacrificeAllEffect : Effect
{
    public override string Api => "SacrificeAll";
    public override IReadOnlySet<string> Params { get; } = P("ValidCards", "Defined", "Controller", "RememberSacrificed");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        if (ab["ValidCards"] is { } vc) Filter.Parse(vc, ctx);
        else Defined.ValidateCards(ab["Defined"], ctx);
        if (ab["Controller"] is { } ctl && ctl != "You") ctx.Need("SacrificeAll.Controller:" + ctl);
    }

    public override void Resolve(EffectContext x)
    {
        IEnumerable<Card> cards = x.Ability["ValidCards"] is { } vc
            ? x.S.BattlefieldCards.Where(c => FilterFor(x.SVarFace, vc).MatchesCard(c, x))
            : Defined.Cards(x.Ability["Defined"], x).Select(id => x.S[id]);
        foreach (var c in cards.Where(c => c.Zone == Zone.Battlefield && (x.Ability["Controller"] != "You" || c.Controller == x.Controller)).ToList())
        {
            x.Game.Sacrifice(c);
            if (x.Ability["RememberSacrificed"] == "True") (x.Host.Remembered ??= new()).Add(c.Id);
        }
    }
}

/// <summary>Investigate: create a Clue token ("{2}, sacrifice this: draw a card").</summary>
sealed class InvestigateEffect : Effect
{
    public override string Api => "Investigate";
    public override IReadOnlySet<string> Params { get; } = P("Num", "Defined");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        ValidateAmount(ab, "Num", ctx);
        Defined.ValidatePlayers(ab["Defined"], ctx);
        if (ctx.Tokens?.Invoke("c_a_clue_draw") is { } clue) ab.Tokens = new() { clue };
        else ctx.Need("TokenScript:c_a_clue_draw");
    }

    public override void Resolve(EffectContext x)
    {
        var n = Amount(x, x.Ability["Num"], 1);
        foreach (var p in Defined.Players(x.Ability["Defined"], x))
            foreach (var (def, count) in Replacements.Tokens(x.Game, x.Ability.Tokens![0], p, n).ToList())
                for (var i = 0; i < count; i++) x.Game.CreateToken(def, p, false, null, null);
    }
}

/// <summary>Exploit: "you may sacrifice a creature"; then "when this exploits a creature" triggers (rule 702.110).</summary>
sealed class ExploitEffect : Effect
{
    public override string Api => "Exploit";
    public override IReadOnlySet<string> Params { get; } = P();

    public override void Resolve(EffectContext x)
    {
        var candidates = x.S.BattlefieldCards.Where(c => c.Controller == x.Controller && c.Types.Is(CardType.Creature)).Select(c => c.Id).ToList();
        if (candidates.Count == 0) return;
        var chosen = x.Game.Decider.ChooseCards(x.Game, x.Controller, Choice.Sacrifice, x.Host, candidates, 0, 1);
        if (chosen.Count == 0) return;
        var victim = x.S[chosen[0]];
        x.Game.Note($"  exploit {victim.Name}");
        x.Game.Sacrifice(victim);
        x.Game.Fire(new TriggerEvent(TriggerMode.Exploited, CardId: victim.Id, TargetCardId: x.Host.Id, Player: x.Controller));
    }
}

/// <summary>"Change the target of target spell with a single target."</summary>
sealed class ChangeTargetsEffect : Effect
{
    public override string Api => "ChangeTargets";
    // "You may choose new targets": the old target is one of the choices.
    public override IReadOnlySet<string> Params { get; } = P("Optional");

    public override void Resolve(EffectContext x)
    {
        foreach (var t in x.Targets)
        {
            if (t.CardId is not { } id || x.S.Stack.LastOrDefault(i => i.IsSpell && i.SourceId == id) is not { } item) continue;
            var spell = x.S[id];
            foreach (var ab in item.Ability.Chain())
            {
                if (ab.TargetFilter is null || ab.ChainIndex >= item.ChainTargets.Count || item.ChainTargets[ab.ChainIndex].Count != 1) continue;
                var old = item.ChainTargets[ab.ChainIndex][0];
                var options = x.Game.TargetCandidates(spell, ab, item.Controller).Where(o => o != old || x.Ability["Optional"] == "True").ToList();
                if (options.Count == 0) continue;
                item.ChainTargets[ab.ChainIndex] = x.Game.Decider.ChooseTargets(x.Game, x.Controller, x.Host, ab, options, 1, 1);
                x.Game.Note($"  {spell.Name} now targets {string.Join(", ", item.ChainTargets[ab.ChainIndex])}");
            }
        }
    }
}

/// <summary>"Choose a card …": each defined player chooses cards from a zone; later effects use them as ChosenCard.</summary>
/// <summary>Foretell: exile this card from your hand face down; it can be cast on a later turn (rule 702.143).</summary>
sealed class ForetellEffect : Effect
{
    public override string Api => "Foretell";
    public override IReadOnlySet<string> Params { get; } = P();

    public override void Resolve(EffectContext x)
    {
        x.Game.MoveTo(x.Host, Zone.Exile);
        x.Host.ForetoldTurn = x.S.Turn;
        x.Game.Note($"  foretell {x.Host.Name}");
    }
}

/// <summary>"Choose a number from Min to Max" (Well of Lost Dreams: how much to pay and draw).</summary>
sealed class ChooseNumberEffect : Effect
{
    public override string Api => "ChooseNumber";
    public override IReadOnlySet<string> Params { get; } = P("Defined", "Min", "Max", "ListTitle");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        if (ab["Defined"] is { } d && d != "You") ctx.Need("ChooseNumber.Defined:" + d);
        ValidateAmount(ab, "Min", ctx);
        if (ab["Max"] is null) ctx.Need("ChooseNumber:NoMax");
        ValidateAmount(ab, "Max", ctx);
    }

    public override void Resolve(EffectContext x)
    {
        var min = Amount(x, x.Ability["Min"], 0);
        var max = Math.Max(min, Amount(x, x.Ability["Max"], 0));
        x.Host.ChosenNumber = Math.Max(min, x.Game.Decider.ChooseX(x.Game, x.Controller, x.Host, max));
        x.Game.Note($"  chose {x.Host.ChosenNumber}");
    }
}

/// <summary>"Prevent all combat damage that would be dealt this turn" (Fog).</summary>
sealed class FogEffect : Effect
{
    public override string Api => "Fog";
    public override IReadOnlySet<string> Params { get; } = P();
    public override void Resolve(EffectContext x) => x.S.ThisTurn.NoCombatDamage = true;
}

/// <summary>Deals the damage DamageMap$ held back; here it was already dealt (see DealDamage).</summary>
sealed class DamageResolveEffect : Effect
{
    public override string Api => "DamageResolve";
    public override IReadOnlySet<string> Params { get; } = P();
    public override void Resolve(EffectContext x) { }
}

/// <summary>"Choose a source": a card on the battlefield or the stack (prevention shields name it).</summary>
sealed class ChooseSourceEffect : Effect
{
    public override string Api => "ChooseSource";
    public override IReadOnlySet<string> Params { get; } = P("Choices", "Defined");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        if (ab["Choices"] is { } ch) Filter.Parse(ch, ctx); else ctx.Need("ChooseSource:NoChoices");
        if (ab["Defined"] is { } d && d != "You") ctx.Need("ChooseSource.Defined:" + d);
    }

    public override void Resolve(EffectContext x)
    {
        var filter = FilterFor(x.SVarFace, x.Ability["Choices"]!);
        var options = x.S.BattlefieldCards.Concat(x.Game.CardsIn(Zone.Stack)).Where(c => c.Id != x.Host.Id && filter.MatchesCard(c, x)).Select(c => c.Id).ToList();
        if (options.Count == 0) return;
        x.Host.ChosenCards = x.Game.Decider.ChooseCards(x.Game, x.Controller, Choice.Generic, x.Host, options, 1, 1);
    }
}

sealed class ChooseCardEffect : Effect
{
    public override string Api => "ChooseCard";
    public override IReadOnlySet<string> Params { get; } = P(
        "Choices", "Defined", "ChoiceZone", "Amount", "MinAmount", "Mandatory", "RememberChosen", "ImprintChosen", "AtRandom", "Reveal", "ChoiceDesc");

    private static readonly HashSet<string> Zones = new(StringComparer.Ordinal) { "Battlefield", "Hand", "Graveyard", "Exile", "Library" };

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        if (ab["Choices"] is { } ch) Filter.Parse(ch, ctx); else ctx.Need("ChooseCard:NoChoices");
        Defined.ValidatePlayers(ab["Defined"], ctx);
        if (ab["ChoiceZone"] is { } z && !z.Split(',').All(w => Zones.Contains(w.Trim()))) ctx.Need("ChooseCard.ChoiceZone:" + z);
        ValidateAmount(ab, "Amount", ctx);
        ValidateAmount(ab, "MinAmount", ctx);
    }

    public override void Resolve(EffectContext x)
    {
        var ab = x.Ability;
        var filter = FilterFor(x.SVarFace, ab["Choices"]!);
        var zones = (ab["ChoiceZone"] ?? "Battlefield").Split(',').Select(w => Enum.Parse<Zone>(w.Trim())).ToList();
        var candidates = zones.SelectMany(z => x.Game.CardsIn(z)).Where(c => filter.MatchesCard(c, x)).Select(c => c.Id).ToList();
        var n = Math.Min(Amount(x, ab["Amount"], 1), candidates.Count);
        var min = ab["MinAmount"] is { } mn ? Math.Min(Amount(x, mn, 0), n) : ab["Mandatory"] == "True" ? n : 0;
        x.Host.ChosenCards = new List<int>();
        foreach (var p in Defined.Players(ab["Defined"], x))
        {
            var left = candidates.Where(id => !x.Host.ChosenCards.Contains(id)).ToList();
            List<int> chosen;
            if (ab["AtRandom"] == "True") { x.S.Rng.Shuffle(left); chosen = left.Take(n).ToList(); }
            else if (x.S.Players[p].IsGoldfish) chosen = left.Take(min).ToList();
            else chosen = x.Game.Decider.ChooseCards(x.Game, p, Choice.Generic, x.Host, left, Math.Min(min, left.Count), Math.Min(n, left.Count));
            x.Host.ChosenCards.AddRange(chosen);
        }
        if (ab["RememberChosen"] == "True") (x.Host.Remembered ??= new()).AddRange(x.Host.ChosenCards);
        if (ab["ImprintChosen"] == "True") (x.Host.Imprinted ??= new()).AddRange(x.Host.ChosenCards);
        foreach (var id in x.Host.ChosenCards) x.Game.Note($"  chose {x.S[id].Name}");
    }
}

/// <summary>
/// Manifest (rule 701.40): the top card of a library goes onto the battlefield face down as a 2/2 creature. A goldfish
/// opponent's library is a count, so they get a face-down 2/2 token for the card.
/// </summary>
sealed class ManifestEffect : Effect
{
    public override string Api => "Manifest";
    public override IReadOnlySet<string> Params { get; } = P("DefinedPlayer", "Amount");

    private static readonly CardDef FaceDown =
        CardCompiler.Compile(Forge.ForgeCard.Parse("Name:Face-down creature\nTypes:Creature\nPT:2/2\n"));

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        ValidateAmount(ab, "Amount", ctx);
        Defined.ValidatePlayers(ab["DefinedPlayer"], ctx);
    }

    public override void Resolve(EffectContext x)
    {
        var n = Amount(x, x.Ability["Amount"], 1);
        foreach (var p in Defined.Players(x.Ability["DefinedPlayer"], x).Select(i => x.S.Players[i]))
            for (var i = 0; i < n; i++)
            {
                if (p.IsGoldfish)
                {
                    if (p.AbstractLibrary == 0) break;
                    p.AbstractLibrary--;
                    x.Game.CreateToken(FaceDown, p.Index, false);
                    continue;
                }
                if (p.Library.Count == 0) break;
                var c = x.S[p.Library[0]];
                c.FaceDown = true;
                x.Game.Note($"  manifest {c.Def.Name}");
                x.Game.MoveTo(c, Zone.Battlefield);
            }
    }
}

/// <summary>Turns a face-down permanent face up (morph, megamorph: rule 702.37e).</summary>
sealed class TurnFaceUpEffect : Effect
{
    public override string Api => "TurnFaceUp";
    public override IReadOnlySet<string> Params { get; } = P("Megamorph");

    public override void Resolve(EffectContext x)
    {
        var c = x.Host;
        if (!c.FaceDown) return;
        c.FaceDown = false;
        x.Game.Note($"  turn {c.Name} face up");
        if (x.Ability["Megamorph"] == "True") x.Game.PutCounters(c, "P1P1", 1);
        x.Game.StaticsApplied = true;
        x.Game.Fire(new TriggerEvent(TriggerMode.TurnFaceUp, CardId: c.Id, Player: c.Controller));
    }
}

sealed class DamageAllEffect : Effect
{
    public override string Api => "DamageAll";
    public override IReadOnlySet<string> Params { get; } = P("NumDmg", "ValidCards", "ValidPlayers");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        ValidateAmount(ab, "NumDmg", ctx);
        if (ab["ValidCards"] is { } vc) Filter.Parse(vc, ctx);
        if (ab["ValidPlayers"] is { } vp) Filter.Parse(vp, ctx);
    }

    public override void Resolve(EffectContext x)
    {
        var n = Amount(x, x.Ability["NumDmg"], 0);
        if (x.Ability["ValidCards"] is { } vc)
        {
            var filter = FilterFor(x.SVarFace, vc);
            foreach (var c in x.S.BattlefieldCards.Where(c => filter.MatchesCard(c, x)).ToList()) x.Game.DamageCard(c, n, x.Host);
        }
        if (x.Ability["ValidPlayers"] is { } vp)
        {
            var filter = FilterFor(x.SVarFace, vp);
            foreach (var p in x.S.Players.Where(p => !p.Lost && filter.MatchesPlayer(p.Index, x)).ToList())
                x.Game.DamagePlayer(p.Index, n, x.Host);
        }
    }
}

sealed class PumpAllEffect : Effect
{
    public override string Api => "PumpAll";
    public override IReadOnlySet<string> Params { get; } = P("ValidCards", "NumAtt", "NumDef", "KW");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        Filter.Parse(ab["ValidCards"] ?? "Creature", ctx);
        ValidateAmount(ab, "NumAtt", ctx);
        ValidateAmount(ab, "NumDef", ctx);
        PumpEffect.ValidateKeywords(ab["KW"], ctx);
    }

    public override void Resolve(EffectContext x)
    {
        var filter = FilterFor(x.SVarFace, x.Ability["ValidCards"] ?? "Creature");
        var att = Amount(x, x.Ability["NumAtt"], 0);
        var def = Amount(x, x.Ability["NumDef"], 0);
        foreach (var c in x.S.BattlefieldCards.Where(c => filter.MatchesCard(c, x)).ToList())
            PumpEffect.Apply(c, att, def, x.Ability["KW"]);
    }
}

sealed class PutCounterAllEffect : Effect
{
    public override string Api => "PutCounterAll";
    public override IReadOnlySet<string> Params { get; } = P("ValidCards", "CounterType", "CounterNum");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        Filter.Parse(ab["ValidCards"] ?? "Card", ctx);
        ValidateAmount(ab, "CounterNum", ctx);
        if (ab["CounterType"] is null) ctx.Need("PutCounterAll.CounterType:none");
    }

    public override void Resolve(EffectContext x)
    {
        var filter = FilterFor(x.SVarFace, x.Ability["ValidCards"] ?? "Card");
        var n = Amount(x, x.Ability["CounterNum"], 1);
        var kind = x.Ability["CounterType"]!;
        foreach (var c in x.S.BattlefieldCards.Where(c => filter.MatchesCard(c, x)).ToList()) x.Game.PutCounters(c, kind, n);
    }
}

// ---- the stack ------------------------------------------------------------------------

/// <summary>Counters target spell. Only spells (not abilities) can be targeted for now.</summary>
sealed class CounterEffect : Effect
{
    public override string Api => "Counter";
    public override IReadOnlySet<string> Params { get; } = P("Destination", "RememberTargets", "RememberCounteredCMC");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        if (ab["TargetType"] is { } tt && tt != "Spell") ctx.Need("Counter.TargetType:" + tt);
        if (ab.TargetFilter is null) ctx.Need("Counter:NoTarget");
        if (ab["Destination"] is { } d && d is not ("Graveyard" or "Exile" or "Hand")) ctx.Need("Counter.Destination:" + d);
    }

    public override void Resolve(EffectContext x)
    {
        var dest = x.Ability["Destination"] is { } d ? Enum.Parse<Zone>(d) : Zone.Graveyard;
        foreach (var id in Defined.Cards("Targeted", x))
        {
            var item = x.S.Stack.FirstOrDefault(s => s.IsSpell && s.SourceId == id);
            if (item is null) continue;
            if (x.Ability["RememberTargets"] == "True") (x.Host.Remembered ??= new()).Add(id);
            if (x.Ability["RememberCounteredCMC"] == "True")
            {
                x.Host.StoredVars ??= new();
                x.Host.StoredVars[Card.RememberedNumberKey] = x.Host.StoredVars.GetValueOrDefault(Card.RememberedNumberKey) + x.S[id].ManaValue;
            }
            x.S.Stack.Remove(item);
            x.Game.Note($"  countered {x.S[id].Name}");
            x.Game.MoveTo(x.S[id], item.ExileAfter ? Zone.Exile : dest);
            x.Game.Fire(new TriggerEvent(TriggerMode.Countered, CardId: id, Player: x.Controller));
        }
    }
}

// ---- library ------------------------------------------------------------------------

sealed class SurveilEffect : Effect
{
    public override string Api => "Surveil";
    public override IReadOnlySet<string> Params { get; } = P("Amount");
    public override void Validate(AbilityDef ab, CompileContext ctx) => ValidateAmount(ab, "Amount", ctx);

    public override void Resolve(EffectContext x)
    {
        var n = Amount(x, x.Ability["Amount"], 1);
        var p = x.S.Players[x.Controller];
        var top = p.Library.Take(n).ToList();
        if (top.Count == 0) return;
        p.KnownTop = Math.Max(p.KnownTop, top.Count);   // looked at; cards moved away stop counting
        var toGrave = x.Game.Decider.ChooseCards(x.Game, x.Controller, Choice.PutOnBottom, x.Host, top, 0, top.Count);
        foreach (var id in toGrave) x.Game.MoveTo(x.S[id], Zone.Graveyard);
        x.Game.Note($"  surveil {n}: graveyard {toGrave.Count}");
    }
}

/// <summary>Look at the top N and put them back in any order (Ponder, Sensei's Divining Top); optionally shuffle.</summary>
sealed class RearrangeTopOfLibraryEffect : Effect
{
    public override string Api => "RearrangeTopOfLibrary";
    public override IReadOnlySet<string> Params { get; } = P("NumCards", "Defined", "MayShuffle");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        ValidateAmount(ab, "NumCards", ctx);
        if (ab["Defined"] is { } d && d != "You") ctx.Need("RearrangeTopOfLibrary.Defined:" + d);
        if (ab.TargetFilter is not null) ctx.Need("RearrangeTopOfLibrary:Targeted");
    }

    public override void Resolve(EffectContext x)
    {
        var p = x.S.Players[x.Controller];
        var n = Math.Min(Amount(x, x.Ability["NumCards"], 1), p.Library.Count);
        if (n == 0) return;
        p.KnownTop = Math.Max(p.KnownTop, n);   // looked at: search may use them from here on
        var top = p.Library.Take(n).ToList();
        var order = x.Game.Decider.ChooseCards(x.Game, x.Controller, Choice.KeepOnTop, x.Host, top, top.Count, top.Count);
        for (var i = 0; i < order.Count; i++) p.Library[i] = order[i];
        x.Game.Note("  top: " + string.Join(", ", order.Select(id => x.S[id].Name)));
        if (x.Ability["MayShuffle"] == "True" && x.Game.Decider.ChooseYesNo(x.Game, x.Controller, x.Host, "shuffle?"))
        {
            x.Game.Shuffle(x.Controller);
            x.Game.Note("  shuffled");
        }
    }
}

// ---- regeneration ---------------------------------------------------------------------

sealed class RegenerateEffect : CardsEffect
{
    public override string Api => "Regenerate";
    public override IReadOnlySet<string> Params { get; } = P("Defined");
    public override void Resolve(EffectContext x) { foreach (var c in Cards(x)) c.RegenShields++; }
}

// ---- chance, loops and variables ----------------------------------------------------------

/// <summary>Flips a coin (the controller always calls): runs WinSubAbility$ or LoseSubAbility$.</summary>
sealed class FlipCoinEffect : Effect
{
    public override string Api => "FlipCoin";
    public override IReadOnlySet<string> Params { get; } = P("WinSubAbility", "LoseSubAbility", "NoCall", "Defined");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        CardCompiler.CompileSideChain(ab, "WinSubAbility", ctx);
        CardCompiler.CompileSideChain(ab, "LoseSubAbility", ctx);
        if (ab["Defined"] is { } d && d != "You") ctx.Need("FlipCoin.Defined:" + d);
    }

    public override void Resolve(EffectContext x)
    {
        var won = x.S.Rng.Next(2) == 0;
        x.Game.Note($"  coin flip: {(won ? "won" : "lost")}");
        if (x.Ability.SideChains?.GetValueOrDefault(won ? "WinSubAbility" : "LoseSubAbility") is { } chain) x.Game.RunSideChain(x, chain);
    }
}

/// <summary>
/// Runs RepeatSubAbility$ again and again: up to MaxRepeat$ times, and while RepeatCheckSVar$ /
/// RepeatPresent$ hold, or while the player chooses to go on (RepeatOptional$).
/// </summary>
sealed class RepeatEffect : Effect
{
    public override string Api => "Repeat";
    public override IReadOnlySet<string> Params { get; } = P(
        "RepeatSubAbility", "RepeatOptional", "RepeatCheckSVar", "RepeatSVarCompare", "RepeatDefined", "RepeatPresent", "RepeatCompare", "MaxRepeat");

    /// <summary>Safety limit for loops whose stop condition never comes.</summary>
    private const int Limit = 200;

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        if (CardCompiler.CompileSideChain(ab, "RepeatSubAbility", ctx) is null) ctx.Need("Repeat:NoSubAbility");
        ValidateAmount(ab, "MaxRepeat", ctx);
        ValidateAmount(ab, "RepeatCheckSVar", ctx);
        if (ab["RepeatPresent"] is { } rp) Filter.Parse(rp, ctx);
        if (ab["RepeatDefined"] is { } rd && rd is not ("Remembered" or "Imprinted")) ctx.Need("RepeatDefined:" + rd);
    }

    public override void Resolve(EffectContext x)
    {
        var ab = x.Ability;
        var chain = ab.SideChains!["RepeatSubAbility"];
        var max = ab["MaxRepeat"] is { } m ? Math.Min(Amount(x, m, 0), Limit) : Limit;
        for (var count = 0; count < max && !x.S.GameOver; )
        {
            x.Game.RunSideChain(x, chain);
            count++;
            if (count >= max || !KeepGoing(x)) break;
        }
    }

    private static bool KeepGoing(EffectContext x)
    {
        var ab = x.Ability;
        if (ab["RepeatCheckSVar"] is { } sv)
        {
            var cmp = ab["RepeatSVarCompare"] ?? "GE1";
            if (!Filter.Compare(Amount(x, sv, 0), cmp[..2], Amount(x, cmp[2..], 0))) return false;
        }
        if (ab["RepeatPresent"] is { } rp)
        {
            var filter = FilterFor(x.SVarFace, rp);
            var pool = ab["RepeatDefined"] is { } rd ? Defined.Cards(rd, x).Select(id => x.S[id]) : x.Game.CardsIn(Zone.Battlefield);
            var cmp = ab["RepeatCompare"] ?? "GE1";
            if (!Filter.Compare(pool.Count(c => filter.MatchesCard(c, x)), cmp[..2], Amount(x, cmp[2..], 0))) return false;
        }
        if (ab["RepeatOptional"] == "True")
            return x.Game.Decider.ChooseYesNo(x.Game, x.Controller, x.Host, "repeat?");
        return true;
    }
}

/// <summary>Sets one of the card's SVars to a number for later conditions (StoreSVar$).</summary>
sealed class StoreSVarEffect : Effect
{
    public override string Api => "StoreSVar";
    public override IReadOnlySet<string> Params { get; } = P("SVar", "Type", "Expression");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        if (ab["SVar"] is null) ctx.Need("StoreSVar:NoSVar");
        switch (ab["Type"])
        {
            case "Number" when int.TryParse(ab["Expression"], out _): break;
            case "CountSVar": Engine.Amount.Parse(ab["Expression"] ?? "0", ctx); break;
            default: ctx.Need("StoreSVar.Type:" + ab["Type"]); break;
        }
    }

    public override void Resolve(EffectContext x)
    {
        var value = Amount(x, x.Ability["Expression"], 0);
        (x.Host.StoredVars ??= new())[x.Ability["SVar"]!] = value;
    }
}

// ---- effects and choices ----------------------------------------------------------------

/// <summary>
/// Creates an effect object in the command zone carrying static abilities, triggers and
/// replacements for a while (Duration$): "until end of turn, you may play that card",
/// emblems, "the next time …". It remembers RememberObjects$ for its abilities to refer to.
/// </summary>
sealed class EffectEffect : Effect
{
    public override string Api => "Effect";
    public override IReadOnlySet<string> Params { get; } = P(
        "Name", "Image", "StaticAbilities", "Triggers", "ReplacementEffects", "SVars", "RememberObjects", "ImprintCards",
        "Duration", "ExileOnMoved", "ForgetOnMoved", "EffectOwner", "Stackable");

    private static readonly HashSet<string> Durations = new(StringComparer.Ordinal)
    {
        "Permanent", "EndOfTurn", "UntilYourNextTurn", "UntilTheEndOfYourNextTurn", "UntilHostLeavesPlay", "UntilHostLeavesPlayOrEOT", "UntilEndOfCombat",
    };

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        if (ab["Duration"] is { } d && !Durations.Contains(d)) ctx.Need("Effect.Duration:" + d);
        // "Exile target creature an opponent controls until an opponent becomes the monarch": the effect is its owner's.
        if (ab["EffectOwner"] is { } o && o is not ("You" or "TargetedOwner")) ctx.Need("Effect.EffectOwner:" + o);
        Defined.ValidateCards(ab["RememberObjects"], ctx);
        Defined.ValidateCards(ab["ImprintCards"], ctx);
        foreach (var key in new[] { "ExileOnMoved", "ForgetOnMoved" })
            if (ab[key] is { } z && z is not ("Battlefield" or "Exile" or "Graveyard" or "Hand" or "Stack" or "Library")) ctx.Need($"Effect.{key}:{z}");

        var script = new System.Text.StringBuilder();
        script.Append($"Name:{ab["Name"] ?? ctx.Face.Name + "'s effect"}\nManaCost:no cost\nTypes:Effect\n");
        foreach (var (key, prefix) in new[] { ("StaticAbilities", "S:"), ("Triggers", "T:"), ("ReplacementEffects", "R:") })
            foreach (var name in (ab[key] ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!ctx.Face.SVars.TryGetValue(name, out var body)) { ctx.Need("SVarMissing:" + name); continue; }
                script.Append(prefix).Append(body).Append('\n');
            }
        foreach (var (k, v) in ctx.Face.SVars) script.Append("SVar:").Append(k).Append(':').Append(v).Append('\n');
        var def = CardCompiler.Compile(Forge.ForgeCard.Parse(script.ToString()), ctx.Tokens);
        foreach (var m in def.Missing) ctx.Need(m);
        ab.EffectDef = def;
    }

    public override void Resolve(EffectContext x)
    {
        var ab = x.Ability;
        var owner = ab["EffectOwner"] == "TargetedOwner" && Defined.Players("TargetedOwner", x) is [var to, ..] ? to : x.Controller;
        var e = x.Game.CreateEffect(ab.EffectDef!, owner, x.Host, ab["Duration"], ab["ExileOnMoved"], ab["ForgetOnMoved"]);
        if (ab["RememberObjects"] is { } ro)
        {
            e.Remembered = Defined.Cards(ro, x);
            var players = ro.Split(" & ").Where(Defined.IsPlayerRef).SelectMany(part => Defined.Players(part, x)).Distinct().ToList();
            if (players.Count > 0) e.RememberedPlayers = players;
        }
        if (ab["ImprintCards"] is { } ic) e.Imprinted = Defined.Cards(ic, x);
        e.ChosenColor = x.Host.ChosenColor;
        e.ChosenType = x.Host.ChosenType;
        e.ChosenPlayer = x.Host.ChosenPlayer;
    }
}

sealed class ChooseColorEffect : Effect
{
    public override string Api => "ChooseColor";
    public override IReadOnlySet<string> Params { get; } = P("Defined");
    public override void Validate(AbilityDef ab, CompileContext ctx) { if (ab["Defined"] is { } d && d != "You") ctx.Need("ChooseColor.Defined:" + d); }

    public override void Resolve(EffectContext x)
    {
        x.Host.ChosenColor = x.Game.Decider.ChooseColor(x.Game, x.Controller, x.Host, ColorMask.AllColors);
        x.Game.Note($"  chose {x.Host.ChosenColor}");
    }
}

/// <summary>Choose a creature type (offered: the types in your own deck, most common first) or a basic land type.</summary>
sealed class ChooseTypeEffect : Effect
{
    public override string Api => "ChooseType";
    public override IReadOnlySet<string> Params { get; } = P("Defined", "Type", "ValidTypes");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        // "An opponent chooses a creature type" (Callous Oppressor): the chosen player.
        if (ab["Defined"] is { } d && d is not ("You" or "Player.Chosen")) ctx.Need("ChooseType.Defined:" + d);
        if (ab["Type"] is not ("Creature" or "Basic Land" or "Card")) ctx.Need("ChooseType.Type:" + ab["Type"]);
        if (ab["Type"] == "Card" && ab["ValidTypes"] is null) ctx.Need("ChooseType.Card:AnyType");
    }

    public override void Resolve(EffectContext x)
    {
        var chooser = x.Ability["Defined"] == "Player.Chosen" && x.Host.ChosenPlayer is { } chosen ? chosen : x.Controller;
        List<string> options;
        if (x.Ability["Type"] == "Basic Land") options = TypeLine.BasicLandTypes.ToList();
        else if (x.Ability["Type"] == "Card") options = x.Ability["ValidTypes"]!.Split(',', StringSplitOptions.TrimEntries).ToList();
        else
            // Your own deck's creature types; another player names those of the creatures it controls.
            options = (chooser == x.Controller ? x.S.Cards.Where(c => c.Owner == chooser) : x.S.BattlefieldCards.Where(c => c.Controller == chooser))
                .Where(c => c.Types.Is(CardType.Creature) && !c.IsEffect)
                .SelectMany(c => c.Types.Subtypes).GroupBy(t => t).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Key).Take(6).DefaultIfEmpty("Human").ToList();
        // Another player names the type that protects the most of their creatures.
        x.Host.ChosenType = chooser == x.Controller ? options[x.Game.Decider.ChooseOption(x.Game, x.Controller, x.Host, options)] : options[0];
        x.Game.Note($"  chose {x.Host.ChosenType}");
    }
}

/// <summary>Choose a player (usually an opponent). Goldfish opponents are interchangeable, so the choice barely matters.</summary>
sealed class ChoosePlayerEffect : Effect
{
    public override string Api => "ChoosePlayer";
    public override IReadOnlySet<string> Params { get; } = P("Defined", "Choices");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        if (ab["Defined"] is { } d && d != "You") ctx.Need("ChoosePlayer.Defined:" + d);
        Filter.Parse(ab["Choices"] ?? "Player", ctx);
    }

    public override void Resolve(EffectContext x)
    {
        var filter = FilterFor(x.SVarFace, x.Ability["Choices"] ?? "Player");
        var players = x.S.Players.Where(p => !p.Lost && filter.MatchesPlayer(p.Index, x)).Select(p => p.Index).ToList();
        if (players.Count == 0) return;
        var labels = players.Select(p => $"player {p}").ToList();
        x.Host.ChosenPlayer = players[x.Game.Decider.ChooseOption(x.Game, x.Controller, x.Host, labels)];
    }
}

/// <summary>"Spend this mana only to …" (RestrictValid$): which payments a restricted mana ability may be used for.</summary>
public static class ManaRestriction
{
    public static void Validate(string restriction, CompileContext ctx)
    {
        foreach (var term in restriction.Split(',', StringSplitOptions.TrimEntries))
        {
            var dot = term.IndexOf('.');
            var kind = dot < 0 ? term : term[..dot];
            if (kind is not ("Spell" or "Activated")) { ctx.Need("RestrictValid:" + term); continue; }
            if (dot >= 0) Filter.Parse("Card." + term[(dot + 1)..], ctx);
        }
    }

    public static bool Allows(Game g, Card manaSource, string restriction, Card? payingFor, bool forSpell)
    {
        if (payingFor is null) return false;
        foreach (var term in restriction.Split(',', StringSplitOptions.TrimEntries))
        {
            var dot = term.IndexOf('.');
            var kind = dot < 0 ? term : term[..dot];
            if ((kind == "Spell") != forSpell) continue;
            if (dot < 0 || Effect.FilterFor(manaSource.CurrentFace, "Card." + term[(dot + 1)..]).MatchesCard(payingFor, g.ContextFor(manaSource)))
                return true;
        }
        return false;
    }
}

// ---- counters ------------------------------------------------------------------------

/// <summary>
/// Proliferate: each permanent and player with counters gets another of each kind. The engine
/// picks for the player: your permanents (except harmful counters), opponents' poison counters.
/// </summary>
sealed class ProliferateEffect : Effect
{
    public override string Api => "Proliferate";
    public override IReadOnlySet<string> Params { get; } = P("Amount");

    private static readonly HashSet<string> Harmful = new(StringComparer.Ordinal) { "M1M1", "AGE", "DOOM", "DEPLETION", "TIME" };

    public override void Validate(AbilityDef ab, CompileContext ctx) => ValidateAmount(ab, "Amount", ctx);

    public override void Resolve(EffectContext x)
    {
        for (var i = Amount(x, x.Ability["Amount"], 1); i > 0; i--)
        {
            foreach (var c in x.S.BattlefieldCards.Where(c => c.Counters is not null).ToList())
                foreach (var kind in c.Counters!.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList())
                    if (c.Controller == x.Controller ? !Harmful.Contains(kind) : kind == "M1M1")
                        x.Game.PutCounters(c, kind, 1);
            foreach (var p in x.S.Players)
                if (p.Index != x.Controller && !p.Lost && p.Poison > 0) p.Poison++;
            x.Game.Note("  proliferate");
        }
    }
}

/// <summary>"Tap all untapped permanents [of a kind] target player controls, or untap all tapped ones" (Turnabout).</summary>
sealed class TapOrUntapAllEffect : Effect
{
    public override string Api => "TapOrUntapAll";
    public override IReadOnlySet<string> Params { get; } = P("ValidCards", "Defined");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        if (ab["ValidCards"] is { } vc) Filter.Parse(vc, ctx); else ctx.Need("TapOrUntapAll:NoValidCards");
        Defined.ValidatePlayers(ab["Defined"], ctx);
    }

    public override void Resolve(EffectContext x)
    {
        var filter = FilterFor(x.SVarFace, x.Ability["ValidCards"]!);
        var players = x.Ability.TargetFilter is not null ? Defined.Players("Targeted", x) : Defined.Players(x.Ability["Defined"], x, "Player");
        var cards = x.S.BattlefieldCards.Where(c => players.Contains(c.Controller) && filter.MatchesCard(c, x)).ToList();
        var untap = x.Game.Decider.ChooseOption(x.Game, x.Controller, x.Host, new[] { "untap", "tap" }) == 0;
        foreach (var c in cards) c.Tapped = !untap;
        x.Game.Note($"  {(untap ? "untap" : "tap")} {cards.Count}");
    }
}

/// <summary>"Look at the top N cards of your library" (PeekAndReveal with NoReveal$): known to you, optionally remembered.</summary>
/// <summary>
/// Reveal cards from a hand: some or any number of those matching RevealValid$, all of them (RevealAllValid$), given
/// ones (RevealDefined$) or random ones. Revealing only matters for what's remembered: nobody here hides information.
/// </summary>
sealed class RevealEffect : Effect
{
    public override string Api => "Reveal";
    public override IReadOnlySet<string> Params { get; } = P(
        "Defined", "RevealValid", "AnyNumber", "RememberRevealed", "Random", "NumCards", "RevealAllValid", "RevealDefined", "Optional",
        "RevealToAll", "RevealTitle");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        Defined.ValidatePlayers(ab["Defined"], ctx);
        foreach (var key in new[] { "RevealValid", "RevealAllValid" })
            if (ab[key] is { } f) Filter.Parse(f, ctx);
        Defined.ValidateCards(ab["RevealDefined"], ctx);
        ValidateAmount(ab, "NumCards", ctx);
    }

    public override void Resolve(EffectContext x)
    {
        var ab = x.Ability;
        var revealed = new List<int>();
        var unknown = 0;
        if (ab["RevealDefined"] is { } rd) revealed.AddRange(Defined.Cards(rd, x));
        else
            foreach (var p in DrawEffect.Players(x).Select(i => x.S.Players[i]))
            {
                var n = Amount(x, ab["NumCards"], 1);
                if (p.IsGoldfish) { unknown += ab["Random"] == "True" ? Math.Min(n, p.AbstractHand) : 0; continue; }
                var valid = ab["RevealAllValid"] ?? ab["RevealValid"];
                var filter = valid is null ? null : FilterFor(x.SVarFace, valid);
                var hand = p.Hand.Where(id => filter is null || filter.MatchesCard(x.S[id], x)).ToList();
                if (ab["Random"] == "True")
                {
                    x.S.Rng.Shuffle(hand);
                    revealed.AddRange(hand.Take(n));
                }
                // Any number: all of them (revealing costs nothing, and the effects reward more).
                else if (ab["RevealAllValid"] is not null || ab["AnyNumber"] == "True") revealed.AddRange(hand);
                else if (hand.Count > 0 && !(ab["Optional"] == "True" && !x.Game.Decider.ChooseYesNo(x.Game, p.Index, x.Host, "reveal a card?")))
                    revealed.AddRange(x.Game.Decider.ChooseCards(x.Game, p.Index, Choice.Generic, x.Host, hand, Math.Min(n, hand.Count), Math.Min(n, hand.Count)));
            }
        if (revealed.Count > 0) x.Game.Note("  reveal " + string.Join(", ", revealed.Select(id => x.S[id].Name)));
        if (ab["RememberRevealed"] == "True")
        {
            (x.Host.Remembered ??= new()).AddRange(revealed);
            x.Host.RememberedUnknown += unknown;
        }
    }
}

sealed class PeekAndRevealEffect : Effect
{
    public override string Api => "PeekAndReveal";
    public override IReadOnlySet<string> Params { get; } = P("PeekAmount", "NoReveal", "RememberPeeked", "Defined", "RevealValid", "RememberRevealed",
        // "Reveal" without looking first, or "you may reveal it": the same to a goldfish.
        "NoPeek", "RevealOptional");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        ValidateAmount(ab, "PeekAmount", ctx);
        if (ab["Defined"] is { } d && d != "You") ctx.Need("PeekAndReveal.Defined:" + d);
        if (ab["RevealValid"] is { } rv) Filter.Parse(rv, ctx);
    }

    public override void Resolve(EffectContext x)
    {
        var p = x.S.Players[x.Controller];
        var top = p.Library.Take(Amount(x, x.Ability["PeekAmount"], 1)).ToList();
        p.KnownTop = Math.Max(p.KnownTop, top.Count);
        // Revealing to goldfish opponents changes nothing; what matters is what's remembered.
        var filter = x.Ability["RevealValid"] is { } rv ? FilterFor(x.SVarFace, rv) : null;
        if (x.Ability["RememberPeeked"] == "True") (x.Host.Remembered ??= new()).AddRange(top);
        if (x.Ability["RememberRevealed"] == "True") (x.Host.Remembered ??= new()).AddRange(top.Where(id => filter is null || filter.MatchesCard(x.S[id], x)));
    }
}
