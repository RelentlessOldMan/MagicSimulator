using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using MagicSim.Core.Players;
using static MagicSim.Tests.TestGames;

namespace MagicSim.Tests;

/// <summary>
/// Buyback, miracle, experience counters, removing counters if you choose, damage divided evenly with a cost per extra
/// target, copies controlled by the caster and "if this was cast from a graveyard", on hand-written cards in Forge's format.
/// </summary>
public class BuybackAndMiracleTests
{
    private static readonly string[] Scripts =
    {
        "Name:Peak\nTypes:Land\nA:AB$ Mana | Cost$ T | Produced$ R\n",
        "Name:Filler\nManaCost:9\nTypes:Sorcery\n",
        "Name:Big\nManaCost:3\nTypes:Sorcery\nA:SP$ GainLife | LifeAmount$ 1\n",
        "Name:Ping\nManaCost:0\nTypes:Sorcery\nA:SP$ GainLife | LifeAmount$ 1\n",
        "Name:Speculate\nManaCost:R\nTypes:Sorcery\nK:Buyback:2\nA:SP$ GainLife | LifeAmount$ 1\n",
        "Name:Reforge\nManaCost:5\nTypes:Sorcery\nK:Miracle:R\nA:SP$ GainLife | LifeAmount$ 5\n",
        "Name:Mentor\nManaCost:0\nTypes:Creature Goblin Wizard\nPT:2/2\n" +
        "T:Mode$ SpellCast | ValidCard$ Instant.cmcGTX,Sorcery.cmcGTX | ValidActivatingPlayer$ You | TriggerZones$ Battlefield | Execute$ TrigExp\n" +
        "SVar:TrigExp:DB$ PutCounter | Defined$ You | CounterType$ Experience | CounterNum$ 1\n" +
        "S:Mode$ ReduceCost | ValidCard$ Instant,Sorcery | Type$ Spell | Activator$ You | Amount$ X\n" +
        "SVar:X:Count$YourCountersExperience\n",
        "Name:Battery\nManaCost:0\nTypes:Artifact\n" +
        "A:AB$ RemoveCounter | Cost$ 0 | Defined$ Self | CounterType$ CHARGE | CounterNum$ All | Optional$ True | RememberAmount$ True | SubAbility$ DBLife\n" +
        "SVar:DBLife:DB$ GainLife | LifeAmount$ X | SubAbility$ DBCleanup\nSVar:X:Count$RememberedSize\n" +
        "SVar:DBCleanup:DB$ Cleanup | ClearRemembered$ True\n",
        "Name:Blast\nManaCost:X R\nTypes:Sorcery\n" +
        "S:Mode$ RaiseCost | ValidCard$ Card.Self | Type$ Spell | Amount$ More | Relative$ True | EffectZone$ All\n" +
        "A:SP$ DealDamage | ValidTgts$ Any | NumDmg$ X | TargetMin$ 0 | TargetMax$ MaxTargets | DivideEvenly$ RoundedDown\n" +
        "SVar:X:Count$xPaid\nSVar:MaxTargets:PlayerCountPlayers$Amount\nSVar:More:TargetedObjects$Amount/Minus.1\n",
        "Name:Encore\nManaCost:0\nTypes:Sorcery\n" +
        "A:SP$ Effect | Triggers$ TrigCast\n" +
        "SVar:TrigCast:Mode$ SpellCast | ValidCard$ Instant,Sorcery | TriggerZones$ Command | ValidActivatingPlayer$ Player | Execute$ TrigCopy\n" +
        "SVar:TrigCopy:DB$ CopySpellAbility | Defined$ TriggeredSpellAbility | Controller$ TriggeredCardController | MayChooseTarget$ True\n",
        "Name:Vengeance\nManaCost:0\nTypes:Instant\nK:Flashback:0\n" +
        "A:SP$ CopySpellAbility | ValidTgts$ Instant.YouCtrl,Sorcery.YouCtrl | TargetType$ Spell | Amount$ X | MayChooseTarget$ True\n" +
        "SVar:X:Count$wasCastFromGraveyard.2.1\n",
        "Name:Zap\nManaCost:0\nTypes:Instant\nA:SP$ DealDamage | ValidTgts$ Creature.tapped | NumDmg$ 1\n",
        "Name:Bear\nManaCost:0\nTypes:Creature Bear\nPT:2/2\n",
    };

    private static readonly CardPool Pool = new(ForgeLibrary.FromScripts(Scripts, new Dictionary<string, string>()), null);

    private static Game Setup(string[] hand, string[]? battlefield = null, string[]? library = null, TestDecider? decider = null) =>
        TestGames.Setup(Pool, hand, battlefield, library, decider);

    private static void Cast(Game g, string name, int index = 0)
    {
        g.CastSpell(g.State[InHand(g, name)], index);
        ResolveAll(g);
    }

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
    public void BuybackReturnsItToYourHand()
    {
        var g = Setup(new[] { "Speculate" }, battlefield: new[] { "Peak", "Peak", "Peak" });
        var spell = Named(g, "Speculate");
        Assert.Equal(2, spell.CurrentFace.SpellAbilities.Count);
        Cast(g, "Speculate", 1);
        Assert.Equal(41, Life(g));
        Assert.Equal(Zone.Hand, spell.Zone);

        var h = Setup(new[] { "Speculate" }, battlefield: new[] { "Peak" });
        Assert.False(h.CanCast(Named(h, "Speculate"), 1));
        Cast(h, "Speculate", 0);
        Assert.Equal(Zone.Graveyard, Named(h, "Speculate").Zone);
    }

    [Fact]
    public void MiracleCastsTheFirstCardDrawnThisTurnCheaply()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Peak" }, library: new[] { "Reforge", "Reforge" });
        var first = g.State[g.State.Players[Game.Me].Library[0]];
        Assert.False(g.CanCast(first, 1));   // not while it's just in the library
        g.Draw(Game.Me, 1);
        ResolveAll(g);
        Assert.Equal(45, Life(g));
        Assert.Equal(Zone.Graveyard, first.Zone);

        g.Draw(Game.Me, 1);   // the second card this turn: no miracle
        ResolveAll(g);
        Assert.Equal(45, Life(g));
        Assert.Single(g.State.Players[Game.Me].Hand);
        Assert.False(g.CanCast(g.State[g.State.Players[Game.Me].Hand[0]], 1));
    }

    [Fact]
    public void ExperienceCountersMakeSpellsCheaper()
    {
        var g = Setup(new[] { "Big", "Big", "Ping" }, battlefield: new[] { "Mentor", "Peak", "Peak", "Peak", "Peak", "Peak", "Peak" });
        Cast(g, "Ping");   // mana value 0 isn't greater than 0 experience
        Assert.Equal(0, g.State.Players[Game.Me].CounterCount("Experience"));
        Cast(g, "Big");
        Assert.Equal(1, g.State.Players[Game.Me].CounterCount("Experience"));
        Cast(g, "Big");    // costs 2 now
        Assert.Equal(2, g.State.Players[Game.Me].CounterCount("Experience"));
        Assert.Single(g.State.BattlefieldCards, c => c.Name == "Peak" && !c.Tapped);
    }

    [Fact]
    public void RemoveAllTheCountersAndCountThem()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Battery" });
        var battery = OnBattlefield(g, "Battery");
        g.PutCounters(battery, "CHARGE", 3);
        g.ActivateAbility(battery, 0);
        ResolveAll(g);
        Assert.Equal(43, Life(g));
        Assert.Equal(0, battery.CounterCount("CHARGE"));

        var d = new TestDecider { Choose = (game, dec) => dec.Kind == DecisionKind.YesNo ? dec.Options.ToList().IndexOf(false) : null };
        var h = Setup(Array.Empty<string>(), battlefield: new[] { "Battery" }, decider: d);
        var kept = OnBattlefield(h, "Battery");
        h.PutCounters(kept, "CHARGE", 3);
        h.ActivateAbility(kept, 0);
        ResolveAll(h);
        Assert.Equal(40, Life(h));
        Assert.Equal(3, kept.CounterCount("CHARGE"));
    }

    [Fact]
    public void DamageDividedEvenlyTakesOneTargetForItsCost()
    {
        var d = new TestDecider
        {
            Choose = (game, dec) => dec.Kind == DecisionKind.X ? dec.Options.ToList().IndexOf(4)
                : dec.Kind == DecisionKind.Target ? dec.Options.ToList().FindIndex(o => o is TargetRef { Player: 1 }) : null,
        };
        var g = Setup(new[] { "Blast" }, battlefield: new[] { "Peak", "Peak", "Peak", "Peak", "Peak" }, decider: d);
        Cast(g, "Blast");
        Assert.Equal(36, g.State.Players[1].Life);
        Assert.Equal(40, g.State.Players[2].Life);
    }

    [Fact]
    public void ThatPlayerCopiesTheSpell()
    {
        var g = Setup(new[] { "Encore", "Ping" });
        Cast(g, "Encore");
        Cast(g, "Ping");
        Assert.Equal(42, Life(g));
    }

    [Fact]
    public void CopyTwiceWhenCastFromTheGraveyard()
    {
        var g = Setup(new[] { "Ping", "Vengeance" });
        g.CastSpell(g.State[InHand(g, "Ping")], 0);
        g.CastSpell(g.State[InHand(g, "Vengeance")], 0);
        ResolveAll(g);
        Assert.Equal(42, Life(g));

        var h = Setup(new[] { "Ping" }, library: new[] { "Vengeance" });
        h.MoveTo(Named(h, "Vengeance"), Zone.Graveyard);
        h.CastSpell(h.State[InHand(h, "Ping")], 0);
        h.CastSpell(Named(h, "Vengeance"), 0);
        ResolveAll(h);
        Assert.Equal(43, Life(h));
    }

    [Fact]
    public void ACopyWithNoNewTargetKeepsTheOriginal()
    {
        var g = Setup(new[] { "Zap" }, battlefield: new[] { "Bear" });
        var bear = OnBattlefield(g, "Bear");
        bear.Tapped = true;
        g.CastSpell(g.State[InHand(g, "Zap")], 0);
        bear.Tapped = false;   // nothing is a legal new target now
        g.CopySpell(g.State.Stack[^1], Game.Me, newTargets: true);
        Assert.Single(g.State.Stack[^1].ChainTargets[0]);
        ResolveAll(g);
        Assert.Equal(Zone.Graveyard, bear.Zone);
    }
}
