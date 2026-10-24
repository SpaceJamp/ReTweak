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
// ReTweak.Cli --run [--duration secs]   what the app does, headless: your saved settings (Auto, power limit,
//                                         emergency cut, alerts) until Ctrl+C. Windows (admin) or Linux (root)
// ReTweak.Cli --settings      the saved settings and where they live
// ReTweak.Cli --set <setting> <value>   change one, checked like the Settings dialog: --set Fans.Mode Smart
//                                         (under sudo this is root's settings, the ones the service uses)
// add --debug to any of these for a full diagnostic log
// ReTweak.Cli --version          version and build number
// ReTweak.Cli --security-check   audit ReTweak's files, autostart, PawnIO, data folders and Windows protections
// ReTweak.Cli --hold-test        check the elevated folder holds against renames and junctions (admin)

// --debug: full diagnostic log (JSON lines) to %LocalAppData%\ReTweak\diagnostics, like the app's --debug.
// --run also follows the saved DiagnosticsLevel, like the app (Settings -> Diagnostics, or --set DiagnosticsLevel Info).
var diagLevel = args.Contains("--debug") ? DiagLevel.Trace : args.Contains("--run") ? AppSettings.Load().DiagnosticsLevel : DiagLevel.Off;
if (diagLevel != DiagLevel.Off)
{
    // Same rule as the app: elevated, only into the admin-only %ProgramData% folder. Root on Linux: /var/log, root-only already.
    bool windowsAdmin = TrustedPaths.IsElevated && OperatingSystem.IsWindows();
    string diagFolder = windowsAdmin
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ReTweak", "diagnostics")
        : TrustedPaths.IsElevated ? "/var/log/retweak"
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReTweak", "diagnostics");
    if (windowsAdmin && !TrustedPaths.EnsureAdminOnlyFolder(diagFolder))
    {
        Console.WriteLine($"Couldn't secure {diagFolder}; running without the diagnostic log.");
        diagFolder = "";
    }
    if (diagFolder.Length > 0) Diag.Configure(diagLevel, diagFolder);
    Diag.Info(DiagCategory.App, "cli started", new { args, admin = Environment.IsPrivilegedProcess });
    AppDomain.CurrentDomain.ProcessExit += (_, _) => Diag.Shutdown();
    if (diagFolder.Length > 0) Console.WriteLine($"Diagnostic log: {diagFolder}");
}

if (args.Contains("--version"))
{
    Console.WriteLine($"ReTweak {BuildInfo.Display}");
    return;
}

if (args.Contains("--hold-test"))
{
    Environment.ExitCode = HoldTest.Run();
    return;
}

if (args.Contains("--security-check"))
{
    // It audits Windows things (registry, services, ACLs); Registry throws PlatformNotSupportedException elsewhere.
    if (!OperatingSystem.IsWindows()) { Console.WriteLine("--security-check is Windows-only."); return; }
    string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReTweak");
    var findings = SecurityCheck.Run(dataFolder: data,
        diagnosticsFolder: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ReTweak", "diagnostics"));
    Console.WriteLine(SecurityCheck.Format(findings));
    Environment.ExitCode = findings.Any(x => x.Status == CheckStatus.Fail) ? 2 : findings.Any(x => x.Status == CheckStatus.Warn) ? 1 : 0;
    return;
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

if (args.Contains("--settings"))
{
    Console.WriteLine($"{AppSettings.FilePath}{(File.Exists(AppSettings.FilePath) ? "" : " (not saved yet: defaults)")}");
    Console.WriteLine(AppSettings.Load().Describe());
    return;
}

int setIdx = Array.IndexOf(args, "--set");
if (setIdx >= 0)
{
    if (setIdx + 2 >= args.Length) { Console.WriteLine("Usage: --set <setting> <value>, e.g. --set PowerLimitPercent 85 or --set Fans.Mode Smart"); Environment.ExitCode = 1; return; }
    try
    {
        var changed = AppSettings.Load().With(args[setIdx + 1], args[setIdx + 2]);
        changed.Save();
        Console.WriteLine($"Saved {args[setIdx + 1]} = {args[setIdx + 2]} to {AppSettings.FilePath}. A running --run or app picks it up when restarted.");
    }
    catch (ArgumentException ex) { Console.WriteLine($"Not saved: {ex.Message}"); Environment.ExitCode = 1; }
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

if (args.Contains("--run"))
{
    Environment.ExitCode = RunFan(monitor, "", args, saved: true);
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
Console.WriteLine($"ReTweak {BuildInfo.Display} · driver {monitor.DriverVersion}");
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
            Console.WriteLine("    gpu thermal channels: " + string.Join("  ", ch.Select((c, i) => $"#{i + 1} {(c is double v ? $"{v:F1}°C" : "-")}")) + "  (#5 = max of #1-#4 = hotspot, #6 = die average)" +
                              (s.HotspotSpreadC is double sp ? $"  die spread {sp:F1}°C" : ""));
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
        else if (gpu.IsAsus)
            Console.WriteLine($"    connector: - ({monitor.ConnectorNote(gpu.Index)})");
    }
    if (watch) { Thread.Sleep(1000); Console.WriteLine(); }
} while (watch);

static int RunFan(GpuMonitor monitor, string mode, string[] args, bool saved = false)
{
    var gpu = monitor.Gpus.FirstOrDefault(g => monitor.FanCount(g.Index) > 0);
    if (gpu is null) { Console.WriteLine("No GPU with controllable fans found."); return 1; }

    if (mode.Equals("auto", StringComparison.OrdinalIgnoreCase))
    {
        var err = monitor.RestoreAutoFans(gpu.Index);
        Console.WriteLine(err is null ? "Fans are back on the driver's automatic curve." : $"Couldn't restore automatic fans: {err}");
        return err is null ? 0 : 1;
    }

    // --run: the saved settings, power limit included. --fan <mode>: a quick test with defaults (power left alone).
    AppSettings settings;
    if (saved)
    {
        settings = AppSettings.Load();
        // Under sudo this is root's home (/root/.local/share/...), not yours: say which file, so defaults aren't a surprise.
        Console.WriteLine(File.Exists(AppSettings.FilePath) ? $"Settings: {AppSettings.FilePath}"
                                                            : $"Settings: {AppSettings.FilePath} not found, using defaults");
        foreach (var problem in AppSettings.LoadProblems) Console.WriteLine($"settings: {problem}");
    }
    else if (mode.Equals("curve", StringComparison.OrdinalIgnoreCase)) settings = new() { Fans = new() { Mode = FanMode.Curve } };
    else if (mode.Equals("smart", StringComparison.OrdinalIgnoreCase)) settings = new() { Fans = new() { Mode = FanMode.Smart } };
    else if (int.TryParse(mode, out int pct) && pct is >= 0 and <= 100) settings = new() { Fans = new() { Mode = FanMode.Fixed, FixedPercent = pct } };
    else { Console.WriteLine("Usage: --fan <0-100> | curve | smart | auto  [--duration secs]"); return 1; }
    var options = settings.Fans;

    int durIdx = Array.IndexOf(args, "--duration");
    var duration = durIdx >= 0 && durIdx + 1 < args.Length && int.TryParse(args[durIdx + 1], out int secs)
        ? TimeSpan.FromSeconds(secs) : Timeout.InfiniteTimeSpan;

    using var control = new GpuControl(monitor, gpu.Index); // the same loop as the app: fans, power limit, emergency cut
    if (GpuControl.CanWrite && !control.InControl)
    {
        Console.WriteLine("Another ReTweak (the app, the service or another --run / --fan) is controlling the fans and power. Stop it first.");
        return 1;
    }
    control.Notice += (title, text) => Console.WriteLine($"*** {title}: {text}");
    control.Apply(settings);

    // The app's alerts, headless: temperatures and die spread every tick, the connector pins when the card has the sensor.
    var alerts = new AlertEngine(settings.Alerts);
    alerts.Raised += a =>
    {
        Console.WriteLine($"{DateTime.Now:HH:mm:ss}  ALERT {a.Severity}: {a.Title} - {a.Detail}");
        Diag.Write(a.Severity == AlertSeverity.Critical ? DiagLevel.Error : DiagLevel.Warn, DiagCategory.Alert, "alert raised",
                   new { a.Key, severity = a.Severity.ToString(), a.Title, a.Detail });
    };
    alerts.Cleared += key =>
    {
        Console.WriteLine($"{DateTime.Now:HH:mm:ss}  alert cleared: {key}");
        Diag.Info(DiagCategory.Alert, "alert cleared", new { key });
    };
    using var connector = monitor.HasConnectorSensor(gpu.Index) ? new ConnectorSampler(monitor, gpu.Index, alerts) : null;
    connector?.Start();
    var done = new ManualResetEventSlim();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => control.Dispose(); // console closed: still hand the fans back

    Console.WriteLine($"Controlling {gpu.Name} fans ({options.Mode}, failsafe {options.FailsafeTempC:F0} °C" +
                      (saved ? $", power limit {settings.PowerLimitPercent} %" : "") + $"){(GpuControl.CanWrite ? "" : " - read-only: run as admin/root to write")}. Ctrl+C to stop.");
    var started = System.Diagnostics.Stopwatch.StartNew();
    int result = 0;
    string? lastText = null;
    do
    {
        var s = monitor.Sample(gpu.Index, includeConnector: false);
        var st = control.Tick(s)!; // a GPU with fans: picked above
        alerts.ProcessGpu(s);
        // To a terminal, every second; to a log (the systemd journal), only when the status changes.
        if (!Console.IsOutputRedirected || st.Text != lastText)
            Console.WriteLine($"{DateTime.Now:HH:mm:ss}  core {s.CoreTempC:F1} °C  fans {string.Join("/", s.FanPercents.Select(f => $"{f}%"))}  {st.Text}");
        lastText = st.Text;
        // --fan is a fan test: nothing to do without rights. --run still monitors and alerts.
        if (st.NeedsAdmin && !saved) { result = 1; break; }
    } while (!done.Wait(1000) && (duration == Timeout.InfiniteTimeSpan || started.Elapsed < duration));

    control.Dispose();
    if (control.InControl) Console.WriteLine("Fans handed back to the driver.");
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
