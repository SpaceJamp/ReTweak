using System.Text.Json;

namespace ReTweak.Core;

/// <summary>User settings, stored as JSON in %LocalAppData%\ReTweak\settings.json (~/.local/share/ReTweak on Linux).</summary>
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
    /// <summary>
    /// Power limit in % of the card's default (100 = leave it at the default). Less power means less heat for the
    /// fans to move, for a few % of performance. Applied when ReTweak runs as administrator; clamped to what the
    /// driver allows. The driver resets it at reboot and ReTweak applies it again when it starts.
    /// </summary>
    public int PowerLimitPercent { get; init; } = 100;
    /// <summary>The range accepted on load and in the Settings dialog (the driver's own range applies on top).</summary>
    public const int PowerLimitMinPercent = 50, PowerLimitMaxPercent = 150;

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
        int powerLimit = s.PowerLimitPercent;
        if (powerLimit < PowerLimitMinPercent || powerLimit > PowerLimitMaxPercent)
        {
            problems.Add($"power limit {powerLimit}% reset to 100%");
            powerLimit = 100;
        }
        LoadProblems = problems;
        return s with { Fans = fans ?? new(), Alerts = alerts ?? new(), LogFolder = logFolder, PowerLimitPercent = powerLimit };
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
