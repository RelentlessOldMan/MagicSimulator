using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using MagicSim.Core.Players;
using static MagicSim.Tests.TestGames;

namespace MagicSim.Tests;

/// <summary>
/// Restrictions that bind you too (draw limits, a chosen mana value, no enter/die triggers, creatures that weren't cast),
/// exiling a card from a graveyard as a cost, colors among permanents, and choosing one permanent of each type, on
/// hand-written cards in Forge's format.
/// </summary>
public class HateBearTests
{
    private static readonly string[] Scripts =
    {
        "Name:Filler\nManaCost:9\nTypes:Sorcery\n",
        "Name:Imp\nManaCost:0\nTypes:Creature Imp\nPT:1/1\n",
        "Name:Ogre\nManaCost:R\nTypes:Creature Ogre\nPT:3/3\n",
        "Name:Elf\nManaCost:G\nTypes:Creature Elf\nPT:1/1\n",
        "Name:Totem\nManaCost:0\nTypes:Artifact\n",
        "Name:Aura\nManaCost:1\nTypes:Enchantment\n",
        "Name:Rector\nManaCost:3 W\nTypes:Creature Human Cleric\nPT:1/2\n" +
        "T:Mode$ ChangesZone | ValidCard$ Card.Self | Origin$ Battlefield | Destination$ Graveyard | Execute$ TrigExile\n" +
        "SVar:TrigExile:AB$ ChangeZone | Cost$ ExileAnyGrave<1/Card.TriggeredNewCard> | Origin$ Library | Destination$ Battlefield | ChangeType$ Enchantment.YouOwn | ChangeNum$ 1\n",
        "Name:Rainbow\nManaCost:0\nTypes:Sorcery\nA:SP$ GainLife | LifeAmount$ X\nSVar:X:Count$Valid Permanent.YouCtrl$Colors\n",
        "Name:Mindcensor\nManaCost:2 W\nTypes:Creature Bird\nPT:2/1\nS:Mode$ Continuous | Affected$ Player.Opponent | AddKeyword$ LimitSearchLibrary\n",
        "Name:Spirit\nManaCost:1 W\nTypes:Creature Spirit\nPT:3/1\nS:Mode$ CantDraw | ValidPlayer$ Player | DrawLimit$ 1\n",
        "Name:Divination\nManaCost:0\nTypes:Sorcery\nA:SP$ Draw | NumCards$ 2\n",
        "Name:Prelate\nManaCost:0\nTypes:Creature Human\nPT:2/2\nK:ETBReplacement:Other:ChooseNumber\n" +
        "SVar:ChooseNumber:DB$ ChooseNumber | Defined$ You\nS:Mode$ CantBeCast | ValidCard$ Card.nonCreature+cmcEQChosen\n",
        "Name:Priest\nManaCost:0\nTypes:Creature Human\nPT:2/2\n" +
        "R:Event$ Moved | ActiveZones$ Battlefield | Destination$ Battlefield | ValidCard$ Creature.!token+!wasCast | ReplaceWith$ Exile\n" +
        "SVar:Exile:DB$ ChangeZone | Hidden$ True | Origin$ All | Destination$ Exile | Defined$ ReplacedCard\n",
        "Name:Reanimate\nManaCost:0\nTypes:Sorcery\nA:SP$ ChangeZone | Origin$ Graveyard | Destination$ Battlefield | ValidTgts$ Creature.YouOwn | TgtZone$ Graveyard\n",
        "Name:Hush\nManaCost:2\nTypes:Creature Faerie\nPT:1/2\n" +
        "S:Mode$ DisableTriggers | ValidCause$ Creature | ValidMode$ ChangesZone,ChangesZoneAll | Destination$ Battlefield\n" +
        "S:Mode$ DisableTriggers | ValidCause$ Creature | ValidMode$ ChangesZone,ChangesZoneAll | Origin$ Battlefield | Destination$ Graveyard\n",
        "Name:Healer\nManaCost:0\nTypes:Creature Cleric\nPT:1/1\n" +
        "T:Mode$ ChangesZone | Origin$ Any | Destination$ Battlefield | ValidCard$ Card.Self | Execute$ TrigGain\nSVar:TrigGain:DB$ GainLife | LifeAmount$ 3\n",
        "Name:Arrogance\nManaCost:0\nTypes:Sorcery\n" +
        "A:SP$ RepeatEach | RepeatPlayers$ Player | RepeatSubAbility$ YouChoose | SubAbility$ SacAllOthers\n" +
        "SVar:YouChoose:DB$ ChooseCard | Defined$ You | Choices$ Permanent | ChooseEach$ Artifact & Creature & Enchantment & Planeswalker | ControlledByPlayer$ Remembered | RememberChosen$ True | Mandatory$ True\n" +
        "SVar:SacAllOthers:DB$ SacrificeAll | ValidCards$ Permanent.nonLand+!IsRemembered | SubAbility$ DBCleanup\n" +
        "SVar:DBCleanup:DB$ Cleanup | ClearRemembered$ True\n",
        "Name:Cavern\nTypes:Land\nK:ETBReplacement:Other:ChooseCT\nSVar:ChooseCT:DB$ ChooseType | Defined$ You | Type$ Creature\n" +
        "A:AB$ Mana | Cost$ T | Produced$ Any | RestrictValid$ Spell.Creature+ChosenType | AddsNoCounter$ True\n",
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
    public void ExileItFromTheGraveyardToFetchAnEnchantment()
    {
        var g = Setup(Array.Empty<string>(), new[] { "Rector" }, new[] { "Filler", "Aura" });
        var rector = OnBattlefield(g, "Rector");
        g.MoveTo(rector, Zone.Graveyard);
        ResolveAll(g);
        Assert.Equal(Zone.Exile, rector.Zone);
        Assert.Equal(Zone.Battlefield, g.State.Cards.First(c => c.Name == "Aura").Zone);
    }

    [Fact]
    public void CountColorsAmongYourPermanents()
    {
        var g = Setup(new[] { "Rainbow" }, new[] { "Ogre", "Elf", "Imp", "Totem" });
        Cast(g, "Rainbow");
        Assert.Equal(42, Life(g));
    }

    [Fact]
    public void NoMoreThanOneCardEachTurn()
    {
        var g = Setup(new[] { "Divination", "Divination" }, new[] { "Spirit" }, Fillers(6));
        Cast(g, "Divination");
        Assert.Equal(2, g.State.Players[Game.Me].Hand.Count);   // drew one, plus the other Divination
        Cast(g, "Divination");
        Assert.Empty(g.State.Players[Game.Me].Hand.Where(id => g.State[id].Name == "Divination"));
        Assert.Single(g.State.Players[Game.Me].Hand);
    }

    [Fact]
    public void NoncreatureSpellsWithTheChosenManaValueCantBeCast()
    {
        var g = Setup(new[] { "Prelate", "Divination", "Imp" },
            decider: new TestDecider { Choose = (game, d) => d.Kind == DecisionKind.X ? d.Options.ToList().IndexOf(0) : null });
        Cast(g, "Prelate");
        Assert.Equal(0, OnBattlefield(g, "Prelate").ChosenNumber);
        Assert.False(g.CanCast(g.State[InHand(g, "Divination")], 0));
        Assert.True(g.CanCast(g.State[InHand(g, "Imp")], -1));
    }

    [Fact]
    public void CreaturesThatWerentCastAreExiled()
    {
        var g = Setup(new[] { "Reanimate", "Imp" }, new[] { "Priest", "Ogre" });
        var ogre = OnBattlefield(g, "Ogre");
        g.MoveTo(ogre, Zone.Graveyard);
        Cast(g, "Reanimate");
        Assert.Equal(Zone.Exile, ogre.Zone);
        Cast(g, "Imp");
        Assert.Equal(Zone.Battlefield, OnBattlefield(g, "Imp").Zone);
    }

    [Fact]
    public void CreaturesEnteringDontTrigger()
    {
        var g = Setup(new[] { "Healer" }, new[] { "Hush" });
        Cast(g, "Healer");
        Assert.Equal(40, Life(g));
        var h = Setup(new[] { "Healer" });
        Cast(h, "Healer");
        Assert.Equal(43, Life(h));
    }

    [Fact]
    public void KeepOnePermanentOfEachType()
    {
        var g = Setup(new[] { "Arrogance" }, new[] { "Imp", "Ogre", "Totem", "Aura", "Aura" });
        Cast(g, "Arrogance");
        var left = g.State.BattlefieldCards.Select(c => c.Name).OrderBy(n => n).ToList();
        Assert.Equal(3, left.Count);
        Assert.Contains("Totem", left);
        Assert.Contains("Aura", left);
    }
}
