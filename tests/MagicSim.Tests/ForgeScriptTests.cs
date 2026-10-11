using MagicSim.Core.Forge;

namespace MagicSim.Tests;

// Scripts here are written for the tests in Forge's format; no Forge files are copied (GPL).
public class ForgeScriptTests
{
    private const string Ritual = """
        Name:Test Ritual
        ManaCost:B
        Types:Instant
        A:SP$ Mana | Produced$ B | Amount$ 3 | SpellDescription$ Add {B}{B}{B}.
        Oracle:Add {B}{B}{B}.
        """;

    private const string Split = """
        Name:Left Half
        ManaCost:1 U
        Types:Instant
        A:SP$ Draw | NumCards$ 2 | SubAbility$ DBLose
        SVar:DBLose:DB$ LoseLife | LifeAmount$ X
        SVar:X:Count$CardsInYourHand
        AlternateMode:Split
        Oracle:Draw two cards.

        ALTERNATE

        Name:Right Half
        ManaCost:R
        Types:Sorcery
        A:SP$ DealDamage | ValidTgts$ Any | NumDmg$ 2
        Oracle:Deal 2.
        """;

    private const string Creature = """
        Name:Test Beast
        ManaCost:2 G
        Types:Creature Beast
        PT:3/3
        K:Trample
        K:Protection from red
        T:Mode$ ChangesZone | Origin$ Any | Destination$ Battlefield | ValidCard$ Card.Self | Execute$ TrigDraw
        SVar:TrigDraw:DB$ Draw | NumCards$ 1
        A:AB$ Pump | Cost$ Sac<1/CARDNAME> T | NumAtt$ +1
        S:Mode$ Continuous | Affected$ Creature.YouCtrl | AddKeyword$ Haste
        AI:RemoveDeck:Random
        Oracle:…
        """;

    [Fact]
    public void ParsesSingleFace()
    {
        var c = ForgeCard.Parse(Ritual);
        Assert.Equal("Test Ritual", c.FullName);
        Assert.Equal("B", c.Front.ManaCost);
        var a = Assert.Single(c.Front.Abilities);
        Assert.Equal("SP", a.AbilityKind);
        Assert.Equal("Mana", a.Api);
        Assert.Equal("3", a["Amount"]);
        Assert.Equal(new[] { "Api:Mana" }, ForgeRequirements.For(c));
    }

    [Fact]
    public void ParsesSplitCardAndFollowsSVars()
    {
        var c = ForgeCard.Parse(Split);
        Assert.Equal("Left Half // Right Half", c.FullName);
        Assert.Equal(2, c.Faces.Count);
        Assert.Equal("Split", c.AlternateMode);
        Assert.Equal(new[] { "Api:DealDamage", "Api:Draw", "Api:LoseLife", "Count:CardsInYourHand" }, ForgeRequirements.For(c));
    }

    [Fact]
    public void CollectsTriggersStaticsKeywordsAndCosts()
    {
        var c = ForgeCard.Parse(Creature);
        Assert.Equal("3/3", c.Front.PT);
        Assert.Equal(new[]
        {
            "Api:Draw", "Api:Pump", "Cost:Sac", "Cost:Tap",
            "Keyword:Protection", "Keyword:Trample", "Static:Continuous", "Trigger:ChangesZone",
        }, ForgeRequirements.For(c));
        Assert.Equal("RemoveDeck:Random", Assert.Single(c.Front.AiHints));
    }

    [Fact]
    public void LibraryFindsFullAndFaceNames()
    {
        var lib = ForgeLibrary.FromScripts(new[] { Ritual, Split });
        Assert.NotNull(lib.Find("test ritual"));
        Assert.Same(lib.Find("Left Half // Right Half"), lib.Find("Right Half"));
    }

    [Fact]
    public void CoverageRanksBlockers()
    {
        var lib = ForgeLibrary.FromScripts(new[] { Ritual, Split, Creature });
        var support = new EngineSupport();
        support.Requirements.Add("Api:Draw");
        var r = Coverage.Analyze(lib.Cards, support);
        Assert.Equal(0, r.Supported);
        var top = r.TopBlockers[0];
        Assert.Equal("Api:Mana", top.Requirement);   // the only thing blocking Test Ritual
        Assert.Equal(1, top.CardsUnlockedAlone);

        support.Requirements.Add("Api:Mana");
        Assert.Equal(SupportTier.Scripted, support.TierOf(lib.Find("Test Ritual")!, out _));
    }
}
