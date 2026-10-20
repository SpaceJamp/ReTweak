using Microsoft.UI.Xaml;
using ReTweak.App.Native;
using ReTweak.Core;

namespace ReTweak.App;

public partial class App : Application
{
    private Window? _window;
    // Held for the process lifetime: only one ReTweak should poll the sensor and own the tray icon.
    private static Mutex? _singleInstance;
    // Held only by an elevated copy, so a second elevated launch can tell it isn't taking over a normal one.
    private static Mutex? _elevatedInstance;
    private const string InstanceName = @"Local\ReTweak.SingleInstance", ElevatedName = @"Local\ReTweak.ElevatedInstance";

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var cmdArgs = Environment.GetCommandLineArgs();
        bool first;
        try { _singleInstance = new Mutex(initiallyOwned: true, InstanceName, out first); }
        catch (UnauthorizedAccessException) { first = false; } // an elevated copy created it
        // "Restart as administrator": the normal copy started this one and is exiting.
        if (!first && _singleInstance is not null && cmdArgs.Contains(Elevation.RestartArg))
            first = WaitForPreviousInstance(_singleInstance, TimeSpan.FromSeconds(10));

        if (Elevation.IsAdmin)
        {
            _elevatedInstance = new Mutex(initiallyOwned: true, ElevatedName, out bool firstElevated);
            // Run as administrator while a normal copy runs: that copy can't read the hotspot or set fans, so take over.
            if (!first && firstElevated && _singleInstance is not null && TrayIcon.AskRunningInstanceToExit())
                first = WaitForPreviousInstance(_singleInstance, TimeSpan.FromSeconds(30))
                        || true; // it was told to exit: carry on rather than leave nothing running
        }

        if (!first)
        {
            TrayIcon.SignalRunningInstance(); // bring the running copy to the front instead
            Exit();
            return;
        }

        // --minimized: started with Windows; stay in the tray until the user opens the window.
        bool minimized = cmdArgs.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        var settings = AppSettings.Load();
        Diagnostics.Apply(settings.DiagnosticsLevel); // before anything else, so startup problems are logged
        Diagnostics.HookCrashes(this);
        foreach (var problem in AppSettings.LoadProblems) Diag.Warn(DiagCategory.Settings, problem);
        Notifications.Register();
        GpuMonitor? monitor = null;
        try
        {
            monitor = new GpuMonitor();
            Diag.Info(DiagCategory.App, "started", Diagnostics.Describe(monitor, settings));
            _window = new MainWindow(new MainViewModel(monitor, settings));
        }
        catch (Exception ex)
        {
            Diag.Error(DiagCategory.App, "couldn't start monitoring", ex, Diagnostics.Describe(monitor, settings));
            monitor?.Dispose(); // stop NVML and the hotspot sampler if the view model couldn't start
            _window = MainWindow.ForError(ex.Message);
            minimized = false; // an error with no visible window would be invisible
        }
        _window.Closed += (_, _) =>
        {
            Notifications.Unregister();
            Diag.Info(DiagCategory.App, "exiting");
            Diag.Shutdown();
        };
        _ = Task.Run(Autostart.MigrateIfElevated); // old Run-key entry -> elevated logon task
        if (Diag.IsOn(DiagLevel.Info))
            _ = Task.Run(() =>
            {
                var findings = Diagnostics.RunSecurityCheck(settings);
                Diag.Write(findings.Any(x => x.Status >= CheckStatus.Warn) ? DiagLevel.Warn : DiagLevel.Info, DiagCategory.App, "security check",
                           findings.Where(x => x.Status != CheckStatus.Pass).Select(x => new { x.Area, x.Title, status = x.Status.ToString(), x.Detail }));
            });
        if (!minimized) _window.Activate();
    }

    /// <summary>A normal copy is exiting (asked to, or after starting this one); take its place once it has.</summary>
    private static bool WaitForPreviousInstance(Mutex mutex, TimeSpan timeout)
    {
        try { return mutex.WaitOne(timeout); }
        catch (AbandonedMutexException) { return true; } // it exited without releasing: ours now
    }
}
