using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Win32;
using ReTweak.Core;

namespace ReTweak.App;

/// <summary>
/// App side of the diagnostic log: where it goes, what the user picked (or --debug), the startup
/// environment, crash handlers, and the "Save diagnostics" bundle.
/// </summary>
public static class Diagnostics
{
    public const string DebugArg = "--debug";
    public static string Folder { get; } = Path.Combine(AppSettings.Folder, "diagnostics");
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>--debug on the command line: full (Trace) logging for this run, whatever Settings says.</summary>
    public static bool ForcedByArg { get; } =
        Environment.GetCommandLineArgs().Contains(DebugArg, StringComparer.OrdinalIgnoreCase);

    /// <summary>Applies the level from Settings (or --debug).</summary>
    public static void Apply(DiagLevel fromSettings)
    {
        try { Diag.Configure(ForcedByArg ? DiagLevel.Trace : fromSettings, Folder); }
        catch (Exception) { /* can't create the folder: run without diagnostics rather than not at all */ }
    }

    /// <summary>Logs every unhandled exception, wherever it comes from, before the process goes down.</summary>
    public static void HookCrashes(Microsoft.UI.Xaml.Application app)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Diag.Error(DiagCategory.Error, "unhandled exception", e.ExceptionObject as Exception, new { e.IsTerminating });
            if (e.IsTerminating) Diag.Shutdown();
        };
        TaskScheduler.UnobservedTaskException += (_, e) => Diag.Error(DiagCategory.Error, "unobserved task exception", e.Exception);
        app.UnhandledException += (_, e) => Diag.Error(DiagCategory.Error, "unhandled UI exception", e.Exception, new { e.Message });
    }

    /// <summary>Everything about the machine and ReTweak's setup that helps explain a log.</summary>
    public static object Describe(GpuMonitor? monitor, AppSettings settings) => new
    {
        retweak = typeof(Diagnostics).Assembly.GetName().Version?.ToString(),
        exe = System.Environment.ProcessPath,
        args = System.Environment.GetCommandLineArgs().Skip(1),
        os = System.Environment.OSVersion.VersionString,
        dotnet = System.Environment.Version.ToString(),
        is64Bit = System.Environment.Is64BitProcess,
        admin = Elevation.IsAdmin,
        cpuCount = System.Environment.ProcessorCount,
        culture = System.Globalization.CultureInfo.CurrentCulture.Name,
        nvidiaDriver = monitor?.DriverVersion,
        pawnIo = PawnIoVersion(),
        gpus = monitor?.Gpus.Select(g => new
        {
            g.Index, g.Name, g.PciBus, deviceId = $"{g.DeviceId:X4}", subsystem = $"{g.SubVendorId:X4}:{g.SubDeviceId:X4}",
            connectorSensor = monitor.HasConnectorSensor(g.Index),
            memoryTemp = monitor.HasMemoryTemperature(g.Index),
            hotspot = monitor.HotspotState(g.Index).ToString(),
            hotspotNote = monitor.HotspotNote(g.Index),
            fans = monitor.FanCount(g.Index),
            fanRange = monitor.FanSpeedRange(g.Index) is var (min, max) ? $"{min}-{max}%" : null,
        }),
        otherFanTools = Process.GetProcesses().Select(p => p.ProcessName)
            .Where(n => n.Contains("GPU Tweak", StringComparison.OrdinalIgnoreCase) || n.Contains("ASUSGPUFan", StringComparison.OrdinalIgnoreCase)
                     || n.Contains("Afterburner", StringComparison.OrdinalIgnoreCase) || n.Contains("FanControl", StringComparison.OrdinalIgnoreCase)
                     || n.Contains("HWiNFO", StringComparison.OrdinalIgnoreCase) || n.Contains("HWMonitor", StringComparison.OrdinalIgnoreCase))
            .Distinct(),
        settings,
    };

    private static string? PawnIoVersion()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
            return key?.GetValue("DisplayVersion") as string ?? (key is null ? "not installed" : "installed");
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Zips the diagnostic logs, settings, today's connector CSV and an environment summary to the
    /// Desktop, and returns the zip's path.
    /// </summary>
    public static string SaveBundle(GpuMonitor? monitor, AppSettings settings)
    {
        Diag.Info(DiagCategory.App, "saving diagnostics bundle");
        Diag.FlushTiming();
        Thread.Sleep(300); // let the writer catch up
        string zipPath = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.DesktopDirectory),
                                      $"ReTweak-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        var summary = zip.CreateEntry("environment.json");
        using (var w = new StreamWriter(summary.Open()))
            w.Write(JsonSerializer.Serialize(Describe(monitor, settings), Indented));

        foreach (var file in Diag.LogFiles(Folder).Take(Diag.MaxFiles)) AddShared(zip, file, "diagnostics/" + Path.GetFileName(file));
        string settingsFile = Path.Combine(AppSettings.Folder, "settings.json");
        if (File.Exists(settingsFile)) AddShared(zip, settingsFile, "settings.json");
        if (Directory.Exists(settings.LogFolder))
            foreach (var csv in Directory.GetFiles(settings.LogFolder, "*.csv").OrderByDescending(File.GetLastWriteTime).Take(2))
                AddShared(zip, csv, "connector-logs/" + Path.GetFileName(csv));
        return zipPath;
    }

    /// <summary>Copies a file that may still be open for writing (the current log).</summary>
    private static void AddShared(ZipArchive zip, string path, string name)
    {
        try
        {
            using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var dst = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
            src.CopyTo(dst);
        }
        catch (IOException) { /* skip a file that vanished */ }
    }

    /// <summary>Opens Explorer with the file selected.</summary>
    public static void Reveal(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
}
