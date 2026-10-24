using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using ReTweak.Core;

namespace ReTweak.App;

/// <summary>Native Windows toast notifications for alerts.</summary>
internal static class Notifications
{
    private static bool _registered;

    /// <summary>User setting; in-app alerts are shown either way.</summary>
    public static bool Enabled { get; set; } = true;
    private static readonly Dictionary<string, DateTime> _lastShown = [];
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);

    public static void Register()
    {
        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "notification.png");
            AppNotificationManager.Default.Register("ReTweak", new Uri(iconPath));
            UseCurrentIcon(iconPath);
            _registered = true;
        }
        catch (Exception ex)
        {
            _registered = false; // toasts unavailable; in-app alerts still work
            Diag.Error(DiagCategory.App, "notifications couldn't register; no toasts this session", ex);
        }
    }

    /// <summary>
    /// Windows App SDK copies the icon once, at the first registration, into its own cache and never refreshes it, even
    /// when Register is given a new icon; the taskbar and toasts kept showing the old bolt. Point the registration at our
    /// icon instead. It lives under HKCU\Software\Classes\AppUserModelId: the exe's path (\ as .) names a NotificationGUID,
    /// whose key holds IconUri. Our icon sits in the install folder, which only administrators can change.
    /// </summary>
    private static void UseCurrentIcon(string iconPath)
    {
        try
        {
            string root = @"Software\Classes\AppUserModelId\";
            using var byExe = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(root + Environment.ProcessPath!.Replace('\\', '.'));
            if (byExe?.GetValue("NotificationGUID") is not string guid) return;
            using var reg = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(root + guid, writable: true);
            if (reg is null || string.Equals(reg.GetValue("IconUri") as string, iconPath, StringComparison.OrdinalIgnoreCase)) return;
            reg.SetValue("IconUri", iconPath);
            Diag.Info(DiagCategory.App, "notification icon updated", new { iconPath });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Diag.Warn(DiagCategory.App, "couldn't update the notification icon", new { ex.Message });
        }
    }

    public static void Unregister()
    {
        if (_registered) AppNotificationManager.Default.Unregister();
    }

    /// <summary>A plain informational toast (e.g. "still running in the tray").</summary>
    public static void ShowInfo(string title, string text)
    {
        if (!Enabled || !_registered) return;
        try { AppNotificationManager.Default.Show(new AppNotificationBuilder().AddText(title).AddText(text).BuildNotification()); }
        catch { }
    }

    /// <summary>Shows a toast for warnings and criticals, at most once per alert per cooldown.</summary>
    public static void Show(Alert a)
    {
        if (!Enabled || !_registered || a.Severity < AlertSeverity.Warning) return;
        string key = $"{a.Key}:{a.Severity}";
        if (_lastShown.TryGetValue(key, out var last) && DateTime.Now - last < Cooldown) return;
        _lastShown[key] = DateTime.Now;

        var builder = new AppNotificationBuilder()
            .AddText(a.Severity == AlertSeverity.Critical ? $"⚠ {a.Title}" : a.Title)
            .AddText(a.Detail);
        if (a.Severity == AlertSeverity.Critical)
            builder.SetScenario(AppNotificationScenario.Urgent);
        try { AppNotificationManager.Default.Show(builder.BuildNotification()); }
        catch { /* never let a toast failure break monitoring */ }
    }
}
