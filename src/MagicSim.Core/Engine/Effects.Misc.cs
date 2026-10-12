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

/// <summary>Removes counters from permanents ("remove a +1/+1 counter", "remove all counters").</summary>
sealed class RemoveCounterEffect : CardsEffect
{
    public override string Api => "RemoveCounter";
    public override IReadOnlySet<string> Params { get; } = P("Defined", "CounterType", "CounterNum");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        base.Validate(ab, ctx);
        if (ab["CounterNum"] is { } n && n != "All") ValidateAmount(ab, "CounterNum", ctx);
        if (ab["CounterType"] is null) ctx.Need("RemoveCounter.CounterType:none");
    }

    public override void Resolve(EffectContext x)
    {
        var kind = x.Ability["CounterType"]!;
        foreach (var c in Cards(x))
        {
            if (c.Counters is null) continue;
            var kinds = kind == "Any" || kind == "All" ? c.Counters.Keys.ToList() : new List<string> { kind };
            foreach (var k in kinds)
            {
                var n = x.Ability["CounterNum"] == "All" ? c.CounterCount(k) : Amount(x, x.Ability["CounterNum"], 1);
                if (c.Counters.ContainsKey(k)) c.Counters[k] = Math.Max(0, c.CounterCount(k) - n);
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
