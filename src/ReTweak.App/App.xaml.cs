using System.Security.Principal;
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
        bool first;
        try { _singleInstance = new Mutex(initiallyOwned: true, InstanceName, out first); }
        catch (UnauthorizedAccessException) { first = false; } // an elevated copy created it

        if (IsAdmin)
        {
            _elevatedInstance = new Mutex(initiallyOwned: true, ElevatedName, out bool firstElevated);
            // Run as administrator while a normal copy runs: that copy can't read the hotspot, so take over.
            if (!first && firstElevated && _singleInstance is not null && TrayIcon.AskRunningInstanceToExit())
                first = WaitForPreviousInstance(_singleInstance);
        }

        if (!first)
        {
            TrayIcon.SignalRunningInstance(); // bring the running copy to the front instead
            Exit();
            return;
        }

        // --minimized: started with Windows; stay in the tray until the user opens the window.
        bool minimized = Environment.GetCommandLineArgs().Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        Notifications.Register();
        try
        {
            _window = new MainWindow(new MainViewModel(new GpuMonitor(), AppSettings.Load()));
        }
        catch (Exception ex)
        {
            _window = MainWindow.ForError(ex.Message);
            minimized = false; // an error with no visible window would be invisible
        }
        _window.Closed += (_, _) => Notifications.Unregister();
        if (!minimized) _window.Activate();
    }

    private static bool IsAdmin => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>The normal copy was asked to exit; take its place once it has.</summary>
    private static bool WaitForPreviousInstance(Mutex mutex)
    {
        try { return mutex.WaitOne(TimeSpan.FromSeconds(10)); }
        catch (AbandonedMutexException) { return true; } // it exited without releasing: ours now
    }
}
