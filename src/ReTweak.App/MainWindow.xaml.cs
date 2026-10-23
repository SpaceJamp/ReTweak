using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
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
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "ReTweak.ico")); // title bar, taskbar, Alt+Tab
        // Resize takes physical pixels; scale so the layout gets the same room at 125-200% display scaling.
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1180 * scale), (int)(900 * scale)));

        ViewModel.HistoryChanged += RedrawCharts;
        ViewModel.FanChanged += DrawFanCurve;
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

    /// <summary>"v0.9.0 · build 36" in the title bar; the tooltip has the commit.</summary>
    public static string VersionText { get; } = $"v{BuildInfo.Version} · build {BuildInfo.Build}{(BuildInfo.Dirty ? "+" : "")}";
    public static string VersionTooltip { get; } = $"ReTweak {BuildInfo.Display}";
    /// <summary>Column widths of a 0-100 % bar: the filled part and the rest.</summary>
    public static GridLength Filled(double percent) => new(Math.Clamp(percent, 0, 100), GridUnitType.Star);
    public static GridLength Unfilled(double percent) => new(100 - Math.Clamp(percent, 0, 100), GridUnitType.Star);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

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
        var saved = await SettingsDialog.ShowAsync(Content.XamlRoot, ViewModel.Settings, ViewModel.LogPath, ViewModel.SaveDiagnostics,
                                                 ViewModel.PowerLimitInfo);
        if (saved is not null) ViewModel.Apply(saved);
    }

    /// <summary>Opens pawnio.eu through Explorer, not ShellExecute in this (possibly elevated) process.</summary>
    private void GetPawnIo_Click(object sender, RoutedEventArgs e) => TrustedPaths.OpenLink(new Uri("https://pawnio.eu/"));

    private void RestartElevated_Click(object sender, RoutedEventArgs e)
    {
        // Only exit once the UAC prompt was accepted; the elevated copy waits for this one to finish.
        if (Elevation.StartElevatedCopy()) ExitApp();
    }

    private void Chart_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RedrawCharts();
        DrawFanCurve();
    }

    private const double FanChartMinC = 20, FanChartMaxC = 110, DragGrabRadius = 14;
    // Plot area inside the canvas: room on the left for the fan-speed axis, below for the temperature axis.
    private const double PlotLeft = 58, PlotTop = 8, PlotRight = 10, PlotBottom = 38;
    private List<FanPoint>? _dragCurve; // working copy while a point is being dragged
    private int _dragIndex = -1;

    private async void FanPreset_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string name && FanOptions.Presets.TryGetValue(name, out var curve)
            && ViewModel.SetFanCurve(curve, switchToCurve: true) is { } problem)
            await new ContentDialog { Title = "Can't use that preset", Content = problem, CloseButtonText = "OK", XamlRoot = Content.XamlRoot }.ShowAsync();
    }

    /// <summary>Size of the plot area (the canvas minus the axes).</summary>
    private (double W, double H) FanChartSize =>
        (FanChart.ActualWidth - PlotLeft - PlotRight, FanChart.ActualHeight - PlotTop - PlotBottom);
    private double FanX(double c) => PlotLeft + Math.Clamp((c - FanChartMinC) / (FanChartMaxC - FanChartMinC), 0, 1) * FanChartSize.W;
    private double FanY(double pct) => PlotTop + FanChartSize.H - Math.Clamp(pct / 100, 0, 1) * FanChartSize.H;

    private void FanChart_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var o = ViewModel.FanOptions;
        if (o.Mode != FanMode.Curve) return;
        var pos = e.GetCurrentPoint(FanChart).Position;
        int nearest = -1;
        double best = DragGrabRadius;
        for (int i = 0; i < o.Curve.Count; i++)
        {
            double d = Math.Sqrt(Math.Pow(FanX(o.Curve[i].TempC) - pos.X, 2) + Math.Pow(FanY(o.Curve[i].Percent) - pos.Y, 2));
            if (d < best) (best, nearest) = (d, i);
        }
        if (nearest < 0) return;
        _dragCurve = [.. o.Curve];
        _dragIndex = nearest;
        FanChart.CapturePointer(e.Pointer);
        e.Handled = true;
        DrawFanCurve();
    }

    private void FanChart_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_dragCurve is null) return;
        var pos = e.GetCurrentPoint(FanChart).Position;
        var (w, h) = FanChartSize;
        if (w <= 0 || h <= 0) return;
        int i = _dragIndex;
        // Stay between the neighbours so the curve keeps rising: temperatures 1 °C apart, speeds never falling.
        double minC = i > 0 ? _dragCurve[i - 1].TempC + 1 : FanChartMinC;
        double maxC = i < _dragCurve.Count - 1 ? _dragCurve[i + 1].TempC - 1 : FanChartMaxC;
        int minPct = i > 0 ? _dragCurve[i - 1].Percent : 0;
        int maxPct = i < _dragCurve.Count - 1 ? _dragCurve[i + 1].Percent : 100;
        double c = Math.Clamp(Math.Round(FanChartMinC + (pos.X - PlotLeft) / w * (FanChartMaxC - FanChartMinC)), minC, maxC);
        int pct = Math.Clamp((int)Math.Round((PlotTop + h - pos.Y) / h * 100), minPct, maxPct);
        _dragCurve[i] = new FanPoint(c, pct);
        e.Handled = true;
        DrawFanCurve();
    }

    private async void FanChart_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_dragCurve is not { } curve) return;
        _dragCurve = null;
        _dragIndex = -1;
        FanChart.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        if (ViewModel.SetFanCurve(curve, switchToCurve: false) is { } problem)
            await new ContentDialog { Title = "Can't save the curve", Content = problem, CloseButtonText = "OK", XamlRoot = Content.XamlRoot }.ShowAsync();
        DrawFanCurve();
    }

    private void FanChart_PointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_dragCurve is null) return;
        _dragCurve = null; // drag cancelled (window lost focus...): keep the saved curve
        _dragIndex = -1;
        DrawFanCurve();
    }

    // The fan chart is rebuilt only when its size, the settings or a drag change; the once-a-second
    // update just moves the "now" dot (rebuilding ~60 elements every second cost ~10% of a core).
    private (double W, double H, FanOptions? Options, ActivityKind Activity)? _fanChartKey;
    private Ellipse? _fanNowDot;

    private void DrawFanCurve()
    {
        if (!AppWindow.IsVisible || !ViewModel.HasFanControl) return;
        var canvas = FanChart;
        var (w, h) = FanChartSize;
        if (w <= 0 || h <= 0) return;

        var o = ViewModel.FanOptions;
        var key = (w, h, (FanOptions?)o, ViewModel.FanActivity); // the target line moves with the activity
        if (_dragCurve is null && _fanChartKey == key && _fanNowDot is not null)
        {
            PlaceNowDot(_fanNowDot, o);
            return;
        }
        _fanChartKey = _dragCurve is null ? key : null; // while dragging, rebuild on every move
        canvas.Children.Clear();
        _fanNowDot = null;
        var curve = _dragCurve ?? o.Curve;
        double X(double c) => FanX(c);
        double Y(double pct) => FanY(pct);
        var grid = (Brush)Application.Current.Resources["ControlStrongStrokeColorDisabledBrush"];
        var label = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

        double left = PlotLeft, top = PlotTop, right = PlotLeft + w, bottom = PlotTop + h;
        var axis = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
        TextBlock Label(string text, double size = 11) => new() { Text = text, FontSize = size, Foreground = label };

        // Fan speed axis (y): gridline and label every 25 %.
        for (int pct = 0; pct <= 100; pct += 25)
        {
            canvas.Children.Add(new Line { X1 = left, X2 = right, Y1 = Y(pct), Y2 = Y(pct), Stroke = grid, StrokeThickness = pct == 0 ? 1.5 : 0.5 });
            var t = Label($"{pct}%");
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(t, left - 6 - t.DesiredSize.Width);
            Canvas.SetTop(t, Y(pct) - t.DesiredSize.Height / 2);
            canvas.Children.Add(t);
        }
        // Temperature axis (x): gridline every 10 °C, label every 20 °C (every 10 °C when there's room).
        double labelStep = w >= 420 ? 10 : 20;
        for (double c = FanChartMinC; c <= FanChartMaxC; c += 10)
        {
            canvas.Children.Add(new Line { X1 = X(c), X2 = X(c), Y1 = top, Y2 = bottom, Stroke = grid, StrokeThickness = c == FanChartMinC ? 1.5 : 0.5 });
            if ((c - FanChartMinC) % labelStep != 0) continue;
            var t = Label($"{c:F0}");
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(t, X(c) - t.DesiredSize.Width / 2);
            Canvas.SetTop(t, bottom + 3);
            canvas.Children.Add(t);
        }
        // Axis titles.
        var axisSource = o.Mode switch { FanMode.Driver => FanSource.Core, FanMode.Smart => FanSource.Hotspot, _ => o.Source };
        string axisName = o.Mode == FanMode.Smart && o.SmartInput == SmartInput.Average ? "Average (core, hotspot)"
            : FanOptions.SourceName(axisSource) switch { "core" => "GPU core", "hottest" => "Hottest sensor", var n => char.ToUpper(n[0]) + n[1..] };
        var xTitle = Label($"{axisName} temperature (°C)", 12);
        xTitle.Foreground = axis;
        xTitle.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(xTitle, left + (w - xTitle.DesiredSize.Width) / 2);
        Canvas.SetTop(xTitle, bottom + 18);
        canvas.Children.Add(xTitle);
        var yTitle = Label("Fan speed (%)", 12);
        yTitle.Foreground = axis;
        yTitle.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        yTitle.RenderTransform = new RotateTransform { Angle = -90 };
        Canvas.SetLeft(yTitle, 0);
        Canvas.SetTop(yTitle, top + (h + yTitle.DesiredSize.Width) / 2);
        canvas.Children.Add(yTitle);

        // The x axis is the curve's input; the core failsafe line only lines up when that's the core.
        double? failsafeC = o.Source == FanSource.Core ? o.FailsafeTempC
                          : o.Source == FanSource.Hotspot ? o.FailsafeHotspotC
                          : o.Source == FanSource.Memory ? o.FailsafeMemoryC : null;
        if (o.Mode != FanMode.Driver && failsafeC is double fs)
            canvas.Children.Add(new Line
            {
                X1 = X(fs), X2 = X(fs), Y1 = top, Y2 = bottom,
                Stroke = Theme.Critical, StrokeThickness = 1, StrokeDashArray = [4, 4],
            });

        double? zeroBelow = o.Mode == FanMode.Curve && o.ZeroRpm ? o.ZeroRpmBelowC
                          : o.Mode == FanMode.Smart && o.SmartIdleStop ? o.SmartIdleStopBelowC : null;
        if (zeroBelow is double zb)
        {
            // Below this the driver has the fans and may stop them (Auto: at light load only).
            var zone = new Rectangle { Width = Math.Max(0, X(zb) - left), Height = h, Fill = Theme.Muted, Opacity = 0.15 };
            Canvas.SetLeft(zone, left);
            Canvas.SetTop(zone, top);
            canvas.Children.Add(zone);
            var t = new TextBlock { Text = "0 RPM", FontSize = 12, Foreground = label };
            Canvas.SetLeft(t, left + 4);
            Canvas.SetTop(t, bottom - 18); // bottom corner: the mode description sits in the top-left
            canvas.Children.Add(t);
        }

        if (o.Mode == FanMode.Smart)
        {
            // No curve: Auto finds the speed itself. Show the target for the current activity, its dead band
            // and the ceiling.
            var activity = ViewModel.FanActivity;
            var priority = activity != ActivityKind.Unknown && o.Activities.TryGetValue(activity, out var prof) ? prof.Priority : o.SmartPriority;
            var tune = FanOptions.SmartTuning.For(priority);
            double target = ViewModel.FanTargetC;
            if (o.CeilingC > 0)
                canvas.Children.Add(new Line
                {
                    X1 = X(o.CeilingC), X2 = X(o.CeilingC), Y1 = top, Y2 = bottom,
                    Stroke = Theme.Critical, StrokeThickness = 1.5, StrokeDashArray = [4, 4],
                });
            var band = new Rectangle
            {
                Width = Math.Max(1, X(target + tune.DeadbandC) - X(target - tune.DeadbandC)), Height = h,
                Fill = Theme.Info, Opacity = 0.12,
            };
            Canvas.SetLeft(band, X(target - tune.DeadbandC));
            Canvas.SetTop(band, top);
            canvas.Children.Add(band);
            canvas.Children.Add(new Line
            {
                X1 = X(target), X2 = X(target), Y1 = top, Y2 = bottom,
                Stroke = Theme.Info, StrokeThickness = 2, StrokeDashArray = [4, 3],
            });
            var t = new TextBlock
            {
                Text = (activity == ActivityKind.Unknown ? "" : $"{activity}: ") +
                       $"Auto holds the {(o.SmartInput == SmartInput.Average ? "core/hotspot average" : "hotspot")} at {target:F0} °C ({priority})" +
                       (o.CeilingC > 0 ? $" and keeps the hotspot under {o.CeilingC:F0} °C (red line)." : ".") +
                       " Every sensor votes; the one closest to its limit sets the speed.",
                FontSize = 12, Foreground = label, TextWrapping = TextWrapping.Wrap, Width = Math.Max(0, X(target) - left - 12),
            };
            Canvas.SetLeft(t, left + 4);
            Canvas.SetTop(t, top + 2);
            canvas.Children.Add(t);
        }
        else if (o.Mode != FanMode.Driver)
        {
            var preview = o with { Curve = curve };
            var line = new Polyline { Stroke = Theme.Info, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
            for (double c = FanChartMinC; c <= FanChartMaxC; c += 0.5)
            {
                double pct = failsafeC is double f && c >= f ? 100 : o.Mode == FanMode.Fixed ? o.FixedPercent : preview.CurvePercent(c);
                line.Points.Add(new Point(X(c), Y(pct)));
            }
            canvas.Children.Add(line);
        }
        else
        {
            var t = new TextBlock
            {
                Text = o.AutoAssist
                    ? $"Driver's curve. Safety net steps in at hotspot {o.AssistHotspotC:F0} °C, memory {o.AssistMemoryC:F0} °C, or hotspot {o.AssistGapC:F0} °C above core."
                    : "Driver's curve. ReTweak doesn't touch the fans.",
                FontSize = 12, Foreground = label, TextWrapping = TextWrapping.Wrap, Width = Math.Max(0, w - 8),
            };
            Canvas.SetLeft(t, left + 4);
            Canvas.SetTop(t, top + 2);
            canvas.Children.Add(t);
        }

        if (o.Mode == FanMode.Curve)
            for (int i = 0; i < curve.Count; i++)
            {
                bool dragging = i == _dragIndex;
                canvas.Children.Add(Dot(X(curve[i].TempC), Y(curve[i].Percent), dragging ? 12 : 9, Theme.Info));
                if (dragging)
                {
                    var tag = new TextBlock { Text = $"{curve[i].TempC:F0} °C · {curve[i].Percent}%", FontSize = 12, Foreground = label };
                    Canvas.SetLeft(tag, Math.Clamp(X(curve[i].TempC) + 8, left, Math.Max(left, right - 90)));
                    Canvas.SetTop(tag, Math.Clamp(Y(curve[i].Percent) - 22, top, bottom));
                    canvas.Children.Add(tag);
                }
            }

        _fanNowDot = Dot(0, 0, 10, Theme.Muted);
        canvas.Children.Add(_fanNowDot);
        PlaceNowDot(_fanNowDot, o);
        Glide(_fanNowDot); // after the first placement, so a rebuilt chart doesn't fly the dot in from the corner
    }

    /// <summary>Moves the live dot to the current temperature and fan speed.</summary>
    private void PlaceNowDot(Ellipse dot, FanOptions o)
    {
        if (ViewModel.FanTempC is not double now) { dot.Visibility = Visibility.Collapsed; return; }
        double pct = ViewModel.FanAppliedPercent ?? ViewModel.CurrentFanPercent ?? 0;
        dot.Visibility = Visibility.Visible;
        dot.Fill = o.Mode != FanMode.Driver || ViewModel.FanAppliedPercent is not null ? Theme.Ok : Theme.Muted;
        Canvas.SetLeft(dot, FanX(now) - dot.Width / 2);
        Canvas.SetTop(dot, FanY(pct) - dot.Height / 2);
    }

    /// <summary>
    /// Every later move of <paramref name="e"/> glides instead of jumping, on Hyprland's default curve
    /// (bezier 0.05, 0.9, 0.1, 1.05: quick start, a touch of overshoot). Shorter than the 1 s update.
    /// </summary>
    private static void Glide(UIElement e)
    {
        var visual = ElementCompositionPreview.GetElementVisual(e);
        var c = visual.Compositor;
        var move = c.CreateVector3KeyFrameAnimation();
        move.Target = "Offset";
        move.InsertExpressionKeyFrame(1f, "this.FinalValue",
            c.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.05f, 0.9f), new System.Numerics.Vector2(0.1f, 1.05f)));
        move.Duration = TimeSpan.FromMilliseconds(600);
        var implicitAnimations = c.CreateImplicitAnimationCollection();
        implicitAnimations["Offset"] = move;
        visual.ImplicitAnimations = implicitAnimations;
    }

    private static Ellipse Dot(double x, double y, double size, Brush fill)
    {
        var e = new Ellipse { Width = size, Height = size, Fill = fill };
        Canvas.SetLeft(e, x - size / 2);
        Canvas.SetTop(e, y - size / 2);
        return e;
    }

    private void RedrawCharts()
    {
        if (!AppWindow.IsVisible) return; // nothing to draw while hidden in the tray
        var o = ViewModel.AlertOptions;
        double peak = ViewModel.PowerHistory.Count > 0 ? ViewModel.PowerHistory.Max() : 0;
        Draw(PowerChart, ViewModel.PowerHistory, 0, Math.Max(50, peak * 1.2), []);
        Draw(VoltChart, ViewModel.VoltHistory, o.CriticalLowVolts - 0.2, 12.4, [o.WarnLowVolts, o.CriticalLowVolts]);
    }

    /// <summary>Elements of a history chart, created once per canvas and then only moved.</summary>
    private sealed class HistoryChart(Line[] thresholds, Polyline line)
    {
        public Line[] Thresholds { get; } = thresholds;
        public Polyline Line { get; } = line;
    }
    private readonly Dictionary<Canvas, HistoryChart> _historyCharts = [];

    private void Draw(Canvas canvas, IReadOnlyList<double> data, double min, double max, double[] thresholds)
    {
        double w = canvas.ActualWidth, h = canvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        if (!_historyCharts.TryGetValue(canvas, out var chart) || chart.Thresholds.Length != thresholds.Length)
        {
            canvas.Children.Clear();
            var lines = thresholds.Select((_, i) => new Line
            {
                Stroke = i == 0 ? Theme.Warn : Theme.Critical,
                StrokeThickness = 1,
                StrokeDashArray = [4, 4],
            }).ToArray();
            var poly = new Polyline { Stroke = Theme.Info, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
            foreach (var l in lines) canvas.Children.Add(l);
            canvas.Children.Add(poly);
            _historyCharts[canvas] = chart = new HistoryChart(lines, poly);
        }

        double Y(double v) => h - Math.Clamp((v - min) / (max - min), 0, 1) * h;

        for (int i = 0; i < thresholds.Length; i++)
        {
            var l = chart.Thresholds[i];
            (l.X1, l.X2, l.Y1, l.Y2) = (0, w, Y(thresholds[i]), Y(thresholds[i]));
        }

        var points = new PointCollection();
        if (data.Count >= 2)
        {
            double step = w / (MainViewModel.HistoryLength - 1);
            double x0 = w - (data.Count - 1) * step; // newest sample on the right edge
            for (int i = 0; i < data.Count; i++)
                points.Add(new Point(x0 + i * step, Y(data[i])));
        }
        chart.Line.Points = points; // one property change instead of a new element
    }
}
