using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace MagicSim.ForgeWrapper;

/// <summary>
/// A local Forge desktop install plus a portable Java to run it (CONTRACT.md §3, "Forge playtest"). Forge is GPL-3.0:
/// `sim forge-setup` downloads a release from Forge's GitHub to each machine; nothing from it is in this repo.
/// Layout: app\ (the release), jdk\ (Java 17), user\ and cache\ (Forge's data, kept out of a normal Forge install).
/// </summary>
public sealed class ForgeApp
{
    public const string DefaultVersion = "2.0.15";
    public const string DirEnvVar = "MAGICSIM_FORGE_APP";

    public string Dir { get; }
    public string AppDir => Path.Combine(Dir, "app");
    public string Java { get; }
    public string Jar { get; }
    public string Version { get; }

    private ForgeApp(string dir, string java, string jar, string version) => (Dir, Java, Jar, Version) = (dir, java, jar, version);

    /// <summary>%MAGICSIM_FORGE_APP%, else %LOCALAPPDATA%\MagicSim\forge-app.</summary>
    public static string DefaultDir =>
        Environment.GetEnvironmentVariable(DirEnvVar) is { Length: > 0 } p
            ? p
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MagicSim", "forge-app");

    private static string VersionFile(string dir) => Path.Combine(dir, "version.txt");

    public static ForgeApp Load(string? dir = null)
    {
        dir = Path.GetFullPath(dir ?? DefaultDir);
        var missing = $"Forge isn't set up in {dir}. Run `sim forge-setup` first.";
        if (!File.Exists(VersionFile(dir))) throw new FileNotFoundException(missing);
        var java = FindJava(Path.Combine(dir, "jdk")) ?? throw new FileNotFoundException(missing);
        var jar = Directory.EnumerateFiles(Path.Combine(dir, "app"), "forge-gui-desktop-*-jar-with-dependencies.jar").FirstOrDefault()
                  ?? throw new FileNotFoundException(missing);
        return new ForgeApp(dir, java, jar, File.ReadAllText(VersionFile(dir)).Trim());
    }

    // ---- setup --------------------------------------------------------------------------

    public static string ForgeUrl(string version) =>
        $"https://github.com/Card-Forge/forge/releases/download/forge-{version}/forge-installer-{version}.tar.bz2";

    /// <summary>Microsoft's build of OpenJDK 17 for this machine.</summary>
    public static string JavaUrl()
    {
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "aarch64" : "x64";
        return OperatingSystem.IsWindows() ? $"https://aka.ms/download-jdk/microsoft-jdk-17-windows-{arch}.zip"
            : OperatingSystem.IsMacOS() ? $"https://aka.ms/download-jdk/microsoft-jdk-17-macos-{arch}.tar.gz"
            : $"https://aka.ms/download-jdk/microsoft-jdk-17-linux-{arch}.tar.gz";
    }

    /// <summary>Downloads what's missing: Java once, and Forge again only when the version changes.</summary>
    public static async Task<ForgeApp> SetupAsync(string dir, string version, TextWriter log)
    {
        dir = Path.GetFullPath(dir);
        Directory.CreateDirectory(dir);
        var downloads = Path.Combine(dir, "downloads");
        Directory.CreateDirectory(downloads);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MagicSim");

        var jdk = Path.Combine(dir, "jdk");
        if (FindJava(jdk) is null)
        {
            var url = JavaUrl();
            var archive = Path.Combine(downloads, url.EndsWith(".zip") ? "jdk.zip" : "jdk.tar.gz");
            await DownloadAsync(http, url, archive, log);
            if (Directory.Exists(jdk)) Directory.Delete(jdk, true);
            Directory.CreateDirectory(jdk);
            log.WriteLine("Unpacking Java ...");
            if (archive.EndsWith(".zip")) ZipFile.ExtractToDirectory(archive, jdk);
            else Tar(log, "-xzf", archive, "-C", jdk);
            File.Delete(archive);
        }

        var installed = File.Exists(VersionFile(dir)) ? File.ReadAllText(VersionFile(dir)).Trim() : null;
        var app = Path.Combine(dir, "app");
        if (installed != version || !Directory.Exists(app))
        {
            var archive = Path.Combine(downloads, $"forge-installer-{version}.tar.bz2");
            await DownloadAsync(http, ForgeUrl(version), archive, log);
            if (File.Exists(VersionFile(dir))) File.Delete(VersionFile(dir));
            if (Directory.Exists(app)) Directory.Delete(app, true);
            Directory.CreateDirectory(app);
            log.WriteLine("Unpacking Forge ...");
            Tar(log, "-xjf", archive, "-C", app);
            File.Delete(archive);
            File.WriteAllText(VersionFile(dir), version + "\n");
        }

        // Forge keeps its preferences, logs and image cache here instead of the user's normal Forge folders.
        string Slashes(string p) => p.Replace('\\', '/');
        File.WriteAllText(Path.Combine(app, "forge.profile.properties"),
            $"userDir={Slashes(Path.Combine(dir, "user"))}\ncacheDir={Slashes(Path.Combine(dir, "cache"))}\n");
        var forge = Load(dir);
        forge.EnsureInitialized(log);
        log.WriteLine($"Forge {forge.Version} is ready in {dir}");
        return forge;
    }

    /// <summary>
    /// Forge creates its data folders on its first start, and two first starts at once collide ("cannot create profile
    /// directory"). So the first start runs alone: Forge sets up, finds no deck, and exits.
    /// </summary>
    public void EnsureInitialized(TextWriter log)
    {
        if (Directory.Exists(Path.Combine(Dir, "cache", "db"))) return;
        log.WriteLine("Starting Forge once to set up its data folders ...");
        var psi = StartInfo("sim", "-d", "none.dck", "-D", AppDir.Replace('\\', '/') + "/", "-n", "1", "-q");
        psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        using var p = Process.Start(psi) ?? throw new IOException($"could not start {Java}");
        var stderr = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (!Directory.Exists(Path.Combine(Dir, "cache", "db")))
            throw new IOException($"Forge didn't start: {FirstError(stderr.Result.Split('\n'))}");
    }

    /// <summary>`java -jar forge.jar` with these arguments, run from the app folder so it finds its profile file.</summary>
    public ProcessStartInfo StartInfo(params string[] forgeArgs)
    {
        var psi = new ProcessStartInfo(Java) { WorkingDirectory = AppDir, UseShellExecute = false };
        foreach (var a in new[] { "-Xmx3072m", "-Dio.netty.tryReflectionSetAccessible=true", "-Dfile.encoding=UTF-8", "-jar", Jar })
            psi.ArgumentList.Add(a);
        foreach (var a in forgeArgs) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>The exception Java printed ("java.lang.RuntimeException: ..."), else the last lines.</summary>
    public static string FirstError(IEnumerable<string> stderr)
    {
        var lines = stderr.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("at ", StringComparison.Ordinal)).ToList();
        var exception = lines.FirstOrDefault(l => l.Contains("Exception") || l.Contains("Error:"));
        if (exception is not null) return exception.StartsWith("main > ", StringComparison.Ordinal) ? exception[7..] : exception;
        return lines.Count == 0 ? "no output" : string.Join(" | ", lines.TakeLast(3));
    }

    private static async Task DownloadAsync(HttpClient http, string url, string path, TextWriter log)
    {
        log.WriteLine($"Downloading {url} ...");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var tmp = path + ".part";
        await using (var file = File.Create(tmp))
            await response.Content.CopyToAsync(file);
        File.Move(tmp, path, overwrite: true);
        log.WriteLine($"  {new FileInfo(path).Length / 1e6:F0} MB");
    }

    private static string? FindJava(string jdk)
    {
        if (!Directory.Exists(jdk)) return null;
        var exe = OperatingSystem.IsWindows() ? "java.exe" : "java";
        return Directory.EnumerateFiles(jdk, exe, SearchOption.AllDirectories)
            .FirstOrDefault(p => Path.GetFileName(Path.GetDirectoryName(p)) == "bin");
    }

    private static void Tar(TextWriter log, params string[] args)
    {
        // Windows' own tar (bsdtar) reads .bz2 itself; a Git-for-Windows tar earlier on the PATH may not.
        var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
        var psi = new ProcessStartInfo(OperatingSystem.IsWindows() && File.Exists(system) ? system : "tar")
            { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new IOException("could not start tar (is it on the PATH?)");
        var stderr = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new IOException($"tar {string.Join(' ', args)} failed: {stderr.Result.Trim()}");
    }
}
