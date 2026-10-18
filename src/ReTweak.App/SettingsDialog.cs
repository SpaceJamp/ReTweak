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
        foreach (var n in new[] { spreadWarn, spreadCrit }) n.SmallChange = 5;
        foreach (var n in new[] { lowWarn, lowCrit, highWarn, imbWarn, imbCrit }) n.SmallChange = 0.01;

        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var pairs = new (NumberBox, NumberBox?)[] { (pinWarn, pinCrit), (lowWarn, lowCrit), (highWarn, null), (spreadWarn, spreadCrit), (imbWarn, imbCrit) };
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
            var boxes = new[] { pinWarn, pinCrit, lowWarn, lowCrit, highWarn, spreadWarn, spreadCrit, imbWarn, imbCrit };
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
            };
            problem ??= AppSettings.Validate(alerts);
            if (problem is null && log.IsOn && string.IsNullOrWhiteSpace(logFolder.Text)) problem = "Choose a log folder or turn logging off.";

            var updated = current with
            {
                Alerts = alerts,
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
