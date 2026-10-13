using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using MagicSim.Core.Players;
using static MagicSim.Tests.TestGames;

namespace MagicSim.Tests;

/// <summary>
/// "You can't play lands", an enchanted player who can't activate abilities, cleanup-step and "that player's turn"
/// delayed triggers, card types in a graveyard, every player choosing a number, and exchanging control, on hand-written
/// cards in Forge's format.
/// </summary>
public class PlayerControlTests
{
    private static readonly string[] Scripts =
    {
        "Name:Peak\nTypes:Land Mountain\nA:AB$ Mana | Cost$ T | Produced$ R\n",
        "Name:Filler\nManaCost:9\nTypes:Sorcery\n",
        "Name:Imp\nManaCost:1\nTypes:Creature Imp\nPT:1/1\n",
        "Name:Giant\nManaCost:6\nTypes:Creature Giant\nPT:6/6\n",
        "Name:Totem\nManaCost:2\nTypes:Artifact\nA:AB$ GainLife | Cost$ T | LifeAmount$ 1\n",
        "Name:Mining\nManaCost:R\nTypes:Enchantment\nS:Mode$ CantPlayLand | Player$ You\n",
        "Name:Splendor\nManaCost:0\nTypes:Enchantment Aura Curse\nK:Enchant:Player\nSVar:AttachAILogic:Curse\n" +
        "S:Mode$ CantBeActivated | Activator$ Player.EnchantedBy | ValidSA$ Activated.!ManaAbility+!Loyalty\n",
        "Name:Sweeper\nManaCost:0\nTypes:Sorcery\n" +
        "A:SP$ DelayedTrigger | Mode$ Phase | Phase$ Cleanup | Execute$ DBGain | Static$ True\nSVar:DBGain:DB$ GainLife | LifeAmount$ 2\n",
        "Name:Slaver\nManaCost:0\nTypes:Sorcery\n" +
        "A:SP$ ControlPlayer | ValidTgts$ Opponent | SubAbility$ DBDelTrig\n" +
        "SVar:DBDelTrig:DB$ DelayedTrigger | Mode$ Phase | DelayedTriggerDefinedPlayer$ Targeted | Phase$ Cleanup | Execute$ DBGain | Static$ True\n" +
        "SVar:DBGain:DB$ GainLife | LifeAmount$ 5\n",
        "Name:Promised\nManaCost:0\nTypes:Sorcery\nA:SP$ GainLife | LifeAmount$ X\nSVar:X:Count$ValidGraveyard Card.YouOwn$CardTypes\n",
        "Name:Wheel\nManaCost:0\nTypes:Sorcery\n" +
        "A:SP$ ChooseNumber | Defined$ Player | Secretly$ True | Highest$ DBLoseLife | NotLowest$ DBDiscard\n" +
        "SVar:DBLoseLife:DB$ DealDamage | Defined$ Remembered | NumDmg$ Count$ChosenNumber\n" +
        "SVar:DBDiscard:DB$ Discard | Defined$ Remembered | Mode$ Hand | SubAbility$ DBDraw\n" +
        "SVar:DBDraw:DB$ Draw | Defined$ Remembered | NumCards$ 7\n",
        "Name:Mischief\nManaCost:0\nTypes:Sorcery\n" +
        "A:SP$ Pump | ValidTgts$ Permanent.YouCtrl+nonLand | SubAbility$ DBExchange\n" +
        "SVar:DBExchange:DB$ ExchangeControl | Defined$ ParentTarget | ValidTgts$ Permanent.OppCtrl+nonLand | TargetsWithRelatedProperty$ LECMC\n",
    };

    private static readonly CardPool Pool = new(ForgeLibrary.FromScripts(Scripts, new Dictionary<string, string>()), null);

    private static Game Setup(string[] hand, string[]? battlefield = null, string[]? library = null, TestDecider? decider = null) =>
        TestGames.Setup(Pool, hand, battlefield, library, decider);

    private static string[] Fillers(int n) => Enumerable.Repeat("Filler", n).ToArray();

    private static int Life(Game g, int player = Game.Me) => g.State.Players[player].Life;

    private static void Cast(Game g, string name)
    {
        var card = g.State[InHand(g, name)];
        g.CastSpell(card, card.CurrentFace.SpellAbilities.Count == 0 ? -1 : 0);
        ResolveAll(g);
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
    public void YouCantPlayLands()
    {
        var g = Setup(new[] { "Peak" }, new[] { "Mining" });
        Assert.DoesNotContain(g.LegalActions(Game.Me), a => a is GameAction.PlayLand);
    }

    [Fact]
    public void TheEnchantedPlayerCantActivateAbilities()
    {
        var g = Setup(new[] { "Splendor" }, new[] { "Totem" },
            decider: new TestDecider { Choose = (game, d) => d.Kind == DecisionKind.Target ? d.Options.ToList().FindIndex(o => o is TargetRef { Player: 1 }) : null });
        var totem = OnBattlefield(g, "Totem");
        Cast(g, "Splendor");
        Assert.Contains(g.LegalActions(Game.Me), a => a is GameAction.Activate act && act.CardId == totem.Id);
        totem.Controller = 1;   // the cursed player's ability
        Assert.True(Statics.CantBeActivated(g, totem, totem.Activated[0]));
    }

    [Fact]
    public void AtTheBeginningOfTheCleanupStep()
    {
        var g = Setup(new[] { "Sweeper" }, library: Fillers(5));
        Cast(g, "Sweeper");
        g.ResumeTurn();
        Assert.Equal(42, Life(g));
    }

    [Fact]
    public void ADelayedTriggerOnlyInThatPlayersTurn()
    {
        var g = Setup(new[] { "Slaver" }, library: Fillers(5));
        Cast(g, "Slaver");
        g.ResumeTurn();
        Assert.Equal(40, Life(g));   // not at your own cleanup
        g.RunOpponentTurns();
        Assert.Equal(45, Life(g));
    }

    [Fact]
    public void CountCardTypesInYourGraveyard()
    {
        var g = Setup(new[] { "Promised" }, new[] { "Peak", "Imp", "Totem" });
        foreach (var c in g.State.BattlefieldCards.ToList()) g.MoveTo(c, Zone.Graveyard);
        Cast(g, "Promised");
        Assert.Equal(43, Life(g));   // land, creature and artifact; the sorcery itself isn't there yet
    }

    [Fact]
    public void ChooseOneToWheelYourHand()
    {
        var g = Setup(new[] { "Wheel", "Imp" }, library: Fillers(10),
            decider: new TestDecider { Choose = (game, d) => d.Kind == DecisionKind.Option ? 1 : null });
        Cast(g, "Wheel");
        Assert.Equal(39, Life(g));
        Assert.Equal(7, g.State.Players[Game.Me].Hand.Count);
        Assert.Equal(40, Life(g, 1));

        var h = Setup(new[] { "Wheel", "Imp" }, library: Fillers(10));
        Cast(h, "Wheel");
        Assert.Equal(40, Life(h));
        Assert.Single(h.State.Players[Game.Me].Hand);
    }

    [Fact]
    public void ExchangeForAPermanentWithLesserManaValue()
    {
        var g = Setup(new[] { "Mischief" }, new[] { "Totem", "Giant", "Imp" });
        var (totem, giant, imp) = (OnBattlefield(g, "Totem"), OnBattlefield(g, "Giant"), OnBattlefield(g, "Imp"));
        giant.Controller = imp.Controller = 1;
        Assert.DoesNotContain(g.TargetCandidates(totem, Pool.Resolve("Mischief")!.Faces[0].SpellAbilities[0].Sub!, Game.Me), t => t.CardId == totem.Id);
        Cast(g, "Mischief");
        Assert.Equal(1, totem.Controller);
        Assert.Equal(Game.Me, imp.Controller);
        Assert.Equal(1, giant.Controller);
    }
}
