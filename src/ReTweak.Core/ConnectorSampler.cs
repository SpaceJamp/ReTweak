using System.Diagnostics;

namespace ReTweak.Core;

/// <summary>
/// A new frame from the connector sensor, with raw and smoothed values.
/// <paramref name="Uptime"/> is monotonic (unaffected by clock changes) and is what
/// timing logic uses; <paramref name="Time"/> is wall-clock time for display/logs.
/// </summary>
public sealed record ConnectorFrame(DateTime Time, TimeSpan Uptime, ConnectorReading Raw, ConnectorReading Smoothed);

/// <summary>
/// Polls the connector sensor on a background thread. The ASUS chip publishes new
/// values every ~600 ms; polling at 100 ms picks each one up within 100 ms
/// while using ~4% of the I2C bus. Duplicate reads are dropped, so
/// subscribers and the alert engine see each frame exactly once.
/// </summary>
public sealed class ConnectorSampler : IDisposable
{
    private readonly GpuMonitor _monitor;
    private readonly int _gpuIndex;
    private readonly TimeSpan _interval;
    private readonly double _smoothing;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();
    private CsvLogger? _log;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private ConnectorReading? _lastRaw;
    private TimeSpan _lastChange, _lastFrameAt;
    private readonly double?[] _emaVolts = new double?[6], _emaAmps = new double?[6];

    public AlertEngine Alerts { get; }

    /// <summary>Starts or stops CSV logging at runtime. The previous logger is closed.</summary>
    public void SetLog(CsvLogger? log) => Interlocked.Exchange(ref _log, log)?.Dispose();

    /// <summary>File currently being logged to, or null.</summary>
    public string? LogPath => Volatile.Read(ref _log)?.Path;

    /// <summary>Raised on the sampler thread for every new frame.</summary>
    public event Action<ConnectorFrame>? FrameReceived;

    /// <summary>Raised on the sampler thread if a subscriber or the logger throws. Monitoring continues.</summary>
    public event Action<Exception>? Error;

    /// <param name="smoothing">EMA weight for new frames (0..1]; 0.3 averages over ~3 frames (~0.6 s).</param>
    public ConnectorSampler(GpuMonitor monitor, int gpuIndex, AlertEngine? alerts = null,
                            TimeSpan? interval = null, double smoothing = 0.3, CsvLogger? log = null)
    {
        if (!monitor.HasConnectorSensor(gpuIndex))
            throw new InvalidOperationException("This GPU has no connector sensor.");
        _monitor = monitor;
        _gpuIndex = gpuIndex;
        _interval = interval ?? TimeSpan.FromMilliseconds(100);
        _smoothing = Math.Clamp(smoothing, 0.01, 1);
        _log = log;
        Alerts = alerts ?? new AlertEngine();
        _thread = new Thread(Run) { IsBackground = true, Name = "ConnectorSampler", Priority = ThreadPriority.AboveNormal };
    }

    public void Start() => _thread.Start();

    private void Run()
    {
        var token = _cts.Token;
        var next = _clock.Elapsed; // monotonic: wall-clock jumps can't stall or burst the loop
        while (!token.IsCancellationRequested)
        {
            // A safety monitor must not die because a subscriber, the logger or a read threw.
            try { Tick(); }
            catch (Exception ex)
            {
                Diag.Error(DiagCategory.Connector, "connector sampler tick threw", ex);
                try { Error?.Invoke(ex); } catch { }
            }

            next += _interval;
            var wait = next - _clock.Elapsed;
            if (wait > TimeSpan.Zero) token.WaitHandle.WaitOne(wait);
            else next = _clock.Elapsed; // fell behind; don't burst to catch up
        }
    }

    private void Tick()
    {
        var uptime = _clock.Elapsed;
        var raw = _monitor.ReadConnector(_gpuIndex);

        if (raw is null)
        {
            Diag.Warn(DiagCategory.Connector, "connector read failed");
            Alerts.ReadFailed(uptime);
            return;
        }
        if (_lastRaw is not null && raw.Pins.SequenceEqual(_lastRaw.Pins))
        {
            // Reads succeed but the chip hasn't published anything new.
            Alerts.NoNewData(uptime, uptime - _lastChange);
            return;
        }

        _lastRaw = raw;
        _lastChange = uptime;
        var frame = new ConnectorFrame(DateTime.Now, uptime, raw, Smooth(raw));
        if (Diag.IsOn(DiagLevel.Debug))
            Diag.Debug(DiagCategory.Connector, "frame", new
            {
                pins = raw.Pins.Select(p => new { p.Pin, v = Math.Round(p.Volts, 3), a = Math.Round(p.Amps, 3), p.Valid }),
                totalW = Math.Round(raw.TotalWatts, 1),
                imbalance = Math.Round(raw.Imbalance, 3),
                sincePreviousMs = Math.Round((uptime - _lastFrameAt).TotalMilliseconds),
            });
        _lastFrameAt = uptime;
        Alerts.Process(frame);
        Volatile.Read(ref _log)?.Write(frame);
        FrameReceived?.Invoke(frame);
    }

    private ConnectorReading Smooth(ConnectorReading raw)
    {
        var pins = new PinReading[raw.Pins.Count];
        for (int i = 0; i < pins.Length; i++)
        {
            var p = raw.Pins[i];
            if (p.Valid && i < _emaVolts.Length)
            {
                // Seed from the first *valid* value so a glitched first frame can't poison the average.
                _emaVolts[i] = _emaVolts[i] is double v ? v + _smoothing * (p.Volts - v) : p.Volts;
                _emaAmps[i] = _emaAmps[i] is double a ? a + _smoothing * (p.Amps - a) : p.Amps;
            }
            bool have = i < _emaVolts.Length && _emaVolts[i].HasValue;
            pins[i] = new PinReading(p.Pin, _emaVolts[i] ?? 0, _emaAmps[i] ?? 0, p.Valid && have);
        }
        return new ConnectorReading(pins);
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_thread.IsAlive && !_thread.Join(TimeSpan.FromSeconds(2)))
            return; // still inside a driver call; let the background thread finish on its own
        _cts.Dispose();
        SetLog(null);
    }
}
