using System.Runtime.InteropServices;
using ReTweak.Core;

namespace ReTweak.App.Native;

/// <summary>
/// Notification-area icon via Shell_NotifyIcon (WinUI 3 has no tray API). Owns a hidden
/// top-level window, created on the UI thread, that receives the icon's mouse messages,
/// Explorer restarts (TaskbarCreated) and "show yourself" requests from a second instance.
/// </summary>
internal sealed partial class TrayIcon : IDisposable
{
    public const string WindowClass = "ReTweak.TrayWindow";
    /// <summary>Posted by a second instance to bring the running one to the front.</summary>
    public const uint WmShowRequest = WmApp + 2;
    /// <summary>Sent by an elevated copy taking over from this (unelevated) one.</summary>
    public const uint WmExitRequest = WmApp + 3;

    private const uint WmApp = 0x8000;
    private const uint WmTrayCallback = WmApp + 1;
    private const uint WmLButtonUp = 0x0202, WmLButtonDblClk = 0x0203, WmRButtonUp = 0x0205, WmNull = 0x0000;
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4;
    private const uint MfString = 0x0, MfSeparator = 0x800;
    private const uint TpmRightButton = 0x2, TpmReturnCmd = 0x100;
    private const int IdiApplication = 32512, IdiError = 32513, IdiWarning = 32515;
    private const int CmdOpen = 1, CmdExit = 2;

    private static readonly uint WmTaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    // Kept in a field so the GC can't collect the delegate while Windows still calls it.
    private readonly WndProcDelegate _wndProc;
    private readonly nint _hwnd;
    private nint _icon;
    // ReTweak's own tray icons (Assets/tray*.ico); 0 falls back to the stock Windows icons.
    private readonly nint _iconNormal, _iconWarning, _iconCritical;
    private string _tip = "ReTweak";
    private bool _added, _disposed;

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        _wndProc = WndProc;
        var wc = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = WindowClass,
        };
        RegisterClassEx(ref wc);
        // A real (never shown) top-level window, not HWND_MESSAGE: message-only windows
        // don't receive the TaskbarCreated broadcast, and the icon would vanish after an Explorer restart.
        _hwnd = CreateWindowEx(0, WindowClass, "ReTweak", 0, 0, 0, 0, 0, 0, 0, wc.hInstance, 0);
        // When elevated, still let a normal copy ask us to show the window (UIPI blocks it otherwise).
        ChangeWindowMessageFilterEx(_hwnd, WmShowRequest, MsgFltAllow, 0);
        _iconNormal = LoadTrayIcon("tray.ico");
        _iconWarning = LoadTrayIcon("tray-warning.ico");
        _iconCritical = LoadTrayIcon("tray-critical.ico");
        _icon = _iconNormal != 0 ? _iconNormal : LoadIcon(0, IdiApplication);
        Add();
    }

    /// <summary>Loads an .ico from Assets at the notification area's size (which depends on DPI).</summary>
    private static nint LoadTrayIcon(string file)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", file);
        if (!File.Exists(path)) return 0;
        int size = GetSystemMetrics(SmCxSmIcon);
        return LoadImage(0, path, ImageIcon, size, size, LrLoadFromFile);
    }

    /// <summary>Updates the icon and tooltip to reflect the worst active alert.</summary>
    public void SetStatus(AlertSeverity worst, string tooltip)
    {
        _icon = worst switch
        {
            AlertSeverity.Critical => _iconCritical != 0 ? _iconCritical : LoadIcon(0, IdiError),
            AlertSeverity.Warning => _iconWarning != 0 ? _iconWarning : LoadIcon(0, IdiWarning),
            _ => _iconNormal != 0 ? _iconNormal : LoadIcon(0, IdiApplication),
        };
        _tip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        Notify(NimModify);
    }

    private void Add()
    {
        _added = Notify(NimAdd);
    }

    private bool Notify(uint message)
    {
        if (_disposed) return false;
        var data = new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = WmTrayCallback,
            hIcon = _icon,
            szTip = _tip,
        };
        return Shell_NotifyIcon(message, ref data);
    }

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WmTrayCallback)
        {
            uint mouse = (uint)(lParam & 0xFFFF);
            if (mouse is WmLButtonUp or WmLButtonDblClk) OpenRequested?.Invoke();
            else if (mouse == WmRButtonUp) ShowMenu();
            return 0;
        }
        if (msg == WmShowRequest)
        {
            OpenRequested?.Invoke();
            return 0;
        }
        if (msg == WmExitRequest)
        {
            ExitRequested?.Invoke();
            return 0;
        }
        if (msg == WmTaskbarCreated && WmTaskbarCreated != 0)
        {
            Add(); // Explorer restarted: the shell forgot our icon
            return 0;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, MfString, CmdOpen, "Open ReTweak");
        AppendMenu(menu, MfSeparator, 0, null);
        AppendMenu(menu, MfString, CmdExit, "Exit (stop monitoring)");
        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd); // required so the menu closes when clicking elsewhere
        int cmd = TrackPopupMenu(menu, TpmRightButton | TpmReturnCmd, pt.X, pt.Y, 0, _hwnd, 0);
        PostMessage(_hwnd, WmNull, 0, 0);
        DestroyMenu(menu);
        if (cmd == CmdOpen) OpenRequested?.Invoke();
        else if (cmd == CmdExit) ExitRequested?.Invoke();
    }

    /// <summary>Asks an already-running instance to show its window. Returns false if none was found.</summary>
    public static bool SignalRunningInstance()
    {
        var hwnd = FindWindow(WindowClass, null);
        return hwnd != 0 && PostMessage(hwnd, WmShowRequest, 0, 0);
    }

    /// <summary>Asks an already-running instance to exit. Returns false if none was found.</summary>
    public static bool AskRunningInstanceToExit()
    {
        var hwnd = FindWindow(WindowClass, null);
        return hwnd != 0 && PostMessage(hwnd, WmExitRequest, 0, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_added) Notify(NimDelete);
        _disposed = true;
        DestroyWindow(_hwnd);
        foreach (var icon in new[] { _iconNormal, _iconWarning, _iconCritical })
            if (icon != 0) DestroyIcon(icon); // LoadImage(LR_LOADFROMFILE) icons are ours to free
    }

    // ---- Win32 ----

    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize, style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WndClassEx wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h,
                                              nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    // Must be the W variant: the class is registered as Unicode, and DefWindowProcA would mangle text messages.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string cls, string? name);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool ChangeWindowMessageFilterEx(nint hwnd, uint msg, uint action, nint changeInfo);
    private const uint MsgFltAllow = 1;
    [DllImport("user32.dll")] private static extern nint LoadIcon(nint instance, nint name);
    private const uint ImageIcon = 1, LrLoadFromFile = 0x10;
    private const int SmCxSmIcon = 49;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadImage(nint instance, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nint id, string? text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point pt);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
}
