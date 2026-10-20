using System.Diagnostics;

namespace ReTweak.Core;

/// <summary>The last second of hotspot readings, summarised.</summary>
/// <param name="AverageC">Hotspot averaged over the window: what to show and what the fan curve follows.</param>
/// <param name="MaxC">Highest hotspot reading in the window: what failsafes and the fan safety net use.</param>
/// <param name="Channels">Each thermal channel averaged over the window (#1-#4 sensors, #5 their maximum).</param>
public sealed record HotspotSummary(double AverageC, double MaxC, IReadOnlyList<double?> Channels, int Samples);

/// <summary>
/// A sliding window of thermal-channel readings. The hotspot register updates at least
/// 10 times a second and is jumpy: at light load it spans ~4 °C within a second and moves
/// ~1.4 °C between 0.1 s reads (research/hotspot_probe.py). A single 1 Hz read is noise;
/// a window gives a steady value to show and an honest peak for safety decisions.
/// </summary>
public sealed class HotspotWindow(TimeSpan length)
{
    private readonly Queue<(TimeSpan At, double?[] Channels)> _samples = new();

    public void Add(TimeSpan at, double?[] channels)
    {
        _samples.Enqueue((at, channels));
        Trim(at);
    }

    /// <summary>Null when the window holds no valid hotspot reading.</summary>
    public HotspotSummary? Summarize(TimeSpan now)
    {
        Trim(now);
        var hot = _samples.Select(s => NvidiaHotspot.Hotspot(s.Channels)).OfType<double>().ToList();
        if (hot.Count == 0) return null;
        int count = _samples.Max(s => s.Channels.Length);
        var channels = Enumerable.Range(0, count)
            .Select(i => _samples.Select(s => i < s.Channels.Length ? s.Channels[i] : null).OfType<double>().ToList())
            .Select(v => v.Count > 0 ? v.Average() : (double?)null)
            .ToList();
        return new HotspotSummary(hot.Average(), hot.Max(), channels, hot.Count);
    }

    private void Trim(TimeSpan now)
    {
        while (_samples.Count > 0 && now - _samples.Peek().At > length) _samples.Dequeue();
    }
}

/// <summary>
/// Reads the hotspot channels every 100 ms on a background thread. <see cref="Summary"/> may be
/// called from any thread. Owns the <see cref="NvidiaHotspot"/> handle: nothing else should read it.
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

    private void Run()
    {
        var token = _cts.Token;
        while (!token.WaitHandle.WaitOne(Interval))
        {
            try { ReadOnce(); }
            catch (Exception) { /* a failed read just leaves a gap; the window empties if reads keep failing */ }
        }
    }

    private void ReadOnce()
    {
        if (_hotspot.Read() is not { } channels) return;
        lock (_lock) _window.Add(_clock.Elapsed, channels);
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_thread.IsAlive && !_thread.Join(TimeSpan.FromSeconds(1))) return; // still inside the driver; don't close under it
        _hotspot.Dispose();
        _cts.Dispose();
    }
}
