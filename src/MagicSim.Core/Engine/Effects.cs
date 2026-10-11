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

    protected static int Amount(EffectContext x, string? text, int fallback)
    {
        if (text is null) return fallback;
        var face = x.Host.CurrentFace;
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
        new ShuffleEffect(), new RevealHandEffect(),
    }.ToDictionary(e => e.Api, StringComparer.Ordinal);

    public static Effect? Get(string api) => All.TryGetValue(api, out var e) ? e : null;
    public static IEnumerable<string> Apis => All.Keys;
}

// ---- mana ------------------------------------------------------------------------------

sealed class ManaEffect : Effect
{
    public override string Api => "Mana";
    public override IReadOnlySet<string> Params { get; } = P("Produced", "Amount", "ActivationZone");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        ValidateAmount(ab, "Amount", ctx);
        var produced = ab["Produced"];
        if (produced is "Combo ColorIdentity") return;
        if (Game.ProducedMask(produced) is null) ctx.Need("ManaProduced:" + produced);
    }

    public override void Resolve(EffectContext x)
    {
        var amount = Amount(x, x.Ability["Amount"], 1);
        var produced = x.Ability["Produced"]!;
        var mask = produced == "Combo ColorIdentity" ? x.Game.CommanderIdentity(x.Controller) : Game.ProducedMask(produced)!.Value;
        if (mask == 0) return;
        var p = x.S.Players[x.Controller];
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
        "ChangeTypeDesc", "Shuffle", "Reveal", "SelectPrompt", "Reorder", "Optional", "NoReveal");

    private static readonly HashSet<string> Zones = new() { "Library", "Hand", "Battlefield", "Graveyard", "Exile" };

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        foreach (var key in new[] { "Origin", "Destination" })
            if (ab[key] is { } z && !z.Split(',').All(s => Zones.Contains(s.Trim()))) ctx.Need($"ChangeZone.{key}:{z}");
        if (ab["Destination"] is null) ctx.Need("ChangeZone.Destination:none");
        if (ab["ChangeType"] is { } ct) Filter.Parse(ct, ctx);
        if (ab["ChangeNum"] is { } cn && cn != "All") Engine.Amount.Parse(cn, ctx);
        Defined.ValidateCards(ab["Defined"], ctx);
    }

    public override void Resolve(EffectContext x)
    {
        var ab = x.Ability;
        var dest = Enum.Parse<Zone>(ab["Destination"]!);
        var origins = (ab["Origin"] ?? "Battlefield").Split(',').Select(s => Enum.Parse<Zone>(s.Trim())).ToList();
        var position = ab["LibraryPosition"] is { } lp ? int.Parse(lp) : 0;
        var tapped = ab["Tapped"] == "True";

        // Known cards (Defined$ or targets): just move them.
        if (ab["Defined"] is not null || ab.TargetFilter is not null)
        {
            var ids = ab["Defined"] is { } d ? Defined.Cards(d, x) : Defined.Cards("Targeted", x);
            foreach (var id in ids)
            {
                var c = x.S[id];
                if (!origins.Contains(c.Zone)) continue;
                if (ab["Optional"] == "True" && !x.Game.Decider.ChooseYesNo(x.Game, x.Controller, x.Host, $"move {c.Name}?")) continue;
                x.Game.MoveTo(c, dest, position, tapped);
            }
            return;
        }

        // Hidden or chosen-from-zone: search/choose from the controller's cards in the origin zone.
        var filter = ab["ChangeType"] is { } ct ? FilterFor(x.Host.CurrentFace, ct) : null;
        var candidates = origins.SelectMany(z => x.S.ZoneList(x.Controller, z))
            .Where(id => filter is null || filter.MatchesCard(x.S[id], x)).ToList();
        var num = ab["ChangeNum"] is "All" ? candidates.Count : Amount(x, ab["ChangeNum"], 1);
        var mandatory = ab["Mandatory"] == "True" || origins.Contains(Zone.Hand) && ab["Hidden"] is null;
        var max = Math.Min(num, candidates.Count);
        var min = mandatory ? max : 0;
        var kind = origins.Contains(Zone.Library) ? Choice.Tutor : Choice.Generic;
        var chosen = max == 0 ? new List<int>() : x.Game.Decider.ChooseCards(x.Game, x.Controller, kind, x.Host, candidates, min, max);
        if (kind == Choice.Tutor) x.Game.Note($"  search: {(chosen.Count == 0 ? "nothing" : string.Join(", ", chosen.Select(id => x.S[id].Name)))}");
        foreach (var id in chosen) x.Game.MoveTo(x.S[id], dest, position, tapped);
        if (origins.Contains(Zone.Library) && ab["Shuffle"] != "False") x.Game.Shuffle(x.Controller);
    }
}

sealed class DigEffect : Effect
{
    public override string Api => "Dig";
    public override IReadOnlySet<string> Params { get; } = P(
        "DigNum", "ChangeNum", "DestinationZone", "DestinationZone2", "LibraryPosition", "LibraryPosition2", "RestRandomOrder",
        "ChangeValid", "Optional", "AnyNumber", "Reveal", "Defined", "NoMove", "SkipReorder", "ForceRevealToController");

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
        var filter = ab["ChangeValid"] is { } cv ? FilterFor(x.Host.CurrentFace, cv) : null;
        var eligible = looked.Where(id => filter is null || filter.MatchesCard(x.S[id], x)).ToList();
        var num = ab["ChangeNum"] switch { "All" => eligible.Count, "Any" => eligible.Count, var s => Amount(x, s, 1) };
        var max = Math.Min(num, eligible.Count);
        var min = ab["Optional"] == "True" || ab["AnyNumber"] == "True" || ab["ChangeNum"] == "Any" ? 0 : max;
        var chosen = max == 0 ? new List<int>() : ab["ChangeNum"] == "All" ? eligible
            : x.Game.Decider.ChooseCards(x.Game, x.Controller, Choice.ChooseFromRevealed, x.Host, eligible, min, max);

        var dest = Enum.Parse<Zone>(ab["DestinationZone"] ?? "Hand");
        var pos = ab["LibraryPosition"] is { } lp ? int.Parse(lp) : -1;
        foreach (var id in chosen) x.Game.MoveTo(x.S[id], dest, pos);

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
        var filter = FilterFor(x.Host.CurrentFace, ab["Valid"]!);
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
    public override IReadOnlySet<string> Params { get; } = P("NumCards", "Defined");
    public override void Validate(AbilityDef ab, CompileContext ctx) { ValidateAmount(ab, "NumCards", ctx); Defined.ValidatePlayers(ab["Defined"], ctx); }

    public override void Resolve(EffectContext x)
    {
        var n = Amount(x, x.Ability["NumCards"], 1);
        foreach (var p in DrawEffect.Players(x))
        {
            x.Game.Mill(p, n);
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
        var lib = x.S.Players[x.Controller].Library;
        var top = lib.Take(n).ToList();
        if (top.Count == 0) return;
        var bottom = x.Game.Decider.ChooseCards(x.Game, x.Controller, Choice.PutOnBottom, x.Host, top, 0, top.Count);
        foreach (var id in bottom) x.Game.MoveTo(x.S[id], Zone.Library, -1);
        var p = x.S.Players[x.Controller];
        p.KnownTop = Math.Max(p.KnownTop, top.Count - bottom.Count);
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
    public override IReadOnlySet<string> Params { get; } = P("ClearRemembered", "ClearNamedCard");

    public override void Resolve(EffectContext x)
    {
        if (x.Ability["ClearRemembered"] == "True") x.Host.Remembered = null;
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
    public override IReadOnlySet<string> Params { get; } = P("NumDmg", "Defined");
    public override void Validate(AbilityDef ab, CompileContext ctx) { ValidateAmount(ab, "NumDmg", ctx); Defined.ValidatePlayers(ab["Defined"], ctx); }

    public override void Resolve(EffectContext x)
    {
        var n = Amount(x, x.Ability["NumDmg"], 0);
        var targets = x.Ability["Defined"] is { } d
            ? Defined.Players(d, x).Select(TargetRef.OfPlayer).ToList()
            : x.Targets;
        foreach (var t in targets)
        {
            if (t.Player is { } p) x.Game.DamagePlayer(p, n, x.Host);
            else if (t.CardId is { } id && x.S[id].Zone == Zone.Battlefield) x.S[id].Damage += n;
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
    public override IReadOnlySet<string> Params { get; } = P("NumAtt", "NumDef", "KW", "Defined");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        base.Validate(ab, ctx);
        ValidateAmount(ab, "NumAtt", ctx);
        ValidateAmount(ab, "NumDef", ctx);
        if (ab["KW"] is { } kw)
            foreach (var k in kw.Split('&', StringSplitOptions.TrimEntries))
                if (!Keywords.Supported.Contains(Forge.ForgeRequirements.KeywordName(k))) ctx.Need("Keyword:" + k);
    }

    public override void Resolve(EffectContext x)
    {
        var att = Amount(x, x.Ability["NumAtt"], 0);
        var def = Amount(x, x.Ability["NumDef"], 0);
        foreach (var c in Cards(x))
        {
            c.PowerBonus += att;
            c.ToughnessBonus += def;
            if (x.Ability["KW"] is { } kw)
            {
                c.TempKeywords ??= new HashSet<string>(StringComparer.Ordinal);
                foreach (var k in kw.Split('&', StringSplitOptions.TrimEntries)) c.TempKeywords.Add(Forge.ForgeRequirements.KeywordName(k));
            }
        }
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
        foreach (var c in Cards(x))
        {
            c.Counters ??= new();
            c.Counters[kind] = c.CounterCount(kind) + n;
        }
    }
}

sealed class DestroyEffect : CardsEffect
{
    public override string Api => "Destroy";
    public override IReadOnlySet<string> Params { get; } = P("Defined");
    public override void Resolve(EffectContext x)
    {
        foreach (var c in Cards(x))
            if (!c.HasKeyword("Indestructible")) x.Game.MoveTo(c, Zone.Graveyard);
    }
}

sealed class SacrificeEffect : Effect
{
    public override string Api => "Sacrifice";
    public override IReadOnlySet<string> Params { get; } = P("SacValid", "Amount", "Defined");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        if (ab["SacValid"] is { } sv && sv != "Self") Filter.Parse(sv, ctx);
        ValidateAmount(ab, "Amount", ctx);
        Defined.ValidatePlayers(ab["Defined"], ctx);
    }

    public override void Resolve(EffectContext x)
    {
        var valid = x.Ability["SacValid"] ?? "Self";
        if (valid == "Self")
        {
            if (x.Host.Zone == Zone.Battlefield) x.Game.MoveTo(x.Host, Zone.Graveyard);
            return;
        }
        var filter = FilterFor(x.Host.CurrentFace, valid);
        foreach (var p in Defined.Players(x.Ability["Defined"], x))
        {
            if (x.S.Players[p].IsGoldfish) continue;
            var candidates = x.S.BattlefieldCards.Where(c => c.Controller == p && filter.MatchesCard(c, x)).Select(c => c.Id).ToList();
            var n = Math.Min(Amount(x, x.Ability["Amount"], 1), candidates.Count);
            foreach (var id in x.Game.Decider.ChooseCards(x.Game, p, Choice.Sacrifice, x.Host, candidates, n, n))
                x.Game.MoveTo(x.S[id], Zone.Graveyard);
        }
    }
}
