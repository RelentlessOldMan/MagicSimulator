using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using MagicSim.Core.Players;
using static MagicSim.Tests.TestGames;

namespace MagicSim.Tests;

/// <summary>
/// Suspend with X, giving suspend, adding or removing a counter, "whenever a time counter is removed" from exile,
/// paying {0} once each turn, extra upkeeps and beginning phases, and copying a spell for its target's controller,
/// on hand-written cards in Forge's format.
/// </summary>
public class SuspendAndPhaseTests
{
    private static readonly string[] Scripts =
    {
        "Name:Peak\nTypes:Land Mountain\nA:AB$ Mana | Cost$ T | Produced$ R\n",
        "Name:Filler\nManaCost:9\nTypes:Sorcery\n",
        "Name:Bear\nManaCost:2\nTypes:Creature Bear\nPT:2/2\n",
        "Name:Imp\nManaCost:0\nTypes:Creature Imp\nPT:1/1\n",
        "Name:Giant\nManaCost:6\nTypes:Creature Giant\nPT:6/6\n",
        "Name:Kraken\nManaCost:6 U U\nTypes:Creature Kraken\nPT:6/6\nK:Suspend:9:2 U\n" +
        "T:Mode$ SpellCast | ValidCard$ Card | ValidActivatingPlayer$ Opponent | Execute$ TrigRemoveCounter | TriggerZones$ Exile | IsPresent$ Card.Self+suspended | PresentZone$ Exile\n" +
        "SVar:TrigRemoveCounter:DB$ RemoveCounter | Defined$ Self | CounterType$ TIME | CounterNum$ 1\n",
        "Name:Slowpoke\nManaCost:5\nTypes:Sorcery\nK:Suspend:3:R\nA:SP$ GainLife | LifeAmount$ 5\n",
        "Name:Spinner\nManaCost:0\nTypes:Instant\n" +
        "A:SP$ AddOrRemoveCounter | CounterNum$ 1 | ValidTgts$ Permanent.inZoneBattlefield,Card.suspended | TgtZone$ Battlefield,Exile\n",
        "Name:Chronicler\nManaCost:3 R\nTypes:Creature Avatar\nPT:3/3\nK:Suspend:X:XMin1 X R\n" +
        "T:Mode$ CounterRemoved | ValidCard$ Card.Self | TriggerZones$ Exile | CounterType$ TIME | Execute$ TrigDraw\n" +
        "SVar:TrigDraw:DB$ Draw | Defined$ You\n",
        "Name:Jhoira\nManaCost:1 U R\nTypes:Legendary Creature Human Wizard\nPT:2/2\n" +
        "A:AB$ PutCounter | Cost$ ExileFromHand<1/Card.nonLand/nonland card> | Defined$ ExiledCards | CounterType$ TIME | CounterNum$ 4 | SubAbility$ GiveSuspend\n" +
        "SVar:GiveSuspend:DB$ Pump | Defined$ ExiledCards.withoutSuspend | KW$ Suspend | PumpZone$ Exile | Duration$ Permanent\n",
        "Name:Foretold\nManaCost:2 U\nTypes:Enchantment\n" +
        "S:Mode$ Continuous | MayPlay$ True | MayPlayAltManaCost$ 0 | MayPlayLimit$ 1 | MayPlayDontGrantZonePermissions$ True | Affected$ Card.nonLand+cmcLEX | AffectedZone$ Hand,Graveyard,Library,Exile,Command\n" +
        "SVar:X:Count$CardCounters.TIME\n",
        "Name:Haze\nManaCost:0\nTypes:Enchantment Aura\nK:Enchant:Player\n" +
        "T:Mode$ Phase | FirstUpkeep$ True | ValidPlayer$ Player.EnchantedBy | TriggerZones$ Battlefield | Execute$ AddUpkeep\n" +
        "SVar:AddUpkeep:DB$ AddPhase | ExtraPhase$ Upkeep\n",
        "Name:Keeper\nManaCost:1\nTypes:Enchantment\n" +
        "T:Mode$ Phase | Phase$ Upkeep | ValidPlayer$ You | TriggerZones$ Battlefield | Execute$ TrigGain\nSVar:TrigGain:DB$ GainLife | LifeAmount$ 1\n",
        "Name:Sphinx\nManaCost:6 U\nTypes:Creature Sphinx\nPT:0/7\n" +
        "T:Mode$ Phase | Phase$ Main2 | ValidPlayer$ You | TriggerZones$ Battlefield | Execute$ TrigAddPhase\n" +
        "SVar:TrigAddPhase:DB$ AddPhase | ExtraPhase$ Beginning\n",
        "Name:Chain\nManaCost:0\nTypes:Instant\n" +
        "A:SP$ ChangeZone | ValidTgts$ Permanent.nonLand | Origin$ Battlefield | Destination$ Hand | SubAbility$ DBCopy\n" +
        "SVar:DBCopy:DB$ CopySpellAbility | Defined$ Parent | Controller$ TargetedController | UnlessPayer$ TargetedController | UnlessCost$ Sac<1/Land> | UnlessSwitched$ True | MayChooseTarget$ True\n",
    };

    private static readonly CardPool Pool = new(ForgeLibrary.FromScripts(Scripts, new Dictionary<string, string>()), null);

    private static Game Setup(string[] hand, string[]? battlefield = null, string[]? library = null, TestDecider? decider = null) =>
        TestGames.Setup(Pool, hand, battlefield, library, decider);

    private static string[] Fillers(int n) => Enumerable.Repeat("Filler", n).ToArray();

    private static int Life(Game g) => g.State.Players[Game.Me].Life;

    private static void Cast(Game g, string name)
    {
        var card = g.State[InHand(g, name)];
        g.CastSpell(card, card.CurrentFace.SpellAbilities.Count == 0 ? -1 : 0);
        ResolveAll(g);
    }

    private static Card Suspend(Game g, string name)
    {
        var card = g.State[InHand(g, name)];
        g.ActivateAbility(card, card.Activated.ToList().FindIndex(a => a.Api == "Suspend"));
        ResolveAll(g);
        return card;
    }

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
    public void RemoveATimeCounterFromASuspendedCard()
    {
        var g = Setup(new[] { "Slowpoke", "Spinner" }, new[] { "Peak" },
            decider: new TestDecider { Choose = (game, d) => d.Kind == DecisionKind.Target ? TestDecider.Named(game, d, "Slowpoke") : null });
        var slowpoke = Suspend(g, "Slowpoke");
        Assert.True(slowpoke.Suspended);
        Assert.Equal(3, slowpoke.CounterCount("TIME"));
        Cast(g, "Spinner");
        Assert.Equal(2, slowpoke.CounterCount("TIME"));
    }

    [Fact]
    public void SuspendXAndDrawForEachTimeCounterRemoved()
    {
        var g = Setup(new[] { "Chronicler" }, new[] { "Peak", "Peak", "Peak" }, Fillers(5));
        var chronicler = Suspend(g, "Chronicler");
        Assert.Equal(2, chronicler.CounterCount("TIME"));
        g.RemoveCounters(chronicler, "TIME", 1);
        ResolveAll(g);
        Assert.Single(g.State.Players[Game.Me].Hand);
    }

    [Fact]
    public void ExiledCardGainsSuspend()
    {
        var g = Setup(new[] { "Giant" }, new[] { "Jhoira" });
        var giant = g.State[InHand(g, "Giant")];
        g.ActivateAbility(OnBattlefield(g, "Jhoira"), 0);
        ResolveAll(g);
        Assert.Equal(Zone.Exile, giant.Zone);
        Assert.Equal(4, giant.CounterCount("TIME"));
        Assert.True(giant.Suspended);
    }

    [Fact]
    public void PayNothingForOneSpellEachTurn()
    {
        var g = Setup(new[] { "Bear", "Bear" }, new[] { "Foretold" });
        g.PutCounters(OnBattlefield(g, "Foretold"), "TIME", 2);
        var bears = g.State.Players[Game.Me].Hand.Select(id => g.State[id]).ToList();
        Assert.True(g.CanCast(bears[0], -1));
        g.CastSpell(bears[0], -1);
        ResolveAll(g);
        Assert.False(g.CanCast(bears[1], -1));
        g.State.Turn++;
        Assert.True(g.CanCast(bears[1], -1));
    }

    [Fact]
    public void AnAdditionalUpkeepOnlyAfterTheFirst()
    {
        var g = Setup(new[] { "Haze" }, new[] { "Keeper" }, Fillers(10),
            new TestDecider { Choose = (game, d) => d.Kind == DecisionKind.Target ? d.Options.ToList().FindIndex(o => o is TargetRef { Player: Game.Me }) : null });
        Cast(g, "Haze");
        g.ResumeTurn();
        g.RunTurn();
        Assert.Equal(42, Life(g));
    }

    [Fact]
    public void AnAdditionalBeginningPhaseAfterTheSecondMainPhase()
    {
        var g = Setup(Array.Empty<string>(), new[] { "Sphinx", "Keeper" }, Fillers(10));
        g.State.Turn = 2;   // the first turn on the play has no draw
        g.ResumeTurn();
        Assert.Equal(41, Life(g));
        Assert.Single(g.State.Players[Game.Me].Hand);
    }

    [Fact]
    public void SacrificeALandToCopyTheSpell()
    {
        var g = Setup(new[] { "Chain" }, new[] { "Bear", "Imp", "Peak" },
            decider: new TestDecider { Choose = (game, d) => d.Kind == DecisionKind.Target ? TestDecider.Named(game, d, "Bear") is >= 0 and var i ? i : null : null });
        Cast(g, "Chain");
        var hand = g.State.Players[Game.Me].Hand.Select(id => g.State[id].Name).ToList();
        Assert.Contains("Bear", hand);
        Assert.Contains("Imp", hand);
        Assert.Equal(Zone.Graveyard, g.State.Cards.First(c => c.Name == "Peak").Zone);
    }
}
