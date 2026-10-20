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
    /// <summary>Diagnostic log level (Off by default); files go to %LocalAppData%\ReTweak\diagnostics.</summary>
    public DiagLevel DiagnosticsLevel { get; init; } = DiagLevel.Off;
    public string LogFolder { get; init; } = Path.Combine(Folder, "logs");

    /// <summary>What <see cref="Load"/> had to replace with defaults, for the diagnostic log (it runs before logging starts).</summary>
    public static IReadOnlyList<string> LoadProblems { get; private set; } = [];

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return Checked(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new());
        }
        catch (Exception ex) { LoadProblems = [$"settings.json unreadable, using defaults: {ex.Message}"]; } // rather than refuse to monitor
        return new();
    }

    /// <summary>
    /// The same checks the Settings dialog applies, on what was read from disk. settings.json can be edited
    /// (or damaged) outside ReTweak, and the elevated app drives the fans from it: a failsafe at 1000 °C or
    /// an empty curve must not be trusted. A section that fails falls back to its defaults.
    /// </summary>
    internal static AppSettings Checked(AppSettings s)
    {
        var problems = new List<string>();
        var fans = s.Fans;
        string? fanProblem;
        try { fanProblem = fans is null || fans.Curve is null || fans.Activities is null ? "missing" : fans.Validate(); }
        catch (Exception ex) { fanProblem = ex.Message; }
        if (fanProblem is not null)
        {
            problems.Add($"fan settings reset to defaults: {fanProblem}");
            fans = new FanOptions();
        }
        var alerts = s.Alerts;
        string? alertProblem = alerts is null ? "missing" : Validate(alerts);
        if (alertProblem is not null)
        {
            problems.Add($"alert thresholds reset to defaults: {alertProblem}");
            alerts = new AlertOptions();
        }
        string logFolder = string.IsNullOrWhiteSpace(s.LogFolder) ? new AppSettings().LogFolder : s.LogFolder;
        LoadProblems = problems;
        return s with { Fans = fans ?? new(), Alerts = alerts ?? new(), LogFolder = logFolder };
    }

    public void Save()
    {
        // Atomic replace (a crash mid-save can't corrupt settings), and safe when elevated: no following links.
        TrustedPaths.WriteFileSafely(FilePath, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>Validation for user-entered thresholds; null when valid.</summary>
    public static string? Validate(AlertOptions o)
    {
        if (o.PinWarnAmps <= 0 || o.PinCriticalAmps <= o.PinWarnAmps) return "Pin current: warning must be above 0 and below critical.";
        if (o.CriticalLowVolts <= 0 || o.WarnLowVolts <= o.CriticalLowVolts) return "Low voltage: warning must be above critical.";
        if (o.WarnHighVolts <= o.WarnLowVolts) return "High-voltage warning must be above the low-voltage warning.";
        if (o.WarnSpreadVolts <= 0 || o.CriticalSpreadVolts <= o.WarnSpreadVolts) return "Voltage spread: warning must be above 0 and below critical.";
        if (o.WarnImbalance <= 1 || o.CriticalImbalance <= o.WarnImbalance) return "Imbalance: warning must be above x1.00 and below critical.";
        foreach (var (name, w, c) in new[] { ("Core", o.WarnCoreTempC, o.CriticalCoreTempC), ("Memory", o.WarnMemoryTempC, o.CriticalMemoryTempC),
                                         ("Hotspot", o.WarnHotspotTempC, o.CriticalHotspotTempC) })
            if (w < 30 || c <= w || c > 125) return $"{name} temperature: warning must be at least 30 °C and below critical (max 125 °C).";
        if (o.WarnDieSpreadC < 10 || o.CriticalDieSpreadC <= o.WarnDieSpreadC || o.CriticalDieSpreadC > 100)
            return "Die spread: warning must be at least 10 °C and below critical (max 100 °C).";
        return null;
    }
}

public enum AutostartState
{
    Off,
    /// <summary>Logon task registered for this exe.</summary>
    On,
    /// <summary>Logon task points at a different exe (the app was moved or rebuilt elsewhere).</summary>
    Stale,
    /// <summary>Only the old Run-key entry exists: starts with Windows, but without administrator rights.</summary>
    Legacy,
}

/// <summary>
/// Start with Windows through a per-user logon task with highest privileges, so ReTweak starts
/// elevated (GPU hotspot, fan control) without a UAC prompt at every logon. Creating the task
/// needs administrator rights once. Earlier versions used the HKCU Run key, which can't start
/// an elevated app; an entry left there is migrated to the task.
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ReTweak";
    private const string Arguments = "--minimized";
    private static readonly LogonTask LogonTask = new("ReTweak");

    private static string Exe => Environment.ProcessPath!;
    /// <summary>This exe is under Program Files, where only administrators can change it.</summary>
    public static bool IsInstalledCopy => TrustedPaths.IsUnder(Exe, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
    private static string RunKeyCommand => $"\"{Exe}\" {Arguments}";

    public static bool IsAdmin => ReTweak.Core.LogonTask.IsElevated;

    /// <summary>Runs schtasks.exe, so call it once per use rather than per property read.</summary>
    public static AutostartState State
    {
        get
        {
            if (LogonTask.Query() is { } t)
                return string.Equals(t.Command, Exe, StringComparison.OrdinalIgnoreCase) && t.Arguments == Arguments
                    ? AutostartState.On : AutostartState.Stale;
            return RunKeyValue is { Length: > 0 } ? AutostartState.Legacy : AutostartState.Off;
        }
    }

    private static string? RunKeyValue
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) as string;
        }
    }

    /// <summary>
    /// Registers (pointing at this exe) or removes the logon task, and drops any old Run-key entry.
    /// When unelevated, <paramref name="elevate"/> lets schtasks.exe ask for administrator rights (UAC).
    /// Blocks until done, including while the UAC prompt is up. Returns null on success, else the reason.
    /// </summary>
    public static string? Set(bool enabled, bool elevate)
    {
        string? error;
        bool ok = enabled ? LogonTask.Register(Exe, Arguments, elevate, out error) : LogonTask.Remove(elevate, out error);
        if (ok || !enabled) RemoveRunKey(); // keep the old entry until the task replaces it
        return ok ? null : error;
    }

    /// <summary>
    /// Started elevated with only an old Run-key entry for this exe: switch it to the logon task
    /// without asking, since the user already chose to start with Windows. A Run-key entry for
    /// another exe, or a stale task, is left for the Settings dialog to fix when the user saves.
    /// </summary>
    public static void MigrateIfElevated()
    {
        if (!IsAdmin) return;
        try
        {
            if (string.Equals(RunKeyValue, RunKeyCommand, StringComparison.OrdinalIgnoreCase) && LogonTask.Query() is null)
                Set(true, elevate: false);
            // Installed to Program Files while the task still starts a copy elsewhere (the build folder): move it here.
            // Only toward a protected copy, never away from one, so running a dev build doesn't undo an install.
            else if (LogonTask.Query() is { } task && !string.Equals(task.Command, Exe, StringComparison.OrdinalIgnoreCase) && IsInstalledCopy)
            {
                string? why = Set(true, elevate: false);
                Diag.Info(DiagCategory.App, why is null ? "Start with Windows moved to the installed copy" : "couldn't move Start with Windows to the installed copy",
                          new { from = task.Command, to = Exe, why });
            }
        }
        catch (Exception) { /* best effort: the Run-key entry keeps working until the next try */ }
    }

    private static void RemoveRunKey()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
