using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using MagicSim.Core.Players;
using static MagicSim.Tests.TestGames;

namespace MagicSim.Tests;

/// <summary>
/// Cards compared with the card that triggered, copying a triggered ability, flickering, "can't cast spells with that
/// name" for a remembered player, metalcraft on triggers and "unless that player pays X", on hand-written cards in
/// Forge's format.
/// </summary>
public class FlickerAndCopyTriggerTests
{
    private static readonly string[] Scripts =
    {
        "Name:Peak\nTypes:Land\nA:AB$ Mana | Cost$ T | Produced$ R\n",
        "Name:Filler\nManaCost:9\nTypes:Sorcery\n",
        "Name:Bear\nManaCost:0\nTypes:Creature Bear\nPT:2/2\n",
        "Name:Rock\nManaCost:0\nTypes:Artifact\n",
        "Name:Wall\nManaCost:0\nTypes:Enchantment\n",
        "Name:Curio\nManaCost:0\nTypes:Artifact\n" +
        "T:Mode$ ChangesZone | ValidCard$ Permanent.YouCtrl+nonArtifact | Origin$ Any | Destination$ Battlefield | TriggerZones$ Battlefield | Execute$ TrigBounce\n" +
        "SVar:TrigBounce:DB$ ChangeZone | DefinedPlayer$ TriggeredCardController | ChangeType$ Triggered.Other+sharesPermanentTypeWith | ChangeNum$ 1 | Origin$ Battlefield | Destination$ Hand | Hidden$ True | Optional$ You\n",
        "Name:Elk\nManaCost:0\nTypes:Creature Elk\nPT:1/1\n" +
        "T:Mode$ ChangesZone | ValidCard$ Card.Self | Origin$ Any | Destination$ Battlefield | Execute$ TrigLife\n" +
        "SVar:TrigLife:DB$ GainLife | LifeAmount$ 1\n",
        "Name:Resonator\nManaCost:0\nTypes:Artifact\n" +
        "A:AB$ CopySpellAbility | Cost$ T | TargetType$ Triggered.YouCtrl | ValidTgts$ Card,Emblem | MayChooseTarget$ True\n",
        "Name:Guardian\nManaCost:0\nTypes:Creature Cat Beast\nPT:1/4\n" +
        "T:Mode$ ChangesZone | ValidCard$ Card.Self | Origin$ Any | Destination$ Battlefield | Execute$ Exile | OptionalDecider$ You\n" +
        "SVar:Exile:DB$ ChangeZone | ValidTgts$ Permanent.Other+YouCtrl | Origin$ Battlefield | Destination$ Exile | RememberTargets$ True | ForgetOtherTargets$ True | SubAbility$ Return\n" +
        "SVar:Return:DB$ ChangeZone | Defined$ Remembered | Origin$ Exile | Destination$ Battlefield | GainControl$ True\n",
        "Name:Mage\nManaCost:0\nTypes:Creature Human Wizard\nPT:2/3\n" +
        "T:Mode$ ChangesZone | Origin$ Any | Destination$ Battlefield | ValidCard$ Card.Self | Execute$ TrigBounce\n" +
        "SVar:TrigBounce:DB$ ChangeZone | ValidTgts$ Creature.Other | Origin$ Battlefield | Destination$ Hand | RememberLKI$ True | SubAbility$ DBEffect\n" +
        "SVar:DBEffect:DB$ Effect | RememberObjects$ RememberedLKI & RememberedOwner | StaticAbilities$ CantCast | Duration$ UntilYourNextTurn | SubAbility$ DBCleanup\n" +
        "SVar:CantCast:Mode$ CantBeCast | ValidCard$ Card.nonLand+sharesNameWith RememberedLKI | Caster$ Player.IsRemembered\n" +
        "SVar:DBCleanup:DB$ Cleanup | ClearRemembered$ True\n",
        "Name:Fair\nManaCost:no cost\nTypes:Land\n" +
        "T:Mode$ Phase | Phase$ Upkeep | ValidPlayer$ You | TriggerZones$ Battlefield | Metalcraft$ True | Execute$ TrigGainLife\n" +
        "SVar:TrigGainLife:DB$ GainLife | Defined$ You | LifeAmount$ 1\n",
        "Name:Sentinel\nManaCost:0\nTypes:Creature Human Soldier\nPT:1/1\n" +
        "T:Mode$ SpellCast | ValidCard$ Card.nonCreature | ValidActivatingPlayer$ Player | Execute$ TrigDraw | TriggerZones$ Battlefield\n" +
        "SVar:TrigDraw:DB$ Draw | Defined$ You | UnlessCost$ X | UnlessPayer$ TriggeredActivator | NumCards$ 1\nSVar:X:Count$CardPower\n",
        "Name:Ping\nManaCost:0\nTypes:Sorcery\nA:SP$ GainLife | LifeAmount$ 1\n",
        "Name:Channel\nManaCost:no cost\nTypes:Land\n" +
        "A:AB$ DealDamage | Cost$ 0 Discard<1/CARDNAME> | ActivationZone$ Hand | ValidTgts$ Creature.attacking,Creature.blocking | NumDmg$ 4\n",
    };

    private static readonly CardPool Pool = new(ForgeLibrary.FromScripts(Scripts, new Dictionary<string, string>()), null);

    private static Game Setup(string[] hand, string[]? battlefield = null, string[]? library = null, TestDecider? decider = null) =>
        TestGames.Setup(Pool, hand, battlefield, library, decider);

    private static int Life(Game g) => g.State.Players[Game.Me].Life;

    [Fact]
    public void EveryTestCardCompiles()
    {
        foreach (var s in Scripts)
        {
            var name = s[5..s.IndexOf('\n')];
            Assert.Equal(name + ": ", name + ": " + string.Join("; ", Pool.Compile(name)!.Missing));
        }
    }

    [Fact]
    public void ReturnAnotherPermanentThatSharesAType()
    {
        var g = Setup(new[] { "Bear" }, battlefield: new[] { "Curio", "Wall", "Elk" });
        var bear = g.State[InHand(g, "Bear")];
        g.CastSpell(bear, -1);
        ResolveAll(g);
        Assert.Equal(Zone.Battlefield, bear.Zone);   // not the creature that entered
        Assert.Equal(Zone.Hand, g.State.Cards.First(c => c.Name == "Elk").Zone);   // the other creature
        Assert.Equal(Zone.Battlefield, OnBattlefield(g, "Wall").Zone);
    }

    [Fact]
    public void CopyATriggeredAbility()
    {
        var g = Setup(new[] { "Elk" }, battlefield: new[] { "Resonator" });
        g.CastSpell(g.State[InHand(g, "Elk")], -1);
        g.ResolveTop();   // the Elk enters
        var d = (TestDecider)g.Decider;
        // With the Elk's trigger on the stack, copy it.
        d.Choose = (game, dec) => dec.Kind == DecisionKind.Action && game.State.Stack.Any(i => i.IsTriggered && game.State[i.SourceId].Name == "Elk")
            ? dec.Options.ToList().FindIndex(o => o is GameAction.Activate) is var i and >= 0 ? i : 0
            : dec.Kind == DecisionKind.Action ? 0 : null;
        g.PriorityLoop();
        Assert.Equal(42, Life(g));
        Assert.True(OnBattlefield(g, "Resonator").Tapped);
    }

    [Fact]
    public void ExileAnotherPermanentAndReturnIt()
    {
        var g = Setup(new[] { "Guardian" }, battlefield: new[] { "Elk" });
        var elk = OnBattlefield(g, "Elk");
        elk.Tapped = true;
        g.CastSpell(g.State[InHand(g, "Guardian")], -1);
        ResolveAll(g);
        Assert.Equal(Zone.Battlefield, elk.Zone);
        Assert.False(elk.Tapped);   // it's a new object
        Assert.Equal(41, Life(g));  // and it entered again
    }

    [Fact]
    public void ItsOwnerCantCastSpellsWithThatName()
    {
        var g = Setup(new[] { "Mage", "Elk" }, battlefield: new[] { "Bear" });
        g.CastSpell(g.State[InHand(g, "Mage")], -1);
        ResolveAll(g);
        var bear = g.State[InHand(g, "Bear")];
        Assert.False(g.CanCast(bear, -1));
        Assert.True(g.CanCast(g.State[InHand(g, "Elk")], -1));   // other names are fine
    }

    [Fact]
    public void MetalcraftOnATrigger()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Fair", "Rock", "Rock" }, library: Fillers(3));
        g.State.Step = Step.Upkeep;
        g.FirePhase("Upkeep");
        ResolveAll(g);
        Assert.Equal(40, Life(g));

        var h = Setup(Array.Empty<string>(), battlefield: new[] { "Fair", "Rock", "Rock", "Rock" }, library: Fillers(3));
        h.State.Step = Step.Upkeep;
        h.FirePhase("Upkeep");
        ResolveAll(h);
        Assert.Equal(41, Life(h));
    }

    [Fact]
    public void DrawUnlessTheCasterPaysX()
    {
        var no = new TestDecider { Choose = (game, dec) => dec.Kind == DecisionKind.YesNo ? dec.Options.ToList().IndexOf(false) : null };
        var g = Setup(new[] { "Ping" }, battlefield: new[] { "Sentinel", "Peak" }, library: Fillers(3), decider: no);
        g.CastSpell(g.State[InHand(g, "Ping")], 0);
        ResolveAll(g);
        Assert.Single(g.State.Players[Game.Me].Hand);

        var yes = new TestDecider { Choose = (game, dec) => dec.Kind == DecisionKind.YesNo ? dec.Options.ToList().IndexOf(true) : null };
        var h = Setup(new[] { "Ping" }, battlefield: new[] { "Sentinel", "Peak" }, library: Fillers(3), decider: yes);
        h.CastSpell(h.State[InHand(h, "Ping")], 0);
        ResolveAll(h);
        Assert.Empty(h.State.Players[Game.Me].Hand);
        Assert.True(OnBattlefield(h, "Peak").Tapped);
    }

    [Fact]
    public void NothingBlocksAGoldfish()
    {
        var g = Setup(new[] { "Channel" }, battlefield: new[] { "Bear" });
        Assert.Empty(g.TargetCandidates(g.State[InHand(g, "Channel")], g.State[InHand(g, "Channel")].Activated[0], Game.Me));
    }

    private static string[] Fillers(int n) => Enumerable.Repeat("Filler", n).ToArray();
}
