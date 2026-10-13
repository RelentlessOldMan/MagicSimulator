namespace MagicSim.Cli;

public sealed class UsageException(string message) : Exception(message);

/// <summary>Minimal "--name value" / positional parser; repeatable options keep every value. Switches take no value.</summary>
public sealed class Options
{
    private static readonly HashSet<string> Switches = new() { "--low" };

    public List<string> Positional { get; } = new();
    private readonly Dictionary<string, List<string>> _named = new();
    private readonly HashSet<string> _switches = new();

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (Switches.Contains(a)) o._switches.Add(a);
            else if (a.StartsWith("--"))
            {
                if (i + 1 >= args.Length) throw new UsageException($"{a} needs a value");
                if (!o._named.TryGetValue(a, out var list)) o._named[a] = list = new();
                list.Add(args[++i]);
            }
            else o.Positional.Add(a);
        }
        return o;
    }

    public bool Has(string name) => _switches.Contains(name);

    public string? Get(string name) => _named.TryGetValue(name, out var v) ? v[^1] : null;

    public IReadOnlyList<string> GetAll(string name) =>
        _named.TryGetValue(name, out var v) ? v : Array.Empty<string>();

    public List<string> RequirePositional(int count)
    {
        if (Positional.Count != count)
            throw new UsageException($"expected {count} argument(s), got {Positional.Count}");
        return Positional;
    }
}
