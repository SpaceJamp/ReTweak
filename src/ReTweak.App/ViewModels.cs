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
    /// <summary>Selected index of the dashboard's Auto / Fixed / Curve buttons; setting it saves the mode.</summary>
    public int FanModeIndex
    {
        get => _fanModeIndex;
        set
        {
            if (value < 0 || value == _fanModeIndex) return;
            Set(ref _fanModeIndex, value);
            var updated = _settings with { Fans = _settings.Fans with { Mode = (FanMode)value } };
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
            HotspotAvailability.Available => "from the GPU via PawnIO",
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
            FanSpeedRange = $"Driver allows {_fan.Range.Min}-{_fan.Range.Max} %";
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

    /// <summary>Applies settings live: thresholds from the next frame, logging on/off, toasts.</summary>
    public void Apply(AppSettings settings)
    {
        _settings = settings;
        Notifications.Enabled = settings.Toasts;
        _alerts.Options = settings.Alerts;
        if (_fan is not null)
        {
            _fan.Options = settings.Fans;
            Set(ref _fanModeIndex, (int)settings.Fans.Mode, nameof(FanModeIndex));
            FanChartLabel = settings.Fans.Mode == FanMode.Auto
                ? "Fan control · the dot is now"
                : "Fan curve · the dot is now";
        }
        if (_sampler is null) return;
        bool logging = _sampler.LogPath is not null;
        if (settings.LogConnector && (!logging || !_sampler.LogPath!.StartsWith(settings.LogFolder, StringComparison.OrdinalIgnoreCase)))
        {
            try { _sampler.SetLog(CsvLogger.Daily(settings.LogFolder)); }
            catch (Exception) { _sampler.SetLog(null); } // bad folder: keep monitoring without logging
        }
        else if (!settings.LogConnector && logging)
            _sampler.SetLog(null);
    }

    public string? LogPath => _sampler?.LogPath;

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
        Fans = s.FanPercents.Count > 0 ? string.Join(" / ", s.FanPercents.Select(f => $"{f}%")) : "-";
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
        try { st = _fan.Tick(new FanTemps(s.CoreTempC, s.HotspotC, s.MemoryTempC, s.HotspotMaxC)); }
        catch (Exception ex) { st = new(_fan.Options.Mode, null, $"Fan control error: {ex.Message}", AlertSeverity.Warning); }
        FanTempC = st.SourceTempC ?? s.CoreTempC;
        // Unelevated, Auto is simply the driver's curve; mention the safety net quietly instead of
        // nagging with the restart button (that's for Fixed and Curve, which can't work at all).
        bool autoUnelevated = st.Mode == FanMode.Auto && !Elevation.IsAdmin;
        FanStatus = autoUnelevated && _fan.Options.AutoAssist ? "Automatic (driver). The safety net works when ReTweak runs as administrator." : st.Text;
        FanNeedsAdmin = st.NeedsAdmin && !autoUnelevated && !Elevation.IsAdmin;
        FanStatusBrush = st.Severity > AlertSeverity.None ? Theme.For(st.Severity) : st.Mode == FanMode.Auto ? Theme.Muted : Theme.Ok;
        FanChanged?.Invoke();
    }

    public FanOptions FanOptions => _settings.Fans;
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
        var vm = new AlertViewModel(a.Key, a.Title, a.Detail, Theme.InfoBar(a.Severity));
        var existing = Alerts.FirstOrDefault(x => x.Key == a.Key);
        if (existing is not null) Alerts[Alerts.IndexOf(existing)] = vm;
        else Alerts.Insert(0, vm);
        RefreshStatus();
        AlertRaised?.Invoke(a);
    }

    private void OnCleared(string key)
    {
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
        StatusChanged?.Invoke();
    }

    public void Dispose()
    {
        _timer.Stop();
        _fan?.Dispose(); // back to the driver's curve before NVML shuts down
        _sampler?.Dispose();
        _monitor.Dispose();
    }
}
