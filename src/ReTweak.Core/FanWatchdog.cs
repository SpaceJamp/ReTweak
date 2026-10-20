using System.Diagnostics;
using ReTweak.Core.Interop;

namespace ReTweak.Core;

/// <summary>
/// A manual fan speed outlives the process that set it. ReTweak hands the fans back when it exits normally,
/// but not when it's killed (Task Manager, a native crash): then they would stay at the last speed until the
/// driver resets. The elevated app starts a small copy of itself in watchdog mode, which waits for it to exit
/// and, if it didn't exit cleanly, returns every fan on every NVIDIA GPU to the driver's automatic curve.
/// </summary>
public static class FanWatchdog
{
    public const string Arg = "--fan-watchdog";

    /// <summary>Starts the watchdog for this process. Best effort: false if it couldn't be started.</summary>
    public static bool Start()
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add(Arg);
            psi.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var p = Process.Start(psi);
            return p is not null;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Watchdog mode: waits for <paramref name="pid"/>; returns the fans to the driver if it didn't exit cleanly.</summary>
    public static int Run(string pid)
    {
        if (!int.TryParse(pid, out int id)) return 2;
        int exitCode;
        try
        {
            using var parent = Process.GetProcessById(id);
            // Only watch ReTweak itself (same program file), not whatever process a stray argument names.
            if (!string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return 3;
            parent.WaitForExit();
            exitCode = parent.ExitCode;
        }
        catch (ArgumentException) { return 0; }      // already gone before we started watching: nothing known to undo
        catch (InvalidOperationException) { exitCode = -1; } // exited while we looked: treat as not clean
        if (exitCode == 0) return 0;                  // a clean exit already handed the fans back
        int restored = RestoreAllFans();
        return restored >= 0 ? 0 : 1;
    }

    /// <summary>Every fan on every NVIDIA GPU back to the driver's curve. Returns how many, or -1 if NVML failed.</summary>
    public static int RestoreAllFans()
    {
        if (Nvml.Init() != Nvml.Success) return -1;
        try
        {
            int restored = 0;
            if (Nvml.DeviceGetCount(out uint count) != Nvml.Success) return -1;
            for (uint i = 0; i < count; i++)
            {
                if (Nvml.DeviceGetHandleByIndex(i, out var dev) != Nvml.Success) continue;
                if (Nvml.DeviceGetNumFans(dev, out uint fans) != Nvml.Success) continue;
                for (uint f = 0; f < fans; f++)
                    if (Nvml.DeviceSetDefaultFanSpeed(dev, f) == Nvml.Success) restored++;
            }
            return restored;
        }
        finally { Nvml.Shutdown(); }
    }
}
