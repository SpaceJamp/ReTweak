using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using ReTweak.Core;

namespace ReTweak.App;

/// <summary>Native Windows toast notifications for alerts.</summary>
internal static class Notifications
{
    private static bool _registered;
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

    /// <summary>Shows a toast for warnings and criticals, at most once per alert per cooldown.</summary>
    public static void Show(Alert a)
    {
        if (!_registered || a.Severity < AlertSeverity.Warning) return;
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
