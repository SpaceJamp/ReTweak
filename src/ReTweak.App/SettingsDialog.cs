using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ReTweak.Core;

namespace ReTweak.App;

/// <summary>Settings dialog. Returns the saved settings, or null if cancelled.</summary>
internal static class SettingsDialog
{
    /// <param name="saveDiagnostics">Writes the diagnostics zip and returns its path.</param>
    public static async Task<AppSettings?> ShowAsync(XamlRoot root, AppSettings current, string? activeLogPath, Func<string> saveDiagnostics)
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
        // Diagnostic log
        var diagLevel = new ComboBox
        {
            Header = "Diagnostic log", MinWidth = 300,
            ItemsSource = new[]
            {
                "Off",
                "Events: startup, settings, alerts, errors, fan mode changes",
                "Detailed: + every sensor reading, fan decision, driver-call timing",
                "Everything: + raw 10 Hz hotspot reads (large files)",
            },
            SelectedIndex = current.DiagnosticsLevel switch { DiagLevel.Off => 0, DiagLevel.Info => 1, DiagLevel.Debug => 2, _ => 3 },
            IsEnabled = !Diagnostics.ForcedByArg,
        };
        var diagNote = new TextBlock
        {
            Text = (Diagnostics.Problem is { } problem ? problem + " " : "") +
                   (Diagnostics.ForcedByArg ? "Started with --debug: logging everything for this run. " : "") +
                   (Diag.CurrentFile is { } f0 ? $"Writing {f0}" : $"Files go to {Diagnostics.Folder} (10 MB each, last 10 kept)."),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
        };
        var saveDiag = new Button { Content = "Save diagnostics to Desktop" };
        var openDiag = new HyperlinkButton { Content = "Open log folder" };
        saveDiag.Click += (_, _) =>
        {
            try { Diagnostics.Reveal(saveDiagnostics()); }
            catch (Exception ex) { diagNote.Text = $"Couldn't save diagnostics: {ex.Message}"; }
        };
        openDiag.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(Diagnostics.Folder);
                TrustedPaths.OpenInExplorer(Diagnostics.Folder); // full path to explorer.exe, no ShellExecute
            }
            catch (Exception ex) { diagNote.Text = $"Couldn't open the folder: {ex.Message}"; }
        };
        var diagButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        diagButtons.Children.Add(saveDiag);
        diagButtons.Children.Add(openDiag);

        // Security check: runs off the UI thread (signature checks, registry, ACLs), results shown inline.
        var securityResults = new StackPanel { Spacing = 6 };
        var runSecurity = new Button { Content = "Run security check" };
        runSecurity.Click += async (_, _) =>
        {
            runSecurity.IsEnabled = false;
            securityResults.Children.Clear();
            securityResults.Children.Add(new ProgressRing { IsActive = true, Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Left });
            var findings = await Task.Run(() => Diagnostics.RunSecurityCheck(current));
            securityResults.Children.Clear();
            int warn = findings.Count(x => x.Status == CheckStatus.Warn), fail = findings.Count(x => x.Status == CheckStatus.Fail);
            securityResults.Children.Add(new InfoBar
            {
                IsOpen = true, IsClosable = false,
                Severity = fail > 0 ? InfoBarSeverity.Error : warn > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success,
                Title = fail > 0 ? $"{fail} problem(s) found" : warn > 0 ? $"{warn} thing(s) to look at" : "All clear",
                Message = $"{findings.Count(x => x.Status == CheckStatus.Pass)} passed, {findings.Count(x => x.Status == CheckStatus.Info)} for information.",
            });
            foreach (var x in findings.OrderByDescending(x => x.Status))
            {
                var item = new StackPanel { Spacing = 2 };
                item.Children.Add(new TextBlock
                {
                    Text = $"{(x.Status switch { CheckStatus.Fail => "✖", CheckStatus.Warn => "⚠", CheckStatus.Info => "ℹ", _ => "✔" })}  {x.Area} · {x.Title}",
                    Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], TextWrapping = TextWrapping.Wrap,
                    Foreground = x.Status switch { CheckStatus.Fail => Theme.Critical, CheckStatus.Warn => Theme.Warn, CheckStatus.Pass => Theme.Ok, _ => Theme.Muted },
                });
                if (x.Detail.Length > 0) item.Children.Add(Caption(x.Detail));
                if (x.Advice.Length > 0) item.Children.Add(Caption("→ " + x.Advice));
                securityResults.Children.Add(item);
            }
            runSecurity.IsEnabled = true;
        };

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
        var dieWarn = Num("Die spread warning (°C above the coolest sensor)", o.WarnDieSpreadC);
        var dieCrit = Num("Die spread critical (°C)", o.CriticalDieSpreadC);
        foreach (var n in new[] { coreWarn, coreCrit, memWarn, memCrit, hotWarn, hotCrit, dieWarn, dieCrit }) n.SmallChange = 1;
        foreach (var n in new[] { spreadWarn, spreadCrit }) n.SmallChange = 5;
        foreach (var n in new[] { lowWarn, lowCrit, highWarn, imbWarn, imbCrit }) n.SmallChange = 0.01;

        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var pairs = new (NumberBox, NumberBox?)[]
        {
            (pinWarn, pinCrit), (lowWarn, lowCrit), (highWarn, null), (spreadWarn, spreadCrit), (imbWarn, imbCrit),
            (coreWarn, coreCrit), (memWarn, memCrit), (hotWarn, hotCrit), (dieWarn, dieCrit),
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
            dieWarn.Value = d.WarnDieSpreadC; dieCrit.Value = d.CriticalDieSpreadC;
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
        var smartInput = new ComboBox
        {
            Header = "Auto: hold the target on", MinWidth = 220, SelectedIndex = (int)f.SmartInput,
            ItemsSource = new[] { "Hotspot (fastest)", "Average of core, hotspot and memory (steadier)" },
        };
        ToolTipService.SetToolTip(smartInput, "The average moves less with every load change. It runs below the hotspot (about 2 °C at idle, " +
                                              "about 11 °C in games on an RTX 5080), so pick targets for it. Failsafes, the ceiling and the memory " +
                                              "and VRM limits always watch each sensor on its own.");
        var smartPriority = new ComboBox
        {
            Header = "Auto: favour", MinWidth = 220, SelectedIndex = (int)f.SmartPriority,
            ItemsSource = new[] { "Quiet (slow, rare changes)", "Balanced", "Tight (holds within ~1 °C)" },
        };
        var smartIdle = new ToggleSwitch { Header = "Auto: 0 RPM at light load (idle, desktop, video)", IsOn = f.SmartIdleStop };
        var smartIdleBelow = Num("Auto: 0 RPM below (°C hotspot; fans return 5 °C above)", f.SmartIdleStopBelowC);
        smartIdleBelow.SmallChange = 1;
        var ceiling = Num("Auto: keep the hotspot under (°C, 0 = no ceiling)", f.CeilingC);
        ceiling.SmallChange = 1;
        var adapt = new ToggleSwitch { Header = "Auto: adapt to what you're doing (idle, video, gaming, rendering)", IsOn = f.AdaptToActivity };
        var foreground = new ToggleSwitch
        {
            Header = "Auto: look at the foreground app (fullscreen, program name; stays on this PC)", IsOn = f.UseForegroundApp,
        };
        var kinds = new[] { ActivityKind.Idle, ActivityKind.Desktop, ActivityKind.Video, ActivityKind.Gaming, ActivityKind.Compute };
        var styles = new[] { "Quiet", "Balanced", "Tight" };
        var actTargets = new Dictionary<ActivityKind, NumberBox>();
        var actStyles = new Dictionary<ActivityKind, ComboBox>();
        void FillActivities(IReadOnlyDictionary<ActivityKind, ActivityProfile> profiles)
        {
            foreach (var k in kinds)
            {
                var prof = profiles.TryGetValue(k, out var v) ? v : FanOptions.DefaultActivities[k];
                actTargets[k].Value = prof.TargetHotspotC;
                actStyles[k].SelectedIndex = (int)prof.Priority;
            }
        }
        foreach (var k in kinds)
        {
            actTargets[k] = Num($"{k}: hotspot target (°C)", 0);
            actTargets[k].SmallChange = 1;
            actStyles[k] = new ComboBox { Header = $"{k}: style", ItemsSource = styles, MinWidth = 150 };
        }
        FillActivities(f.Activities);
        var activityGrid = Pairs(kinds.Select(k => ((FrameworkElement)actTargets[k], (FrameworkElement?)actStyles[k])).ToArray());
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
            smartPriority.SelectedIndex = (int)d.SmartPriority; smartIdle.IsOn = d.SmartIdleStop; smartIdleBelow.Value = d.SmartIdleStopBelowC;
            smartInput.SelectedIndex = (int)d.SmartInput;
            ceiling.Value = d.CeilingC; adapt.IsOn = d.AdaptToActivity; foreground.IsOn = d.UseForegroundApp;
            FillActivities(d.Activities);
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
            Section("Diagnostics"), diagLevel, diagNote, diagButtons,
            Section("About"), Caption($"ReTweak {BuildInfo.Display}"),
            Section("Security"), Caption("Checks ReTweak's files, autostart, the PawnIO driver, data folders, known UAC-bypass entries, " +
                                         "risky drivers and Windows protections. Read-only."), runSecurity, securityResults,
            Section("Alert thresholds"), grid, reset,
            Section("Fan control"), fanNote, Pairs((fanMode, fanSource)), fanFixed,
            Section("Auto"), ceiling, adapt, foreground, activityGrid,
            Caption("When Auto can't tell what you're doing (no load readings), it uses these:"),
            Pairs((smartHot, smartCore), (smartPriority, smartInput)), smartIdle, smartIdleBelow,
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
            var boxes = new[] { pinWarn, pinCrit, lowWarn, lowCrit, highWarn, spreadWarn, spreadCrit, imbWarn, imbCrit, coreWarn, coreCrit, memWarn, memCrit, hotWarn, hotCrit, dieWarn, dieCrit };
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
                WarnDieSpreadC = dieWarn.Value,
                CriticalDieSpreadC = dieCrit.Value,
            };
            problem ??= AppSettings.Validate(alerts);

            var fanBoxes = curveTemps.Concat(curvePcts).Concat(new[]
            {
                fanFixed, fanHyst, rampUp, rampDown, smoothing, zeroBelow, failCore, failHot, failMem, smartHot, smartCore, ceiling, smartIdleBelow,
                assistHot, assistMem, assistGap, assistBoost, assistHold, assistMargin,
            });
            if (problem is null && fanBoxes.Any(b => double.IsNaN(b.Value))) problem = "Every fan setting needs a number.";
            var fans = current.Fans with
            {
                Mode = MainViewModel.FanModeOrder[Math.Max(0, fanMode.SelectedIndex)],
                SmartTargetHotspotC = smartHot.Value,
                SmartTargetCoreC = smartCore.Value,
                SmartPriority = (SmartPriority)Math.Max(0, smartPriority.SelectedIndex),
                SmartInput = (SmartInput)Math.Max(0, smartInput.SelectedIndex),
                SmartIdleStop = smartIdle.IsOn,
                SmartIdleStopBelowC = smartIdleBelow.Value,
                CeilingC = ceiling.Value,
                AdaptToActivity = adapt.IsOn,
                UseForegroundApp = foreground.IsOn,
                Activities = kinds.ToDictionary(k => k, k =>
                {
                    var old = current.Fans.Activities.TryGetValue(k, out var v) ? v : FanOptions.DefaultActivities[k];
                    return old with { TargetHotspotC = actTargets[k].Value, Priority = (SmartPriority)Math.Max(0, actStyles[k].SelectedIndex) };
                }),
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
                DiagnosticsLevel = Diagnostics.ForcedByArg ? current.DiagnosticsLevel
                    : diagLevel.SelectedIndex switch { 1 => DiagLevel.Info, 2 => DiagLevel.Debug, 3 => DiagLevel.Trace, _ => DiagLevel.Off },
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
                    Diag.Warn(DiagCategory.Settings, "settings not saved", new { problem });
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
