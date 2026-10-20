using ReTweak.Core;

// ReTweak.Cli                 one snapshot of every GPU
// ReTweak.Cli --watch         snapshot every second
// ReTweak.Cli --alerts [--log file.csv] [--duration secs]   live connector monitoring with alerts
//                                         (--log also writes file.gpu.csv with board power/clocks)
// ReTweak.Cli --bench         connector sensor latency / noise
// ReTweak.Cli --simulate      run alert engine against scripted failures (no hardware)
// ReTweak.Cli --replay file.csv   run a logged session through the current alert engine
// ReTweak.Cli --fans          fan count, allowed range, current and target speeds
// ReTweak.Cli --fan <pct>|curve|smart [--duration secs]   drive the fans until Ctrl+C, then hand them back (admin)
// ReTweak.Cli --fan auto      hand the fans back to the driver (e.g. after a crash) (admin)
// add --debug to any of these for a full diagnostic log

// --debug: full diagnostic log (JSON lines) to %LocalAppData%\ReTweak\diagnostics, like the app's --debug.
if (args.Contains("--debug"))
{
    string diagFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReTweak", "diagnostics");
    Diag.Configure(DiagLevel.Trace, diagFolder);
    Diag.Info(DiagCategory.App, "cli started", new { args, admin = Environment.IsPrivilegedProcess });
    AppDomain.CurrentDomain.ProcessExit += (_, _) => Diag.Shutdown();
    Console.WriteLine($"Diagnostic log: {diagFolder}");
}

int replayIdx = Array.IndexOf(args, "--replay");
if (replayIdx >= 0 && replayIdx + 1 < args.Length)
{
    Environment.ExitCode = Simulate.Replay(args[replayIdx + 1]) > 0 ? 1 : 0;
    return;
}

if (args.Contains("--simulate"))
{
    Environment.ExitCode = Simulate.Run();
    return;
}

using var monitor = new GpuMonitor();

if (args.Contains("--bench"))
{
    Bench.Run(monitor);
    return;
}

if (args.Contains("--fans"))
{
    foreach (var gpu in monitor.Gpus)
    {
        var range = monitor.FanSpeedRange(gpu.Index);
        var s = monitor.Sample(gpu.Index, includeConnector: false);
        Console.WriteLine($"[{gpu.Index}] {gpu.Name}: {monitor.FanCount(gpu.Index)} fan(s), manual range " +
                          (range is var (min, max) ? $"{min}-{max}%" : "unknown"));
        for (int f = 0; f < monitor.FanCount(gpu.Index); f++)
            Console.WriteLine($"    fan {f}: {(f < s.FanPercents.Count ? $"{s.FanPercents[f]}%" : "-")}  target {monitor.FanTarget(gpu.Index, f)?.ToString() ?? "-"}%");
    }
    return;
}

int fanIdx = Array.IndexOf(args, "--fan");
if (fanIdx >= 0)
{
    Environment.ExitCode = RunFan(monitor, fanIdx + 1 < args.Length ? args[fanIdx + 1] : "", args);
    return;
}

if (args.Contains("--alerts"))
{
    RunAlerts(monitor, args);
    return;
}

bool watch = args.Contains("--watch");
Console.WriteLine($"Driver {monitor.DriverVersion}");
// The hotspot is sampled at 10 Hz and averaged over a second: give the window time to fill.
if (monitor.Gpus.Any(g => monitor.HasHotspot(g.Index))) Thread.Sleep(GpuMonitor.HotspotWindow);
do
{
    foreach (var gpu in monitor.Gpus)
    {
        var s = monitor.Sample(gpu.Index);
        Console.WriteLine($"[{gpu.Index}] {gpu.Name}  (bus {gpu.PciBus}, subsys {gpu.SubVendorId:X4}:{gpu.SubDeviceId:X4})");
        Console.WriteLine($"    core {s.CoreTempC:F2}°C" +
                          (s.HotspotC is double hs ? $"  hotspot {hs:F1}°C (1 s avg, peak {s.HotspotMaxC:F1})" : $"  hotspot - ({monitor.HotspotNote(gpu.Index)}" +
                                                         (monitor.HotspotState(gpu.Index) == HotspotAvailability.PawnIoMissing ? "; optional, see https://pawnio.eu/" : "") + ")") +
                          (s.MemoryTempC is double mt ? $"  memory {mt:F1}°C ({s.MemoryTempSource})" : "") +
                          $"  core clock {s.CoreClockMhz} MHz  mem clock {s.MemoryClockMhz} MHz  load {s.GpuUtilization}%");
        if (s.ThermalChannels is { } ch)
            Console.WriteLine("    gpu thermal channels: " + string.Join("  ", ch.Select((c, i) => $"#{i + 1} {(c is double v ? $"{v:F1}°C" : "-")}")) + "  (#5 = max of #1-#4 = hotspot)");
        if (s.BoardTemps is { } board)
            Console.WriteLine("    board: " + string.Join("  ", board.Select(b => $"{b.Name} {(b.Celsius is double c ? $"{c:F1}°C" : "-")}")));
        Console.WriteLine($"    power {s.PowerWatts:F1} / {s.PowerLimitWatts:F0} W  fans {string.Join(", ", s.FanPercents.Select(f => $"{f}%"))}");
        if (s.VramTotalBytes is ulong total)
            Console.WriteLine($"    vram {s.VramUsedBytes / 1048576} / {total / 1048576} MiB");

        if (s.Connector is { } c)
        {
            foreach (var p in c.Pins)
                Console.WriteLine($"    pin {p.Pin}: {p.Volts,6:F3} V  {p.Amps,5:F2} A  {p.Watts,5:F1} W{(p.Valid ? "" : "  (bad read)")}");
            Console.WriteLine($"    connector: {c.TotalAmps:F2} A  {c.TotalWatts:F1} W  imbalance x{c.Imbalance:F2}");
        }
        else if (monitor.HasConnectorSensor(gpu.Index))
            Console.WriteLine("    connector: read failed");
    }
    if (watch) { Thread.Sleep(1000); Console.WriteLine(); }
} while (watch);

static int RunFan(GpuMonitor monitor, string mode, string[] args)
{
    var gpu = monitor.Gpus.FirstOrDefault(g => monitor.FanCount(g.Index) > 0);
    if (gpu is null) { Console.WriteLine("No GPU with controllable fans found."); return 1; }

    if (mode.Equals("auto", StringComparison.OrdinalIgnoreCase))
    {
        var err = monitor.RestoreAutoFans(gpu.Index);
        Console.WriteLine(err is null ? "Fans are back on the driver's automatic curve." : $"Couldn't restore automatic fans: {err}");
        return err is null ? 0 : 1;
    }

    FanOptions options;
    if (mode.Equals("curve", StringComparison.OrdinalIgnoreCase)) options = new() { Mode = FanMode.Curve };
    else if (mode.Equals("smart", StringComparison.OrdinalIgnoreCase)) options = new() { Mode = FanMode.Smart };
    else if (int.TryParse(mode, out int pct) && pct is >= 0 and <= 100) options = new() { Mode = FanMode.Fixed, FixedPercent = pct };
    else { Console.WriteLine("Usage: --fan <0-100> | curve | smart | auto  [--duration secs]"); return 1; }

    int durIdx = Array.IndexOf(args, "--duration");
    var duration = durIdx >= 0 && durIdx + 1 < args.Length && int.TryParse(args[durIdx + 1], out int secs)
        ? TimeSpan.FromSeconds(secs) : Timeout.InfiniteTimeSpan;

    using var fans = new FanController(monitor, gpu.Index) { Options = options };
    var done = new ManualResetEventSlim();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => fans.Dispose(); // console closed: still hand the fans back

    Console.WriteLine($"Controlling {gpu.Name} fans ({options.Mode}, failsafe {options.FailsafeTempC:F0} °C). Ctrl+C to stop.");
    var started = System.Diagnostics.Stopwatch.StartNew();
    int result = 0;
    do
    {
        var s = monitor.Sample(gpu.Index, includeConnector: false);
        var st = fans.Tick(new FanTemps(s.CoreTempC, s.HotspotC, s.MemoryTempC, s.HotspotMaxC, s.PowerWatts / s.PowerLimitWatts));
        Console.WriteLine($"{DateTime.Now:HH:mm:ss}  core {s.CoreTempC:F1} °C  fans {string.Join("/", s.FanPercents.Select(f => $"{f}%"))}  {st.Text}");
        if (st.NeedsAdmin) { result = 1; break; } // nothing to do until rerun as administrator
    } while (!done.Wait(1000) && (duration == Timeout.InfiniteTimeSpan || started.Elapsed < duration));

    fans.Dispose();
    Console.WriteLine("Fans handed back to the driver.");
    return result;
}

static void RunAlerts(GpuMonitor monitor, string[] args)
{
    var gpu = monitor.Gpus.FirstOrDefault(g => monitor.HasConnectorSensor(g.Index));
    if (gpu is null) { Console.WriteLine("No connector sensor found."); return; }

    int logIdx = Array.IndexOf(args, "--log");
    var log = logIdx >= 0 && logIdx + 1 < args.Length ? new CsvLogger(args[logIdx + 1]) : null;

    using var sampler = new ConnectorSampler(monitor, gpu.Index, log: log);
    sampler.Alerts.Raised += a =>
    {
        Console.ForegroundColor = a.Severity == AlertSeverity.Critical ? ConsoleColor.Red : ConsoleColor.Yellow;
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {a.Severity}: {a.Title} - {a.Detail}");
        Console.ResetColor();
        // Beep off-thread: alert events run on the sampler thread, and a 300 ms beep there would drop frames.
        if (a.Severity == AlertSeverity.Critical) Task.Run(() => { if (OperatingSystem.IsWindows()) Console.Beep(1200, 300); });
    };
    sampler.Error += ex => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  sampler error (still running): {ex.Message}");
    sampler.Alerts.Cleared += k => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  cleared: {k}");

    var lastPrint = DateTime.MinValue;
    sampler.FrameReceived += f =>
    {
        if (f.Time - lastPrint < TimeSpan.FromSeconds(1)) return;
        lastPrint = f.Time;
        var s = f.Smoothed;
        Console.WriteLine($"{f.Time:HH:mm:ss}  " + string.Join("  ", s.Pins.Select(p => $"{p.Pin}:{p.Volts:F3}V/{p.Amps:F2}A")) +
                          $"  | {s.TotalWatts:F0} W");
    };

    int durIdx = Array.IndexOf(args, "--duration");
    var duration = durIdx >= 0 && durIdx + 1 < args.Length && int.TryParse(args[durIdx + 1], out int secs)
        ? TimeSpan.FromSeconds(secs) : Timeout.InfiniteTimeSpan;

    // Alongside the connector log, record the driver's view once a second (board power includes the PCIe slot).
    StreamWriter? gpuLog = null;
    if (logIdx >= 0 && logIdx + 1 < args.Length)
    {
        const string header = "time,board_w,limit_w,temp_c,core_mhz,mem_mhz,load_pct,mem_temp_c,hotspot_c,hotspot_max_c";
        string gpuPath = Path.ChangeExtension(args[logIdx + 1], ".gpu.csv");
        // An older log with different columns: start a new file rather than append rows that don't match its header.
        if (File.Exists(gpuPath) && File.ReadLines(gpuPath).FirstOrDefault() is { } existing && existing != header)
        {
            gpuPath = Path.ChangeExtension(args[logIdx + 1], $".gpu.{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            Console.WriteLine($"{Path.GetFileName(Path.ChangeExtension(args[logIdx + 1], ".gpu.csv"))} has older columns; writing {Path.GetFileName(gpuPath)} instead.");
        }
        bool fresh = !File.Exists(gpuPath) || new FileInfo(gpuPath).Length == 0;
        gpuLog = new StreamWriter(gpuPath, append: true) { AutoFlush = true };
        if (fresh) gpuLog.WriteLine(header);
    }

    Console.WriteLine($"Monitoring {gpu.Name} connector. Ctrl+C to stop." + (log is null ? "" : " Logging to CSV.") +
                      (duration == Timeout.InfiniteTimeSpan ? "" : $" Stopping after {duration.TotalSeconds:F0} s."));
    sampler.Start();
    var done = new ManualResetEventSlim();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };

    var inv = System.Globalization.CultureInfo.InvariantCulture;
    var started = System.Diagnostics.Stopwatch.StartNew();
    while (!done.Wait(1000) && (duration == Timeout.InfiniteTimeSpan || started.Elapsed < duration))
    {
        var s = monitor.Sample(gpu.Index, includeConnector: false);
        sampler.Alerts.ProcessGpu(s); // core / memory temperature alerts
        gpuLog?.WriteLine(string.Join(",", DateTime.Now.ToString("O"), s.PowerWatts?.ToString("F1", inv), s.PowerLimitWatts?.ToString("F0", inv),
                                      s.CoreTempC?.ToString("F2", inv), s.CoreClockMhz, s.MemoryClockMhz, s.GpuUtilization,
                                      s.MemoryTempC?.ToString("F0", inv), s.HotspotC?.ToString("F2", inv),
                                      s.HotspotMaxC?.ToString("F2", inv)));
    }
    gpuLog?.Dispose();
    Console.WriteLine("Stopped.");
}
