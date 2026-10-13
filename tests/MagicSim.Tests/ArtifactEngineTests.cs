using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using MagicSim.Core.Players;
using static MagicSim.Tests.TestGames;

namespace MagicSim.Tests;

/// <summary>
/// "Whenever you activate an ability" (and copying it), the untap symbol, untap limits, revealing cards from hand, the
/// highest mana value and sharing a card type with an imprinted card, on hand-written cards in Forge's format.
/// </summary>
public class ArtifactEngineTests
{
    private static readonly string[] Scripts =
    {
        "Name:Peak\nTypes:Land\nA:AB$ Mana | Cost$ T | Produced$ R\n",
        "Name:Filler\nManaCost:9\nTypes:Sorcery\n",
        "Name:Rock\nManaCost:1\nTypes:Artifact\n",
        "Name:Monolith\nManaCost:5\nTypes:Artifact\n",
        "Name:Bear\nManaCost:2\nTypes:Creature Bear\nPT:2/2\n",
        "Name:Fountain\nManaCost:0\nTypes:Artifact\nA:AB$ GainLife | Cost$ 1 | LifeAmount$ 1\n",
        "Name:Rings\nManaCost:0\nTypes:Artifact\n" +
        "T:Mode$ AbilityCast | ValidActivatingPlayer$ You | ValidSA$ SpellAbility.!ManaAbility | TriggerZones$ Battlefield | Execute$ TrigCopySpell | OptionalDecider$ You\n" +
        "SVar:TrigCopySpell:AB$ CopySpellAbility | Cost$ 2 | Defined$ TriggeredSpellAbility | MayChooseTarget$ True\n",
        "Name:Pala\nManaCost:0\nTypes:Artifact Creature Scarecrow\nPT:1/1\nA:AB$ GainLife | Cost$ Q | LifeAmount$ 5\n",
        "Name:Orb\nManaCost:0\nTypes:Artifact\n" +
        "S:Mode$ Continuous | Affected$ Player | AddKeyword$ UntapAdjust:Land:1 | IsPresent$ Card.Self+untapped\n",
        "Name:Worker\nManaCost:0\nTypes:Artifact Creature Construct\nPT:1/2\n" +
        "A:AB$ Reveal | Cost$ T | RevealValid$ Card.Artifact+YouCtrl | AnyNumber$ True | RememberRevealed$ True | SubAbility$ DBMana\n" +
        "SVar:DBMana:DB$ Mana | Produced$ C | Amount$ X | SubAbility$ DBCleanup\nSVar:DBCleanup:DB$ Cleanup | ClearRemembered$ True\n" +
        "SVar:X:Remembered$Amount/Twice\n",
        "Name:Consul\nManaCost:0\nTypes:Creature Vedalken\nPT:1/4\n" +
        "T:Mode$ Phase | Phase$ Upkeep | ValidPlayer$ You | TriggerZones$ Battlefield | CheckSVar$ X | Execute$ TrigLife\n" +
        "SVar:TrigLife:DB$ GainLife | LifeAmount$ 1\nSVar:X:Count$Valid Artifact.greatestCMC_Artifact+YouCtrl\n",
        "Name:Anvil\nManaCost:0\nTypes:Artifact\n" +
        "S:Mode$ ReduceCost | ValidCard$ Card.sharesCardTypeWith Imprinted | Type$ Spell | Activator$ You | Amount$ 2\n",
    };

    private static readonly CardPool Pool = new(ForgeLibrary.FromScripts(Scripts, new Dictionary<string, string>()), null);

    private static Game Setup(string[] hand, string[]? battlefield = null, string[]? library = null) =>
        TestGames.Setup(Pool, hand, battlefield, library);

    private static int Life(Game g) => g.State.Players[Game.Me].Life;

    private static string[] Fillers(int n) => Enumerable.Repeat("Filler", n).ToArray();

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
    public void PayToCopyAnActivatedAbility()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Rings", "Fountain", "Peak", "Peak", "Peak" });
        g.ActivateAbility(OnBattlefield(g, "Fountain"), 0);
        ResolveAll(g);
        Assert.Equal(42, Life(g));
        Assert.All(g.State.BattlefieldCards.Where(c => c.Name == "Peak"), c => Assert.True(c.Tapped));
    }

    [Fact]
    public void TheUntapSymbol()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Pala" });
        var pala = OnBattlefield(g, "Pala");
        Assert.DoesNotContain(g.LegalActions(Game.Me), a => a is GameAction.Activate act && act.CardId == pala.Id);
        pala.Tapped = true;
        g.ActivateAbility(pala, 0);
        ResolveAll(g);
        Assert.False(pala.Tapped);
        Assert.Equal(45, Life(g));
    }

    [Fact]
    public void UntapOnlyOneLand()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Orb", "Peak", "Peak", "Peak", "Rock" }, library: Fillers(5));
        foreach (var c in g.State.BattlefieldCards.Where(c => c.Name is "Peak" or "Rock")) c.Tapped = true;
        g.State.Step = Step.End;
        g.RunTurn();
        Assert.Equal(1, g.State.BattlefieldCards.Count(c => c.Name == "Peak" && !c.Tapped));
        Assert.False(OnBattlefield(g, "Rock").Tapped);
    }

    [Fact]
    public void RevealArtifactsForMana()
    {
        var g = Setup(new[] { "Rock", "Rock", "Bear" }, battlefield: new[] { "Worker" });
        g.ActivateAbility(OnBattlefield(g, "Worker"), 0);
        ResolveAll(g);
        Assert.Equal(4, g.State.Players[Game.Me].Pool[ManaColor.C]);
    }

    [Fact]
    public void TheArtifactWithTheHighestManaValue()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Consul", "Rock", "Monolith" }, library: Fillers(3));
        g.State.Step = Step.Upkeep;
        g.FirePhase("Upkeep");
        ResolveAll(g);
        Assert.Equal(41, Life(g));

        var h = Setup(Array.Empty<string>(), battlefield: new[] { "Consul", "Rock", "Monolith" }, library: Fillers(3));
        OnBattlefield(h, "Monolith").Controller = 1;
        h.State.Step = Step.Upkeep;
        h.FirePhase("Upkeep");
        ResolveAll(h);
        Assert.Equal(40, Life(h));
    }

    [Fact]
    public void SpellsThatShareATypeWithTheImprintedCardCostLess()
    {
        var g = Setup(new[] { "Monolith", "Bear" }, battlefield: new[] { "Anvil", "Peak", "Peak", "Peak" }, library: new[] { "Rock" });
        var anvil = OnBattlefield(g, "Anvil");
        var rock = g.State.Cards.First(c => c.Name == "Rock");
        g.MoveTo(rock, Zone.Exile);
        anvil.Imprinted = new() { rock.Id };
        Assert.True(g.CanCast(g.State[InHand(g, "Monolith")], -1));   // 5 - 2 = 3
        g.MoveTo(OnBattlefield(g, "Peak"), Zone.Graveyard);
        Assert.False(g.CanCast(g.State[InHand(g, "Monolith")], -1));
        Assert.True(g.CanCast(g.State[InHand(g, "Bear")], -1));
    }
}
