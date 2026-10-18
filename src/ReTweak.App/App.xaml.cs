using Microsoft.UI.Xaml;
using ReTweak.Core;

namespace ReTweak.App;

public partial class App : Application
{
    private Window? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Notifications.Register();
        try
        {
            _window = new MainWindow(new MainViewModel(new GpuMonitor()));
        }
        catch (Exception ex)
        {
            _window = MainWindow.ForError(ex.Message);
        }
        _window.Closed += (_, _) => Notifications.Unregister();
        _window.Activate();
    }
}
