namespace ReTweak.Core;

public enum FanMode { Auto, Fixed, Curve }

/// <summary>One point of a fan curve: at <paramref name="TempC"/> run the fans at <paramref name="Percent"/>.</summary>
public sealed record FanPoint(double TempC, int Percent);

public sealed record FanOptions
{
    /// <summary>Auto leaves the fans to the driver; ReTweak writes nothing.</summary>
    public FanMode Mode { get; init; } = FanMode.Auto;
    public int FixedPercent { get; init; } = 60;
    /// <summary>Linear between points; flat below the first and above the last.</summary>
    public IReadOnlyList<FanPoint> Curve { get; init; } =
        [new(40, 30), new(55, 40), new(65, 55), new(75, 75), new(83, 100)];
    /// <summary>The curve only steps down once the core is this much cooler, so fans don't hunt.</summary>
    public double HysteresisC { get; init; } = 3;
    /// <summary>At or above this core temperature every manual mode runs at full speed.</summary>
    public double FailsafeTempC { get; init; } = 88;

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
        if (FailsafeTempC is < 60 or > 110) return "Full-speed failsafe must be 60-110 °C.";
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
}

public sealed record FanStatus(FanMode Mode, int? Percent, string Text, AlertSeverity Severity = AlertSeverity.None, bool NeedsAdmin = false);

/// <summary>
/// Drives the fans from <see cref="FanOptions"/>. Call <see cref="Tick"/> about once a second
/// with the core temperature, always from the same thread.
///
/// Safety: anything unexpected hands the fans back to the driver's automatic curve, which
/// is what the card does with no software running. That covers switching to Auto, a lost
/// temperature reading, a failed write and <see cref="Dispose"/>. Manual modes also go to
/// full speed at <see cref="FanOptions.FailsafeTempC"/>. A manual speed survives the process
/// being killed, so callers should also dispose on process exit.
/// </summary>
public sealed class FanController : IDisposable
{
    /// <summary>Seconds without a temperature before giving the fans back to the driver.</summary>
    public const int MissedTempsBeforeRelease = 3;
    private const int OverridesBeforeWarning = 3;

    private readonly Func<int, string?> _set;
    private readonly Func<string?> _restore;
    private readonly Func<int?> _readTarget;
    private readonly (int Min, int Max) _range;

    private FanOptions _options = new();
    private int? _applied;          // speed ReTweak last wrote; null while the driver is in charge
    private int _missedTemps, _overrides;
    private string? _blocked;       // write failed; don't retry until the options change
    private bool _blockedByPermission;
    private bool _disposed;

    public FanStatus Status { get; private set; } = new(FanMode.Auto, null, "Automatic (driver)");

    public FanController(GpuMonitor monitor, int gpuIndex)
        : this(p => monitor.SetFanSpeed(gpuIndex, p), () => monitor.RestoreAutoFans(gpuIndex),
               () => monitor.FanTarget(gpuIndex, 0), monitor.FanSpeedRange(gpuIndex) ?? (0, 100)) { }

    /// <summary>For simulations: plug in your own fan writes.</summary>
    public FanController(Func<int, string?> set, Func<string?> restore, Func<int?> readTarget, (int Min, int Max) range)
    {
        _set = set;
        _restore = restore;
        _readTarget = readTarget;
        _range = range;
    }

    public (int Min, int Max) Range => _range;

    public FanOptions Options
    {
        get => _options;
        set
        {
            _options = value;
            _blocked = null; // new settings: allow another attempt
            _blockedByPermission = false;
            _overrides = 0;
        }
    }

    public FanStatus Tick(double? coreTempC)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var o = _options;

        if (o.Mode == FanMode.Auto)
        {
            Release();
            return Status = new(FanMode.Auto, null, "Automatic (driver)");
        }
        if (_blocked is not null)
            return Status = new(o.Mode, null, $"Can't set fan speed: {_blocked}. Fans are on automatic.",
                                AlertSeverity.Warning, _blockedByPermission);

        if (coreTempC is not double temp)
        {
            if (++_missedTemps < MissedTempsBeforeRelease || _applied is null) return Status;
            Release();
            return Status = new(o.Mode, null, "No temperature reading, so the driver has the fans until it's back.", AlertSeverity.Warning);
        }
        _missedTemps = 0;

        bool failsafe = temp >= o.FailsafeTempC;
        int target = failsafe ? 100 : o.Mode == FanMode.Fixed ? o.FixedPercent : CurveTarget(o, temp);
        target = Math.Clamp(target, _range.Min, _range.Max);

        // Re-write if the driver is aiming elsewhere: another program (GPU Tweak III's fan service,
        // Afterburner...) may be fighting over the fans.
        int? driverTarget = _applied is null ? null : _readTarget();
        bool overridden = driverTarget is int dt && Math.Abs(dt - _applied!.Value) > 2;
        _overrides = overridden ? _overrides + 1 : 0;

        if (target != _applied || overridden)
        {
            if (_set(target) is { } error)
            {
                _blocked = error;
                _blockedByPermission = error.Contains("administrator");
                _applied = null;
                _overrides = 0;
                _restore(); // a multi-fan write can fail halfway; undo the fans that did change
                return Status = new(o.Mode, null, $"Can't set fan speed: {error}. Fans are on automatic.",
                                    AlertSeverity.Warning, _blockedByPermission);
            }
            _applied = target;
        }

        string mode = o.Mode == FanMode.Fixed ? "Fixed" : "Curve";
        if (failsafe)
            return Status = new(o.Mode, target, $"Full speed: core {temp:F0} °C is at the {o.FailsafeTempC:F0} °C failsafe", AlertSeverity.Warning);
        if (_overrides >= OverridesBeforeWarning)
            return Status = new(o.Mode, target, $"{mode} {target}%, but another program keeps changing the fans. Close other fan tools.",
                                AlertSeverity.Warning);
        return Status = new(o.Mode, target, $"{mode} · {target}% at {temp:F0} °C");
    }

    /// <summary>Speeds up right away; slows down only once the curve still allows it <see cref="FanOptions.HysteresisC"/> warmer.</summary>
    private int CurveTarget(FanOptions o, double temp)
    {
        int up = o.CurvePercent(temp);
        if (_applied is not int current || up >= current) return up;
        return Math.Max(up, Math.Min(current, o.CurvePercent(temp + o.HysteresisC)));
    }

    /// <summary>Gives the fans back to the driver if ReTweak holds them.</summary>
    private void Release()
    {
        _overrides = 0;
        if (_applied is null) return;
        _applied = null;
        _restore();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Release();
    }
}
