using System.Collections.Concurrent;
using MagicSim.Core.Cards;
using MagicSim.Core.Forge;

namespace MagicSim.Core.Engine;

/// <summary>
/// Turns card names into compiled <see cref="CardDef"/>s (cached). A card the engine can't
/// fully run becomes a blank: same name, types and cost, but it does nothing (CONTRACT.md §6).
/// </summary>
public sealed class CardPool
{
    private readonly ForgeLibrary _forge;
    private readonly CardIndex? _index;
    private readonly ConcurrentDictionary<string, CardDef> _compiled = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CardDef> _blank = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CardDef?> _tokens = new(StringComparer.Ordinal);

    public CardPool(ForgeLibrary forge, CardIndex? index)
    {
        _forge = forge;
        _index = index;
        // Subtypes in any card's or token's type line ("Serf" only appears on tokens).
        foreach (var c in forge.Cards.Concat(forge.Tokens.Values))
            foreach (var f in c.Faces)
                foreach (var w in f.Types.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    if (char.IsUpper(w[0]) && TypeLine.Parse(w) is { Types: 0, Supers: 0 }) Filter.KnownSubtypes.Add(w);
    }

    /// <summary>The full compile result, supported or not.</summary>
    public CardDef? Compile(string name)
    {
        var src = _forge.Find(name);
        if (src is null) return null;
        return _compiled.GetOrAdd(src.FullName, _ =>
        {
            var def = CardCompiler.Compile(src, Token);
            def.ColorIdentity = Identity(src.FullName);
            return def;
        });
    }

    /// <summary>A token script (TokenScript$ name) compiled, or null if Forge has no such script.</summary>
    public CardDef? Token(string script) =>
        _tokens.GetOrAdd(script, s => _forge.FindToken(s) is { } t ? CardCompiler.Compile(t, Token) : null);

    /// <summary>What a game uses: the compiled card if supported, else a blank stand-in.</summary>
    public CardDef? Resolve(string name)
    {
        var def = Compile(name);
        if (def is null) return null;
        if (def.Supported) return def;
        return _blank.GetOrAdd(def.Name, _ =>
        {
            var b = CardCompiler.Blank(def.Source);
            b.ColorIdentity = def.ColorIdentity;
            return b;
        });
    }

    private ColorMask Identity(string name)
    {
        ColorMask m = 0;
        if (_index?.Find(name) is { } card)
            foreach (var letter in card.ColorIdentity)
                if (letter.Length == 1 && ManaColors.FromLetter(letter[0]) is { } c) m |= c.Mask();
        return m;
    }
}
