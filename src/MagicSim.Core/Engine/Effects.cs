using System.Collections.Concurrent;

namespace MagicSim.Core.Engine;

/// <summary>
/// One Forge effect type (ApiType). <see cref="Params"/> lists every parameter the handler
/// understands; a card using any other parameter is reported unsupported rather than run wrong.
/// </summary>
public abstract class Effect
{
    public abstract string Api { get; }
    public abstract IReadOnlySet<string> Params { get; }
    public virtual void Validate(AbilityDef ab, CompileContext ctx) { }
    public abstract void Resolve(EffectContext x);

    protected static HashSet<string> P(params string[] names)
    {
        var set = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (var c in Conditions.Params) set.Add(c);
        set.Add("TgtZone");
        return set;
    }

    // Cached parsing of a face's expressions at resolution time.
    private static readonly ConcurrentDictionary<(FaceDef, string), Amount> AmountCache = new();
    private static readonly ConcurrentDictionary<(FaceDef, string), Filter> FilterCache = new();

    /// <summary>Evaluates an amount expression of the context's source card.</summary>
    public static int EvalAmount(EffectContext x, string text) => Amount(x, text, 0);

    protected static int Amount(EffectContext x, string? text, int fallback)
    {
        if (text is null) return fallback;
        var face = x.SVarFace;
        return AmountCache.GetOrAdd((face, text), k => Engine.Amount.Parse(k.Item2, new CompileContext { Face = k.Item1, Missing = new() })).Eval(x);
    }

    public static Filter FilterFor(FaceDef face, string text) =>
        FilterCache.GetOrAdd((face, text), k => Filter.Parse(k.Item2, new CompileContext { Face = k.Item1, Missing = new() }));

    protected static void ValidateAmount(AbilityDef ab, string key, CompileContext ctx)
    {
        if (ab[key] is { } v) Engine.Amount.Parse(v, ctx);
    }
}

public static class Effects
{
    private static readonly Dictionary<string, Effect> All = new Effect[]
    {
        new ManaEffect(), new DrawEffect(), new ChangeZoneEffect(), new DealDamageEffect(), new LoseLifeEffect(),
        new GainLifeEffect(), new WinsGameEffect(), new LosesGameEffect(), new DigEffect(), new NameCardEffect(),
        new DigUntilEffect(), new MillEffect(), new DiscardEffect(), new PumpEffect(), new UntapEffect(), new TapEffect(),
        new PutCounterEffect(), new DestroyEffect(), new SacrificeEffect(), new ScryEffect(), new CleanupEffect(),
        new ShuffleEffect(), new RevealHandEffect(), new CharmEffect(), new TokenEffect(), new AttachEffect(),
        new ChangeZoneAllEffect(), new DestroyAllEffect(), new DamageAllEffect(), new PumpAllEffect(), new PutCounterAllEffect(),
        new CounterEffect(), new SurveilEffect(), new RearrangeTopOfLibraryEffect(), new RegenerateEffect(),
        new ManaReflectedEffect(), new FlipCoinEffect(), new RepeatEffect(), new StoreSVarEffect(),
        new EffectEffect(), new ChooseColorEffect(), new ChooseTypeEffect(), new ChoosePlayerEffect(),
        new CopyPermanentEffect(), new CloneEffect(), new AnimateEffect(), new ProliferateEffect(),
        new DelayedTriggerEffect(), new RepeatEachEffect(), new CopySpellAbilityEffect(), new PlayEffect(), new GainControlEffect(),
        new AnimateAllEffect(), new FightEffect(), new BranchEffect(), new MultiplyCounterEffect(), new SetStateEffect(),
        new UntapAllEffect(), new RemoveCounterEffect(), new ClassLevelUpEffect(), new ProtectionEffect(), new RollDiceEffect(),
        new ImmediateTriggerEffect(), new SuspendEffect(), new AmassEffect(),
        new PoisonEffect(), new ExploreEffect(), new CascadeEffect(),
    }.ToDictionary(e => e.Api, StringComparer.Ordinal);

    public static Effect? Get(string api) => All.TryGetValue(api, out var e) ? e : null;
    public static IEnumerable<string> Apis => All.Keys;
}

// ---- mana ------------------------------------------------------------------------------

/// <summary>
/// "Add one mana of any color [type] that [cards] could produce / are": Reflecting Pool, Fellwar Stone,
/// Exotic Orchard, Chrome Mox. Goldfish opponents have no lands, so a land an opponent controls is
/// assumed able to produce every color, as in a typical multiplayer game.
/// </summary>
sealed class ManaReflectedEffect : Effect
{
    public override string Api => "ManaReflected";
    public override IReadOnlySet<string> Params { get; } = P("ColorOrType", "Valid", "ReflectProperty", "Defined");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        if (ab["ReflectProperty"] is not ("Is" or "Produce" or "Produced")) ctx.Need("ManaReflected.ReflectProperty:" + ab["ReflectProperty"]);
        if (ab["Valid"] is { } v && v != "Defined.Imprinted") Filter.Parse(v, ctx);
        Defined.ValidatePlayers(ab["Defined"], ctx);
    }

    public override void Resolve(EffectContext x)
    {
        var mask = Game.ReflectedMask(x.Game, x.Host, x.Ability);
        if (mask == 0) return;
        var color = System.Numerics.BitOperations.PopCount((uint)mask) == 1
            ? ManaColors.All.First(c => c.Mask() == mask)
            : x.Game.Decider.ChooseColor(x.Game, x.Controller, x.Host, mask);
        x.S.Players[x.Ability["Defined"] is { } d ? Defined.Players(d, x).FirstOrDefault(x.Controller) : x.Controller].Pool.Add(color);
    }
}

sealed class ManaEffect : Effect
{
    public override string Api => "Mana";
    public override IReadOnlySet<string> Params { get; } =
        P(new[] { "Produced", "Amount", "ActivationZone", "RestrictValid", "Defined" }.Concat(Presence.Params).ToArray());

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        ValidateAmount(ab, "Amount", ctx);
        Defined.ValidatePlayers(ab["Defined"], ctx);
        Presence.Validate(ab.P, ctx);
        if (ab.P.Values.Keys.Any(Presence.Params.Contains) && ab.Kind != AbilityKind.Activated) ctx.Need("Mana.IsPresent+NotActivated");
        if (ab["RestrictValid"] is { } rv)
        {
            // Restricted mana is only ever spent automatically, on a payment it's allowed for.
            if (ab.Kind != AbilityKind.Activated || !ab.Cost.Mana.IsZero || ab.Cost.Untap) ctx.Need("Mana.RestrictValid+Manual");
            ManaRestriction.Validate(rv, ctx);
        }
        var produced = ab["Produced"];
        if (produced is "Combo ColorIdentity" or "Chosen") return;
        if (Game.ProducedMask(produced) is null && FixedMana(produced) is null) ctx.Need("ManaProduced:" + produced);
    }

    public override void Resolve(EffectContext x)
    {
        var amount = Amount(x, x.Ability["Amount"], 1);
        var produced = x.Ability["Produced"]!;
        // Defined$: who gets the mana (e.g. the player who tapped a land, for TapsForMana triggers).
        var p = x.S.Players[x.Ability["Defined"] is { } d ? Defined.Players(d, x).FirstOrDefault(x.Controller) : x.Controller];
        if (FixedMana(produced) is { } several)   // "U B": one of each
        {
            foreach (var c in several) p.Pool.Add(c, amount);
            return;
        }
        var mask = produced switch
        {
            "Combo ColorIdentity" => x.Game.CommanderIdentity(x.Controller),
            "Chosen" => x.Host.ChosenColor?.Mask() ?? 0,
            _ => Game.ProducedMask(produced)!.Value,
        };
        if (mask == 0) return;
        var combo = produced.StartsWith("Combo", StringComparison.Ordinal);
        if (!combo)
        {
            // "Any" = one color chosen for all of it (e.g. Lion's Eye Diamond).
            var color = Single(mask) ?? x.Game.Decider.ChooseColor(x.Game, x.Controller, x.Host, mask);
            p.Pool.Add(color, amount);
        }
        else
            for (var i = 0; i < amount; i++)
                p.Pool.Add(Single(mask) ?? x.Game.Decider.ChooseColor(x.Game, x.Controller, x.Host, mask));
    }

    /// <summary>"U B" or "C C": a fixed list of symbols. Null for anything else.</summary>
    public static List<ManaColor>? FixedMana(string? produced)
    {
        var words = produced?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words is null || words.Length < 2) return null;
        var list = new List<ManaColor>();
        foreach (var w in words)
        {
            if (w.Length != 1 || ManaColors.FromLetter(w[0]) is not { } c) return null;
            list.Add(c);
        }
        return list;
    }

    private static ManaColor? Single(ColorMask m) =>
        System.Numerics.BitOperations.PopCount((uint)m) == 1 ? ManaColors.All.First(c => c.Mask() == m) : null;
}

// ---- cards and zones -------------------------------------------------------------------

sealed class DrawEffect : Effect
{
    public override string Api => "Draw";
    public override IReadOnlySet<string> Params { get; } = P("NumCards", "Defined");
    public override void Validate(AbilityDef ab, CompileContext ctx) { ValidateAmount(ab, "NumCards", ctx); Defined.ValidatePlayers(ab["Defined"], ctx); }

    public override void Resolve(EffectContext x)
    {
        var n = Amount(x, x.Ability["NumCards"], 1);
        foreach (var p in Players(x)) x.Game.Draw(p, n);
    }

    public static List<int> Players(EffectContext x) =>
        x.Ability.TargetFilter is not null && x.Ability["Defined"] is null
            ? Defined.Players("Targeted", x)
            : Defined.Players(x.Ability["Defined"], x);
}

sealed class ChangeZoneEffect : Effect
{
    public override string Api => "ChangeZone";
    public override IReadOnlySet<string> Params { get; } = P(
        "Origin", "Destination", "ChangeType", "ChangeNum", "Hidden", "Mandatory", "Tapped", "LibraryPosition", "Defined",
        "ChangeTypeDesc", "Shuffle", "Reveal", "SelectPrompt", "Reorder", "Optional", "NoReveal", "ShuffleNonMandatory",
        "DefinedPlayer", "NoShuffle", "RememberChanged", "Imprint", "GainControl", "WithCountersType", "WithCountersAmount", "RememberTargets", "Duration", "TargetsWithDefinedController");

    private static readonly HashSet<string> Zones = new() { "Library", "Hand", "Battlefield", "Graveyard", "Exile" };

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        foreach (var key in new[] { "Origin", "Destination" })
            if (ab[key] is { } z && !z.Split(',').All(s => Zones.Contains(s.Trim()) || key == "Origin" && s.Trim() is "Stack" or "All" && (ab["Defined"] is not null || ab.TargetFilter is not null)))
                ctx.Need($"ChangeZone.{key}:{z}");
        ValidateAmount(ab, "WithCountersAmount", ctx);
        if (ab["Duration"] is { } dur && dur != "UntilHostLeavesPlay") ctx.Need("ChangeZone.Duration:" + dur);
        if (ab["Destination"] is null) ctx.Need("ChangeZone.Destination:none");
        if (ab["ChangeType"] is { } ct) Filter.Parse(ct, ctx);
        if (ab["ChangeNum"] is { } cn && cn != "All") Engine.Amount.Parse(cn, ctx);
        Defined.ValidateCards(ab["Defined"], ctx);
        Defined.ValidatePlayers(ab["DefinedPlayer"], ctx);
    }

    /// <summary>RememberChanged$ / Imprint$: note the moved card on the source.</summary>
    /// <summary>Moves a card for this ability: under the ability's controller (GainControl$), with counters (WithCountersType$).</summary>
    private static void Move(EffectContext x, Card c, Zone dest, int position, bool tapped)
    {
        var ab = x.Ability;
        if (c.Zone == Zone.Stack)
        {
            // A spell leaves the stack (it isn't countered, but it won't resolve either).
            if (x.S.Stack.FirstOrDefault(s => s.IsSpell && s.SourceId == c.Id) is { } item) x.S.Stack.Remove(item);
            else return;
        }
        if (dest == Zone.Battlefield && c.Zone != Zone.Battlefield)
        {
            if (ab["GainControl"] == "True") c.Controller = x.Controller;
            if (ab["WithCountersType"] is { } kind) Game.AddCounters(c, kind, Amount(x, ab["WithCountersAmount"], 1));
        }
        x.Game.MoveTo(c, dest, position, tapped);
    }

    public static void Note(EffectContext x, int id)
    {
        if (x.Ability["RememberChanged"] == "True") (x.Host.Remembered ??= new()).Add(id);
        if (x.Ability["Imprint"] == "True") (x.Host.Imprinted ??= new()).Add(id);
    }

    public override void Resolve(EffectContext x)
    {
        var ab = x.Ability;
        var dest = Enum.Parse<Zone>(ab["Destination"]!);
        var origins = ab["Origin"] == "All"
            ? new List<Zone> { Zone.Battlefield, Zone.Graveyard, Zone.Exile, Zone.Hand, Zone.Library, Zone.Stack, Zone.Command }
            : (ab["Origin"] ?? "Battlefield").Split(',').Select(s => Enum.Parse<Zone>(s.Trim())).ToList();
        var position = ab["LibraryPosition"] is { } lp ? int.Parse(lp) : 0;
        var tapped = ab["Tapped"] == "True";

        // Known cards (Defined$ or targets): just move them.
        if (ab["Defined"] is not null || ab.TargetFilter is not null)
        {
            var ids = ab["Defined"] is { } d ? Defined.Cards(d, x) : Defined.Cards("Targeted", x);
            if (ab["RememberTargets"] == "True") (x.Host.Remembered ??= new()).AddRange(Defined.Cards("Targeted", x));
            foreach (var id in ids)
            {
                var c = x.S[id];
                if (!origins.Contains(c.Zone)) continue;
                if (ab["Optional"] == "True" && !x.Game.Decider.ChooseYesNo(x.Game, x.Controller, x.Host, $"move {c.Name}?")) continue;
                var from = c.Zone;
                Move(x, c, dest, position, tapped);
                Note(x, id);
                // "… until this leaves the battlefield" (Banisher Priest, Oblivion Ring-style).
                if (ab["Duration"] == "UntilHostLeavesPlay" && from == Zone.Battlefield && c.Zone == dest)
                    x.S.UntilHostLeaves.Add((c.Id, x.Host.Id));
            }
            return;
        }

        // Hidden or chosen-from-zone: the searching player chooses from their own cards in the origin zones.
        var searchers = ab["DefinedPlayer"] is { } dp ? Defined.Players(dp, x) : new List<int> { x.Controller };
        foreach (var searcher in searchers)
            if (!x.S.Players[searcher].IsGoldfish) Search(x, searcher, origins, dest, position, tapped);
    }

    private void Search(EffectContext x, int searcher, List<Zone> origins, Zone dest, int position, bool tapped)
    {
        var ab = x.Ability;
        var filter = ab["ChangeType"] is { } ct ? FilterFor(x.SVarFace, ct) : null;
        var candidates = origins.SelectMany(z => x.S.ZoneList(searcher, z))
            .Where(id => filter is null || filter.MatchesCard(x.S[id], x)).ToList();
        var num = ab["ChangeNum"] is "All" ? candidates.Count : Amount(x, ab["ChangeNum"], 1);
        var mandatory = ab["Optional"] != "True" && (ab["Mandatory"] == "True" || origins.Contains(Zone.Hand) && ab["Hidden"] is null);
        var max = Math.Min(num, candidates.Count);
        var min = mandatory ? max : 0;
        var kind = origins.Contains(Zone.Library) ? Choice.Tutor : Choice.Generic;
        var chosen = max == 0 ? new List<int>() : x.Game.Decider.ChooseCards(x.Game, searcher, kind, x.Host, candidates, min, max);
        if (kind == Choice.Tutor) x.Game.Note($"  search: {(chosen.Count == 0 ? "nothing" : string.Join(", ", chosen.Select(id => x.S[id].Name)))}");
        foreach (var id in chosen)
        {
            Move(x, x.S[id], dest, position, tapped);
            Note(x, id);
        }
        if (origins.Contains(Zone.Library) && ab["Shuffle"] != "False" && ab["NoShuffle"] != "True") x.Game.Shuffle(searcher);
    }
}

sealed class DigEffect : Effect
{
    public override string Api => "Dig";
    public override IReadOnlySet<string> Params { get; } = P(
        "DigNum", "ChangeNum", "DestinationZone", "DestinationZone2", "LibraryPosition", "LibraryPosition2", "RestRandomOrder",
        "ChangeValid", "Optional", "AnyNumber", "Reveal", "Defined", "NoMove", "SkipReorder", "ForceRevealToController",
        "RememberChanged", "Imprint", "NoReveal");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        ValidateAmount(ab, "DigNum", ctx);
        if (ab["ChangeNum"] is { } cn && cn is not ("All" or "Any")) Engine.Amount.Parse(cn, ctx);
        if (ab["ChangeValid"] is { } cv) Filter.Parse(cv, ctx);
        foreach (var key in new[] { "DestinationZone", "DestinationZone2" })
            if (ab[key] is { } z && z is not ("Hand" or "Library" or "Graveyard" or "Exile" or "Battlefield")) ctx.Need($"Dig.{key}:{z}");
        Defined.ValidatePlayers(ab["Defined"], ctx);
        if (ab["NoMove"] is not null) ctx.Need("Dig.NoMove");
    }

    public override void Resolve(EffectContext x)
    {
        var ab = x.Ability;
        var player = Defined.Players(ab["Defined"], x)[0];
        var lib = x.S.Players[player].Library;
        var n = Math.Min(Amount(x, ab["DigNum"], 1), lib.Count);
        var looked = lib.Take(n).ToList();
        var filter = ab["ChangeValid"] is { } cv ? FilterFor(x.SVarFace, cv) : null;
        var eligible = looked.Where(id => filter is null || filter.MatchesCard(x.S[id], x)).ToList();
        var num = ab["ChangeNum"] switch { "All" => eligible.Count, "Any" => eligible.Count, var s => Amount(x, s, 1) };
        var max = Math.Min(num, eligible.Count);
        var min = ab["Optional"] == "True" || ab["AnyNumber"] == "True" || ab["ChangeNum"] == "Any" ? 0 : max;
        var chosen = max == 0 ? new List<int>() : ab["ChangeNum"] == "All" ? eligible
            : x.Game.Decider.ChooseCards(x.Game, x.Controller, Choice.ChooseFromRevealed, x.Host, eligible, min, max);

        var dest = Enum.Parse<Zone>(ab["DestinationZone"] ?? "Hand");
        var pos = ab["LibraryPosition"] is { } lp ? int.Parse(lp) : -1;
        foreach (var id in chosen)
        {
            x.Game.MoveTo(x.S[id], dest, pos);
            ChangeZoneEffect.Note(x, id);
        }

        var rest = looked.Except(chosen).ToList();
        if (ab["RestRandomOrder"] == "True") x.S.Rng.Shuffle(rest);
        var dest2 = Enum.Parse<Zone>(ab["DestinationZone2"] ?? "Library");
        var pos2 = ab["LibraryPosition2"] is { } lp2 ? int.Parse(lp2) : -1;
        foreach (var id in rest) x.Game.MoveTo(x.S[id], dest2, pos2);
    }
}

sealed class DigUntilEffect : Effect
{
    public override string Api => "DigUntil";
    public override IReadOnlySet<string> Params { get; } = P(
        "Valid", "ValidDescription", "FoundDestination", "RevealedDestination", "Amount", "Defined", "Shuffle");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        if (ab["Valid"] is { } v) Filter.Parse(v, ctx); else ctx.Need("DigUntil.Valid:none");
        ValidateAmount(ab, "Amount", ctx);
        Defined.ValidatePlayers(ab["Defined"], ctx);
    }

    public override void Resolve(EffectContext x)
    {
        var ab = x.Ability;
        var player = Defined.Players(ab["Defined"], x)[0];
        var filter = FilterFor(x.SVarFace, ab["Valid"]!);
        var want = Amount(x, ab["Amount"], 1);
        var found = new List<int>();
        var revealed = new List<int>();
        var lib = x.S.Players[player].Library;
        foreach (var id in lib.ToList())
        {
            if (found.Count >= want) break;
            if (filter.MatchesCard(x.S[id], x)) found.Add(id); else revealed.Add(id);
        }
        var foundDest = Enum.Parse<Zone>(ab["FoundDestination"] ?? "Hand");
        var revealedDest = Enum.Parse<Zone>(ab["RevealedDestination"] ?? "Graveyard");
        foreach (var id in found) x.Game.MoveTo(x.S[id], foundDest);
        foreach (var id in revealed) x.Game.MoveTo(x.S[id], revealedDest, -1);
        x.Game.Note($"  revealed {revealed.Count}, found {(found.Count == 0 ? "nothing" : string.Join(", ", found.Select(id => x.S[id].Name)))}");
        if (ab["Shuffle"] == "True") x.Game.Shuffle(player);
    }
}

sealed class MillEffect : Effect
{
    public override string Api => "Mill";
    public override IReadOnlySet<string> Params { get; } = P("NumCards", "Defined", "RememberMilled");
    public override void Validate(AbilityDef ab, CompileContext ctx) { ValidateAmount(ab, "NumCards", ctx); Defined.ValidatePlayers(ab["Defined"], ctx); }

    public override void Resolve(EffectContext x)
    {
        var n = Amount(x, x.Ability["NumCards"], 1);
        foreach (var p in DrawEffect.Players(x))
        {
            var milled = x.Game.Mill(p, n);
            if (x.Ability["RememberMilled"] == "True") (x.Host.Remembered ??= new()).AddRange(milled);
            if (p != x.Controller) x.Game.RememberWinCard(x.Host);
        }
    }
}

sealed class DiscardEffect : Effect
{
    public override string Api => "Discard";
    public override IReadOnlySet<string> Params { get; } = P("Mode", "NumCards", "Defined", "DiscardValid");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        if (ab["Mode"] is { } m && m is not ("TgtChoose" or "Hand" or "Random")) ctx.Need("Discard.Mode:" + m);
        ValidateAmount(ab, "NumCards", ctx);
        Defined.ValidatePlayers(ab["Defined"], ctx);
        if (ab["DiscardValid"] is { } dv) Filter.Parse(dv, ctx);
    }

    public override void Resolve(EffectContext x)
    {
        foreach (var p in DrawEffect.Players(x))
        {
            var hand = x.S.Players[p].Hand;
            if (x.S.Players[p].IsGoldfish) continue;   // goldfish opponents have no real hand
            var mode = x.Ability["Mode"] ?? "TgtChoose";
            var n = Math.Min(Amount(x, x.Ability["NumCards"], 1), hand.Count);
            List<int> chosen = mode switch
            {
                "Hand" => hand.ToList(),
                "Random" => Enumerable.Range(0, n).Select(_ => hand[x.S.Rng.Next(hand.Count)]).Distinct().ToList(),
                _ => x.Game.Decider.ChooseCards(x.Game, p, Choice.Discard, x.Host, hand.ToList(), n, n),
            };
            foreach (var id in chosen) x.Game.Discard(x.S[id]);
        }
    }
}

sealed class ShuffleEffect : Effect
{
    public override string Api => "Shuffle";
    public override IReadOnlySet<string> Params { get; } = P("Defined");
    public override void Validate(AbilityDef ab, CompileContext ctx) => Defined.ValidatePlayers(ab["Defined"], ctx);
    public override void Resolve(EffectContext x) { foreach (var p in Defined.Players(x.Ability["Defined"], x)) x.Game.Shuffle(p); }
}

sealed class ScryEffect : Effect
{
    public override string Api => "Scry";
    public override IReadOnlySet<string> Params { get; } = P("ScryNum", "Defined");
    public override void Validate(AbilityDef ab, CompileContext ctx) { ValidateAmount(ab, "ScryNum", ctx); Defined.ValidatePlayers(ab["Defined"], ctx); }

    public override void Resolve(EffectContext x)
    {
        var n = Amount(x, x.Ability["ScryNum"], 1);
        var p = x.S.Players[x.Controller];
        var top = p.Library.Take(n).ToList();
        if (top.Count == 0) return;
        p.KnownTop = Math.Max(p.KnownTop, top.Count);   // looked at; cards put on the bottom stop counting
        var bottom = x.Game.Decider.ChooseCards(x.Game, x.Controller, Choice.PutOnBottom, x.Host, top, 0, top.Count);
        foreach (var id in bottom) x.Game.MoveTo(x.S[id], Zone.Library, -1);
        x.Game.Note($"  scry {n}: bottom {bottom.Count}");
    }
}

sealed class RevealHandEffect : Effect
{
    // Looking at a goldfish opponent's hand does nothing; the effect exists so cards like
    // Gitaxian Probe can resolve their other parts.
    public override string Api => "RevealHand";
    public override IReadOnlySet<string> Params { get; } = P("Look", "Defined");
    public override void Resolve(EffectContext x) { }
}

sealed class CleanupEffect : Effect
{
    public override string Api => "Cleanup";
    public override IReadOnlySet<string> Params { get; } = P("ClearRemembered", "ClearNamedCard", "ClearImprinted");

    public override void Resolve(EffectContext x)
    {
        if (x.Ability["ClearRemembered"] == "True") x.Host.Remembered = null;
        if (x.Ability["ClearImprinted"] == "True") x.Host.Imprinted = null;
        if (x.Ability["ClearNamedCard"] == "True") x.Host.NamedCard = null;
    }
}

sealed class NameCardEffect : Effect
{
    public override string Api => "NameCard";
    public override IReadOnlySet<string> Params { get; } = P("Defined");
    public override void Validate(AbilityDef ab, CompileContext ctx) => Defined.ValidatePlayers(ab["Defined"], ctx);

    public override void Resolve(EffectContext x)
    {
        x.Host.NamedCard = x.Game.Decider.ChooseCardName(x.Game, x.Controller, x.Host);
        x.Game.Note($"  named \"{x.Host.NamedCard}\"");
    }
}

// ---- life, damage, winning ---------------------------------------------------------------

sealed class DealDamageEffect : Effect
{
    public override string Api => "DealDamage";
    public override IReadOnlySet<string> Params { get; } = P("NumDmg", "Defined", "DividedAsYouChoose");
    public override void Validate(AbilityDef ab, CompileContext ctx) { ValidateAmount(ab, "NumDmg", ctx); Defined.ValidatePlayers(ab["Defined"], ctx); }

    public override void Resolve(EffectContext x)
    {
        var n = Amount(x, x.Ability["NumDmg"], 0);
        var targets = x.Ability["Defined"] is { } d
            ? Defined.Players(d, x).Select(TargetRef.OfPlayer).ToList()
            : x.Targets;
        // "N damage divided as you choose": one to each target, the rest to the first (players first).
        var divided = x.Ability["DividedAsYouChoose"] is not null && targets.Count > 0;
        var order = divided ? targets.OrderBy(t => t.Player is null).ToList() : targets;
        for (var i = 0; i < order.Count; i++)
        {
            var t = order[i];
            var dmg = !divided ? n : i == 0 ? n - (order.Count - 1) : 1;
            if (dmg <= 0) continue;
            if (t.Player is { } p) x.Game.DamagePlayer(p, dmg, x.Host);
            else if (t.CardId is { } id && x.S[id].Zone == Zone.Battlefield) x.Game.DamageCard(x.S[id], dmg, x.Host);
        }
    }
}

sealed class LoseLifeEffect : Effect
{
    public override string Api => "LoseLife";
    public override IReadOnlySet<string> Params { get; } = P("LifeAmount", "Defined");
    public override void Validate(AbilityDef ab, CompileContext ctx) { ValidateAmount(ab, "LifeAmount", ctx); Defined.ValidatePlayers(ab["Defined"], ctx); }

    public override void Resolve(EffectContext x)
    {
        var n = Amount(x, x.Ability["LifeAmount"], 1);
        foreach (var p in DrawEffect.Players(x)) x.Game.LoseLife(p, n, x.Host);
    }
}

sealed class GainLifeEffect : Effect
{
    public override string Api => "GainLife";
    public override IReadOnlySet<string> Params { get; } = P("LifeAmount", "Defined");
    public override void Validate(AbilityDef ab, CompileContext ctx) { ValidateAmount(ab, "LifeAmount", ctx); Defined.ValidatePlayers(ab["Defined"], ctx); }

    public override void Resolve(EffectContext x)
    {
        var n = Amount(x, x.Ability["LifeAmount"], 1);
        foreach (var p in DrawEffect.Players(x)) x.Game.GainLife(p, n);
    }
}

sealed class WinsGameEffect : Effect
{
    public override string Api => "WinsGame";
    public override IReadOnlySet<string> Params { get; } = P("Defined");
    public override void Validate(AbilityDef ab, CompileContext ctx) => Defined.ValidatePlayers(ab["Defined"], ctx);

    public override void Resolve(EffectContext x)
    {
        var p = Defined.Players(x.Ability["Defined"], x)[0];
        x.Game.RememberWinCard(x.Host);
        x.Game.Win(p, $"{x.Host.Name}");
    }
}

sealed class LosesGameEffect : Effect
{
    public override string Api => "LosesGame";
    public override IReadOnlySet<string> Params { get; } = P("Defined");
    public override void Validate(AbilityDef ab, CompileContext ctx) => Defined.ValidatePlayers(ab["Defined"], ctx);

    public override void Resolve(EffectContext x)
    {
        foreach (var p in Defined.Players(x.Ability["Defined"], x))
        {
            x.S.Players[p].Life = Math.Min(x.S.Players[p].Life, 0);
            x.S.Players[p].Lost = true;
            if (p == Game.Me) { x.S.GameOver = true; x.S.Winner = null; }
            else x.Game.RememberWinCard(x.Host);
        }
    }
}

// ---- permanents ------------------------------------------------------------------------

abstract class CardsEffect : Effect
{
    /// <summary>Defined$ cards, else the ability's targets, else the source.</summary>
    protected static List<Card> Cards(EffectContext x) =>
        (x.Ability["Defined"] is { } d ? Defined.Cards(d, x)
            : x.Ability.TargetFilter is not null ? Defined.Cards("Targeted", x)
            : Defined.Cards("Self", x))
        .Select(id => x.S[id]).Where(c => c.Zone == Zone.Battlefield).ToList();

    public override void Validate(AbilityDef ab, CompileContext ctx) => Defined.ValidateCards(ab["Defined"], ctx);
}

sealed class PumpEffect : CardsEffect
{
    public override string Api => "Pump";
    public override IReadOnlySet<string> Params { get; } = P("NumAtt", "NumDef", "KW", "Defined", "RememberObjects");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        base.Validate(ab, ctx);
        if (ab["NumAtt"] != "Double") ValidateAmount(ab, "NumAtt", ctx);
        if (ab["NumDef"] != "Double") ValidateAmount(ab, "NumDef", ctx);
        ValidateKeywords(ab["KW"], ctx);
        Defined.ValidateCards(ab["RememberObjects"], ctx);
    }

    public static void ValidateKeywords(string? kw, CompileContext ctx)
    {
        if (kw is null) return;
        foreach (var k in kw.Split('&', StringSplitOptions.TrimEntries))
            if (!Keywords.Supported.Contains(Forge.ForgeRequirements.KeywordName(k))) ctx.Need("Keyword:" + Forge.ForgeRequirements.KeywordName(k));
    }

    public static void Apply(Card c, int att, int def, string? kw)
    {
        c.PowerBonus += att;
        c.ToughnessBonus += def;
        if (kw is null) return;
        c.TempKeywords ??= new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in kw.Split('&', StringSplitOptions.TrimEntries)) c.TempKeywords.Add(Forge.ForgeRequirements.KeywordName(k));
    }

    public override void Resolve(EffectContext x)
    {
        // "Double": gets +X/+0 where X is its power ("double its power").
        var doubleAtt = x.Ability["NumAtt"] == "Double";
        var doubleDef = x.Ability["NumDef"] == "Double";
        var att = doubleAtt ? 0 : Amount(x, x.Ability["NumAtt"], 0);
        var def = doubleDef ? 0 : Amount(x, x.Ability["NumDef"], 0);
        // "Pump" with only RememberObjects$ is Forge's way to remember cards on the source.
        if (x.Ability["RememberObjects"] is { } ro) (x.Host.Remembered ??= new()).AddRange(Defined.Cards(ro, x));
        if (x.Ability["Defined"] is null && x.Ability.TargetFilter is null && x.Ability["RememberObjects"] is not null) return;
        foreach (var c in Cards(x)) Apply(c, doubleAtt ? Math.Max(0, c.Power) : att, doubleDef ? Math.Max(0, c.Toughness) : def, x.Ability["KW"]);
    }
}

sealed class UntapEffect : CardsEffect
{
    public override string Api => "Untap";
    public override IReadOnlySet<string> Params { get; } = P("Defined");
    public override void Resolve(EffectContext x) { foreach (var c in Cards(x)) c.Tapped = false; }
}

sealed class TapEffect : CardsEffect
{
    public override string Api => "Tap";
    public override IReadOnlySet<string> Params { get; } = P("Defined");
    public override void Resolve(EffectContext x) { foreach (var c in Cards(x)) c.Tapped = true; }
}

sealed class PutCounterEffect : CardsEffect
{
    public override string Api => "PutCounter";
    public override IReadOnlySet<string> Params { get; } = P("CounterType", "CounterNum", "Defined");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        base.Validate(ab, ctx);
        ValidateAmount(ab, "CounterNum", ctx);
        if (ab["CounterType"] is null) ctx.Need("PutCounter.CounterType:none");
    }

    public override void Resolve(EffectContext x)
    {
        var n = Amount(x, x.Ability["CounterNum"], 1);
        var kind = x.Ability["CounterType"]!;
        foreach (var c in Cards(x)) x.Game.PutCounters(c, kind, n);
    }
}

sealed class DestroyEffect : CardsEffect
{
    public override string Api => "Destroy";
    public override IReadOnlySet<string> Params { get; } = P("Defined", "NoRegen");
    public override void Resolve(EffectContext x)
    {
        foreach (var c in Cards(x)) x.Game.Destroy(c, regenerate: x.Ability["NoRegen"] != "True");
    }
}

sealed class SacrificeEffect : Effect
{
    public override string Api => "Sacrifice";
    public override IReadOnlySet<string> Params { get; } = P("SacValid", "Amount", "Defined", "RememberSacrificed", "Optional", "SacMessage");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        if (ab["SacValid"] is { } sv && sv != "Self") Filter.Parse(sv, ctx);
        ValidateAmount(ab, "Amount", ctx);
        Defined.ValidatePlayers(ab["Defined"], ctx);
    }

    public override void Resolve(EffectContext x)
    {
        var valid = x.Ability["SacValid"] ?? "Self";
        var remember = x.Ability["RememberSacrificed"] == "True";
        if (valid == "Self")
        {
            if (x.Host.Zone != Zone.Battlefield) return;
            if (x.Ability["Optional"] == "True" && !x.Game.Decider.ChooseYesNo(x.Game, x.Controller, x.Host, $"sacrifice {x.Host.Name}?")) return;
            if (remember) (x.Host.Remembered ??= new()).Add(x.Host.Id);
            x.Game.Sacrifice(x.Host);
            return;
        }
        var filter = FilterFor(x.SVarFace, valid);
        foreach (var p in Defined.Players(x.Ability["Defined"], x))
        {
            if (x.S.Players[p].IsGoldfish) continue;
            var candidates = x.S.BattlefieldCards.Where(c => c.Controller == p && filter.MatchesCard(c, x)).Select(c => c.Id).ToList();
            var n = Math.Min(Amount(x, x.Ability["Amount"], 1), candidates.Count);
            var min = x.Ability["Optional"] == "True" ? 0 : n;
            foreach (var id in x.Game.Decider.ChooseCards(x.Game, p, Choice.Sacrifice, x.Host, candidates, min, n))
            {
                if (remember) (x.Host.Remembered ??= new()).Add(id);
                x.Game.Sacrifice(x.S[id]);
            }
        }
    }
}
