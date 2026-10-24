using System.Diagnostics;

namespace ReTweak.Core;

/// <summary>The last second of hotspot readings, summarised.</summary>
/// <param name="AverageC">Hotspot averaged over the window: what to show and what the fan curve follows.</param>
/// <param name="MaxC">Highest hotspot reading in the window: what failsafes and the fan safety net use.</param>
/// <param name="Channels">Each thermal channel averaged over the window (#1-#4 sensors, #5 their maximum, #6 the die average).</param>
/// <param name="SpreadC">Die spread (hotspot minus the coolest sensor) averaged over the window; null without all four sensors.</param>
public sealed record HotspotSummary(double AverageC, double MaxC, IReadOnlyList<double?> Channels, int Samples, double? SpreadC = null)
{
    /// <summary>NVIDIA's die average (channel #6), or null.</summary>
    public double? DieAverageC => Channels.Count > NvidiaHotspot.AverageChannel ? Channels[NvidiaHotspot.AverageChannel] : null;
}

/// <summary>
/// A sliding window of thermal-channel readings. The hotspot register updates at least
/// 10 times a second and is jumpy: at light load it spans ~4 °C within a second and moves
/// ~1.4 °C between 0.1 s reads (research/hotspot_probe.py). A single 1 Hz read is noise;
/// a window gives a steady value to show and an honest peak for safety decisions.
/// </summary>
public sealed class HotspotWindow(TimeSpan length)
{
    private readonly Queue<(TimeSpan At, double?[] Channels)> _samples = new();

    /// <summary>False (and nothing stored) for a read with no usable hotspot, including one that contradicts itself.</summary>
    public bool Add(TimeSpan at, double?[] channels)
    {
        Trim(at);
        if (NvidiaHotspot.Hotspot(channels) is null) return false;
        _samples.Enqueue((at, channels));
        return true;
    }

    /// <summary>Null when the window holds no valid hotspot reading.</summary>
    public HotspotSummary? Summarize(TimeSpan now)
    {
        Trim(now);
        var hot = _samples.Select(s => NvidiaHotspot.Hotspot(s.Channels)).OfType<double>().ToList();
        var spread = _samples.Select(s => NvidiaHotspot.Spread(s.Channels)).OfType<double>().ToList();
        if (hot.Count == 0) return null;
        int count = _samples.Max(s => s.Channels.Length);
        var channels = Enumerable.Range(0, count)
            .Select(i => _samples.Select(s => i < s.Channels.Length ? s.Channels[i] : null).OfType<double>().ToList())
            .Select(v => v.Count > 0 ? v.Average() : (double?)null)
            .ToList();
        return new HotspotSummary(hot.Average(), hot.Max(), channels, hot.Count, spread.Count > 0 ? spread.Average() : null);
    }

    private void Trim(TimeSpan now)
    {
        while (_samples.Count > 0 && now - _samples.Peek().At > length) _samples.Dequeue();
    }
}

/// <summary>
/// Reads the hotspot channels every 100 ms on a background thread. <see cref="Summary"/> may be
/// called from any thread. Owns the <see cref="NvidiaHotspot"/> handle: nothing else should read it.
/// Also reads the memory-chip temperatures once a second (<see cref="MemoryChips"/>).
/// </summary>
internal sealed class HotspotSampler : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100), WindowLength = TimeSpan.FromSeconds(1);

    private readonly NvidiaHotspot _hotspot;
    private readonly HotspotWindow _window = new(WindowLength);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _lock = new();
    private int _tick;
    private long _rejected;
    private int[]? _memoryChips;
    private bool _memoryUnsupported;

    public HotspotSampler(NvidiaHotspot hotspot)
    {
        _hotspot = hotspot;
        ReadOnce(); // so a one-shot snapshot (the CLI) has a reading straight away
        _thread = new Thread(Run) { IsBackground = true, Name = "HotspotSampler" };
        _thread.Start();
    }

    public HotspotSummary? Summary()
    {
        lock (_lock) return _window.Summarize(_clock.Elapsed);
    }

    /// <summary>Latest memory-chip temperatures (°C), or null if the GPU doesn't report them.</summary>
    public int[]? MemoryChips()
    {
        lock (_lock) return _memoryChips;
    }

    private void Run()
    {
        var token = _cts.Token;
        while (!token.WaitHandle.WaitOne(Interval))
        {
            try { ReadOnce(); }
            catch (Exception ex) { Diag.Error(DiagCategory.Hotspot, "hotspot read threw", ex); } // a gap; the window empties if reads keep failing
        }
    }

    private void ReadOnce()
    {
        double?[]? channels;
        using (var timer = Diag.Time("hotspot.thermal_registers"))
        {
            channels = _hotspot.Read();
            if (channels is null) timer.Fail("ioctl failed");
        }
        if (channels is not null)
        {
            Diag.Trace(DiagCategory.Hotspot, "raw", new { channelsC = channels, hotspotC = NvidiaHotspot.Hotspot(channels), spreadC = NvidiaHotspot.Spread(channels) });
            bool kept;
            lock (_lock) kept = _window.Add(_clock.Elapsed, channels);
            // A maximum below the coolest sensor is a bad read, not a temperature. Log the first and then
            // every 600th (once a minute if it never stops) so a log shows it without filling up.
            if (!kept && channels.Length > NvidiaHotspot.HotspotChannel && channels[NvidiaHotspot.HotspotChannel] is not null
                && _rejected++ % 600 == 0)
                Diag.Warn(DiagCategory.Hotspot, "hotspot read rejected: maximum below the coolest sensor", new { channelsC = channels, rejected = _rejected });
        }

        // Memory chips change slowly: once a second is plenty. Stop asking if the module says no.
        if (_memoryUnsupported || _tick++ % 10 != 0) return;
        int[]? chips;
        using (var timer = Diag.Time("hotspot.memory_temperatures"))
        {
            chips = _hotspot.ReadMemoryChips();
            if (chips is null) timer.Fail("no memory sensors");
        }
        if (chips is null && _tick == 1)
        {
            _memoryUnsupported = true;
            Diag.Info(DiagCategory.Hotspot, "memory-chip temperatures not available on this GPU");
        }
        lock (_lock) _memoryChips = chips;
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_thread.IsAlive && !_thread.Join(TimeSpan.FromSeconds(1))) return; // still inside the driver; don't close under it
        _hotspot.Dispose();
        _cts.Dispose();
    }
}
