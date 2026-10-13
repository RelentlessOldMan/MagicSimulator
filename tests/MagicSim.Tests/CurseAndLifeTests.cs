using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using MagicSim.Core.Players;
using static MagicSim.Tests.TestGames;

namespace MagicSim.Tests;

/// <summary>
/// Curses (Auras on players), life-total trigger conditions, "choose a number" with a switched unless cost, mana spent
/// from Treasures, "enters with additional counters" from another permanent, cast-from-graveyard, lands played from
/// exile, opponents' life lost this turn and die-roll results, on hand-written cards in Forge's format.
/// </summary>
public class CurseAndLifeTests
{
    private static readonly string[] Scripts =
    {
        "Name:Peak\nTypes:Land\nA:AB$ Mana | Cost$ T | Produced$ R\n",
        "Name:Filler\nManaCost:9\nTypes:Sorcery\n",
        "Name:Bear\nManaCost:2\nTypes:Creature Bear\nPT:2/2\n",
        "Name:Sovereign\nManaCost:6\nTypes:Creature Cat\nPT:0/6\n" +
        "T:Mode$ Phase | Phase$ Upkeep | ValidPlayer$ You | TriggerZones$ Battlefield | LifeTotal$ You | LifeAmount$ GE40 | Execute$ TrigWin\n" +
        "SVar:TrigWin:DB$ WinsGame | Defined$ You\n",
        "Name:Well\nManaCost:4\nTypes:Artifact\n" +
        "T:Mode$ LifeGained | ValidPlayer$ You | TriggerZones$ Battlefield | Execute$ TrigStore\n" +
        "SVar:TrigStore:DB$ ChooseNumber | Defined$ You | Min$ 0 | Max$ Y | ListTitle$ amount of mana to pay | SubAbility$ DBDraw\n" +
        "SVar:DBDraw:DB$ Draw | Defined$ You | NumCards$ X | UnlessCost$ X | UnlessPayer$ You | UnlessSwitched$ True\n" +
        "SVar:Y:TriggerCount$LifeAmount\nSVar:X:Count$ChosenNumber\n",
        "Name:Salve\nManaCost:0\nTypes:Instant\nA:SP$ GainLife | LifeAmount$ 3\n",
        "Name:Hex\nManaCost:0\nTypes:Enchantment Aura Curse\nK:Enchant:Player\nSVar:AttachAILogic:Curse\n" +
        "T:Mode$ AttackersDeclared | AttackedTarget$ Player.EnchantedBy | Execute$ TrigDrain | TriggerZones$ Battlefield\n" +
        "SVar:TrigDrain:DB$ LoseLife | Defined$ EnchantedPlayer | LifeAmount$ 3\n",
        "Name:Trove\nTypes:Artifact Treasure\nA:AB$ Mana | Cost$ T Sac<1/CARDNAME> | Produced$ Any\n",
        "Name:Painter\nManaCost:0\nTypes:Creature Human\nPT:1/2\n" +
        "R:Event$ Moved | ActiveZones$ Battlefield | ValidCard$ Creature.YouCtrl+Other | Destination$ Battlefield | ReplaceWith$ EnterWithP1P1 | ReplacementResult$ Updated\n" +
        "SVar:EnterWithP1P1:DB$ PutCounter | ETB$ True | Defined$ ReplacedCard | CounterType$ P1P1 | CounterNum$ Treasure\n" +
        "SVar:Treasure:ReplacedCardLKI$CastTotalManaSpent Treasure\n",
        "Name:Ghoul\nManaCost:0\nTypes:Creature Zombie\nPT:1/1\n" +
        "T:Mode$ SpellCast | ValidCard$ Card.wasCastFromGraveyard | ValidActivatingPlayer$ You | TriggerZones$ Battlefield | Execute$ TrigGain\n" +
        "SVar:TrigGain:DB$ GainLife | LifeAmount$ 2\n",
        "Name:Spark\nManaCost:R\nTypes:Sorcery\nK:Flashback:R\nA:SP$ GainLife | LifeAmount$ 1\n",
        "Name:Scout\nManaCost:0\nTypes:Creature Elf\nPT:1/1\n" +
        "T:Mode$ LandPlayed | ValidCard$ Land.YouCtrl | Origin$ Exile | TriggerZones$ Battlefield | Execute$ TrigGain\n" +
        "SVar:TrigGain:DB$ GainLife | LifeAmount$ 1\n",
        "Name:Zap\nManaCost:0\nTypes:Sorcery\nA:SP$ DealDamage | ValidTgts$ Opponent | NumDmg$ 3\n",
        "Name:Gloat\nManaCost:0\nTypes:Sorcery\nA:SP$ GainLife | LifeAmount$ X\nSVar:X:Count$LifeOppsLostThisTurn\n",
        "Name:Die\nManaCost:0\nTypes:Sorcery\nA:SP$ RollDice | Sides$ 20 | ResultSVar$ Result | SubAbility$ DBGain\n" +
        "SVar:DBGain:DB$ GainLife | LifeAmount$ Result\n",
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
    public void WinAtUpkeepOnlyWithFortyLife()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Sovereign" }, library: Fillers(10));
        g.ResumeTurn();
        g.RunTurn();
        Assert.True(g.State.GameOver);
        Assert.Equal(Game.Me, g.State.Winner);

        var h = Setup(Array.Empty<string>(), battlefield: new[] { "Sovereign" }, library: Fillers(10));
        h.State.Players[Game.Me].Life = 39;
        h.ResumeTurn();
        h.RunTurn();
        Assert.Null(h.State.Winner);
    }

    [Fact]
    public void PayTheChosenNumberToDrawThatMany()
    {
        var g = Setup(new[] { "Salve" }, battlefield: new[] { "Well", "Peak", "Peak", "Peak" }, library: Fillers(5));
        Cast(g, "Salve");
        Assert.Equal(3, OnBattlefield(g, "Well").ChosenNumber);
        Assert.Equal(3, g.State.Players[Game.Me].Hand.Count);
        Assert.All(g.State.BattlefieldCards.Where(c => c.Name == "Peak"), c => Assert.True(c.Tapped));

        // Choosing a number it can't pay for draws nothing.
        var h = Setup(new[] { "Salve" }, battlefield: new[] { "Well", "Peak" }, library: Fillers(5));
        Cast(h, "Salve");
        Assert.Empty(h.State.Players[Game.Me].Hand);
    }

    [Fact]
    public void CurseAnOpponentAndTriggerWhenTheyAreAttacked()
    {
        var g = Setup(new[] { "Hex" }, battlefield: new[] { "Bear" }, library: Fillers(5),
            decider: new TestDecider { Choose = (game, d) => d.Kind == DecisionKind.Target ? d.Options.ToList().FindIndex(o => o is TargetRef { Player: 1 }) : null });
        Cast(g, "Hex");
        var hex = OnBattlefield(g, "Hex");
        Assert.Equal(1, hex.EnchantedPlayer);
        g.ResumeTurn();
        Assert.Equal(40 - 2 - 3, Life(g, 1));
        Assert.Equal(Zone.Battlefield, hex.Zone);
    }

    [Fact]
    public void RandomPlayCursesAnOpponent()
    {
        var g = Setup(new[] { "Hex" });
        var decision = new Decision
        {
            Kind = DecisionKind.Target, Player = Game.Me, Source = g.State[InHand(g, "Hex")],
            Options = new object?[] { new TargetRef { Player = Game.Me }, new TargetRef { Player = 1 } },
        };
        var policy = new RolloutPolicy(1);
        var picks = Enumerable.Range(0, 50).Count(_ => policy.PickPublic(g, decision) == 1);
        Assert.True(picks > 40, $"cursed the opponent {picks} times of 50");
    }

    [Fact]
    public void CountersForEachTreasureManaSpent()
    {
        var g = Setup(new[] { "Bear" }, battlefield: new[] { "Painter", "Trove", "Peak" });
        Cast(g, "Bear");
        var bear = OnBattlefield(g, "Bear");
        Assert.Equal(2, bear.ManaSpentToCast);
        Assert.Equal(1, bear.TreasureManaSpent);
        Assert.Equal(1, bear.CounterCount("P1P1"));

        var h = Setup(new[] { "Bear" }, battlefield: new[] { "Painter", "Peak", "Peak" });
        Cast(h, "Bear");
        Assert.Equal(0, OnBattlefield(h, "Bear").CounterCount("P1P1"));
    }

    [Fact]
    public void TriggerOnASpellCastFromTheGraveyard()
    {
        var g = Setup(new[] { "Spark" }, battlefield: new[] { "Ghoul", "Peak", "Peak" });
        var spark = g.State[InHand(g, "Spark")];
        Cast(g, "Spark");
        Assert.Equal(41, Life(g));
        Assert.Equal(Zone.Graveyard, spark.Zone);
        g.CastSpell(spark, 0);
        ResolveAll(g);
        Assert.Equal(44, Life(g));
    }

    [Fact]
    public void TriggerOnlyForALandPlayedFromExile()
    {
        var g = Setup(new[] { "Peak" }, battlefield: new[] { "Scout" }, library: new[] { "Peak" });
        g.PlayLand(g.State[InHand(g, "Peak")]);
        ResolveAll(g);
        Assert.Equal(40, Life(g));

        var exiled = g.State[g.State.Players[Game.Me].Library[0]];
        g.MoveTo(exiled, Zone.Exile);
        g.State.Players[Game.Me].LandsPlayedThisTurn = 0;
        g.PlayLand(exiled);
        ResolveAll(g);
        Assert.Equal(41, Life(g));
    }

    [Fact]
    public void GainTheLifeOpponentsLostThisTurn()
    {
        var g = Setup(new[] { "Zap", "Gloat" });
        Cast(g, "Zap");
        Cast(g, "Gloat");
        Assert.Equal(43, Life(g));
    }

    [Fact]
    public void UseTheDieRoll()
    {
        var g = Setup(new[] { "Die" });
        Cast(g, "Die");
        Assert.InRange(Life(g), 41, 60);
    }
}
