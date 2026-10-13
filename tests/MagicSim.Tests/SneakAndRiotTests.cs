using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using MagicSim.Core.Players;
using static MagicSim.Tests.TestGames;

namespace MagicSim.Tests;

/// <summary>
/// Putting creatures onto the battlefield attacking (returned to hand at the end step), forgetting other remembered
/// cards, once-each-turn activations, "whose controller was dealt combat damage by this", bloodthirst and riot,
/// on hand-written cards in Forge's format.
/// </summary>
public class SneakAndRiotTests
{
    private static readonly string[] Scripts =
    {
        "Name:Peak\nTypes:Land Mountain\nA:AB$ Mana | Cost$ T | Produced$ R\n",
        "Name:Filler\nManaCost:9\nTypes:Sorcery\n",
        "Name:Imp\nManaCost:0\nTypes:Creature Imp\nPT:1/1\n",
        "Name:Giant\nManaCost:6\nTypes:Creature Giant\nPT:6/6\n",
        "Name:Boar\nManaCost:5\nTypes:Creature Boar\nPT:5/5\n" +
        "T:Mode$ Attacks | ValidCard$ Card.Self | Execute$ TrigChange | OptionalDecider$ You\n" +
        "SVar:TrigChange:DB$ ChangeZone | Origin$ Hand | Destination$ Battlefield | ChangeType$ Creature.YouCtrl | Tapped$ True | Attacking$ True | AtEOT$ Hand\n",
        "Name:Sneak\nManaCost:3 R\nTypes:Enchantment\n" +
        "A:AB$ ChangeZone | Cost$ R | Origin$ Hand | Destination$ Battlefield | ChangeType$ Creature.YouCtrl | SubAbility$ DBPump | RememberChanged$ True | ForgetOtherRemembered$ True\n" +
        "SVar:DBPump:DB$ Animate | Keywords$ Haste | Defined$ Remembered | Duration$ Permanent | AtEOT$ Sacrifice\n",
        "Name:Kite\nManaCost:6\nTypes:Artifact Creature Dragon\nPT:5/5\n" +
        "A:AB$ DestroyAll | Cost$ X | ValidCards$ Permanent.nonLand+cmcEQX+ControlledBy Player.wasDealtCombatDamageThisTurnBySource | ActivationLimit$ 1 | Hidden$ True\n" +
        "SVar:X:Count$xPaid\n",
        "Name:Furyborn\nManaCost:0\nTypes:Creature Dragon\nPT:1/1\nK:Bloodthirst:2\n",
        "Name:Zap\nManaCost:0\nTypes:Sorcery\nA:SP$ DealDamage | ValidTgts$ Opponent | NumDmg$ 1\n",
        "Name:Rioter\nManaCost:0\nTypes:Creature Goblin\nPT:1/1\nK:Riot\n",
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
    public void PutOntoTheBattlefieldAttackingThenBackToHand()
    {
        var g = Setup(new[] { "Giant" }, new[] { "Boar" }, Fillers(5));
        var giant = g.State[InHand(g, "Giant")];
        g.ResumeTurn();
        Assert.Equal(40 - 5 - 6, g.State.Players[1].Life);
        Assert.Equal(Zone.Hand, giant.Zone);
    }

    [Fact]
    public void RemembersOnlyTheCreatureJustPutOntoTheBattlefield()
    {
        var g = Setup(new[] { "Imp", "Giant" }, new[] { "Sneak", "Peak", "Peak" });
        var sneak = OnBattlefield(g, "Sneak");
        g.ActivateAbility(sneak, 0);
        ResolveAll(g);
        g.ActivateAbility(sneak, 0);
        ResolveAll(g);
        Assert.Single(sneak.Remembered!);
        Assert.All(g.State.BattlefieldCards.Where(c => c.Name is "Imp" or "Giant"), c => Assert.True(c.HasKeyword("Haste")));
    }

    [Fact]
    public void DestroyWhatThePlayerItHitControlsOnceEachTurn()
    {
        var g = Setup(Array.Empty<string>(), new[] { "Kite", "Imp", "Imp" });
        var kite = OnBattlefield(g, "Kite");
        var imps = g.State.BattlefieldCards.Where(c => c.Name == "Imp").ToList();
        imps[0].Controller = 1;
        g.State.ThisTurn.CombatDamagedBy.Add((kite.Id, 1));
        g.ActivateAbility(kite, 0);
        ResolveAll(g);
        Assert.Equal(Zone.Graveyard, imps[0].Zone);
        Assert.Equal(Zone.Battlefield, imps[1].Zone);   // its controller wasn't hit
        Assert.DoesNotContain(g.LegalActions(Game.Me), a => a is GameAction.Activate act && act.CardId == kite.Id);
        g.State.Turn++;
        Assert.Contains(g.LegalActions(Game.Me), a => a is GameAction.Activate act && act.CardId == kite.Id);
    }

    [Fact]
    public void BloodthirstOnlyAfterAnOpponentWasDealtDamage()
    {
        var g = Setup(new[] { "Furyborn", "Zap", "Furyborn" });
        Cast(g, "Furyborn");
        Assert.Equal(0, OnBattlefield(g, "Furyborn").CounterCount("P1P1"));
        Cast(g, "Zap");
        Cast(g, "Furyborn");
        Assert.Contains(g.State.BattlefieldCards, c => c.Name == "Furyborn" && c.CounterCount("P1P1") == 2);
    }

    [Fact]
    public void RiotChoosesACounterOrHaste()
    {
        var g = Setup(new[] { "Rioter" });
        Cast(g, "Rioter");
        var rioter = OnBattlefield(g, "Rioter");
        Assert.Equal(1, rioter.CounterCount("P1P1"));
        Assert.False(rioter.HasKeyword("Haste"));

        var h = Setup(new[] { "Rioter" }, decider: new TestDecider { Choose = (game, d) => d.Kind == DecisionKind.Option ? 1 : null });
        Cast(h, "Rioter");
        var hasty = OnBattlefield(h, "Rioter");
        Assert.Equal(0, hasty.CounterCount("P1P1"));
        Assert.True(hasty.HasKeyword("Haste"));
    }
}
