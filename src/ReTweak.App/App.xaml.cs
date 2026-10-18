using Microsoft.UI.Xaml;
using ReTweak.App.Native;
using ReTweak.Core;

namespace ReTweak.App;

public partial class App : Application
{
    private Window? _window;
    // Held for the process lifetime: only one ReTweak should poll the sensor and own the tray icon.
    private static Mutex? _singleInstance;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\ReTweak.SingleInstance", out bool first);
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
}
