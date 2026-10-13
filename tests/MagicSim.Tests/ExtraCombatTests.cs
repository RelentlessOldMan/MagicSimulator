using System.Reflection;
using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using MagicSim.Core.Players;
using static MagicSim.Tests.TestGames;

namespace MagicSim.Tests;

/// <summary>
/// Mana that lasts the turn, hideaway, exert, strive, mentor, dethrone, "attacks for the first time each turn",
/// "attacked this turn", bonuses for each attack, extra combats that untap, effects until end of combat and abilities of
/// the top card of the library, on hand-written cards in Forge's format.
/// </summary>
public class ExtraCombatTests
{
    private static readonly string[] Scripts =
    {
        "Name:Peak\nTypes:Land\nA:AB$ Mana | Cost$ T | Produced$ R\n",
        "Name:Filler\nManaCost:9\nTypes:Sorcery\n",
        "Name:Ping\nManaCost:0\nTypes:Sorcery\nA:SP$ GainLife | LifeAmount$ 1\n",
        "Name:Big\nManaCost:7\nTypes:Sorcery\nA:SP$ GainLife | LifeAmount$ 5\n",
        "Name:Bear\nManaCost:2\nTypes:Creature Bear\nPT:2/2\n",
        "Name:Muse\nManaCost:0\nTypes:Creature God\nPT:3/3\n" +
        "T:Mode$ SpellCast | ValidCard$ Card | ValidActivatingPlayer$ You | Execute$ TrigMana | TriggerZones$ Battlefield\n" +
        "SVar:TrigMana:DB$ Mana | Produced$ R | PersistentMana$ True\n",
        "Name:Knoll\nManaCost:no cost\nTypes:Land\nK:Hideaway:2\n" +
        "A:AB$ Mana | Cost$ T | Produced$ R\n" +
        "A:AB$ Play | Cost$ R T | Defined$ ExiledWith | Amount$ All | Controller$ You | WithoutManaCost$ True | Optional$ True | ConditionCheckSVar$ X | ConditionSVarCompare$ GE7\n" +
        "SVar:X:Count$MaxOppDamageThisTurn\n",
        "Name:Celebrant\nManaCost:0\nTypes:Creature Human Warrior\nPT:4/1\n" +
        "S:Mode$ OptionalAttackCost | ValidCard$ Card.Self | IsPresent$ Creature.Self+notExertedThisTurn | Trigger$ TrigUntapAll | Cost$ Exert<1/CARDNAME>\n" +
        "SVar:TrigUntapAll:DB$ UntapAll | ValidCards$ Creature.YouCtrl+StrictlyOther | SubAbility$ DBAddCombat\n" +
        "SVar:DBAddCombat:DB$ AddPhase | ExtraPhase$ Combat | AfterPhase$ EndCombat\n",
        "Name:Twin\nManaCost:1\nTypes:Sorcery\nK:Strive:1\n" +
        "A:SP$ Pump | ValidTgts$ Creature.YouCtrl | TargetMin$ 0 | TargetMax$ 4 | NumAtt$ +1 | NumDef$ +1\n",
        "Name:Boss\nManaCost:0\nTypes:Creature Goblin Soldier\nPT:3/3\nK:Mentor\n" +
        "T:Mode$ Phase | Phase$ BeginCombat | ValidPlayer$ You | TriggerZones$ Battlefield | Execute$ TrigEffect\n" +
        "SVar:TrigEffect:DB$ Effect | StaticAbilities$ Nothing | Duration$ UntilEndOfCombat\n" +
        "SVar:Nothing:Mode$ Continuous | Affected$ Card.Self | AddPower$ 0\n",
        "Name:Snoop\nManaCost:0\nTypes:Creature Goblin Rogue\nPT:2/2\n" +
        "S:Mode$ Continuous | TopCardOfLibraryIs$ Goblin | AffectedDefined$ Self | GainsAbilitiesOf$ Goblin.TopLibrary+YouCtrl | GainsAbilitiesOfZones$ Library\n",
        "Name:Fountain Goblin\nManaCost:9\nTypes:Creature Goblin\nPT:1/1\nA:AB$ GainLife | Cost$ 0 | LifeAmount$ 1\n",
        "Name:Warlord\nManaCost:0\nTypes:Creature Human Barbarian\nPT:3/3\n" +
        "T:Mode$ Attacks | ValidCard$ Creature.Self | TriggerZones$ Battlefield | Execute$ TrigUntap | FirstAttack$ True\n" +
        "SVar:TrigUntap:DB$ UntapAll | ValidCards$ Card.Self | SubAbility$ DBAddCombat\n" +
        "SVar:DBAddCombat:DB$ AddPhase | ExtraPhase$ Combat | AfterPhase$ EndCombat\n",
        "Name:Assault\nManaCost:0\nTypes:Sorcery\nA:SP$ UntapAll | ValidCards$ Creature.attackedThisTurn\n",
        "Name:Scourge\nManaCost:0\nTypes:Creature Dragon\nPT:5/5\nK:Dethrone\n",
        "Name:Fury\nManaCost:0\nTypes:Creature Minotaur\nPT:6/6\n" +
        "S:Mode$ Continuous | Affected$ Creature.YouCtrl | AddPower$ AffectedX\nSVar:AffectedX:Count$CardNumAttacksThisTurn\n" +
        "T:Mode$ ChangesZone | Origin$ Any | Destination$ Battlefield | ValidCard$ Land.YouCtrl | TriggerZones$ Battlefield | PlayerTurn$ True | Phase$ Main1,Main2 | Execute$ TrigAddPhase\n" +
        "SVar:TrigAddPhase:DB$ AddPhase | ExtraPhase$ Combat | ExtraPhaseDelayedTrigger$ DelTrigUntap | ExtraPhaseDelayedTriggerExcute$ TrigUntapAll\n" +
        "SVar:DelTrigUntap:Mode$ Phase | Phase$ BeginCombat | ValidPlayer$ You\n" +
        "SVar:TrigUntapAll:DB$ UntapAll | ValidCards$ Creature.YouCtrl\n",
    };

    private static readonly CardPool Pool = new(ForgeLibrary.FromScripts(Scripts, new Dictionary<string, string>()), null);

    private static Game Setup(string[] hand, string[]? battlefield = null, string[]? library = null) =>
        TestGames.Setup(Pool, hand, battlefield, library);

    private static int Life(Game g, int p = Game.Me) => g.State.Players[p].Life;

    private static string[] Fillers(int n) => Enumerable.Repeat("Filler", n).ToArray();

    private static void SetStep(Game g, Step step) =>
        typeof(Game).GetMethod("SetStep", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(g, new object[] { step });

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
    public void ManaThatLastsUntilEndOfTurn()
    {
        var g = Setup(new[] { "Ping" }, battlefield: new[] { "Muse", "Peak" });
        g.CastSpell(g.State[InHand(g, "Ping")], 0);
        ResolveAll(g);
        g.ActivateAbility(OnBattlefield(g, "Peak"), 0);
        Assert.Equal(2, g.State.Players[Game.Me].Pool[ManaColor.R]);
        SetStep(g, Step.BeginCombat);
        Assert.Equal(1, g.State.Players[Game.Me].Pool[ManaColor.R]);   // the land's mana is gone, the persistent one stays
        SetStep(g, Step.Untap);
        Assert.Equal(0, g.State.Players[Game.Me].Pool.Total);
    }

    [Fact]
    public void HideawayAndPlayItLater()
    {
        var g = Setup(new[] { "Knoll" }, battlefield: new[] { "Peak" }, library: new[] { "Big", "Filler", "Filler" });
        var knoll = g.State[InHand(g, "Knoll")];
        g.PlayLand(knoll);
        ResolveAll(g);
        var big = g.State.Cards.First(c => c.Name == "Big");
        Assert.Equal(Zone.Exile, big.Zone);
        Assert.Equal(knoll.Id, big.ExiledWith);
        Assert.Equal(new[] { "Filler", "Filler" }, g.State.Players[Game.Me].Library.Select(id => g.State[id].Name));

        g.ActivateAbility(knoll, 1);   // no opponent was dealt 7 damage yet
        ResolveAll(g);
        Assert.Equal(Zone.Exile, big.Zone);

        var h = Setup(new[] { "Knoll" }, battlefield: new[] { "Peak" }, library: new[] { "Big", "Filler", "Filler" });
        var knoll2 = h.State[InHand(h, "Knoll")];
        h.PlayLand(knoll2);
        ResolveAll(h);
        h.DamagePlayer(1, 7, null);
        h.ActivateAbility(knoll2, 1);
        ResolveAll(h);
        Assert.Equal(45, Life(h));
    }

    [Fact]
    public void ExertForAnotherCombat()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Celebrant", "Bear" }, library: Fillers(5));
        g.ResumeTurn();
        Assert.Equal(40 - 4 - 2 - 2, Life(g, 1));   // the bear untaps and attacks again
        Assert.True(OnBattlefield(g, "Celebrant").SkipNextUntap);
    }

    [Fact]
    public void StriveTakesAsManyTargetsAsThereIsManaFor()
    {
        var g = Setup(new[] { "Twin" }, battlefield: new[] { "Bear", "Bear", "Bear", "Bear", "Peak", "Peak", "Peak" });
        g.CastSpell(g.State[InHand(g, "Twin")], 0);
        ResolveAll(g);
        Assert.Equal(3, g.State.BattlefieldCards.Count(c => c.Name == "Bear" && c.Power == 3));
        Assert.All(g.State.BattlefieldCards.Where(c => c.Name == "Peak"), c => Assert.True(c.Tapped));
    }

    [Fact]
    public void MentorAndEffectsUntilEndOfCombat()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Boss", "Bear" }, library: Fillers(5));
        g.ResumeTurn();
        Assert.Equal(1, OnBattlefield(g, "Bear").CounterCount("P1P1"));
        Assert.DoesNotContain(g.State.Players[Game.Me].Command, id => g.State[id].IsEffect);
    }

    [Fact]
    public void AbilitiesOfTheTopCardOfYourLibrary()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Snoop" }, library: new[] { "Fountain Goblin", "Filler" });
        Statics.Apply(g);
        var snoop = OnBattlefield(g, "Snoop");
        Assert.Single(snoop.Activated);
        g.ActivateAbility(snoop, 0);
        ResolveAll(g);
        Assert.Equal(41, Life(g));

        g.Draw(Game.Me, 1);
        Statics.Apply(g);
        Assert.Empty(snoop.Activated);
    }

    [Fact]
    public void OnlyTheFirstAttackEachTurnUntapsIt()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Warlord" }, library: Fillers(5));
        g.ResumeTurn();
        Assert.Equal(34, Life(g, 1));
        Assert.Equal(2, g.State.ThisTurn.Attacks[OnBattlefield(g, "Warlord").Id]);
    }

    [Fact]
    public void UntapCreaturesThatAttackedThisTurn()
    {
        var g = Setup(new[] { "Assault" }, battlefield: new[] { "Bear", "Muse" });
        var (bear, muse) = (OnBattlefield(g, "Bear"), OnBattlefield(g, "Muse"));
        bear.Tapped = muse.Tapped = true;
        g.State.ThisTurn.Attacks[bear.Id] = 1;
        g.CastSpell(g.State[InHand(g, "Assault")], 0);
        ResolveAll(g);
        Assert.False(bear.Tapped);
        Assert.True(muse.Tapped);
    }

    [Fact]
    public void DethroneWhenAttackingThePlayerWithTheMostLife()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Scourge" }, library: Fillers(5));
        g.ResumeTurn();
        Assert.Equal(1, OnBattlefield(g, "Scourge").CounterCount("P1P1"));

        var h = Setup(Array.Empty<string>(), battlefield: new[] { "Scourge" }, library: Fillers(5));
        h.State.Players[Game.Me].Life = 50;
        h.ResumeTurn();
        Assert.Equal(0, OnBattlefield(h, "Scourge").CounterCount("P1P1"));
    }

    [Fact]
    public void PowerForEachAttackAndLandfallCombats()
    {
        var g = Setup(new[] { "Peak" }, battlefield: new[] { "Fury", "Bear" });
        var bear = OnBattlefield(g, "Bear");
        g.State.ThisTurn.Attacks[bear.Id] = 2;
        Statics.Apply(g);
        Assert.Equal(4, bear.Power);

        g.PlayLand(g.State[InHand(g, "Peak")]);
        ResolveAll(g);
        Assert.Single(g.State.ThisTurn.ExtraPhases);
        Assert.Single(g.State.Delayed);
    }
}
