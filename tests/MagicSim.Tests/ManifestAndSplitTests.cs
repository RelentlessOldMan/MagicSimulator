using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using MagicSim.Core.Players;
using static MagicSim.Tests.TestGames;

namespace MagicSim.Tests;

/// <summary>
/// "Whenever you copy a spell", triggers from casting or copying a spell triggering an additional time, manifest and
/// split cards, on hand-written cards in Forge's format.
/// </summary>
public class ManifestAndSplitTests
{
    private static readonly string[] Scripts =
    {
        "Name:Peak\nTypes:Land\nA:AB$ Mana | Cost$ T | Produced$ R\n",
        "Name:Filler\nManaCost:9\nTypes:Sorcery\n",
        "Name:Ping\nManaCost:0\nTypes:Instant\nA:SP$ GainLife | LifeAmount$ 1\n",
        "Name:Bear\nManaCost:0\nTypes:Creature Bear\nPT:2/2\n",
        "Name:Giant\nManaCost:1\nTypes:Creature Giant\nPT:5/5\n",
        "Name:Twin\nManaCost:0\nTypes:Instant\n" +
        "A:SP$ CopySpellAbility | ValidTgts$ Instant,Sorcery | TargetType$ Spell | MayChooseTarget$ True\n",
        "Name:Stormsire\nManaCost:0\nTypes:Creature Elemental\nPT:4/4\n" +
        "T:Mode$ SpellCopy | ValidCard$ Instant | ValidActivatingPlayer$ You | TriggerZones$ Battlefield | Execute$ TrigPut\n" +
        "SVar:TrigPut:DB$ PutCounter | Defined$ Self | CounterType$ P1P1 | CounterNum$ 1\n",
        "Name:Adept\nManaCost:0\nTypes:Creature Human Wizard\nPT:1/1\n" +
        "T:Mode$ SpellCastOrCopy | ValidCard$ Instant,Sorcery | ValidActivatingPlayer$ You | TriggerZones$ Battlefield | Execute$ TrigLife\n" +
        "SVar:TrigLife:DB$ GainLife | LifeAmount$ 10\n",
        "Name:Duality\nManaCost:0\nTypes:Creature Efreet Wizard\nPT:2/2\n" +
        "S:Mode$ Panharmonicon | ValidMode$ SpellCast,SpellCopy,SpellCastOrCopy | ValidCard$ Permanent.YouCtrl | ValidCause$ Instant,Sorcery | ValidActivator$ You\n",
        "Name:Shift\nManaCost:0\nTypes:Instant\n" +
        "A:SP$ ChangeZone | Origin$ Battlefield | Destination$ Exile | ValidTgts$ Creature | SubAbility$ DBManifest\n" +
        "SVar:DBManifest:DB$ Manifest | DefinedPlayer$ TargetedController\n",
        "Name:Haunt\nManaCost:0\nTypes:Sorcery\nA:SP$ Manifest | DefinedPlayer$ Opponent\n",
        "Name:Fire\nManaCost:1\nTypes:Instant\nA:SP$ GainLife | LifeAmount$ 2\nAlternateMode:Split\n\nALTERNATE\n\n" +
        "Name:Ice\nManaCost:2\nTypes:Instant\nA:SP$ GainLife | LifeAmount$ 3\n",
        "Name:Seeker\nManaCost:0\nTypes:Sorcery\n" +
        "A:SP$ ChangeZone | Origin$ Library | Destination$ Hand | ChangeType$ Card.cmcEQ3 | ChangeNum$ 1\n",
    };

    private static readonly CardPool Pool = new(ForgeLibrary.FromScripts(Scripts, new Dictionary<string, string>()), null);

    private static Game Setup(string[] hand, string[]? battlefield = null, string[]? library = null) =>
        TestGames.Setup(Pool, hand, battlefield, library);

    private static int Life(Game g) => g.State.Players[Game.Me].Life;

    [Fact]
    public void EveryTestCardCompiles()
    {
        foreach (var s in Scripts)
        {
            var name = s[5..s.IndexOf('\n')];
            if (name == "Fire") name = "Fire // Ice";
            Assert.Equal(name + ": ", name + ": " + string.Join("; ", Pool.Resolve(name)!.Missing));
        }
    }

    [Fact]
    public void WheneverYouCopyAnInstant()
    {
        var g = Setup(new[] { "Ping", "Twin" }, battlefield: new[] { "Stormsire" });
        g.CastSpell(g.State[InHand(g, "Ping")], 0);
        g.CastSpell(g.State[InHand(g, "Twin")], 0);
        ResolveAll(g);
        Assert.Equal(42, Life(g));
        Assert.Equal(1, OnBattlefield(g, "Stormsire").CounterCount("P1P1"));   // casting Twin isn't copying
    }

    [Fact]
    public void CastOrCopyTriggersTriggerTwice()
    {
        var g = Setup(new[] { "Ping" }, battlefield: new[] { "Adept" });
        g.CastSpell(g.State[InHand(g, "Ping")], 0);
        ResolveAll(g);
        Assert.Equal(51, Life(g));

        var h = Setup(new[] { "Ping" }, battlefield: new[] { "Adept", "Duality" });
        h.CastSpell(h.State[InHand(h, "Ping")], 0);
        ResolveAll(h);
        Assert.Equal(61, Life(h));
    }

    [Fact]
    public void ManifestTheTopCardAndTurnItFaceUpForItsManaCost()
    {
        var g = Setup(new[] { "Shift" }, battlefield: new[] { "Bear", "Peak" }, library: new[] { "Giant", "Filler" });
        g.CastSpell(g.State[InHand(g, "Shift")], 0);
        ResolveAll(g);
        var giant = g.State.Cards.First(c => c.Name == "Giant" || c.Def.Name == "Giant");
        Assert.Equal(Zone.Battlefield, giant.Zone);
        Assert.True(giant.FaceDown);
        Assert.Equal(2, giant.Power);
        Assert.Equal(Zone.Exile, g.State.Cards.First(c => c.Def.Name == "Bear").Zone);

        g.ActivateAbility(giant, 0);
        ResolveAll(g);
        Assert.False(giant.FaceDown);
        Assert.Equal(5, giant.Power);
    }

    [Fact]
    public void ANoncreatureCardManifestedStaysFaceDown()
    {
        var g = Setup(new[] { "Shift" }, battlefield: new[] { "Bear" }, library: new[] { "Filler", "Filler" });
        g.CastSpell(g.State[InHand(g, "Shift")], 0);
        ResolveAll(g);
        var manifested = g.State.BattlefieldCards.Single();
        Assert.True(manifested.FaceDown);
        Assert.Empty(manifested.CurrentFace.Activated);
    }

    [Fact]
    public void AGoldfishOpponentManifestsFromTheirLibraryCount()
    {
        var g = Setup(new[] { "Haunt" });
        var library = g.State.Players[1].AbstractLibrary;
        g.CastSpell(g.State[InHand(g, "Haunt")], 0);
        ResolveAll(g);
        Assert.Equal(library - 1, g.State.Players[1].AbstractLibrary);
        Assert.Contains(g.State.BattlefieldCards, c => c.Controller == 1 && c.Power == 2 && c.Types.Is(CardType.Creature));
    }

    [Fact]
    public void ASplitCardIsCastAsEitherHalfAndHasBothManaValues()
    {
        var g = Setup(new[] { "Fire // Ice", "Seeker" }, battlefield: new[] { "Peak", "Peak" }, library: new[] { "Fire // Ice" });
        var split = g.State[InHand(g, "Fire // Ice")];
        Assert.Equal(3, split.ManaValue);
        var halves = g.LegalActions(Game.Me).OfType<GameAction.Cast>().Where(a => a.CardId == split.Id).ToList();
        Assert.Equal(2, halves.Count);

        g.Perform(halves.Single(a => a.Face == 1));
        ResolveAll(g);
        Assert.Equal(43, Life(g));
        Assert.Equal(Zone.Graveyard, split.Zone);
        Assert.Equal(0, split.Face);

        g.CastSpell(g.State[InHand(g, "Seeker")], 0);   // finds the other one by its combined mana value
        ResolveAll(g);
        Assert.Single(g.State.Players[Game.Me].Hand);
    }
}
