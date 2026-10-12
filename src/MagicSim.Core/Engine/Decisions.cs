namespace MagicSim.Core.Engine;

/// <summary>Something a player can do when they have priority.</summary>
public abstract record GameAction
{
    public sealed record Pass : GameAction { public override string ToString() => "pass"; }
    /// <summary>Play a land; <paramref name="Face"/> is the face it's played as (modal double-faced cards).</summary>
    public sealed record PlayLand(int CardId, int Face = 0) : GameAction;
    /// <summary>Cast a card from hand or the command zone, using spell ability <paramref name="SpellIndex"/> (-1 = a permanent spell) of face <paramref name="Face"/>.</summary>
    public sealed record Cast(int CardId, int SpellIndex, int Face = 0) : GameAction;
    public sealed record Activate(int CardId, int AbilityIndex) : GameAction;

    public static readonly Pass PassAction = new();
}

public enum Choice
{
    Tutor, Discard, Sacrifice, ExileFromGraveyard, TapForCost, KeepOnTop, PutOnBottom, ChooseFromRevealed, Bottom, Generic, Copy,
}

/// <summary>
/// Every choice the rules hand to a player. The goldfish opponents never get asked anything;
/// the simulated player's decider is the search (or a test script).
/// </summary>
public interface IDecider
{
    GameAction ChooseAction(Game g, int player, IReadOnlyList<GameAction> legal);
    bool KeepHand(Game g, int player, int mulligansTaken);
    List<int> ChooseCards(Game g, int player, Choice kind, Card? source, IReadOnlyList<int> candidates, int min, int max);
    List<TargetRef> ChooseTargets(Game g, int player, Card source, AbilityDef ability, IReadOnlyList<TargetRef> candidates, int min, int max);
    int ChooseX(Game g, int player, Card source, int maxX);
    bool ChooseYesNo(Game g, int player, Card? source, string question);
    ManaColor ChooseColor(Game g, int player, Card? source, ColorMask options);
    string ChooseCardName(Game g, int player, Card source);
    List<int> ChooseAttackers(Game g, int player, IReadOnlyList<int> candidates);
    int ChooseOption(Game g, int player, Card? source, IReadOnlyList<string> options);
}
