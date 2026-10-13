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
        new DelayedTriggerEffect(), new RepeatEachEffect(), new TwoPilesEffect(), new CumulativeUpkeepEffect(), new PhasesEffect(), new TapAllEffect(), new PairEffect(), new VoteEffect(), new BecomeMonarchEffect(), new EndTurnEffect(), new CopySpellAbilityEffect(), new PlayEffect(), new GainControlEffect(),
        new AnimateAllEffect(), new FightEffect(), new BranchEffect(), new MultiplyCounterEffect(), new SetStateEffect(),
        new UntapAllEffect(), new RemoveCounterEffect(), new ClassLevelUpEffect(), new ProtectionEffect(), new RollDiceEffect(),
        new ImmediateTriggerEffect(), new SuspendEffect(), new AmassEffect(),
        new PoisonEffect(), new ExploreEffect(), new CascadeEffect(), new DiscoverEffect(), new NinjutsuEffect(), new EarthbendEffect(),
        new MoveCounterEffect(), new GenericChoiceEffect(), new AddPhaseEffect(), new AddTurnEffect(),
        new TapOrUntapAllEffect(), new PeekAndRevealEffect(), new SacrificeAllEffect(), new TurnFaceUpEffect(), new ManifestEffect(), new RevealEffect(), new ChooseSourceEffect(), new ForetellEffect(), new ChooseNumberEffect(), new DamageResolveEffect(), new FogEffect(), new AddOrRemoveCounterEffect(), new ControlPlayerEffect(), new ExchangeControlEffect(), new RemoveFromCombatEffect(), new GoadEffect(), new ChooseCardEffect(), new ExploitEffect(), new ChangeTargetsEffect(), new InvestigateEffect(),
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
        P(new[] { "Produced", "Amount", "ActivationZone", "RestrictValid", "Defined", "PersistentMana",
            // "If that mana is spent on a creature spell, it gains haste": applied when paying (Game.UseManaSource).
            "AddsKeywords", "AddsKeywordsValid",
            // "… it gains haste until end of turn": keywords given this way already last until the turn ends.
            "AddsKeywordsUntil",
            // "That spell can't be countered" (Cavern of Souls): goldfish opponents counter nothing.
            "AddsNoCounter",
            // "When that mana is spent to cast a creature spell …, scry 1": also when paying.
            "TriggersWhenSpent" }.Concat(Presence.Params).ToArray());

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        if (ab["TriggersWhenSpent"] is { } spent)
        {
            if (!ctx.Face.SVars.TryGetValue(spent, out var body)) ctx.Need("SVarMissing:" + spent);
            else
            {
                ab.WhenSpent = CardCompiler.CompileTrigger(new Forge.ForgeParams(body), ctx);
                if (ab.WhenSpent.Mode != "SpellCast") ctx.Need("TriggersWhenSpent:" + ab.WhenSpent.Mode);
            }
        }
        ValidateAmount(ab, "Amount", ctx);
        Defined.ValidatePlayers(ab["Defined"], ctx);
        Presence.Validate(ab.P, ctx);
        if (ab.P.Values.Keys.Any(Presence.Params.Contains) && ab.Kind != AbilityKind.Activated) ctx.Need("Mana.IsPresent+NotActivated");
        if (ab["RestrictValid"] is { } rv)
        {
            // Restricted mana floats apart and is only ever spent automatically, on a payment it's allowed for.
            if (ab.Kind != AbilityKind.Activated) ctx.Need("Mana.RestrictValid+NotActivated");
            ManaRestriction.Validate(rv, ctx);
        }
        var produced = ab["Produced"];
        if (produced is "Combo ColorIdentity" or "Chosen") return;
        if (produced?.StartsWith(EachColorAmong, StringComparison.Ordinal) == true) { Filter.Parse(produced[EachColorAmong.Length..], ctx); return; }
        if (Game.ProducedMask(produced) is null && FixedMana(produced) is null) ctx.Need("ManaProduced:" + produced);
    }

    public override void Resolve(EffectContext x)
    {
        var amount = Amount(x, x.Ability["Amount"], 1);
        if (x.Ability.Kind == AbilityKind.Activated) amount = x.Game.TappedForMana(x.Host, x.Ability, x.Controller, amount);
        var produced = x.Ability["Produced"]!;
        // Defined$: who gets the mana (e.g. the player who tapped a land, for TapsForMana triggers).
        var p = x.S.Players[x.Ability["Defined"] is { } d ? Defined.Players(d, x).FirstOrDefault(x.Controller) : x.Controller];
        if (FixedMana(produced) is { } several)   // "U B": one of each
        {
            foreach (var c in several) p.Pool.Add(c, amount);
            return;
        }
        // "For each color among permanents you control, add one mana of that color" (Bloom Tender).
        if (produced.StartsWith(EachColorAmong, StringComparison.Ordinal))
        {
            var filter = FilterFor(x.Host.CurrentFace, produced[EachColorAmong.Length..]);
            var colors = x.S.BattlefieldCards.Where(c => filter.MatchesCard(c, x)).Aggregate((ColorMask)0, (m, c) => m | c.Colors);
            foreach (var c in ManaColors.All)
                if (c != ManaColor.C && (colors & c.Mask()) != 0) p.Pool.Add(c, amount);
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
            // "Spend this mana only to cast creature spells" (Food Chain): it floats apart from the rest.
            if (x.Ability["RestrictValid"] is { } rv) (p.RestrictedPool ??= new()).Add(new RestrictedMana(x.Host.Id, rv, color, amount));
            else p.Pool.Add(color, amount);
            if (x.Ability["PersistentMana"] == "True") p.PersistentPool.Add(color, amount);
        }
        else
            for (var i = 0; i < amount; i++)
                p.Pool.Add(Single(mask) ?? x.Game.Decider.ChooseColor(x.Game, x.Controller, x.Host, mask));
    }

    private const string EachColorAmong = "Special EachColorAmong_Valid ";

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
    public override IReadOnlySet<string> Params { get; } = P("NumCards", "Defined", "Upto");
    public override void Validate(AbilityDef ab, CompileContext ctx) { ValidateAmount(ab, "NumCards", ctx); Defined.ValidatePlayers(ab["Defined"], ctx); }

    public override void Resolve(EffectContext x)
    {
        var n = Amount(x, x.Ability["NumCards"], 1);
        foreach (var p in Players(x))
        {
            // "Draw up to N": a goldfish opponent draws them all (worse for you); you don't draw yourself out.
            var draw = x.Ability["Upto"] == "True" && !x.S.Players[p].IsGoldfish ? Math.Min(n, Math.Max(0, x.S.Players[p].LibraryCount - 1)) : n;
            x.Game.Draw(p, draw);
        }
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
        "DefinedPlayer", "NoShuffle", "RememberChanged", "Imprint", "GainControl", "OriginAlternative", "AIXMax", "WithCountersType", "WithCountersAmount", "RememberTargets", "ForgetOtherTargets", "Duration", "TargetsWithDefinedController", "RememberLKI",
        // "Any number of creature cards with total mana value 6 or less."
        "WithTotalCMC",
        // Who chooses from another player's hand: you; a goldfish opponent's hand is unknown, so nothing is found.
        "Chooser",
        // Brought back for a while: exiled or sacrificed (or returned to hand) at the next end step, exiled if it would leave.
        "AtEOT", "LeaveBattlefield",
        // "Onto the battlefield tapped and attacking" (Ilharg); "remember only that creature" (Sneak Attack).
        "Attacking", "ForgetOtherRemembered",
        // Face down only hides it from opponents, which goldfish opponents don't care about.
        "ExileFaceDown");

    private static readonly HashSet<string> Zones = new() { "Library", "Hand", "Battlefield", "Graveyard", "Exile" };

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        // "From outside the game" (wishes): a Commander deck has no sideboard, so there's nothing to get.
        foreach (var key in new[] { "Origin", "Destination", "OriginAlternative" })
            if (ab[key] is { } z && !z.Split(',').All(s => Zones.Contains(s.Trim()) || key == "Origin" && s.Trim() == "Sideboard"
                    || key == "Origin" && s.Trim() is "Stack" or "All" && (ab["Defined"] is not null || ab.TargetFilter is not null)
                    // "Exile [this spell]": with nothing defined, the resolving spell itself.
                    || key == "Origin" && z == "Stack" && ab["Defined"] is null && ab.TargetFilter is null
                    // "Exile this effect": an effect object ends itself (Words of Waste).
                    || key == "Origin" && s.Trim() == "Command" && ab["Defined"] == "Self"))
                ctx.Need($"ChangeZone.{key}:{z}");
        ValidateAmount(ab, "WithCountersAmount", ctx);
        if (ab["Duration"] is { } dur && dur != "UntilHostLeavesPlay") ctx.Need("ChangeZone.Duration:" + dur);
        if (ab["Destination"] is null) ctx.Need("ChangeZone.Destination:none");
        if (ab["AtEOT"] is { } eot && eot is not ("Exile" or "Sacrifice" or "Hand")) ctx.Need("ChangeZone.AtEOT:" + eot);
        if (ab["LeaveBattlefield"] is { } lb && lb != "Exile") ctx.Need("ChangeZone.LeaveBattlefield:" + lb);
        // "Search your library for a Forest card and a Plains card": EACH Forest & Plains, one of each.
        if (ab["ChangeType"] is { } each && each.StartsWith("EACH ", StringComparison.Ordinal))
            foreach (var part in each[5..].Split(" & ")) Filter.Parse(part, ctx);
        else if (ab["ChangeType"] is { } ct) Filter.Parse(ct, ctx);
        if (ab["ChangeNum"] is { } cn && cn != "All") Engine.Amount.Parse(cn, ctx);
        if (ab["WithTotalCMC"] is { } total) Engine.Amount.Parse(total, ctx);
        // "Target player searches their library" (Fertilid): a goldfish opponent's library is only a count, so for them nothing is found.
        if (ab["Chooser"] is { } chooser && chooser is not ("You" or "Targeted")) ctx.Need("ChangeZone.Chooser:" + chooser);
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
            // A spell leaves the stack (it isn't countered, but it won't resolve either); or the resolving
            // spell moves itself ("shuffle Green Sun's Zenith into its owner's library").
            if (x.S.Stack.FirstOrDefault(s => s.IsSpell && s.SourceId == c.Id) is { } item) x.S.Stack.Remove(item);
            else if (!(x.Item.IsSpell && x.Item.SourceId == c.Id)) return;
        }
        // The card a replacement is replacing its own entering for (Mox Diamond): it enters, or goes elsewhere instead.
        if (c.Zone == Zone.Battlefield && !x.S.Battlefield.Contains(c.Id))
        {
            if (dest != Zone.Battlefield) c.EnterInstead = dest;
            return;
        }
        if (dest == Zone.Battlefield && c.Zone != Zone.Battlefield)
        {
            if (ab["GainControl"] == "True") c.Controller = x.Controller;
            if (ab["WithCountersType"] is { } kind) Game.AddCounters(c, kind, Amount(x, ab["WithCountersAmount"], 1));
        }
        x.Game.MoveTo(c, dest, position, tapped);
        if (dest == Zone.Exile && c.Zone == Zone.Exile) c.ExiledWith = x.Host.Id;
        if (dest == Zone.Battlefield && c.Zone == Zone.Battlefield)
        {
            if (ab["AtEOT"] is { } eot) x.S.ThisTurn.AtEndStep.Add((c.Id, eot));
            if (ab["LeaveBattlefield"] == "Exile") c.ExileIfLeaves = true;
            // Attacking without having been declared as an attacker (it doesn't trigger "whenever … attacks").
            if (ab["Attacking"] == "True" && x.S.Step == Step.DeclareAttackers) x.S.Attackers.Add(c.Id);
        }
    }

    public static void Note(EffectContext x, int id)
    {
        if (x.Ability["RememberChanged"] == "True" || x.Ability["RememberLKI"] == "True") (x.Host.Remembered ??= new()).Add(id);
        if (x.Ability["Imprint"] == "True") (x.Host.Imprinted ??= new()).Add(id);
    }

    public override void Resolve(EffectContext x)
    {
        var ab = x.Ability;
        var dest = Enum.Parse<Zone>(ab["Destination"]!);
        var origins = ab["Origin"] == "All"
            ? new List<Zone> { Zone.Battlefield, Zone.Graveyard, Zone.Exile, Zone.Hand, Zone.Library, Zone.Stack, Zone.Command }
            : (ab["Origin"] ?? "Battlefield").Split(',').Select(s => s.Trim()).Where(s => s != "Sideboard").Select(Enum.Parse<Zone>).ToList();
        // "Search your library and/or graveyard": both are searched.
        if (ab["OriginAlternative"] is { } alt) origins.AddRange(alt.Split(',').Select(s => Enum.Parse<Zone>(s.Trim())).Where(z => !origins.Contains(z)));
        var position = ab["LibraryPosition"] is { } lp ? int.Parse(lp) : 0;
        var tapped = ab["Tapped"] == "True";
        if (ab["ForgetOtherRemembered"] == "True") { x.Host.Remembered = null; x.Host.RememberedUnknown = 0; }

        // Known cards (Defined$ or targets): just move them. From the stack with neither, the spell itself.
        var self = ab["Origin"] == "Stack" && ab["Defined"] is null && ab.TargetFilter is null;
        if (ab["Defined"] is not null || ab.TargetFilter is not null || self)
        {
            var ids = self ? Defined.Cards("Self", x) : ab["Defined"] is { } d ? Defined.Cards(d, x) : Defined.Cards("Targeted", x);
            if (ab["ForgetOtherTargets"] == "True") x.Host.Remembered = null;
            if (ab["RememberTargets"] == "True") (x.Host.Remembered ??= new()).AddRange(Defined.Cards("Targeted", x));
            foreach (var id in ids)
            {
                var c = x.S[id];
                if (!origins.Contains(c.Zone)) continue;
                if (c.IsEffect) { x.Game.EndEffect(c); continue; }   // an effect object exiling itself just ends
                if (ab["Optional"] == "True" && !x.Game.Decider.ChooseYesNo(x.Game, x.Controller, x.Host, $"move {c.Name}?")) continue;
                var from = c.Zone;
                Move(x, c, dest, position, tapped);
                Note(x, id);
                // "… until this leaves the battlefield" (Banisher Priest, Oblivion Ring-style).
                if (ab["Duration"] == "UntilHostLeavesPlay" && from == Zone.Battlefield && c.Zone == dest)
                    x.S.UntilHostLeaves.Add((c.Id, x.Host.Id));
                if (dest == Zone.Library && ab["Shuffle"] == "True" && c.Zone == Zone.Library) x.Game.Shuffle(c.Owner);
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
        if (ab["ChangeType"] is { } each && each.StartsWith("EACH ", StringComparison.Ordinal))
        {
            SearchEach(x, searcher, origins, dest, position, tapped, each[5..].Split(" & "));
            return;
        }
        var filter = ab["ChangeType"] is { } ct ? FilterFor(x.SVarFace, ct) : null;
        var candidates = origins.SelectMany(z => x.S.ZoneList(searcher, z))
            .Where(id => filter is null || filter.MatchesCard(x.S[id], x)).ToList();
        var num = ab["ChangeNum"] is "All" ? candidates.Count : Amount(x, ab["ChangeNum"], 1);
        var mandatory = ab["Optional"] != "True" && (ab["Mandatory"] == "True" || origins.Contains(Zone.Hand) && ab["Hidden"] is null);
        var max = Math.Min(num, candidates.Count);
        var min = mandatory ? max : 0;
        var kind = origins.Contains(Zone.Library) ? Choice.Tutor : Choice.Generic;
        List<int> chosen;
        if (ab["WithTotalCMC"] is { } total)
        {
            // One at a time, each within what's left of the total.
            chosen = new();
            var left = Amount(x, total, 0);
            while (chosen.Count < max)
            {
                var fits = candidates.Where(id => !chosen.Contains(id) && x.S[id].ManaValue <= left).ToList();
                if (fits.Count == 0) break;
                var pick = x.Game.Decider.ChooseCards(x.Game, searcher, kind, x.Host, fits, 0, 1);
                if (pick.Count == 0) break;
                chosen.Add(pick[0]);
                left -= x.S[pick[0]].ManaValue;
            }
        }
        else chosen = max == 0 ? new List<int>() : x.Game.Decider.ChooseCards(x.Game, searcher, kind, x.Host, candidates, min, max);
        if (kind == Choice.Tutor) x.Game.Note($"  search: {(chosen.Count == 0 ? "nothing" : string.Join(", ", chosen.Select(id => x.S[id].Name)))}");
        var shuffle = origins.Contains(Zone.Library) && ab["Shuffle"] != "False" && ab["NoShuffle"] != "True";
        // "Shuffle and put that card on top" (Vampiric Tutor): the shuffle comes before the card goes back.
        if (shuffle && dest == Zone.Library)
        {
            foreach (var id in chosen)
            {
                x.Game.RemoveFromZone(x.S[id]);
                x.S[id].Zone = Zone.None;
            }
            x.Game.Shuffle(searcher);
            shuffle = false;
        }
        foreach (var id in chosen)
        {
            Move(x, x.S[id], dest, position, tapped);
            Note(x, id);
        }
        if (shuffle) x.Game.Shuffle(searcher);
    }

    /// <summary>"Search your library for a Forest card and a Plains card": up to one card for each filter.</summary>
    private static void SearchEach(EffectContext x, int searcher, List<Zone> origins, Zone dest, int position, bool tapped, string[] filters)
    {
        var chosen = new List<int>();
        foreach (var f in filters)
        {
            var filter = FilterFor(x.SVarFace, f);
            var candidates = origins.SelectMany(z => x.S.ZoneList(searcher, z)).Where(id => !chosen.Contains(id) && filter.MatchesCard(x.S[id], x)).ToList();
            if (candidates.Count > 0) chosen.AddRange(x.Game.Decider.ChooseCards(x.Game, searcher, Choice.Tutor, x.Host, candidates, 0, 1));
        }
        x.Game.Note($"  search: {(chosen.Count == 0 ? "nothing" : string.Join(", ", chosen.Select(id => x.S[id].Name)))}");
        foreach (var id in chosen)
        {
            Move(x, x.S[id], dest, position, tapped);
            Note(x, id);
        }
        if (origins.Contains(Zone.Library)) x.Game.Shuffle(searcher);
    }
}

sealed class DigEffect : Effect
{
    public override string Api => "Dig";
    public override IReadOnlySet<string> Params { get; } = P(
        "DigNum", "ChangeNum", "DestinationZone", "DestinationZone2", "LibraryPosition", "LibraryPosition2", "RestRandomOrder",
        "ChangeValid", "Optional", "AnyNumber", "Reveal", "Defined", "NoMove", "SkipReorder", "ForceRevealToController",
        "RememberChanged", "Imprint", "NoReveal",
        // "Onto the battlefield tapped and attacking" (Winota).
        "Tapped", "Attacking",
        // Face-down exile and who may look at it: hidden information doesn't matter against goldfish opponents.
        "ExileFaceDown", "WithMayLook");

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
        var players = Defined.Players(ab["Defined"], x);
        if (players.Count == 0) return;
        var player = players[0];
        if (x.S.Players[player].IsGoldfish) { DigUnknown(x, x.S.Players[player]); return; }
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
            x.Game.MoveTo(x.S[id], dest, pos, tapped: ab["Tapped"] == "True");
            if (dest == Zone.Battlefield && ab["Attacking"] == "True" && x.S.Step == Step.DeclareAttackers && x.S[id].Zone == Zone.Battlefield)
                x.S.Attackers.Add(id);
            if (dest == Zone.Exile) x.S[id].ExiledWith = x.Host.Id;
            ChangeZoneEffect.Note(x, id);
        }

        var rest = looked.Except(chosen).ToList();
        if (ab["RestRandomOrder"] == "True") x.S.Rng.Shuffle(rest);
        var dest2 = Enum.Parse<Zone>(ab["DestinationZone2"] ?? "Library");
        var pos2 = ab["LibraryPosition2"] is { } lp2 ? int.Parse(lp2) : -1;
        foreach (var id in rest) x.Game.MoveTo(x.S[id], dest2, pos2);
    }

    /// <summary>
    /// A goldfish opponent's library is only a count: "exiles the top twenty cards of their library" takes 20 off it.
    /// The cards are unknown, so a filter takes them only if it would take any card of theirs.
    /// </summary>
    private static void DigUnknown(EffectContext x, Player p)
    {
        var ab = x.Ability;
        var n = Math.Min(Amount(x, ab["DigNum"], 1), p.AbstractLibrary);
        var takes = ab["ChangeValid"] is not { } cv || FilterFor(x.SVarFace, cv).MatchesUnknownCard(p.Index, x);
        var chosen = !takes || ab["Optional"] == "True" ? 0
            : ab["ChangeNum"] is "All" or "Any" ? n : Math.Min(n, Amount(x, ab["ChangeNum"], 1));
        void Move(int count, string? zone)
        {
            var to = Enum.Parse<Zone>(zone ?? "Library");
            if (to == Zone.Library || count == 0) return;
            p.AbstractLibrary -= count;
            if (to == Zone.Hand) p.AbstractHand += count;
        }
        Move(chosen, ab["DestinationZone"] ?? "Hand");
        Move(n - chosen, ab["DestinationZone2"]);
        x.Game.Note($"  opponent {p.Index}: {n} card(s) off the top of their library");
    }
}

sealed class DigUntilEffect : Effect
{
    public override string Api => "DigUntil";
    public override IReadOnlySet<string> Params { get; } = P(
        "Valid", "ValidDescription", "FoundDestination", "RevealedDestination", "Amount", "Defined", "Shuffle", "RememberRevealed", "RevealedLibraryPosition");

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
        var revealedAt = ab["RevealedLibraryPosition"] is { } rlp && int.TryParse(rlp, out var at) ? at : -1;
        foreach (var id in revealed) x.Game.MoveTo(x.S[id], revealedDest, revealedAt);
        // The other cards revealed ("lose 1 life for each of the exiled cards").
        if (ab["RememberRevealed"] == "True") (x.Host.Remembered ??= new()).AddRange(revealed);
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
            var before = x.S.Players[p].LibraryCount;
            var milled = x.Game.Mill(p, n);
            if (x.Ability["RememberMilled"] == "True")
            {
                (x.Host.Remembered ??= new()).AddRange(milled);
                // A goldfish library's cards are unknown: remembered by number.
                if (x.S.Players[p].IsGoldfish) x.Host.RememberedUnknown += before - x.S.Players[p].LibraryCount;
            }
            if (p != x.Controller) x.Game.RememberWinCard(x.Host);
        }
    }
}

sealed class DiscardEffect : Effect
{
    public override string Api => "Discard";
    public override IReadOnlySet<string> Params { get; } = P("Mode", "NumCards", "Defined", "DiscardValid", "Optional", "RememberDiscarded", "UnlessType");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        if (ab["Mode"] is { } m && m is not ("TgtChoose" or "Hand" or "Random")) ctx.Need("Discard.Mode:" + m);
        ValidateAmount(ab, "NumCards", ctx);
        Defined.ValidatePlayers(ab["Defined"], ctx);
        if (ab["DiscardValid"] is { } dv) Filter.Parse(dv, ctx);
        if (ab["UnlessType"] is { } ut) Filter.Parse(ut, ctx);
    }

    public override void Resolve(EffectContext x)
    {
        foreach (var p in DrawEffect.Players(x))
        {
            if (x.S.Players[p].IsGoldfish) { DiscardUnknown(x, p); continue; }
            var hand = x.S.Players[p].Hand;
            var mode = x.Ability["Mode"] ?? "TgtChoose";
            var filter = x.Ability["DiscardValid"] is { } dv ? FilterFor(x.SVarFace, dv) : null;
            var eligible = hand.Where(id => filter is null || filter.MatchesCard(x.S[id], x)).ToList();
            var n = Math.Min(Amount(x, x.Ability["NumCards"], 1), eligible.Count);
            // "Discards two cards unless they discard an artifact card": one of those does instead.
            if (x.Ability["UnlessType"] is { } unless && FilterFor(x.SVarFace, unless) is var instead
                && eligible.Where(id => instead.MatchesCard(x.S[id], x)).ToList() is { Count: > 0 } ofType)
            {
                eligible = ofType;
                n = 1;
            }
            List<int> chosen = mode switch
            {
                "Hand" => eligible,
                "Random" => Enumerable.Range(0, n).Select(_ => eligible[x.S.Rng.Next(eligible.Count)]).Distinct().ToList(),
                _ => x.Game.Decider.ChooseCards(x.Game, p, Choice.Discard, x.Host, eligible, x.Ability["Optional"] == "True" ? 0 : n, n),
            };
            foreach (var id in chosen) x.Game.Discard(x.S[id]);
            if (x.Ability["RememberDiscarded"] == "True") (x.Host.Remembered ??= new()).AddRange(chosen);
        }
    }

    /// <summary>
    /// A goldfish opponent's hand is only a count, so where what's in it matters, it's assumed to be what's worse for
    /// you: no card that fits DiscardValid$ (unless only whose it is matters), and a card that fits UnlessType$; they
    /// never take an optional discard.
    /// </summary>
    private static void DiscardUnknown(EffectContext x, int p)
    {
        var ab = x.Ability;
        if (ab["Optional"] == "True") return;
        if (ab["DiscardValid"] is { } dv && !FilterFor(x.SVarFace, dv).MatchesUnknownCard(p, x)) return;
        var n = ab["Mode"] == "Hand" ? x.S.Players[p].AbstractHand : Amount(x, ab["NumCards"], 1);
        if (ab["UnlessType"] is not null) n = Math.Min(n, 1);
        var discarded = x.Game.DiscardUnknown(p, n);
        if (ab["RememberDiscarded"] == "True") x.Host.RememberedUnknown += discarded;
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
    public override IReadOnlySet<string> Params { get; } = P("ClearRemembered", "ClearNamedCard", "ClearImprinted", "ClearChosenPlayer", "ClearChosenColor", "ClearChosenCard");

    public override void Resolve(EffectContext x)
    {
        if (x.Ability["ClearChosenColor"] == "True") x.Host.ChosenColor = null;
        if (x.Ability["ClearChosenCard"] == "True") x.Host.ChosenCards = null;
        if (x.Ability["ClearChosenPlayer"] == "True") x.Host.ChosenPlayer = null;
        if (x.Ability["ClearRemembered"] == "True") { x.Host.Remembered = null; x.Host.RememberedUnknown = 0; x.Host.StoredVars?.Remove(Card.RememberedNumberKey); }
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
    public override IReadOnlySet<string> Params { get; } = P("NumDmg", "Defined", "DividedAsYouChoose", "DamageSource", "RememberDamaged", "DivideEvenly",
        // "Deals 4 damage to any target and 3 damage to each of up to two other targets" (Drakuseth): Forge holds the
        // damage until DamageResolve$ deals it all at once; here each part is dealt as it resolves.
        "DamageMap");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        ValidateAmount(ab, "NumDmg", ctx);
        if (ab["DivideEvenly"] is { } even && even != "RoundedDown") ctx.Need("DealDamage.DivideEvenly:" + even);
        // Defined$ players, or cards ("deals 2 damage to it": TriggeredCardLKICopy).
        if (ab["Defined"] is { } d && !Defined.IsPlayerRef(d)) Defined.ValidateCards(d, ctx);
        else Defined.ValidatePlayers(ab["Defined"], ctx);
        Defined.ValidateCards(ab["DamageSource"], ctx);
    }

    public override void Resolve(EffectContext x)
    {
        var n = Amount(x, x.Ability["NumDmg"], 0);
        var targets = x.Ability["Defined"] is { } d
            ? Defined.IsPlayerRef(d) ? Defined.Players(d, x).Select(TargetRef.OfPlayer).ToList()
            // "Remembered" may be cards or players (Wheel of Misfortune remembers the players who chose highest).
            : d == "Remembered" ? Defined.Cards(d, x).Select(TargetRef.OfCard).Concat(Defined.Players(d, x).Select(TargetRef.OfPlayer)).ToList()
            : Defined.Cards(d, x).Select(TargetRef.OfCard).ToList()
            : x.Targets;
        // "N damage divided as you choose": one to each target, the rest to the first (players first).
        var divided = x.Ability["DividedAsYouChoose"] is not null && targets.Count > 0;
        var order = divided ? targets.OrderBy(t => t.Player is null).ToList() : targets;
        // "It deals damage …" (DamageSource$): another card is the source (its lifelink, infect and triggers count).
        var source = x.Ability["DamageSource"] is { } ds && Defined.Cards(ds, x).FirstOrDefault(-1) is >= 0 and var sid ? x.S[sid] : x.Host;
        for (var i = 0; i < order.Count; i++)
        {
            var t = order[i];
            // "X damage divided evenly, rounded down, among any number of targets."
            var dmg = x.Ability["DivideEvenly"] is not null ? n / order.Count : !divided ? n : i == 0 ? n - (order.Count - 1) : 1;
            if (dmg <= 0) continue;
            if (t.Player is { } p) x.Game.DamagePlayer(p, dmg, source);
            else if (t.CardId is { } id && x.S[id].Zone == Zone.Battlefield)
            {
                x.Game.DamageCard(x.S[id], dmg, source);
                if (x.Ability["RememberDamaged"] == "True") (x.Host.Remembered ??= new()).Add(id);
            }
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
    protected static List<Card> Cards(EffectContext x, Zone zone = Zone.Battlefield) =>
        (x.Ability["Defined"] is { } d ? Defined.Cards(d, x)
            : x.Ability.TargetFilter is not null ? Defined.Cards("Targeted", x)
            : Defined.Cards("Self", x))
        .Select(id => x.S[id]).Where(c => c.Zone == zone).ToList();

    public override void Validate(AbilityDef ab, CompileContext ctx) => Defined.ValidateCards(ab["Defined"], ctx);
}

sealed class PumpEffect : CardsEffect
{
    public override string Api => "Pump";
    public override IReadOnlySet<string> Params { get; } = P("NumAtt", "NumDef", "KW", "Defined", "RememberObjects", "ForgetImprinted", "PumpZone", "Duration");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        // A player gaining a keyword ("you gain protection from everything", hexproof): nothing a goldfish opponent tests.
        if (ab["Defined"] is { } who && Defined.IsPlayerRef(who)) return;
        // "That land doesn't untap during its controller's next untap step": lasts until it has skipped one.
        // "It gains suspend" (in exile) lasts too.
        if (ab["Duration"] is { } dur && !(dur == "Permanent" && ab["KW"] is SkipNextUntapKeyword or "Suspend" && ab["NumAtt"] is null && ab["NumDef"] is null))
            ctx.Need("Pump.Duration:" + dur);
        base.Validate(ab, ctx);
        if (ab["NumAtt"] != "Double") ValidateAmount(ab, "NumAtt", ctx);
        if (ab["NumDef"] != "Double") ValidateAmount(ab, "NumDef", ctx);
        ValidateKeywords(ab["KW"], ctx);
        Defined.ValidateCards(ab["RememberObjects"], ctx);
        Defined.ValidateCards(ab["ForgetImprinted"], ctx);
        // "Target card in your graveyard gains flashback until end of turn."
        // "It gains suspend" (Jhoira): an exiled card with time counters becomes suspended.
        if (ab["PumpZone"] is { } pz && !(pz == "Graveyard" && ab["KW"] == "Flashback" || pz == "Exile" && ab["KW"] == "Suspend"))
            ctx.Need("Pump.PumpZone:" + pz);
    }

    public const string SkipNextUntapKeyword = "HIDDEN This card doesn't untap during your next untap step.";

    public static void ValidateKeywords(string? kw, CompileContext ctx)
    {
        if (kw is null or SkipNextUntapKeyword) return;
        foreach (var k in kw.Split('&', StringSplitOptions.TrimEntries))
            if (!Keywords.Supported.Contains(Forge.ForgeRequirements.KeywordName(k))) ctx.Need("Keyword:" + Forge.ForgeRequirements.KeywordName(k));
    }

    public static void Apply(Card c, int att, int def, string? kw)
    {
        c.PowerBonus += att;
        c.ToughnessBonus += def;
        if (kw is null) return;
        if (kw == SkipNextUntapKeyword) { c.SkipNextUntap = true; return; }
        c.TempKeywords ??= new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in kw.Split('&', StringSplitOptions.TrimEntries)) c.TempKeywords.Add(Forge.ForgeRequirements.KeywordName(k));
    }

    public override void Resolve(EffectContext x)
    {
        if (x.Ability["Defined"] is { } who && Defined.IsPlayerRef(who)) return;
        // "Double": gets +X/+0 where X is its power ("double its power").
        var doubleAtt = x.Ability["NumAtt"] == "Double";
        var doubleDef = x.Ability["NumDef"] == "Double";
        var att = doubleAtt ? 0 : Amount(x, x.Ability["NumAtt"], 0);
        var def = doubleDef ? 0 : Amount(x, x.Ability["NumDef"], 0);
        // "Pump" with only RememberObjects$ is Forge's way to remember cards on the source.
        if (x.Ability["RememberObjects"] is { } ro) (x.Host.Remembered ??= new()).AddRange(Defined.Cards(ro, x));
        if (x.Ability["ForgetImprinted"] is { } fi) foreach (var id in Defined.Cards(fi, x)) x.Host.Imprinted?.Remove(id);
        if (x.Ability["Defined"] is null && x.Ability.TargetFilter is null && (x.Ability["RememberObjects"] ?? x.Ability["ForgetImprinted"]) is not null) return;
        if (x.Ability["PumpZone"] == "Exile")
        {
            foreach (var c in Cards(x, Zone.Exile).Where(c => c.CounterCount("TIME") > 0)) c.Suspended = true;
            return;
        }
        foreach (var c in Cards(x, x.Ability["PumpZone"] == "Graveyard" ? Zone.Graveyard : Zone.Battlefield)) Apply(c, doubleAtt ? Math.Max(0, c.Power) : att, doubleDef ? Math.Max(0, c.Toughness) : def, x.Ability["KW"]);
    }
}

sealed class UntapEffect : CardsEffect
{
    public override string Api => "Untap";
    public override IReadOnlySet<string> Params { get; } = P("Defined", "UntapType", "Amount", "UntapUpTo");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        base.Validate(ab, ctx);
        if (ab["UntapType"] is { } ut) Filter.Parse(ut, ctx);
        ValidateAmount(ab, "Amount", ctx);
        if ((ab["Amount"] ?? ab["UntapUpTo"]) is not null && ab["UntapType"] is null) ctx.Need("Untap.Amount+NoType");
    }

    public override void Resolve(EffectContext x)
    {
        if (x.Ability["UntapType"] is not { } type)
        {
            foreach (var c in Cards(x)) c.Tapped = false;
            return;
        }
        // "Untap up to three lands": untapping never hurts against a goldfish, so as many as allowed.
        var filter = FilterFor(x.SVarFace, type);
        var tapped = x.S.BattlefieldCards.Where(c => c.Tapped && c.Controller == x.Controller && filter.MatchesCard(c, x)).Select(c => c.Id).ToList();
        var n = Math.Min(Amount(x, x.Ability["Amount"], 1), tapped.Count);
        foreach (var id in x.Game.Decider.ChooseCards(x.Game, x.Controller, Choice.Generic, x.Host, tapped, n, n)) x.S[id].Tapped = false;
    }
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
    public override IReadOnlySet<string> Params { get; } = P("CounterType", "CounterNum", "Defined", "DividedAsYouChoose",
        // Adapt N: N +1/+1 counters if it has none. Monstrosity N: if it isn't monstrous, N counters and it becomes monstrous.
        "Adapt", "Monstrosity",
        // "Put those counters on this" (The Ozolith): CounterType$ EachFromSource with EachFromSource$ <card>.
        "EachFromSource");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        // "You get an experience counter": counters on players.
        if (ab["Defined"] is { } who && Defined.IsPlayerRef(who)) Defined.ValidatePlayers(who, ctx);
        else base.Validate(ab, ctx);
        ValidateAmount(ab, "CounterNum", ctx);
        ValidateAmount(ab, "Adapt", ctx);
        ValidateAmount(ab, "Monstrosity", ctx);
        if (ab["CounterType"] is null && ab["Adapt"] is null && ab["Monstrosity"] is null) ctx.Need("PutCounter.CounterType:none");
        if (ab["CounterType"] == "EachFromSource") Defined.ValidateCards(ab["EachFromSource"], ctx);
    }

    public override void Resolve(EffectContext x)
    {
        if (x.Ability["Adapt"] is { } adapt)
        {
            if (x.Host.Zone == Zone.Battlefield && x.Host.CounterCount("P1P1") == 0) x.Game.PutCounters(x.Host, "P1P1", Amount(x, adapt, 1));
            return;
        }
        if (x.Ability["Monstrosity"] is { } monstrosity)
        {
            if (x.Host.Zone != Zone.Battlefield || x.Host.Monstrous) return;
            var m = Amount(x, monstrosity, 1);
            x.Game.PutCounters(x.Host, "P1P1", m);
            x.Host.Monstrous = true;
            x.Game.Fire(new TriggerEvent(TriggerMode.BecomeMonstrous, CardId: x.Host.Id, Player: x.Controller, Amount: m));
            return;
        }
        if (x.Ability["CounterType"] == "EachFromSource")
        {
            var from = Defined.Cards(x.Ability["EachFromSource"], x).Select(id => x.S[id]).FirstOrDefault();
            var counters = from is null ? null : from.Zone == Zone.Battlefield ? from.Counters : from.LeftCounters ?? from.Counters;
            foreach (var c in Cards(x))
                foreach (var (k, v) in counters?.Where(kv => kv.Value > 0).ToList() ?? new())
                    x.Game.PutCounters(c, k, v);
            return;
        }
        var n = Amount(x, x.Ability["CounterNum"], 1);
        var kind = x.Ability["CounterType"]!;
        if (x.Ability["Defined"] is { } who && Defined.IsPlayerRef(who))
        {
            foreach (var p in Defined.Players(who, x))
            {
                var player = x.S.Players[p];
                if (kind.Equals("Poison", StringComparison.OrdinalIgnoreCase)) player.Poison += n;
                else
                {
                    var key = kind.ToUpperInvariant();
                    (player.Counters ??= new())[key] = player.CounterCount(key) + n;
                }
                x.Game.Note($"  player {p} gets {n} {kind.ToLowerInvariant()} counter(s)");
            }
            return;
        }
        var cards = Cards(x).ToList();
        // "Put four time counters on the exiled card" (Jhoira of the Ghitu): the card exiled to pay the cost.
        if (x.Ability["Defined"] is { } exiled && exiled.StartsWith("ExiledCards", StringComparison.Ordinal)) cards.AddRange(Cards(x, Zone.Exile));
        // "Distribute four +1/+1 counters among any number of target creatures": one each, the rest on the first.
        if (x.Ability["DividedAsYouChoose"] is not null)
        {
            for (var i = 0; i < cards.Count; i++) x.Game.PutCounters(cards[i], kind, i == 0 ? n - (cards.Count - 1) : 1);
            return;
        }
        foreach (var c in cards) x.Game.PutCounters(c, kind, n);
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

/// <summary>"All permanents you control phase out" until your next untap step (Teferi's Protection).</summary>
sealed class PhasesEffect : Effect
{
    public override string Api => "Phases";
    public override IReadOnlySet<string> Params { get; } = P("Defined");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        if (ab["Defined"] is not { } d || ab.TargetFilter is not null) ctx.Need("Phases:" + (ab["Defined"] ?? "Targeted"));
        else Defined.ValidateCards(d, ctx);
    }

    public override void Resolve(EffectContext x)
    {
        foreach (var id in Defined.Cards(x.Ability["Defined"], x))
            if (x.S[id].Controller == Game.Me) x.Game.PhaseOut(x.S[id]);
    }
}

/// <summary>The second half of cumulative upkeep (rule 702.24): pay the cost once per age counter, or sacrifice it.</summary>
sealed class CumulativeUpkeepEffect : Effect
{
    public override string Api => "CumulativeUpkeep";
    public override IReadOnlySet<string> Params { get; } = P("UpkeepCost");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        var cost = CardCompiler.ParseCost(ab["UpkeepCost"] ?? "", ctx);
        // Paying "sacrifice a land" twice is two different lands; only costs that simply add up are supported.
        if (cost.Tap || cost.Parts.Any(p => p.Kind != CostPartKind.PayLife)) ctx.Need("CumulativeUpkeep:" + ab["UpkeepCost"]);
    }

    public override void Resolve(EffectContext x)
    {
        var host = x.Host;
        if (host.Zone != Zone.Battlefield) return;
        var once = CardCompiler.ParseCost(x.Ability["UpkeepCost"]!, new CompileContext { Face = x.SVarFace, Missing = new() });
        var total = Cost.Free;
        for (var i = 0; i < host.CounterCount("AGE"); i++) total = total.Plus(once);
        if (x.Game.CanPayCost(total, host, x.Controller, 0, excludeSourceFromMana: false)
            && x.Game.Decider.ChooseYesNo(x.Game, x.Controller, host, $"pay cumulative upkeep {total.Mana} for {host.Name}?")
            && x.Game.PayCost(total, host, x.Controller, 0, excludeSourceFromMana: false))
            return;
        x.Game.Sacrifice(host);
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
