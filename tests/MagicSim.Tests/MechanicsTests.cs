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
        "Name:Impulse\nManaCost:0\nTypes:Sorcery\n" +
        "A:SP$ Dig | Defined$ You | DigNum$ 1 | ChangeNum$ All | DestinationZone$ Exile | RememberChanged$ True | SubAbility$ DBEffect\n" +
        "SVar:DBEffect:DB$ Effect | StaticAbilities$ STPlay | RememberObjects$ Remembered | ForgetOnMoved$ Exile | SubAbility$ DBCleanup\n" +
        "SVar:STPlay:Mode$ Continuous | MayPlay$ True | Affected$ Card.IsRemembered | AffectedZone$ Exile\n" +
        "SVar:DBCleanup:DB$ Cleanup | ClearRemembered$ True\n",
        "Name:Mimic\nManaCost:2\nTypes:Creature Shapeshifter\nPT:0/0\nK:ETBReplacement:Copy:DBCopy:Optional\n" +
        "SVar:DBCopy:DB$ Clone | Choices$ Creature.Other | AddTypes$ Artifact\n",
        "Name:Mirror\nManaCost:0\nTypes:Artifact\n" +
        "A:AB$ CopyPermanent | Cost$ T | ValidTgts$ Creature.YouCtrl | AddKeywords$ Haste | NonLegendary$ True | AtEOT$ Sacrifice\n",
        "Name:Glass\nManaCost:0\nTypes:Artifact\nA:AB$ Clone | Cost$ 0 | ValidTgts$ Creature | Duration$ UntilEndOfTurn\n",
        "Name:Colonnade\nManaCost:no cost\nTypes:Land\nA:AB$ Mana | Cost$ T | Produced$ W\n" +
        "A:AB$ Animate | Cost$ 0 | Defined$ Self | Power$ 4 | Toughness$ 4 | Types$ Creature,Elemental | Colors$ White | OverwriteColors$ True | Keywords$ Flying & Vigilance\n",
        "Name:Lantern\nManaCost:3\nTypes:Artifact\nS:Mode$ Continuous | Affected$ Land.YouCtrl | AddAbility$ AnyMana\nSVar:AnyMana:AB$ Mana | Cost$ T | Produced$ Any\n",
        "Name:Rally\nManaCost:1\nTypes:Enchantment\nS:Mode$ Continuous | Affected$ Creature.YouCtrl | AddTrigger$ TrigAtk | AddSVar$ X\n" +
        "SVar:TrigAtk:Mode$ Attacks | ValidCard$ Card.Self | Execute$ TrigGain\nSVar:TrigGain:DB$ GainLife | Defined$ You | LifeAmount$ X\n" +
        "SVar:X:Count$Valid Enchantment.YouCtrl/Plus.2\n",
        "Name:March\nManaCost:3\nTypes:Enchantment\n" +
        "S:Mode$ Continuous | Affected$ Artifact.nonCreature+YouCtrl | AddType$ Creature & Golem | SetPower$ 2 | SetToughness$ 2\n",
        "Name:Shifter\nManaCost:1\nTypes:Creature Shapeshifter\nPT:1/1\nK:Changeling\n",
        "Name:Totem\nManaCost:1\nTypes:Artifact\nS:Mode$ Continuous | Affected$ Bear.YouCtrl | AddPower$ 1\n",
        "Name:Tale\nManaCost:0\nTypes:Enchantment Saga\nK:Chapter:2:DBGain,DBDraw\n" +
        "SVar:DBGain:DB$ GainLife | Defined$ You | LifeAmount$ 2\nSVar:DBDraw:DB$ Draw | Defined$ You | NumCards$ 1\n",
        "Name:Spread\nManaCost:0\nTypes:Sorcery\nA:SP$ Proliferate\n",
        "Name:Lotus\nManaCost:0\nTypes:Artifact\nA:AB$ Mana | Cost$ T | Produced$ Any | Amount$ 3 | RestrictValid$ Spell.Creature\n",
        "Name:Grove\nManaCost:no cost\nTypes:Land\nA:AB$ Mana | Cost$ T | Produced$ G | IsPresent$ Creature.YouCtrl\n",
        "Name:Spell Side\nManaCost:1\nTypes:Sorcery\nA:SP$ GainLife | Defined$ You | LifeAmount$ 3\nAlternateMode:Modal\n" +
        "ALTERNATE\nName:Land Side\nManaCost:no cost\nTypes:Land\nA:AB$ Mana | Cost$ T | Produced$ W\n",
        "Name:Thane\nManaCost:0\nTypes:Enchantment\n" +
        "R:Event$ DamageDone | ActiveZones$ Battlefield | ValidSource$ Card.YouCtrl | ValidTarget$ Player.Opponent,Permanent.OppCtrl | ReplaceWith$ DmgPlus2\n" +
        "SVar:DmgPlus2:DB$ ReplaceEffect | VarName$ DamageAmount | VarValue$ X\nSVar:X:ReplaceCount$DamageAmount/Plus.2\n",
        "Name:Triple\nManaCost:0\nTypes:Enchantment\n" +
        "R:Event$ DamageDone | ActiveZones$ Battlefield | ValidSource$ Card.YouCtrl,Emblem.YouCtrl | ValidTarget$ Permanent,Player | ReplaceWith$ Dmg3\n" +
        "SVar:Dmg3:DB$ ReplaceEffect | VarName$ DamageAmount | VarValue$ X\nSVar:X:ReplaceCount$DamageAmount/Thrice\n",
        "Name:Season\nManaCost:0\nTypes:Enchantment\n" +
        "R:Event$ CreateToken | ActiveZones$ Battlefield | ValidToken$ Card.YouCtrl | ReplaceWith$ DoubleToken | EffectOnly$ True\n" +
        "SVar:DoubleToken:DB$ ReplaceToken | Type$ Amount\n" +
        "R:Event$ AddCounter | ActiveZones$ Battlefield | ValidCard$ Permanent.YouCtrl+inZoneBattlefield | EffectOnly$ True | ReplaceWith$ DoubleCounters\n" +
        "SVar:DoubleCounters:DB$ ReplaceCounter | Amount$ Y\nSVar:Y:ReplaceCount$CounterNum/Twice\n",
        "Name:Scales\nManaCost:0\nTypes:Enchantment\n" +
        "R:Event$ AddCounter | ActiveZones$ Battlefield | ValidCard$ Creature.YouCtrl+inZoneBattlefield | ValidCounterType$ P1P1 | ReplaceWith$ AddOne\n" +
        "SVar:AddOne:DB$ ReplaceCounter | ValidCounterType$ P1P1 | ChooseCounter$ True | Amount$ X\nSVar:X:ReplaceCount$CounterNum/Plus.1\n",
        "Name:Feed\nManaCost:0\nTypes:Sorcery\nA:SP$ PutCounter | ValidTgts$ Creature | CounterType$ P1P1 | CounterNum$ 1\n",
        "Name:Peace\nManaCost:0\nTypes:Enchantment\n" +
        "R:Event$ Moved | ActiveZones$ Battlefield | Destination$ Graveyard | ValidCard$ Card | ReplaceWith$ Exile\n" +
        "SVar:Exile:DB$ ChangeZone | Hidden$ True | Origin$ All | Destination$ Exile | Defined$ ReplacedCard\n",
        "Name:Sure\nManaCost:0\nTypes:Sorcery\nR:Event$ Counter | ValidCard$ Card.Self | ValidSA$ Spell | Layer$ CantHappen\nA:SP$ Draw | NumCards$ 1\n",
        "Name:Walker\nManaCost:0\nTypes:Legendary Planeswalker Test\nLoyalty:3\n" +
        "A:AB$ GainLife | Cost$ AddCounter<1/LOYALTY> | Planeswalker$ True | Defined$ You | LifeAmount$ 2\n" +
        "A:AB$ Draw | Cost$ SubCounter<3/LOYALTY> | Planeswalker$ True | Defined$ You | NumCards$ 1\n",
        "Name:Trip\nManaCost:0\nTypes:Instant\nA:SP$ GainLife | Defined$ You | LifeAmount$ 1 | SubAbility$ DelTrig\n" +
        "SVar:DelTrig:DB$ DelayedTrigger | Mode$ Phase | Phase$ Upkeep | ValidPlayer$ You | NextTurn$ True | Execute$ DrawSlowtrip | TriggerDescription$ Draw a card at the beginning of the next turn's upkeep.\n" +
        "SVar:DrawSlowtrip:DB$ Draw | NumCards$ 1 | Defined$ You\n",
        "Name:Fling\nManaCost:0\nTypes:Sorcery\nA:SP$ Pump | ValidTgts$ Creature | NumAtt$ +2 | SubAbility$ DelTrig\n" +
        "SVar:DelTrig:DB$ DelayedTrigger | Mode$ Phase | Phase$ End of Turn | RememberObjects$ Targeted | Execute$ TrigBye\n" +
        "SVar:TrigBye:DB$ ChangeZone | Defined$ DelayTriggerRemembered | Origin$ Battlefield | Destination$ Exile\n",
        "Name:Toll\nManaCost:0\nTypes:Sorcery\nA:SP$ RepeatEach | RepeatPlayers$ Player.Opponent | RepeatSubAbility$ DBLose\n" +
        "SVar:DBLose:DB$ LoseLife | Defined$ Remembered | LifeAmount$ 2\n",
        "Name:Census\nManaCost:0\nTypes:Sorcery\nA:SP$ RepeatEach | RepeatCards$ Creature.YouCtrl | RepeatSubAbility$ DBGain\n" +
        "SVar:DBGain:DB$ GainLife | Defined$ You | LifeAmount$ X\nSVar:X:Remembered$CardPower\n",
        "Name:Twin\nManaCost:0\nTypes:Instant\nA:SP$ CopySpellAbility | ValidTgts$ Instant,Sorcery | TargetType$ Spell | MayChooseTarget$ True\n",
        "Name:Echo\nManaCost:0\nTypes:Enchantment\n" +
        "T:Mode$ SpellCast | ValidCard$ Instant,Sorcery | ValidActivatingPlayer$ You | TriggerZones$ Battlefield | Execute$ TrigCopy\n" +
        "SVar:TrigCopy:DB$ CopySpellAbility | Defined$ TriggeredSpellAbility | MayChooseTarget$ True\n",
        "Name:Desire\nManaCost:0\nTypes:Sorcery\n" +
        "A:SP$ Play | Valid$ Card.nonLand+YouOwn | ValidZone$ Hand | ValidSA$ Spell | WithoutManaCost$ True | Amount$ All | Controller$ You | Optional$ True\n",
        "Name:Grab\nManaCost:0\nTypes:Sorcery\nA:SP$ GainControl | ValidTgts$ Creature | LoseControl$ EOT | Untap$ True | AddKWs$ Haste\n",
        "Name:Altar\nManaCost:0\nTypes:Artifact\nA:AB$ GainLife | Cost$ Sac<1/Creature> | Defined$ You | LifeAmount$ 1\n" +
        "T:Mode$ Sacrificed | ValidCard$ Creature | ValidPlayer$ You | TriggerZones$ Battlefield | Execute$ TrigDraw\n" +
        "SVar:TrigDraw:DB$ Draw | Defined$ You | NumCards$ 1\n",
        "Name:Grief\nManaCost:0\nTypes:Enchantment\n" +
        "T:Mode$ ChangesZoneAll | ValidCards$ Creature.YouCtrl | Origin$ Battlefield | Destination$ Graveyard | TriggerZones$ Battlefield | Execute$ TrigGain | ActivationLimit$ 1\n" +
        "SVar:TrigGain:DB$ GainLife | Defined$ You | LifeAmount$ X\nSVar:X:TriggerCount$Amount\n",
        "Name:Banner\nManaCost:0\nTypes:Artifact\nK:ETBReplacement:Other:ChooseCT\n" +
        "SVar:ChooseCT:DB$ ChooseType | Defined$ You | Type$ Creature\n" +
        "S:Mode$ Continuous | Affected$ Creature.ChosenType+YouCtrl | AddPower$ 1\n",
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
    public void ImpulseDrawLetsYouPlayTheExiledCardThisTurnOnly()
    {
        var g = Setup(new[] { "Impulse" }, library: new[] { "Plain", "Filler" });
        g.CastSpell(g.State[InHand(g, "Impulse")], 0);
        ResolveAll(g);
        var plain = g.State.Cards.First(c => c.Name == "Plain");
        Assert.Equal(Zone.Exile, plain.Zone);
        Assert.Contains(g.LegalActions(Game.Me), a => a is GameAction.PlayLand pl && pl.CardId == plain.Id);
        g.ExpireEffects("cleanup");
        Assert.DoesNotContain(g.LegalActions(Game.Me), a => a is GameAction.PlayLand);
    }

    [Fact]
    public void ChosenCreatureTypeGetsTheBonus()
    {
        var g = Setup(new[] { "Banner" }, battlefield: new[] { "Bear", "Sneak" });
        g.CastSpell(g.State[InHand(g, "Banner")], -1);
        ResolveAll(g);
        Assert.Equal("Bear", OnBattlefield(g, "Banner").ChosenType);
        Assert.Equal(3, OnBattlefield(g, "Bear").Power);
        Assert.Equal(1, OnBattlefield(g, "Sneak").Power);
    }

    [Fact]
    public void CloneEntersAsACopyWithItsExceptions()
    {
        var g = Setup(new[] { "Mimic" }, battlefield: new[] { "Bog", "Bog", "Sneak" });
        g.CastSpell(g.State[InHand(g, "Mimic")], -1);
        ResolveAll(g);
        var copy = g.State.BattlefieldCards.Single(c => c.Name == "Sneak" && c.Types.Is(CardType.Artifact));
        Assert.Equal("Mimic", copy.Def.Name);
        Assert.Equal(1, copy.Power);
        Assert.True(copy.HasKeyword("Haste"));
        Assert.Single(copy.CurrentFace.Triggers);
    }

    [Fact]
    public void CloneWithNothingToCopyIsAZeroZeroAndDies()
    {
        var g = Setup(new[] { "Mimic" }, battlefield: new[] { "Bog", "Bog" });
        g.CastSpell(g.State[InHand(g, "Mimic")], -1);
        ResolveAll(g);
        g.CheckStateBasedActions();
        Assert.Equal(Zone.Graveyard, g.State.Cards.First(c => c.Def.Name == "Mimic").Zone);
    }

    [Fact]
    public void TokenCopyHasHasteAndIsSacrificedAtEndOfTurn()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Mirror", "Bear" });
        g.ActivateAbility(OnBattlefield(g, "Mirror"), 0);
        ResolveAll(g);
        var token = g.State.BattlefieldCards.Single(c => c.Name == "Bear" && c.IsToken);
        Assert.Equal(2, token.Power);
        Assert.True(token.HasKeyword("Haste"));
        Assert.False(g.IsSick(token));
        Assert.Contains((token.Id, "Sacrifice"), g.State.ThisTurn.AtEndStep);
    }

    [Fact]
    public void BecomesACopyUntilEndOfTurn()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Glass", "Bear" });
        var glass = OnBattlefield(g, "Glass");
        g.ActivateAbility(glass, 0);
        ResolveAll(g);
        Assert.Equal("Bear", glass.Name);
        Assert.True(glass.Types.Is(CardType.Creature));
        Assert.Equal(2, glass.Toughness);
        g.ExpireEffects("cleanup");
        Assert.Equal("Glass", glass.Name);
    }

    [Fact]
    public void ManlandBecomesACreatureUntilEndOfTurnAndAttacks()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Colonnade" }, library: new[] { "Filler", "Filler" });
        var land = OnBattlefield(g, "Colonnade");
        g.ActivateAbility(land, 1);
        ResolveAll(g);
        g.CheckStateBasedActions();
        Assert.True(land.Types.Is(CardType.Creature) && land.Types.Is(CardType.Land));
        Assert.True(land.Types.HasSubtype("Elemental"));
        Assert.Equal(4, land.Power);
        Assert.True(land.HasKeyword("Flying"));
        Assert.Equal(ManaColor.W.Mask(), land.Colors);
        g.ResumeTurn();
        Assert.Equal(36, g.State.Players.Where(p => p.Index != Game.Me).Min(p => p.Life));
        Assert.False(land.Types.Is(CardType.Creature));
    }

    [Fact]
    public void StaticGrantsAManaAbility()
    {
        var g = Setup(new[] { "Sprout" }, battlefield: new[] { "Lantern", "Bog" });
        g.CheckStateBasedActions();
        Assert.True(g.CanCast(g.State[InHand(g, "Sprout")], 0));
    }

    [Fact]
    public void GrantedTriggerUsesTheGrantersSVars()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Rally", "Bear" }, library: new[] { "Filler", "Filler" });
        g.ResumeTurn();
        Assert.Equal(43, g.State.Players[Game.Me].Life);
    }

    [Fact]
    public void AddTypeAffectsWhatItMatchedBeforeTheTypeChanged()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "March", "Vault" });
        g.CheckStateBasedActions();
        var vault = OnBattlefield(g, "Vault");
        Assert.True(vault.Types.Is(CardType.Creature) && vault.Types.HasSubtype("Golem"));
        Assert.Equal(2, vault.Power);
        Assert.Equal(2, vault.Toughness);
    }

    [Fact]
    public void ChangelingIsEveryCreatureType()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Totem", "Shifter" });
        g.CheckStateBasedActions();
        Assert.Equal(2, OnBattlefield(g, "Shifter").Power);
    }

    [Fact]
    public void SagaTriggersEachChapterThenIsSacrificed()
    {
        var g = Setup(new[] { "Tale" }, library: new[] { "Filler", "Filler", "Filler" });
        g.CastSpell(g.State[InHand(g, "Tale")], -1);
        ResolveAll(g);
        var tale = OnBattlefield(g, "Tale");
        Assert.Equal(42, g.State.Players[Game.Me].Life);
        Assert.Equal(1, tale.CounterCount("LORE"));
        g.ResumeTurn();
        g.RunTurn();   // chapter II after the next draw step
        Assert.Equal(Zone.Graveyard, tale.Zone);
        Assert.Equal(2, g.State.Players[Game.Me].Hand.Count);   // the draw step and chapter II
    }

    [Fact]
    public void ProliferateAddsOneOfEachKind()
    {
        var g = Setup(new[] { "Spread" }, battlefield: new[] { "Bear" });
        var bear = OnBattlefield(g, "Bear");
        Game.AddCounters(bear, "P1P1", 1);
        g.State.Players[2].Poison = 1;
        g.CastSpell(g.State[InHand(g, "Spread")], 0);
        ResolveAll(g);
        Assert.Equal(2, bear.CounterCount("P1P1"));
        Assert.Equal(2, g.State.Players[2].Poison);
        Assert.Equal(0, g.State.Players[1].Poison);
    }

    [Fact]
    public void RestrictedManaOnlyPaysForWhatItAllows()
    {
        var g = Setup(new[] { "Bear", "Sig" }, battlefield: new[] { "Lotus" });
        Assert.True(g.CanCast(g.State[InHand(g, "Bear")], -1));
        Assert.False(g.CanCast(g.State[InHand(g, "Sig")], -1));
        Assert.DoesNotContain(g.LegalActions(Game.Me), a => a is GameAction.Activate);
        g.CastSpell(g.State[InHand(g, "Bear")], -1);
        Assert.True(OnBattlefield(g, "Lotus").Tapped);
    }

    [Fact]
    public void ManaAbilityWithAConditionNeedsItToHold()
    {
        var g = Setup(new[] { "Sprout" }, battlefield: new[] { "Grove" });
        Assert.False(g.CanCast(g.State[InHand(g, "Sprout")], 0));
        g = Setup(new[] { "Sprout" }, battlefield: new[] { "Grove", "Bear" });
        Assert.True(g.CanCast(g.State[InHand(g, "Sprout")], 0));
    }

    [Fact]
    public void ModalDoubleFacedCardPlaysEitherFace()
    {
        var g = Setup(new[] { "Spell Side" }, battlefield: new[] { "Bog" });
        var card = g.State[InHand(g, "Spell Side")];
        var legal = g.LegalActions(Game.Me);
        Assert.Contains(legal, a => a is GameAction.Cast { Face: 0 } c && c.CardId == card.Id);
        var play = legal.OfType<GameAction.PlayLand>().Single(a => a.CardId == card.Id);
        Assert.Equal(1, play.Face);
        g.Perform(play);
        Assert.Equal("Land Side", card.Name);
        Assert.True(card.Types.Is(CardType.Land));
    }

    [Fact]
    public void ModalDoubleFacedSpellGoesToTheGraveyardAsItsFrontFace()
    {
        var g = Setup(new[] { "Spell Side" }, battlefield: new[] { "Bog" });
        var card = g.State[InHand(g, "Spell Side")];
        g.Perform(g.LegalActions(Game.Me).OfType<GameAction.Cast>().Single());
        ResolveAll(g);
        Assert.Equal(43, g.State.Players[Game.Me].Life);
        Assert.Equal(Zone.Graveyard, card.Zone);
        Assert.Equal(0, card.Face);
    }

    [Fact]
    public void DamageReplacementsApplyInTheBestOrder()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Thane", "Triple", "Bear" }, library: new[] { "Filler", "Filler" });
        g.ResumeTurn();
        Assert.Equal(40 - (2 + 2) * 3, g.State.Players.Where(p => p.Index != Game.Me).Min(p => p.Life));
    }

    [Fact]
    public void TokenDoublingCreatesTwiceAsMany()
    {
        var g = Setup(new[] { "Muster" }, battlefield: new[] { "Season", "Bog" });
        g.CastSpell(g.State[InHand(g, "Muster")], 0);
        ResolveAll(g);
        Assert.Equal(4, g.State.BattlefieldCards.Count(c => c.IsToken));
    }

    [Fact]
    public void CounterReplacementsStack()
    {
        var g = Setup(new[] { "Feed" }, battlefield: new[] { "Season", "Scales", "Bear" });
        g.CastSpell(g.State[InHand(g, "Feed")], 0);
        ResolveAll(g);
        Assert.Equal((1 + 1) * 2, OnBattlefield(g, "Bear").CounterCount("P1P1"));
    }

    [Fact]
    public void GraveyardBoundCardsAreExiledInstead()
    {
        var g = Setup(new[] { "Spread" }, battlefield: new[] { "Peace" });
        g.CastSpell(g.State[InHand(g, "Spread")], 0);
        ResolveAll(g);
        Assert.Equal(Zone.Exile, g.State.Cards.First(c => c.Name == "Spread").Zone);
    }

    [Fact]
    public void CantBeCounteredIsSupportedAndDoesNothingInGoldfish()
    {
        var g = Setup(new[] { "Sure" }, library: new[] { "Filler" });
        Assert.False(g.State[InHand(g, "Sure")].IsBlank);
    }

    [Fact]
    public void PlaneswalkerEntersWithLoyaltyAndActivatesOncePerTurn()
    {
        var g = Setup(new[] { "Walker" });
        g.CastSpell(g.State[InHand(g, "Walker")], -1);
        ResolveAll(g);
        var walker = OnBattlefield(g, "Walker");
        Assert.Equal(3, walker.CounterCount("LOYALTY"));
        Assert.Contains(g.LegalActions(Game.Me), a => a is GameAction.Activate act && act.CardId == walker.Id);
        g.ActivateAbility(walker, 0);
        ResolveAll(g);
        Assert.Equal(4, walker.CounterCount("LOYALTY"));
        Assert.Equal(42, g.State.Players[Game.Me].Life);
        Assert.DoesNotContain(g.LegalActions(Game.Me), a => a is GameAction.Activate act && act.CardId == walker.Id);
    }

    [Fact]
    public void PlaneswalkerWithNoLoyaltyIsPutIntoTheGraveyard()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Walker" }, library: new[] { "Filler" });
        var walker = OnBattlefield(g, "Walker");
        Game.AddCounters(walker, "LOYALTY", 3);
        g.ActivateAbility(walker, 1);
        ResolveAll(g);
        g.CheckStateBasedActions();
        Assert.Equal(Zone.Graveyard, walker.Zone);
        Assert.Single(g.State.Players[Game.Me].Hand);
    }

    [Fact]
    public void SlowtripDrawsAtTheNextUpkeep()
    {
        var g = Setup(new[] { "Trip" }, library: new[] { "Filler", "Filler", "Filler" });
        g.CastSpell(g.State[InHand(g, "Trip")], 0);
        ResolveAll(g);
        Assert.Empty(g.State.Players[Game.Me].Hand);
        Assert.Single(g.State.Delayed);
        g.ResumeTurn();
        g.RunTurn();
        Assert.Equal(2, g.State.Players[Game.Me].Hand.Count);   // the slowtrip and the draw step
        Assert.Empty(g.State.Delayed);
    }

    [Fact]
    public void DelayedTriggerRemembersItsTarget()
    {
        var g = Setup(new[] { "Fling" }, battlefield: new[] { "Bear" }, library: new[] { "Filler", "Filler" });
        g.CastSpell(g.State[InHand(g, "Fling")], 0);
        ResolveAll(g);
        var bear = g.State.Cards.First(c => c.Name == "Bear");
        Assert.Equal(Zone.Battlefield, bear.Zone);
        g.ResumeTurn();
        Assert.Equal(Zone.Exile, bear.Zone);
    }

    [Fact]
    public void RepeatEachOpponentLosesLife()
    {
        var g = Setup(new[] { "Toll" });
        g.CastSpell(g.State[InHand(g, "Toll")], 0);
        ResolveAll(g);
        Assert.All(g.State.Players.Where(p => p.Index != Game.Me), p => Assert.Equal(38, p.Life));
        Assert.Equal(40, g.State.Players[Game.Me].Life);
    }

    [Fact]
    public void RepeatEachCardRemembersItInTurn()
    {
        var g = Setup(new[] { "Census" }, battlefield: new[] { "Bear", "Sneak" });
        g.CastSpell(g.State[InHand(g, "Census")], 0);
        ResolveAll(g);
        Assert.Equal(43, g.State.Players[Game.Me].Life);
    }

    [Fact]
    public void CopyTargetSpell()
    {
        var g = Setup(new[] { "Toll", "Twin" });
        g.CastSpell(g.State[InHand(g, "Toll")], 0);
        g.CastSpell(g.State[InHand(g, "Twin")], 0);
        ResolveAll(g);
        Assert.All(g.State.Players.Where(p => p.Index != Game.Me), p => Assert.Equal(36, p.Life));
    }

    [Fact]
    public void TriggerCopiesTheSpellThatWasCast()
    {
        var g = Setup(new[] { "Toll" }, battlefield: new[] { "Echo" });
        g.CastSpell(g.State[InHand(g, "Toll")], 0);
        ResolveAll(g);
        Assert.All(g.State.Players.Where(p => p.Index != Game.Me), p => Assert.Equal(36, p.Life));
        Assert.Equal(Zone.Graveyard, g.State.Cards.First(c => c.Name == "Toll").Zone);
    }

    [Fact]
    public void PlayCastsCardsWithoutPayingTheirManaCost()
    {
        var g = Setup(new[] { "Desire", "Filler" }, library: new[] { "Bear" }, decider: new TestDecider { Choose = (game, d) => d.Kind == DecisionKind.Card ? 0 : null });
        g.CastSpell(g.State[InHand(g, "Desire")], 0);
        ResolveAll(g);
        Assert.Equal(Zone.Graveyard, g.State.Cards.First(c => c.Name == "Filler").Zone);
        Assert.Equal("Bear", g.State[g.State.Players[Game.Me].Hand.Single()].Name);
    }

    [Fact]
    public void GainControlUntilEndOfTurn()
    {
        var g = Setup(new[] { "Grab" }, battlefield: new[] { "Bear" }, library: new[] { "Filler", "Filler" });
        var bear = OnBattlefield(g, "Bear");
        bear.Controller = 2;
        bear.Tapped = true;
        g.CastSpell(g.State[InHand(g, "Grab")], 0);
        ResolveAll(g);
        Assert.Equal(Game.Me, bear.Controller);
        Assert.False(bear.Tapped);
        Assert.False(g.IsSick(bear));
        g.ResumeTurn();
        Assert.Equal(2, bear.Controller);
    }

    [Fact]
    public void SacrificeTriggersFire()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Altar", "Bear" }, library: new[] { "Filler" });
        g.ActivateAbility(OnBattlefield(g, "Altar"), 0);
        ResolveAll(g);
        Assert.Equal(41, g.State.Players[Game.Me].Life);
        Assert.Single(g.State.Players[Game.Me].Hand);
    }

    [Fact]
    public void OneOrMoreTriggerFiresOnceForCardsMovingTogether()
    {
        var g = Setup(new[] { "Wrath" }, battlefield: new[] { "Grief", "Bear", "Sneak", "Bog", "Bog" });
        g.CastSpell(g.State[InHand(g, "Wrath")], 0);
        ResolveAll(g);
        Assert.Equal(42, g.State.Players[Game.Me].Life);
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
