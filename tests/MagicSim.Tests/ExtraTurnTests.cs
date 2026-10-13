using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using MagicSim.Core.Players;
using static MagicSim.Tests.TestGames;

namespace MagicSim.Tests;

/// <summary>
/// "You lose the game at the end of that extra turn", "you can't lose the game this turn", awaken, foretell and
/// choosing a source, on hand-written cards in Forge's format.
/// </summary>
public class ExtraTurnTests
{
    private static readonly string[] Scripts =
    {
        "Name:Peak\nTypes:Land\nA:AB$ Mana | Cost$ T | Produced$ R\n",
        "Name:Filler\nManaCost:9\nTypes:Sorcery\n",
        "Name:Bear\nManaCost:2\nTypes:Creature Bear\nPT:2/2\n",
        "Name:Fortune\nManaCost:0\nTypes:Instant\n" +
        "A:SP$ AddTurn | NumTurns$ 1 | ExtraTurnDelayedTrigger$ DBDelTrig | ExtraTurnDelayedTriggerExecute$ TrigLose\n" +
        "SVar:DBDelTrig:ThisTurn$ True | Mode$ Phase | Phase$ End of Turn\n" +
        "SVar:TrigLose:DB$ LosesGame | Defined$ You\n",
        "Name:Grace\nManaCost:0\nTypes:Instant\n" +
        "A:SP$ Effect | ReplacementEffects$ DBYouCantLose,DBOppCantWin,SelflessDamage\n" +
        "SVar:DBYouCantLose:Event$ GameLoss | ValidPlayer$ You | Layer$ CantHappen\n" +
        "SVar:DBOppCantWin:Event$ GameWin | ValidPlayer$ Opponent | Layer$ CantHappen | Secondary$ True\n" +
        "SVar:SelflessDamage:Event$ LifeReduced | ValidPlayer$ You.lifeGE1 | Result$ LT1 | IsDamage$ True | ReplaceWith$ ReduceLoss\n" +
        "SVar:ReduceLoss:DB$ ReplaceEffect | VarName$ Amount | VarValue$ X\n" +
        "SVar:X:ReplaceCount$Amount/LimitMax.Difference\nSVar:Difference:Count$YourLifeTotal/Minus.1\n",
        "Name:Burn\nManaCost:0\nTypes:Sorcery\nA:SP$ DealDamage | Defined$ You | NumDmg$ 50\n",
        "Name:Drain\nManaCost:0\nTypes:Sorcery\nA:SP$ LoseLife | Defined$ You | LifeAmount$ 50\n",
        "Name:Waterveil\nManaCost:6\nTypes:Sorcery\nK:Awaken:3:1\nA:SP$ GainLife | LifeAmount$ 1\n",
        "Name:Epiphany\nManaCost:7\nTypes:Sorcery\nK:Foretell:1\nA:SP$ GainLife | LifeAmount$ 5\n",
        "Name:Palm\nManaCost:0\nTypes:Instant\nA:SP$ ChooseSource | Choices$ Card,Emblem | SubAbility$ DBEffect\n" +
        "SVar:DBEffect:DB$ Effect | ReplacementEffects$ RPrevent | ConditionDefined$ ChosenCard | ConditionPresent$ Card,Emblem\n" +
        "SVar:RPrevent:Event$ DamageDone | ValidSource$ Card.ChosenCardStrict,Emblem.ChosenCard | ValidTarget$ You | ReplaceWith$ Hit | PreventionEffect$ True\n" +
        "SVar:Hit:DB$ DealDamage | Defined$ ChosenCardController | DamageSource$ EffectSource | NumDmg$ X\nSVar:X:ReplaceCount$DamageAmount\n",
    };

    private static readonly CardPool Pool = new(ForgeLibrary.FromScripts(Scripts, new Dictionary<string, string>()), null);

    private static Game Setup(string[] hand, string[]? battlefield = null, string[]? library = null) =>
        TestGames.Setup(Pool, hand, battlefield, library);

    private static string[] Fillers(int n) => Enumerable.Repeat("Filler", n).ToArray();

    private static void Cast(Game g, string name, int index = 0)
    {
        g.CastSpell(g.State[InHand(g, name)], index);
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
    public void YouLoseAtTheEndOfTheExtraTurn()
    {
        var g = Setup(new[] { "Fortune" }, library: Fillers(10));
        Cast(g, "Fortune");
        g.ResumeTurn();   // this turn's end step: nothing
        Assert.False(g.State.GameOver);
        Assert.Equal(1, g.State.ExtraTurns);
        g.RunExtraTurns();
        Assert.True(g.State.GameOver);
        Assert.Null(g.State.Winner);
    }

    [Fact]
    public void YouCantLoseThisTurnAndDamageLeavesYouAtOne()
    {
        var g = Setup(new[] { "Grace", "Burn", "Drain" }, library: Fillers(10));
        Cast(g, "Grace");
        Cast(g, "Burn");
        Assert.Equal(1, g.State.Players[Game.Me].Life);
        Cast(g, "Drain");   // life loss isn't damage: it goes below 1, but you can't lose this turn
        Assert.True(g.State.Players[Game.Me].Life < 1);
        Assert.False(g.State.GameOver);
        g.ResumeTurn();
        g.RunTurn();
        Assert.True(g.State.GameOver);
    }

    [Fact]
    public void AwakenALand()
    {
        var g = Setup(new[] { "Waterveil" }, battlefield: new[] { "Peak", "Peak" });
        var spell = g.State[InHand(g, "Waterveil")];
        Assert.Equal(2, spell.CurrentFace.SpellAbilities.Count);
        Cast(g, "Waterveil", 1);
        Assert.Equal(41, g.State.Players[Game.Me].Life);
        var land = g.State.BattlefieldCards.Single(c => c.Types.Is(CardType.Creature));
        Assert.Equal(3, land.Power);
        Assert.True(land.HasKeyword("Haste"));
        Assert.True(land.Types.HasSubtype("Elemental"));
    }

    [Fact]
    public void ForetellAndCastItLater()
    {
        var g = Setup(new[] { "Epiphany" }, battlefield: new[] { "Peak", "Peak" }, library: Fillers(10));
        var card = g.State[InHand(g, "Epiphany")];
        var foretell = card.Activated.ToList().FindIndex(a => a.Api == "Foretell");
        Assert.Contains(g.LegalActions(Game.Me), a => a is GameAction.Activate act && act.CardId == card.Id);
        g.ActivateAbility(card, foretell);
        Assert.Equal(Zone.Exile, card.Zone);
        Assert.DoesNotContain(g.LegalActions(Game.Me), a => a is GameAction.Cast c && c.CardId == card.Id);   // not this turn

        g.State.Turn++;
        foreach (var c in g.State.BattlefieldCards) c.Tapped = false;
        Assert.Contains(g.LegalActions(Game.Me), a => a is GameAction.Cast c && c.CardId == card.Id);
        g.CastSpell(card, 0);
        ResolveAll(g);
        Assert.Equal(45, g.State.Players[Game.Me].Life);
        Assert.Equal(Zone.Graveyard, card.Zone);
    }

    [Fact]
    public void ChooseASource()
    {
        var g = Setup(new[] { "Palm" }, battlefield: new[] { "Bear" });
        var palm = g.State[InHand(g, "Palm")];
        Cast(g, "Palm");
        Assert.Equal(new[] { OnBattlefield(g, "Bear").Id }, palm.ChosenCards);
    }
}
