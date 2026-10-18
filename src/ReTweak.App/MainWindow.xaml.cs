using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using ReTweak.App.Native;
using ReTweak.Core;
using Windows.Foundation;
using Windows.Graphics;

namespace ReTweak.App;

public sealed partial class MainWindow : Window
{
    private readonly TrayIcon _tray = new();
    private bool _exiting, _toldAboutTray;
    private AlertSeverity _traySeverity = (AlertSeverity)(-1);
    private string _trayText = "";
    private DateTime _trayUpdated;

    public MainViewModel ViewModel { get; }

    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.Resize(new SizeInt32(1180, 860));

        ViewModel.HistoryChanged += RedrawCharts;
        ViewModel.AlertRaised += Notifications.Show;
        ViewModel.StatusChanged += UpdateTray;
        UpdateTray();

        _tray.OpenRequested += ShowFromTray;
        _tray.ExitRequested += ExitApp;
        AppWindow.Closing += OnClosing;
        Closed += (_, _) =>
        {
            ViewModel.Dispose();
            _tray.Dispose();
        };
    }

    public static Visibility Not(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Fallback window when no NVIDIA driver/GPU is available.</summary>
    public static Window ForError(string message)
    {
        var w = new Window { Title = "ReTweak" };
        w.Content = new TextBlock
        {
            Text = $"ReTweak couldn't start monitoring.\n\n{message}",
            Margin = new Thickness(24),
            TextWrapping = TextWrapping.Wrap,
        };
        w.AppWindow.Resize(new SizeInt32(560, 240));
        return w;
    }

    public void ShowFromTray()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        Activate();
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exiting || !ViewModel.Settings.CloseToTray) return;
        // Closing the window shouldn't stop protecting the connector: hide to the tray instead.
        args.Cancel = true;
        AppWindow.Hide();
        if (!_toldAboutTray)
        {
            _toldAboutTray = true;
            Notifications.ShowInfo("ReTweak is still monitoring",
                "It's in the notification area. Right-click the icon and choose Exit to stop.");
        }
    }

    private void UpdateTray()
    {
        // Severity changes go through immediately; tooltip-only changes at most every 2 s.
        var sev = ViewModel.WorstSeverity;
        string text = ViewModel.TrayText;
        if (sev == _traySeverity && (text == _trayText || DateTime.Now - _trayUpdated < TimeSpan.FromSeconds(2))) return;
        _traySeverity = sev;
        _trayText = text;
        _trayUpdated = DateTime.Now;
        _tray.SetStatus(sev, text);
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var saved = await SettingsDialog.ShowAsync(Content.XamlRoot, ViewModel.Settings, ViewModel.LogPath);
        if (saved is not null) ViewModel.Apply(saved);
    }

    private void Chart_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawCharts();

    private void RedrawCharts()
    {
        if (!AppWindow.IsVisible) return; // nothing to draw while hidden in the tray
        var o = ViewModel.AlertOptions;
        double peak = ViewModel.PowerHistory.Count > 0 ? ViewModel.PowerHistory.Max() : 0;
        Draw(PowerChart, ViewModel.PowerHistory, 0, Math.Max(50, peak * 1.2), []);
        Draw(VoltChart, ViewModel.VoltHistory, o.CriticalLowVolts - 0.2, 12.4, [o.WarnLowVolts, o.CriticalLowVolts]);
    }

    private static void Draw(Canvas canvas, IReadOnlyList<double> data, double min, double max, double[] thresholds)
    {
        canvas.Children.Clear();
        double w = canvas.ActualWidth, h = canvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        double Y(double v) => h - Math.Clamp((v - min) / (max - min), 0, 1) * h;

        for (int i = 0; i < thresholds.Length; i++)
        {
            canvas.Children.Add(new Line
            {
                X1 = 0, X2 = w, Y1 = Y(thresholds[i]), Y2 = Y(thresholds[i]),
                Stroke = i == 0 ? Theme.Warn : Theme.Critical,
                StrokeThickness = 1,
                StrokeDashArray = [4, 4],
            });
        }

        if (data.Count < 2) return;
        var line = new Polyline { Stroke = Theme.Info, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
        double step = w / (MainViewModel.HistoryLength - 1);
        double x0 = w - (data.Count - 1) * step; // newest sample on the right edge
        for (int i = 0; i < data.Count; i++)
            line.Points.Add(new Point(x0 + i * step, Y(data[i])));
        canvas.Children.Add(line);
    }
}
