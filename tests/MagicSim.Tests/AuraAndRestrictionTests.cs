using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using MagicSim.Core.Players;
using static MagicSim.Tests.TestGames;

namespace MagicSim.Tests;

/// <summary>
/// Auras attached to a card, enchanted creatures, losing land types, "its activated abilities can't be activated",
/// activation restrictions, mana-value base power, life lost this turn and triggers that never fire against a goldfish,
/// on hand-written cards in Forge's format.
/// </summary>
public class AuraAndRestrictionTests
{
    private static readonly string[] Scripts =
    {
        "Name:Peak\nTypes:Land Mountain\nA:AB$ Mana | Cost$ T | Produced$ R\n",
        "Name:Bear\nManaCost:2\nTypes:Creature Bear\nPT:2/2\n",
        "Name:Might\nManaCost:0\nTypes:Enchantment Aura\nK:Enchant:Creature\n",
        "Name:Dancer\nManaCost:2\nTypes:Creature Kor\nPT:0/3\n" +
        "S:Mode$ Continuous | AffectedDefined$ Self | AddPower$ X | AddToughness$ X\nSVar:X:Count$Valid Aura.Attached/Times.2\n",
        "Name:Ward\nManaCost:2\nTypes:Enchantment\nS:Mode$ Continuous | Affected$ Creature.enchanted+YouCtrl | AddKeyword$ Shroud\n",
        "Name:Song\nManaCost:0\nTypes:Enchantment Aura\nK:Enchant:Permanent:permanent\n" +
        "S:Mode$ Continuous | AffectedDefined$ Enchanted | SetColor$ Colorless | AddType$ Land & Forest | RemoveCardTypes$ True | RemoveLandTypes$ True\n",
        "Name:Totem\nManaCost:2\nTypes:Artifact\nA:AB$ GainLife | Cost$ T | LifeAmount$ 1\nA:AB$ Mana | Cost$ T | Produced$ R\n",
        "Name:Fetters\nManaCost:0\nTypes:Enchantment Aura\nK:Enchant:Permanent\n" +
        "S:Mode$ CantAttack,CantBlock,CantBeActivated | ValidCard$ Card.EnchantedBy | ValidSA$ Activated.!ManaAbility\n",
        "Name:Silence\nManaCost:2\nTypes:Enchantment\nS:Mode$ CantBeActivated | ValidCard$ Artifact | ValidSA$ Activated\n",
        "Name:Ember\nManaCost:R R\nTypes:Enchantment\n",
        "Name:Starfield\nManaCost:4\nTypes:Enchantment\n" +
        "S:Mode$ Continuous | IsPresent$ Enchantment.YouCtrl | PresentCompare$ GE2 | Affected$ Enchantment.YouCtrl+nonAura+Other | SetPower$ AffectedX | SetToughness$ AffectedX | AddType$ Creature\n" +
        "SVar:AffectedX:Count$CardManaCost\n",
        "Name:Ascension\nManaCost:2\nTypes:Enchantment\n" +
        "A:AB$ GainLife | Cost$ 0 | LifeAmount$ X | IsPresent$ Card.Self+counters_GE1_QUEST\nSVar:X:Count$LifeYouLostThisTurn\n",
        "Name:Drain\nManaCost:0\nTypes:Sorcery\nA:SP$ LoseLife | Defined$ You | LifeAmount$ 3\n",
        "Name:Justice\nManaCost:2\nTypes:Enchantment\n" +
        "T:Mode$ Destroyed | ValidCauser$ Player.Opponent | ValidCard$ Permanent.nonCreature+YouCtrl | TriggerZones$ Battlefield | Execute$ TrigDestroy\n" +
        "SVar:TrigDestroy:DB$ Destroy | ValidTgts$ Permanent | TargetsWithDefinedController$ TriggeredCauser\n",
        "Name:Grasp\nManaCost:0\nTypes:Enchantment\n" +
        "T:Mode$ ChangesZone | Origin$ Any | Destination$ Battlefield | ValidCard$ Card.Self | Execute$ TrigExile\n" +
        "SVar:TrigExile:DB$ ChangeZone | Origin$ Battlefield | Destination$ Exile | ValidTgts$ Permanent.nonLand+OppCtrl | TargetMin$ 0 | TargetMax$ 1 | TargetsForEachPlayer$ True | Duration$ UntilHostLeavesPlay\n",
    };

    private static readonly CardPool Pool = new(ForgeLibrary.FromScripts(Scripts, new Dictionary<string, string>()), null);

    private static Game Setup(string[] hand, string[]? battlefield = null, TestDecider? decider = null) =>
        TestGames.Setup(Pool, hand, battlefield, decider: decider);

    private static TestDecider Targeting(string name) => new() { Choose = (g, d) => d.Kind == DecisionKind.Target ? TestDecider.Named(g, d, name) : null };

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
    public void BiggerForEachAuraAttachedToIt()
    {
        var g = Setup(new[] { "Might", "Might" }, new[] { "Dancer", "Bear" }, Targeting("Dancer"));
        Cast(g, "Might");
        Cast(g, "Might");
        Statics.Apply(g);
        Assert.Equal(4, OnBattlefield(g, "Dancer").Power);
    }

    [Fact]
    public void EnchantedCreaturesHaveShroud()
    {
        var g = Setup(new[] { "Might" }, new[] { "Ward", "Bear", "Dancer" }, Targeting("Bear"));
        Cast(g, "Might");
        Statics.Apply(g);
        Assert.True(OnBattlefield(g, "Bear").HasKeyword("Shroud"));
        Assert.False(OnBattlefield(g, "Dancer").HasKeyword("Shroud"));
    }

    [Fact]
    public void EnchantedPermanentIsOnlyAForest()
    {
        var g = Setup(new[] { "Song" }, new[] { "Peak" }, Targeting("Peak"));
        Cast(g, "Song");
        Statics.Apply(g);
        var peak = OnBattlefield(g, "Peak");
        Assert.True(peak.Types.HasSubtype("Forest"));
        Assert.False(peak.Types.HasSubtype("Mountain"));
        Assert.True(peak.Types.Is(CardType.Land));
    }

    [Fact]
    public void EnchantedPermanentsAbilitiesCantBeActivatedExceptMana()
    {
        var g = Setup(new[] { "Fetters", "Bear" }, new[] { "Totem", "Peak" }, Targeting("Totem"));
        var totem = OnBattlefield(g, "Totem");
        Assert.Contains(g.LegalActions(Game.Me), a => a is GameAction.Activate act && act.CardId == totem.Id);
        Cast(g, "Fetters");
        Assert.DoesNotContain(g.LegalActions(Game.Me), a => a is GameAction.Activate act && act.CardId == totem.Id);
        Assert.True(g.CanCast(g.State[InHand(g, "Bear")], -1));   // its mana ability still pays
    }

    [Fact]
    public void ArtifactManaAbilitiesCantBeActivatedEither()
    {
        var g = Setup(new[] { "Bear" }, new[] { "Totem", "Peak", "Silence" });
        Assert.False(g.CanCast(g.State[InHand(g, "Bear")], -1));
    }

    [Fact]
    public void EnchantmentsBecomeCreaturesWithTheirManaValue()
    {
        var g = Setup(Array.Empty<string>(), new[] { "Starfield", "Ember" });
        Statics.Apply(g);
        var ember = OnBattlefield(g, "Ember");
        Assert.True(ember.Types.Is(CardType.Creature));
        Assert.Equal(2, ember.Power);
        Assert.Equal(2, ember.Toughness);
        Assert.False(OnBattlefield(g, "Starfield").Types.Is(CardType.Creature));
    }

    [Fact]
    public void ActivateOnlyWithACounterAndGainTheLifeLostThisTurn()
    {
        var g = Setup(new[] { "Drain" }, new[] { "Ascension" });
        var ascension = OnBattlefield(g, "Ascension");
        Assert.DoesNotContain(g.LegalActions(Game.Me), a => a is GameAction.Activate act && act.CardId == ascension.Id);
        g.PutCounters(ascension, "QUEST", 1);
        Assert.Contains(g.LegalActions(Game.Me), a => a is GameAction.Activate act && act.CardId == ascension.Id);
        Cast(g, "Drain");
        g.ActivateAbility(ascension, 0);
        ResolveAll(g);
        Assert.Equal(40, g.State.Players[Game.Me].Life);
    }

    [Fact]
    public void ExileOnePermanentOfEachOpponent()
    {
        var g = Setup(new[] { "Grasp" }, new[] { "Bear" });
        var bear = OnBattlefield(g, "Bear");
        bear.Controller = 1;
        Cast(g, "Grasp");
        Assert.Equal(Zone.Exile, bear.Zone);
        g.MoveTo(OnBattlefield(g, "Grasp"), Zone.Graveyard);
        ResolveAll(g);
        Assert.Equal(Zone.Battlefield, bear.Zone);
    }
}
