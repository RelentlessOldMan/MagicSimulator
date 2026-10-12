using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using MagicSim.Core.Players;
using static MagicSim.Tests.TestGames;

namespace MagicSim.Tests;

/// <summary>
/// Repeatable modes, multikicker, jump-start, tapping everything, "whenever you counter a spell", choosing new targets
/// and soulbond, on hand-written cards in Forge's format.
/// </summary>
public class ModesAndKickerTests
{
    private static readonly string[] Scripts =
    {
        "Name:Bog\nTypes:Land\nA:AB$ Mana | Cost$ T | Produced$ B\n",
        "Name:Filler\nManaCost:9\nTypes:Sorcery\n",
        "Name:Bear\nManaCost:0\nTypes:Creature Bear\nPT:2/2\n",
        "Name:Pup\nManaCost:0\nTypes:Creature Dog\nPT:1/1\n",
        "Name:Confluence\nManaCost:0\nTypes:Sorcery\n" +
        "A:SP$ Charm | Choices$ DBHit,DBLife | CharmNum$ 3 | CanRepeatModes$ True\n" +
        "SVar:DBHit:DB$ DealDamage | Defined$ Player.Opponent | NumDmg$ 2 | SpellDescription$ 2 damage to each opponent.\n" +
        "SVar:DBLife:DB$ GainLife | LifeAmount$ 1 | SpellDescription$ Gain 1 life.\n",
        "Name:Storm Front\nManaCost:0\nTypes:Sorcery\nK:Multikicker:B\nA:SP$ GainLife | LifeAmount$ X\nSVar:X:Count$TimesKicked/Plus.1\n",
        "Name:Insight\nManaCost:0\nTypes:Instant\nK:Jump-start\nA:SP$ Draw | NumCards$ 2\n",
        "Name:Freeze\nManaCost:0\nTypes:Instant\nA:SP$ TapAll | ValidCards$ Creature.Bear\n",
        "Name:Dud\nManaCost:0\nTypes:Sorcery\nA:SP$ Draw | NumCards$ 1\n",
        "Name:Nope\nManaCost:0\nTypes:Instant\nA:SP$ Counter | TargetType$ Spell | ValidTgts$ Card\n",
        "Name:Clerk\nManaCost:0\nTypes:Creature Human Wizard\nPT:1/3\n" +
        "T:Mode$ Countered | ValidCause$ SpellAbility.YouCtrl | ValidSA$ Spell | TriggerZones$ Battlefield | Execute$ TrigLife\n" +
        "SVar:TrigLife:DB$ GainLife | LifeAmount$ 1\n",
        "Name:Swat\nManaCost:0\nTypes:Instant\n" +
        "A:SP$ ChangeTargets | TargetType$ Spell,Activated,Triggered | Optional$ True | ValidTgts$ Card,Emblem\n",
        "Name:Lookout\nManaCost:0\nTypes:Creature Human Scout\nPT:2/1\nK:Soulbond\n" +
        "S:Mode$ Continuous | Affected$ Creature.PairedWith,Creature.Self+Paired | AddPower$ 1\n",
    };

    private static readonly CardPool Pool = new(ForgeLibrary.FromScripts(Scripts, new Dictionary<string, string>()), null);

    private static Game Setup(string[] hand, string[]? battlefield = null, string[]? library = null) =>
        TestGames.Setup(Pool, hand, battlefield, library);

    private static void Cast(Game g, string name, int index = 0)
    {
        var c = g.State[InHand(g, name)];
        g.CastSpell(c, c.Types.IsPermanent ? -1 : index);
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
    public void TheSameModeMoreThanOnce()
    {
        // Two modes, choose three with repetition: hit×3, hit×2 + life, hit + life×2, life×3.
        Assert.Equal(4, Pool.Resolve("Confluence")!.Front.SpellAbilities.Count);
        var g = Setup(new[] { "Confluence" });
        Cast(g, "Confluence", 0);
        Assert.Equal(34, g.State.Players[1].Life);
        var h = Setup(new[] { "Confluence" });
        Cast(h, "Confluence", 1);
        Assert.Equal(36, h.State.Players[1].Life);
        Assert.Equal(41, Life(h));
    }

    [Fact]
    public void KickedAnyNumberOfTimes()
    {
        var g = Setup(new[] { "Storm Front" }, battlefield: new[] { "Bog", "Bog", "Bog" });
        var spell = g.State[InHand(g, "Storm Front")];
        Assert.Equal(1 + CardCompiler.MaxMultikicks, spell.CurrentFace.SpellAbilities.Count);
        Assert.True(g.CanCast(spell, 3));    // kicked three times: {B}{B}{B}
        Assert.False(g.CanCast(spell, 4));
        Cast(g, "Storm Front", 3);
        Assert.Equal(44, Life(g));
    }

    [Fact]
    public void JumpStartFromTheGraveyardByDiscardingACard()
    {
        var g = Setup(new[] { "Filler" }, library: new[] { "Insight", "Filler", "Filler", "Filler" });
        var insight = Named(g, "Insight");
        g.MoveTo(insight, Zone.Graveyard);
        Assert.True(g.CanCast(insight, 0));
        g.CastSpell(insight, 0);
        ResolveAll(g);
        Assert.Equal(Zone.Exile, insight.Zone);
        Assert.Equal(2, g.State.Players[Game.Me].Hand.Count);
        Assert.Single(g.State.Players[Game.Me].Graveyard);

        var h = Setup(Array.Empty<string>(), library: new[] { "Insight", "Filler" });
        h.MoveTo(Named(h, "Insight"), Zone.Graveyard);
        Assert.False(h.CanCast(Named(h, "Insight"), 0));   // nothing to discard
    }

    [Fact]
    public void TapAllTheCreaturesThatFit()
    {
        var g = Setup(new[] { "Freeze" }, battlefield: new[] { "Bear", "Pup" });
        Cast(g, "Freeze");
        Assert.True(OnBattlefield(g, "Bear").Tapped);
        Assert.False(OnBattlefield(g, "Pup").Tapped);
    }

    [Fact]
    public void WheneverYouCounterASpell()
    {
        var g = Setup(new[] { "Dud", "Nope" }, battlefield: new[] { "Clerk" }, library: new[] { "Filler", "Filler" });
        g.CastSpell(g.State[InHand(g, "Dud")], 0);
        g.CastSpell(g.State[InHand(g, "Nope")], 0);
        ResolveAll(g);
        Assert.Equal(Zone.Graveyard, Named(g, "Dud").Zone);
        Assert.Empty(g.State.Players[Game.Me].Hand);   // Dud never resolved
        Assert.Equal(41, Life(g));
    }

    [Fact]
    public void SoulbondPairsWhenEitherEntersAndEndsWhenOneLeaves()
    {
        var g = Setup(new[] { "Lookout", "Bear", "Pup" });
        Cast(g, "Lookout");
        var lookout = OnBattlefield(g, "Lookout");
        Assert.Null(lookout.PairedWith);
        Assert.Equal(2, lookout.Power);

        Cast(g, "Bear");
        var bear = OnBattlefield(g, "Bear");
        Assert.Equal(bear.Id, lookout.PairedWith);
        Assert.Equal(3, lookout.Power);
        Assert.Equal(3, bear.Power);

        Cast(g, "Pup");   // the Lookout is already paired
        Assert.Null(OnBattlefield(g, "Pup").PairedWith);

        g.MoveTo(bear, Zone.Graveyard);
        ResolveAll(g);
        Assert.Null(lookout.PairedWith);
        Assert.Equal(2, lookout.Power);
    }
}
