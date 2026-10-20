using System.Text.Json;
using Microsoft.Win32;
using ReTweak.Core;

namespace ReTweak.App;

/// <summary>User settings, stored as JSON in %LocalAppData%\ReTweak\settings.json.</summary>
public sealed record AppSettings
{
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReTweak");
    private static string FilePath => Path.Combine(Folder, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public AlertOptions Alerts { get; init; } = new();
    public FanOptions Fans { get; init; } = new();
    /// <summary>Closing the window keeps ReTweak monitoring in the tray.</summary>
    public bool CloseToTray { get; init; } = true;
    public bool Toasts { get; init; } = true;
    public bool LogConnector { get; init; }
    public string LogFolder { get; init; } = Path.Combine(Folder, "logs");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (Exception) { /* corrupt or unreadable: fall back to defaults rather than refuse to monitor */ }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, overwrite: true); // atomic replace: a crash mid-save can't corrupt settings
    }

    /// <summary>Validation for user-entered thresholds; null when valid.</summary>
    public static string? Validate(AlertOptions o)
    {
        if (o.PinWarnAmps <= 0 || o.PinCriticalAmps <= o.PinWarnAmps) return "Pin current: warning must be above 0 and below critical.";
        if (o.CriticalLowVolts <= 0 || o.WarnLowVolts <= o.CriticalLowVolts) return "Low voltage: warning must be above critical.";
        if (o.WarnHighVolts <= o.WarnLowVolts) return "High-voltage warning must be above the low-voltage warning.";
        if (o.WarnSpreadVolts <= 0 || o.CriticalSpreadVolts <= o.WarnSpreadVolts) return "Voltage spread: warning must be above 0 and below critical.";
        if (o.WarnImbalance <= 1 || o.CriticalImbalance <= o.WarnImbalance) return "Imbalance: warning must be above x1.00 and below critical.";
        foreach (var (name, w, c) in new[] { ("Core", o.WarnCoreTempC, o.CriticalCoreTempC), ("Memory", o.WarnMemoryTempC, o.CriticalMemoryTempC) })
            if (w < 30 || c <= w || c > 125) return $"{name} temperature: warning must be at least 30 °C and below critical (max 125 °C).";
        return null;
    }
}

/// <summary>Start with Windows via the per-user Run key (no admin rights needed).</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ReTweak";

    private static string Command => $"\"{Environment.ProcessPath}\" --minimized";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
    }

    /// <summary>True if the registered command points at a different exe (e.g. the app was moved or rebuilt elsewhere).</summary>
    public static bool IsStale
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s && !string.Equals(s, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
