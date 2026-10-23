using ReTweak.Core.Interop;

namespace ReTweak.Core;

/// <summary>
/// The once-a-second control loop for one GPU, shared by the app and the CLI (also on Linux): fan control, the
/// power limit and the emergency power cut. Writing fans and the power limit needs administrator (Windows) or
/// root (Linux); without it, everything reads and the fans stay with the driver.
/// </summary>
public sealed class GpuControl : IDisposable
{
    private readonly GpuMonitor _monitor;
    private readonly int _gpu;
    private readonly string _gpuName;
    private readonly PowerEmergency _emergency = new();
    private int? _powerLimitApplied;
    private AppSettings _settings = new();
    private bool _disposed;

    /// <summary>Null if the card reports no fans.</summary>
    public FanController? Fan { get; }
    public static bool CanWrite => Environment.IsPrivilegedProcess;
    public bool EmergencyActive => _emergency.Active;
    public double EmergencyWatts { get; private set; }
    public bool EmergencyFailed { get; private set; }

    /// <summary>Something the user should hear about (title, text): the emergency cut and its end.</summary>
    public event Action<string, string>? Notice;
    /// <summary>The emergency cut started or ended.</summary>
    public event Action? EmergencyChanged;

    public GpuControl(GpuMonitor monitor, int gpu)
    {
        _monitor = monitor;
        _gpu = gpu;
        _gpuName = monitor.Gpus[gpu].Name;
        if (monitor.FanCount(gpu) == 0) return;
        Fan = new FanController(monitor, gpu);
        Fan.Airflow.Load(AirflowStore.Load(_gpuName)); // what Auto learned about this card last time
    }

    /// <summary>Applies settings live: fan options and the power limit.</summary>
    public void Apply(AppSettings settings)
    {
        _settings = settings;
        ApplyPowerLimit(settings.PowerLimitPercent);
        if (Fan is not null) Fan.Options = settings.Fans;
    }

    /// <summary>One control step. Null when the card has no fans.</summary>
    public FanStatus? Tick(GpuSnapshot s)
    {
        if (Fan is null) return null;
        var fg = Fan.Options.UseForegroundApp ? Foreground.Read() : ForegroundApp.None;
        FanStatus st;
        try { st = Fan.Tick(FanTemps.From(s, fg)); }
        catch (Exception ex)
        {
            // Nothing is controlling the fans now: give them to the driver rather than leave a manual speed.
            Diag.Error(DiagCategory.Fan, "fan control tick failed", ex);
            Fan.ReleaseToDriver();
            st = new(Fan.Options.Mode, null, $"Fan control error: {ex.Message}. The driver has the fans.", AlertSeverity.Warning);
        }
        HailMary(st);
        return st;
    }

    /// <summary>Sets the power limit when it changes. 100 % at startup leaves the card alone (another tool may own it).</summary>
    private void ApplyPowerLimit(int percent)
    {
        // During a hail-mary cut, a Settings save must not put the power back early: the cut ends by itself.
        if (_emergency.Active || percent == (_powerLimitApplied ?? 100) || !CanWrite || _monitor.PowerLimits(_gpu) is null) return;
        string? error = _monitor.SetPowerLimitPercent(_gpu, percent, out double watts);
        if (error is null) _powerLimitApplied = percent;
        // watts is what the driver got: a % outside its range is clamped (50 % became 250 W, 62.5 %, on an RTX 5080).
        Diag.Write(error is null ? DiagLevel.Info : DiagLevel.Warn, DiagCategory.Settings,
                   error is null ? "power limit set" : "power limit not set", new { percent, watts = Math.Round(watts), error });
    }

    /// <summary>Full fans can't hold a failsafe: cut the power to the driver's minimum until it cools (PowerEmergency).</summary>
    private void HailMary(FanStatus st)
    {
        var action = _emergency.Update(Fan!.FailsafeActive, st.Percent >= Fan.Range.Max, TimeSpan.FromMilliseconds(Environment.TickCount64));
        if (action == PowerEmergency.Action.Cut)
        {
            string? error = _monitor.SetPowerLimitPercent(_gpu, 0, out double watts); // 0 % clamps to the driver's minimum
            EmergencyWatts = watts;
            EmergencyFailed = error is not null;
            _powerLimitApplied = 0; // so the restore below always puts the user's limit back, even 100 %
            Diag.Warn(DiagCategory.Fan, "emergency power cut", new { watts = Math.Round(watts), error, status = st.Text });
            EmergencyChanged?.Invoke();
            Notice?.Invoke("ReTweak: emergency power cut",
                error is null ? $"Fans are at full speed and still at a failsafe. Power is limited to {watts:F0} W until it cools."
                              : $"Fans are at full speed and still at a failsafe, and the power limit couldn't be lowered: {error}.");
        }
        else if (action == PowerEmergency.Action.Restore)
        {
            ApplyPowerLimit(_settings.PowerLimitPercent);
            Diag.Info(DiagCategory.Fan, "emergency power cut over", new { percent = _settings.PowerLimitPercent });
            EmergencyChanged?.Invoke();
            Notice?.Invoke("ReTweak: power back to normal", $"Cool again for {PowerEmergency.ReleaseAfter.TotalSeconds:F0} s; your power limit is back.");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Exiting during a cut: give the user's limit back, or it stays at the minimum until a reboot (a 100 %
        // setting is left alone at the next start).
        if (_emergency.Active && CanWrite) _monitor.SetPowerLimitPercent(_gpu, _settings.PowerLimitPercent, out _);
        if (Fan is not null) AirflowStore.Save(_gpuName, Fan.Airflow.Corrections);
        Fan?.Dispose(); // back to the driver's curve before NVML shuts down
    }
}
