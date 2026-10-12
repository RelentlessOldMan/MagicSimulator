namespace MagicSim.Core.Engine;

// ---- modal spells ----------------------------------------------------------------------

/// <summary>
/// "Choose one —": the compiler expands each allowed combination of modes into its own ability
/// chain headed by this effect, so resolving the head itself does nothing.
/// </summary>
sealed class CharmEffect : Effect
{
    public override string Api => "Charm";
    public override IReadOnlySet<string> Params { get; } = P("Choices", "CharmNum", "MinCharmNum", "AdditionalDescription");
    public override void Resolve(EffectContext x) { }
}

// ---- tokens and attachments --------------------------------------------------------------

sealed class TokenEffect : Effect
{
    public override string Api => "Token";
    public override IReadOnlySet<string> Params { get; } = P(
        "TokenScript", "TokenOwner", "TokenAmount", "TokenTapped", "RememberTokens", "TokenPower", "TokenToughness",
        "TokenAttacking", "PumpKeywords", "PumpDuration", "AtEOT");

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
        "UseAllOriginZones");

    private static readonly HashSet<string> Zones = new() { "Library", "Hand", "Battlefield", "Graveyard", "Exile" };

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        foreach (var key in new[] { "Origin", "Destination" })
            if (ab[key] is not { } z || !z.Split(',').All(s => Zones.Contains(s.Trim()))) ctx.Need($"ChangeZoneAll.{key}:{ab[key]}");
        if (ab["ChangeType"] is { } ct) Filter.Parse(ct, ctx);
        Defined.ValidatePlayers(ab["Defined"], ctx);
    }

    public override void Resolve(EffectContext x)
    {
        var ab = x.Ability;
        var origins = ab["Origin"]!.Split(',').Select(s => Enum.Parse<Zone>(s.Trim())).ToList();
        var dest = Enum.Parse<Zone>(ab["Destination"]!);
        var filter = ab["ChangeType"] is { } ct ? FilterFor(x.SVarFace, ct) : null;
        // Whose zones: Defined$ or targeted players, else everyone's.
        var players = ab["Defined"] is { } d ? Defined.Players(d, x)
            : ab.TargetFilter is not null ? Defined.Players("Targeted", x)
            : x.S.Players.Select(p => p.Index).ToList();
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
    public override IReadOnlySet<string> Params { get; } = P("ValidCards", "NoRegen", "RememberDestroyed");
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
    public override IReadOnlySet<string> Params { get; } = P("Destination");

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
            x.S.Stack.Remove(item);
            x.Game.Note($"  countered {x.S[id].Name}");
            x.Game.MoveTo(x.S[id], item.ExileAfter ? Zone.Exile : dest);
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
        "Permanent", "EndOfTurn", "UntilYourNextTurn", "UntilTheEndOfYourNextTurn", "UntilHostLeavesPlay", "UntilHostLeavesPlayOrEOT",
    };

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        if (ab["Duration"] is { } d && !Durations.Contains(d)) ctx.Need("Effect.Duration:" + d);
        if (ab["EffectOwner"] is { } o && o != "You") ctx.Need("Effect.EffectOwner:" + o);
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
        var e = x.Game.CreateEffect(ab.EffectDef!, x.Controller, x.Host, ab["Duration"], ab["ExileOnMoved"], ab["ForgetOnMoved"]);
        if (ab["RememberObjects"] is { } ro) e.Remembered = Defined.Cards(ro, x);
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
        if (ab["Defined"] is { } d && d != "You") ctx.Need("ChooseType.Defined:" + d);
        if (ab["Type"] is not ("Creature" or "Basic Land" or "Card")) ctx.Need("ChooseType.Type:" + ab["Type"]);
        if (ab["Type"] == "Card" && ab["ValidTypes"] is null) ctx.Need("ChooseType.Card:AnyType");
    }

    public override void Resolve(EffectContext x)
    {
        List<string> options;
        if (x.Ability["Type"] == "Basic Land") options = TypeLine.BasicLandTypes.ToList();
        else if (x.Ability["Type"] == "Card") options = x.Ability["ValidTypes"]!.Split(',', StringSplitOptions.TrimEntries).ToList();
        else
            options = x.S.Cards.Where(c => c.Owner == x.Controller && c.Types.Is(CardType.Creature) && !c.IsEffect)
                .SelectMany(c => c.Types.Subtypes).GroupBy(t => t).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Key).Take(6).DefaultIfEmpty("Human").ToList();
        x.Host.ChosenType = options[x.Game.Decider.ChooseOption(x.Game, x.Controller, x.Host, options)];
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
sealed class PeekAndRevealEffect : Effect
{
    public override string Api => "PeekAndReveal";
    public override IReadOnlySet<string> Params { get; } = P("PeekAmount", "NoReveal", "RememberPeeked", "Defined", "RevealValid", "RememberRevealed");

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
