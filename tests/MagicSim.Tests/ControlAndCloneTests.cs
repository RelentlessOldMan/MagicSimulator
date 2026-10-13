using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using MagicSim.Core.Players;
using static MagicSim.Tests.TestGames;

namespace MagicSim.Tests;

/// <summary>
/// Granted trigger keywords, control from a static and "for as long as", an opponent choosing a type, copies that enter
/// with a counter or keep a static ability, and the legend rule switched off, on hand-written cards in Forge's format.
/// </summary>
public class ControlAndCloneTests
{
    private static readonly string[] Scripts =
    {
        "Name:Filler\nManaCost:9\nTypes:Sorcery\n",
        "Name:Imp\nManaCost:0\nTypes:Creature Imp\nPT:1/1\n",
        "Name:Bear\nManaCost:2\nTypes:Creature Bear\nPT:2/2\n",
        "Name:Legend\nManaCost:3\nTypes:Legendary Creature Human\nPT:3/3\n",
        "Name:Rose\nManaCost:3\nTypes:Creature Vampire\nPT:3/3\nS:Mode$ Continuous | Affected$ Creature.Other+YouCtrl | AddKeyword$ Dethrone\n",
        "Name:Magic\nManaCost:0\nTypes:Enchantment Aura\nK:Enchant:Creature\nS:Mode$ Continuous | AffectedDefined$ Enchanted | GainControl$ You\n",
        "Name:Sower\nManaCost:0\nTypes:Creature Faerie\nPT:1/1\n" +
        "T:Mode$ ChangesZone | Origin$ Any | Destination$ Battlefield | ValidCard$ Card.Self | Execute$ TrigChange\n" +
        "SVar:TrigChange:DB$ GainControl | ValidTgts$ Creature.OppCtrl | LoseControl$ LeavesPlay\n",
        "Name:Helm\nManaCost:4\nTypes:Artifact\nK:You may choose not to untap CARDNAME during your untap step.\n" +
        "A:AB$ GainControl | Cost$ T | ValidTgts$ Creature.OppCtrl | LoseControl$ Untap,LeavesPlay,LoseControl\n",
        "Name:Oppressor\nManaCost:0\nTypes:Creature Octopus\nPT:1/1\nK:ETBReplacement:Other:DBChooseOpp\n" +
        "SVar:DBChooseOpp:DB$ ChoosePlayer | Choices$ Player.Opponent | SubAbility$ DBChooseType\n" +
        "SVar:DBChooseType:DB$ ChooseType | Defined$ Player.Chosen | Type$ Creature\n" +
        "A:AB$ GainControl | Cost$ T | ValidTgts$ Creature.IsNotChosenType | LoseControl$ Untap,LeavesPlay\n",
        "Name:Spark\nManaCost:0\nTypes:Creature Illusion\nPT:0/0\nK:ETBReplacement:Copy:DBCopy:Optional\n" +
        "SVar:DBCopy:DB$ Clone | Choices$ Creature.Other+YouCtrl | NonLegendary$ True | SubAbility$ DBConditionEffect\n" +
        "SVar:DBConditionEffect:DB$ Effect | RememberObjects$ Self | ReplacementEffects$ ETBCreat\n" +
        "SVar:ETBCreat:Event$ Moved | ValidCard$ Creature.IsRemembered | Destination$ Battlefield | ReplaceWith$ DBPutP1P1 | ReplacementResult$ Updated\n" +
        "SVar:DBPutP1P1:DB$ PutCounter | Defined$ ReplacedNewCard.Creature | CounterType$ P1P1 | ETB$ True | CounterNum$ 1 | SubAbility$ DBExile\n" +
        "SVar:DBExile:DB$ ChangeZone | Defined$ Self | Origin$ Command | Destination$ Exile\n",
        "Name:Saka\nManaCost:0\nTypes:Legendary Creature Human\nPT:3/1\nK:ETBReplacement:Copy:DBCopy:Optional\n" +
        "SVar:DBCopy:DB$ Clone | Choices$ Creature.YouCtrl+Other | AddStaticAbilities$ NoLegendRule\n" +
        "SVar:NoLegendRule:Mode$ IgnoreLegendRule | ValidCard$ Permanent.YouCtrl\n" +
        "S:Mode$ IgnoreLegendRule | ValidCard$ Permanent.YouCtrl\n",
    };

    private static readonly CardPool Pool = new(ForgeLibrary.FromScripts(Scripts, new Dictionary<string, string>()), null);

    private static Game Setup(string[] hand, string[]? battlefield = null, string[]? library = null, TestDecider? decider = null) =>
        TestGames.Setup(Pool, hand, battlefield, library, decider);

    private static string[] Fillers(int n) => Enumerable.Repeat("Filler", n).ToArray();

    private static void Cast(Game g, string name)
    {
        var card = g.State[InHand(g, name)];
        g.CastSpell(card, card.CurrentFace.SpellAbilities.Count == 0 ? -1 : 0);
        ResolveAll(g);
    }

    /// <summary>Hands a battlefield card to the opponent.</summary>
    private static Card Theirs(Game g, string name)
    {
        var c = OnBattlefield(g, name);
        c.Controller = 1;
        return c;
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
    public void OtherCreaturesHaveDethrone()
    {
        var g = Setup(Array.Empty<string>(), new[] { "Rose", "Imp" }, Fillers(5));
        g.ResumeTurn();
        Assert.Equal(1, OnBattlefield(g, "Imp").CounterCount("P1P1"));
        Assert.Equal(0, OnBattlefield(g, "Rose").CounterCount("P1P1"));
    }

    [Fact]
    public void YouControlEnchantedCreatureWhileTheAuraStays()
    {
        var g = Setup(new[] { "Magic" }, new[] { "Bear" });
        var bear = Theirs(g, "Bear");
        Cast(g, "Magic");
        Statics.Apply(g);
        Assert.Equal(Game.Me, bear.Controller);
        g.MoveTo(OnBattlefield(g, "Magic"), Zone.Graveyard);
        Statics.Apply(g);
        Assert.Equal(1, bear.Controller);
    }

    [Fact]
    public void ControlForAsLongAsItRemainsOnTheBattlefield()
    {
        var g = Setup(new[] { "Sower" }, new[] { "Bear" });
        var bear = Theirs(g, "Bear");
        Cast(g, "Sower");
        Assert.Equal(Game.Me, bear.Controller);
        g.MoveTo(OnBattlefield(g, "Sower"), Zone.Graveyard);
        ResolveAll(g);
        Assert.Equal(1, bear.Controller);
    }

    [Fact]
    public void ControlWhileItStaysTappedAndItIsKeptTapped()
    {
        var g = Setup(Array.Empty<string>(), new[] { "Helm", "Bear" }, Fillers(5));
        var bear = Theirs(g, "Bear");
        var helm = OnBattlefield(g, "Helm");
        g.ActivateAbility(helm, 0);
        ResolveAll(g);
        Assert.Equal(Game.Me, bear.Controller);
        g.ResumeTurn();
        g.RunTurn();   // its untap step leaves the Helm tapped
        Assert.True(helm.Tapped);
        Assert.Equal(Game.Me, bear.Controller);
        helm.Tapped = false;
        ResolveAll(g);
        Assert.Equal(1, bear.Controller);
    }

    [Fact]
    public void AnOpponentChoosesATypeThatCantBeTaken()
    {
        var g = Setup(new[] { "Oppressor" }, new[] { "Bear", "Bear", "Imp" });
        foreach (var bear in g.State.BattlefieldCards.Where(c => c.Name == "Bear").ToList()) bear.Controller = 1;
        var imp = Theirs(g, "Imp");
        Cast(g, "Oppressor");
        var oppressor = OnBattlefield(g, "Oppressor");
        Assert.Equal("Bear", oppressor.ChosenType);
        var candidates = g.TargetCandidates(oppressor, oppressor.Activated[0], Game.Me);
        Assert.Contains(candidates, t => t.CardId == imp.Id);
        Assert.DoesNotContain(candidates, t => t.CardId is { } id && g.State[id].Name == "Bear");
    }

    [Fact]
    public void ACopyThatEntersWithACounterAndIsntLegendary()
    {
        var g = Setup(new[] { "Spark" }, new[] { "Legend" });
        Cast(g, "Spark");
        var copies = g.State.BattlefieldCards.Where(c => c.Name == "Legend").ToList();
        Assert.Equal(2, copies.Count);
        var spark = copies.Single(c => c.CopyDef is not null);
        Assert.Equal(1, spark.CounterCount("P1P1"));
        Assert.False(spark.Types.Has(SuperType.Legendary));
        Assert.DoesNotContain(g.State.Players[Game.Me].Command, id => g.State[id].IsEffect);
    }

    [Fact]
    public void ACopyThatKeepsTheLegendRuleOff()
    {
        var g = Setup(new[] { "Saka" }, new[] { "Legend" });
        Cast(g, "Saka");
        Assert.Equal(2, g.State.BattlefieldCards.Count(c => c.Name == "Legend" && c.Types.Has(SuperType.Legendary)));
    }
}
