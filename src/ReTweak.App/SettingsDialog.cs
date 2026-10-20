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

        var startState = Autostart.State;
        var autostart = new ToggleSwitch
        {
            Header = "Start with Windows as administrator (in the tray, no window)",
            IsOn = startState != AutostartState.Off,
        };
        var autostartNote = new TextBlock
        {
            Text = startState switch
            {
                AutostartState.Legacy => "Set up by an older ReTweak, which starts without administrator rights (no hotspot). " +
                                         "Save to switch to the elevated start; Windows asks for permission once.",
                AutostartState.Stale => "Registered for a ReTweak in another folder. Save to point it at this one" +
                                        (Autostart.IsAdmin ? "." : "; Windows asks for permission once."),
                _ => "Uses a Task Scheduler logon task with highest privileges, so ReTweak starts elevated without a prompt" +
                     (Autostart.IsAdmin ? "." : ". Turning it on or off asks for administrator permission once."),
            },
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
        };
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
        var hotWarn = Num("Hotspot temperature warning (°C)", o.WarnHotspotTempC);
        var hotCrit = Num("Hotspot temperature critical (°C)", o.CriticalHotspotTempC);
        foreach (var n in new[] { coreWarn, coreCrit, memWarn, memCrit, hotWarn, hotCrit }) n.SmallChange = 1;
        foreach (var n in new[] { spreadWarn, spreadCrit }) n.SmallChange = 5;
        foreach (var n in new[] { lowWarn, lowCrit, highWarn, imbWarn, imbCrit }) n.SmallChange = 0.01;

        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var pairs = new (NumberBox, NumberBox?)[]
        {
            (pinWarn, pinCrit), (lowWarn, lowCrit), (highWarn, null), (spreadWarn, spreadCrit), (imbWarn, imbCrit),
            (coreWarn, coreCrit), (memWarn, memCrit), (hotWarn, hotCrit),
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
            hotWarn.Value = d.WarnHotspotTempC; hotCrit.Value = d.CriticalHotspotTempC;
        };

        // Fan control
        var f = current.Fans;
        var fanMode = new ComboBox
        {
            Header = "Fan mode", MinWidth = 220, SelectedIndex = Math.Max(0, Array.IndexOf(MainViewModel.FanModeOrder, f.Mode)),
            ItemsSource = new[] { "Auto (ReTweak holds a target)", "Driver (card's curve + safety net)", "Fixed speed", "Curve" },
        };
        var smartHot = Num("Auto: hold the hotspot at (°C)", f.SmartTargetHotspotC);
        var smartCore = Num("Auto: without a hotspot, hold the core at (°C)", f.SmartTargetCoreC);
        foreach (var n in new[] { smartHot, smartCore }) n.SmallChange = 1;
        var smartPriority = new ComboBox
        {
            Header = "Auto: favour", MinWidth = 220, SelectedIndex = (int)f.SmartPriority,
            ItemsSource = new[] { "Quiet (slow, rare changes)", "Balanced", "Tight (holds within ~1 °C)" },
        };
        var smartIdle = new ToggleSwitch { Header = "Auto: let the driver stop the fans at idle (0 RPM)", IsOn = f.SmartIdleStop };
        var fanSource = new ComboBox
        {
            Header = "Curve follows", MinWidth = 220, SelectedIndex = (int)f.Source,
            ItemsSource = new[] { "Core temperature", "Hotspot (needs PawnIO; else core)", "Memory temperature", "Hottest of the three" },
        };
        var fanFixed = Num("Fixed fan speed (%)", f.FixedPercent);
        var fanHyst = Num("Curve hysteresis (°C)", f.HysteresisC);
        var rampUp = Num("Speed up at most (%/s, 0 = instant)", f.RampUpPerSecond);
        var rampDown = Num("Slow down at most (%/s, 0 = instant)", f.RampDownPerSecond);
        var smoothing = Num("Average temperature over (s)", f.SmoothingSeconds);
        var zeroRpm = new ToggleSwitch { Header = "Curve: let the driver stop the fans (0 RPM) when cool", IsOn = f.ZeroRpm };
        var zeroBelow = Num("Zero RPM below (°C)", f.ZeroRpmBelowC);
        var failCore = Num("Full speed at core (°C)", f.FailsafeTempC);
        var failHot = Num("Full speed at hotspot (°C)", f.FailsafeHotspotC);
        var failMem = Num("Full speed at memory (°C)", f.FailsafeMemoryC);
        var assist = new ToggleSwitch { Header = "Auto: step in when the driver's curve isn't enough", IsOn = f.AutoAssist };
        var assistHot = Num("Step in at hotspot (°C)", f.AssistHotspotC);
        var assistMem = Num("Step in at memory (°C)", f.AssistMemoryC);
        var assistGap = Num("Step in at hotspot above core (°C)", f.AssistGapC);
        var assistBoost = Num("Boost over the driver's speed (%)", f.AssistBoostPercent);
        var assistHold = Num("Hand back after cool for (s)", f.AssistHoldSeconds);
        var assistMargin = Num("Cool means below by (°C)", f.AssistReleaseMarginC);
        foreach (var n in new[] { fanFixed, rampUp, rampDown, zeroBelow, failCore, failHot, failMem, assistHot, assistMem, assistGap, assistBoost, assistHold, assistMargin })
            n.SmallChange = 1;
        foreach (var n in new[] { fanHyst, smoothing }) n.SmallChange = 0.5;

        Grid Pairs(params (FrameworkElement, FrameworkElement?)[] rows)
        {
            var g = new Grid { ColumnSpacing = 12, RowSpacing = 8 };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition());
            for (int r = 0; r < rows.Length; r++)
            {
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(rows[r].Item1, r);
                g.Children.Add(rows[r].Item1);
                if (rows[r].Item2 is { } b) { Grid.SetRow(b, r); Grid.SetColumn(b, 1); g.Children.Add(b); }
            }
            return g;
        }

        const int CurvePoints = 5;
        var curveTemps = new NumberBox[CurvePoints];
        var curvePcts = new NumberBox[CurvePoints];
        for (int i = 0; i < CurvePoints; i++)
        {
            curveTemps[i] = Num($"Point {i + 1} · °C", 0);
            curvePcts[i] = Num($"Point {i + 1} · fan %", 0);
            curveTemps[i].SmallChange = curvePcts[i].SmallChange = 1;
        }
        var curveGrid = Pairs(Enumerable.Range(0, CurvePoints).Select(i => ((FrameworkElement)curveTemps[i], (FrameworkElement?)curvePcts[i])).ToArray());
        void FillCurve(IReadOnlyList<FanPoint> pts)
        {
            for (int i = 0; i < CurvePoints; i++)
            {
                var p = pts[Math.Min(i, pts.Count - 1)]; // shorter saved curves: repeat the last point (rejected on save)
                curveTemps[i].Value = p.TempC;
                curvePcts[i].Value = p.Percent;
            }
        }
        FillCurve(f.Curve.Count == CurvePoints ? f.Curve : new FanOptions().Curve);

        var presetRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        presetRow.Children.Add(new TextBlock { Text = "Load preset:", VerticalAlignment = VerticalAlignment.Center });
        foreach (var (name, pts) in FanOptions.Presets)
        {
            var b = new HyperlinkButton { Content = name };
            b.Click += (_, _) => FillCurve(pts);
            presetRow.Children.Add(b);
        }

        TextBlock Caption(string text) => new()
        {
            Text = text,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
        };
        var fanNote = Caption("Fixed, Curve and the Auto safety net write to the card and need ReTweak to run as administrator. " +
                              "Switching to Auto without the safety net, closing ReTweak or losing the temperature reading hands the fans " +
                              "back to the driver. Close GPU Tweak III and other fan tools first, or they'll fight over the fans.");
        var assistNote = Caption("The driver's own curve only sees the core. The safety net watches the hotspot and memory too, and " +
                                 "a hotspot far above the core, a sign of poor cooler contact. Thresholds are starting points, not load-tested.");
        var fanReset = new HyperlinkButton { Content = "Reset fan settings to defaults" };
        fanReset.Click += (_, _) =>
        {
            var d = new FanOptions();
            fanSource.SelectedIndex = (int)d.Source;
            smartHot.Value = d.SmartTargetHotspotC; smartCore.Value = d.SmartTargetCoreC;
            smartPriority.SelectedIndex = (int)d.SmartPriority; smartIdle.IsOn = d.SmartIdleStop;
            fanFixed.Value = d.FixedPercent; fanHyst.Value = d.HysteresisC;
            rampUp.Value = d.RampUpPerSecond; rampDown.Value = d.RampDownPerSecond; smoothing.Value = d.SmoothingSeconds;
            zeroRpm.IsOn = d.ZeroRpm; zeroBelow.Value = d.ZeroRpmBelowC;
            failCore.Value = d.FailsafeTempC; failHot.Value = d.FailsafeHotspotC; failMem.Value = d.FailsafeMemoryC;
            assist.IsOn = d.AutoAssist; assistHot.Value = d.AssistHotspotC; assistMem.Value = d.AssistMemoryC; assistGap.Value = d.AssistGapC;
            assistBoost.Value = d.AssistBoostPercent; assistHold.Value = d.AssistHoldSeconds; assistMargin.Value = d.AssistReleaseMarginC;
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
            error, Section("General"), autostart, autostartNote, tray, toasts,
            Section("Logging"), log, logFolder, logNote,
            Section("Alert thresholds"), grid, reset,
            Section("Fan control"), fanNote, Pairs((fanMode, fanSource)), fanFixed,
            Section("Auto"), Pairs((smartHot, smartCore), (smartPriority, null)), smartIdle,
            Section("Curve"), presetRow, curveGrid, Pairs((fanHyst, smoothing), (rampUp, rampDown)), zeroRpm, zeroBelow,
            Section("Failsafes (all modes that write)"), Pairs((failCore, failHot), (failMem, null)),
            Section("Auto safety net"), assistNote, assist, Pairs((assistHot, assistMem), (assistGap, assistBoost), (assistHold, assistMargin)),
            fanReset,
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
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            string? problem = null;
            var boxes = new[] { pinWarn, pinCrit, lowWarn, lowCrit, highWarn, spreadWarn, spreadCrit, imbWarn, imbCrit, coreWarn, coreCrit, memWarn, memCrit, hotWarn, hotCrit };
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
                WarnHotspotTempC = hotWarn.Value,
                CriticalHotspotTempC = hotCrit.Value,
            };
            problem ??= AppSettings.Validate(alerts);

            var fanBoxes = curveTemps.Concat(curvePcts).Concat(new[]
            {
                fanFixed, fanHyst, rampUp, rampDown, smoothing, zeroBelow, failCore, failHot, failMem, smartHot, smartCore,
                assistHot, assistMem, assistGap, assistBoost, assistHold, assistMargin,
            });
            if (problem is null && fanBoxes.Any(b => double.IsNaN(b.Value))) problem = "Every fan setting needs a number.";
            var fans = current.Fans with
            {
                Mode = MainViewModel.FanModeOrder[Math.Max(0, fanMode.SelectedIndex)],
                SmartTargetHotspotC = smartHot.Value,
                SmartTargetCoreC = smartCore.Value,
                SmartPriority = (SmartPriority)Math.Max(0, smartPriority.SelectedIndex),
                SmartIdleStop = smartIdle.IsOn,
                Source = (FanSource)Math.Max(0, fanSource.SelectedIndex),
                FixedPercent = (int)Math.Round(fanFixed.Value),
                Curve = Enumerable.Range(0, CurvePoints).Select(i => new FanPoint(curveTemps[i].Value, (int)Math.Round(curvePcts[i].Value))).ToList(),
                HysteresisC = fanHyst.Value,
                RampUpPerSecond = rampUp.Value,
                RampDownPerSecond = rampDown.Value,
                SmoothingSeconds = smoothing.Value,
                ZeroRpm = zeroRpm.IsOn,
                ZeroRpmBelowC = zeroBelow.Value,
                FailsafeTempC = failCore.Value,
                FailsafeHotspotC = failHot.Value,
                FailsafeMemoryC = failMem.Value,
                AutoAssist = assist.IsOn,
                AssistHotspotC = assistHot.Value,
                AssistMemoryC = assistMem.Value,
                AssistGapC = assistGap.Value,
                AssistBoostPercent = (int)Math.Round(assistBoost.Value),
                AssistHoldSeconds = assistHold.Value,
                AssistReleaseMarginC = assistMargin.Value,
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

            bool wantStart = autostart.IsOn;
            if (problem is null && (wantStart != (startState != AutostartState.Off) || (wantStart && startState != AutostartState.On)))
            {
                // Registering the task can show a UAC prompt: wait for it off the UI thread.
                var deferral = args.GetDeferral();
                dialog.IsPrimaryButtonEnabled = false;
                try
                {
                    string? why = await Task.Run(() => Autostart.Set(wantStart, elevate: true));
                    if (why is not null) problem = $"Start with Windows wasn't {(wantStart ? "set up" : "turned off")}: {why}";
                    else startState = wantStart ? AutostartState.On : AutostartState.Off;
                }
                catch (Exception ex) { problem = $"Start with Windows wasn't changed: {ex.Message}"; }
                finally { dialog.IsPrimaryButtonEnabled = true; }
                Finish();
                deferral.Complete(); // after Finish: args.Cancel must be set before the deferral ends
            }
            else Finish();

            void Finish()
            {
                if (problem is null)
                {
                    try { updated.Save(); }
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
            }
        };

        await dialog.ShowAsync();
        return result;
    }
}
