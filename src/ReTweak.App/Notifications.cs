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
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch
        {
            _registered = false; // toasts unavailable; in-app alerts still work
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
