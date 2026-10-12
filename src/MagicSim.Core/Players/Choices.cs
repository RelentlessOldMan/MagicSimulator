using MagicSim.Core.Engine;

namespace MagicSim.Core.Players;

public enum DecisionKind { Action, Card, Target, X, YesNo, Color, Name, Option, Keep, Bottom, Attackers }

/// <summary>
/// One choice point, reduced to "pick one of these options". Multi-card choices become a
/// sequence of single picks (with a null "done" option once the minimum is met), so search,
/// replay and policies all work on plain option indices.
/// </summary>
public sealed class Decision
{
    public required DecisionKind Kind { get; init; }
    public required IReadOnlyList<object?> Options { get; init; }
    public Choice CardChoice { get; init; } = Choice.Generic;
    public Card? Source { get; init; }
    public AbilityDef? Ability { get; init; }
    public int Player { get; init; }
    /// <summary>Cards already picked in this multi-pick (for policies).</summary>
    public IReadOnlyList<int> Picked { get; init; } = Array.Empty<int>();

    /// <summary>Whether search should explore this decision (vs. leave it to the policy).</summary>
    public bool Searchable => Kind is not (DecisionKind.Keep or DecisionKind.Bottom or DecisionKind.Attackers);
}

/// <summary>
/// Implements every <see cref="IDecider"/> question by enumerating options in a fixed,
/// deterministic order and asking <see cref="Pick"/> for an index. Deterministic enumeration
/// is what lets search replay a game to the same decision point.
/// </summary>
public abstract class ChoiceDecider : IDecider
{
    /// <summary>The name used when a "name a card" effect should name nothing in the deck.</summary>
    public const string NotInDeck = "(a card not in your deck)";

    protected abstract int Pick(Game g, Decision d);

    public virtual bool PassesQuietSteps => false;

    /// <summary>Lets one decider delegate a decision to another.</summary>
    public int PickPublic(Game g, Decision d) => Pick(g, d);

    public GameAction ChooseAction(Game g, int player, IReadOnlyList<GameAction> legal) =>
        legal.Count == 1 ? legal[0] : legal[Pick(g, new Decision { Kind = DecisionKind.Action, Options = legal, Player = player })];

    public bool KeepHand(Game g, int player, int mulligansTaken) =>
        Pick(g, new Decision { Kind = DecisionKind.Keep, Options = new object?[] { true, false }, Player = player }) == 0;

    public List<int> ChooseCards(Game g, int player, Choice kind, Card? source, IReadOnlyList<int> candidates, int min, int max)
    {
        var picked = new List<int>();
        var remaining = candidates.ToList();
        var decisionKind = kind == Choice.Bottom ? DecisionKind.Bottom : DecisionKind.Card;
        while (picked.Count < max && remaining.Count > 0)
        {
            // Same-named cards are interchangeable: offer each name once.
            var options = new List<object?>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in remaining)
                if (names.Add(g.State[id].Name)) options.Add(id);
            if (picked.Count >= min) options.Add(null);
            if (options.Count == 1 && options[0] is null) break;
            var choice = options.Count == 1 ? options[0]
                : options[Pick(g, new Decision { Kind = decisionKind, CardChoice = kind, Options = options, Source = source, Player = player, Picked = picked })];
            if (choice is not int id2) break;
            picked.Add(id2);
            remaining.Remove(id2);
        }
        return picked;
    }

    public List<TargetRef> ChooseTargets(Game g, int player, Card source, AbilityDef ability, IReadOnlyList<TargetRef> candidates, int min, int max)
    {
        var picked = new List<TargetRef>();
        var remaining = candidates.ToList();
        while (picked.Count < max && remaining.Count > 0)
        {
            // Interchangeable targets (a board of identical tokens) are offered once.
            var attachedTo = g.State.BattlefieldCards.Where(c => c.AttachedTo >= 0).Select(c => c.AttachedTo).ToHashSet();
            var options = remaining.DistinctBy(t => TargetClass(g, t, attachedTo)).Cast<object?>().ToList();
            if (picked.Count >= min) options.Add(null);
            var choice = options.Count == 1 ? options[0]
                : options[Pick(g, new Decision { Kind = DecisionKind.Target, Options = options, Source = source, Ability = ability, Player = player })];
            if (choice is not TargetRef t) break;
            picked.Add(t);
            remaining.Remove(t);
        }
        return picked;
    }

    /// <summary>
    /// Two plain cards (no counters, attachments, memory or one-off changes) with the same definition, controller
    /// and visible state make the same target; anything else is its own class.
    /// </summary>
    private static object TargetClass(Game g, TargetRef t, HashSet<int> attachedTo)
    {
        if (t.CardId is not { } id) return t;
        var c = g.State[id];
        var plain = c.Counters is not { Count: > 0 } && c.TempKeywords is null && c.Animations is null && c.CopyDef is null
            && c.Remembered is null && c.Imprinted is null && c.StoredVars is null && c.ChosenCards is null && c.NamedCard is null
            && c.ChosenColor is null && c.ChosenType is null && c.ChosenPlayer is null && c.ChosenMode is null
            && c.AttachedTo < 0 && !attachedTo.Contains(id) && !c.FaceDown && c.RegenShields == 0 && !c.IsCommander
            && c.ExiledWith < 0 && c.LeftTriggers is null && c.StaticActivated is null && c.StaticTriggers is null;
        if (!plain) return t;
        var keywords = c.StaticKeywords is null ? "" : string.Join(",", c.StaticKeywords.Order(StringComparer.Ordinal));
        return (c.Def, c.Zone, c.IsToken, c.Owner, c.Controller, c.Tapped, c.Damage, c.Power, c.Toughness,
            g.IsSick(c), c.EnteredTurn, keywords, c.StaticTypes?.ToString(), c.StaticColors);
    }

    public int ChooseX(Game g, int player, Card source, int maxX)
    {
        if (maxX <= 0) return 0;
        // Largest first, then the smallest: mana X usually wants the most, "pay X life" a little.
        var values = Enumerable.Range(0, Math.Min(maxX, 6) + 1).Select(i => maxX - i)
            .Concat(Enumerable.Range(0, Math.Min(maxX, 7)).Reverse()).Distinct();
        var options = values.Select(v => (object?)v).ToList();
        return (int)options[Pick(g, new Decision { Kind = DecisionKind.X, Options = options, Source = source, Player = player })]!;
    }

    public bool ChooseYesNo(Game g, int player, Card? source, string question) =>
        Pick(g, new Decision { Kind = DecisionKind.YesNo, Options = new object?[] { true, false }, Source = source, Player = player }) == 0;

    public ManaColor ChooseColor(Game g, int player, Card? source, ColorMask options)
    {
        var colors = ManaColors.All.Where(c => (options & c.Mask()) != 0).Cast<object?>().ToList();
        if (colors.Count == 1) return (ManaColor)colors[0]!;
        return (ManaColor)colors[Pick(g, new Decision { Kind = DecisionKind.Color, Options = colors, Source = source, Player = player })]!;
    }

    public string ChooseCardName(Game g, int player, Card source)
    {
        var names = new List<object?> { NotInDeck };
        names.AddRange(g.State.Players[player].Library.Select(id => g.State[id].Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        return (string)names[Pick(g, new Decision { Kind = DecisionKind.Name, Options = names, Source = source, Player = player })]!;
    }

    public List<int> ChooseAttackers(Game g, int player, IReadOnlyList<int> candidates)
    {
        // Goldfish: nobody blocks, so attacking with everything is never worse.
        var all = candidates.ToList();
        var options = new object?[] { all, new List<int>() };
        return (List<int>)options[Pick(g, new Decision { Kind = DecisionKind.Attackers, Options = options, Player = player })]!;
    }

    public int ChooseOption(Game g, int player, Card? source, IReadOnlyList<string> options) =>
        options.Count == 1 ? 0 : Pick(g, new Decision { Kind = DecisionKind.Option, Options = options.Cast<object?>().ToList(), Source = source, Player = player });
}
