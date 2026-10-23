using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ReTweak.Core;

namespace ReTweak.App;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed partial class PinViewModel(int pin) : Observable
{
    public const double BarTrackHeight = 160;

    private string _volts = "-", _amps = "-";
    private double _barHeight;
    private SolidColorBrush _brush = Theme.Ok;

    public string Label { get; } = $"Pin {pin}";
    public string Volts { get => _volts; private set => Set(ref _volts, value); }
    public string Amps { get => _amps; private set => Set(ref _amps, value); }
    /// <summary>Bar height in px; a full track is 110% of the pin rating.</summary>
    public double BarHeight { get => _barHeight; private set => Set(ref _barHeight, value); }
    public SolidColorBrush Brush { get => _brush; private set => Set(ref _brush, value); }

    public void Update(PinReading p, AlertOptions o)
    {
        if (!p.Valid) { Amps = "bad read"; Brush = Theme.Muted; return; }
        Volts = $"{p.Volts:F3} V";
        Amps = $"{p.Amps:F2} A";
        BarHeight = Math.Clamp(p.Amps / (o.PinCriticalAmps * 1.1), 0, 1) * BarTrackHeight;
        Brush = p.Amps >= o.PinCriticalAmps || p.Volts < o.CriticalLowVolts ? Theme.Critical
              : p.Amps >= o.PinWarnAmps || p.Volts < o.WarnLowVolts ? Theme.Warn
              : Theme.Ok;
    }
}

public sealed partial class BoardTempViewModel(string name) : Observable
{
    private string _value = "-";
    public string Name { get; } = name;
    public string Value { get => _value; set => Set(ref _value, value); }
}

public sealed record AlertViewModel(string Key, string Title, string Detail, InfoBarSeverity Severity);

public sealed partial class MainViewModel : Observable, IDisposable
{
    public const int HistoryLength = 300; // 60 s at the chip's ~5 Hz

    private readonly GpuMonitor _monitor;
    private readonly ConnectorSampler? _sampler;
    private readonly FanController? _fan;
    private readonly AlertEngine _alerts; // shared: connector frames (sampler thread) + GPU temps (UI thread)
    private readonly DispatcherQueueTimer _timer;
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private readonly int _gpu;

    private string _temp = "-", _core = "-", _mem = "-", _load = "-", _power = "-", _powerLimit = "", _fans = "-", _vram = "-";
    private double _powerPercent, _vramPercent;
    private string _connAmps = "-", _connWatts = "-", _spread = "-", _imbalance = "-", _status = "All clear";
    private SolidColorBrush _statusBrush = Theme.Ok;

    private AppSettings _settings;
    private AlertSeverity _worst;

    public string GpuName { get; }
    public string Subtitle { get; }
    public bool HasConnector => _sampler is not null;
    public AlertOptions AlertOptions => _settings.Alerts;
    public AppSettings Settings => _settings;
    public AlertSeverity WorstSeverity => _worst;
    /// <summary>Short status line for the tray tooltip.</summary>
    public string TrayText => $"ReTweak · {Status}" + (HasConnector && ConnectorWatts != "-" ? $" · connector {ConnectorWatts}" : "");
    /// <summary>Raised on the UI thread when the worst alert or the connector reading changes.</summary>
    public event Action? StatusChanged;

    private string _memTemp = "-", _memSource = "", _hotspot = "-";
    public string Hotspot { get => _hotspot; private set => Set(ref _hotspot, value); }
    private string _hotspotChannels = "", _hotspotPeak = "";
    /// <summary>"1 s average · peak 46.8 °C": the shown value is averaged, safety decisions use the peak.</summary>
    public string HotspotPeak { get => _hotspotPeak; private set => Set(ref _hotspotPeak, value); }
    /// <summary>The four GPU thermal sensors the hotspot is the maximum of.</summary>
    public string HotspotChannels { get => _hotspotChannels; private set => Set(ref _hotspotChannels, value); }
    private string _hotspotSpread = "";
    public string HotspotSpread { get => _hotspotSpread; private set => Set(ref _hotspotSpread, value); }
    /// <summary>Where the hotspot comes from, or what the user can do to get one.</summary>
    public string HotspotNote { get; }
    /// <summary>Show the "Get PawnIO" link: an RTX 50 card without the optional driver.</summary>
    public bool OfferPawnIo { get; }
    /// <summary>Tooltip on the hotspot tile: why a driver is involved and what it can do.</summary>
    public string HotspotHelp { get; }
    public bool HasMemoryTemp { get; }
    public string Temperature { get => _temp; private set => Set(ref _temp, value); }
    public string MemoryTemperature { get => _memTemp; private set => Set(ref _memTemp, value); }
    /// <summary>Where the memory temperature comes from (shown under the value).</summary>
    public string MemoryTempSource { get => _memSource; private set => Set(ref _memSource, value); }
    /// <summary>ASUS board sensors (register 0x60); empty on cards without the ASUS chip.</summary>
    public ObservableCollection<BoardTempViewModel> BoardTemps { get; } = [];
    public bool HasBoardTemps => HasConnector;
    public string CoreClock { get => _core; private set => Set(ref _core, value); }
    public string MemoryClock { get => _mem; private set => Set(ref _mem, value); }
    public string Load { get => _load; private set => Set(ref _load, value); }
    public string Power { get => _power; private set => Set(ref _power, value); }
    public string PowerLimit { get => _powerLimit; private set => Set(ref _powerLimit, value); }
    public double PowerPercent { get => _powerPercent; private set => Set(ref _powerPercent, value); }
    public string Fans { get => _fans; private set => Set(ref _fans, value); }
    public string Vram { get => _vram; private set => Set(ref _vram, value); }
    public double VramPercent { get => _vramPercent; private set => Set(ref _vramPercent, value); }

    public ObservableCollection<PinViewModel> Pins { get; } = new(Enumerable.Range(1, 6).Select(i => new PinViewModel(i)));
    public string ConnectorAmps { get => _connAmps; private set => Set(ref _connAmps, value); }
    public string ConnectorWatts { get => _connWatts; private set => Set(ref _connWatts, value); }
    public string Spread { get => _spread; private set => Set(ref _spread, value); }
    public string Imbalance { get => _imbalance; private set => Set(ref _imbalance, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public SolidColorBrush StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value); }

    public ObservableCollection<AlertViewModel> Alerts { get; } = [];

    private string _fanStatus = "", _fanSpeedRange = "";
    private SolidColorBrush _fanBrush = Theme.Muted;
    private bool _fanNeedsAdmin;
    private int _fanModeIndex;
    public bool HasFanControl => _fan is not null;
    public string FanStatus { get => _fanStatus; private set => Set(ref _fanStatus, value); }
    public SolidColorBrush FanStatusBrush { get => _fanBrush; private set => Set(ref _fanBrush, value); }
    public bool FanNeedsAdmin { get => _fanNeedsAdmin; private set => Set(ref _fanNeedsAdmin, value); }
    public string FanSpeedRange { get => _fanSpeedRange; private set => Set(ref _fanSpeedRange, value); }
    /// <summary>Order of the mode buttons and Settings list: Auto (smart) first, then Driver, Fixed, Curve.</summary>
    public static readonly FanMode[] FanModeOrder = [FanMode.Smart, FanMode.Driver, FanMode.Fixed, FanMode.Curve];

    /// <summary>Selected index of the dashboard's mode buttons (see <see cref="FanModeOrder"/>); setting it saves the mode.</summary>
    public int FanModeIndex
    {
        get => _fanModeIndex;
        set
        {
            if (value < 0 || value == _fanModeIndex) return;
            Set(ref _fanModeIndex, value);
            var updated = _settings with { Fans = _settings.Fans with { Mode = FanModeOrder[value] } };
            try { updated.Save(); } catch (Exception) { /* still apply it for this session */ }
            Apply(updated);
            PollGpu(); // react now rather than in up to a second
        }
    }
    private string _fanChartLabel = "";
    /// <summary>Caption over the fan chart: what the x axis is.</summary>
    public string FanChartLabel { get => _fanChartLabel; private set => Set(ref _fanChartLabel, value); }

    /// <summary>Saves a new curve (from dragging points or a preset); null if saved, else why not.</summary>
    public string? SetFanCurve(IReadOnlyList<FanPoint> curve, bool switchToCurve)
    {
        var fans = _settings.Fans with { Curve = curve, Mode = switchToCurve ? FanMode.Curve : _settings.Fans.Mode };
        if (fans.Validate() is { } problem) return problem;
        var updated = _settings with { Fans = fans };
        try { updated.Save(); } catch (Exception) { /* still apply it for this session */ }
        Apply(updated);
        PollGpu(); // react now rather than in up to a second
        return null;
    }

    /// <summary>Last temperature the fans were driven from (the curve's input), for the curve chart.</summary>
    public double? FanTempC { get; private set; }
    /// <summary>Raised on the UI thread when the fan status or curve changes.</summary>
    public event Action? FanChanged;

    public List<double> PowerHistory { get; } = [];
    public List<double> VoltHistory { get; } = [];
    public event Action? HistoryChanged;

    /// <summary>Raised on the UI thread when an alert appears or escalates.</summary>
    public event Action<Alert>? AlertRaised;

    public MainViewModel(GpuMonitor monitor, AppSettings settings)
    {
        _monitor = monitor;
        _settings = settings;
        var gpu = monitor.Gpus.FirstOrDefault(g => monitor.HasConnectorSensor(g.Index)) ?? monitor.Gpus.FirstOrDefault()
                  ?? throw new InvalidOperationException("No NVIDIA GPU found.");
        _gpu = gpu.Index;
        GpuName = gpu.Name;
        Subtitle = $"Driver {monitor.DriverVersion}  ·  PCI bus {gpu.PciBus}  ·  subsystem {gpu.SubVendorId:X4}:{gpu.SubDeviceId:X4}"
                   + (monitor.HasConnectorSensor(_gpu) ? "  ·  ASUS Power Detector found" : "");

        HasMemoryTemp = monitor.HasMemoryTemperature(_gpu);
        var hotspotState = monitor.HotspotState(_gpu);
        OfferPawnIo = hotspotState == HotspotAvailability.PawnIoMissing;
        HotspotNote = hotspotState switch
        {
            HotspotAvailability.Available => "", // the normal case needs no line on the tile; the tooltip says where it comes from
            HotspotAvailability.PawnIoMissing => "Needs the optional PawnIO driver",
            HotspotAvailability.NeedsAdmin => "Run ReTweak as administrator to read it",
            _ => monitor.HotspotNote(_gpu),
        };
        HotspotHelp = hotspotState == HotspotAvailability.NotApplicable
            ? "This card's driver doesn't report a hotspot."
            : "RTX 50 drivers no longer report the hotspot. ReTweak can read it from the GPU's temperature " +
              "registers through PawnIO, an open-source, signed driver also used by LibreHardwareMonitor and " +
              "FanControl. ReTweak only loads PawnIO's signed NVIDIA module, which can only read those registers. " +
              "PawnIO is optional: everything else in ReTweak works without it. It needs ReTweak to run as administrator.";

        _alerts = new AlertEngine(settings.Alerts);
        // Raised on the sampler thread or the UI thread; always marshal so handlers run on the UI thread.
        _alerts.Raised += a => _ui.TryEnqueue(() => OnAlert(a));
        _alerts.Cleared += k => _ui.TryEnqueue(() => OnCleared(k));

        if (monitor.HasConnectorSensor(_gpu))
        {
            _sampler = new ConnectorSampler(monitor, _gpu, _alerts);
            _sampler.FrameReceived += f => _ui.TryEnqueue(() => OnFrame(f));
            _sampler.Start();
        }

        if (monitor.FanCount(_gpu) > 0)
        {
            _fan = new FanController(monitor, _gpu);
            _fan.Airflow.Load(AirflowStore.Load(GpuName)); // what Auto learned about this card last time
            FanSpeedRange = $" · driver allows {_fan.Range.Min}-{_fan.Range.Max} %";
            // A manual speed would outlive ReTweak, so hand the fans back however the process ends.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => _fan.Dispose();
            AppDomain.CurrentDomain.UnhandledException += (_, _) => _fan.Dispose();
        }

        Apply(settings);
        _timer = _ui.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => PollGpu();
        _timer.Start();
        PollGpu();
    }

    private int? _powerLimitApplied;
    private readonly PowerEmergency _emergency = new();
    private double _emergencyWatts;
    private bool _emergencyFailed;

    /// <summary>The card's power range for the Settings dialog, or null if it doesn't report one.</summary>
    public string? PowerLimitInfo => _monitor.PowerLimits(_gpu) is { } l
        ? $"default {l.DefaultW:F0} W; the driver allows {l.MinW / l.DefaultW * 100:F0}-{l.MaxW / l.DefaultW * 100:F0} %" +
          (Elevation.IsAdmin ? "" : ". Needs ReTweak running as administrator.")
        : null;

    /// <summary>Sets the power limit when it changes. 100 % at startup leaves the card alone (another tool may own it).</summary>
    private void ApplyPowerLimit(int percent)
    {
        // During a hail-mary cut, a Settings save must not put the power back early: the cut ends by itself.
        if (_emergency.Active || percent == (_powerLimitApplied ?? 100) || !Elevation.IsAdmin || _monitor.PowerLimits(_gpu) is null) return;
        string? error = _monitor.SetPowerLimitPercent(_gpu, percent, out double watts);
        if (error is null) _powerLimitApplied = percent;
        // watts is what the driver got: a % outside its range is clamped (50 % became 250 W, 62.5 %, on an RTX 5080).
        Diag.Write(error is null ? DiagLevel.Info : DiagLevel.Warn, DiagCategory.Settings,
                   error is null ? "power limit set" : "power limit not set", new { percent, watts = Math.Round(watts), error });
    }

    /// <summary>Full fans can't hold a failsafe: cut the power to the driver's minimum until it cools (PowerEmergency).</summary>
    private void HailMary(FanStatus st)
    {
        var action = _emergency.Update(_fan!.FailsafeActive, st.Percent >= _fan.Range.Max, TimeSpan.FromMilliseconds(Environment.TickCount64));
        if (action == PowerEmergency.Action.Cut)
        {
            string? error = _monitor.SetPowerLimitPercent(_gpu, 0, out double watts); // 0 % clamps to the driver's minimum
            _emergencyWatts = watts;
            _emergencyFailed = error is not null;
            RefreshStatus();
            _powerLimitApplied = 0; // so the restore below always puts the user's limit back, even 100 %
            Diag.Warn(DiagCategory.Fan, "emergency power cut", new { watts = Math.Round(watts), error, status = st.Text });
            Notifications.ShowInfo("ReTweak: emergency power cut",
                error is null ? $"Fans are at full speed and still at a failsafe. Power is limited to {watts:F0} W until it cools."
                              : $"Fans are at full speed and still at a failsafe, and the power limit couldn't be lowered: {error}.");
        }
        else if (action == PowerEmergency.Action.Restore)
        {
            ApplyPowerLimit(_settings.PowerLimitPercent);
            RefreshStatus();
            Diag.Info(DiagCategory.Fan, "emergency power cut over", new { percent = _settings.PowerLimitPercent });
            Notifications.ShowInfo("ReTweak: power back to normal", $"Cool again for {PowerEmergency.ReleaseAfter.TotalSeconds:F0} s; your power limit is back.");
        }
    }

    /// <summary>Applies settings live: thresholds from the next frame, logging on/off, toasts.</summary>
    public void Apply(AppSettings settings)
    {
        Diagnostics.Apply(settings.DiagnosticsLevel);
        Diag.Info(DiagCategory.Settings, "settings applied", settings);
        _settings = settings;
        Notifications.Enabled = settings.Toasts;
        _alerts.Options = settings.Alerts;
        ApplyPowerLimit(settings.PowerLimitPercent);
        if (_fan is not null)
        {
            _fan.Options = settings.Fans;
            Set(ref _fanModeIndex, Math.Max(0, Array.IndexOf(FanModeOrder, settings.Fans.Mode)), nameof(FanModeIndex));
            FanChartLabel = settings.Fans.Mode switch
            {
                FanMode.Smart => "Auto holds the target · the dot is now",
                FanMode.Driver => "The card's own curve · the dot is now",
                FanMode.Fixed => "Fixed speed · the dot is now",
                _ => "Fan curve · the dot is now",
            };
            // One hint per mode, pointing at what that mode actually has.
            FanHelp = settings.Fans.Mode switch
            {
                FanMode.Smart => "Targets per activity, the ceiling and 0 RPM are in Settings.",
                FanMode.Driver => "The safety net's thresholds are in Settings.",
                FanMode.Fixed => "Set the speed in Settings.",
                _ => "Drag the points on the chart. Input, ramping, 0 RPM and failsafes are in Settings.",
            };
        }
        if (_sampler is null) return;
        bool logging = _sampler.LogPath is not null;
        if (settings.LogConnector && (!logging || !_sampler.LogPath!.StartsWith(settings.LogFolder, StringComparison.OrdinalIgnoreCase)))
        {
            try { _sampler.SetLog(CsvLogger.Daily(settings.LogFolder)); }
            catch (Exception ex) when (Diag.IsOn(DiagLevel.Warn) && LogRefused(ex)) { }
            catch (Exception) { _sampler.SetLog(null); } // bad folder: keep monitoring without logging
        }
        else if (!settings.LogConnector && logging)
            _sampler.SetLog(null);
    }

    public string? LogPath => _sampler?.LogPath;

    private bool LogRefused(Exception ex)
    {
        Diag.Warn(DiagCategory.Settings, "connector log not started", new { ex.Message });
        _sampler?.SetLog(null);
        return true;
    }

    /// <summary>Zips logs, settings and an environment summary to the Desktop; returns the zip's path.</summary>
    public string SaveDiagnostics() => Diagnostics.SaveBundle(_monitor, _settings);

    private void PollGpu()
    {
        var s = _monitor.Sample(_gpu, includeConnector: false);
        _alerts.ProcessGpu(s);

        Temperature = s.CoreTempC is double t ? $"{t:F1} °C" : "-";
        MemoryTemperature = s.MemoryTempC is double mt ? $"{mt:F1} °C" : "-";
        Hotspot = s.HotspotC is double hs ? $"{hs:F1} °C" : "-";
        HotspotPeak = s.HotspotMaxC is double hm ? $"1 s average · peak {hm:F1} °C" : "";
        HotspotChannels = s.ThermalChannels is { } ch
            ? "Sensors " + string.Join("  ", ch.Take(4).Select((c, i) => $"#{i + 1} {(c is double v ? $"{v:F1}" : "-")}"))
            : "";
        HotspotSpread = s.HotspotSpreadC is double sp
            ? $"Die spread {sp:F1} °C" + (s.DieAverageC is double da ? $" · die average {da:F1} °C" : "")
            : "";
        MemoryTempSource = s.MemoryTempSource is { } src ? $"from {src}" : "";
        if (s.BoardTemps is { } board)
        {
            if (BoardTemps.Count == 0)
                foreach (var t0 in board) BoardTemps.Add(new BoardTempViewModel(t0.Name));
            for (int i = 0; i < board.Count && i < BoardTemps.Count; i++)
                BoardTemps[i].Value = board[i].Celsius is double bc ? $"{bc:F1} °C" : "-";
        }
        CoreClock = s.CoreClockMhz is uint c ? $"{c} MHz" : "-";
        MemoryClock = s.MemoryClockMhz is uint m ? $"{m} MHz" : "-";
        Load = s.GpuUtilization is uint u ? $"{u} %" : "-";
        Power = s.PowerWatts is double w ? $"{w:F0} W" : "-";
        PowerLimit = s.PowerLimitWatts is double l ? $"of {l:F0} W limit" : "";
        PowerPercent = s.PowerWatts is double pw && s.PowerLimitWatts is double pl && pl > 0 ? Math.Clamp(pw / pl * 100, 0, 100) : 0;
        // Each fan's target: ReTweak drives them all to the same %; their measured speeds wander a % apart.
        Fans = s.FanPercents.Count > 0
            ? string.Join(" / ", s.FanPercents.Select((f, i) => $"{_monitor.FanTarget(_gpu, i) ?? (int)f}%"))
            : "-";
        CurrentFanPercent = s.FanPercents.Count > 0 ? s.FanPercents.Average(f => (double)f) : null;
        if (s.VramTotalBytes is ulong total && s.VramUsedBytes is ulong used)
        {
            Vram = $"{used / 1073741824.0:F1} / {total / 1073741824.0:F0} GB";
            VramPercent = (double)used / total * 100;
        }
        UpdateFans(s);
    }

    private void UpdateFans(GpuSnapshot s)
    {
        if (_fan is null) return;
        FanStatus st;
        var fg = _fan.Options.UseForegroundApp ? ReTweak.Core.Interop.Foreground.Read() : ReTweak.Core.Interop.ForegroundApp.None;
        try
        {
            st = _fan.Tick(FanTemps.From(s, fg));
        }
        catch (Exception ex)
        {
            // Nothing is controlling the fans now: give them to the driver rather than leave a manual speed.
            Diag.Error(DiagCategory.Fan, "fan control tick failed", ex);
            _fan.ReleaseToDriver();
            st = new(_fan.Options.Mode, null, $"Fan control error: {ex.Message}. The driver has the fans.", AlertSeverity.Warning);
        }
        HailMary(st);
        FanTempC = st.SourceTempC ?? s.CoreTempC;
        // Unelevated, Auto is simply the driver's curve; mention the safety net quietly instead of
        // nagging with the restart button (that's for Fixed and Curve, which can't work at all).
        bool autoUnelevated = st.Mode == FanMode.Driver && !Elevation.IsAdmin;
        FanStatus = autoUnelevated && _fan.Options.AutoAssist ? "Automatic (driver). The safety net works when ReTweak runs as administrator." : st.Text;
        var inputs = new List<string> { "core" };
        if (s.HotspotC is not null) inputs.Add("hotspot + 4 sensors");
        if (s.MemoryChipsC is { Count: > 0 } chips) inputs.Add($"{chips.Count} memory chips");
        else if (s.MemoryTempC is not null) inputs.Add("memory");
        if (s.VrmMaxC is not null) inputs.Add("VRMs");
        inputs.Add("load, power, video engines");
        if (_fan.Options.UseForegroundApp) inputs.Add("foreground app");
        FanInputs = _fan.Options.Mode != FanMode.Smart ? "" // only Auto uses them
            : (s.HotspotC is not null ? "Auto sees (PawnIO): " : "Auto sees (install PawnIO for more): ") + string.Join(", ", inputs);
        FanNeedsAdmin = st.NeedsAdmin && !autoUnelevated && !Elevation.IsAdmin;
        FanStatusBrush = st.Severity > AlertSeverity.None ? Theme.For(st.Severity) : st.Mode == FanMode.Driver ? Theme.Muted : Theme.Ok;
        if (_emergency.Active)
        {
            FanStatus = (_emergencyFailed
                            ? "Emergency: fans are at full speed and still at a failsafe, and the power limit couldn't be lowered. "
                            : $"Emergency power cut to {_emergencyWatts:F0} W: fans are at full speed and still at a failsafe. " +
                              $"Your limit comes back after {PowerEmergency.ReleaseAfter.TotalSeconds:F0} s without one. ") + FanStatus;
            FanStatusBrush = Theme.Critical;
        }
        FanChanged?.Invoke();
    }

    public FanOptions FanOptions => _settings.Fans;
    /// <summary>What Auto thinks you're doing right now, and the hotspot target it's holding for it.</summary>
    public ActivityKind FanActivity => _fan?.Status.Activity ?? ActivityKind.Unknown;
    public double FanTargetC => FanActivity != ActivityKind.Unknown && FanOptions.Activities.TryGetValue(FanActivity, out var p)
        ? p.TargetHotspotC : FanOptions.SmartTargetHotspotC;
    /// <summary>Which sensors Auto can use: more with PawnIO (hotspot, its sensors, memory chips).</summary>
    private string _fanInputs = "";
    public string FanInputs { get => _fanInputs; private set => Set(ref _fanInputs, value); }
    private string _fanHelp = "";
    public string FanHelp { get => _fanHelp; private set => Set(ref _fanHelp, value); }
    public int? FanAppliedPercent => _fan?.Status.Percent;
    /// <summary>Average reported fan speed (%), for the chart while the driver is in charge.</summary>
    public double? CurrentFanPercent { get; private set; }

    private void OnFrame(ConnectorFrame f)
    {
        var o = AlertOptions;
        for (int i = 0; i < Pins.Count && i < f.Smoothed.Pins.Count; i++)
            Pins[i].Update(f.Smoothed.Pins[i], o);

        var r = f.Smoothed;
        var valid = r.Pins.Where(p => p.Valid).ToList();
        ConnectorAmps = $"{r.TotalAmps:F1} A";
        ConnectorWatts = $"{r.TotalWatts:F0} W";
        Spread = valid.Count >= 2 ? $"{(valid.Max(p => p.Volts) - valid.Min(p => p.Volts)) * 1000:F0} mV" : "-";
        Imbalance = $"x{r.Imbalance:F2}";
        StatusChanged?.Invoke();

        // A frame with no valid pins would chart as 0 V / 0 W; leave a gap-free history instead.
        if (f.Raw.Pins.Any(p => p.Valid))
        {
            Push(PowerHistory, f.Raw.TotalWatts);
            Push(VoltHistory, f.Raw.MinVolts);
            HistoryChanged?.Invoke();
        }
    }

    private static void Push(List<double> list, double value)
    {
        list.Add(value);
        if (list.Count > HistoryLength) list.RemoveAt(0);
    }

    private void OnAlert(Alert a)
    {
        Diag.Write(a.Severity >= AlertSeverity.Warning ? DiagLevel.Warn : DiagLevel.Info, DiagCategory.Alert, "alert",
                   new { a.Key, severity = a.Severity.ToString(), a.Title, a.Detail, a.Since });
        var vm = new AlertViewModel(a.Key, a.Title, a.Detail, Theme.InfoBar(a.Severity));
        var existing = Alerts.FirstOrDefault(x => x.Key == a.Key);
        if (existing is not null) Alerts[Alerts.IndexOf(existing)] = vm;
        else Alerts.Insert(0, vm);
        RefreshStatus();
        AlertRaised?.Invoke(a);
    }

    private void OnCleared(string key)
    {
        Diag.Info(DiagCategory.Alert, "alert cleared", new { key });
        var existing = Alerts.FirstOrDefault(x => x.Key == key);
        if (existing is not null) Alerts.Remove(existing);
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var worst = _alerts.Active.FirstOrDefault();
        Status = worst is null ? "All clear" : $"{worst.Severity}: {worst.Title}";
        StatusBrush = Theme.For(worst?.Severity ?? AlertSeverity.None);
        _worst = worst?.Severity ?? AlertSeverity.None;
        if (_emergency.Active) // outranks any alert: ReTweak itself is holding the card back right now
        {
            Status = _emergencyFailed ? "Emergency: couldn't lower power" : $"Emergency power cut · {_emergencyWatts:F0} W";
            StatusBrush = Theme.Critical;
            _worst = AlertSeverity.Critical;
        }
        StatusChanged?.Invoke();
    }

    public void Dispose()
    {
        _timer.Stop();
        // Exiting during a cut: give the user's limit back, or it stays at the minimum until a reboot (a 100 %
        // setting is left alone at the next start).
        if (_emergency.Active && Elevation.IsAdmin) _monitor.SetPowerLimitPercent(_gpu, _settings.PowerLimitPercent, out _);
        if (_fan is not null) AirflowStore.Save(GpuName, _fan.Airflow.Corrections);
        _fan?.Dispose(); // back to the driver's curve before NVML shuts down
        _sampler?.Dispose();
        _monitor.Dispose();
    }
}
