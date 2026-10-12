using System.Collections.Concurrent;
using MagicSim.Core.Forge;

namespace MagicSim.Core.Engine;

/// <summary>
/// Copy effects (rule 707): a copy takes the copiable values of the original, which are its
/// printed values plus other copy effects and the copy's own "except …" changes, and nothing else.
/// </summary>
public static class Copies
{
    /// <summary>The "except …" parameters a copy effect may carry.</summary>
    public static readonly string[] ExceptParams = { "AddTypes", "SetPower", "SetToughness", "AddKeywords", "NonLegendary", "SetColor", "SetCreatureTypes" };

    public static void ValidateExcept(AbilityDef ab, CompileContext ctx)
    {
        foreach (var key in new[] { "SetPower", "SetToughness" })
            if (ab[key] is { } v && !int.TryParse(v, out _)) ctx.Need($"{ab.Api}.{key}:{v}");
        if (ab["AddKeywords"] is { } kws)
            foreach (var k in kws.Split('&', StringSplitOptions.TrimEntries))
                if (!Keywords.Supported.Contains(ForgeRequirements.KeywordName(k))) ctx.Need("Keyword:" + k);
        if (ab["SetColor"] is { } col && ColorsOf(col) is null) ctx.Need($"{ab.Api}.SetColor:{col}");
    }

    // A copy of the same face with the same changes is the same definition; caching keeps the
    // per-face expression caches from growing with every copy made.
    private static readonly ConcurrentDictionary<(FaceDef, AbilityDef?, int?, int?), CardDef> Cache = new();

    /// <summary>What a copy of <paramref name="original"/> is, with the "except …" changes of <paramref name="ab"/>.</summary>
    public static CardDef Of(Card original, AbilityDef? ab)
    {
        var printed = original.CopyDef ?? original.Def;
        return Cache.GetOrAdd((original.CurrentFace, ab, original.TokenPower, original.TokenToughness), k =>
        {
            var (face, mods, tp, tt) = k;
            var types = face.Types;
            var keywords = new List<string>();
            int? power = tp ?? face.Power, toughness = tt ?? face.Toughness;
            var colors = face.Colors;
            if (mods is not null)
            {
                if (mods["AddTypes"] is { } add) types = TypeLine.Parse(types + " " + add.Replace('&', ' '));
                if (mods["SetCreatureTypes"] is { } ct)
                    types = TypeLine.Parse(new TypeLine { Types = types.Types, Supers = types.Supers }
                        + " " + string.Join(' ', types.Subtypes.Where(s => !TypeLine.IsCreatureType(s))) + " " + ct.Replace('&', ' '));
                if (mods["NonLegendary"] == "True")
                    types = new TypeLine { Types = types.Types, Supers = types.Supers & ~SuperType.Legendary, Subtypes = types.Subtypes, AllCreatureTypes = types.AllCreatureTypes };
                if (mods["SetPower"] is { } sp) power = int.Parse(sp);
                if (mods["SetToughness"] is { } st) toughness = int.Parse(st);
                if (mods["AddKeywords"] is { } kws) keywords.AddRange(kws.Split('&', StringSplitOptions.TrimEntries));
                if (mods["SetColor"] is { } col) colors = ColorsOf(col)!.Value;
            }
            var copy = new CardDef { Name = face.Name, Source = printed.Source, IsBlank = printed.IsBlank, ColorIdentity = printed.ColorIdentity };
            copy.Faces.Add(face.CopyWith(types, power, toughness, colors, keywords));
            return copy;
        });
    }

    private static ColorMask? ColorsOf(string text)
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

    /// <summary>The cards a copy effect copies: Defined$, its targets, or a choice (Choices$ in ChoiceZone$).</summary>
    public static List<int> Originals(EffectContext x, bool optional)
    {
        var ab = x.Ability;
        if (ab["Choices"] is { } choices)
        {
            var zone = ab["ChoiceZone"] is { } z ? Enum.Parse<Zone>(z) : Zone.Battlefield;
            var filter = Effect.FilterFor(x.SVarFace, choices);
            var candidates = x.Game.CardsIn(zone).Where(c => filter.MatchesCard(c, x)).Select(c => c.Id).ToList();
            if (candidates.Count == 0) return candidates;
            return x.Game.Decider.ChooseCards(x.Game, x.Controller, Choice.Copy, x.Host, candidates, optional ? 0 : 1, 1);
        }
        if (ab["Defined"] is { } d) return Defined.Cards(d, x);
        return ab.TargetFilter is not null ? Defined.Cards("Targeted", x) : new();
    }

    public static readonly HashSet<string> ChoiceZones = new(StringComparer.Ordinal) { "Battlefield", "Graveyard", "Exile", "Hand" };

    public static void ValidateOriginals(AbilityDef ab, CompileContext ctx)
    {
        if (ab["Choices"] is { } ch)
        {
            Filter.Parse(ch, ctx);
            if (ab["ChoiceZone"] is { } z && !ChoiceZones.Contains(z)) ctx.Need($"{ab.Api}.ChoiceZone:{z}");
        }
        else if (ab["Defined"] is { } d) Defined.ValidateCards(d, ctx);
        else if (ab.TargetFilter is null && ab["Populate"] is null) ctx.Need($"{ab.Api}:NoOriginal");
    }
}

/// <summary>"Create a token that's a copy of …": Kiki-Jiki, Helm of the Host, populate.</summary>
sealed class CopyPermanentEffect : Effect
{
    public override string Api => "CopyPermanent";
    public override IReadOnlySet<string> Params { get; } = P(new[]
    {
        "Defined", "Choices", "ChoiceZone", "NumCopies", "TokenTapped", "TokenAttacking", "PumpKeywords", "PumpDuration",
        "AtEOT", "RememberTokens", "Controller", "Populate",
    }.Concat(Copies.ExceptParams).ToArray());

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        Copies.ValidateOriginals(ab, ctx);
        Copies.ValidateExcept(ab, ctx);
        ValidateAmount(ab, "NumCopies", ctx);
        Defined.ValidatePlayers(ab["Controller"], ctx);
        if (ab["TokenAttacking"] is { } att && att != "True") ctx.Need("CopyPermanent.TokenAttacking:" + att);
        if (ab["PumpDuration"] is { } pd && pd is not ("EOT" or "EndOfTurn")) ctx.Need("CopyPermanent.PumpDuration:" + pd);
        if (ab["PumpKeywords"] is { } kws)
            foreach (var k in kws.Split('&', StringSplitOptions.TrimEntries))
                if (!Keywords.Supported.Contains(ForgeRequirements.KeywordName(k))) ctx.Need("Keyword:" + k);
        if (ab["AtEOT"] is { } eot && eot is not ("Exile" or "Sacrifice")) ctx.Need("CopyPermanent.AtEOT:" + eot);
    }

    public override void Resolve(EffectContext x)
    {
        var ab = x.Ability;
        List<int> originals;
        if (ab["Populate"] == "True")
        {
            // "Populate": copy a creature token you control.
            var tokens = x.S.BattlefieldCards.Where(c => c.IsToken && c.Controller == x.Controller && c.Types.Is(CardType.Creature)).Select(c => c.Id).ToList();
            originals = tokens.Count == 0 ? tokens : x.Game.Decider.ChooseCards(x.Game, x.Controller, Choice.Copy, x.Host, tokens, 1, 1);
        }
        else originals = Copies.Originals(x, optional: false);
        var n = Amount(x, ab["NumCopies"], 1);
        var controller = ab["Controller"] is { } ctl ? Defined.Players(ctl, x).FirstOrDefault(x.Controller) : x.Controller;
        foreach (var id in originals)
        {
            var def = Copies.Of(x.S[id], ab);
            for (var i = 0; i < n; i++)
            {
                var token = x.Game.CreateToken(def, controller, ab["TokenTapped"] == "True");
                if (ab["RememberTokens"] == "True") (x.Host.Remembered ??= new()).Add(token.Id);
                if (ab["PumpKeywords"] is { } kws)
                {
                    token.TempKeywords ??= new HashSet<string>(StringComparer.Ordinal);
                    foreach (var k in kws.Split('&', StringSplitOptions.TrimEntries)) token.TempKeywords.Add(ForgeRequirements.KeywordName(k));
                }
                if (ab["TokenAttacking"] == "True" && x.S.Step == Step.DeclareAttackers) x.S.Attackers.Add(token.Id);
                if (ab["AtEOT"] is { } eot) x.S.ThisTurn.AtEndStep.Add((token.Id, eot));
            }
        }
    }
}

/// <summary>"… becomes a copy of …" / "enter as a copy of …": Clone, Phyrexian Metamorph, Mirage Mirror.</summary>
sealed class CloneEffect : Effect
{
    public override string Api => "Clone";
    public override IReadOnlySet<string> Params { get; } = P(new[] { "Defined", "Choices", "ChoiceZone", "CloneTarget", "Duration", "Optional" }
        .Concat(Copies.ExceptParams).ToArray());

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        Copies.ValidateOriginals(ab, ctx);
        Copies.ValidateExcept(ab, ctx);
        Defined.ValidateCards(ab["CloneTarget"], ctx);
        if (ab["Duration"] is { } d && d is not ("UntilEndOfTurn" or "EOT")) ctx.Need("Clone.Duration:" + d);
    }

    public override void Resolve(EffectContext x)
    {
        var ab = x.Ability;
        if (Copies.Originals(x, ab["Optional"] == "True").FirstOrDefault(-1) is not (>= 0 and var id)) return;
        var copy = Copies.Of(x.S[id], ab);
        foreach (var t in Defined.Cards(ab["CloneTarget"], x))
        {
            var c = x.S[t];
            if (c.Zone is not (Zone.Battlefield or Zone.None)) continue;
            c.CopyDef = copy;
            c.CopyUntilCleanup = ab["Duration"] is not null;
            x.Game.Note($"  {c.Def.Name} becomes a copy of {copy.Name}");
        }
    }
}
