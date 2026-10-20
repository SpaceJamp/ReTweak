using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace ReTweak.Core;

public enum DiagLevel { Trace = 0, Debug = 1, Info = 2, Warn = 3, Error = 4, Off = 5 }

/// <summary>What a diagnostic line is about; lets a reader filter a busy log.</summary>
public enum DiagCategory { App, Settings, Sensor, Connector, Hotspot, Fan, Alert, Driver, Timing, Error }

/// <summary>
/// Diagnostic log for figuring out problems. Writes JSON lines
/// (<c>{"t":…,"lvl":…,"cat":…,"msg":…,…fields}</c>) to rotating files in a folder, from a background
/// thread so sensor loops never wait on the disk. Off by default; when off, a call costs one
/// comparison. Driver-call timing is aggregated (<see cref="Time"/>) and flushed as one line per call
/// every <see cref="TimingInterval"/>, with individual lines only for slow or failed calls.
/// Thread-safe.
/// </summary>
public static class Diag
{
    public const long MaxFileBytes = 10 * 1024 * 1024;
    public const int MaxFiles = 10;
    public static readonly TimeSpan TimingInterval = TimeSpan.FromSeconds(10);
    /// <summary>A single driver call slower than this is logged on its own (Warn).</summary>
    public static readonly TimeSpan SlowCall = TimeSpan.FromMilliseconds(50);

    private static volatile DiagLevel _level = DiagLevel.Off;
    private static Channel<string>? _queue;
    private static Task? _writer;
    private static string? _folder, _currentFile;
    private static readonly Lock Gate = new();
    private static readonly ConcurrentDictionary<string, CallStats> Calls = new();
    private static Timer? _timingTimer;
    private static long _dropped;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static DiagLevel Level => _level;
    public static string? Folder => _folder;
    /// <summary>File being written now, or null when off.</summary>
    public static string? CurrentFile => _currentFile;

    public static bool IsOn(DiagLevel level) => level >= _level && _level != DiagLevel.Off;

    /// <summary>Starts (or reconfigures) logging. <see cref="DiagLevel.Off"/> stops it.</summary>
    public static void Configure(DiagLevel level, string folder)
    {
        lock (Gate)
        {
            if (level == DiagLevel.Off) { StopLocked(); return; }
            bool restart = _queue is null || !string.Equals(folder, _folder, StringComparison.OrdinalIgnoreCase);
            _level = level;
            if (!restart) return;
            StopLocked();
            _level = level;
            Directory.CreateDirectory(folder);
            _folder = folder;
            _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(20_000)
            {
                FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true,
            });
            var queue = _queue;
            _writer = Task.Run(() => WriteLoop(queue, folder));
            _timingTimer = new Timer(_ => FlushTiming(), null, TimingInterval, TimingInterval);
        }
        Write(DiagLevel.Info, DiagCategory.App, "diagnostics started", new { level = level.ToString(), folder });
    }

    /// <summary>Flushes pending lines and stops. Safe to call at exit.</summary>
    public static void Shutdown()
    {
        lock (Gate) StopLocked();
    }

    private static void StopLocked()
    {
        if (_queue is null) { _level = DiagLevel.Off; return; }
        FlushTiming();
        _timingTimer?.Dispose();
        _timingTimer = null;
        _queue.Writer.TryComplete();
        try { _writer?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _queue = null;
        _writer = null;
        _currentFile = null;
        _level = DiagLevel.Off;
    }

    public static void Trace(DiagCategory cat, string msg, object? fields = null) => Write(DiagLevel.Trace, cat, msg, fields);
    public static void Debug(DiagCategory cat, string msg, object? fields = null) => Write(DiagLevel.Debug, cat, msg, fields);
    public static void Info(DiagCategory cat, string msg, object? fields = null) => Write(DiagLevel.Info, cat, msg, fields);
    public static void Warn(DiagCategory cat, string msg, object? fields = null) => Write(DiagLevel.Warn, cat, msg, fields);

    public static void Error(DiagCategory cat, string msg, Exception? ex = null, object? fields = null) =>
        Write(DiagLevel.Error, cat, msg, ex is null ? fields : new { fields, exception = ex.GetType().FullName, message = ex.Message, stack = ex.ToString() });

    /// <summary>Queues one line. Fields are any object or anonymous type, serialized as a nested "data" object.</summary>
    public static void Write(DiagLevel level, DiagCategory cat, string msg, object? fields = null)
    {
        if (!IsOn(level) || _queue is not { } queue) return;
        string line;
        try
        {
            var sb = new StringBuilder(160);
            sb.Append("{\"t\":\"").Append(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"))
              .Append("\",\"lvl\":\"").Append(level).Append("\",\"cat\":\"").Append(cat)
              .Append("\",\"thr\":").Append(Environment.CurrentManagedThreadId)
              .Append(",\"msg\":").Append(JsonSerializer.Serialize(msg, Json));
            if (fields is not null) sb.Append(",\"data\":").Append(JsonSerializer.Serialize(fields, fields.GetType(), Json));
            line = sb.Append('}').ToString();
        }
        catch (Exception ex) { line = $"{{\"lvl\":\"Error\",\"cat\":\"Error\",\"msg\":\"log line couldn't be serialized: {ex.GetType().Name}\"}}"; }
        if (!queue.Writer.TryWrite(line)) Interlocked.Increment(ref _dropped);
    }

    // ---- driver-call timing ----

    private sealed class CallStats
    {
        private readonly Lock _lock = new();
        private readonly List<double> _ms = [];
        private int _failures;
        private string? _lastFailure;

        public void Add(double ms, string? failure)
        {
            lock (_lock)
            {
                _ms.Add(ms);
                if (failure is not null) { _failures++; _lastFailure = failure; }
            }
        }

        public object? TakeSummary()
        {
            lock (_lock)
            {
                if (_ms.Count == 0) return null;
                _ms.Sort();
                var s = new
                {
                    count = _ms.Count,
                    meanMs = Math.Round(_ms.Average(), 3),
                    p95Ms = Math.Round(_ms[(int)Math.Min(_ms.Count - 1, Math.Ceiling(_ms.Count * 0.95) - 1)], 3),
                    maxMs = Math.Round(_ms[^1], 3),
                    failures = _failures,
                    lastFailure = _lastFailure,
                };
                _ms.Clear();
                _failures = 0;
                _lastFailure = null;
                return s;
            }
        }
    }

    /// <summary>
    /// Times a driver call: <c>using var t = Diag.Time("nvml.temperature"); … t.Fail("rc 3");</c>.
    /// Free when diagnostics are off.
    /// </summary>
    public static CallTimer Time(string call) => IsOn(DiagLevel.Debug) ? new CallTimer(call, Stopwatch.GetTimestamp()) : default;

    public struct CallTimer(string? call, long start) : IDisposable
    {
        private string? _failure;

        /// <summary>Marks the call as failed (logged with the timing).</summary>
        public void Fail(string why) => _failure = why;

        public readonly void Dispose()
        {
            if (call is null) return;
            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Calls.GetOrAdd(call, _ => new CallStats()).Add(ms, _failure);
            if (_failure is not null) Warn(DiagCategory.Driver, "driver call failed", new { call, ms = Math.Round(ms, 3), failure = _failure });
            else if (ms >= SlowCall.TotalMilliseconds) Warn(DiagCategory.Driver, "slow driver call", new { call, ms = Math.Round(ms, 3) });
        }
    }

    /// <summary>Writes one Timing line per call seen since the last flush.</summary>
    public static void FlushTiming()
    {
        foreach (var (call, stats) in Calls)
            if (stats.TakeSummary() is { } summary) Write(DiagLevel.Debug, DiagCategory.Timing, call, summary);
        if (Interlocked.Exchange(ref _dropped, 0) is var dropped and > 0)
            Write(DiagLevel.Warn, DiagCategory.App, "log lines dropped (writer behind)", new { dropped });
    }

    // ---- files ----

    private static async Task WriteLoop(Channel<string> queue, string folder)
    {
        StreamWriter? file = null;
        long size = 0;
        try
        {
            await foreach (var line in queue.Reader.ReadAllAsync())
            {
                if (file is null || size >= MaxFileBytes)
                {
                    file?.Dispose();
                    (file, size) = OpenNext(folder);
                }
                file.WriteLine(line);
                size += line.Length + 2;
                if (queue.Reader.Count == 0) file.Flush(); // flush when caught up, so a crash loses little
            }
        }
        catch (Exception) { /* disk full or folder gone: diagnostics must never take the app down */ }
        finally { file?.Dispose(); }
    }

    private static (StreamWriter, long) OpenNext(string folder)
    {
        // Never follow a junction or symlink here: an elevated ReTweak must not be steered into creating or
        // deleting files elsewhere. CreateNew refuses an existing file (or a planted link) instead of appending.
        if (!TrustedPaths.IsPlainPath(folder)) throw new IOException($"{folder} is (or is inside) a junction or symlink");
        string path = Path.Combine(folder, $"diag-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}.jsonl");
        var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = false };
        _currentFile = path;
        foreach (var old in Directory.GetFiles(folder, "diag-*.jsonl").OrderByDescending(f => f).Skip(MaxFiles))
        {
            try
            {
                if ((File.GetAttributes(old) & FileAttributes.ReparsePoint) == 0 && TrustedPaths.IsPlainPath(folder)) File.Delete(old);
            }
            catch (Exception) { /* in use or gone */ }
        }
        return (writer, 0);
    }

    /// <summary>Diagnostic log files, newest first.</summary>
    public static IReadOnlyList<string> LogFiles(string folder) =>
        Directory.Exists(folder) ? Directory.GetFiles(folder, "diag-*.jsonl").OrderByDescending(f => f).ToList() : [];
}
