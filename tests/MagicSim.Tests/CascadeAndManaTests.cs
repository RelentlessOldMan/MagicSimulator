using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using MagicSim.Core.Players;
using static MagicSim.Tests.TestGames;

namespace MagicSim.Tests;

/// <summary>
/// Cascade only with delirium, lands put onto the battlefield as you cascade, retrace, casting a card from exile,
/// distributing counters, "doesn't untap during its next untap step", mana of each color among your permanents and
/// creature-only mana from exiling a creature, on hand-written cards in Forge's format.
/// </summary>
public class CascadeAndManaTests
{
    private static readonly string[] Scripts =
    {
        "Name:Peak\nTypes:Land\nA:AB$ Mana | Cost$ T | Produced$ R\n",
        "Name:Filler\nManaCost:9\nTypes:Sorcery\n",
        "Name:Ping\nManaCost:0\nTypes:Sorcery\nA:SP$ GainLife | LifeAmount$ 1\n",
        "Name:Bear\nManaCost:2\nTypes:Creature Bear\nPT:2/2\n",
        "Name:Rock\nManaCost:1\nTypes:Artifact\n",
        "Name:Ogre\nManaCost:3\nTypes:Creature Ogre\nPT:3/3\n",
        "Name:Giant\nManaCost:4\nTypes:Creature Giant\nPT:4/4\n",
        "Name:Big\nManaCost:4\nTypes:Sorcery\nA:SP$ GainLife | LifeAmount$ 1\n",
        "Name:Marauder\nManaCost:2\nTypes:Creature Human Berserker\nPT:3/1\n" +
        "S:Mode$ Continuous | Affected$ Card.Self | EffectZone$ Stack | AffectedZone$ Stack | AddKeyword$ Cascade | Condition$ Delirium\n",
        "Name:Cascader\nManaCost:2\nTypes:Sorcery\nK:Cascade\nA:SP$ GainLife | LifeAmount$ 5\n",
        "Name:Averna\nManaCost:0\nTypes:Legendary Creature Elemental Shaman\nPT:4/2\n" +
        "R:Event$ Cascade | ValidPlayer$ You | ActiveZones$ Battlefield | ReplaceWith$ DBLand | ReplacementResult$ Updated\n" +
        "SVar:DBLand:DB$ ChangeZone | Hidden$ True | Origin$ Exile | ChooseFromDefined$ ReplacedCards.Land | ChangeNum$ 1 | Destination$ Battlefield | Tapped$ True\n",
        "Name:Throes\nManaCost:1\nTypes:Sorcery\nK:Retrace\nA:SP$ GainLife | LifeAmount$ 1\n",
        "Name:Griffin\nManaCost:1\nTypes:Creature Griffin\nPT:3/3\n" +
        "S:Mode$ Continuous | Affected$ Card.Self | MayPlay$ True | AffectedZone$ Exile | EffectZone$ Exile\n",
        "Name:Gearhulk\nManaCost:0\nTypes:Artifact Creature Construct\nPT:4/4\n" +
        "T:Mode$ ChangesZone | Origin$ Any | Destination$ Battlefield | ValidCard$ Card.Self | Execute$ TrigPut\n" +
        "SVar:TrigPut:DB$ PutCounter | ValidTgts$ Creature.YouCtrl | CounterType$ P1P1 | CounterNum$ 4 | TargetMin$ 0 | TargetMax$ 4 | DividedAsYouChoose$ 4\n",
        "Name:Freeze\nManaCost:0\nTypes:Sorcery\n" +
        "A:SP$ Pump | ValidTgts$ Land | KW$ HIDDEN This card doesn't untap during your next untap step. | Duration$ Permanent\n",
        "Name:Gold\nManaCost:W U\nTypes:Artifact\n",
        "Name:Tender\nManaCost:0\nTypes:Creature Elf Druid\nPT:1/1\n" +
        "A:AB$ Mana | Cost$ T | Produced$ Special EachColorAmong_Valid Permanent.YouCtrl\n",
        "Name:Chain\nManaCost:0\nTypes:Enchantment\n" +
        "A:AB$ Mana | Cost$ Exile<1/Creature> | Produced$ Any | Amount$ X | RestrictValid$ Spell.Creature\n" +
        "SVar:X:Exiled$CardManaCost/Plus.1\n",
    };

    private static readonly CardPool Pool = new(ForgeLibrary.FromScripts(Scripts, new Dictionary<string, string>()), null);

    private static Game Setup(string[] hand, string[]? battlefield = null, string[]? library = null) =>
        TestGames.Setup(Pool, hand, battlefield, library);

    private static Card Named(Game g, string name) => g.State.Cards.First(c => c.Name == name);

    private static int Life(Game g) => g.State.Players[Game.Me].Life;

    [Fact]
    public void EveryTestCardCompiles()
    {
        foreach (var s in Scripts)
        {
            var name = s[5..s.IndexOf('\n')];
            Assert.Equal(name + ": ", name + ": " + string.Join("; ", Pool.Resolve(name)!.Missing));
        }
    }

    [Fact]
    public void CascadeOnlyWithDelirium()
    {
        var g = Setup(new[] { "Marauder" }, battlefield: new[] { "Peak", "Peak" }, library: new[] { "Ping", "Filler" });
        g.CastSpell(g.State[InHand(g, "Marauder")], -1);
        ResolveAll(g);
        Assert.Equal(40, Life(g));

        var h = Setup(new[] { "Marauder" }, battlefield: new[] { "Peak", "Peak" }, library: new[] { "Ping", "Filler", "Peak", "Bear", "Rock", "Big" });
        foreach (var name in new[] { "Bear", "Rock", "Big" }) h.MoveTo(Named(h, name), Zone.Graveyard);
        h.MoveTo(h.State[h.State.Players[Game.Me].Library.First(id => h.State[id].Name == "Peak")], Zone.Graveyard);
        h.CastSpell(h.State[InHand(h, "Marauder")], -1);
        ResolveAll(h);
        Assert.Equal(41, Life(h));
    }

    [Fact]
    public void AsYouCascadePutALandOntoTheBattlefield()
    {
        var g = Setup(new[] { "Cascader" }, battlefield: new[] { "Averna", "Peak", "Peak" }, library: new[] { "Peak", "Ping", "Filler" });
        var exiledLand = g.State[g.State.Players[Game.Me].Library[0]];
        g.CastSpell(g.State[InHand(g, "Cascader")], 0);
        ResolveAll(g);
        Assert.Equal(46, Life(g));
        Assert.Equal(Zone.Battlefield, exiledLand.Zone);
        Assert.True(exiledLand.Tapped);
    }

    [Fact]
    public void RetraceDiscardsALandAndStaysInTheGraveyard()
    {
        var g = Setup(new[] { "Peak" }, battlefield: new[] { "Peak" }, library: new[] { "Throes" });
        var throes = Named(g, "Throes");
        g.MoveTo(throes, Zone.Graveyard);
        Assert.True(g.CanCast(throes, 0));
        g.CastSpell(throes, 0);
        ResolveAll(g);
        Assert.Equal(41, Life(g));
        Assert.Equal(Zone.Graveyard, throes.Zone);
        Assert.Empty(g.State.Players[Game.Me].Hand);
        Assert.False(g.CanCast(throes, 0));   // no land card left to discard
    }

    [Fact]
    public void CastFromExile()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Peak" }, library: new[] { "Griffin" });
        var griffin = Named(g, "Griffin");
        bool Castable() => g.LegalActions(Game.Me).Any(a => a is GameAction.Cast c && c.CardId == griffin.Id);
        g.MoveTo(griffin, Zone.Graveyard);
        Assert.False(Castable());
        g.MoveTo(griffin, Zone.Exile);
        Assert.True(Castable());
        g.CastSpell(griffin, -1);
        ResolveAll(g);
        Assert.Equal(Zone.Battlefield, griffin.Zone);
    }

    [Fact]
    public void DistributeCountersAmongTargets()
    {
        var g = Setup(new[] { "Gearhulk" }, battlefield: new[] { "Bear" });
        g.CastSpell(g.State[InHand(g, "Gearhulk")], -1);
        ResolveAll(g);
        Assert.Equal(4, g.State.BattlefieldCards.Sum(c => c.CounterCount("P1P1")));
    }

    [Fact]
    public void DoesntUntapDuringItsNextUntapStep()
    {
        var g = Setup(new[] { "Freeze" }, battlefield: new[] { "Peak" }, library: new[] { "Filler", "Filler", "Filler" });
        var peak = OnBattlefield(g, "Peak");
        peak.Tapped = true;
        g.CastSpell(g.State[InHand(g, "Freeze")], 0);
        ResolveAll(g);
        g.ResumeTurn();
        g.RunTurn();
        Assert.True(peak.Tapped);
        g.RunTurn();
        Assert.False(peak.Tapped);
    }

    [Fact]
    public void OneManaOfEachColorAmongYourPermanents()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Tender", "Gold" });
        g.ActivateAbility(OnBattlefield(g, "Tender"), 0);
        var pool = g.State.Players[Game.Me].Pool;
        Assert.Equal(1, pool[ManaColor.W]);
        Assert.Equal(1, pool[ManaColor.U]);
        Assert.Equal(2, pool.Total);
    }

    [Fact]
    public void ExileACreatureForManaOnlyForCreatures()
    {
        var g = Setup(new[] { "Big", "Giant" }, battlefield: new[] { "Chain", "Ogre" });
        g.ActivateAbility(OnBattlefield(g, "Chain"), 0);
        Assert.Equal(Zone.Exile, Named(g, "Ogre").Zone);
        Assert.Equal(4, g.State.Players[Game.Me].RestrictedPool!.Sum(r => r.Amount));
        Assert.False(g.CanCast(g.State[InHand(g, "Big")], 0));
        var giant = g.State[InHand(g, "Giant")];
        Assert.True(g.CanCast(giant, -1));
        g.CastSpell(giant, -1);
        ResolveAll(g);
        Assert.Equal(Zone.Battlefield, giant.Zone);
        Assert.Empty(g.State.Players[Game.Me].RestrictedPool!);
    }
}
