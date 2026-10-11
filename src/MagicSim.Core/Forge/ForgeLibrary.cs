using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MagicSim.Core.Cards;

namespace MagicSim.Core.Forge;

/// <summary>
/// Forge card scripts, read from a local sparse clone of the Forge repo (GPL-3.0; never
/// bundled — CONTRACT.md §6). `sim sync-forge` clones or updates it and builds a compact
/// gzip JSONL index ({"file", "script"} per card) next to it.
/// </summary>
public sealed class ForgeLibrary
{
    public const string RepoUrl = "https://github.com/Card-Forge/forge.git";
    public const string CardsSubdir = "forge-gui/res/cardsfolder";
    public const string TokensSubdir = "forge-gui/res/tokenscripts";
    /// <summary>Index entries for token scripts have this file prefix.</summary>
    public const string TokenPrefix = "tokenscripts/";
    public const string DirEnvVar = "MAGICSIM_FORGE";

    private readonly Dictionary<string, ForgeCard> _byName = new();
    public List<ForgeCard> Cards { get; } = new();
    /// <summary>Token scripts by script name ("c_a_treasure_sac"), as used by TokenScript$.</summary>
    public Dictionary<string, ForgeCard> Tokens { get; } = new(StringComparer.Ordinal);

    /// <summary>%MAGICSIM_FORGE%, else %LOCALAPPDATA%\MagicSim\forge.</summary>
    public static string DefaultDir =>
        Environment.GetEnvironmentVariable(DirEnvVar) is { Length: > 0 } p
            ? p
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MagicSim", "forge");

    public static string IndexPath(string dir) => Path.Combine(dir, "forge-cards.jsonl.gz");

    public static ForgeLibrary Load(string? dir = null)
    {
        var path = IndexPath(dir ?? DefaultDir);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Forge card scripts not found at {path}. Run `sim sync-forge` first.", path);
        var lib = new ForgeLibrary();
        using var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            var e = JsonSerializer.Deserialize<IndexEntry>(line)!;
            if (e.file.StartsWith(TokenPrefix, StringComparison.Ordinal))
                lib.Tokens[Path.GetFileNameWithoutExtension(e.file)] = ForgeCard.Parse(e.script, e.file);
            else lib.Add(ForgeCard.Parse(e.script, e.file));
        }
        return lib;
    }

    public static ForgeLibrary FromScripts(IEnumerable<string> scripts, IReadOnlyDictionary<string, string>? tokens = null)
    {
        var lib = new ForgeLibrary();
        foreach (var s in scripts) lib.Add(ForgeCard.Parse(s));
        if (tokens is not null)
            foreach (var (name, script) in tokens) lib.Tokens[name] = ForgeCard.Parse(script, TokenPrefix + name + ".txt");
        return lib;
    }

    public ForgeCard? FindToken(string script) => Tokens.TryGetValue(script, out var t) ? t : null;

    private void Add(ForgeCard card)
    {
        Cards.Add(card);
        AddKey(card.FullName, card);
        foreach (var f in card.Faces) AddKey(f.Name, card);
    }

    private void AddKey(string name, ForgeCard card)
    {
        if (name.Length == 0) return;
        // Front-face name of a real card wins over e.g. an Alchemy "A-" rebalance or a back face.
        _byName.TryAdd(CardIndex.Normalize(name), card);
    }

    public ForgeCard? Find(string name) =>
        _byName.TryGetValue(CardIndex.Normalize(name), out var c) ? c : null;

    // ---- sync ---------------------------------------------------------------------------

    private sealed record IndexEntry(string file, string script);

    public static int Sync(string dir, TextWriter log)
    {
        var git = Path.Combine(dir, ".git");
        if (!Directory.Exists(git))
        {
            Directory.CreateDirectory(dir);
            log.WriteLine($"Cloning Forge card scripts into {dir} (sparse, ~110 MB) ...");
            Git(log, Path.GetDirectoryName(Path.GetFullPath(dir))!,
                "clone", "--depth", "1", "--filter=blob:none", "--sparse", RepoUrl, Path.GetFullPath(dir));
            Git(log, dir, "sparse-checkout", "set", CardsSubdir, TokensSubdir);
        }
        else
        {
            log.WriteLine($"Updating Forge card scripts in {dir} ...");
            Git(log, dir, "sparse-checkout", "set", CardsSubdir, TokensSubdir);
            Git(log, dir, "fetch", "--depth", "1", "origin");
            Git(log, dir, "reset", "--hard", "FETCH_HEAD");
        }

        var root = Path.Combine(dir, CardsSubdir);
        var tmp = IndexPath(dir) + ".tmp";
        var count = 0;
        using (var file = File.Create(tmp))
        using (var gz = new GZipStream(file, CompressionLevel.Optimal))
        using (var w = new StreamWriter(gz, new UTF8Encoding(false)) { NewLine = "\n" })
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.txt", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
                w.WriteLine(JsonSerializer.Serialize(new IndexEntry(rel, File.ReadAllText(path))));
                count++;
            }
            foreach (var path in Directory.EnumerateFiles(Path.Combine(dir, TokensSubdir), "*.txt").Order(StringComparer.Ordinal))
                w.WriteLine(JsonSerializer.Serialize(new IndexEntry(TokenPrefix + Path.GetFileName(path), File.ReadAllText(path))));
        }
        File.Move(tmp, IndexPath(dir), overwrite: true);
        log.WriteLine($"Indexed {count} Forge card scripts into {IndexPath(dir)}");
        return count;
    }

    private static void Git(TextWriter log, string workDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = workDir, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new IOException("could not start git (is it on the PATH?)");
        var stderr = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new IOException($"git {string.Join(' ', args)} failed: {stderr.Result.Trim()}");
    }
}
