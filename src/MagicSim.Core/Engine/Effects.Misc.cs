namespace MagicSim.Core.Engine;

/// <summary>"Creatures you control become …": Animate applied to every card matching ValidCards$.</summary>
sealed class AnimateAllEffect : Effect
{
    public override string Api => "AnimateAll";
    public override IReadOnlySet<string> Params { get; } = P(
        "ValidCards", "Types", "Power", "Toughness", "Keywords", "Colors", "OverwriteColors", "RemoveCardTypes", "RemoveCreatureTypes",
        "RemoveSuperTypes", "RemoveAllAbilities", "Abilities", "Triggers", "staticAbilities", "Duration", "sVars", "AddAllCreatureTypes");

    private static readonly AnimateEffect Single = new();

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        Filter.Parse(ab["ValidCards"] ?? "Card", ctx);
        // The same changes as Animate, on cards chosen by a filter instead of Defined$.
        var probe = new AbilityDef { P = new Forge.ForgeParams(ab.P.Raw + " | Defined$ Self"), Api = "Animate", Kind = ab.Kind, Face = ab.Face };
        Single.Validate(probe, ctx);
        ab.Grants = probe.Grants;
    }

    public override void Resolve(EffectContext x)
    {
        var filter = FilterFor(x.SVarFace, x.Ability["ValidCards"] ?? "Card");
        var cards = x.S.BattlefieldCards.Where(c => filter.MatchesCard(c, x)).ToList();
        AnimateEffect.AnimateCards(x, cards);
    }
}

/// <summary>Two creatures fight: each deals damage equal to its power to the other (rule 701.12).</summary>
sealed class FightEffect : Effect
{
    public override string Api => "Fight";
    public override IReadOnlySet<string> Params { get; } = P("Defined");

    public override void Validate(AbilityDef ab, CompileContext ctx) => Defined.ValidateCards(ab["Defined"], ctx);

    public override void Resolve(EffectContext x)
    {
        var fighters = (x.Ability["Defined"] is { } d ? Defined.Cards(d, x) : new List<int>())
            .Concat(Defined.Cards("Targeted", x)).Distinct().Select(id => x.S[id]).Where(c => c.Zone == Zone.Battlefield && c.Types.Is(CardType.Creature)).ToList();
        if (fighters.Count != 2) return;
        var (a, b) = (fighters[0], fighters[1]);
        int pa = a.Power, pb = b.Power;
        x.Game.DamageCard(b, pa, a);
        x.Game.DamageCard(a, pb, b);
    }
}

/// <summary>"If …, [this]; otherwise [that]": runs TrueSubAbility$ or FalseSubAbility$.</summary>
sealed class BranchEffect : Effect
{
    public override string Api => "Branch";
    public override IReadOnlySet<string> Params { get; } = P("BranchConditionSVar", "BranchConditionSVarCompare", "TrueSubAbility", "FalseSubAbility");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        if (ab["BranchConditionSVar"] is not { } sv) { ctx.Need("Branch:NoCondition"); return; }
        Engine.Amount.Parse(sv, ctx);
        var cmp = ab["BranchConditionSVarCompare"] ?? "GE1";
        if (cmp.Length < 3) ctx.Need("BranchCompare:" + cmp); else Engine.Amount.Parse(cmp[2..], ctx);
        CardCompiler.CompileSideChain(ab, "TrueSubAbility", ctx);
        CardCompiler.CompileSideChain(ab, "FalseSubAbility", ctx);
    }

    public override void Resolve(EffectContext x)
    {
        var ab = x.Ability;
        var cmp = ab["BranchConditionSVarCompare"] ?? "GE1";
        var yes = Filter.Compare(Amount(x, ab["BranchConditionSVar"], 0), cmp[..2], Amount(x, cmp[2..], 0));
        if (ab.SideChains?.GetValueOrDefault(yes ? "TrueSubAbility" : "FalseSubAbility") is { } chain) x.Game.RunSideChain(x, chain);
    }
}

/// <summary>"Double the number of [kind] counters on …".</summary>
sealed class MultiplyCounterEffect : CardsEffect
{
    public override string Api => "MultiplyCounter";
    public override IReadOnlySet<string> Params { get; } = P("Defined", "CounterType", "Multiplier");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        base.Validate(ab, ctx);
        ValidateAmount(ab, "Multiplier", ctx);
    }

    public override void Resolve(EffectContext x)
    {
        var multiplier = Amount(x, x.Ability["Multiplier"], 2);
        foreach (var c in Cards(x))
            foreach (var kind in (x.Ability["CounterType"] is { } k ? new[] { k } : c.Counters?.Keys.ToArray() ?? Array.Empty<string>()))
                x.Game.PutCounters(c, kind, c.CounterCount(kind) * (multiplier - 1));
    }
}

/// <summary>Transforms a double-faced permanent (rule 701.28).</summary>
sealed class SetStateEffect : CardsEffect
{
    public override string Api => "SetState";
    public override IReadOnlySet<string> Params { get; } = P("Defined", "Mode");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        base.Validate(ab, ctx);
        if (ab["Mode"] != "Transform") ctx.Need("SetState.Mode:" + ab["Mode"]);
    }

    public override void Resolve(EffectContext x)
    {
        foreach (var c in Cards(x))
        {
            if (c.Zone != Zone.Battlefield || c.Def.Layout != "DoubleFaced" || c.Def.Faces.Count < 2 || c.CopyDef is not null) continue;
            c.Face = 1 - c.Face;
            x.Game.Note($"  {c.Def.Faces[1 - c.Face].Name} transforms into {c.Name}");
        }
    }
}

/// <summary>Untaps every permanent matching ValidCards$ (Defined$ players' permanents if given).</summary>
sealed class UntapAllEffect : Effect
{
    public override string Api => "UntapAll";
    public override IReadOnlySet<string> Params { get; } = P("ValidCards", "Defined");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        Filter.Parse(ab["ValidCards"] ?? "Permanent", ctx);
        Defined.ValidatePlayers(ab["Defined"], ctx);
    }

    public override void Resolve(EffectContext x)
    {
        var filter = FilterFor(x.SVarFace, x.Ability["ValidCards"] ?? "Permanent");
        var players = x.Ability["Defined"] is { } d ? Defined.Players(d, x) : null;
        foreach (var c in x.S.BattlefieldCards)
            if ((players is null || players.Contains(c.Controller)) && filter.MatchesCard(c, x)) c.Tapped = false;
    }
}

/// <summary>"Tap all creatures your opponents control."</summary>
sealed class TapAllEffect : Effect
{
    public override string Api => "TapAll";
    public override IReadOnlySet<string> Params { get; } = P("ValidCards");
    public override void Validate(AbilityDef ab, CompileContext ctx) => Filter.Parse(ab["ValidCards"] ?? "Permanent", ctx);

    public override void Resolve(EffectContext x)
    {
        var filter = FilterFor(x.SVarFace, x.Ability["ValidCards"] ?? "Permanent");
        foreach (var c in x.S.BattlefieldCards)
            if (filter.MatchesCard(c, x)) c.Tapped = true;
    }
}

/// <summary>
/// Soulbond's pairing: this creature and another unpaired creature you control (Defined$, or one you choose) become
/// paired, if both are still on the battlefield, unpaired and yours.
/// </summary>
sealed class PairEffect : Effect
{
    public override string Api => "Pair";
    public override IReadOnlySet<string> Params { get; } = P("Defined");
    public override void Validate(AbilityDef ab, CompileContext ctx) => Defined.ValidateCards(ab["Defined"], ctx);

    public override void Resolve(EffectContext x)
    {
        var host = x.Host;
        bool Pairable(Card c) => c.Zone == Zone.Battlefield && c.Controller == x.Controller && c.PairedWith is null && c.Types.Is(CardType.Creature);
        if (!Pairable(host)) return;
        var candidates = (x.Ability["Defined"] is { } d ? Defined.Cards(d, x) : x.S.Battlefield)
            .Where(id => id != host.Id && Pairable(x.S[id])).Distinct().ToList();
        if (candidates.Count == 0) return;
        var chosen = candidates.Count == 1 ? candidates : x.Game.Decider.ChooseCards(x.Game, x.Controller, Choice.Generic, host, candidates, 1, 1);
        if (chosen.Count == 0) return;
        var other = x.S[chosen[0]];
        host.PairedWith = other.Id;
        other.PairedWith = host.Id;
        x.Game.Note($"  {host.Name} paired with {other.Name}");
    }
}

/// <summary>Removes counters from permanents ("remove a +1/+1 counter", "remove all counters").</summary>
sealed class RemoveCounterEffect : CardsEffect
{
    public override string Api => "RemoveCounter";
    public override IReadOnlySet<string> Params { get; } = P("Defined", "CounterType", "CounterNum", "RememberRemoved", "RememberAmount", "Optional");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        base.Validate(ab, ctx);
        if (ab["CounterNum"] is { } n && n != "All") ValidateAmount(ab, "CounterNum", ctx);
        if (ab["CounterType"] is null) ctx.Need("RemoveCounter.CounterType:none");
    }

    public override void Resolve(EffectContext x)
    {
        var kind = x.Ability["CounterType"]!;
        // RememberAmount$ is counted the same way: one remembered entry per counter removed.
        var remember = x.Ability["RememberRemoved"] == "True" || x.Ability["RememberAmount"] == "True";
        foreach (var c in Cards(x))
        {
            if (c.Counters is null) continue;
            if (x.Ability["Optional"] == "True" && !x.Game.Decider.ChooseYesNo(x.Game, x.Controller, x.Host, $"remove counters from {c.Name}?")) continue;
            var kinds = kind == "Any" || kind == "All" ? c.Counters.Keys.ToList() : new List<string> { kind };
            // "Remove up to N counters" of any kinds: N in all.
            var budget = kind == "Any" && x.Ability["CounterNum"] != "All" ? Amount(x, x.Ability["CounterNum"], 1) : int.MaxValue;
            foreach (var k in kinds)
            {
                var n = x.Ability["CounterNum"] == "All" ? c.CounterCount(k) : Amount(x, x.Ability["CounterNum"], 1);
                var removed = Math.Min(Math.Min(n, budget), c.CounterCount(k));
                if (removed <= 0) continue;
                c.Counters[k] -= removed;
                budget -= removed;
                // RememberRemoved$: "for each counter removed this way" counts what's remembered.
                if (remember)
                    for (var i = 0; i < removed; i++) (x.Host.Remembered ??= new()).Add(c.Id);
            }
        }
    }
}

/// <summary>A Class gains its next level.</summary>
sealed class ClassLevelUpEffect : Effect
{
    public override string Api => "ClassLevelUp";
    public override IReadOnlySet<string> Params { get; } = P("ClassLevel");

    public override void Resolve(EffectContext x)
    {
        x.Host.ClassLevel = int.Parse(x.Ability["ClassLevel"]!);
        x.Game.StaticsApplied = true;
        x.Game.Fire(new TriggerEvent(TriggerMode.ClassLevelGained, CardId: x.Host.Id, Amount: x.Host.ClassLevel));
        x.Game.Note($"  {x.Host.Name} is now level {x.Host.ClassLevel}");
    }
}

/// <summary>"… gains protection from …": nothing a goldfish opponent does is affected by protection.</summary>
sealed class ProtectionEffect : Effect
{
    public override string Api => "Protection";
    public override IReadOnlySet<string> Params { get; } = P("Gains", "Choices", "Defined", "Duration", "AILogic");
    public override void Resolve(EffectContext x) { }
}

/// <summary>Rolls a die (Sides$) and runs the ResultSubAbilities$ entry for the result ("1-9:DBA,10-20:DBB").</summary>
sealed class RollDiceEffect : Effect
{
    public override string Api => "RollDice";
    public override IReadOnlySet<string> Params { get; } = P("Sides", "ResultSubAbilities", "ResultSVar", "Amount");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        if (!int.TryParse(ab["Sides"] ?? "6", out _)) ctx.Need("RollDice.Sides:" + ab["Sides"]);
        if (ab["Amount"] is { } a && a != "1") ctx.Need("RollDice.Amount:" + a);
        if (ab["ResultSubAbilities"] is not { } results) { if (ab["ResultSVar"] is null) ctx.Need("RollDice:NoResult"); return; }
        foreach (var entry in results.Split(',', StringSplitOptions.TrimEntries))
        {
            var colon = entry.IndexOf(':');
            if (colon < 0 || Range(entry[..colon]) is null) { ctx.Need("RollDice.Result:" + entry); continue; }
            var name = entry[(colon + 1)..];
            if (!ctx.Face.SVars.TryGetValue(name, out var body)) { ctx.Need("SVarMissing:" + name); continue; }
            if (CardCompiler.CompileAbility(new Forge.ForgeParams(body), ctx, AbilityKind.Sub, CardCompiler.SideChainIndex) is { } chain)
                (ab.SideChains ??= new())[entry[..colon]] = chain;
        }
    }

    private static (int Lo, int Hi)? Range(string text)
    {
        var parts = text.Split('-');
        if (parts.Length == 1 && int.TryParse(parts[0], out var one)) return (one, one);
        if (parts.Length == 2 && int.TryParse(parts[0], out var lo) && int.TryParse(parts[1], out var hi)) return (lo, hi);
        return null;
    }

    public override void Resolve(EffectContext x)
    {
        var sides = int.Parse(x.Ability["Sides"] ?? "6");
        var roll = x.S.Rng.Next(sides) + 1;
        x.Game.Note($"  rolled {roll} (d{sides})");
        if (x.Ability["ResultSVar"] is { } sv) (x.Host.StoredVars ??= new())[sv] = roll;
        foreach (var (range, chain) in x.Ability.SideChains ?? new())
            if (Range(range) is var (lo, hi) && roll >= lo && roll <= hi) x.Game.RunSideChain(x, chain);
    }
}

/// <summary>Amass [type] N: put N +1/+1 counters on an Army you control, creating a 0/0 [type] Army first if you have none.</summary>
sealed class AmassEffect : Effect
{
    public override string Api => "Amass";
    public override IReadOnlySet<string> Params { get; } = P("Type", "Num", "RememberAmass");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        ValidateAmount(ab, "Num", ctx);
        var script = TokenScript(ab["Type"]);
        if (ctx.Tokens?.Invoke(script) is { } def) ab.Tokens = new() { def };
        else ctx.Need("TokenScript:" + script);
    }

    private static string TokenScript(string? type) => type is null or "" ? "b_0_0_army" : $"b_0_0_{type.ToLowerInvariant()}_army";

    public override void Resolve(EffectContext x)
    {
        var army = x.S.BattlefieldCards.FirstOrDefault(c => c.Controller == x.Controller && c.Types.HasSubtype("Army"))
                   ?? x.Game.CreateToken(x.Ability.Tokens![0], x.Controller, tapped: false);
        x.Game.PutCounters(army, "P1P1", Amount(x, x.Ability["Num"], 1));
        if (x.Ability["RememberAmass"] == "True") (x.Host.Remembered ??= new()).Add(army.Id);
    }
}

/// <summary>"[Players] get N poison counters."</summary>
sealed class PoisonEffect : Effect
{
    public override string Api => "Poison";
    public override IReadOnlySet<string> Params { get; } = P("Num", "Defined");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        ValidateAmount(ab, "Num", ctx);
        Defined.ValidatePlayers(ab["Defined"], ctx);
    }

    public override void Resolve(EffectContext x)
    {
        var n = Amount(x, x.Ability["Num"], 1);
        foreach (var p in DrawEffect.Players(x)) x.S.Players[p].Poison += n;
    }
}

/// <summary>Explore (rule 701.44): reveal the top card; a land goes to hand, otherwise a +1/+1 counter and you may mill it.</summary>
sealed class ExploreEffect : CardsEffect
{
    public override string Api => "Explore";
    public override IReadOnlySet<string> Params { get; } = P("Defined");

    public override void Resolve(EffectContext x)
    {
        foreach (var c in Cards(x).DefaultIfEmpty(x.Host).ToList())
        {
            var lib = x.S.Players[c.Controller].Library;
            if (lib.Count == 0) { if (c.Zone == Zone.Battlefield) x.Game.PutCounters(c, "P1P1", 1); continue; }
            var top = x.S[lib[0]];
            x.S.Players[c.Controller].KnownTop = Math.Max(1, x.S.Players[c.Controller].KnownTop);
            if (top.Types.Is(CardType.Land)) { x.Game.MoveTo(top, Zone.Hand); continue; }
            if (c.Zone == Zone.Battlefield) x.Game.PutCounters(c, "P1P1", 1);
            if (x.Game.Decider.ChooseYesNo(x.Game, c.Controller, x.Host, $"put {top.Name} into your graveyard?")) x.Game.MoveTo(top, Zone.Graveyard);
        }
    }
}

/// <summary>
/// Cascade (rule 702.85): exile cards from the top until a nonland card with lesser mana value; you may
/// cast it without paying its mana cost; the rest go to the bottom in a random order.
/// </summary>
sealed class CascadeEffect : Effect
{
    public override string Api => "Cascade";
    public override IReadOnlySet<string> Params { get; } = P();

    public override void Resolve(EffectContext x) => Reveal(x, x.Host.CurrentFace.ManaCost.ManaValue - 1, toHandInstead: false);

    /// <summary>Exiles from the top until a nonland card with mana value at most <paramref name="maxValue"/>; casts it free (or puts it in hand); the rest go to the bottom.</summary>
    public static void Reveal(EffectContext x, int maxValue, bool toHandInstead)
    {
        var lib = x.S.Players[x.Controller].Library;
        var mv = maxValue + 1;
        var exiled = new List<Card>();
        Card? hit = null;
        while (lib.Count > 0)
        {
            var c = x.S[lib[0]];
            x.Game.MoveTo(c, Zone.Exile);
            exiled.Add(c);
            if (!c.Types.Is(CardType.Land) && c.CurrentFace.ManaCost.ManaValue < mv) { hit = c; break; }
        }
        x.Game.Note($"  cascade exiles {exiled.Count} card(s)" + (hit is null ? "" : $", hitting {hit.Name}"));
        if (!toHandInstead) Replacements.AsCascade(x.Game, x.Controller, exiled);
        if (hit is not null && x.Game.CanCastFromEffect(hit, free: true)
            && x.Game.Decider.ChooseYesNo(x.Game, x.Controller, hit, $"cast {hit.Name} without paying its mana cost?"))
        {
            x.Game.CastFromEffect(hit, free: true, exileAfter: false);
            exiled.Remove(hit);
        }
        else if (hit is not null && toHandInstead)
        {
            x.Game.MoveTo(hit, Zone.Hand);   // discover: or put it into your hand
            exiled.Remove(hit);
        }
        x.S.Rng.Shuffle(exiled);
        foreach (var c in exiled)
            if (c.Zone == Zone.Exile) x.Game.MoveTo(c, Zone.Library, position: -1);
    }
}

/// <summary>Discover N (rule 701.57): like cascade with mana value N or less, and the card may go to hand instead.</summary>
sealed class DiscoverEffect : Effect
{
    public override string Api => "Discover";
    public override IReadOnlySet<string> Params { get; } = P("Num");
    public override void Validate(AbilityDef ab, CompileContext ctx) => ValidateAmount(ab, "Num", ctx);
    public override void Resolve(EffectContext x) => CascadeEffect.Reveal(x, Amount(x, x.Ability["Num"], 0), toHandInstead: true);
}

/// <summary>Ninjutsu: the cost returned an attacker; this enters tapped and attacking.</summary>
sealed class NinjutsuEffect : Effect
{
    public override string Api => "Ninjutsu";
    public override IReadOnlySet<string> Params { get; } = P();

    public override void Resolve(EffectContext x)
    {
        if (x.Host.Zone != Zone.Hand) return;
        x.Game.MoveTo(x.Host, Zone.Battlefield, tapped: true);
        if (x.Host.Zone == Zone.Battlefield) x.S.Attackers.Add(x.Host.Id);
    }
}

/// <summary>
/// Earthbend N: target land you control becomes a 0/0 creature with haste that's still a land, gets N
/// +1/+1 counters, and returns to the battlefield tapped when it dies or is exiled.
/// </summary>
sealed class EarthbendEffect : Effect
{
    public override string Api => "Earthbend";
    public override IReadOnlySet<string> Params { get; } = P("Num");

    private static readonly AbilityDef Becomes = new()
    {
        P = new Forge.ForgeParams("DB$ Animate | Types$ Creature | Keywords$ Haste | Duration$ Permanent"), Api = "Animate", Kind = AbilityKind.Sub,
    };

    public override void Validate(AbilityDef ab, CompileContext ctx) => ValidateAmount(ab, "Num", ctx);

    public override void Resolve(EffectContext x)
    {
        var lands = x.S.BattlefieldCards.Where(c => c.Controller == x.Controller && c.Types.Is(CardType.Land)).Select(c => c.Id).ToList();
        if (lands.Count == 0) return;
        var land = x.S[x.Game.Decider.ChooseCards(x.Game, x.Controller, Choice.Generic, x.Host, lands, 1, 1)[0]];
        (land.Animations ??= new()).Add(new Animation(Becomes, "Creature", 0, 0, null, "Permanent", x.Host.Id, x.S.Turn));
        x.Game.StaticsApplied = true;
        x.Game.PutCounters(land, "P1P1", Amount(x, x.Ability["Num"], 1));
        if (!x.S.ReturnWhenDies.Contains(land.Id)) x.S.ReturnWhenDies.Add(land.Id);
        x.Game.Note($"  earthbend {land.Name}");
    }
}

/// <summary>Moves counters from one permanent to another.</summary>
sealed class MoveCounterEffect : Effect
{
    public override string Api => "MoveCounter";
    public override IReadOnlySet<string> Params { get; } = P("Source", "Defined", "CounterType", "CounterNum");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        Defined.ValidateCards(ab["Source"], ctx);
        Defined.ValidateCards(ab["Defined"], ctx);
        if (ab["CounterType"] is null) ctx.Need("MoveCounter.CounterType:none");
        if (ab["CounterNum"] is { } n && n != "All") ValidateAmount(ab, "CounterNum", ctx);
    }

    public override void Resolve(EffectContext x)
    {
        var kind = x.Ability["CounterType"]!;
        var from = Defined.Cards(x.Ability["Source"] ?? "Self", x).Select(id => x.S[id]).FirstOrDefault();
        var to = Defined.Cards(x.Ability["Defined"], x, "Targeted").Select(id => x.S[id]).FirstOrDefault(c => c.Zone == Zone.Battlefield);
        if (from is null || to is null || from.Id == to.Id) return;
        var n = Math.Min(from.CounterCount(kind), x.Ability["CounterNum"] == "All" ? int.MaxValue : Amount(x, x.Ability["CounterNum"], 1));
        if (n <= 0) return;
        from.Counters![kind] -= n;
        x.Game.PutCounters(to, kind, n);
    }
}

/// <summary>"Choose one: …" where you choose at resolution (GenericChoice with Defined$ You).</summary>
sealed class GenericChoiceEffect : Effect
{
    public override string Api => "GenericChoice";
    // FallbackAbility$ is what the AI does when it can't choose; a chooser here always can.
    public override IReadOnlySet<string> Params { get; } = P("Choices", "Defined", "ShowChoice", "AILogic", "FallbackAbility", "TempRemember", "SetChosenMode");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        if (ab["Defined"] is { } d && d is not ("You" or "Opponent" or "Player")) { ctx.Need("GenericChoice.Defined:" + d); return; }
        if (ab["TempRemember"] is { } tr && tr != "Chooser") ctx.Need("GenericChoice.TempRemember:" + tr);
        foreach (var name in (ab["Choices"] ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!ctx.Face.SVars.TryGetValue(name, out var body)) { ctx.Need("SVarMissing:" + name); continue; }
            if (CardCompiler.CompileAbility(new Forge.ForgeParams(body), ctx, AbilityKind.Sub, CardCompiler.SideChainIndex) is { } chain)
                (ab.SideChains ??= new())[name] = chain;
        }
        if (ab.SideChains is null) ctx.Need("GenericChoice:NoChoices");
    }

    public override void Resolve(EffectContext x)
    {
        var options = x.Ability.SideChains!.ToList();
        // Each defined player chooses in turn ("each opponent … unless that player …"); TempRemember$ Chooser makes them "Remembered".
        var saved = x.Item.IteratedPlayer;
        foreach (var p in Defined.Players(x.Ability["Defined"], x))
        {
            if (x.S.GameOver) break;
            if (x.Ability["TempRemember"] == "Chooser") x.Item.IteratedPlayer = p;
            // Goldfish opponents make no real choices: they take the first.
            var pick = options.Count == 1 || x.S.Players[p].IsGoldfish ? 0
                : x.Game.Decider.ChooseOption(x.Game, p, x.Host, options.Select(o => o.Value.Description).ToList());
            // "As this enters, choose Khans or Dragons": remembered as the card's chosen mode.
            if (x.Ability["SetChosenMode"] == "True") x.Host.ChosenMode = options[pick].Key;
            x.Game.RunSideChain(x, options[pick].Value);
        }
        x.Item.IteratedPlayer = saved;
    }
}

/// <summary>
/// "Will of the council", "council's dilemma": each player votes for one of the choices, starting with you. Goldfish
/// opponents vote for the first choice you didn't (CONTRACT.md §3). With EachVote$, every vote's choice happens for its
/// voter (who is "Remembered"); otherwise the choice with the most votes happens, and a tie is VoteTiedAbility$ or your vote.
/// </summary>
sealed class VoteEffect : Effect
{
    public override string Api => "Vote";
    public override IReadOnlySet<string> Params { get; } = P("Defined", "Choices", "EachVote", "VoteTiedAbility", "AILogic", "Secretly", "VoteMessage");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        if (ab["Defined"] is { } d && d != "Player") ctx.Need("Vote.Defined:" + d);
        foreach (var name in (ab["Choices"] ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!ctx.Face.SVars.TryGetValue(name, out var body)) { ctx.Need("SVarMissing:" + name); continue; }
            if (CardCompiler.CompileAbility(new Forge.ForgeParams(body), ctx, AbilityKind.Sub, CardCompiler.SideChainIndex) is { } chain)
                (ab.SideChains ??= new())[name] = chain;
        }
        if (ab.SideChains is null) ctx.Need("Vote:NoChoices");
        if (ab["VoteTiedAbility"] is not null && CardCompiler.CompileSideChain(ab, "VoteTiedAbility", ctx) is null) ctx.Need("Vote:VoteTiedAbility");
    }

    public override void Resolve(EffectContext x)
    {
        var choices = (x.Ability["Choices"] ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(x.Ability.SideChains!.ContainsKey).ToList();
        var voters = Defined.Players(x.Ability["Defined"] ?? "Player", x).OrderBy(p => p == x.Controller ? 0 : 1).ToList();
        int? yours = null;
        var votes = new List<(int Voter, int Choice)>();
        foreach (var p in voters)
        {
            int pick;
            if (x.S.Players[p].IsGoldfish) pick = yours is { } y && choices.Count > 1 ? (y == 0 ? 1 : 0) : 0;
            else
            {
                pick = choices.Count == 1 ? 0 : x.Game.Decider.ChooseOption(x.Game, p, x.Host, choices.Select(c => x.Ability.SideChains![c].Description).ToList());
                if (p == x.Controller) yours = pick;
            }
            votes.Add((p, pick));
        }
        x.Game.Note("  votes: " + string.Join(", ", votes.Select(v => $"player {v.Voter} {choices[v.Choice]}")));

        var saved = x.Item.IteratedPlayer;
        if (x.Ability["EachVote"] == "True")
            foreach (var (voter, choice) in votes)
            {
                if (x.S.GameOver) break;
                x.Item.IteratedPlayer = voter;
                x.Game.RunSideChain(x, x.Ability.SideChains![choices[choice]]);
            }
        else
        {
            var tally = votes.GroupBy(v => v.Choice).Select(g => (Choice: g.Key, Count: g.Count())).ToList();
            var most = tally.Max(t => t.Count);
            var winners = tally.Where(t => t.Count == most).Select(t => t.Choice).ToList();
            if (winners.Count == 1) x.Game.RunSideChain(x, x.Ability.SideChains![choices[winners[0]]]);
            else if (x.Ability.SideChains!.TryGetValue("VoteTiedAbility", out var tied)) x.Game.RunSideChain(x, tied);
            else x.Game.RunSideChain(x, x.Ability.SideChains![choices[yours is { } y && winners.Contains(y) ? y : winners[0]]]);
        }
        x.Item.IteratedPlayer = saved;
    }
}

/// <summary>"End the turn" (rule 723).</summary>
sealed class EndTurnEffect : Effect
{
    public override string Api => "EndTurn";
    public override IReadOnlySet<string> Params { get; } = P();
    public override void Resolve(EffectContext x) => x.Game.EndTheTurn();
}

/// <summary>"You become the monarch."</summary>
sealed class BecomeMonarchEffect : Effect
{
    public override string Api => "BecomeMonarch";
    public override IReadOnlySet<string> Params { get; } = P("Defined");
    public override void Validate(AbilityDef ab, CompileContext ctx) => Defined.ValidatePlayers(ab["Defined"], ctx);

    public override void Resolve(EffectContext x)
    {
        foreach (var p in Defined.Players(x.Ability["Defined"] ?? "You", x)) x.Game.BecomeMonarch(p);
    }
}

/// <summary>"After this phase, there is an additional combat phase [followed by an additional main phase]."</summary>
sealed class AddPhaseEffect : Effect
{
    public override string Api => "AddPhase";
    public override IReadOnlySet<string> Params { get; } = P("ExtraPhase", "AfterPhase", "FollowedBy");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        if (ab["ExtraPhase"] != "Combat") ctx.Need("AddPhase.ExtraPhase:" + ab["ExtraPhase"]);
        if (ab["AfterPhase"] is { } a && a != "EndCombat") ctx.Need("AddPhase.AfterPhase:" + a);
        if (ab["FollowedBy"] is { } f && f != "Main2") ctx.Need("AddPhase.FollowedBy:" + f);
    }

    public override void Resolve(EffectContext x)
    {
        var steps = new List<Step> { Step.BeginCombat, Step.DeclareAttackers, Step.CombatDamage, Step.EndCombat };
        if (x.Ability["FollowedBy"] == "Main2") steps.Add(Step.Main2);
        // "After this phase": the main phase it was used in, or this combat's end.
        var after = x.Ability["AfterPhase"] == "EndCombat" || x.S.Step is Step.BeginCombat or Step.DeclareAttackers or Step.CombatDamage
            ? Step.EndCombat : x.S.Step;
        x.S.ThisTurn.ExtraPhases.Add((after, steps.ToArray()));
    }
}

/// <summary>"Take an extra turn after this one."</summary>
sealed class AddTurnEffect : Effect
{
    public override string Api => "AddTurn";
    public override IReadOnlySet<string> Params { get; } = P("NumTurns", "Defined");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        ValidateAmount(ab, "NumTurns", ctx);
        Defined.ValidatePlayers(ab["Defined"], ctx);
    }

    public override void Resolve(EffectContext x)
    {
        // Only the simulated player's turns are played; extra turns for a goldfish opponent change nothing.
        var players = x.Ability["Defined"] is { } d ? Defined.Players(d, x) : x.Ability.TargetFilter is not null ? Defined.Players("Targeted", x) : new() { x.Controller };
        if (!players.Contains(Game.Me)) return;
        x.S.ExtraTurns += Amount(x, x.Ability["NumTurns"], 1);
        x.Game.Note($"  extra turn(s): {x.S.ExtraTurns}");
    }
}
