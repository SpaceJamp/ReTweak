using System.Diagnostics;

namespace ReTweak.Core;

/// <summary>
/// Stored as numbers in settings.json, so values never change: Driver (0, the card's own curve; formerly "Auto"),
/// Fixed, Curve, and Smart (3), the closed-loop controller the dashboard calls "Auto".
/// </summary>
public enum FanMode { Driver = 0, Fixed = 1, Curve = 2, Smart = 3 }

/// <summary>How the Smart (Auto) controller trades temperature accuracy against fan noise.</summary>
public enum SmartPriority { Quiet, Balanced, Tight }

/// <summary>Which temperature the curve follows.</summary>
public enum FanSource { Core, Hotspot, Memory, Hottest }

/// <summary>One point of a fan curve: at <paramref name="TempC"/> run the fans at <paramref name="Percent"/>.</summary>
public sealed record FanPoint(double TempC, int Percent);

/// <summary>The temperatures fan control works from; any may be missing.</summary>
/// <param name="HotspotC">Hotspot averaged over the last second: what the curve follows.</param>
/// <param name="HotspotMaxC">Highest hotspot reading in the last second: what failsafes and the safety net use.</param>
/// <param name="PowerFraction">Board power / power limit (0-1): lets Smart mode react to load before temperature does.</param>
/// <param name="MemoryChipMaxC">Hottest memory chip, read through PawnIO (null without it).</param>
/// <param name="VrmMaxC">Hottest VRM board sensor (ASUS cards).</param>
/// <param name="Activity">Load and foreground-app readings for activity detection (null: don't adapt).</param>
public readonly record struct FanTemps(double? CoreC, double? HotspotC = null, double? MemoryC = null, double? HotspotMaxC = null,
                                       double? PowerFraction = null, double? MemoryChipMaxC = null, double? VrmMaxC = null,
                                       ActivitySample? Activity = null)
{
    /// <summary>The hotspot to make safety decisions on: the peak when known, else the average.</summary>
    public double? HotspotPeakC => HotspotMaxC ?? HotspotC;
}

public sealed record FanOptions
{
    /// <summary>Smart (shown as "Auto") is ReTweak's own controller; Driver leaves the fans to the card's curve.</summary>
    public FanMode Mode { get; init; } = FanMode.Smart;
    public int FixedPercent { get; init; } = 60;
    /// <summary>Linear between points; flat below the first and above the last.</summary>
    public IReadOnlyList<FanPoint> Curve { get; init; } = Presets["Balanced"];
    public FanSource Source { get; init; } = FanSource.Core;
    /// <summary>The curve only steps down once the source is this much cooler, so fans don't hunt.</summary>
    public double HysteresisC { get; init; } = 3;

    /// <summary>Full speed at or above any of these, in every mode that writes to the fans.</summary>
    public double FailsafeTempC { get; init; } = 88;
    public double FailsafeHotspotC { get; init; } = 105;
    public double FailsafeMemoryC { get; init; } = 100;

    /// <summary>Curve mode: below this temperature the driver gets the fans, so they can stop (0 RPM).</summary>
    public bool ZeroRpm { get; init; }
    public double ZeroRpmBelowC { get; init; } = 50;

    /// <summary>Fastest speed change in %/s; 0 = instant. The failsafe always jumps straight to 100%.</summary>
    public double RampUpPerSecond { get; init; } = 10;
    public double RampDownPerSecond { get; init; } = 3;
    /// <summary>Time constant for averaging the source temperature; 0 = off. Failsafes use raw readings.</summary>
    public double SmoothingSeconds { get; init; } = 2;

    /// <summary>
    /// Auto mode safety net: the driver keeps the fans (its curve sees only the core) until the hotspot or
    /// memory gets too hot, or the hotspot runs far ahead of the core, a sign of poor cooler contact.
    /// ReTweak then speeds the fans up and hands back once things stay cool for <see cref="AssistHoldSeconds"/>.
    /// Placeholder thresholds until load-tested.
    /// </summary>
    public bool AutoAssist { get; init; } = true;
    public double AssistHotspotC { get; init; } = 95;
    public double AssistMemoryC { get; init; } = 94;
    public double AssistGapC { get; init; } = 25;
    public double AssistReleaseMarginC { get; init; } = 5;
    public double AssistHoldSeconds { get; init; } = 30;
    /// <summary>Added to the driver's speed when the safety net steps in.</summary>
    public int AssistBoostPercent { get; init; } = 20;

    /// <summary>
    /// Smart mode: hold the hotspot (or, without one, the core) at a target. The fan speed is a
    /// load estimate from board power plus a proportional-integral correction on the temperature
    /// error; see <see cref="SmartTuning"/>. Failsafes, ramping and the driver's minimum still apply.
    /// </summary>
    public double SmartTargetHotspotC { get; init; } = 70;
    /// <summary>Used when there's no hotspot (no PawnIO): the core runs a few degrees below the hotspot.</summary>
    public double SmartTargetCoreC { get; init; } = 62;
    /// <summary>
    /// Auto pushes hard to keep the hotspot's 1 s average (the value the dashboard shows) under this (core: 8 °C
    /// lower without a hotspot),
    /// whatever the activity. 0 turns it off. At full load this can need fans near 100%.
    /// </summary>
    public double CeilingC { get; init; } = 80;
    public SmartPriority SmartPriority { get; init; } = SmartPriority.Balanced;
    /// <summary>
    /// Auto's 0 RPM: at light load (idle, desktop, video; under 25% power) and below
    /// <see cref="SmartIdleStopBelowC"/>, hand the fans to the driver so they can stop.
    /// </summary>
    public bool SmartIdleStop { get; init; }
    /// <summary>Auto's 0 RPM threshold on the hotspot (8 °C lower on the core without one). Fans return 5 °C above it.</summary>
    public double SmartIdleStopBelowC { get; init; } = 50;

    /// <summary>Auto picks a target and style for what you're doing (<see cref="Activities"/>).</summary>
    public bool AdaptToActivity { get; init; } = true;
    /// <summary>Let activity detection look at the foreground window (fullscreen, process name). Stays on this PC.</summary>
    public bool UseForegroundApp { get; init; } = true;
    public IReadOnlyDictionary<ActivityKind, ActivityProfile> Activities { get; init; } = DefaultActivities;

    public static IReadOnlyDictionary<ActivityKind, ActivityProfile> DefaultActivities { get; } = new Dictionary<ActivityKind, ActivityProfile>
    {
        // All under the default 80 °C ceiling, with room for load spikes.
        [ActivityKind.Idle] = new(60, SmartPriority.Quiet, 0),
        [ActivityKind.Desktop] = new(62, SmartPriority.Quiet, 0),
        [ActivityKind.Video] = new(62, SmartPriority.Quiet, 0),
        [ActivityKind.Gaming] = new(70, SmartPriority.Tight, 5),
        [ActivityKind.Compute] = new(69, SmartPriority.Balanced, 3),
    };

    /// <summary>
    /// Auto also listens to these sensors, each against its own target and limit: whichever is closest to
    /// its limit steers the fans. Placeholders until load-tested.
    /// </summary>
    public double MemoryTargetC { get; init; } = 84;
    public double MemoryChipTargetC { get; init; } = 90;
    public double MemoryChipLimitC { get; init; } = 105;
    public double VrmTargetC { get; init; } = 90;
    public double VrmLimitC { get; init; } = 105;

    /// <summary>Ready-made curves, written for the core temperature.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<FanPoint>> Presets { get; } = new Dictionary<string, IReadOnlyList<FanPoint>>
    {
        ["Quiet"] = [new(40, 30), new(60, 38), new(70, 50), new(80, 75), new(87, 100)],
        ["Balanced"] = [new(40, 30), new(55, 40), new(65, 55), new(75, 75), new(83, 100)],
        // Cooling first: fans are already well up at light load and hit full speed in the low 70s.
        ["Performance"] = [new(30, 50), new(45, 65), new(55, 80), new(63, 92), new(70, 100)],
    };

    /// <summary>Null when valid, else what's wrong.</summary>
    public string? Validate()
    {
        if (FixedPercent is < 0 or > 100) return "Fixed fan speed must be 0-100 %.";
        if (Curve.Count < 2) return "The fan curve needs at least two points.";
        for (int i = 0; i < Curve.Count; i++)
        {
            var p = Curve[i];
            if (p.TempC is < 20 or > 110) return $"Curve point {i + 1}: temperature must be 20-110 °C.";
            if (p.Percent is < 0 or > 100) return $"Curve point {i + 1}: fan speed must be 0-100 %.";
            if (i > 0 && p.TempC <= Curve[i - 1].TempC) return $"Curve point {i + 1}: temperatures must increase from point to point.";
            if (i > 0 && p.Percent < Curve[i - 1].Percent) return $"Curve point {i + 1}: fan speed can't drop as temperature rises.";
        }
        if (HysteresisC is < 0 or > 10) return "Hysteresis must be 0-10 °C.";
        if (FailsafeTempC is < 40 or > 110) return "Core failsafe must be 40-110 °C.";
        if (FailsafeHotspotC is < 40 or > 125) return "Hotspot failsafe must be 40-125 °C.";
        if (FailsafeMemoryC is < 40 or > 125) return "Memory failsafe must be 40-125 °C.";
        if (ZeroRpmBelowC is < 25 or > 80) return "Zero RPM threshold must be 25-80 °C.";
        double sourceFailsafe = Source switch
        {
            FanSource.Hotspot => FailsafeHotspotC,
            FanSource.Memory => FailsafeMemoryC,
            FanSource.Hottest => Math.Min(FailsafeTempC, Math.Min(FailsafeHotspotC, FailsafeMemoryC)),
            _ => FailsafeTempC,
        };
        if (ZeroRpm && ZeroRpmBelowC >= sourceFailsafe) return "Zero RPM threshold must be below the failsafe for the curve's input.";
        if (SmartIdleStopBelowC is < 25 or > 70) return "Auto's 0 RPM threshold must be 25-70 °C.";
        if (RampUpPerSecond is < 0 or > 100 || RampDownPerSecond is < 0 or > 100) return "Ramp rates must be 0-100 %/s.";
        if (SmoothingSeconds is < 0 or > 30) return "Temperature smoothing must be 0-30 s.";
        // A failsafe at or below a safety-net threshold is fine: full speed simply arrives first.
        if (AssistHotspotC < 50 || AssistMemoryC < 50) return "Safety-net thresholds must be at least 50 °C.";
        if (AssistGapC is < 5 or > 60) return "Hotspot-to-core gap must be 5-60 °C.";
        if (AssistReleaseMarginC is < 1 or > 20) return "Safety-net release margin must be 1-20 °C.";
        if (AssistHoldSeconds is < 0 or > 600) return "Safety-net hold time must be 0-600 s.";
        if (AssistBoostPercent is < 0 or > 100) return "Safety-net boost must be 0-100 %.";
        if (SmartTargetHotspotC is < 40 or > 105) return "Auto hotspot target must be 40-105 °C.";
        if (CeilingC != 0 && CeilingC is < 45 or > 110) return "Auto ceiling must be 45-110 °C (0 = off).";
        if (SmartTargetCoreC is < 35 or > 95) return "Auto core target must be 35-95 °C.";
        if (SmartTargetHotspotC >= FailsafeHotspotC) return "Auto hotspot target must be below the hotspot failsafe.";
        if (SmartTargetCoreC >= FailsafeTempC) return "Auto core target must be below the core failsafe.";
        foreach (var (kind, prof) in Activities)
        {
            if (prof.TargetHotspotC is < 40 or > 105) return $"Auto {kind} target must be 40-105 °C.";
            if (prof.TargetHotspotC >= FailsafeHotspotC) return $"Auto {kind} target must be below the hotspot failsafe.";
            if (prof.LeadSeconds is < 0 or > 15) return $"Auto {kind} look-ahead must be 0-15 s.";
        }
        // A memory target at or above a lowered memory failsafe just drops that vote (see SmartTick): not an error.
        if (MemoryChipTargetC >= MemoryChipLimitC || VrmTargetC >= VrmLimitC) return "Memory-chip and VRM targets must be below their limits.";
        return null;
    }

    /// <summary>Fan speed the curve asks for at <paramref name="tempC"/>.</summary>
    public int CurvePercent(double tempC)
    {
        if (tempC <= Curve[0].TempC) return Curve[0].Percent;
        for (int i = 1; i < Curve.Count; i++)
        {
            var (a, b) = (Curve[i - 1], Curve[i]);
            if (tempC <= b.TempC)
                return (int)Math.Round(a.Percent + (b.Percent - a.Percent) * (tempC - a.TempC) / (b.TempC - a.TempC));
        }
        return Curve[^1].Percent;
    }

    /// <summary>Controller constants for each <see cref="SmartPriority"/>.</summary>
    /// <param name="Kp">% fan per °C of error.</param>
    /// <param name="Ki">% fan per °C·s of accumulated error.</param>
    /// <param name="DeadbandC">Errors smaller than this are ignored, so the fans don't chase noise.</param>
    public readonly record struct SmartTuning(double Kp, double Ki, double DeadbandC, double RampUp, double RampDown, double SmoothingSeconds)
    {
        public static SmartTuning For(SmartPriority p) => p switch
        {
            SmartPriority.Quiet => new(2.5, 0.05, 2.0, 5, 1.5, 4),
            SmartPriority.Tight => new(7.0, 0.30, 0.25, 25, 6, 1),
            _ => new(4.0, 0.12, 0.75, 10, 3, 2),
        };
    }

    public static string SourceName(FanSource s) => s switch
    {
        FanSource.Hotspot => "hotspot",
        FanSource.Memory => "memory",
        FanSource.Hottest => "hottest",
        _ => "core",
    };
}

/// <param name="Percent">Speed ReTweak is holding, or null while the driver has the fans.</param>
/// <param name="SourceTempC">Smoothed temperature the curve used (for the chart).</param>
/// <param name="Assisting">Auto mode's safety net has taken over.</param>
/// <param name="Activity">What Auto thinks you're doing (Smart mode).</param>
/// <param name="Steering">The sensor closest to its limit, which is setting the fan speed (Smart mode).</param>
public sealed record FanStatus(FanMode Mode, int? Percent, string Text, AlertSeverity Severity = AlertSeverity.None,
                               bool NeedsAdmin = false, double? SourceTempC = null, bool Assisting = false,
                               ActivityKind Activity = ActivityKind.Unknown, string? Steering = null);

/// <summary>
/// Drives the fans from <see cref="FanOptions"/>. Call <see cref="Tick"/> about once a second,
/// always from the same thread.
///
/// Safety: anything unexpected hands the fans back to the driver's automatic curve, which
/// is what the card does with no software running. That covers switching to Auto, a lost
/// temperature reading, a failed write and <see cref="Dispose"/>. Every mode that writes goes
/// to full speed at a failsafe temperature, without ramping. A manual speed survives the process
/// being killed, so callers should also dispose on process exit.
/// </summary>
public sealed class FanController : IDisposable
{
    /// <summary>Seconds without a temperature before giving the fans back to the driver.</summary>
    public const int MissedTempsBeforeRelease = 3;
    private const int OverridesBeforeWarning = 3, OverrideWindowTicks = 10;
    private readonly Queue<bool> _recentOverrides = new(); // last few ticks: did another program change the fans?
    /// <summary>The safety net creeps the speed up by this much per second while it's still too hot.</summary>
    private const double AssistCreepPerSecond = 2;

    private readonly Func<int, string?> _set;
    private readonly Func<string?> _restore;
    private readonly Func<int?> _readTarget;
    private readonly Func<TimeSpan> _clock;
    private readonly (int Min, int Max) _range;

    private FanOptions _options = new();
    private int? _applied;           // speed ReTweak last wrote; null while the driver is in charge
    private double? _commanded;      // ramped speed, unrounded
    private int? _lastTarget;        // curve target, for hysteresis
    private double? _smoothed;       // averaged source temperature
    private TimeSpan? _lastTick;
    private int _missedTemps, _overrides;
    private bool _inZeroRpm, _assisting;
    private double _assistTarget;
    private TimeSpan? _coolSince;
    private double _integral;        // Smart: accumulated correction, % fan
    private bool _smartRunning, _smartIdle;
    private TimeSpan? _idleExitAt;
    private string? _smartInput;     // "hotspot" or "core": switching resets the controller
    private object? _lastSmart;      // this tick's controller terms, for the diagnostic log
    private readonly ActivityClassifier _activity = new();
    private readonly Queue<double> _powerTrend = new(); // last few power readings, for look-ahead
    private int _stableTicks;        // consecutive ticks on target, for learning
    private double _leadHold;        // projected power, fading out after a ramp
    private int _ceilingTicks;       // + seconds over the ceiling at full fans, - seconds under it
    private bool _ceilingWarning;

    /// <summary>Airflow the card needs per power level, learned in Auto; persist it with <see cref="AirflowModel.Corrections"/>.</summary>
    public AirflowModel Airflow { get; } = new();
    public string ActivityReason => _activity.Reason;
    private string? _blocked;        // write failed; retried after BlockedRetry unless it was a permission error
    private bool _blockedByPermission;
    private TimeSpan _blockedAt;
    private bool _disposed;
    // The UI timer ticks; ProcessExit / UnhandledException dispose from other threads. Without this, a tick
    // in the middle of a write could set the fans again just after Dispose handed them back.
    private readonly Lock _gate = new();
    /// <summary>
    /// A failed write that wasn't about permissions (a driver reset after a TDR or an update) is tried again
    /// after this long, instead of leaving ReTweak off for the rest of the session.
    /// </summary>
    public static readonly TimeSpan BlockedRetry = TimeSpan.FromSeconds(30);

    public FanStatus Status { get; private set; } = new(FanMode.Driver, null, "Automatic (driver)");

    public FanController(GpuMonitor monitor, int gpuIndex)
        : this(p => monitor.SetFanSpeed(gpuIndex, p), () => monitor.RestoreAutoFans(gpuIndex),
               () => monitor.FanTarget(gpuIndex, 0), monitor.FanSpeedRange(gpuIndex) ?? (0, 100)) { }

    /// <summary>For simulations: plug in your own fan writes and, optionally, a clock.</summary>
    public FanController(Func<int, string?> set, Func<string?> restore, Func<int?> readTarget, (int Min, int Max) range,
                         Func<TimeSpan>? clock = null)
    {
        _set = set;
        _restore = restore;
        _readTarget = readTarget;
        _range = range;
        var sw = Stopwatch.StartNew();
        _clock = clock ?? (() => sw.Elapsed);
    }

    public (int Min, int Max) Range => _range;

    public FanOptions Options
    {
        get => _options;
        set
        {
            lock (_gate) SetOptions(value);
        }
    }

    private void SetOptions(FanOptions value)
    {
        var previous = _options;
        _options = value;
        _blocked = null; // new settings: allow another attempt
        _blockedByPermission = false;
        _overrides = 0;
        _recentOverrides.Clear();
        if (value.Source != previous.Source) _smoothed = null; // a different input: don't average across it
        if (value.Mode != FanMode.Driver) { _assisting = false; _coolSince = null; }
        if (value.Mode != FanMode.Smart || value.SmartPriority != previous.SmartPriority) _smartRunning = false;
    }

    /// <summary>Runs one control step; with diagnostics on, logs the inputs, internals and the decision.</summary>
    public FanStatus Tick(FanTemps temps)
    {
        lock (_gate) return TickLocked(temps);
    }

    /// <summary>
    /// Hands the fans back to the driver now (its own curve keeps the card safe). For when a tick failed:
    /// a manual speed must not stay stuck while nothing is controlling it.
    /// </summary>
    public void ReleaseToDriver()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _smartRunning = false;
            _assisting = false;
            Release();
        }
    }

    private FanStatus TickLocked(FanTemps temps)
    {
        var before = Status;
        _lastSmart = null;
        var st = TickCore(temps);
        if (Diag.IsOn(DiagLevel.Debug))
            Diag.Debug(DiagCategory.Fan, "tick", new
            {
                mode = _options.Mode.ToString(),
                temps.CoreC, temps.HotspotC, temps.HotspotMaxC, temps.MemoryC, temps.PowerFraction,
                smoothedC = _smoothed,
                written = _applied,
                commanded = _commanded,
                smart = _lastSmart,
                assisting = _assisting,
                zeroRpm = _inZeroRpm,
                overrides = _overrides,
                blocked = _blocked,
                status = st.Text,
            });
        if (st.Mode != before.Mode || st.Assisting != before.Assisting || st.NeedsAdmin != before.NeedsAdmin
            || st.Severity != before.Severity || (st.Percent is null) != (before.Percent is null))
            Diag.Info(DiagCategory.Fan, "fan state changed", new { from = before.Text, to = st.Text, severity = st.Severity.ToString() });
        if (st.Activity != before.Activity)
            Diag.Info(DiagCategory.Fan, "activity changed", new { from = before.Activity.ToString(), to = st.Activity.ToString(), reason = _activity.Reason });
        return st;
    }

    private FanStatus TickCore(FanTemps temps)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var o = _options;
        var now = _clock();
        double dt = _lastTick is TimeSpan last ? Math.Clamp((now - last).TotalSeconds, 0, 5) : 1;
        _lastTick = now;

        if (o.Mode == FanMode.Driver && !o.AutoAssist)
        {
            Release();
            return Status = new(FanMode.Driver, null, "Automatic (driver)");
        }
        if (_blocked is not null && !_blockedByPermission && now - _blockedAt >= BlockedRetry)
        {
            Diag.Info(DiagCategory.Fan, "retrying fan control after a failed write", new { error = _blocked });
            _blocked = null;
        }
        if (_blocked is not null)
            return Status = o.Mode == FanMode.Driver
                ? new(FanMode.Driver, null, $"Automatic (driver). Safety net off: {_blocked}.", AlertSeverity.None, _blockedByPermission)
                : new(o.Mode, null, $"Can't set fan speed: {_blocked}. Fans are on automatic.", AlertSeverity.Warning, _blockedByPermission);

        string? failsafe = Failsafe(o, temps);
        var (source, sourceName) = o.Mode == FanMode.Smart
            ? (temps.HotspotC ?? temps.CoreC, temps.HotspotC is null ? "core" : "hotspot")
            : SourceTemp(o, temps);
        if (o.Mode == FanMode.Smart && sourceName != _smartInput)
        {
            _smartInput = sourceName; // hotspot appeared or vanished: different scale, start over
            _smoothed = null;
            _smartRunning = false;
        }
        if (source is not double raw)
        {
            _smoothed = null;
            // Whatever reading is left can still demand full speed.
            if (failsafe is not null)
                return Drive(100, jump: true, o, dt) ?? (Status = new(o.Mode, _applied, $"Full speed: {failsafe}", AlertSeverity.Warning));
            if (++_missedTemps < MissedTempsBeforeRelease || _applied is null) return Status;
            _assisting = false; // don't resume an old assist when readings return
            _coolSince = null;
            Release();
            return Status = new(o.Mode, null, "No temperature reading, so the driver has the fans until it's back.", AlertSeverity.Warning);
        }
        _missedTemps = 0;
        double smoothing = o.Mode == FanMode.Smart ? FanOptions.SmartTuning.For(o.SmartPriority).SmoothingSeconds : o.SmoothingSeconds;
        double smooth = _smoothed is double s && smoothing > 0 ? s + (raw - s) * dt / (smoothing + dt) : raw;
        _smoothed = smooth;

        if (o.Mode == FanMode.Driver) return AutoTick(o, temps, failsafe, now, dt);
        if (o.Mode == FanMode.Smart) return SmartTick(o, temps, failsafe, smooth, sourceName, dt);

        int target;
        if (failsafe is not null) target = 100;
        else if (o.Mode == FanMode.Fixed) target = o.FixedPercent;
        else
        {
            if (o.ZeroRpm)
            {
                _inZeroRpm = _inZeroRpm ? smooth < o.ZeroRpmBelowC + o.HysteresisC : smooth < o.ZeroRpmBelowC;
                if (_inZeroRpm)
                {
                    Release();
                    return Status = new(o.Mode, null, $"Zero RPM · driver has the fans ({sourceName} {smooth:F0} °C; stops below {o.ZeroRpmBelowC:F0} °C, back on at {o.ZeroRpmBelowC + o.HysteresisC:F0} °C)",
                                        SourceTempC: smooth);
                }
            }
            target = CurveTarget(o, smooth);
        }

        target = Math.Clamp(target, _range.Min, _range.Max);
        if (Drive(target, jump: failsafe is not null, o, dt) is { } blocked) return blocked;

        string mode = o.Mode == FanMode.Fixed ? "Fixed" : "Curve";
        if (failsafe is not null)
            return Status = new(o.Mode, _applied, $"Full speed: {failsafe}", AlertSeverity.Warning, SourceTempC: smooth);
        if (_overrides >= OverridesBeforeWarning)
            return Status = new(o.Mode, _applied, $"{mode} {_applied}%, but another program keeps changing the fans. Close other fan tools.",
                                AlertSeverity.Warning, SourceTempC: smooth);
        string ramp = _applied != target ? $" → {target}%" : "";
        return Status = new(o.Mode, _applied, $"{mode} · {_applied}%{ramp} at {sourceName} {smooth:F0} °C", SourceTempC: smooth);
    }

    /// <summary>
    /// Smart: fan % = load estimate + Kp·error + integral, ramped, clamped to the driver's range.
    /// The load estimate (25% idle to 80% at the power limit) moves the fans as soon as power changes;
    /// the PI term then trims the temperature onto the target. Starting takes over from wherever the
    /// driver had the fans (bumpless), and the integral stops growing while the output is pinned
    /// (anti-windup) so it doesn't overshoot when the load drops.
    /// </summary>
    private FanStatus SmartTick(FanOptions o, FanTemps t, string? failsafe, double temp, string input, double dt)
    {
        // 1. What is the user doing, and how should Auto behave for it?
        var activity = o.AdaptToActivity && t.Activity is { } sample ? _activity.Update(sample) : ActivityKind.Unknown;
        var profile = activity != ActivityKind.Unknown && o.Activities.TryGetValue(activity, out var p)
            ? p : new ActivityProfile(o.SmartTargetHotspotC, o.SmartPriority, 3);
        var tune = FanOptions.SmartTuning.For(profile.Priority);
        double primaryTarget = input == "hotspot" ? profile.TargetHotspotC
                             : activity == ActivityKind.Unknown ? o.SmartTargetCoreC : profile.TargetHotspotC - 8;
        double primaryLimit = input == "hotspot" ? o.FailsafeHotspotC : o.FailsafeTempC;
        double span = primaryLimit - primaryTarget;

        // 2. Every sensor votes, scaled to its own headroom: 0 = at its target, span = at its limit.
        //    The most urgent one steers. Secondary sensors' targets sit near their limits, so they only
        //    take over when they're the real problem (hot VRAM or VRMs while the hotspot looks fine).
        var votes = new List<(string Name, double Value, double Target, double Error)> { (input, temp, primaryTarget, temp - primaryTarget) };
        void Vote(string name, double? value, double target, double limit)
        {
            if (value is double v && limit > target) votes.Add((name, v, target, (v - target) / (limit - target) * span));
        }
        if (input == "hotspot") Vote("core", t.CoreC, o.FailsafeTempC - 8, o.FailsafeTempC);
        Vote("memory", t.MemoryC, o.MemoryTargetC, o.FailsafeMemoryC);
        Vote("memory chips", t.MemoryChipMaxC, o.MemoryChipTargetC, o.MemoryChipLimitC);
        Vote("VRM", t.VrmMaxC, o.VrmTargetC, o.VrmLimitC);
        // The ceiling: starts pushing 3 °C below it and counts as being at a limit when the hotspot reaches it.
        // Measured on the 1 s average, not the peak: the peak jumps ~±4 °C second to second, and steering on it
        // made the fans hunt between ~67% and 96% in a real game (diagnostic log, 2026-10-20). Failsafes still
        // use the peak.
        double? ceilingInput = input == "hotspot" ? t.HotspotC : t.CoreC;
        double ceiling = input == "hotspot" ? o.CeilingC : o.CeilingC - 8;
        if (o.CeilingC > 0) Vote($"{input} ceiling", ceilingInput, ceiling - 3, ceiling);
        var steer = votes.MaxBy(v => v.Error);
        double error = steer.Error;

        // 3. Load now, and where a rising trend will be in a few seconds.
        // Unknown power (no reading, or a 0 W limit giving NaN/∞) counts as moderate load. A NaN here would
        // stick in the look-ahead and integral for good, and be saved into the learned airflow table.
        bool powerKnown = t.PowerFraction is double pf && double.IsFinite(pf);
        double power = powerKnown ? Math.Clamp(t.PowerFraction!.Value, 0, 1.2) : 0.35;
        _powerTrend.Enqueue(power);
        while (_powerTrend.Count > 5) _powerTrend.Dequeue();
        double slope = _powerTrend.Count >= 2 ? (_powerTrend.Last() - _powerTrend.First()) / (_powerTrend.Count - 1) : 0;
        // The projection fades out (2% of the limit per second) instead of snapping back when the ramp ends:
        // the heat from the ramp is still arriving then, and dropping the fans at that moment raised the peak.
        double projected = Math.Min(1.2, power + Math.Max(0, slope) * profile.LeadSeconds);
        _leadHold = Math.Max(projected, Math.Max(power, _leadHold - 0.02 * dt));
        double predicted = _leadHold;

        // 0 RPM: an absolute threshold, like the curve's, rather than a distance below the target. The old rule
        // (20 °C under target, under 15% power, idle only) never engaged on an RTX 5080 Astral: it idles at
        // ~60 W (16% of its limit) with the hotspot ~40 °C, and the desktop counts as its own activity.
        // Leaves 5 °C above the threshold, at 30% power, when another sensor nears its target, or on a game.
        bool lightActivity = activity is ActivityKind.Idle or ActivityKind.Desktop or ActivityKind.Video or ActivityKind.Unknown;
        if (o.SmartIdleStop && failsafe is null && lightActivity)
        {
            double stopBelow = input == "hotspot" ? o.SmartIdleStopBelowC : o.SmartIdleStopBelowC - 8;
            double others = votes.Skip(1).Select(v => v.Error).DefaultIfEmpty(double.MinValue).Max();
            // Once the fans come back they run at least a minute, so a card that idles near the threshold
            // cycles slowly instead of stopping and starting every few seconds.
            var now = _clock();
            bool wasIdle = _smartIdle;
            _smartIdle = _smartIdle
                ? temp < stopBelow + 5 && power < 0.30 && others < -5
                : temp < stopBelow && power < 0.25 && others < -10 && !(now - _idleExitAt < TimeSpan.FromSeconds(60));
            if (wasIdle && !_smartIdle) _idleExitAt = now;
            if (_smartIdle)
            {
                _smartRunning = false;
                Release();
                return Status = new(FanMode.Smart, null, $"Auto · 0 RPM, driver can stop the fans ({input} {temp:F0} °C, back on at {stopBelow + 5:F0} °C)", SourceTempC: temp,
                                    Activity: activity);
            }
        }
        else _smartIdle = false;

        // 4. Proportional-integral on the steering error, on top of the learned load estimate.
        double feedForward = Airflow.Estimate(predicted);
        double effective = Math.Abs(error) <= tune.DeadbandC ? 0 : error - Math.Sign(error) * tune.DeadbandC;
        if (!_smartRunning)
        {
            // Bumpless: pick the integral so the first output is where the fans already are. If that needs an
            // integral pushing against the error (e.g. positive while far below target), the gap is the
            // proportional term saturating, not a lasting offset: start at zero and let the ramp limit move
            // the fans from where the driver had them. (Seen in a diagnostic log: +60 at 43 °C below target.)
            double now0 = _applied ?? _readTarget() ?? feedForward;
            double bumpless = now0 - feedForward - tune.Kp * effective;
            _integral = bumpless * effective < 0 ? 0 : Math.Clamp(bumpless, -60, 60);
            _smartRunning = true;
        }

        double unclamped = feedForward + tune.Kp * effective + _integral;
        bool pinnedHigh = unclamped >= _range.Max && effective > 0, pinnedLow = unclamped <= _range.Min && effective < 0;
        // While the look-ahead is adding fan, being briefly below target is its doing, not a lasting surplus:
        // don't let the integral unwind, or it's missing when the projected heat actually arrives.
        bool leading = predicted > power + 0.01;
        if (!pinnedHigh && !pinnedLow && !(leading && effective < 0)) _integral = Math.Clamp(_integral + tune.Ki * effective * dt, -60, 60);
        else if (_integral * effective < 0) _integral *= Math.Exp(-dt / 10); // pinned, and the integral points the wrong way: drain it

        // 5. Learning: holding the target with a steady integral means the load estimate is off for this
        //    card at this power. After 20 s on target, move it into the airflow table a little at a time.
        // "On target" allows for the hotspot's normal jitter: a tight dead band alone would never stay put for 20 s.
        _stableTicks = Math.Abs(error) <= Math.Max(tune.DeadbandC, 1.5) && !pinnedHigh && !pinnedLow && failsafe is null
                       && powerKnown && Math.Abs(slope) < 0.01 ? _stableTicks + 1 : 0;
        double learned = 0;
        if (_stableTicks >= 20 && Math.Abs(_integral) > 0.5)
        {
            learned = Airflow.Learn(power, _integral * 0.02 * dt);
            _integral -= learned; // the estimate rose by exactly this, so the output doesn't move
        }

        int target100 = failsafe is not null ? 100 : (int)Math.Round(Math.Clamp(feedForward + tune.Kp * effective + _integral, _range.Min, _range.Max));
        _lastSmart = new
        {
            activity = activity.ToString(), activityReason = _activity.Reason, profile,
            steering = steer.Name, votes = votes.Select(v => new { v.Name, v.Value, v.Target, error = Math.Round(v.Error, 2) }),
            power = Math.Round(power, 3), slope = Math.Round(slope, 4), predicted = Math.Round(predicted, 3),
            feedForward = Math.Round(feedForward, 2), proportional = Math.Round(tune.Kp * effective, 2),
            integral = Math.Round(_integral, 2), unclamped = Math.Round(unclamped, 2), pinnedHigh, pinnedLow,
            learned = Math.Round(learned, 4), stableTicks = _stableTicks, target = target100,
        };

        var ramped = o with { RampUpPerSecond = tune.RampUp, RampDownPerSecond = tune.RampDown };
        if (Drive(target100, jump: failsafe is not null, ramped, dt) is { } blocked) return blocked;

        string what = activity == ActivityKind.Unknown ? "" : $"{activity} · ";
        if (failsafe is not null)
            return Status = new(FanMode.Smart, _applied, $"Full speed: {failsafe}", AlertSeverity.Warning, SourceTempC: temp, Activity: activity);
        if (_overrides >= OverridesBeforeWarning)
            return Status = new(FanMode.Smart, _applied, $"Auto {_applied}%, but another program keeps changing the fans. Close other fan tools.",
                                AlertSeverity.Warning, SourceTempC: temp, Activity: activity);
        // "Can't hold the ceiling" with hysteresis: 3 s over at full fans to say so, 3 s under to stop.
        bool overAtMax = o.CeilingC > 0 && _applied >= _range.Max && ceilingInput > ceiling;
        bool underCeiling = !(ceilingInput > ceiling - 0.5);
        _ceilingTicks = overAtMax ? Math.Max(1, _ceilingTicks + 1) : underCeiling ? Math.Min(-1, _ceilingTicks - 1) : _ceilingTicks;
        if (_ceilingTicks >= 3) _ceilingWarning = true;
        else if (_ceilingTicks <= -3) _ceilingWarning = false;
        if (_ceilingWarning)
            return Status = new(FanMode.Smart, _applied, $"Auto · {what}fans at maximum and {input} {ceilingInput:F0} °C is still over the {ceiling:F0} °C ceiling",
                                AlertSeverity.Warning, SourceTempC: temp, Activity: activity, Steering: steer.Name);
        return Status = new(FanMode.Smart, _applied, $"Auto · {what}{_applied}% · {steer.Name} {steer.Value:F1} °C → {steer.Target:F0} °C",
                            SourceTempC: temp, Activity: activity, Steering: steer.Name);
    }

    /// <summary>Auto: the driver has the fans unless the safety net (or a failsafe) needs to step in.</summary>
    private FanStatus AutoTick(FanOptions o, FanTemps t, string? failsafe, TimeSpan now, double dt)
    {
        // Cooler contact shows as a sustained gap, so compare the averaged hotspot; its 1 s peak runs ~10 °C
        // above the core from noise alone (research/hotspot_probe.py).
        double? gap = t.HotspotC - t.CoreC;
        string? hot = failsafe
            ?? (t.HotspotPeakC >= o.AssistHotspotC ? $"hotspot {t.HotspotPeakC:F0} °C" : null)
            ?? (t.MemoryC >= o.AssistMemoryC ? $"memory {t.MemoryC:F0} °C" : null)
            ?? (gap >= o.AssistGapC ? $"hotspot {gap:F0} °C above core (check cooler contact)" : null);
        bool cool = !(t.HotspotPeakC >= o.AssistHotspotC - o.AssistReleaseMarginC)
                 && !(t.MemoryC >= o.AssistMemoryC - o.AssistReleaseMarginC)
                 && !(gap >= o.AssistGapC - o.AssistReleaseMarginC);

        if (hot is not null)
        {
            if (!_assisting)
            {
                _assisting = true;
                _assistTarget = (_readTarget() ?? _range.Min) + o.AssistBoostPercent;
            }
            else _assistTarget += AssistCreepPerSecond * dt;
            _assistTarget = Math.Min(_assistTarget, 100);
            _coolSince = null;
        }
        else if (_assisting && cool)
        {
            _coolSince ??= now;
            if ((now - _coolSince.Value).TotalSeconds >= o.AssistHoldSeconds)
            {
                _assisting = false;
                _coolSince = null;
                Release();
            }
        }
        else _coolSince = null;

        if (!_assisting)
        {
            Release();
            string watching = t.HotspotPeakC is null ? "safety net watching memory" : "safety net on";
            return Status = new(FanMode.Driver, null, $"Automatic (driver) · {watching}");
        }

        int target = Math.Clamp(failsafe is not null ? 100 : (int)Math.Round(_assistTarget), _range.Min, _range.Max);
        if (Drive(target, jump: failsafe is not null, o, dt) is { } blocked) return blocked;
        string why = hot ?? (_coolSince is null ? "cooling down" : $"cool, handing back in {o.AssistHoldSeconds - (now - _coolSince.Value).TotalSeconds:F0} s");
        return Status = new(FanMode.Driver, _applied, failsafe is not null ? $"Full speed: {failsafe}" : $"Safety net · {_applied}% · {why}",
                            AlertSeverity.Warning, Assisting: true);
    }

    /// <summary>Ramps toward <paramref name="target"/> and writes it. Returns a status only if the write failed.</summary>
    private FanStatus? Drive(int target, bool jump, FanOptions o, double dt)
    {
        // Taking over from the driver: start from where it had the fans, not from zero.
        double from = _commanded ?? _readTarget() ?? target;
        double next = target;
        if (!jump)
        {
            if (target > from && o.RampUpPerSecond > 0) next = Math.Min(target, from + o.RampUpPerSecond * dt);
            else if (target < from && o.RampDownPerSecond > 0) next = Math.Max(target, from - o.RampDownPerSecond * dt);
        }
        _commanded = next;
        int write = Math.Clamp((int)Math.Round(next), _range.Min, _range.Max);

        // Re-write if the driver is aiming elsewhere: another program (GPU Tweak III's fan service,
        // Afterburner...) may be fighting over the fans.
        int? driverTarget = _applied is null ? null : _readTarget();
        bool overridden = driverTarget is int d && Math.Abs(d - _applied!.Value) > 2;
        // Count overrides over the last few ticks, not in a row: a tool that fights every other tick
        // (seen in a log: ASUS's fan service setting 0% every ~3 s) never makes three in a row.
        _recentOverrides.Enqueue(overridden);
        while (_recentOverrides.Count > OverrideWindowTicks) _recentOverrides.Dequeue();
        _overrides = _recentOverrides.Count(o => o);
        if (write == _applied && !overridden) return null;

        if (_set(write) is { } error)
        {
            _blocked = error;
            _blockedByPermission = error.Contains("administrator");
            _blockedAt = _clock();
            ResetHeld();
            _assisting = false;
            _restore(); // a multi-fan write can fail halfway; undo the fans that did change
            return Status = o.Mode == FanMode.Driver
                ? new(FanMode.Driver, null, $"Automatic (driver). Safety net off: {error}.", AlertSeverity.None, _blockedByPermission)
                : new(o.Mode, null, $"Can't set fan speed: {error}. Fans are on automatic.", AlertSeverity.Warning, _blockedByPermission);
        }
        _applied = write;
        return null;
    }

    private static (double? Temp, string Name) SourceTemp(FanOptions o, FanTemps t)
    {
        double? wanted = o.Source switch
        {
            FanSource.Hotspot => t.HotspotC,
            FanSource.Memory => t.MemoryC,
            FanSource.Hottest => new[] { t.CoreC, t.HotspotC, t.MemoryC }.Max(),
            _ => t.CoreC,
        };
        // A missing hotspot (no PawnIO, not elevated) or memory sensor falls back to the core.
        return wanted is not null ? (wanted, FanOptions.SourceName(o.Source)) : (t.CoreC, "core");
    }

    private static string? Failsafe(FanOptions o, FanTemps t) =>
        t.CoreC >= o.FailsafeTempC ? $"core {t.CoreC:F0} °C is at the {o.FailsafeTempC:F0} °C failsafe"
        : t.HotspotPeakC >= o.FailsafeHotspotC ? $"hotspot {t.HotspotPeakC:F0} °C is at the {o.FailsafeHotspotC:F0} °C failsafe"
        : t.MemoryC >= o.FailsafeMemoryC ? $"memory {t.MemoryC:F0} °C is at the {o.FailsafeMemoryC:F0} °C failsafe"
        : null;

    /// <summary>Speeds up right away; slows down only once the curve still allows it <see cref="FanOptions.HysteresisC"/> warmer.</summary>
    private int CurveTarget(FanOptions o, double temp)
    {
        int up = o.CurvePercent(temp);
        int target = _lastTarget is not int current || up >= current ? up
                   : Math.Max(up, Math.Min(current, o.CurvePercent(temp + o.HysteresisC)));
        _lastTarget = target;
        return target;
    }

    private void ResetHeld()
    {
        _applied = null;
        _commanded = null;
        _lastTarget = null;
        _overrides = 0;
        _recentOverrides.Clear();
    }

    /// <summary>Gives the fans back to the driver if ReTweak holds them.</summary>
    private void Release()
    {
        bool held = _applied is not null;
        ResetHeld();
        if (held) _restore();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Release();
        }
    }
}
