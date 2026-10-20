using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ReTweak.Core;

namespace ReTweak.App;

/// <summary>Settings dialog. Returns the saved settings, or null if cancelled.</summary>
internal static class SettingsDialog
{
    public static async Task<AppSettings?> ShowAsync(XamlRoot root, AppSettings current, string? activeLogPath)
    {
        var o = current.Alerts;

        var autostart = new ToggleSwitch { Header = "Start with Windows (in the tray, no window)", IsOn = Autostart.IsEnabled };
        var tray = new ToggleSwitch { Header = "Keep monitoring in the tray when the window is closed", IsOn = current.CloseToTray };
        var toasts = new ToggleSwitch { Header = "Windows notifications for warnings and critical alerts", IsOn = current.Toasts };
        var log = new ToggleSwitch { Header = "Log connector readings to CSV", IsOn = current.LogConnector };
        var logFolder = new TextBox { Header = "Log folder", Text = current.LogFolder };
        var logNote = new TextBlock
        {
            Text = activeLogPath is null ? "Not logging right now." : $"Logging to {activeLogPath}",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
        };

        NumberBox Num(string header, double value) => new()
        {
            Header = header,
            Value = value,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            SmallChange = 0.1,
            MinWidth = 150,
        };
        var pinWarn = Num("Pin current warning (A)", o.PinWarnAmps);
        var pinCrit = Num("Pin current critical (A)", o.PinCriticalAmps);
        var lowWarn = Num("Low voltage warning (V)", o.WarnLowVolts);
        var lowCrit = Num("Low voltage critical (V)", o.CriticalLowVolts);
        var highWarn = Num("High voltage warning (V)", o.WarnHighVolts);
        var spreadWarn = Num("Pin voltage spread warning (mV)", o.WarnSpreadVolts * 1000);
        var spreadCrit = Num("Pin voltage spread critical (mV)", o.CriticalSpreadVolts * 1000);
        var imbWarn = Num("Current imbalance warning (×)", o.WarnImbalance);
        var imbCrit = Num("Current imbalance critical (×)", o.CriticalImbalance);
        var coreWarn = Num("GPU core temperature warning (°C)", o.WarnCoreTempC);
        var coreCrit = Num("GPU core temperature critical (°C)", o.CriticalCoreTempC);
        var memWarn = Num("Memory temperature warning (°C)", o.WarnMemoryTempC);
        var memCrit = Num("Memory temperature critical (°C)", o.CriticalMemoryTempC);
        foreach (var n in new[] { coreWarn, coreCrit, memWarn, memCrit }) n.SmallChange = 1;
        foreach (var n in new[] { spreadWarn, spreadCrit }) n.SmallChange = 5;
        foreach (var n in new[] { lowWarn, lowCrit, highWarn, imbWarn, imbCrit }) n.SmallChange = 0.01;

        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var pairs = new (NumberBox, NumberBox?)[]
        {
            (pinWarn, pinCrit), (lowWarn, lowCrit), (highWarn, null), (spreadWarn, spreadCrit), (imbWarn, imbCrit),
            (coreWarn, coreCrit), (memWarn, memCrit),
        };
        for (int r = 0; r < pairs.Length; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(pairs[r].Item1, r);
            grid.Children.Add(pairs[r].Item1);
            if (pairs[r].Item2 is { } b) { Grid.SetRow(b, r); Grid.SetColumn(b, 1); grid.Children.Add(b); }
        }

        var reset = new HyperlinkButton { Content = "Reset thresholds to defaults" };
        reset.Click += (_, _) =>
        {
            var d = new AlertOptions();
            pinWarn.Value = d.PinWarnAmps; pinCrit.Value = d.PinCriticalAmps;
            lowWarn.Value = d.WarnLowVolts; lowCrit.Value = d.CriticalLowVolts; highWarn.Value = d.WarnHighVolts;
            spreadWarn.Value = d.WarnSpreadVolts * 1000; spreadCrit.Value = d.CriticalSpreadVolts * 1000;
            imbWarn.Value = d.WarnImbalance; imbCrit.Value = d.CriticalImbalance;
            coreWarn.Value = d.WarnCoreTempC; coreCrit.Value = d.CriticalCoreTempC;
            memWarn.Value = d.WarnMemoryTempC; memCrit.Value = d.CriticalMemoryTempC;
        };

        // Fan control
        var f = current.Fans;
        var fanMode = new ComboBox { Header = "Fan mode", ItemsSource = new[] { "Auto (driver)", "Fixed speed", "Curve" }, SelectedIndex = (int)f.Mode, MinWidth = 200 };
        var fanFixed = Num("Fixed fan speed (%)", f.FixedPercent);
        var fanHyst = Num("Curve hysteresis (°C)", f.HysteresisC);
        var fanFailsafe = Num("Full speed at or above (°C)", f.FailsafeTempC);
        foreach (var n in new[] { fanFixed, fanFailsafe }) n.SmallChange = 1;
        fanHyst.SmallChange = 0.5;

        const int CurvePoints = 5;
        var curveGrid = new Grid { ColumnSpacing = 12, RowSpacing = 8 };
        curveGrid.ColumnDefinitions.Add(new ColumnDefinition());
        curveGrid.ColumnDefinitions.Add(new ColumnDefinition());
        var curveTemps = new NumberBox[CurvePoints];
        var curvePcts = new NumberBox[CurvePoints];
        void FillCurve(IReadOnlyList<FanPoint> pts)
        {
            for (int i = 0; i < CurvePoints; i++)
            {
                var p = pts[Math.Min(i, pts.Count - 1)]; // shorter saved curves: repeat the last point (rejected on save)
                curveTemps[i].Value = p.TempC;
                curvePcts[i].Value = p.Percent;
            }
        }
        for (int i = 0; i < CurvePoints; i++)
        {
            curveGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            curveTemps[i] = Num($"Point {i + 1} · core °C", 0);
            curvePcts[i] = Num($"Point {i + 1} · fan %", 0);
            curveTemps[i].SmallChange = curvePcts[i].SmallChange = 1;
            Grid.SetRow(curveTemps[i], i);
            Grid.SetRow(curvePcts[i], i);
            Grid.SetColumn(curvePcts[i], 1);
            curveGrid.Children.Add(curveTemps[i]);
            curveGrid.Children.Add(curvePcts[i]);
        }
        FillCurve(f.Curve.Count == CurvePoints ? f.Curve : new FanOptions().Curve);

        var fanNote = new TextBlock
        {
            Text = "Fixed and Curve write to the card and need ReTweak to run as administrator. " +
                   "Auto, closing ReTweak or losing the temperature reading hands the fans back to the driver. " +
                   "Close GPU Tweak III and other fan tools first, or they'll fight over the fans.",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
        };
        var fanReset = new HyperlinkButton { Content = "Reset fan settings to defaults" };
        fanReset.Click += (_, _) =>
        {
            var d = new FanOptions();
            fanFixed.Value = d.FixedPercent; fanHyst.Value = d.HysteresisC; fanFailsafe.Value = d.FailsafeTempC;
            FillCurve(d.Curve);
        };

        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false, IsOpen = false, Title = "Can't save" };

        TextBlock Section(string text) => new()
        {
            Text = text,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            Margin = new Thickness(0, 8, 0, 0),
        };
        var panel = new StackPanel { Spacing = 8, MinWidth = 420 };
        foreach (var e in new UIElement[]
        {
            error, Section("General"), autostart, tray, toasts,
            Section("Logging"), log, logFolder, logNote,
            Section("Alert thresholds"), grid, reset,
            Section("Fan control"), fanNote, fanMode, fanFixed, curveGrid, fanHyst, fanFailsafe, fanReset,
        }) panel.Children.Add(e);

        var dialog = new ContentDialog
        {
            Title = "Settings",
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
            Content = new ScrollViewer { Content = panel, MaxHeight = 560 },
        };

        AppSettings? result = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            string? problem = null;
            var boxes = new[] { pinWarn, pinCrit, lowWarn, lowCrit, highWarn, spreadWarn, spreadCrit, imbWarn, imbCrit, coreWarn, coreCrit, memWarn, memCrit };
            if (boxes.Any(b => double.IsNaN(b.Value))) problem = "Every threshold needs a number.";

            var alerts = current.Alerts with
            {
                PinWarnAmps = pinWarn.Value,
                PinCriticalAmps = pinCrit.Value,
                WarnLowVolts = lowWarn.Value,
                CriticalLowVolts = lowCrit.Value,
                WarnHighVolts = highWarn.Value,
                WarnSpreadVolts = spreadWarn.Value / 1000,
                CriticalSpreadVolts = spreadCrit.Value / 1000,
                WarnImbalance = imbWarn.Value,
                CriticalImbalance = imbCrit.Value,
                WarnCoreTempC = coreWarn.Value,
                CriticalCoreTempC = coreCrit.Value,
                WarnMemoryTempC = memWarn.Value,
                CriticalMemoryTempC = memCrit.Value,
            };
            problem ??= AppSettings.Validate(alerts);

            var fanBoxes = curveTemps.Concat(curvePcts).Append(fanFixed).Append(fanHyst).Append(fanFailsafe);
            if (problem is null && fanBoxes.Any(b => double.IsNaN(b.Value))) problem = "Every fan setting needs a number.";
            var fans = current.Fans with
            {
                Mode = (FanMode)Math.Max(0, fanMode.SelectedIndex),
                FixedPercent = (int)Math.Round(fanFixed.Value),
                HysteresisC = fanHyst.Value,
                FailsafeTempC = fanFailsafe.Value,
                Curve = Enumerable.Range(0, CurvePoints).Select(i => new FanPoint(curveTemps[i].Value, (int)Math.Round(curvePcts[i].Value))).ToList(),
            };
            problem ??= fans.Validate();
            if (problem is null && log.IsOn && string.IsNullOrWhiteSpace(logFolder.Text)) problem = "Choose a log folder or turn logging off.";

            var updated = current with
            {
                Alerts = alerts,
                Fans = fans,
                CloseToTray = tray.IsOn,
                Toasts = toasts.IsOn,
                LogConnector = log.IsOn,
                LogFolder = logFolder.Text.Trim(),
            };

            if (problem is null)
            {
                try
                {
                    if (autostart.IsOn != Autostart.IsEnabled || (autostart.IsOn && Autostart.IsStale)) Autostart.Set(autostart.IsOn);
                    updated.Save();
                }
                catch (Exception ex) { problem = $"Couldn't save settings: {ex.Message}"; }
            }

            if (problem is not null)
            {
                error.Message = problem;
                error.IsOpen = true;
                args.Cancel = true; // keep the dialog open so the user can fix it
                return;
            }
            result = updated;
        };

        await dialog.ShowAsync();
        return result;
    }
}
