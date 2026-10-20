using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ReTweak.Core.Interop;

/// <summary>The program in front: its process name and whether its window fills its monitor.</summary>
public readonly record struct ForegroundApp(string? ProcessName, bool Fullscreen)
{
    public static readonly ForegroundApp None = new(null, false);
}

/// <summary>
/// Reads the foreground window for activity detection. Local only: the name is used to tell a game
/// from a render and never leaves the PC (it does appear in the diagnostic log when that's on).
/// </summary>
public static unsafe partial class Foreground
{
    private static int _cachedPid;
    private static string? _cachedName;

    public static ForegroundApp Read()
    {
        try
        {
            nint hwnd = GetForegroundWindow();
            if (hwnd == 0) return ForegroundApp.None;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == Environment.ProcessId) return ForegroundApp.None;
            if (pid != _cachedPid)
            {
                using var p = Process.GetProcessById((int)pid);
                (_cachedPid, _cachedName) = ((int)pid, p.ProcessName);
            }
            return new ForegroundApp(_cachedName, IsFullscreen(hwnd));
        }
        catch (Exception) { return ForegroundApp.None; } // process exited, access denied: not worth failing a tick
    }

    private static bool IsFullscreen(nint hwnd)
    {
        // The desktop and taskbar cover the screen too but aren't an app.
        char* buf = stackalloc char[64];
        int n = GetClassNameW(hwnd, buf, 64);
        var name = new ReadOnlySpan<char>(buf, Math.Max(0, n));
        if (name.SequenceEqual("Progman") || name.SequenceEqual("WorkerW") || name.SequenceEqual("Shell_TrayWnd")) return false;

        if (!GetWindowRect(hwnd, out var r)) return false;
        nint monitor = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info)) return false;
        var m = info.Monitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }

    [LibraryImport("user32.dll")] private static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")] private static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetWindowRect(nint hwnd, out Rect rect);
    [LibraryImport("user32.dll")] private static partial nint MonitorFromWindow(nint hwnd, uint flags);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [LibraryImport("user32.dll")] private static partial int GetClassNameW(nint hwnd, char* name, int max);
}
