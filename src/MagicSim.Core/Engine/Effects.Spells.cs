namespace MagicSim.Core.Engine;

/// <summary>"Copy that spell" / "copy target instant or sorcery spell": copies go on the stack above it (rule 707.10).</summary>
sealed class CopySpellAbilityEffect : Effect
{
    public override string Api => "CopySpellAbility";
    public override IReadOnlySet<string> Params { get; } = P("Defined", "Amount", "Controller", "MayChooseTarget", "Optional");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        if (ab["Defined"] is { } d && d is not ("TriggeredSpellAbility" or "Parent" or "Targeted")) ctx.Need("CopySpellAbility.Defined:" + d);
        if (ab["Defined"] is null && ab.TargetFilter is null) ctx.Need("CopySpellAbility:NothingToCopy");
        if (ab["TargetType"] is { } tt && tt is not ("Spell" or "Triggered.YouCtrl")) ctx.Need("CopySpellAbility.TargetType:" + tt);
        // "That player copies it" (Bonus Round): the copied spell's controller.
        if (ab["Controller"] is { } c && c is not ("You" or "TriggeredCardController")) ctx.Need("CopySpellAbility.Controller:" + c);
        ValidateAmount(ab, "Amount", ctx);
    }

    public override void Resolve(EffectContext x)
    {
        var ab = x.Ability;
        if (ab["Optional"] == "True" && !x.Game.Decider.ChooseYesNo(x.Game, x.Controller, x.Host, "copy the spell?")) return;
        // The spells: the one that triggered this, the parent's target, or this ability's targets.
        var ids = ab["Defined"] switch
        {
            "TriggeredSpellAbility" => x.Item.TriggeredCardId is { } tc ? new List<int> { tc } : new(),
            "Parent" => Defined.Cards("ParentTarget", x),
            _ => Defined.Cards("Targeted", x),
        };
        var n = Amount(x, ab["Amount"], 1);
        foreach (var id in ids)
        {
            var original = ab["TargetType"] == "Triggered.YouCtrl"
                ? x.S.Stack.LastOrDefault(s => s.IsTriggered && s.SourceId == id && s != x.Item)
                : x.S.Stack.LastOrDefault(s => s.IsSpell && s.SourceId == id);
            if (original is null || original.IsSpell && x.S[id].Types.IsPermanent) continue;   // copies of permanent spells (tokens) aren't supported
            var copier = ab["Controller"] == "TriggeredCardController" ? original.Controller : x.Controller;
            for (var i = 0; i < n; i++) x.Game.CopySpell(original, copier, ab["MayChooseTarget"] == "True");
        }
    }
}

/// <summary>"You may cast [card] without paying its mana cost": cascade-like effects, Mind's Desire, Sneak Attack-style casts.</summary>
sealed class PlayEffect : Effect
{
    public override string Api => "Play";
    public override IReadOnlySet<string> Params { get; } = P(
        "Defined", "Valid", "ValidZone", "ValidSA", "WithoutManaCost", "Optional", "Amount", "Controller", "ForgetPlayed", "RememberPlayed",
        "ReplaceGraveyard", "ImprintPlayed",
        // Generated for miracle: cast it for its miracle cost.
        "Miracle");

    private static readonly HashSet<string> Zones = new(StringComparer.Ordinal) { "Hand", "Exile", "Graveyard", "Library", "Command" };

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        if (ab["Defined"] is { } d) Defined.ValidateCards(d, ctx);
        else if (ab["Valid"] is { } v) Filter.Parse(v, ctx);
        else if (ab.TargetFilter is null) ctx.Need("Play:NothingToPlay");
        if (ab["ValidZone"] is { } z && !Zones.Contains(z)) ctx.Need("Play.ValidZone:" + z);
        // "Spell.cmcLE4": a spell with mana value 4 or less.
        if (ab["ValidSA"] is { } sa && sa != "Spell")
        {
            if (sa.StartsWith("Spell.", StringComparison.Ordinal) && !sa.Contains(',')) Filter.Parse(sa, ctx);
            else ctx.Need("Play.ValidSA:" + sa);
        }
        if (ab["Controller"] is { } c && c != "You") ctx.Need("Play.Controller:" + c);
        if (ab["Amount"] is { } a && a != "All") ValidateAmount(ab, "Amount", ctx);
        if (ab["ReplaceGraveyard"] is { } rg && rg != "Exile") ctx.Need("Play.ReplaceGraveyard:" + rg);
    }

    public override void Resolve(EffectContext x)
    {
        var ab = x.Ability;
        List<int> candidates;
        if (ab["Defined"] is { } d) candidates = Defined.Cards(d, x);
        else if (ab["Valid"] is { } v)
        {
            var filter = FilterFor(x.SVarFace, v);
            var zone = ab["ValidZone"] is { } z ? Enum.Parse<Zone>(z) : Zone.Hand;
            candidates = x.Game.CardsIn(zone).Where(c => filter.MatchesCard(c, x)).Select(c => c.Id).ToList();
        }
        else candidates = Defined.Cards("Targeted", x);

        if (ab["ValidSA"] is { } validSA && validSA != "Spell")
        {
            var spellFilter = FilterFor(x.SVarFace, validSA);
            candidates = candidates.Where(id => spellFilter.MatchesCard(x.S[id], x)).ToList();
        }
        var free = ab["WithoutManaCost"] == "True";
        var remaining = ab["Amount"] == "All" ? int.MaxValue : Amount(x, ab["Amount"], 1);
        while (remaining-- > 0)
        {
            var miracle = ab["Miracle"] == "True";
            var castable = candidates.Where(id => x.S[id] is var c && !c.Types.Is(CardType.Land) && c.Zone is not (Zone.Stack or Zone.Battlefield or Zone.None)
                                                  && (miracle ? x.Game.CanCastMiracle(c) : x.Game.CanCastFromEffect(c, free))).ToList();
            if (castable.Count == 0) break;
            var pick = x.Game.Decider.ChooseCards(x.Game, x.Controller, Choice.Generic, x.Host, castable, ab["Optional"] == "True" ? 0 : 1, 1);
            if (pick.Count == 0) break;
            var card = x.S[pick[0]];
            candidates.Remove(card.Id);
            if (miracle) x.Game.CastMiracle(card);
            else x.Game.CastFromEffect(card, free, exileAfter: ab["ReplaceGraveyard"] == "Exile");
            if (ab["RememberPlayed"] == "True") (x.Host.Remembered ??= new()).Add(card.Id);
            if (ab["ImprintPlayed"] == "True") (x.Host.Imprinted ??= new()).Add(card.Id);
            if (ab["ForgetPlayed"] == "True") x.Host.Remembered?.Remove(card.Id);
        }
    }
}

/// <summary>"Gain control of …" (until end of turn, with haste and untapped: Threaten effects).</summary>
sealed class GainControlEffect : Effect
{
    public override string Api => "GainControl";
    public override IReadOnlySet<string> Params { get; } = P("Defined", "NewController", "LoseControl", "Untap", "AddKWs", "AllValid",
        // "For each opponent, gain control of target permanent that player controls": one target per player.
        "TargetsForEachPlayer");

    public override void Validate(AbilityDef ab, CompileContext ctx)
    {
        Defined.ValidateCards(ab["Defined"], ctx);
        // "Gain control of all creatures" (Insurrection).
        if (ab["AllValid"] is { } all) Filter.Parse(all, ctx);
        else if (ab["Defined"] is null && ab.TargetFilter is null) ctx.Need("GainControl:NoTarget");
        if (ab.TargetFilter is { CanTargetPlayers: true }) ctx.Need("GainControl:Player");
        Defined.ValidatePlayers(ab["NewController"], ctx);
        if (ab["LoseControl"] is { } lc && lc != "EOT") ctx.Need("GainControl.LoseControl:" + lc);
        if (ab["AddKWs"] is { } kws)
            foreach (var k in kws.Split('&', StringSplitOptions.TrimEntries))
                if (!Keywords.Supported.Contains(Forge.ForgeRequirements.KeywordName(k))) ctx.Need("Keyword:" + k);
    }

    public override void Resolve(EffectContext x)
    {
        var ab = x.Ability;
        var newController = ab["NewController"] is { } nc ? Defined.Players(nc, x).FirstOrDefault(x.Controller) : x.Controller;
        var cards = ab["AllValid"] is { } all ? Defined.Cards("Valid " + all, x) : Defined.Cards(ab["Defined"], x, "Targeted");
        foreach (var id in cards)
        {
            var c = x.S[id];
            if (c.Zone != Zone.Battlefield) continue;
            if (ab["LoseControl"] == "EOT") x.S.ThisTurn.ControlUntilEot.Add((c.Id, c.Controller));
            if (c.Controller != newController)
            {
                c.Controller = newController;
                c.ControlledSinceTurn = x.S.Turn;
            }
            if (ab["Untap"] == "True") c.Tapped = false;
            if (ab["AddKWs"] is { } kws)
            {
                c.TempKeywords ??= new HashSet<string>(StringComparer.Ordinal);
                foreach (var k in kws.Split('&', StringSplitOptions.TrimEntries)) c.TempKeywords.Add(Forge.ForgeRequirements.KeywordName(k));
            }
            x.Game.Note($"  player {newController} gains control of {c.Name}");
        }
    }
}
