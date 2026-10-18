using ReTweak.Core;

// ReTweak.Cli                 one snapshot of every GPU
// ReTweak.Cli --watch         snapshot every second
// ReTweak.Cli --alerts [--log file.csv] [--duration secs]   live connector monitoring with alerts
//                                         (--log also writes file.gpu.csv with board power/clocks)
// ReTweak.Cli --bench         connector sensor latency / noise
// ReTweak.Cli --simulate      run alert engine against scripted failures (no hardware)
// ReTweak.Cli --replay file.csv   run a logged session through the current alert engine

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

if (args.Contains("--alerts"))
{
    RunAlerts(monitor, args);
    return;
}

bool watch = args.Contains("--watch");
Console.WriteLine($"Driver {monitor.DriverVersion}");
do
{
    foreach (var gpu in monitor.Gpus)
    {
        var s = monitor.Sample(gpu.Index);
        Console.WriteLine($"[{gpu.Index}] {gpu.Name}  (bus {gpu.PciBus}, subsys {gpu.SubVendorId:X4}:{gpu.SubDeviceId:X4})");
        Console.WriteLine($"    temp {s.TemperatureC}°C  core {s.CoreClockMhz} MHz  mem {s.MemoryClockMhz} MHz  load {s.GpuUtilization}%");
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
        string gpuPath = Path.ChangeExtension(args[logIdx + 1], ".gpu.csv");
        bool fresh = !File.Exists(gpuPath);
        gpuLog = new StreamWriter(gpuPath, append: true) { AutoFlush = true };
        if (fresh) gpuLog.WriteLine("time,board_w,limit_w,temp_c,core_mhz,mem_mhz,load_pct");
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
        if (gpuLog is null) continue;
        var s = monitor.Sample(gpu.Index, includeConnector: false);
        gpuLog.WriteLine(string.Join(",", DateTime.Now.ToString("O"), s.PowerWatts?.ToString("F1", inv), s.PowerLimitWatts?.ToString("F0", inv),
                                     s.TemperatureC, s.CoreClockMhz, s.MemoryClockMhz, s.GpuUtilization));
    }
    gpuLog?.Dispose();
    Console.WriteLine("Stopped.");
}
