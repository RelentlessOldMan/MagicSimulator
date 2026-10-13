using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using MagicSim.Core.Players;
using static MagicSim.Tests.TestGames;

namespace MagicSim.Tests;

/// <summary>
/// Mutate (over and under, "whenever this mutates", leaving as several cards, casting from the graveyard by mutating),
/// "if it wasn't put onto the battlefield with this ability", digging and remembering what's found, and "if it entered
/// from your library", on hand-written cards in Forge's format.
/// </summary>
public class MutateTests
{
    private static readonly string[] Scripts =
    {
        "Name:Filler\nManaCost:9\nTypes:Sorcery\n",
        "Name:Ogre\nManaCost:2\nTypes:Creature Ogre\nPT:3/3\nK:Reach\n",
        "Name:Knight\nManaCost:2\nTypes:Creature Human Knight\nPT:2/2\n",
        "Name:Mutant\nManaCost:5\nTypes:Creature Beast\nPT:6/6\nK:Mutate:0\nK:Trample\n",
        "Name:Hemo\nManaCost:3\nTypes:Creature Vampire\nPT:3/2\nK:Mutate:0\n" +
        "T:Mode$ Mutates | ValidCard$ Card.Self | Execute$ TrigGain\nSVar:TrigGain:DB$ GainLife | Defined$ You | LifeAmount$ X\nSVar:X:Count$TimesMutated\n",
        "Name:Brokkos\nManaCost:4\nTypes:Creature Nightmare\nPT:6/6\nK:Mutate:0\n" +
        "S:Mode$ Continuous | Affected$ Card.Self | MayPlay$ True | ValidSA$ Spell.Mutate | AffectedZone$ Graveyard | EffectZone$ Graveyard\n",
        "Name:Doom\nManaCost:0\nTypes:Sorcery\nA:SP$ Destroy | ValidTgts$ Creature\n",
        "Name:Wisp\nManaCost:0\nTypes:Creature Spirit\nPT:1/1\nK:Flash\n" +
        "T:Mode$ SpellCast | ValidCard$ Card.hasKeywordFlash+Other | ValidActivatingPlayer$ You | TriggerZones$ Battlefield | Execute$ TrigGain\n" +
        "SVar:TrigGain:DB$ GainLife | LifeAmount$ 1\n",
        "Name:Snap\nManaCost:0\nTypes:Instant\nK:Flash\nA:SP$ GainLife | LifeAmount$ 0\n",
        "Name:Kodama\nManaCost:0\nTypes:Creature Spirit\nPT:2/2\n" +
        "T:Mode$ ChangesZone | Origin$ Any | Destination$ Battlefield | ValidCard$ Permanent.YouCtrl+Other | NotThisAbility$ True | TriggerZones$ Battlefield | Execute$ TrigChange\n" +
        "SVar:TrigChange:DB$ ChangeZone | Origin$ Hand | Destination$ Battlefield | ChangeType$ Permanent.YouCtrl+cmcLEX | ChangeNum$ 1\n" +
        "SVar:X:TriggeredCard$CardManaCost\n",
        "Name:Starrix\nManaCost:0\nTypes:Sorcery\n" +
        "A:SP$ DigUntil | Amount$ 1 | Defined$ You | Valid$ Permanent | RevealedDestination$ Exile | FoundDestination$ Exile | RememberFound$ True | SubAbility$ DBToPlay\n" +
        "SVar:DBToPlay:DB$ ChangeZone | Defined$ Remembered | Origin$ Exile | Destination$ Battlefield | SubAbility$ DBCleanup\n" +
        "SVar:DBCleanup:DB$ Cleanup | ClearRemembered$ True\n",
        "Name:Fblthp\nManaCost:0\nTypes:Creature Homunculus\nPT:1/1\n" +
        "T:Mode$ ChangesZone | Origin$ Any | Destination$ Battlefield | ValidCard$ Card.Self | Execute$ DBDraw\n" +
        "SVar:DBDraw:DB$ Draw | NumCards$ DrawNum\nSVar:DrawNum:SVar$LibNum1/LimitMax.2\n" +
        "SVar:LibNum1:Count$wasCastFromYourLibrary.2.1/Plus.LibNum2\nSVar:LibNum2:TriggeredCardLKI$Valid Card.inZoneLibrary\n",
        "Name:Otrimi\nManaCost:0\nTypes:Sorcery\nA:SP$ ChangeZone | Origin$ Graveyard | Destination$ Hand | ValidTgts$ Creature.YouCtrl+withMutate\n",
    };

    private static readonly CardPool Pool = new(ForgeLibrary.FromScripts(Scripts, new Dictionary<string, string>()), null);

    private static Game Setup(string[] hand, string[]? battlefield = null, string[]? library = null, TestDecider? decider = null) =>
        TestGames.Setup(Pool, hand, battlefield, library, decider);

    private static string[] Fillers(int n) => Enumerable.Repeat("Filler", n).ToArray();

    private static int Life(Game g) => g.State.Players[Game.Me].Life;

    private static int MutateIndex(Card c) => c.CurrentFace.SpellAbilities.ToList().FindIndex(a => a.IsMutate);

    private static void CastMutating(Game g, string name)
    {
        var card = g.State[InHand(g, name)];
        g.CastSpell(card, MutateIndex(card));
        ResolveAll(g);
    }

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
    public void MutateOverAndAttackRightAway()
    {
        var g = Setup(new[] { "Mutant" }, new[] { "Ogre" }, Fillers(5));
        var ogre = OnBattlefield(g, "Ogre");
        CastMutating(g, "Mutant");
        Assert.Equal("Mutant", ogre.Name);
        Assert.Equal(6, ogre.Power);
        Assert.True(ogre.HasKeyword("Reach"));
        Assert.True(ogre.HasKeyword("Trample"));
        Assert.Equal(Zone.Exile, g.State.Cards.First(c => c.Name == "Mutant" && c.Id != ogre.Id).Zone);
        g.ResumeTurn();
        Assert.Equal(34, g.State.Players[1].Life);
    }

    [Fact]
    public void MutateUnderKeepsTheCreaturesCharacteristics()
    {
        var g = Setup(new[] { "Mutant" }, new[] { "Ogre" }, decider: new TestDecider { Choose = (game, d) => d.Kind == DecisionKind.Option ? 1 : null });
        var ogre = OnBattlefield(g, "Ogre");
        CastMutating(g, "Mutant");
        Assert.Equal("Ogre", ogre.Name);
        Assert.Equal(3, ogre.Power);
        Assert.True(ogre.HasKeyword("Trample"));
    }

    [Fact]
    public void WhenItMutatesCountTheTimes()
    {
        var g = Setup(new[] { "Hemo", "Mutant" }, new[] { "Ogre" });
        CastMutating(g, "Hemo");
        Assert.Equal(41, Life(g));
        CastMutating(g, "Mutant");   // Hemo's trigger is under it now: mutating again counts twice
        Assert.Equal(43, Life(g));
    }

    [Fact]
    public void AMutatedCreatureDiesAsAllItsCards()
    {
        var g = Setup(new[] { "Mutant", "Doom" }, new[] { "Ogre" });
        CastMutating(g, "Mutant");
        Cast(g, "Doom");
        Assert.Equal(2, g.State.Players[Game.Me].Graveyard.Count(id => g.State[id].Name is "Ogre" or "Mutant"));
    }

    [Fact]
    public void OnlyNonHumansYouOwnAndOtherwiseItJustEnters()
    {
        var g = Setup(new[] { "Mutant" }, new[] { "Knight" });
        var mutant = g.State[InHand(g, "Mutant")];
        Assert.False(g.CanCast(mutant, MutateIndex(mutant)));
        var h = Setup(new[] { "Mutant" }, new[] { "Ogre" });
        var card = h.State[InHand(h, "Mutant")];
        h.CastSpell(card, MutateIndex(card));
        h.MoveTo(OnBattlefield(h, "Ogre"), Zone.Graveyard);
        ResolveAll(h);
        Assert.Equal(Zone.Battlefield, card.Zone);
        Assert.Equal("Mutant", card.Name);
    }

    [Fact]
    public void CastFromTheGraveyardOnlyByMutating()
    {
        var g = Setup(Array.Empty<string>(), new[] { "Ogre" }, new[] { "Brokkos" });
        var brokkos = g.State.Cards.First(c => c.Name == "Brokkos");
        g.MoveTo(brokkos, Zone.Graveyard);
        Assert.False(g.CanCast(brokkos, 0));
        Assert.True(g.CanCast(brokkos, MutateIndex(brokkos)));
    }

    [Fact]
    public void AnotherSpellWithFlash()
    {
        var g = Setup(new[] { "Snap", "Doom" }, new[] { "Wisp" });
        Cast(g, "Snap");
        Assert.Equal(41, Life(g));
    }

    [Fact]
    public void NotWhatThisAbilityPutOntoTheBattlefield()
    {
        var g = Setup(new[] { "Knight", "Snap" }, new[] { "Kodama" }, new[] { "Ogre" });
        g.MoveTo(g.State.Cards.First(c => c.Name == "Ogre"), Zone.Battlefield);   // something else put it there: Kodama puts the Knight in
        ResolveAll(g);
        Assert.Equal(Zone.Battlefield, g.State.Cards.First(c => c.Name == "Knight").Zone);
        Assert.Contains(g.State.Players[Game.Me].Hand, id => g.State[id].Name == "Snap");   // but not again for the Knight
    }

    [Fact]
    public void DigForAPermanentAndPutItOntoTheBattlefield()
    {
        var g = Setup(new[] { "Starrix" }, library: new[] { "Filler", "Ogre", "Filler" });
        Cast(g, "Starrix");
        Assert.Equal(Zone.Battlefield, g.State.Cards.First(c => c.Name == "Ogre").Zone);
        Assert.Equal(Zone.Exile, g.State.Cards.First(c => c.Name == "Filler").Zone);
    }

    [Fact]
    public void DrawTwoIfItEnteredFromYourLibrary()
    {
        var g = Setup(new[] { "Fblthp" }, library: Fillers(5));
        Cast(g, "Fblthp");
        Assert.Single(g.State.Players[Game.Me].Hand);
        var h = Setup(Array.Empty<string>(), library: new[] { "Fblthp", "Filler", "Filler", "Filler" });
        h.MoveTo(h.State.Cards.First(c => c.Name == "Fblthp"), Zone.Battlefield);
        ResolveAll(h);
        Assert.Equal(2, h.State.Players[Game.Me].Hand.Count);
    }

    [Fact]
    public void ReturnACreatureCardWithMutate()
    {
        var g = Setup(new[] { "Otrimi" }, new[] { "Ogre" }, new[] { "Mutant" });
        g.MoveTo(g.State.Cards.First(c => c.Name == "Mutant"), Zone.Graveyard);
        g.MoveTo(OnBattlefield(g, "Ogre"), Zone.Graveyard);
        Cast(g, "Otrimi");
        Assert.Contains(g.State.Players[Game.Me].Hand, id => g.State[id].Name == "Mutant");
    }
}
