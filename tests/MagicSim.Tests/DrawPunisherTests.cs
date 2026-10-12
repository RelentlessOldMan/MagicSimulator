using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using MagicSim.Core.Players;
using static MagicSim.Tests.TestGames;

namespace MagicSim.Tests;

/// <summary>
/// "Except the first card they draw in each draw step", "if it's an opponent's turn", hands put on the bottom and
/// drawn again, ending the turn, cipher and split second, on hand-written cards in Forge's format.
/// </summary>
public class DrawPunisherTests
{
    private static readonly string[] Scripts =
    {
        "Name:Peak\nTypes:Land\nA:AB$ Mana | Cost$ T | Produced$ R\n",
        "Name:Filler\nManaCost:9\nTypes:Sorcery\n",
        "Name:Bear\nManaCost:0\nTypes:Creature Bear\nPT:2/2\n",
        "Name:Ping\nManaCost:0\nTypes:Sorcery\nA:SP$ GainLife | LifeAmount$ 1\n",
        "Name:Archer\nManaCost:0\nTypes:Creature Orc Archer\nPT:1/1\n" +
        "T:Mode$ Drawn | ValidCard$ Card.OppOwn | FirstCardInDrawStep$ False | TriggerZones$ Battlefield | Execute$ TrigDamage\n" +
        "SVar:TrigDamage:DB$ DealDamage | NumDmg$ 1 | Defined$ TriggeredPlayer\n",
        "Name:Visions\nManaCost:0\nTypes:Enchantment\n" +
        "T:Mode$ Phase | Phase$ End of Turn | ValidPlayer$ Player | TriggerZones$ Battlefield | Execute$ TrigDraw\n" +
        "SVar:TrigDraw:DB$ Draw | Defined$ TriggeredPlayer | SubAbility$ TrigDamageOpp\n" +
        "SVar:TrigDamageOpp:DB$ DealDamage | Defined$ TriggeredPlayer | NumDmg$ 2 | ConditionOpponentTurn$ True | ConditionCheckSVar$ TrigCount | ConditionSVarCompare$ GE4\n" +
        "SVar:TrigCount:Count$ValidHand Card.ActivePlayerCtrl\n",
        "Name:Puzzle\nManaCost:0\nTypes:Artifact\n" +
        "T:Mode$ Phase | Phase$ Draw | ValidPlayer$ Player | TriggerZones$ Battlefield | Execute$ TrigChangeAll\n" +
        "SVar:TrigChangeAll:DB$ ChangeZoneAll | DefinedPlayer$ TriggeredPlayer | Defined$ TriggeredPlayer | Origin$ Hand | Destination$ Library | LibraryPosition$ -1 | RememberChanged$ True | ForgetOtherRemembered$ True | SubAbility$ DBDraw\n" +
        "SVar:DBDraw:DB$ Draw | NumCards$ X | Defined$ TriggeredPlayer | SubAbility$ DBCleanup\n" +
        "SVar:DBCleanup:DB$ Cleanup | ClearRemembered$ True\nSVar:X:Remembered$Amount\n",
        "Name:Undoing\nManaCost:0\nTypes:Sorcery\n" +
        "A:SP$ Draw | NumCards$ 1 | Defined$ You | SubAbility$ DBEnd\nSVar:DBEnd:DB$ EndTurn | ConditionPlayerTurn$ True\n",
        "Name:Madness\nManaCost:0\nTypes:Sorcery\nK:Cipher\nK:Split second\nA:SP$ GainLife | LifeAmount$ 3\n",
    };

    private static readonly CardPool Pool = new(ForgeLibrary.FromScripts(Scripts, new Dictionary<string, string>()), null);

    private static Game Setup(string[] hand, string[]? battlefield = null, string[]? library = null) =>
        TestGames.Setup(Pool, hand, battlefield, library);

    private static string[] Fillers(int n) => Enumerable.Repeat("Filler", n).ToArray();

    private static int Life(Game g, int p = Game.Me) => g.State.Players[p].Life;

    [Fact]
    public void EveryTestCardCompiles()
    {
        foreach (var s in Scripts)
        {
            var name = s[5..s.IndexOf('\n')];
            Assert.Equal(name + ": ", name + ": " + string.Join("; ", Pool.Resolve(name)!.Missing));
        }
    }

    [Fact]
    public void NotTheFirstCardInTheirDrawStep()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Archer" });
        g.Draw(1, 1);   // in your main phase: counts
        ResolveAll(g);
        Assert.Equal(39, Life(g, 1));

        g.State.ThisTurn = new TurnInfo();
        g.State.ActivePlayer = 1;
        g.State.Step = Step.Draw;
        g.Draw(1, 2);   // their draw step: the first doesn't count, the second does
        ResolveAll(g);
        Assert.Equal(38, Life(g, 1));
    }

    [Fact]
    public void DamageOnlyOnAnOpponentsTurn()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Visions" }, library: Fillers(10));
        g.RunOpponentTurns();   // each opponent draws in their draw and end steps, then takes 2 with 4+ cards
        Assert.Equal(38, Life(g, 1));
        Assert.Equal(38, Life(g, 2));
        Assert.Empty(g.State.Players[Game.Me].Hand);   // "that player draws": only they do

        g.State.ActivePlayer = Game.Me;
        g.State.Step = Step.Main2;
        g.ResumeTurn();   // your end step: you draw, and take no damage
        Assert.Single(g.State.Players[Game.Me].Hand);
        Assert.Equal(40, Life(g));
    }

    [Fact]
    public void PutYourHandOnTheBottomAndDrawThatMany()
    {
        var g = Setup(new[] { "Ping", "Ping" }, battlefield: new[] { "Puzzle" }, library: new[] { "Filler", "Filler", "Bear" });
        g.State.Step = Step.Draw;
        g.FirePhase("Draw");
        ResolveAll(g);
        var me = g.State.Players[Game.Me];
        Assert.Equal(new[] { "Filler", "Filler" }, me.Hand.Select(id => g.State[id].Name));
        Assert.Equal(new[] { "Bear", "Ping", "Ping" }, me.Library.Select(id => g.State[id].Name));

        var opp = g.State.Players[1];
        var (hand, library) = (opp.AbstractHand, opp.AbstractLibrary);
        g.State.ActivePlayer = 1;
        g.FirePhase("Draw");
        ResolveAll(g);
        Assert.Equal(hand, opp.AbstractHand);
        Assert.Equal(library, opp.AbstractLibrary);
    }

    [Fact]
    public void EndTheTurn()
    {
        var g = Setup(new[] { "Undoing", "Ping" }, library: Fillers(5));
        g.CastSpell(g.State[InHand(g, "Ping")], 0);
        g.CastSpell(g.State[InHand(g, "Undoing")], 0);
        g.ResumeTurn();
        Assert.Equal(40, Life(g));   // Ping was exiled from the stack
        Assert.Equal(Zone.Exile, g.State.Cards.First(c => c.Name == "Ping").Zone);
        Assert.Equal(Zone.Exile, g.State.Cards.First(c => c.Name == "Undoing").Zone);
        Assert.Equal(Step.Cleanup, g.State.Step);
    }

    [Fact]
    public void CipherCastsACopyWhenTheCreatureConnects()
    {
        var g = Setup(new[] { "Madness" }, battlefield: new[] { "Bear" }, library: Fillers(5));
        var madness = g.State[InHand(g, "Madness")];
        g.CastSpell(madness, 0);
        ResolveAll(g);
        Assert.Equal(43, Life(g));
        Assert.Equal(Zone.Exile, madness.Zone);
        Assert.Equal(OnBattlefield(g, "Bear").Id, madness.EncodedOn);
        g.ResumeTurn();   // the bear attacks and connects
        Assert.Equal(46, Life(g));
        Assert.Equal(Zone.Exile, madness.Zone);
    }
}
