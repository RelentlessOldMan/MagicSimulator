using System.Diagnostics;

namespace MagicSim.Cli;

/// <summary>
/// `--low` (or MAGICSIM_LOW=1): idle priority on a single core, so long runs leave the rest of the machine alone. The
/// garbage collector and Forge's Java processes stay on that core too, since child processes inherit the affinity.
/// </summary>
public static class LowPriority
{
    public static bool Active { get; private set; }

    public static bool Requested(Options o) =>
        o.Has("--low") || Environment.GetEnvironmentVariable("MAGICSIM_LOW") is "1" or "true";

    /// <summary>The core used: the last one, which the OS tends to schedule other work on least.</summary>
    public static nint CoreMask => (nint)1 << (Environment.ProcessorCount - 1);

    public static void Apply()
    {
        Active = true;
        using var self = Process.GetCurrentProcess();
        try
        {
            self.PriorityClass = ProcessPriorityClass.Idle;
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) self.ProcessorAffinity = CoreMask;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            Console.Error.WriteLine($"sim: --low: couldn't lower priority or pin to one core ({e.Message}); running with one thread anyway");
        }
    }
}
