using System.Diagnostics;
using System.Runtime.InteropServices;
using ReTweak.Core.Interop;

namespace ReTweak.Core;

/// <summary>
/// A manual fan speed outlives the process that set it. ReTweak hands the fans back when it exits normally,
/// but not when it's killed (Task Manager, a native crash): then they would stay at the last speed until the
/// driver resets. The elevated app starts a small copy of itself in watchdog mode, which waits for it to exit
/// and, if it didn't exit cleanly, returns every fan on every NVIDIA GPU to the driver's automatic curve.
/// </summary>
public static partial class FanWatchdog
{
    /// <summary>A hang this long (the UI thread, which also runs the fan ticks, not answering) hands the fans back.</summary>
    public static readonly TimeSpan HangAfter = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(5);
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

    /// <summary>
    /// Watchdog mode: waits for <paramref name="pid"/>; returns the fans to the driver if it didn't exit cleanly, or
    /// while it hangs. <paramref name="uiWindowClass"/> names a window on its UI thread, pinged to tell a hang.
    /// </summary>
    public static int Run(string pid, string? uiWindowClass = null)
    {
        if (!int.TryParse(pid, out int id)) return 2;
        int exitCode;
        try
        {
            using var parent = Process.GetProcessById(id);
            // Only watch ReTweak itself (same program file), not whatever process a stray argument names.
            if (!string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return 3;
            // A frozen app keeps its last manual speed: after HangAfter without an answer, give the fans to the driver
            // once. If it recovers, its next tick takes them back (it rewrites a speed someone else changed).
            int hungChecks = 0;
            bool handedBack = false;
            while (!parent.WaitForExit(CheckEvery))
            {
                bool hung = uiWindowClass is not null && IsHung(id, uiWindowClass);
                hungChecks = hung ? hungChecks + 1 : 0;
                if (hung && !handedBack && hungChecks * CheckEvery >= HangAfter) handedBack = RestoreAllFans() >= 0;
                if (!hung) handedBack = false;
            }
            exitCode = parent.ExitCode;
        }
        catch (ArgumentException) { return 0; }      // already gone before we started watching: nothing known to undo
        catch (InvalidOperationException) { exitCode = -1; } // exited while we looked: treat as not clean
        if (exitCode == 0) return 0;                  // a clean exit already handed the fans back
        int restored = RestoreAllFans();
        return restored >= 0 ? 0 : 1;
    }

    /// <summary>True if <paramref name="pid"/>'s window of this class doesn't answer within 2 s. False if there's none to ask.</summary>
    private static bool IsHung(int pid, string windowClass)
    {
        for (nint w = FindWindowExW(0, 0, windowClass, null); w != 0; w = FindWindowExW(0, w, windowClass, null))
        {
            GetWindowThreadProcessId(w, out uint owner);
            if (owner != pid) continue;
            return SendMessageTimeoutW(w, 0 /* WM_NULL */, 0, 0, 0x2 /* SMTO_ABORTIFHUNG */, 2000, out _) == 0;
        }
        return false;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowExW(nint parent, nint after, string cls, string? title);
    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [LibraryImport("user32.dll")]
    private static partial nint SendMessageTimeoutW(nint hwnd, uint msg, nint wParam, nint lParam, uint flags, uint timeoutMs, out nint result);

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
