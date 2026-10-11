using MagicSim.Core.Engine;
using MagicSim.Core.Forge;
using MagicSim.Core.Players;
using static MagicSim.Tests.TestGames;

namespace MagicSim.Tests;

/// <summary>
/// Rules tests for continuous effects, attachments, tokens, modal spells, replacements and
/// keyword abilities, on hand-written cards in Forge's script format (nothing copied from Forge).
/// </summary>
public class MechanicsTests
{
    private static readonly string[] Scripts =
    {
        "Name:Bog\nManaCost:no cost\nTypes:Basic Land Swamp\n",
        "Name:Plain\nManaCost:no cost\nTypes:Basic Land Plains\n",
        "Name:Shrine\nManaCost:no cost\nTypes:Land Plains Swamp\n" +
        "R:Event$ Moved | ValidCard$ Card.Self | Destination$ Battlefield | ReplaceWith$ DBTap | ReplacementResult$ Updated\n" +
        "SVar:DBTap:DB$ Tap | ETB$ True | Defined$ Self | UnlessCost$ PayLife<2> | UnlessPayer$ You\n",
        "Name:Fort\nManaCost:no cost\nTypes:Land\n" +
        "R:Event$ Moved | ValidCard$ Card.Self | Destination$ Battlefield | ReplaceWith$ LandTapped | ReplacementResult$ Updated\n" +
        "SVar:LandTapped:DB$ Tap | Defined$ Self | ETB$ True | ConditionPresent$ Plains.YouCtrl,Island.YouCtrl | ConditionCompare$ EQ0\n" +
        "A:AB$ Mana | Cost$ T | Produced$ Combo W U\n",
        "Name:Vault\nManaCost:1\nTypes:Artifact\n" +
        "R:Event$ Untap | ValidCard$ Card.Self | ValidStepTurnToController$ You | Layer$ CantHappen\n" +
        "A:AB$ Mana | Cost$ T | Produced$ C | Amount$ 3\n",
        "Name:Anthem\nManaCost:2\nTypes:Enchantment\nS:Mode$ Continuous | Affected$ Creature.YouCtrl | AddPower$ 1 | AddToughness$ 1\n",
        "Name:Bear\nManaCost:2\nTypes:Creature Bear\nPT:2/2\n",
        "Name:Blade\nManaCost:1\nTypes:Artifact Equipment\nK:Equip:1\n" +
        "S:Mode$ Continuous | AffectedDefined$ Equipped | Affected$ Creature | AddPower$ 2 | AddKeyword$ Haste\n",
        "Name:Might\nManaCost:B\nTypes:Enchantment Aura\nK:Enchant:Creature\nS:Mode$ Continuous | AffectedDefined$ Enchanted | AddPower$ 3\n",
        "Name:Goyf\nManaCost:1 B\nTypes:Creature Lhurgoyf\nPT:*/1+*\n" +
        "S:Mode$ Continuous | CharacteristicDefining$ True | SetPower$ X | SetToughness$ Y\n" +
        "SVar:X:Count$ValidGraveyard Card.YouOwn\nSVar:Y:SVar$X/Plus.1\n",
        "Name:Muster\nManaCost:1\nTypes:Sorcery\nA:SP$ Token | TokenScript$ w_1_1_soldier | TokenAmount$ 2 | TokenOwner$ You\n",
        "Name:Loot\nManaCost:0\nTypes:Sorcery\nA:SP$ Token | TokenScript$ c_a_treasure_sac | TokenOwner$ You\n",
        "Name:Command\nManaCost:B\nTypes:Instant\nA:SP$ Charm | Choices$ DBDraw,DBGain,DBHit | CharmNum$ 2\n" +
        "SVar:DBDraw:DB$ Draw | Defined$ You | NumCards$ 1 | SpellDescription$ Draw a card.\n" +
        "SVar:DBGain:DB$ GainLife | Defined$ You | LifeAmount$ 3 | SpellDescription$ Gain 3 life.\n" +
        "SVar:DBHit:DB$ DealDamage | ValidTgts$ Player | NumDmg$ 2 | SpellDescription$ 2 damage.\n",
        "Name:Cycler\nManaCost:9\nTypes:Creature Beast\nPT:9/9\nK:Cycling:1\n",
        "Name:Recall\nManaCost:B\nTypes:Sorcery\nK:Flashback:1 B\nA:SP$ Draw | Defined$ You | NumCards$ 1\n",
        "Name:Golem\nManaCost:5\nTypes:Artifact Creature Golem\nPT:3/3\n" +
        "S:Mode$ ReduceCost | ValidCard$ Card.Self | Type$ Spell | Amount$ 3 | EffectZone$ All\n",
        "Name:Wrath\nManaCost:2\nTypes:Sorcery\nA:SP$ DestroyAll | ValidCards$ Creature\n",
        "Name:Quake\nManaCost:1\nTypes:Sorcery\nA:SP$ DamageAll | NumDmg$ 2 | ValidCards$ Creature | ValidPlayers$ Player\n",
        "Name:Nope\nManaCost:B\nTypes:Instant\nA:SP$ Counter | TargetType$ Spell | ValidTgts$ Card\n",
        "Name:Look\nManaCost:B\nTypes:Sorcery\nA:SP$ RearrangeTopOfLibrary | Defined$ You | NumCards$ 3\n",
        "Name:Sneak\nManaCost:B\nTypes:Creature Rogue\nPT:1/1\nK:Haste\n" +
        "T:Mode$ DamageDone | ValidSource$ Card.Self | ValidTarget$ Player | CombatDamage$ True | Execute$ TrigDraw\n" +
        "SVar:TrigDraw:DB$ Draw | Defined$ You | NumCards$ 1\n",
        "Name:Explore\nManaCost:G\nTypes:Enchantment\nS:Mode$ Continuous | Affected$ You | AdjustLandPlays$ 1\n",
        "Name:Filler\nManaCost:9\nTypes:Sorcery\nA:SP$ Draw | NumCards$ 1\n",
        "Name:Sig\nManaCost:2\nTypes:Artifact\nA:AB$ Mana | Cost$ 1 T | Produced$ U B\n",
        "Name:Plow\nManaCost:W\nTypes:Instant\n" +
        "A:SP$ ChangeZone | ValidTgts$ Creature | Origin$ Battlefield | Destination$ Exile | SubAbility$ DBGain\n" +
        "SVar:DBGain:DB$ GainLife | Defined$ TargetedController | LifeAmount$ X\nSVar:X:Targeted$CardPower\n",
        "Name:Grow\nManaCost:2\nTypes:Sorcery\n" +
        "A:SP$ ChangeZone | Origin$ Library | Destination$ Library | ChangeType$ Land.Basic | ChangeNum$ 2 | RememberChanged$ True | Shuffle$ False | SubAbility$ DB1\n" +
        "SVar:DB1:DB$ ChangeZone | Origin$ Library | Destination$ Battlefield | ChangeType$ Land.IsRemembered | ChangeNum$ 1 | Mandatory$ True | Tapped$ True | Shuffle$ False | SubAbility$ DB2\n" +
        "SVar:DB2:DB$ ChangeZone | Origin$ Library | Destination$ Hand | ChangeType$ Land.IsRemembered | Mandatory$ True | SubAbility$ DBC\n" +
        "SVar:DBC:DB$ Cleanup | ClearRemembered$ True\n",
        "Name:Nausea\nManaCost:B\nTypes:Instant\nA:SP$ Repeat | RepeatSubAbility$ DBDig | RepeatOptional$ True\n" +
        "SVar:DBDig:DB$ Dig | DigNum$ 1 | ChangeNum$ All | ChangeValid$ Card | DestinationZone$ Hand | RememberChanged$ True | SubAbility$ DBLose\n" +
        "SVar:DBLose:DB$ LoseLife | LifeAmount$ X | SubAbility$ DBCleanup\nSVar:DBCleanup:DB$ Cleanup | ClearRemembered$ True\n" +
        "SVar:X:Remembered$CardManaCost\n",
        "Name:Will\nManaCost:3 U U\nTypes:Instant\n" +
        "S:Mode$ AlternativeCost | ValidSA$ Spell.Self | EffectZone$ All | Cost$ PayLife<1> ExileFromHand<1/Card.Blue+Other>\n" +
        "A:SP$ Counter | TargetType$ Spell | ValidTgts$ Card\n",
        "Name:Blueberry\nManaCost:U\nTypes:Instant\nA:SP$ Draw | NumCards$ 1\n",
        "Name:Flip\nManaCost:0\nTypes:Instant\nA:SP$ FlipCoin | WinSubAbility$ DBW | LoseSubAbility$ DBL\n" +
        "SVar:DBW:DB$ GainLife | Defined$ You | LifeAmount$ 1\nSVar:DBL:DB$ LoseLife | Defined$ You | LifeAmount$ 1\n",
        "Name:Stone\nManaCost:2\nTypes:Artifact\nA:AB$ ManaReflected | Cost$ T | ColorOrType$ Color | Valid$ Land.OppCtrl | ReflectProperty$ Produce\n",
        "Name:Pool\nManaCost:no cost\nTypes:Land\nA:AB$ ManaReflected | Cost$ T | ColorOrType$ Type | Valid$ Land.YouCtrl | ReflectProperty$ Produce\n",
        "Name:Sprout\nManaCost:G\nTypes:Sorcery\nA:SP$ Draw | NumCards$ 1\n",
    };

    private static readonly Dictionary<string, string> Tokens = new()
    {
        ["w_1_1_soldier"] = "Name:Soldier Token\nManaCost:no cost\nColors:white\nTypes:Creature Soldier\nPT:1/1\n",
        ["c_a_treasure_sac"] = "Name:Treasure Token\nManaCost:no cost\nTypes:Artifact Treasure\n" +
            "A:AB$ Mana | Cost$ T Sac<1/CARDNAME/this token> | Produced$ Any | Amount$ 1\n",
    };

    private static readonly CardPool Pool = new(ForgeLibrary.FromScripts(Scripts, Tokens), null);

    private static Game Setup(string[] hand, string[]? battlefield = null, string[]? library = null, TestDecider? decider = null) =>
        TestGames.Setup(Pool, hand, battlefield, library, decider);

    [Fact]
    public void AllTestCardsCompile()
    {
        foreach (var script in Scripts)
        {
            var name = script.Split('\n')[0][5..];
            Assert.True(Pool.Compile(name)!.Supported, $"{name}: {string.Join(", ", Pool.Compile(name)!.Missing)}");
        }
    }

    [Fact]
    public void AnthemPumpsYourCreaturesWhileItIsThere()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Bear", "Anthem" });
        g.CheckStateBasedActions();
        var bear = OnBattlefield(g, "Bear");
        Assert.Equal((3, 3), (bear.Power, bear.Toughness));
        g.MoveTo(OnBattlefield(g, "Anthem"), Zone.Graveyard);
        g.CheckStateBasedActions();
        Assert.Equal((2, 2), (bear.Power, bear.Toughness));
    }

    [Fact]
    public void EquipmentGivesItsBonusToTheEquippedCreature()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Bear", "Blade", "Bog" });
        var blade = OnBattlefield(g, "Blade");
        var equip = blade.CurrentFace.Activated.FindIndex(a => a.Api == "Attach");
        Assert.Contains(g.LegalActions(Game.Me), a => a is GameAction.Activate act && act.CardId == blade.Id && act.AbilityIndex == equip);
        g.ActivateAbility(blade, equip);
        ResolveAll(g);
        var bear = OnBattlefield(g, "Bear");
        Assert.Equal(bear.Id, blade.AttachedTo);
        Assert.Equal(4, bear.Power);
        Assert.True(bear.HasKeyword("Haste"));
        Assert.True(OnBattlefield(g, "Bog").Tapped);   // paid {1}
    }

    [Fact]
    public void AuraAttachesToItsTargetAndDiesWithIt()
    {
        var g = Setup(new[] { "Might" }, battlefield: new[] { "Bear", "Bog" });
        g.CastSpell(g.State[InHand(g, "Might")], -1);
        ResolveAll(g);
        var bear = OnBattlefield(g, "Bear");
        Assert.Equal(5, bear.Power);
        g.MoveTo(bear, Zone.Graveyard);
        g.CheckStateBasedActions();
        Assert.DoesNotContain(g.State.BattlefieldCards, c => c.Name == "Might");
    }

    [Fact]
    public void CharacteristicDefiningPowerCountsTheGraveyard()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Goyf" }, library: new[] { "Filler", "Filler" });
        g.Mill(Game.Me, 2);
        g.CheckStateBasedActions();
        var goyf = OnBattlefield(g, "Goyf");
        Assert.Equal((2, 3), (goyf.Power, goyf.Toughness));
    }

    [Fact]
    public void TokensAreCreatedAndTreasureCanPay()
    {
        var g = Setup(new[] { "Loot", "Muster" });
        g.CastSpell(g.State[InHand(g, "Loot")], 0);
        ResolveAll(g);
        Assert.Contains(g.State.BattlefieldCards, c => c.IsToken && c.Types.HasSubtype("Treasure"));
        Assert.True(g.CanCast(g.State[InHand(g, "Muster")], 0));   // the Treasure pays {1}
        g.CastSpell(g.State[InHand(g, "Muster")], 0);
        ResolveAll(g);
        var soldiers = g.State.BattlefieldCards.Where(c => c.IsToken && c.Types.HasSubtype("Soldier")).ToList();
        Assert.Equal(2, soldiers.Count);
        Assert.All(soldiers, s => Assert.Equal(1, s.Power));
        Assert.DoesNotContain(g.State.BattlefieldCards, c => c.Types.HasSubtype("Treasure"));   // sacrificed, and gone
    }

    [Fact]
    public void ModalSpellOffersEachPairOfModes()
    {
        var decider = new TestDecider { Choose = (game, d) => d.Kind == DecisionKind.Target ? d.Options.ToList().FindIndex(o => o is TargetRef { Player: 1 }) : null };
        var g = Setup(new[] { "Command" }, battlefield: new[] { "Bog" }, library: new[] { "Filler" }, decider: decider);
        var command = g.State[InHand(g, "Command")];
        Assert.Equal(3, command.CurrentFace.SpellAbilities.Count);   // choose two of three
        var drawHit = command.CurrentFace.SpellAbilities.FindIndex(a => a.Description == "Draw a card. + 2 damage.");
        Assert.True(drawHit >= 0);
        g.CastSpell(command, drawHit);
        ResolveAll(g);
        Assert.Equal(38, g.State.Players[1].Life);
        Assert.Contains(g.State.Players[Game.Me].Hand, id => g.State[id].Name == "Filler");
        Assert.Equal(40, g.State.Players[Game.Me].Life);
    }

    [Theory]
    [InlineData(true, 38, false)]
    [InlineData(false, 40, true)]
    public void ShocklandPaysTwoLifeOrEntersTapped(bool pay, int life, bool tapped)
    {
        var decider = new TestDecider { Choose = (game, d) => d.Kind == DecisionKind.YesNo ? (pay ? 0 : 1) : null };
        var g = Setup(new[] { "Shrine" }, decider: decider);
        g.PlayLand(g.State[InHand(g, "Shrine")]);
        Assert.Equal(life, g.State.Players[Game.Me].Life);
        Assert.Equal(tapped, OnBattlefield(g, "Shrine").Tapped);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ChecklandEntersTappedWithoutTheRightBasic(bool havePlains, bool tapped)
    {
        var g = Setup(new[] { "Fort" }, battlefield: havePlains ? new[] { "Plain" } : new[] { "Bog" });
        g.PlayLand(g.State[InHand(g, "Fort")]);
        Assert.Equal(tapped, OnBattlefield(g, "Fort").Tapped);
    }

    [Fact]
    public void ManaVaultStyleArtifactStaysTapped()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Vault", "Bog" });
        foreach (var c in g.State.BattlefieldCards) c.Tapped = true;
        g.RunTurn();
        Assert.True(OnBattlefield(g, "Vault").Tapped);
        Assert.False(OnBattlefield(g, "Bog").Tapped);
    }

    [Fact]
    public void CyclingDiscardsAndDraws()
    {
        var g = Setup(new[] { "Cycler" }, battlefield: new[] { "Bog" }, library: new[] { "Filler" });
        var cycler = g.State[InHand(g, "Cycler")];
        var cycle = g.LegalActions(Game.Me).OfType<GameAction.Activate>().Single(a => a.CardId == cycler.Id);
        g.Perform(cycle);
        ResolveAll(g);
        Assert.Equal(Zone.Graveyard, cycler.Zone);
        Assert.Contains(g.State.Players[Game.Me].Hand, id => g.State[id].Name == "Filler");
    }

    [Fact]
    public void FlashbackCastsFromTheGraveyardThenExiles()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Bog", "Bog" }, library: new[] { "Filler", "Recall" });
        g.Mill(Game.Me, 2);
        var recall = g.State.Cards.First(c => c.Name == "Recall");
        Assert.Equal(Zone.Graveyard, recall.Zone);
        var cast = g.LegalActions(Game.Me).OfType<GameAction.Cast>().Single(a => a.CardId == recall.Id);
        g.Perform(cast);
        Assert.All(g.State.BattlefieldCards, c => Assert.True(c.Tapped));   // paid {1}{B}
        ResolveAll(g);
        Assert.Equal(Zone.Exile, recall.Zone);
    }

    [Fact]
    public void SelfCostReductionMakesItCheaper()
    {
        var g = Setup(new[] { "Golem" }, battlefield: new[] { "Bog", "Bog" });
        Assert.True(g.CanCast(g.State[InHand(g, "Golem")], -1));
        var h = Setup(new[] { "Golem" }, battlefield: new[] { "Bog" });
        Assert.False(h.CanCast(h.State[InHand(h, "Golem")], -1));
    }

    [Fact]
    public void SweepersHitEverything()
    {
        var g = Setup(new[] { "Quake", "Wrath" }, battlefield: new[] { "Bear", "Goyf", "Bog", "Bog", "Bog" }, library: new[] { "Filler", "Filler", "Filler" });
        g.Mill(Game.Me, 3);   // Goyf is 3/4
        g.CastSpell(g.State[InHand(g, "Quake")], 0);
        ResolveAll(g);
        Assert.DoesNotContain(g.State.BattlefieldCards, c => c.Name == "Bear");
        Assert.Equal(38, g.State.Players[1].Life);
        Assert.Equal(38, g.State.Players[Game.Me].Life);
        Assert.Contains(g.State.BattlefieldCards, c => c.Name == "Goyf");
        g.CastSpell(g.State[InHand(g, "Wrath")], 0);
        ResolveAll(g);
        Assert.DoesNotContain(g.State.BattlefieldCards, c => c.Types.Is(CardType.Creature));
    }

    [Fact]
    public void CounterspellCountersTargetSpell()
    {
        var g = Setup(new[] { "Bear", "Nope" }, battlefield: new[] { "Bog", "Bog", "Bog" });
        g.CastSpell(g.State[InHand(g, "Bear")], -1);
        g.CastSpell(g.State[InHand(g, "Nope")], 0);
        ResolveAll(g);
        Assert.Empty(g.State.Stack);
        Assert.Equal(Zone.Graveyard, g.State.Cards.First(c => c.Name == "Bear").Zone);
    }

    [Fact]
    public void LookingAtTheTopMakesThoseCardsKnown()
    {
        var decider = new TestDecider { Choose = (game, d) => d.Kind == DecisionKind.Card ? TestDecider.Named(game, d, "Bear") : null };
        var g = Setup(new[] { "Look" }, battlefield: new[] { "Bog" }, library: new[] { "Filler", "Filler", "Bear", "Filler" }, decider: decider);
        g.CastSpell(g.State[InHand(g, "Look")], 0);
        ResolveAll(g);
        var me = g.State.Players[Game.Me];
        Assert.Equal("Bear", g.State[me.Library[0]].Name);
        Assert.Equal(3, me.KnownTop);
    }

    [Fact]
    public void CombatDamageTriggerDraws()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Sneak" }, library: new[] { "Filler", "Filler" });
        g.ResumeTurn();
        Assert.Equal(39, g.State.Players.Where(p => p.Index != Game.Me).Min(p => p.Life));
        Assert.Single(g.State.Players[Game.Me].Hand);
    }

    [Fact]
    public void SignetAddsTwoColors()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Sig", "Bog" });
        var sig = OnBattlefield(g, "Sig");
        g.ActivateAbility(sig, 0);
        Assert.Equal(1, g.State.Players[Game.Me].Pool[ManaColor.U]);
        Assert.Equal(1, g.State.Players[Game.Me].Pool[ManaColor.B]);
    }

    [Fact]
    public void ControllerGainsLifeEqualToTheExiledCreaturesPower()
    {
        var g = Setup(new[] { "Plow" }, battlefield: new[] { "Plain", "Goyf" }, library: new[] { "Filler", "Filler", "Filler" });
        g.Mill(Game.Me, 3);   // Goyf is 3/4
        g.CastSpell(g.State[InHand(g, "Plow")], 0);
        ResolveAll(g);
        Assert.Equal(43, g.State.Players[Game.Me].Life);
    }

    [Fact]
    public void RememberedSearchSplitsLandsBetweenBattlefieldAndHand()
    {
        var g = Setup(new[] { "Grow" }, battlefield: new[] { "Bog", "Bog", "Bog" }, library: new[] { "Filler", "Plain", "Filler", "Bog" });
        g.CastSpell(g.State[InHand(g, "Grow")], 0);
        ResolveAll(g);
        var me = g.State.Players[Game.Me];
        Assert.Equal(4, g.State.BattlefieldCards.Count(c => c.Types.Is(CardType.Land)));
        Assert.Single(me.Hand, id => g.State[id].Types.Is(CardType.Land));
        Assert.Null(g.State.Cards.First(c => c.Name == "Grow").Remembered);
    }

    [Fact]
    public void OptionalRepeatKeepsDigging()
    {
        var reps = 0;
        var decider = new TestDecider { Choose = (game, d) => d.Kind == DecisionKind.YesNo ? (++reps < 3 ? 0 : 1) : null };
        var g = Setup(new[] { "Nausea" }, battlefield: new[] { "Bog" }, library: new[] { "Bear", "Recall", "Filler", "Filler" }, decider: decider);
        g.CastSpell(g.State[InHand(g, "Nausea")], 0);
        ResolveAll(g);
        Assert.Equal(3, g.State.Players[Game.Me].Hand.Count);    // three reveals: Bear (2), Recall (1), Filler (9)
        Assert.Equal(40 - 2 - 1 - 9, g.State.Players[Game.Me].Life);
    }

    [Fact]
    public void AlternativeCostExilesABlueCardAndPaysLife()
    {
        var g = Setup(new[] { "Bear", "Will", "Blueberry" }, battlefield: new[] { "Bog", "Bog" });
        g.CastSpell(g.State[InHand(g, "Bear")], -1);
        var will = g.State[InHand(g, "Will")];
        var alt = will.CurrentFace.SpellAbilities.FindIndex(a => a.AltCost is not null);
        Assert.False(g.CanCast(will, 0));    // can't pay {3}{U}{U}
        Assert.True(g.CanCast(will, alt));
        g.CastSpell(will, alt);
        ResolveAll(g);
        Assert.Equal(39, g.State.Players[Game.Me].Life);
        Assert.Equal(Zone.Exile, g.State.Cards.First(c => c.Name == "Blueberry").Zone);
        Assert.Equal(Zone.Graveyard, g.State.Cards.First(c => c.Name == "Bear").Zone);
    }

    [Fact]
    public void CoinFlipRunsOneOutcomeAndBothHappen()
    {
        var lives = new HashSet<int>();
        for (var seed = 1UL; seed <= 20; seed++)
        {
            var g = Setup(new[] { "Flip" });
            g.State.Rng = new Rng(seed);
            g.CastSpell(g.State[InHand(g, "Flip")], 0);
            ResolveAll(g);
            lives.Add(g.State.Players[Game.Me].Life);
        }
        Assert.Equal(new HashSet<int> { 39, 41 }, lives);
    }

    [Fact]
    public void ReflectedManaFromOpponentsLandsIsAnyColor()
    {
        var g = Setup(new[] { "Sprout" }, battlefield: new[] { "Stone" });
        Assert.True(g.CanCast(g.State[InHand(g, "Sprout")], 0));
    }

    [Fact]
    public void ReflectedManaFromYourLandsIsOnlyTheirColors()
    {
        var g = Setup(new[] { "Sprout", "Recall" }, battlefield: new[] { "Pool", "Bog" });
        Assert.False(g.CanCast(g.State[InHand(g, "Sprout")], 0));   // Bog only makes {B}
        Assert.True(g.CanCast(g.State[InHand(g, "Recall")], 0));    // Pool reflects {B}; Bog pays the other
    }

    [Fact]
    public void ExtraLandDropFromAStatic()
    {
        var g = Setup(new[] { "Bog", "Plain" }, battlefield: new[] { "Explore" });
        g.CheckStateBasedActions();
        g.PlayLand(g.State[InHand(g, "Bog")]);
        Assert.Contains(g.LegalActions(Game.Me), a => a is GameAction.PlayLand);
    }
}
