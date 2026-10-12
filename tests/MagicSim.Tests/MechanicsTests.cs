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
        "Name:Cart\nManaCost:0\nTypes:Artifact Vehicle\nPT:4/4\nK:Crew:2\n",
        "Name:Elf\nManaCost:G\nTypes:Creature Elf\nPT:1/1\n",
        "Name:Call\nManaCost:2 G\nTypes:Sorcery\nK:Convoke\nA:SP$ Draw | NumCards$ 1 | Defined$ You\n",
        "Name:Blight\nManaCost:0\nTypes:Creature Phyrexian\nPT:2/2\nK:Infect\nK:Haste\n",
        "Name:Brute\nManaCost:0\nTypes:Creature Ogre\nPT:3/3\nA:AB$ Fight | Cost$ 0 | Defined$ Self | ValidTgts$ Creature.Other\n",
        "Name:Fork\nManaCost:0\nTypes:Sorcery\n" +
        "A:SP$ Branch | BranchConditionSVar$ X | BranchConditionSVarCompare$ GE2 | TrueSubAbility$ DBA | FalseSubAbility$ DBB\n" +
        "SVar:X:Count$CardsInYourHand\nSVar:DBA:DB$ GainLife | Defined$ You | LifeAmount$ 5\nSVar:DBB:DB$ GainLife | Defined$ You | LifeAmount$ 1\n",
        "Name:Tax\nManaCost:0\nTypes:Creature Human\nPT:2/1\nS:Mode$ RaiseCost | ValidCard$ Card.nonCreature | Type$ Spell | Amount$ 1\n",
        "Name:Quick\nManaCost:0\nTypes:Enchantment\nS:Mode$ CastWithFlash | ValidCard$ Card | ValidSA$ Spell | Caster$ You\n",
        "Name:Harmony\nManaCost:0\nTypes:Artifact\n" +
        "S:Mode$ Panharmonicon | ValidMode$ ChangesZone,ChangesZoneAll | ValidCard$ Permanent.YouCtrl | ValidCause$ Artifact,Creature | Destination$ Battlefield\n",
        "Name:Greeter\nManaCost:0\nTypes:Creature Human\nPT:1/1\n" +
        "T:Mode$ ChangesZone | Destination$ Battlefield | ValidCard$ Card.Self | Execute$ TrigGain\nSVar:TrigGain:DB$ GainLife | Defined$ You | LifeAmount$ 1\n",
        "Name:Raid\nManaCost:0\nTypes:Enchantment\n" +
        "T:Mode$ DamageDoneOnce | ValidSource$ Creature.YouCtrl | ValidTarget$ Player | CombatDamage$ True | TriggerZones$ Battlefield | Execute$ TrigGain\n" +
        "SVar:TrigGain:DB$ GainLife | Defined$ You | LifeAmount$ X\nSVar:X:TriggerCount$DamageAmount\n",
        "Name:Day\nManaCost:0\nTypes:Creature Human\nPT:1/1\nA:AB$ SetState | Cost$ 0 | Defined$ Self | Mode$ Transform\nAlternateMode:DoubleFaced\n" +
        "ALTERNATE\nName:Night\nManaCost:no cost\nTypes:Creature Werewolf\nPT:5/5\n",
        "Name:Pit\nManaCost:0\nTypes:Artifact\nA:AB$ GainLife | Cost$ Sac<1/Creature> | Defined$ You | LifeAmount$ X\nSVar:X:Sacrificed$CardPower\n",
        "Name:Monk\nManaCost:0\nTypes:Creature Monk\nPT:1/1\nK:Prowess\n",
        "Name:Stinger\nManaCost:0\nTypes:Creature Insect\nPT:1/1\nK:Haste\nK:Toxic:2\n",
        "Name:Frog\nManaCost:4\nTypes:Artifact Creature Frog\nPT:2/2\nK:Affinity:Artifact\n",
        "Name:Twice\nManaCost:0\nTypes:Enchantment\n" +
        "T:Mode$ SpellCast | ValidActivatingPlayer$ You | ActivatorThisTurnCast$ EQ2 | TriggerZones$ Battlefield | Execute$ TrigGain\n" +
        "SVar:TrigGain:DB$ GainLife | Defined$ You | LifeAmount$ 5\n",
        "Name:Bookworm\nManaCost:0\nTypes:Enchantment\n" +
        "T:Mode$ Drawn | ValidCard$ Card.YouCtrl | Number$ 2 | TriggerZones$ Battlefield | Execute$ TrigGain\nSVar:TrigGain:DB$ GainLife | Defined$ You | LifeAmount$ 3\n",
        "Name:Tracker\nManaCost:0\nTypes:Enchantment\n" +
        "T:Mode$ CounterAddedOnce | ValidCard$ Creature.YouCtrl | CounterType$ P1P1 | TriggerZones$ Battlefield | Execute$ TrigGain\n" +
        "SVar:TrigGain:DB$ GainLife | Defined$ You | LifeAmount$ 1\n",
        "Name:Measure\nManaCost:0\nTypes:Sorcery\nA:SP$ GainLife | Defined$ You | LifeAmount$ X\nSVar:X:Count$Valid Creature.YouCtrl$GreatestCardPower\n",
        "Name:Raise\nManaCost:0\nTypes:Sorcery\n" +
        "A:SP$ ChangeZone | Origin$ Graveyard | Destination$ Battlefield | ValidTgts$ Creature | TgtZone$ Graveyard | GainControl$ True | WithCountersType$ P1P1 | WithCountersAmount$ 2\n",
        "Name:Wake\nManaCost:0\nTypes:Instant\nA:SP$ UntapAll | ValidCards$ Land.YouCtrl\n",
        "Name:Drain\nManaCost:0\nTypes:Instant\nA:SP$ RemoveCounter | ValidTgts$ Creature | CounterType$ P1P1 | CounterNum$ All\n",
        "Name:Bounce\nManaCost:0\nTypes:Instant\nA:SP$ ChangeZone | Origin$ Stack | Destination$ Hand | ValidTgts$ Card | TgtZone$ Stack | TargetType$ Spell\n",
        "Name:Lesson\nManaCost:0\nTypes:Enchantment Class\n" +
        "K:Class:2:0:AddTrigger$ LvlTrig\nSVar:LvlTrig:Mode$ Phase | Phase$ End of Turn | ValidPlayer$ You | TriggerZones$ Battlefield | Execute$ TrigGain\n" +
        "SVar:TrigGain:DB$ GainLife | Defined$ You | LifeAmount$ 2\n" +
        "K:Class:3:0:AddStaticAbility$ LvlAnthem\nSVar:LvlAnthem:Mode$ Continuous | Affected$ Creature.YouCtrl | AddPower$ 1\n",
        "Name:Reflect\nManaCost:0\nTypes:Enchantment\n" +
        "T:Mode$ TapsForMana | ValidCard$ Land | Activator$ You | Execute$ TrigMana | TriggerZones$ Battlefield | Static$ True\nSVar:TrigMana:DB$ Mana | Produced$ B\n",
        "Name:Legion\nManaCost:0\nTypes:Creature Soldier\nPT:2/2\nK:Haste\nK:Myriad\n",
        "Name:Dice\nManaCost:0\nTypes:Sorcery\nA:SP$ RollDice | Sides$ 20 | ResultSubAbilities$ 1-9:DBLow,10-20:DBHigh\n" +
        "SVar:DBLow:DB$ GainLife | Defined$ You | LifeAmount$ 1\nSVar:DBHigh:DB$ GainLife | Defined$ You | LifeAmount$ 2\n",
        "Name:Prism\nManaCost:0\nTypes:Artifact\nK:ETBReplacement:Other:ChooseC\nSVar:ChooseC:DB$ ChooseColor | Defined$ You\nA:AB$ Mana | Cost$ T | Produced$ Chosen\n",
        "Name:Scatter\nManaCost:X\nTypes:Sorcery\nA:SP$ Pump | ValidTgts$ Creature | TargetMin$ 0 | TargetMax$ X | NumAtt$ +1\nSVar:X:Count$xPaid\n",
        "Name:Vision\nManaCost:no cost\nTypes:Sorcery\nK:Suspend:2:0\nA:SP$ Draw | NumCards$ 3 | Defined$ You\n",
        "Name:Elemental\nManaCost:9\nTypes:Creature Elemental\nPT:3/3\nK:Evoke:0\n" +
        "T:Mode$ ChangesZone | Destination$ Battlefield | ValidCard$ Card.Self | Execute$ TrigGain\nSVar:TrigGain:DB$ GainLife | Defined$ You | LifeAmount$ 3\n",
        "Name:Seeker\nManaCost:9\nTypes:Creature Beast\nPT:1/1\nK:TypeCycling:Basic:0\n",
        "Name:Leyline\nManaCost:4\nTypes:Enchantment\nK:MayEffectFromOpeningHand:FromHand\n" +
        "SVar:FromHand:DB$ ChangeZone | Defined$ Self | Origin$ Hand | Destination$ Battlefield\n",
        "Name:Reflex\nManaCost:0\nTypes:Sorcery\nA:SP$ GainLife | Defined$ You | LifeAmount$ 1 | SubAbility$ DBRef\n" +
        "SVar:DBRef:DB$ ImmediateTrigger | Cost$ PayLife<3> | Execute$ TrigDraw\nSVar:TrigDraw:DB$ Draw | Defined$ You | NumCards$ 2\n",
        "Name:Chains\nManaCost:0\nTypes:Enchantment Aura\nK:Enchant:Creature\nS:Mode$ CantAttack | ValidCard$ Creature.EnchantedBy\n",
        "Name:Knight\nManaCost:1\nTypes:Creature Knight\nPT:2/2\nAlternateMode:Adventure\n" +
        "ALTERNATE\nName:Quest\nManaCost:0\nTypes:Instant Adventure\nA:SP$ GainLife | Defined$ You | LifeAmount$ 2\n",
        "Name:Ring\nManaCost:0\nTypes:Enchantment\n" +
        "T:Mode$ ChangesZone | Destination$ Battlefield | ValidCard$ Card.Self | Execute$ TrigExile\n" +
        "SVar:TrigExile:DB$ ChangeZone | ValidTgts$ Creature | Origin$ Battlefield | Destination$ Exile | Duration$ UntilHostLeavesPlay\n",
        "Name:Maniac\nManaCost:0\nTypes:Creature Human\nPT:2/2\n" +
        "R:Event$ Draw | ActiveZones$ Battlefield | ValidPlayer$ You | IsPresent$ Card.YouOwn | PresentZone$ Library | PresentCompare$ EQ0 | ReplaceWith$ Win\n" +
        "SVar:Win:DB$ WinsGame | Defined$ You\n",
        "Name:Horde\nManaCost:0\nTypes:Sorcery\nA:SP$ Amass | Type$ Orc | Num$ 2\n",
        "Name:Raider\nManaCost:0\nTypes:Creature Human\nPT:2/2\nK:Haste\nK:Mobilize:2\n",
        "Name:Anger\nManaCost:3\nTypes:Creature Incarnation\nPT:2/2\nS:Mode$ Continuous | Affected$ Creature.YouCtrl | AddKeyword$ Haste | EffectZone$ Graveyard\n",
        "Name:Humble\nManaCost:0\nTypes:Enchantment\nS:Mode$ Continuous | Affected$ Artifact.Creature | RemoveType$ Creature\n",
        "Name:Bloom\nManaCost:3\nTypes:Sorcery\nK:Cascade\nA:SP$ Draw | NumCards$ 1 | Defined$ You\n",
        "Name:Scout\nManaCost:0\nTypes:Creature Scout\nPT:1/1\n" +
        "T:Mode$ ChangesZone | Destination$ Battlefield | ValidCard$ Card.Self | Execute$ TrigExplore\nSVar:TrigExplore:DB$ Explore\n",
        "Name:Venom\nManaCost:0\nTypes:Sorcery\nA:SP$ Poison | Defined$ Player.Opponent | Num$ 3\n",
        "Name:Doubler\nManaCost:0\nTypes:Instant\nA:SP$ Pump | ValidTgts$ Creature | NumAtt$ Double\n",
        "Name:Ghost\nManaCost:9\nTypes:Creature Spirit\nPT:1/1\nA:AB$ GainLife | Cost$ 0 Exile<1/CARDNAME> | ActivationZone$ Graveyard | Defined$ You | LifeAmount$ 2\n",
        "Name:Kitchen\nManaCost:0\nTypes:Creature Goblin\nPT:2/2\nK:Persist\n",
        "Name:Ninja\nManaCost:9\nTypes:Creature Ninja\nPT:3/3\nK:Ninjutsu:0\n",
        "Name:Visitation\nManaCost:0\nTypes:Enchantment\n" +
        "R:Event$ CreateToken | ActiveZones$ Battlefield | ValidToken$ Creature.YouCtrl | ReplaceWith$ TokenReplace\n" +
        "SVar:TokenReplace:DB$ ReplaceToken | Type$ ReplaceToken | TokenScript$ r_1_1_warrior\n",
        "Name:Chatter\nManaCost:0\nTypes:Creature Squirrel\nPT:3/3\n" +
        "R:Event$ CreateToken | ActiveZones$ Battlefield | ValidToken$ Card.YouCtrl | ReplaceWith$ DBReplace\n" +
        "SVar:DBReplace:DB$ ReplaceToken | Type$ AddToken | Amount$ 1 | TokenScript$ c_a_treasure_sac\n",
        "Name:Find\nManaCost:0\nTypes:Sorcery\nA:SP$ Discover | Num$ 2\n",
        "Name:Pick\nManaCost:0\nTypes:Sorcery\nA:SP$ GenericChoice | Defined$ You | Choices$ DBA,DBB\n" +
        "SVar:DBA:DB$ GainLife | Defined$ You | LifeAmount$ 1 | SpellDescription$ A\nSVar:DBB:DB$ GainLife | Defined$ You | LifeAmount$ 5 | SpellDescription$ B\n",
        "Name:Cyc2\nManaCost:9\nTypes:Creature Beast\nPT:1/1\nK:Cycling:0\n" +
        "T:Mode$ Cycled | ValidCard$ Card.Self | Execute$ TrigGain\nSVar:TrigGain:DB$ GainLife | Defined$ You | LifeAmount$ 2\n",
        "Name:Bend\nManaCost:0\nTypes:Sorcery\nA:SP$ Earthbend | Num$ 2\n",
        "Name:Watcher\nManaCost:0\nTypes:Creature Human\nPT:2/2\nK:Vigilance\n",
        "Name:Assault\nManaCost:0\nTypes:Sorcery\nA:SP$ AddPhase | ExtraPhase$ Combat | FollowedBy$ Main2\n",
        "Name:Warp\nManaCost:0\nTypes:Sorcery\nA:SP$ AddTurn | Defined$ You | NumTurns$ 1\n",
        "Name:Banner\nManaCost:0\nTypes:Artifact\nK:ETBReplacement:Other:ChooseCT\n" +
        "SVar:ChooseCT:DB$ ChooseType | Defined$ You | Type$ Creature\n" +
        "S:Mode$ Continuous | Affected$ Creature.ChosenType+YouCtrl | AddPower$ 1\n",
    };

    private static readonly Dictionary<string, string> Tokens = new()
    {
        ["w_1_1_soldier"] = "Name:Soldier Token\nManaCost:no cost\nColors:white\nTypes:Creature Soldier\nPT:1/1\n",
        ["c_a_treasure_sac"] = "Name:Treasure Token\nManaCost:no cost\nTypes:Artifact Treasure\n" +
            "A:AB$ Mana | Cost$ T Sac<1/CARDNAME/this token> | Produced$ Any | Amount$ 1\n",
        ["b_0_0_orc_army"] = "Name:Orc Army Token\nManaCost:no cost\nColors:black\nTypes:Creature Orc Army\nPT:0/0\n",
        ["r_1_1_warrior"] = "Name:Warrior Token\nManaCost:no cost\nColors:red\nTypes:Creature Warrior\nPT:1/1\n",
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
    public void CrewTapsCreaturesToMakeTheVehicleACreature()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Cart", "Bear" });
        var cart = OnBattlefield(g, "Cart");
        g.ActivateAbility(cart, 0);
        ResolveAll(g);
        g.CheckStateBasedActions();
        Assert.True(cart.Types.Is(CardType.Creature));
        Assert.True(OnBattlefield(g, "Bear").Tapped);
    }

    [Fact]
    public void ConvokeTapsCreaturesToPay()
    {
        var g = Setup(new[] { "Call" }, battlefield: new[] { "Elf", "Bear", "Bog" }, library: new[] { "Filler" });
        var call = g.State[InHand(g, "Call")];
        Assert.True(g.CanCast(call, 0));
        g.CastSpell(call, 0);
        Assert.True(OnBattlefield(g, "Elf").Tapped);
    }

    [Fact]
    public void InfectGivesPoisonInsteadOfLifeLoss()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Blight" }, library: new[] { "Filler", "Filler" });
        g.ResumeTurn();
        Assert.Equal(2, g.State.Players.Max(p => p.Poison));
        Assert.All(g.State.Players, p => Assert.Equal(40, p.Life));
    }

    [Fact]
    public void FightDealsDamageBothWays()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Brute", "Bear" });
        g.ActivateAbility(OnBattlefield(g, "Brute"), 0);
        ResolveAll(g);
        g.CheckStateBasedActions();
        Assert.Equal(Zone.Graveyard, g.State.Cards.First(c => c.Name == "Bear").Zone);
        Assert.Equal(2, OnBattlefield(g, "Brute").Damage);
    }

    [Fact]
    public void BranchPicksTheMatchingSide()
    {
        var g = Setup(new[] { "Fork" });
        g.CastSpell(g.State[InHand(g, "Fork")], 0);
        ResolveAll(g);
        Assert.Equal(41, g.State.Players[Game.Me].Life);
    }

    [Fact]
    public void RaiseCostMakesNoncreatureSpellsCostMore()
    {
        var g = Setup(new[] { "Sig", "Bear" }, battlefield: new[] { "Tax", "Bog", "Bog" });
        g.CheckStateBasedActions();
        Assert.False(g.CanCast(g.State[InHand(g, "Sig")], -1));
        Assert.True(g.CanCast(g.State[InHand(g, "Bear")], -1));
    }

    [Fact]
    public void CastWithFlashAllowsCastingAtInstantSpeed()
    {
        var g = Setup(new[] { "Bear" }, battlefield: new[] { "Quick", "Bog", "Bog" });
        g.State.Step = Step.BeginCombat;
        Assert.Contains(g.LegalActions(Game.Me), a => a is GameAction.Cast);
    }

    [Fact]
    public void PanharmoniconDoublesEnterTriggers()
    {
        var g = Setup(new[] { "Greeter" }, battlefield: new[] { "Harmony" });
        g.CastSpell(g.State[InHand(g, "Greeter")], -1);
        ResolveAll(g);
        Assert.Equal(42, g.State.Players[Game.Me].Life);
    }

    [Fact]
    public void OneOrMoreCombatDamageTriggersOncePerPlayer()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Raid", "Bear", "Sneak" }, library: new[] { "Filler", "Filler", "Filler" });
        g.ResumeTurn();
        Assert.Equal(43, g.State.Players[Game.Me].Life);
    }

    [Fact]
    public void TransformTurnsTheCardOver()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Day" });
        var card = OnBattlefield(g, "Day");
        g.ActivateAbility(card, 0);
        ResolveAll(g);
        Assert.Equal("Night", card.Name);
        Assert.Equal(5, card.Power);
    }

    [Fact]
    public void SacrificedCreaturesPowerIsKnown()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Pit", "Bear" });
        g.ActivateAbility(OnBattlefield(g, "Pit"), 0);
        ResolveAll(g);
        Assert.Equal(42, g.State.Players[Game.Me].Life);
    }

    [Fact]
    public void ProwessPumpsOnNoncreatureSpells()
    {
        var g = Setup(new[] { "Toll" }, battlefield: new[] { "Monk" });
        g.CastSpell(g.State[InHand(g, "Toll")], 0);
        ResolveAll(g);
        Assert.Equal(2, OnBattlefield(g, "Monk").Power);
    }

    [Fact]
    public void ToxicAddsPoisonWithCombatDamage()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Stinger" }, library: new[] { "Filler", "Filler" });
        g.ResumeTurn();
        var hit = g.State.Players.Single(p => p.Poison > 0);
        Assert.Equal(2, hit.Poison);
        Assert.Equal(39, hit.Life);
    }

    [Fact]
    public void AffinityReducesTheCostForEachArtifact()
    {
        var g = Setup(new[] { "Frog" }, battlefield: new[] { "Mirror", "Glass", "Lantern", "Harmony" });
        Assert.True(g.CanCast(g.State[InHand(g, "Frog")], -1));
    }

    [Fact]
    public void SecondSpellEachTurnTriggers()
    {
        var g = Setup(new[] { "Spread", "Spread" }, battlefield: new[] { "Twice" });
        g.CastSpell(g.State[InHand(g, "Spread")], 0);
        ResolveAll(g);
        Assert.Equal(40, g.State.Players[Game.Me].Life);
        g.CastSpell(g.State[InHand(g, "Spread")], 0);
        ResolveAll(g);
        Assert.Equal(45, g.State.Players[Game.Me].Life);
    }

    [Fact]
    public void SecondCardDrawnEachTurnTriggers()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Bookworm" }, library: new[] { "Filler", "Filler", "Filler" });
        g.Draw(Game.Me, 3);
        ResolveAll(g);
        Assert.Equal(43, g.State.Players[Game.Me].Life);
    }

    [Fact]
    public void CountersPutOnceTriggerOnce()
    {
        var g = Setup(new[] { "Feed" }, battlefield: new[] { "Tracker", "Season", "Bear" });
        g.CastSpell(g.State[InHand(g, "Feed")], 0);
        ResolveAll(g);
        Assert.Equal(2, OnBattlefield(g, "Bear").CounterCount("P1P1"));
        Assert.Equal(41, g.State.Players[Game.Me].Life);
    }

    [Fact]
    public void GreatestPowerAmongMatchingCards()
    {
        var g = Setup(new[] { "Measure" }, battlefield: new[] { "Bear", "Brute" });
        g.CastSpell(g.State[InHand(g, "Measure")], 0);
        ResolveAll(g);
        Assert.Equal(43, g.State.Players[Game.Me].Life);
    }

    [Fact]
    public void ReturnToBattlefieldWithCounters()
    {
        var g = Setup(new[] { "Raise" }, battlefield: new[] { "Bear" });
        var bear = OnBattlefield(g, "Bear");
        g.MoveTo(bear, Zone.Graveyard);
        g.CastSpell(g.State[InHand(g, "Raise")], 0);
        ResolveAll(g);
        Assert.Equal(Zone.Battlefield, bear.Zone);
        Assert.Equal(4, bear.Power);
    }

    [Fact]
    public void UntapAllAndRemoveCounters()
    {
        var g = Setup(new[] { "Wake", "Drain" }, battlefield: new[] { "Bog", "Bog", "Bear" });
        foreach (var c in g.State.BattlefieldCards) c.Tapped = true;
        Game.AddCounters(OnBattlefield(g, "Bear"), "P1P1", 3);
        g.CastSpell(g.State[InHand(g, "Wake")], 0);
        g.CastSpell(g.State[InHand(g, "Drain")], 0);
        ResolveAll(g);
        Assert.All(g.State.BattlefieldCards.Where(c => c.Name == "Bog"), c => Assert.False(c.Tapped));
        Assert.Equal(0, OnBattlefield(g, "Bear").CounterCount("P1P1"));
    }

    [Fact]
    public void ReturnTargetSpellToHand()
    {
        var g = Setup(new[] { "Toll", "Bounce" });
        g.CastSpell(g.State[InHand(g, "Toll")], 0);
        g.CastSpell(g.State[InHand(g, "Bounce")], 0);
        ResolveAll(g);
        Assert.Equal(Zone.Hand, g.State.Cards.First(c => c.Name == "Toll").Zone);
        Assert.All(g.State.Players, p => Assert.Equal(40, p.Life));
    }

    [Fact]
    public void ClassLevelsGainAbilitiesInOrder()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Lesson", "Bear" }, library: new[] { "Filler", "Filler" });
        var lesson = OnBattlefield(g, "Lesson");
        Assert.Single(g.LegalActions(Game.Me).OfType<GameAction.Activate>(), a => a.CardId == lesson.Id);
        g.ActivateAbility(lesson, 0);
        ResolveAll(g);
        Assert.Equal(2, lesson.ClassLevel);
        g.ActivateAbility(lesson, 1);
        ResolveAll(g);
        g.CheckStateBasedActions();
        Assert.Equal(3, OnBattlefield(g, "Bear").Power);
        Assert.DoesNotContain(g.LegalActions(Game.Me), a => a is GameAction.Activate act && act.CardId == lesson.Id);
        g.ResumeTurn();
        Assert.Equal(42, g.State.Players[Game.Me].Life);
    }

    [Fact]
    public void TapsForManaTriggerAddsMana()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Reflect", "Bog" });
        g.ActivateAbility(OnBattlefield(g, "Bog"), 0);
        ResolveAll(g);
        Assert.Equal(2, g.State.Players[Game.Me].Pool[ManaColor.B]);
    }

    [Fact]
    public void MyriadCopiesJoinTheAttack()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Legion" }, library: new[] { "Filler", "Filler" });
        g.ResumeTurn();
        var opponents = g.State.Players.Count(p => p.Index != Game.Me);   // one copy per other opponent
        Assert.Equal(40 - 2 * opponents, g.State.Players.Where(p => p.Index != Game.Me).Min(p => p.Life));
        Assert.Single(g.State.BattlefieldCards, c => c.Name == "Legion");
    }

    [Fact]
    public void DiceRollPicksAResult()
    {
        var lives = new HashSet<int>();
        for (var seed = 1UL; seed <= 30; seed++)
        {
            var g = Setup(new[] { "Dice" });
            g.State.Rng = new Rng(seed);
            g.CastSpell(g.State[InHand(g, "Dice")], 0);
            ResolveAll(g);
            lives.Add(g.State.Players[Game.Me].Life);
        }
        Assert.Equal(new HashSet<int> { 41, 42 }, lives);
    }

    [Fact]
    public void ManaOfTheChosenColor()
    {
        var decider = new TestDecider { Choose = (game, d) => d.Kind == DecisionKind.Color ? d.Options.ToList().IndexOf(ManaColor.G) : null };
        var g = Setup(new[] { "Prism", "Sprout" }, decider: decider);
        g.CastSpell(g.State[InHand(g, "Prism")], -1);
        ResolveAll(g);
        Assert.Equal(ManaColor.G, OnBattlefield(g, "Prism").ChosenColor);
        Assert.True(g.CanCast(g.State[InHand(g, "Sprout")], 0));
    }

    [Fact]
    public void TargetCountCanDependOnX()
    {
        var g = Setup(new[] { "Scatter" });
        var card = g.State[InHand(g, "Scatter")];
        Assert.Equal((0, 2), g.TargetBounds(card, card.CurrentFace.SpellAbilities[0], Game.Me, 2));
    }

    [Fact]
    public void SuspendCastsTheCardWhenTheLastTimeCounterIsRemoved()
    {
        var g = Setup(new[] { "Vision" }, library: Enumerable.Repeat("Filler", 8).ToArray());
        var vision = g.State[InHand(g, "Vision")];
        var suspend = g.LegalActions(Game.Me).OfType<GameAction.Activate>().Single(a => a.CardId == vision.Id);
        g.Perform(suspend);
        ResolveAll(g);
        Assert.Equal(Zone.Exile, vision.Zone);
        Assert.Equal(2, vision.CounterCount("TIME"));
        g.ResumeTurn();
        g.RunTurn();
        Assert.Equal(1, vision.CounterCount("TIME"));
        g.RunTurn();
        Assert.Equal(Zone.Graveyard, vision.Zone);
        Assert.True(g.State.Players[Game.Me].Hand.Count >= 3 + 2);   // two draw steps and the spell
    }

    [Fact]
    public void EvokedCreatureIsSacrificedButItsEnterTriggerHappens()
    {
        var g = Setup(new[] { "Elemental" });
        var card = g.State[InHand(g, "Elemental")];
        var evoke = card.CurrentFace.SpellAbilities.FindIndex(a => a.Evoke);
        Assert.False(g.CanCast(card, 0));
        g.CastSpell(card, evoke);
        ResolveAll(g);
        Assert.Equal(43, g.State.Players[Game.Me].Life);
        Assert.Equal(Zone.Graveyard, card.Zone);
    }

    [Fact]
    public void TypecyclingFindsABasicLand()
    {
        var g = Setup(new[] { "Seeker" }, library: new[] { "Filler", "Bog" });
        g.ActivateAbility(g.State[InHand(g, "Seeker")], 0);
        ResolveAll(g);
        Assert.Equal("Bog", g.State[g.State.Players[Game.Me].Hand.Single()].Name);
        Assert.Equal(Zone.Graveyard, g.State.Cards.First(c => c.Name == "Seeker").Zone);
    }

    [Fact]
    public void LeylineStartsOnTheBattlefield()
    {
        var g = Setup(new[] { "Leyline" });
        g.KeepCurrentHand();
        Assert.Equal(Zone.Battlefield, g.State.Cards.First(c => c.Name == "Leyline").Zone);
    }

    [Fact]
    public void ReflexiveTriggerAfterPaying()
    {
        var g = Setup(new[] { "Reflex" }, library: new[] { "Filler", "Filler" });
        g.CastSpell(g.State[InHand(g, "Reflex")], 0);
        ResolveAll(g);
        Assert.Equal(38, g.State.Players[Game.Me].Life);
        Assert.Equal(2, g.State.Players[Game.Me].Hand.Count);
    }

    [Fact]
    public void EnchantedCreatureCantAttack()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Bear", "Chains" }, library: new[] { "Filler", "Filler" });
        OnBattlefield(g, "Chains").AttachedTo = OnBattlefield(g, "Bear").Id;
        g.ResumeTurn();
        Assert.All(g.State.Players, p => Assert.Equal(40, p.Life));
    }

    [Fact]
    public void TestCardNamesAreUnique()
    {
        var names = Scripts.Select(script => script.Split('\n')[0]).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void AdventureThenCreatureFromExile()
    {
        var g = Setup(new[] { "Knight" }, battlefield: new[] { "Bog" });
        var card = g.State[InHand(g, "Knight")];
        var quest = g.LegalActions(Game.Me).OfType<GameAction.Cast>().Single(a => a.CardId == card.Id && a.Face == 1);
        g.Perform(quest);
        ResolveAll(g);
        Assert.Equal(42, g.State.Players[Game.Me].Life);
        Assert.Equal(Zone.Exile, card.Zone);
        Assert.True(card.OnAdventure);
        var creature = g.LegalActions(Game.Me).OfType<GameAction.Cast>().Single(a => a.CardId == card.Id);
        Assert.Equal(0, creature.Face);
        g.Perform(creature);
        ResolveAll(g);
        Assert.Equal(Zone.Battlefield, card.Zone);
        Assert.Equal("Knight", card.Name);
    }

    [Fact]
    public void ExiledUntilTheHostLeaves()
    {
        var g = Setup(new[] { "Ring" }, battlefield: new[] { "Bear" });
        g.CastSpell(g.State[InHand(g, "Ring")], -1);
        ResolveAll(g);
        var bear = g.State.Cards.First(c => c.Name == "Bear");
        Assert.Equal(Zone.Exile, bear.Zone);
        g.MoveTo(OnBattlefield(g, "Ring"), Zone.Graveyard);
        g.CheckStateBasedActions();
        Assert.Equal(Zone.Battlefield, bear.Zone);
    }

    [Fact]
    public void DrawingFromAnEmptyLibraryWinsWithAReplacement()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Maniac" });
        g.Draw(Game.Me, 1);
        Assert.True(g.State.GameOver);
        Assert.Equal(Game.Me, g.State.Winner);
    }

    [Fact]
    public void AmassGrowsOneArmy()
    {
        var g = Setup(new[] { "Horde", "Horde" });
        g.CastSpell(g.State[InHand(g, "Horde")], 0);
        ResolveAll(g);
        g.CastSpell(g.State[InHand(g, "Horde")], 0);
        ResolveAll(g);
        var army = g.State.BattlefieldCards.Single(c => c.Types.HasSubtype("Army"));
        Assert.Equal(4, army.Power);
    }

    [Fact]
    public void MobilizeTokensAttackToo()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Raider" }, library: new[] { "Filler", "Filler" });
        g.ResumeTurn();
        Assert.Equal(36, g.State.Players.Where(p => p.Index != Game.Me).Min(p => p.Life));
        Assert.DoesNotContain(g.State.BattlefieldCards, c => c.IsToken);
    }

    [Fact]
    public void StaticWorksFromTheGraveyard()
    {
        var g = Setup(new[] { "Bear" }, battlefield: new[] { "Anger", "Bog", "Bog" });
        g.MoveTo(OnBattlefield(g, "Anger"), Zone.Graveyard);
        g.CastSpell(g.State[InHand(g, "Bear")], -1);
        ResolveAll(g);
        g.CheckStateBasedActions();
        Assert.False(g.IsSick(OnBattlefield(g, "Bear")));
    }

    [Fact]
    public void RemoveTypeTakesAwayACardType()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Humble", "Frog" });
        g.CheckStateBasedActions();
        Assert.False(OnBattlefield(g, "Frog").Types.Is(CardType.Creature));
        Assert.True(OnBattlefield(g, "Frog").Types.Is(CardType.Artifact));
    }

    [Fact]
    public void CascadeCastsTheFirstCheaperNonland()
    {
        var g = Setup(new[] { "Bloom" }, battlefield: new[] { "Bog", "Bog", "Bog" }, library: new[] { "Bog", "Filler", "Toll", "Bear" });
        g.CastSpell(g.State[InHand(g, "Bloom")], 0);
        ResolveAll(g);
        Assert.All(g.State.Players.Where(p => p.Index != Game.Me), p => Assert.Equal(38, p.Life));
        Assert.Equal(Zone.Graveyard, g.State.Cards.First(c => c.Name == "Toll").Zone);
        Assert.Empty(g.State.Players[Game.Me].Exile);
        Assert.Equal(2, g.State.Players[Game.Me].Library.Count);   // Bear drawn; the misses went to the bottom
    }

    [Fact]
    public void ExploreFindsALand()
    {
        var g = Setup(new[] { "Scout" }, library: new[] { "Bog" });
        g.CastSpell(g.State[InHand(g, "Scout")], -1);
        ResolveAll(g);
        Assert.Equal("Bog", g.State[g.State.Players[Game.Me].Hand.Single()].Name);
        Assert.Equal(0, OnBattlefield(g, "Scout").CounterCount("P1P1"));
    }

    [Fact]
    public void PoisonCounters()
    {
        var g = Setup(new[] { "Venom" });
        g.CastSpell(g.State[InHand(g, "Venom")], 0);
        ResolveAll(g);
        Assert.All(g.State.Players.Where(p => p.Index != Game.Me), p => Assert.Equal(3, p.Poison));
    }

    [Fact]
    public void DoubleItsPower()
    {
        var g = Setup(new[] { "Doubler" }, battlefield: new[] { "Bear" });
        g.CastSpell(g.State[InHand(g, "Doubler")], 0);
        ResolveAll(g);
        Assert.Equal(4, OnBattlefield(g, "Bear").Power);
    }

    [Fact]
    public void AbilityActivatedFromTheGraveyardByExilingIt()
    {
        var g = Setup(Array.Empty<string>(), battlefield: new[] { "Ghost" });
        var ghost = OnBattlefield(g, "Ghost");
        g.MoveTo(ghost, Zone.Graveyard);
        var act = g.LegalActions(Game.Me).OfType<GameAction.Activate>().Single(a => a.CardId == ghost.Id);
        g.Perform(act);
        ResolveAll(g);
        Assert.Equal(42, g.State.Players[Game.Me].Life);
        Assert.Equal(Zone.Exile, ghost.Zone);
    }

    [Fact]
    public void PersistReturnsOnce()
    {
        var g = Setup(new[] { "Wrath", "Wrath" }, battlefield: new[] { "Kitchen", "Bog", "Bog", "Bog", "Bog" });
        g.CastSpell(g.State[InHand(g, "Wrath")], 0);
        ResolveAll(g);
        var kitchen = g.State.Cards.First(c => c.Name == "Kitchen");
        Assert.Equal(Zone.Battlefield, kitchen.Zone);
        Assert.Equal(1, kitchen.Power);
        g.CastSpell(g.State[InHand(g, "Wrath")], 0);
        ResolveAll(g);
        Assert.Equal(Zone.Graveyard, kitchen.Zone);
    }

    [Fact]
    public void NinjutsuSwapsWithAnAttacker()
    {
        var g = Setup(new[] { "Ninja" }, battlefield: new[] { "Bear" });
        var bear = OnBattlefield(g, "Bear");
        g.State.Step = Step.DeclareAttackers;
        g.State.Attackers.Add(bear.Id);
        var ninja = g.State[InHand(g, "Ninja")];
        g.Perform(g.LegalActions(Game.Me).OfType<GameAction.Activate>().Single(a => a.CardId == ninja.Id));
        ResolveAll(g);
        Assert.Equal(Zone.Hand, bear.Zone);
        Assert.Equal(Zone.Battlefield, ninja.Zone);
        Assert.True(ninja.Tapped);
        Assert.Contains(ninja.Id, g.State.Attackers);
    }

    [Fact]
    public void TokensReplacedOrAdded()
    {
        var g = Setup(new[] { "Muster" }, battlefield: new[] { "Visitation", "Bog" });
        g.CastSpell(g.State[InHand(g, "Muster")], 0);
        ResolveAll(g);
        Assert.Equal(2, g.State.BattlefieldCards.Count(c => c.Name == "Warrior Token"));
        Assert.DoesNotContain(g.State.BattlefieldCards, c => c.Name == "Soldier Token");

        g = Setup(new[] { "Muster" }, battlefield: new[] { "Chatter", "Bog" });
        g.CastSpell(g.State[InHand(g, "Muster")], 0);
        ResolveAll(g);
        Assert.Equal(2, g.State.BattlefieldCards.Count(c => c.Name == "Soldier Token"));
        Assert.Single(g.State.BattlefieldCards, c => c.Name == "Treasure Token");
    }

    [Fact]
    public void DiscoverCastsACheapEnoughCard()
    {
        var g = Setup(new[] { "Find" }, library: new[] { "Filler", "Bear" });
        g.CastSpell(g.State[InHand(g, "Find")], 0);
        ResolveAll(g);
        Assert.Equal(Zone.Battlefield, g.State.Cards.First(c => c.Name == "Bear").Zone);
    }

    [Fact]
    public void GenericChoiceLetsYouPick()
    {
        var g = Setup(new[] { "Pick" }, decider: new TestDecider { Choose = (game, d) => d.Kind == DecisionKind.Option ? 1 : null });
        g.CastSpell(g.State[InHand(g, "Pick")], 0);
        ResolveAll(g);
        Assert.Equal(45, g.State.Players[Game.Me].Life);
    }

    [Fact]
    public void CyclingTriggersWhenYouCycle()
    {
        var g = Setup(new[] { "Cyc2" }, library: new[] { "Filler" });
        g.ActivateAbility(g.State[InHand(g, "Cyc2")], 0);
        ResolveAll(g);
        Assert.Equal(42, g.State.Players[Game.Me].Life);
    }

    [Fact]
    public void EarthbentLandReturnsWhenItDies()
    {
        var g = Setup(new[] { "Bend", "Wrath" }, battlefield: new[] { "Bog", "Bog" });
        g.CastSpell(g.State[InHand(g, "Bend")], 0);
        ResolveAll(g);
        g.CheckStateBasedActions();
        var land = g.State.BattlefieldCards.Single(c => c.Types.Is(CardType.Creature));
        Assert.Equal(2, land.Power);
        Assert.False(g.IsSick(land) && !land.HasKeyword("Haste"));
        g.CastSpell(g.State[InHand(g, "Wrath")], 0);
        ResolveAll(g);
        Assert.Equal(Zone.Battlefield, land.Zone);
        Assert.True(land.Tapped);
        Assert.False(land.Types.Is(CardType.Creature));
    }

    [Fact]
    public void AdditionalCombatPhase()
    {
        var g = Setup(new[] { "Assault" }, battlefield: new[] { "Watcher" }, library: new[] { "Filler", "Filler" });
        g.CastSpell(g.State[InHand(g, "Assault")], 0);
        ResolveAll(g);
        g.ResumeTurn();
        Assert.Equal(36, g.State.Players.Where(p => p.Index != Game.Me).Min(p => p.Life));
    }

    [Fact]
    public void ExtraTurnIsTheSameRound()
    {
        var g = Setup(new[] { "Warp" }, battlefield: new[] { "Bear" }, library: new[] { "Filler", "Filler", "Filler" });
        g.CastSpell(g.State[InHand(g, "Warp")], 0);
        ResolveAll(g);
        Assert.Equal(1, g.State.ExtraTurns);
        g.ResumeTurn();
        g.RunExtraTurns();
        Assert.Equal(2, g.State.Turn);
        Assert.Equal(1, g.State.Round);
        Assert.Equal(36, g.State.Players.Where(p => p.Index != Game.Me).Min(p => p.Life));   // the Bear attacked in both turns
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
