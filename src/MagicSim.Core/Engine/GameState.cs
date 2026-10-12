namespace MagicSim.Core.Engine;

public enum Zone : byte { None, Library, Hand, Battlefield, Graveyard, Exile, Command, Stack }

public enum Step : byte
{
    Untap, Upkeep, Draw, Main1, BeginCombat, DeclareAttackers, CombatDamage, EndCombat, Main2, End, Cleanup,
}

/// <summary>A card (or token) object in a game. Mutable; cloned with the state.</summary>
public sealed class Card
{
    public int Id { get; init; }
    public required CardDef Def { get; init; }
    public int Owner { get; init; }
    public int Controller { get; set; }
    public Zone Zone { get; set; }
    /// <summary>Which face is up (DFC back face after transforming, etc.).</summary>
    public int Face { get; set; }
    public bool Tapped { get; set; }
    /// <summary>Turn this permanent came under its controller's control (summoning sickness).</summary>
    public int ControlledSinceTurn { get; set; }
    public bool IsToken { get; init; }
    /// <summary>An effect or emblem object in the command zone (Forge "Effect"), not a card.</summary>
    public bool IsEffect { get; init; }
    /// <summary>For an effect: its Duration$, the turn it began, the card that made it, and what ends it.</summary>
    public string? EffectDuration { get; init; }
    public int EffectTurn { get; init; }
    public int EffectHost { get; init; } = -1;
    public string? ExileOnMoved { get; init; }
    public string? ForgetOnMoved { get; init; }
    /// <summary>"As this enters, choose a color / creature type / player."</summary>
    public ManaColor? ChosenColor { get; set; }
    public string? ChosenType { get; set; }
    public int? ChosenPlayer { get; set; }
    public bool IsCommander { get; init; }
    /// <summary>Times cast from the command zone (commander tax).</summary>
    public int CommandCasts { get; set; }
    public int Damage { get; set; }
    public int PowerBonus { get; set; }
    public int ToughnessBonus { get; set; }
    public Dictionary<string, int>? Counters { get; set; }
    public HashSet<string>? TempKeywords { get; set; }
    public List<int>? Remembered { get; set; }
    /// <summary>Cards imprinted on (exiled with) this card: Chrome Mox, Tainted Pact.</summary>
    public List<int>? Imprinted { get; set; }
    /// <summary>Values set by StoreSVar$, overriding the card's SVar of that name.</summary>
    public Dictionary<string, int>? StoredVars { get; set; }
    public string? NamedCard { get; set; }
    public int AttachedTo { get; set; } = -1;
    /// <summary>The permanent entered or was cast this turn with X = this value.</summary>
    public int XPaid { get; set; }
    /// <summary>A token created with set power/toughness (TokenPower$/TokenToughness$).</summary>
    public int? TokenPower { get; init; }
    public int? TokenToughness { get; init; }
    /// <summary>Triggers with a per-turn limit (ActivationLimit$) and the turn and times each last triggered.</summary>
    public Dictionary<TriggerDef, (int Turn, int Count)>? TriggerUses { get; set; }
    /// <summary>In exile "on an adventure": its owner may cast the creature half from there (rule 715.4).</summary>
    public bool OnAdventure { get; set; }
    /// <summary>Suspended in exile (rule 702.62).</summary>
    public bool Suspended { get; set; }
    /// <summary>A Class's current level (rule 716).</summary>
    public int ClassLevel { get; set; } = 1;
    /// <summary>The turn this permanent last entered the battlefield.</summary>
    public int EnteredTurn { get; set; } = -1;
    /// <summary>The turn this permanent last activated a loyalty ability: one per turn (rule 606.3).</summary>
    public int LoyaltyActivatedTurn { get; set; }
    /// <summary>Regeneration shields until end of turn (rule 701.15).</summary>
    public int RegenShields { get; set; }

    // Results of continuous effects (rule 613), recomputed by Game.ApplyStatics.
    public int? SetPower { get; set; }
    public int? SetToughness { get; set; }
    public int StaticPower { get; set; }
    public int StaticToughness { get; set; }
    public HashSet<string>? StaticKeywords { get; set; }

    /// <summary>A copy effect on this permanent (Clone): its copiable values, replacing the printed card's.</summary>
    public CardDef? CopyDef { get; set; }
    /// <summary>The copy effect ends at cleanup ("until end of turn").</summary>
    public bool CopyUntilCleanup { get; set; }

    public FaceDef CurrentFace => CopyDef?.Front ?? Def.Faces[Face];
    /// <summary>The card (or what it copies) is unsupported and plays as a blank.</summary>
    public bool IsBlank => (CopyDef ?? Def).IsBlank;
    public string Name => CurrentFace.Name;
    public TypeLine Types => StaticTypes ?? CurrentFace.Types;
    public ColorMask Colors => StaticColors ?? CurrentFace.Colors;
    /// <summary>Activated abilities, including any granted (and without any lost) by continuous effects.</summary>
    public IReadOnlyList<AbilityDef> Activated => StaticActivated ?? CurrentFace.Activated;
    public IReadOnlyList<TriggerDef> Triggers => StaticTriggers ?? CurrentFace.Triggers;
    /// <summary>Static abilities: the card's own plus any its animations give.</summary>
    public IEnumerable<StaticDef> Statics
    {
        get
        {
            if (Animations is null && ClassLevel <= 1) return CurrentFace.Statics;
            var own = Animations?.Any(a => a.Ability["RemoveAllAbilities"] == "True") ?? false ? Enumerable.Empty<StaticDef>() : CurrentFace.Statics;
            return own
                .Concat(CurrentFace.ClassLevels.Where(l => l.Level <= ClassLevel).SelectMany(l => l.Grants.Statics))
                .Concat(Animations?.SelectMany(a => a.Ability.Grants?.Statics ?? Enumerable.Empty<StaticDef>()) ?? Enumerable.Empty<StaticDef>());
        }
    }

    /// <summary>"Becomes a …" effects on this permanent, oldest first.</summary>
    public List<Animation>? Animations { get; set; }
    // More results of continuous effects (layers 4–6).
    public TypeLine? StaticTypes { get; set; }
    public ColorMask? StaticColors { get; set; }
    public List<AbilityDef>? StaticActivated { get; set; }
    public List<TriggerDef>? StaticTriggers { get; set; }
    /// <summary>Lost all abilities (keywords included) to a continuous effect.</summary>
    public bool LostAbilities { get; set; }

    public int Power => (SetPower ?? TokenPower ?? CurrentFace.Power ?? 0) + StaticPower + PowerBonus + CounterCount("P1P1") - CounterCount("M1M1");
    public int Toughness => (SetToughness ?? TokenToughness ?? CurrentFace.Toughness ?? 0) + StaticToughness + ToughnessBonus + CounterCount("P1P1") - CounterCount("M1M1");

    /// <summary>Power/toughness as it last was on the battlefield (rule 608.2h), for cards that have left.</summary>
    public int? LeftPower { get; set; }
    public int? LeftToughness { get; set; }
    public int LastKnownPower => Zone == Zone.Battlefield ? Power : LeftPower ?? Power;
    public int LastKnownToughness => Zone == Zone.Battlefield ? Toughness : LeftToughness ?? Toughness;

    public int CounterCount(string kind) => Counters is not null && Counters.TryGetValue(kind, out var n) ? n : 0;

    public bool HasKeyword(string k) =>
        (!LostAbilities && CurrentFace.KeywordNames.Contains(k)) || (TempKeywords?.Contains(k) ?? false) || (StaticKeywords?.Contains(k) ?? false);

    public void ClearStaticEffects()
    {
        SetPower = SetToughness = null;
        StaticPower = StaticToughness = 0;
        StaticKeywords = null;
        StaticTypes = null;
        StaticColors = null;
        StaticActivated = null;
        StaticTriggers = null;
        LostAbilities = false;
    }

    public Card Clone() => new()
    {
        Id = Id, Def = Def, CopyDef = CopyDef, CopyUntilCleanup = CopyUntilCleanup, Owner = Owner, Controller = Controller, Zone = Zone, Face = Face, Tapped = Tapped,
        ControlledSinceTurn = ControlledSinceTurn, IsToken = IsToken, IsEffect = IsEffect, EffectDuration = EffectDuration,
        EffectTurn = EffectTurn, EffectHost = EffectHost, ExileOnMoved = ExileOnMoved, ForgetOnMoved = ForgetOnMoved,
        ChosenColor = ChosenColor, ChosenType = ChosenType, ChosenPlayer = ChosenPlayer, TokenPower = TokenPower, TokenToughness = TokenToughness, IsCommander = IsCommander, CommandCasts = CommandCasts,
        Damage = Damage, PowerBonus = PowerBonus, ToughnessBonus = ToughnessBonus,
        Counters = Counters is null ? null : new Dictionary<string, int>(Counters),
        TempKeywords = TempKeywords is null ? null : new HashSet<string>(TempKeywords),
        Remembered = Remembered is null ? null : new List<int>(Remembered),
        Imprinted = Imprinted is null ? null : new List<int>(Imprinted),
        StoredVars = StoredVars is null ? null : new Dictionary<string, int>(StoredVars),
        NamedCard = NamedCard, AttachedTo = AttachedTo, LoyaltyActivatedTurn = LoyaltyActivatedTurn, EnteredTurn = EnteredTurn, ClassLevel = ClassLevel, Suspended = Suspended, OnAdventure = OnAdventure,
        TriggerUses = TriggerUses is null ? null : new(TriggerUses), XPaid = XPaid, RegenShields = RegenShields, LeftPower = LeftPower, LeftToughness = LeftToughness,
        SetPower = SetPower, SetToughness = SetToughness, StaticPower = StaticPower, StaticToughness = StaticToughness,
        StaticKeywords = StaticKeywords is null ? null : new HashSet<string>(StaticKeywords),
        Animations = Animations is null ? null : new List<Animation>(Animations),
        StaticTypes = StaticTypes, StaticColors = StaticColors, StaticActivated = StaticActivated, StaticTriggers = StaticTriggers,
        LostAbilities = LostAbilities,
    };

    public override string ToString() => $"{Name}#{Id}";
}

public sealed class Player
{
    public int Index { get; init; }
    public int Life { get; set; }
    public int Poison { get; set; }
    public ManaPool Pool;
    public int LandsPlayedThisTurn { get; set; }
    public int LandDropsPerTurn { get; set; } = 1;
    /// <summary>Extra land drops from continuous effects (Exploration, Azusa).</summary>
    public int StaticLandDrops { get; set; }
    /// <summary>Maximum hand size set by continuous effects (Reliquary Tower = int.MaxValue).</summary>
    public int? StaticMaxHandSize { get; set; }
    public bool Lost { get; set; }
    public string? LossReason { get; set; }
    /// <summary>Goldfish opponents have no real cards: just a library size that milling reduces.</summary>
    public int AbstractLibrary { get; set; }
    public bool IsGoldfish { get; init; }
    public Dictionary<int, int> CommanderDamageTaken { get; set; } = new();
    public int LifePaidTotal { get; set; }
    public bool DrewFromEmpty { get; set; }
    /// <summary>How many cards on top of the library this player knows (put there or looked at). Search keeps them in place.</summary>
    public int KnownTop { get; set; }

    public List<int> Library { get; set; } = new();   // index 0 = top
    public List<int> Hand { get; set; } = new();
    public List<int> Graveyard { get; set; } = new();
    public List<int> Exile { get; set; } = new();
    public List<int> Command { get; set; } = new();

    public int LibraryCount => IsGoldfish ? AbstractLibrary : Library.Count;

    public Player Clone() => new()
    {
        Index = Index, Life = Life, Poison = Poison, Pool = Pool, LandsPlayedThisTurn = LandsPlayedThisTurn,
        LandDropsPerTurn = LandDropsPerTurn, StaticLandDrops = StaticLandDrops, StaticMaxHandSize = StaticMaxHandSize, Lost = Lost, LossReason = LossReason, AbstractLibrary = AbstractLibrary,
        IsGoldfish = IsGoldfish, CommanderDamageTaken = new Dictionary<int, int>(CommanderDamageTaken),
        LifePaidTotal = LifePaidTotal, DrewFromEmpty = DrewFromEmpty, KnownTop = KnownTop,
        Library = new List<int>(Library), Hand = new List<int>(Hand), Graveyard = new List<int>(Graveyard),
        Exile = new List<int>(Exile), Command = new List<int>(Command),
    };
}

/// <summary>A target: a player index or a card id.</summary>
public readonly record struct TargetRef(int? Player, int? CardId)
{
    public static TargetRef OfPlayer(int p) => new(p, null);
    public static TargetRef OfCard(int id) => new(null, id);
    public override string ToString() => Player is { } p ? $"player {p}" : $"card #{CardId}";
}

/// <summary>A spell or ability on the stack.</summary>
public sealed class StackItem
{
    public int Id { get; init; }
    public int SourceId { get; init; }
    public required AbilityDef Ability { get; init; }
    public int Controller { get; init; }
    /// <summary>True if the source card itself is on the stack (a cast spell).</summary>
    public bool IsSpell { get; init; }
    /// <summary>Targets per ability in the chain (index = AbilityDef.ChainIndex).</summary>
    public List<List<TargetRef>> ChainTargets { get; init; } = new();
    public int X { get; init; }
    /// <summary>Cast with flashback: exiled instead of going anywhere else when it leaves the stack.</summary>
    public bool ExileAfter { get; init; }
    /// <summary>For triggers: the card that caused it (TriggeredCard), if any.</summary>
    public int? TriggeredCardId { get; init; }
    /// <summary>For triggers: the player involved (e.g. the player dealt damage), if any.</summary>
    public int? TriggeredPlayer { get; init; }
    public string Description { get; init; } = "";
    /// <summary>For a damage trigger: the source that dealt the damage (TriggeredSource).</summary>
    public int? TriggeredSourceId { get; init; }
    /// <summary>For a trigger: the amount of its event (damage dealt, life gained, cards moved).</summary>
    public int TriggerAmount { get; init; }
    /// <summary>Permanents sacrificed to pay for this (or that triggered it): "Sacrificed".</summary>
    public List<int>? Sacrificed { get; set; }
    /// <summary>For a delayed trigger: the objects it remembers (DelayTriggerRemembered).</summary>
    public List<int>? DelayRemembered { get; init; }
    /// <summary>The player a RepeatEach is currently on ("Remembered" as a player).</summary>
    public int? IteratedPlayer { get; set; }

    public StackItem Clone() => new()
    {
        DelayRemembered = DelayRemembered, IteratedPlayer = IteratedPlayer, TriggerAmount = TriggerAmount, Sacrificed = Sacrificed,
        TriggeredSourceId = TriggeredSourceId,
        Id = Id, SourceId = SourceId, Ability = Ability, Controller = Controller, IsSpell = IsSpell,
        ChainTargets = ChainTargets.Select(t => new List<TargetRef>(t)).ToList(), X = X, TriggeredCardId = TriggeredCardId, TriggeredPlayer = TriggeredPlayer, Description = Description, ExileAfter = ExileAfter,
    };
}

/// <summary>Until-end-of-turn bookkeeping and per-turn counters.</summary>
public sealed class TurnInfo
{
    public int SpellsCast { get; set; }
    public int MaxManaSeen { get; set; }
    /// <summary>"At the beginning of the next end step" actions: (card id, "Exile" or "Sacrifice").</summary>
    public List<(int CardId, string Action)> AtEndStep { get; set; } = new();
    /// <summary>Control changes that end at cleanup: (card id, controller to return it to).</summary>
    public List<(int CardId, int Controller)> ControlUntilEot { get; set; } = new();
    /// <summary>Life each player gained this turn.</summary>
    public Dictionary<int, int> LifeGained { get; set; } = new();
    /// <summary>Cards each player drew this turn.</summary>
    public Dictionary<int, int> CardsDrawn { get; set; } = new();
    public TurnInfo Clone() => new()
    {
        SpellsCast = SpellsCast, MaxManaSeen = MaxManaSeen, AtEndStep = new(AtEndStep), ControlUntilEot = new(ControlUntilEot), LifeGained = new(LifeGained),
        CardsDrawn = new(CardsDrawn),
    };
}

/// <summary>Everything about a game in progress. <see cref="Clone"/> gives an independent copy for search.</summary>
public sealed class GameState
{
    public List<Card> Cards { get; set; } = new();          // index = Card.Id
    public Player[] Players { get; set; } = Array.Empty<Player>();
    public List<int> Battlefield { get; set; } = new();
    public List<StackItem> Stack { get; set; } = new();     // last = top
    public int Turn { get; set; }
    public Step Step { get; set; }
    public int ActivePlayer { get; set; }
    public Rng Rng;
    public bool GameOver { get; set; }
    public int? Winner { get; set; }
    public string? WinReason { get; set; }
    public TurnInfo ThisTurn { get; set; } = new();
    public List<int> Attackers { get; set; } = new();
    public int NextStackId { get; set; }
    /// <summary>Cards exiled "until [host] leaves the battlefield": (exiled card, host).</summary>
    public List<(int CardId, int HostId)> UntilHostLeaves { get; set; } = new();
    /// <summary>Delayed triggers waiting for their event.</summary>
    public List<DelayedTrigger> Delayed { get; set; } = new();
    /// <summary>Cards seen in the game's main player's hand, cards cast, notes — for per-game output.</summary>
    public GameRecord Record { get; set; } = new();

    public Card this[int id] => Cards[id];

    public IEnumerable<Card> BattlefieldCards => Battlefield.Select(id => Cards[id]);

    public List<int> ZoneList(int player, Zone zone) => zone switch
    {
        Zone.Library => Players[player].Library,
        Zone.Hand => Players[player].Hand,
        Zone.Graveyard => Players[player].Graveyard,
        Zone.Exile => Players[player].Exile,
        Zone.Command => Players[player].Command,
        Zone.Battlefield => Battlefield,
        _ => throw new ArgumentException($"no card list for zone {zone}"),
    };

    public GameState Clone() => new()
    {
        Cards = Cards.Select(c => c.Clone()).ToList(),
        Players = Players.Select(p => p.Clone()).ToArray(),
        Battlefield = new List<int>(Battlefield),
        Stack = Stack.Select(s => s.Clone()).ToList(),
        Turn = Turn, Step = Step, ActivePlayer = ActivePlayer, Rng = Rng,
        GameOver = GameOver, Winner = Winner, WinReason = WinReason, ThisTurn = ThisTurn.Clone(), Attackers = new List<int>(Attackers),
        NextStackId = NextStackId, Record = Record.Clone(), Delayed = new List<DelayedTrigger>(Delayed),
        UntilHostLeaves = new(UntilHostLeaves),
    };
}

/// <summary>What per-game output reports (CONTRACT.md §3 magicsim.game/1).</summary>
public sealed class GameRecord
{
    public SortedSet<string> Seen { get; set; } = new(StringComparer.Ordinal);
    public List<string> Cast { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    public List<int> ManaByTurn { get; set; } = new();
    public int StormMax { get; set; }
    public List<string> WinCards { get; set; } = new();
    public string? WinLine { get; set; }
    /// <summary>The last card to damage, drain or mill an opponent: the finisher when they all die.</summary>
    public string? LastHitBy { get; set; }

    public GameRecord Clone() => new()
    {
        Seen = new SortedSet<string>(Seen, StringComparer.Ordinal), Cast = new List<string>(Cast),
        Notes = new List<string>(Notes), ManaByTurn = new List<int>(ManaByTurn), StormMax = StormMax,
        WinCards = new List<string>(WinCards), WinLine = WinLine, LastHitBy = LastHitBy,
    };
}
