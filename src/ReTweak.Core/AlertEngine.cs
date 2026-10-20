namespace ReTweak.Core;

public enum AlertSeverity { None = 0, Info = 1, Warning = 2, Critical = 3 }

public sealed record Alert(string Key, AlertSeverity Severity, string Title, string Detail, DateTime Since);

/// <summary>
/// Thresholds. Defaults follow the 12V-2x6 per-pin rating (9.5 A) and a ±5% window
/// around 12 V, with earlier "warning" bands so problems show up before limits are hit.
/// </summary>
public sealed record AlertOptions
{
    public double PinWarnAmps { get; init; } = 7.6;        // 80% of 9.5 A
    public double PinCriticalAmps { get; init; } = 9.5;    // connector spec per pin
    public double WarnLowVolts { get; init; } = 11.64;     // -3%
    public double CriticalLowVolts { get; init; } = 11.40; // -5%
    public double WarnHighVolts { get; init; } = 12.60;    // +5%

    /// <summary>Voltage difference between best and worst pin. All pins share one PSU rail,
    /// so a spread at the card means one path (wire/contact) has more resistance.</summary>
    /// Load test (RTX 5080 Astral, 400 W): healthy cable held a constant 16 mV spread.
    public double WarnSpreadVolts { get; init; } = 0.040;
    public double CriticalSpreadVolts { get; init; } = 0.100;

    /// <summary>Highest pin current / average pin current. Load test: healthy cable x1.04.</summary>
    public double WarnImbalance { get; init; } = 1.25;
    public double CriticalImbalance { get; init; } = 1.70;

    /// <summary>Spread and imbalance only mean something under load.</summary>
    public double MinLoadAmpsForBalance { get; init; } = 10;

    /// <summary>Trend alert: warn if the worst pin is projected to hit a critical limit within this time.</summary>
    public TimeSpan TrendHorizon { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Must hold at least <see cref="TrendMinFrames"/> chip frames (~600 ms apart).</summary>
    public TimeSpan TrendWindow { get; init; } = TimeSpan.FromSeconds(5);
    public int TrendMinFrames { get; init; } = 6;

    /// <summary>
    /// Debounce, in distinct sensor frames. The ASUS chip publishes new values every ~600 ms
    /// (measured under load and at idle), so 2 frames is ~1.2 s and 3 frames ~1.8 s.
    /// Implemented as a leaky counter: each frame at or above
    /// a level adds 2, each frame below it removes 1, and the alert fires at 2 x this value.
    /// So N consecutive frames always fire, isolated glitches never do, and a value hovering
    /// around a threshold (above it about half the time) still fires instead of resetting forever.
    /// </summary>
    public int CriticalFrames { get; init; } = 2;
    /// <summary>3 frames (~1.8 s): load-test noise was ±25 mA / ±2 mV, far below any warning band.</summary>
    public int WarningFrames { get; init; } = 3;
    /// <summary>
    /// Trend alerts fire when the window slope AND the slope over the last <see cref="TrendRecentFrames"/>
    /// frames both point at a limit, for this many frames in a row. Together these let a load step
    /// (jump, then flat) pass while a continuing ramp still warns before the limit.
    /// </summary>
    public int TrendFrames { get; init; } = 3;
    public int TrendRecentFrames { get; init; } = 3;
    /// <summary>Consecutive frames below the active level before it clears or steps down (hysteresis).</summary>
    public int ClearFrames { get; init; } = 15;
    public int ReadFailuresForAlert { get; init; } = 10;
    /// <summary>Warn if reads succeed but the chip publishes nothing new for this long.</summary>
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// GPU temperatures (°C), checked once a second. Conservative defaults that sit below
    /// typical throttle points; adjust in Settings for your card.
    /// </summary>
    public double WarnCoreTempC { get; init; } = 83;
    public double CriticalCoreTempC { get; init; } = 88;
    public double WarnMemoryTempC { get; init; } = 90;
    public double CriticalMemoryTempC { get; init; } = 100;
    /// <summary>
    /// RTX 50 hotspot (<see cref="GpuSnapshot.HotspotC"/>, read through PawnIO). Placeholders until
    /// load-tested on this card: hotspot typically runs 10-20 °C above core, so these sit roughly
    /// that far above the core thresholds.
    /// </summary>
    public double WarnHotspotTempC { get; init; } = 95;
    public double CriticalHotspotTempC { get; init; } = 105;
}

/// <summary>
/// Evaluates every connector frame. Critical conditions use raw values with a 2-frame
/// debounce to reject single glitches; warnings need 5 frames.
/// Trend rules extrapolate the last few seconds to warn before a limit is crossed.
/// Events are raised on the caller's thread while an internal lock is held: keep handlers short.
/// </summary>
public sealed class AlertEngine
{
    private sealed class State
    {
        public AlertSeverity Active;
        public int WarnScore, CritScore, ClearCount;
        public Alert? Current;
    }

    private readonly Dictionary<string, State> _states = [];
    private readonly Queue<(TimeSpan T, double MinV, double MaxA)> _trend = new();
    private readonly object _lock = new();
    private int _readFailures;

    private AlertOptions _options;

    /// <summary>Thresholds; can be replaced at runtime (takes effect from the next frame).</summary>
    public AlertOptions Options
    {
        get { lock (_lock) return _options; }
        set { lock (_lock) _options = value ?? throw new ArgumentNullException(nameof(value)); }
    }

    /// <summary>Raised when an alert appears or changes severity.</summary>
    public event Action<Alert>? Raised;
    /// <summary>Raised with the key of an alert that has cleared.</summary>
    public event Action<string>? Cleared;

    public AlertEngine(AlertOptions? options = null) => _options = options ?? new AlertOptions();

    public IReadOnlyList<Alert> Active
    {
        get { lock (_lock) return _states.Values.Where(s => s.Current is not null).Select(s => s.Current!).OrderByDescending(a => a.Severity).ToList(); }
    }

    public void ReadFailed(TimeSpan uptime)
    {
        lock (_lock)
        {
            _readFailures++;
            var sev = _readFailures >= Options.ReadFailuresForAlert ? AlertSeverity.Warning : AlertSeverity.None;
            Update("sensor", sev, "Connector sensor not responding", $"{_readFailures} reads failed in a row", immediate: true);
        }
    }

    /// <summary>Reads succeed but return the same frame as before.</summary>
    public void NoNewData(TimeSpan uptime, TimeSpan sinceLastChange)
    {
        lock (_lock)
        {
            _readFailures = 0;
            Update("sensor", AlertSeverity.None, "", "", immediate: true);
            var sev = sinceLastChange >= Options.StaleAfter ? AlertSeverity.Warning : AlertSeverity.None;
            Update("stale", sev, "Connector readings frozen",
                   $"No new data from the sensor for {sinceLastChange.TotalSeconds:F0} s; values shown may be out of date.", immediate: true);
        }
    }

    public void Process(ConnectorFrame frame)
    {
        var o = Options;
        var r = frame.Raw;
        lock (_lock)
        {
            _readFailures = 0;
            Update("sensor", AlertSeverity.None, "", "", immediate: true);
            Update("stale", AlertSeverity.None, "", "", immediate: true);

            foreach (var p in r.Pins)
            {
                if (!p.Valid) continue;
                var amps = Level(p.Amps, o.PinWarnAmps, o.PinCriticalAmps);
                Update($"amps:{p.Pin}", amps, $"Pin {p.Pin} current high",
                       $"{p.Amps:F2} A (rated {ConnectorReading.PinRatedAmps} A)");

                var low = p.Volts < o.CriticalLowVolts ? AlertSeverity.Critical
                        : p.Volts < o.WarnLowVolts ? AlertSeverity.Warning : AlertSeverity.None;
                Update($"vlow:{p.Pin}", low, $"Pin {p.Pin} voltage low", $"{p.Volts:F3} V");

                var high = p.Volts > o.WarnHighVolts ? AlertSeverity.Warning : AlertSeverity.None;
                Update($"vhigh:{p.Pin}", high, $"Pin {p.Pin} voltage high", $"{p.Volts:F3} V");
            }

            var valid = r.Pins.Where(p => p.Valid).ToList();
            if (valid.Count == 0) return;
            bool loaded = r.TotalAmps >= o.MinLoadAmpsForBalance && valid.Count >= 2;

            double spread = valid.Max(p => p.Volts) - valid.Min(p => p.Volts);
            var worstV = valid.MinBy(p => p.Volts);
            Update("spread", loaded ? Level(spread, o.WarnSpreadVolts, o.CriticalSpreadVolts) : AlertSeverity.None,
                   "Pin voltages diverging",
                   $"{spread * 1000:F0} mV between pins; pin {worstV.Pin} lowest. Possible poor contact or damaged wire.");

            var worstA = valid.MaxBy(p => p.Amps);
            Update("imbalance", loaded ? Level(r.Imbalance, o.WarnImbalance, o.CriticalImbalance) : AlertSeverity.None,
                   "Current imbalance between pins",
                   $"Pin {worstA.Pin} carries x{r.Imbalance:F2} the average ({worstA.Amps:F2} A).");

            EvaluateTrend(frame.Uptime, valid);
        }
    }

    /// <summary>
    /// GPU temperature checks; call once per GPU sample (~1 s). Missing readings leave their
    /// alert as it is, so an alert can't silently clear just because a source went away.
    /// </summary>
    public void ProcessGpu(GpuSnapshot s)
    {
        lock (_lock)
        {
            var o = Options;
            void Check(string key, double? value, double warn, double crit, string name)
            {
                if (value is not double v) return;
                Update(key, Level(v, warn, crit), $"{name} temperature high", $"{v:F1} °C (warning {warn:F0} °C, critical {crit:F0} °C)");
            }
            Check("temp:core", s.CoreTempC, o.WarnCoreTempC, o.CriticalCoreTempC, "GPU core");
            Check("temp:memory", s.MemoryTempC, o.WarnMemoryTempC, o.CriticalMemoryTempC, "GPU memory");
            Check("temp:hotspot", s.HotspotC, o.WarnHotspotTempC, o.CriticalHotspotTempC, "GPU hotspot");
        }
    }

    private void EvaluateTrend(TimeSpan now, List<PinReading> valid)
    {
        var o = Options;
        _trend.Enqueue((now, valid.Min(p => p.Volts), valid.Max(p => p.Amps)));
        while (_trend.Count > 0 && now - _trend.Peek().T > o.TrendWindow) _trend.Dequeue();

        AlertSeverity vSev = AlertSeverity.None, aSev = AlertSeverity.None;
        string vDetail = "", aDetail = "";
        if (_trend.Count >= o.TrendMinFrames)
        {
            var pts = _trend.ToArray();
            double[] x = pts.Select(p => (p.T - pts[0].T).TotalSeconds).ToArray();
            // A trend must still be going in the latest few frames. A normal load step (fast
            // change, then flat) stops moving immediately; a real runaway keeps going.
            int recent = Math.Max(0, x.Length - o.TrendRecentFrames);

            double[] v = pts.Select(p => p.MinV).ToArray();
            double vSlope = Slope(x, v);
            double vRecent = Slope(x[recent..], v[recent..]);
            double vNow = v[^1];
            if (vSlope < -0.02 && vRecent < -0.02 && vNow > o.CriticalLowVolts)
            {
                double rate = -Math.Max(vSlope, vRecent); // the gentler of the two, to avoid over-alarming
                double eta = (vNow - o.CriticalLowVolts) / rate;
                if (eta <= o.TrendHorizon.TotalSeconds)
                {
                    vSev = AlertSeverity.Warning;
                    vDetail = $"Falling {rate * 1000:F0} mV/s; {o.CriticalLowVolts:F2} V in ~{eta:F1} s at this rate.";
                }
            }

            double[] a = pts.Select(p => p.MaxA).ToArray();
            double aSlope = Slope(x, a);
            double aRecent = Slope(x[recent..], a[recent..]);
            double aNow = a[^1];
            if (aSlope > 0.2 && aRecent > 0.2 && aNow < o.PinCriticalAmps)
            {
                double rate = Math.Min(aSlope, aRecent);
                double eta = (o.PinCriticalAmps - aNow) / rate;
                if (eta <= o.TrendHorizon.TotalSeconds)
                {
                    aSev = AlertSeverity.Warning;
                    aDetail = $"Rising {rate:F1} A/s; {o.PinCriticalAmps} A in ~{eta:F1} s at this rate.";
                }
            }
        }
        Update("trend:volts", vSev, "Voltage dropping quickly", vDetail, warnFrames: o.TrendFrames);
        Update("trend:amps", aSev, "Pin current climbing quickly", aDetail, warnFrames: o.TrendFrames);
    }

    private static AlertSeverity Level(double value, double warn, double critical) =>
        value >= critical ? AlertSeverity.Critical : value >= warn ? AlertSeverity.Warning : AlertSeverity.None;

    /// <summary>
    /// Theil-Sen slope: the median of all pairwise slopes. Unlike least squares, a single
    /// glitched frame cannot fake a trend; it takes a sustained change in most frames.
    /// </summary>
    private static double Slope(double[] x, double[] y)
    {
        var slopes = new List<double>(x.Length * (x.Length - 1) / 2);
        for (int i = 0; i < x.Length; i++)
            for (int j = i + 1; j < x.Length; j++)
                if (x[j] - x[i] > 1e-9) slopes.Add((y[j] - y[i]) / (x[j] - x[i]));
        if (slopes.Count == 0) return 0;
        slopes.Sort();
        int m = slopes.Count / 2;
        return slopes.Count % 2 == 1 ? slopes[m] : (slopes[m - 1] + slopes[m]) / 2;
    }

    /// <summary>Debounced state machine per alert key (see <see cref="AlertOptions.CriticalFrames"/>).</summary>
    private void Update(string key, AlertSeverity sev, string title, string detail, bool immediate = false, int? warnFrames = null)
    {
        if (!_states.TryGetValue(key, out var s)) _states[key] = s = new State();
        var o = Options;

        int warnTarget = immediate ? 2 : 2 * (warnFrames ?? o.WarningFrames);
        int critTarget = immediate ? 2 : 2 * o.CriticalFrames;
        s.WarnScore = sev >= AlertSeverity.Warning ? Math.Min(s.WarnScore + 2, warnTarget) : Math.Max(s.WarnScore - 1, 0);
        s.CritScore = sev >= AlertSeverity.Critical ? Math.Min(s.CritScore + 2, critTarget) : Math.Max(s.CritScore - 1, 0);

        var reached = s.CritScore >= critTarget ? AlertSeverity.Critical
                    : s.WarnScore >= warnTarget ? AlertSeverity.Warning
                    : sev == AlertSeverity.Info ? AlertSeverity.Info
                    : AlertSeverity.None;

        if (reached > s.Active)
        {
            s.ClearCount = 0;
            s.Active = reached;
            s.Current = new Alert(key, reached, title, detail, s.Current?.Since ?? DateTime.Now);
            Raised?.Invoke(s.Current);
            return;
        }

        if (s.Active == AlertSeverity.None) return;

        if (sev >= s.Active)
        {
            s.ClearCount = 0;
            s.Current = s.Current! with { Detail = detail }; // keep the live value fresh
            return;
        }

        if (immediate || ++s.ClearCount >= o.ClearFrames)
        {
            s.ClearCount = 0;
            s.Active = sev;
            if (sev == AlertSeverity.None)
            {
                s.Current = null;
                Cleared?.Invoke(key);
            }
            else
            {
                s.Current = s.Current! with { Severity = sev, Detail = detail };
                Raised?.Invoke(s.Current);
            }
        }
    }
}
