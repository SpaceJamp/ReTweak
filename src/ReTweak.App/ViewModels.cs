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

public sealed record AlertViewModel(string Key, string Title, string Detail, InfoBarSeverity Severity);

public sealed partial class MainViewModel : Observable, IDisposable
{
    public const int HistoryLength = 300; // 60 s at the chip's ~5 Hz

    private readonly GpuMonitor _monitor;
    private readonly ConnectorSampler? _sampler;
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

    public string Temperature { get => _temp; private set => Set(ref _temp, value); }
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

        if (monitor.HasConnectorSensor(_gpu))
        {
            _sampler = new ConnectorSampler(monitor, _gpu, new AlertEngine(settings.Alerts));
            _sampler.FrameReceived += f => _ui.TryEnqueue(() => OnFrame(f));
            _sampler.Alerts.Raised += a => _ui.TryEnqueue(() => OnAlert(a));
            _sampler.Alerts.Cleared += k => _ui.TryEnqueue(() => OnCleared(k));
            _sampler.Start();
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
        if (_sampler is null) return;
        _sampler.Alerts.Options = settings.Alerts;
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
        Temperature = s.TemperatureC is uint t ? $"{t} °C" : "-";
        CoreClock = s.CoreClockMhz is uint c ? $"{c} MHz" : "-";
        MemoryClock = s.MemoryClockMhz is uint m ? $"{m} MHz" : "-";
        Load = s.GpuUtilization is uint u ? $"{u} %" : "-";
        Power = s.PowerWatts is double w ? $"{w:F0} W" : "-";
        PowerLimit = s.PowerLimitWatts is double l ? $"of {l:F0} W limit" : "";
        PowerPercent = s.PowerWatts is double pw && s.PowerLimitWatts is double pl && pl > 0 ? Math.Clamp(pw / pl * 100, 0, 100) : 0;
        Fans = s.FanPercents.Count > 0 ? string.Join(" / ", s.FanPercents.Select(f => $"{f}%")) : "-";
        if (s.VramTotalBytes is ulong total && s.VramUsedBytes is ulong used)
        {
            Vram = $"{used / 1073741824.0:F1} / {total / 1073741824.0:F0} GB";
            VramPercent = (double)used / total * 100;
        }
    }

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
        var worst = _sampler?.Alerts.Active.FirstOrDefault();
        Status = worst is null ? "All clear" : $"{worst.Severity}: {worst.Title}";
        StatusBrush = Theme.For(worst?.Severity ?? AlertSeverity.None);
        _worst = worst?.Severity ?? AlertSeverity.None;
        StatusChanged?.Invoke();
    }

    public void Dispose()
    {
        _timer.Stop();
        _sampler?.Dispose();
        _monitor.Dispose();
    }
}
