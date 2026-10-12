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
