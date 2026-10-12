using MagicSim.Core.Forge;

namespace MagicSim.Core.Engine;

/// <summary>Abilities, triggers and statics one card gives another (AddAbility$, Abilities$, Triggers$, …), compiled with the giver.</summary>
public sealed class Grants
{
    public List<AbilityDef> Activated { get; } = new();
    public List<TriggerDef> Triggers { get; } = new();
    public List<StaticDef> Statics { get; } = new();

    /// <summary>Compiles the SVars named in these parameters (comma- or " &amp; "-separated). Null if none are present.</summary>
    public static Grants? Compile(ForgeParams p, CompileContext ctx, string abilities, string triggers, string? statics)
    {
        if (p[abilities] is null && p[triggers] is null && (statics is null || p[statics] is null)) return null;
        var g = new Grants();
        foreach (var name in Names(p[abilities]))
            if (Body(name, ctx) is { } body && CardCompiler.CompileAbility(new ForgeParams(body), ctx, AbilityKind.Activated, 0) is { } ab)
            {
                if (ab.P.AbilityKind != "AB") ctx.Need("Grant:" + (ab.P.AbilityKind ?? "?"));
                g.Activated.Add(ab);
            }
        foreach (var name in Names(p[triggers]))
            if (Body(name, ctx) is { } body) g.Triggers.Add(CardCompiler.CompileTrigger(new ForgeParams(body), ctx));
        if (statics is not null)
            foreach (var name in Names(p[statics]))
                if (Body(name, ctx) is { } body) g.Statics.Add(CardCompiler.CompileStatic(new ForgeParams(body), ctx));
        return g;
    }

    private static IEnumerable<string> Names(string? list) =>
        (list ?? "").Split(new[] { ',', '&' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static string? Body(string name, CompileContext ctx)
    {
        if (ctx.Face.SVars.TryGetValue(name, out var body)) return body;
        ctx.Need("SVarMissing:" + name);
        return null;
    }
}

/// <summary>Type-changing effects (layer 4): add types, remove kinds of types, all creature types.</summary>
public static class TypeChange
{
    /// <summary>The words an "add types" list stands for; ChosenType and AllBasicLandType are resolved against the source.</summary>
    public static string Words(string list, Card source) => string.Join(' ',
        list.Split(new[] { ',', '&' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(w => w switch
        {
            "ChosenType" => source.ChosenType ?? "",
            "AllBasicLandType" => string.Join(' ', TypeLine.BasicLandTypes),
            _ => w,
        }));

    public static void Validate(string? list, CompileContext ctx, string what)
    {
        if (list is null) return;
        foreach (var w in list.Split(new[] { ',', '&' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            if (w.StartsWith("Chosen", StringComparison.Ordinal) && w != "ChosenType")
                ctx.Need($"{what}:{w}");
    }

    public static TypeLine Apply(TypeLine t, string? addWords, bool removeCardTypes, bool removeCreatureTypes, bool removeSupertypes, bool allCreatureTypes)
    {
        var added = addWords is null ? null : TypeLine.Parse(addWords);
        var types = (removeCardTypes ? 0 : t.Types) | (added?.Types ?? 0);
        var supers = (removeSupertypes ? 0 : t.Supers) | (added?.Supers ?? 0);
        // Losing the other card types loses their subtypes too (an Equipment that becomes only a creature).
        var subs = t.Subtypes.Where(s => TypeLine.IsCreatureType(s) ? !removeCreatureTypes : !removeCardTypes);
        var allSubs = subs.Concat(added?.Subtypes ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new TypeLine
        {
            Types = types, Supers = supers, Subtypes = allSubs,
            AllCreatureTypes = (t.AllCreatureTypes && !removeCreatureTypes) || allCreatureTypes || (added?.AllCreatureTypes ?? false),
        };
    }
}

/// <summary>One "becomes a …" effect on a permanent, in timestamp order (see Statics.Apply).</summary>
public sealed record Animation(
    AbilityDef Ability, string? AddTypes, int? Power, int? Toughness, ColorMask? Colors, string Duration, int HostId, int Turn);

/// <summary>"… becomes an N/N [type] creature [with abilities] until end of turn": manlands, Ensoul Artifact, Sneak Attack-likes.</summary>
sealed class AnimateEffect : Effect
{
    public override string Api => "Animate";
    public override IReadOnlySet<string> Params { get; } = P(
        "Defined", "Types", "Power", "Toughness", "Keywords", "Colors", "OverwriteColors", "RemoveCardTypes", "RemoveCreatureTypes",
        "RemoveSuperTypes", "RemoveAllAbilities", "Abilities", "Triggers", "staticAbilities", "Duration", "AtEOT", "sVars",
        "AddAllCreatureTypes");

    public static readonly HashSet<string> Durations = new(StringComparer.Ordinal)
    {
        "Permanent", "Perpetual", "EOT", "UntilEndOfTurn", "UntilYourNextTurn", "UntilHostLeavesPlay", "UntilHostLeavesPlayOrEOT",
        "UntilTheEndOfYourNextTurn",
    };

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        Defined.ValidateCards(ab["Defined"], ctx);
        if (ab["Defined"] is null && ab.TargetFilter is null) ctx.Need("Animate:NoDefined");
        ValidateAmount(ab, "Power", ctx);
        ValidateAmount(ab, "Toughness", ctx);
        TypeChange.Validate(ab["Types"], ctx, "Animate.Types");
        if (ab["Keywords"] is { } kws)
            foreach (var k in kws.Split('&', StringSplitOptions.TrimEntries))
                if (!Keywords.Supported.Contains(ForgeRequirements.KeywordName(k))) ctx.Need("Keyword:" + ForgeRequirements.KeywordName(k));
        if (ab["Colors"] is { } col && col != "ChosenColor" && ColorsOf(col) is null) ctx.Need("Animate.Colors:" + col);
        if (ab["Duration"] is { } d && !Durations.Contains(d)) ctx.Need("Animate.Duration:" + d);
        if (ab["AtEOT"] is { } eot && eot is not ("Exile" or "Sacrifice")) ctx.Need("Animate.AtEOT:" + eot);
        ab.Grants = Grants.Compile(ab.P, ctx, "Abilities", "Triggers", "staticAbilities");
    }

    public static ColorMask? ColorsOf(string text)
    {
        ColorMask m = 0;
        foreach (var w in text.Split(new[] { ',', '&', ' ' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (w == "Colorless") continue;
            if (ManaColors.FromWord(w) is not { } c || c == ManaColor.C) return null;
            m |= c.Mask();
        }
        return m;
    }

    public override void Resolve(EffectContext x) =>
        AnimateCards(x, Defined.Cards(x.Ability["Defined"], x, "Targeted").Select(id => x.S[id]).ToList());

    /// <summary>Applies this ability's "becomes …" effect to each of <paramref name="cards"/> on the battlefield.</summary>
    public static void AnimateCards(EffectContext x, List<Card> cards)
    {
        var ab = x.Ability;
        int? power = ab["Power"] is { } p ? Amount(x, p, 0) : null;
        int? toughness = ab["Toughness"] is { } t ? Amount(x, t, 0) : null;
        ColorMask? colors = ab["Colors"] switch
        {
            null => null,
            "ChosenColor" => x.Host.ChosenColor?.Mask() ?? 0,
            var c => ColorsOf(c),
        };
        var types = ab["Types"] is { } ty ? TypeChange.Words(ty, x.Host) : null;
        var anim = new Animation(ab, types, power, toughness, colors, ab["Duration"] ?? "EOT", x.Host.Id, x.S.Turn);
        foreach (var c in cards)
        {
            if (c.Zone != Zone.Battlefield) continue;
            (c.Animations ??= new()).Add(anim);
            if (ab["AtEOT"] is { } eot) x.S.ThisTurn.AtEndStep.Add((c.Id, eot));
            x.Game.Note($"  {c.Name} becomes " + string.Join(' ', new[] { power is null ? null : $"{power}/{toughness}", types }.Where(s => s is not null)));
        }
        x.Game.StaticsApplied = true;
    }

    /// <summary>Whether an animation has ended: at cleanup, at the start of a turn ("untap"), or once its host has left ("links").</summary>
    public static bool Ends(Game g, Animation a, string moment) => (moment, a.Duration) switch
    {
        ("cleanup", "EOT" or "UntilEndOfTurn" or "UntilHostLeavesPlayOrEOT") => true,
        ("cleanup", "UntilTheEndOfYourNextTurn") => a.Turn < g.State.Turn,
        ("untap", "UntilYourNextTurn") => a.Turn < g.State.Turn,
        ("links", "UntilHostLeavesPlay" or "UntilHostLeavesPlayOrEOT") => g.State[a.HostId].Zone != Zone.Battlefield,
        _ => false,
    };
}
