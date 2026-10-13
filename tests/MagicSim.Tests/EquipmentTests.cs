using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using MagicSim.Core.Players;
using static MagicSim.Tests.TestGames;

namespace MagicSim.Tests;

/// <summary>
/// Granted equip, attaching several Equipment at once, a free first equip each turn, Equipment and Aura filters, losing
/// keywords, unattach costs, living weapon, "that creature" of a damage trigger, untap triggers, and meld, on
/// hand-written cards in Forge's format.
/// </summary>
public class EquipmentTests
{
    private static readonly string[] Scripts =
    {
        "Name:Peak\nTypes:Land Mountain\nA:AB$ Mana | Cost$ T | Produced$ R\n",
        "Name:Filler\nManaCost:9\nTypes:Sorcery\n",
        "Name:Ogre\nManaCost:0\nTypes:Creature Ogre\nPT:3/3\n",
        "Name:Bird\nManaCost:0\nTypes:Creature Bird\nPT:1/1\nK:Flying\n",
        "Name:Sword\nManaCost:0\nTypes:Artifact Equipment\nK:Equip:3\n",
        "Name:Paladin\nManaCost:0\nTypes:Creature Human\nPT:2/2\nS:Mode$ Continuous | Affected$ Equipment.YouCtrl | AddKeyword$ Equip:0\n",
        "Name:Rally\nManaCost:0\nTypes:Sorcery\nA:SP$ Attach | Defined$ Targeted | ValidTgts$ Creature | Object$ Valid Aura.YouCtrl,Equipment.YouCtrl | Optional$ True\n",
        "Name:Bruenor\nManaCost:0\nTypes:Creature Dwarf\nPT:5/4\n" +
        "S:Mode$ AlternativeCost | ValidSA$ Activated.Equip | ValidPlayer$ You | Cost$ 0 | CheckSVar$ X | SVarCompare$ LT1\n" +
        "SVar:X:Count$ThisTurnActivated_Activated.Equip+YouCtrl\n",
        "Name:Might\nManaCost:0\nTypes:Enchantment Aura\nK:Enchant:Creature\n",
        "Name:Reverie\nManaCost:0\nTypes:Sorcery\nA:SP$ GainLife | LifeAmount$ X\nSVar:X:Count$Valid Aura.YouCtrl+AttachedTo Creature\n",
        "Name:Coronet\nManaCost:0\nTypes:Enchantment Aura\nK:Enchant:Creature.EnchantedBy Aura.Other:creature with another Aura attached to it\n",
        "Name:Hammer\nManaCost:0\nTypes:Artifact Equipment\nK:Equip:0\n" +
        "S:Mode$ Continuous | AffectedDefined$ Equipped | Affected$ Creature | AddPower$ 10 | AddToughness$ 10 | RemoveKeyword$ Flying\n",
        "Name:Spear\nManaCost:0\nTypes:Artifact\nA:AB$ AnimateAll | Cost$ 0 | ValidCards$ Permanent.OppCtrl | RemoveKeywords$ Flying & Indestructible\n",
        "Name:Sunforger\nManaCost:0\nTypes:Artifact Equipment\nK:Equip:0\nA:AB$ GainLife | Cost$ Unattach<CARDNAME/CARDNAME> | LifeAmount$ 3\n",
        "Name:Kaldra\nManaCost:0\nTypes:Artifact Equipment\nK:Living Weapon\nK:Equip:7\n" +
        "S:Mode$ Continuous | AffectedDefined$ Equipped | Affected$ Creature | AddPower$ 5 | AddToughness$ 5\n",
        "Name:Burner\nManaCost:0\nTypes:Creature Elemental\nPT:1/1\nA:AB$ DealDamage | Cost$ 0 | ValidTgts$ Creature.Other | NumDmg$ 1\n" +
        "T:Mode$ DamageDone | ValidSource$ Card.Self | ValidTarget$ Creature | Execute$ TrigExile\n" +
        "SVar:TrigExile:DB$ ChangeZone | Defined$ TriggeredTarget | Origin$ Battlefield | Destination$ Exile\n",
        "Name:Key\nManaCost:0\nTypes:Artifact\nT:Mode$ Untaps | ValidCard$ Card.Self | TriggerZones$ Battlefield | Execute$ TrigGain\nSVar:TrigGain:DB$ GainLife | LifeAmount$ 1\n",
        "Name:Bodyguard\nManaCost:0\nTypes:Creature Human\nPT:2/1\nK:ETBReplacement:Other:ChooseC\n" +
        "SVar:ChooseC:DB$ ChooseCard | Defined$ You | Choices$ Creature.YouCtrl+Other | Mandatory$ True\n" +
        "A:AB$ Pump | Cost$ Sac<1/CARDNAME> | Defined$ Valid Card.ChosenCardStrict | KW$ Indestructible\n",
        "Name:Battlements\nTypes:Land\nA:AB$ Meld | Cost$ T | Name$ Township | Primary$ Battlements | Secondary$ Garrison\n" +
        "AlternateMode:Meld\n\nALTERNATE\n\nName:Township\nTypes:Legendary Creature Eldrazi Ooze\nPT:7/4\nK:Trample\n",
        "Name:Garrison\nManaCost:2 R\nTypes:Creature Human Soldier\nPT:2/3\n",
        "Name:Doom\nManaCost:0\nTypes:Sorcery\nA:SP$ Destroy | ValidTgts$ Creature\n",
    };

    private static readonly Dictionary<string, string> Tokens = new()
    {
        ["b_0_0_phyrexian_germ"] = "Name:Phyrexian Germ Token\nManaCost:no cost\nColors:black\nTypes:Creature Phyrexian Germ\nPT:0/0\n",
    };

    private static readonly CardPool Pool = new(ForgeLibrary.FromScripts(Scripts, Tokens), null);

    private static Game Setup(string[] hand, string[]? battlefield = null, string[]? library = null, TestDecider? decider = null) =>
        TestGames.Setup(Pool, hand, battlefield, library, decider);

    private static string[] Fillers(int n) => Enumerable.Repeat("Filler", n).ToArray();

    private static TestDecider Targeting(string name) => new() { Choose = (g, d) => d.Kind == DecisionKind.Target ? TestDecider.Named(g, d, name) : null };

    private static void Cast(Game g, string name)
    {
        var card = g.State[InHand(g, name)];
        g.CastSpell(card, card.CurrentFace.SpellAbilities.Count == 0 ? -1 : 0);
        ResolveAll(g);
    }

    private static bool CanActivate(Game g, Card c) => g.LegalActions(Game.Me).Any(a => a is GameAction.Activate act && act.CardId == c.Id);

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
    public void EquipmentGainsEquipZero()
    {
        var g = Setup(Array.Empty<string>(), new[] { "Sword", "Ogre" });
        var sword = OnBattlefield(g, "Sword");
        Assert.False(CanActivate(g, sword));
        var h = Setup(Array.Empty<string>(), new[] { "Sword", "Ogre", "Paladin" });
        Statics.Apply(h);
        Assert.True(CanActivate(h, OnBattlefield(h, "Sword")));
    }

    [Fact]
    public void AttachYourEquipmentToOneCreature()
    {
        var g = Setup(new[] { "Rally" }, new[] { "Sword", "Hammer", "Ogre" }, decider: Targeting("Ogre"));
        Cast(g, "Rally");
        var ogre = OnBattlefield(g, "Ogre");
        Assert.Equal(ogre.Id, OnBattlefield(g, "Sword").AttachedTo);
        Assert.Equal(ogre.Id, OnBattlefield(g, "Hammer").AttachedTo);
    }

    [Fact]
    public void TheFirstEquipEachTurnIsFree()
    {
        var g = Setup(Array.Empty<string>(), new[] { "Sword", "Ogre", "Bruenor" });
        var sword = OnBattlefield(g, "Sword");
        Assert.True(CanActivate(g, sword));
        g.ActivateAbility(sword, sword.Activated.ToList().FindIndex(a => a.IsEquip));
        ResolveAll(g);
        Assert.True(sword.AttachedTo >= 0);
        Assert.False(CanActivate(g, sword));
    }

    [Fact]
    public void AurasAttachedToCreaturesAndAnAuraNeedingAnother()
    {
        var g = Setup(new[] { "Might", "Reverie", "Coronet" }, new[] { "Ogre", "Bird" }, decider: Targeting("Ogre"));
        var coronet = g.State[InHand(g, "Coronet")];
        Assert.Empty(g.TargetCandidates(coronet, coronet.CurrentFace.AuraSpell!, Game.Me));
        Cast(g, "Might");
        Assert.Equal(new[] { OnBattlefield(g, "Ogre").Id }, g.TargetCandidates(coronet, coronet.CurrentFace.AuraSpell!, Game.Me).Select(t => t.CardId!.Value));
        Cast(g, "Reverie");
        Assert.Equal(41, g.State.Players[Game.Me].Life);
    }

    [Fact]
    public void EquippedCreatureLosesFlying()
    {
        var g = Setup(Array.Empty<string>(), new[] { "Hammer", "Bird" });
        var hammer = OnBattlefield(g, "Hammer");
        g.ActivateAbility(hammer, 0);
        ResolveAll(g);
        Statics.Apply(g);
        var bird = OnBattlefield(g, "Bird");
        Assert.False(bird.HasKeyword("Flying"));
        Assert.Equal(11, bird.Power);
    }

    [Fact]
    public void OpponentsPermanentsLoseKeywords()
    {
        var g = Setup(Array.Empty<string>(), new[] { "Spear", "Bird" });
        var bird = OnBattlefield(g, "Bird");
        bird.Controller = 1;
        g.ActivateAbility(OnBattlefield(g, "Spear"), 0);
        ResolveAll(g);
        Statics.Apply(g);
        Assert.False(bird.HasKeyword("Flying"));
    }

    [Fact]
    public void UnattachAsACost()
    {
        var g = Setup(Array.Empty<string>(), new[] { "Sunforger", "Ogre" });
        var sunforger = OnBattlefield(g, "Sunforger");
        var gain = sunforger.Activated.ToList().FindIndex(a => a.Api == "GainLife");
        Assert.DoesNotContain(g.LegalActions(Game.Me), a => a is GameAction.Activate act && act.CardId == sunforger.Id && act.AbilityIndex == gain);
        g.ActivateAbility(sunforger, sunforger.Activated.ToList().FindIndex(a => a.IsEquip));
        ResolveAll(g);
        g.ActivateAbility(sunforger, gain);
        ResolveAll(g);
        Assert.Equal(-1, sunforger.AttachedTo);
        Assert.Equal(43, g.State.Players[Game.Me].Life);
    }

    [Fact]
    public void LivingWeaponMakesAGermToCarryIt()
    {
        var g = Setup(new[] { "Kaldra" });
        Cast(g, "Kaldra");
        var germ = OnBattlefield(g, "Phyrexian Germ Token");
        Assert.Equal(germ.Id, OnBattlefield(g, "Kaldra").AttachedTo);
        Assert.Equal(5, germ.Power);
    }

    [Fact]
    public void ExileTheCreatureItDamaged()
    {
        var g = Setup(Array.Empty<string>(), new[] { "Burner", "Ogre" });
        var ogre = OnBattlefield(g, "Ogre");
        g.ActivateAbility(OnBattlefield(g, "Burner"), 0);
        ResolveAll(g);
        Assert.Equal(Zone.Exile, ogre.Zone);
    }

    [Fact]
    public void TriggerWhenItUntaps()
    {
        var g = Setup(Array.Empty<string>(), new[] { "Key" }, Fillers(5));
        OnBattlefield(g, "Key").Tapped = true;
        g.ResumeTurn();
        g.RunTurn();
        Assert.Equal(41, g.State.Players[Game.Me].Life);
    }

    [Fact]
    public void TheChosenCreatureGainsIndestructible()
    {
        var g = Setup(new[] { "Bodyguard" }, new[] { "Ogre" });
        Cast(g, "Bodyguard");
        g.ActivateAbility(OnBattlefield(g, "Bodyguard"), 0);
        ResolveAll(g);
        Assert.True(OnBattlefield(g, "Ogre").HasKeyword("Indestructible"));
    }

    [Fact]
    public void MeldIntoOneAndLeaveAsTwo()
    {
        var g = Setup(new[] { "Doom" }, new[] { "Battlements", "Garrison" });
        var (battlements, garrison) = (OnBattlefield(g, "Battlements"), OnBattlefield(g, "Garrison"));
        g.ActivateAbility(battlements, battlements.Activated.ToList().FindIndex(a => a.Api == "Meld"));
        ResolveAll(g);
        Assert.Equal("Township", battlements.Name);
        Assert.Equal(Zone.Battlefield, battlements.Zone);
        Assert.Equal(Zone.Exile, garrison.Zone);
        Assert.Equal(7, battlements.Power);
        Cast(g, "Doom");
        Assert.Equal(Zone.Graveyard, battlements.Zone);
        Assert.Equal(Zone.Graveyard, garrison.Zone);
        Assert.Equal("Battlements", battlements.Name);
    }
}
