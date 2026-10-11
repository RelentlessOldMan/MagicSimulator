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
    public bool IsCommander { get; init; }
    /// <summary>Times cast from the command zone (commander tax).</summary>
    public int CommandCasts { get; set; }
    public int Damage { get; set; }
    public int PowerBonus { get; set; }
    public int ToughnessBonus { get; set; }
    public Dictionary<string, int>? Counters { get; set; }
    public HashSet<string>? TempKeywords { get; set; }
    public List<int>? Remembered { get; set; }
    public string? NamedCard { get; set; }
    public int AttachedTo { get; set; } = -1;
    /// <summary>The permanent entered or was cast this turn with X = this value.</summary>
    public int XPaid { get; set; }

    public FaceDef CurrentFace => Def.Faces[Face];
    public string Name => CurrentFace.Name;
    public TypeLine Types => CurrentFace.Types;

    public int Power => (CurrentFace.Power ?? 0) + PowerBonus + CounterCount("P1P1") - CounterCount("M1M1");
    public int Toughness => (CurrentFace.Toughness ?? 0) + ToughnessBonus + CounterCount("P1P1") - CounterCount("M1M1");

    public int CounterCount(string kind) => Counters is not null && Counters.TryGetValue(kind, out var n) ? n : 0;

    public bool HasKeyword(string k) =>
        CurrentFace.KeywordNames.Contains(k) || (TempKeywords?.Contains(k) ?? false);

    public Card Clone() => new()
    {
        Id = Id, Def = Def, Owner = Owner, Controller = Controller, Zone = Zone, Face = Face, Tapped = Tapped,
        ControlledSinceTurn = ControlledSinceTurn, IsToken = IsToken, IsCommander = IsCommander, CommandCasts = CommandCasts,
        Damage = Damage, PowerBonus = PowerBonus, ToughnessBonus = ToughnessBonus,
        Counters = Counters is null ? null : new Dictionary<string, int>(Counters),
        TempKeywords = TempKeywords is null ? null : new HashSet<string>(TempKeywords),
        Remembered = Remembered is null ? null : new List<int>(Remembered),
        NamedCard = NamedCard, AttachedTo = AttachedTo, XPaid = XPaid,
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
        LandDropsPerTurn = LandDropsPerTurn, Lost = Lost, LossReason = LossReason, AbstractLibrary = AbstractLibrary,
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
    /// <summary>For triggers: the card that caused it (TriggeredCard), if any.</summary>
    public int? TriggeredCardId { get; init; }
    public string Description { get; init; } = "";

    public StackItem Clone() => new()
    {
        Id = Id, SourceId = SourceId, Ability = Ability, Controller = Controller, IsSpell = IsSpell,
        ChainTargets = ChainTargets.Select(t => new List<TargetRef>(t)).ToList(), X = X, TriggeredCardId = TriggeredCardId, Description = Description,
    };
}

/// <summary>Until-end-of-turn bookkeeping and per-turn counters.</summary>
public sealed class TurnInfo
{
    public int SpellsCast { get; set; }
    public int MaxManaSeen { get; set; }
    public TurnInfo Clone() => new() { SpellsCast = SpellsCast, MaxManaSeen = MaxManaSeen };
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
        NextStackId = NextStackId, Record = Record.Clone(),
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
