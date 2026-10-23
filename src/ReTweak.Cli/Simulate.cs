using ReTweak.Core;

/// <summary>
/// Feeds scripted scenarios through the alert engine at the real chip's frame rate
/// (~600 ms, measured in a load test), reports when each alert fires relative to the
/// hard limit, and checks each scenario's expectation. Exit code = failed scenarios.
/// </summary>
static class Simulate
{
    private const double FrameSec = 0.6;
    private static int _failures;

    public static int Run()
    {
        _failures = 0;

        Scenario("Voltage sag: all pins fall 60 mV/s under 30 A load", 20, t =>
            Pins(v: _ => 12.0 - Math.Max(0, t - 2) * 0.060, a: _ => 5.0),
            limit: r => r.MinVolts < 11.40, limitName: "pin < 11.40 V",
            expect: "trend:volts", beforeLimit: true);

        Scenario("Bad contact: pin 3 voltage drifts 8 mV/s, current shifts away", 30, t =>
        {
            double d = Math.Max(0, t - 2);
            return Pins(v: p => p == 3 ? 11.98 - d * 0.008 : 11.98,
                        a: p => p == 3 ? Math.Max(1, 5 - d * 0.15) : 5 + d * 0.03);
        }, limit: r => r.MinVolts < 11.40, limitName: "pin < 11.40 V", expect: "spread");

        Scenario("Current ramp: pin 1 climbs 1 A/s from 6 A", 10, t =>
            Pins(v: _ => 11.95, a: p => p == 1 ? 6 + Math.Max(0, t - 1) : 6),
            limit: r => r.MaxAmps >= 9.5, limitName: "pin >= 9.5 A",
            expect: "trend:amps", beforeLimit: true);

        Scenario("Hovering at the critical line: pin 4 alternates 9.4 / 9.6 A", 6, t =>
            Pins(v: _ => 11.95, a: p => p == 4 ? (Math.Round(t / FrameSec) % 2 == 0 ? 9.6 : 9.4) : 6),
            limit: r => r.MaxAmps >= 9.5, limitName: "pin >= 9.5 A", expect: "amps:4", severity: AlertSeverity.Critical);

        Scenario("Hovering at the warning line: pin 2 alternates 7.5 / 7.7 A", 12, t =>
            Pins(v: _ => 11.95, a: p => p == 2 ? (Math.Round(t / FrameSec) % 2 == 0 ? 7.7 : 7.5) : 6),
            limit: _ => false, limitName: "none", expect: "amps:2", severity: AlertSeverity.Warning);

        Scenario("Normal load step: game starts, 1 -> 6 A per pin in 0.6 s, 80 mV droop (no alert)", 10, t =>
        {
            double k = Math.Clamp((t - 2) / 0.6, 0, 1);
            return Pins(v: _ => 12.0 - 0.08 * k, a: _ => 1 + 5 * k);
        }, limit: _ => false, limitName: "none", expect: null);

        Scenario("Single-frame glitch: one frame reads 8 V (no alert)", 5, t =>
            Pins(v: p => p == 2 && Math.Abs(t - 2) < 0.01 ? 8.0 : 11.98, a: _ => 5),
            limit: _ => false, limitName: "none", expect: null);

        StaleCheck();
        TempCheck();
        HotspotDecodeCheck();
        DieSpreadCheck();
        FanCheck();
        SmartCheck();
        HardeningCheck();
        PowerEmergencyCheck();
        AdaptiveCheck();
        TrustedPathsCheck();
        LinuxHotspotCheck();

        Console.WriteLine(_failures == 0 ? "\nAll scenarios passed." : $"\n{_failures} scenario(s) FAILED.");
        return _failures;
    }

    private static void Scenario(string name, double seconds, Func<double, ConnectorReading> at,
                                 Func<ConnectorReading, bool> limit, string limitName,
                                 string? expect, AlertSeverity severity = AlertSeverity.Warning, bool beforeLimit = false)
    {
        Console.WriteLine($"\n== {name}");
        var engine = new AlertEngine();
        var start = new DateTime(2026, 1, 1);
        double now = 0;
        double? limitHit = null, expectedAt = null;
        int raised = 0;
        engine.Raised += a =>
        {
            raised++;
            Console.WriteLine($"  t={now,5:F1}s  {a.Severity,-8} {a.Title}: {a.Detail}");
            if (expectedAt is null && a.Key == expect && a.Severity >= severity) expectedAt = now;
        };
        engine.Cleared += k => Console.WriteLine($"  t={now,5:F1}s  cleared  {k}");

        for (now = 0; now <= seconds; now = Math.Round(now + FrameSec, 3))
        {
            var r = at(now);
            if (limitHit is null && limit(r))
            {
                limitHit = now;
                Console.WriteLine($"  t={now,5:F1}s  ** hard limit reached ({limitName})");
            }
            engine.Process(new ConnectorFrame(start.AddSeconds(now), TimeSpan.FromSeconds(now), r, r));
        }

        string verdict;
        bool pass;
        if (expect is null)
        {
            pass = raised == 0;
            verdict = pass ? "no alerts, as expected" : $"{raised} unexpected alert(s)";
        }
        else if (expectedAt is null)
        {
            pass = false;
            verdict = $"expected {severity} '{expect}' never fired";
        }
        else if (beforeLimit && limitHit is double hit)
        {
            pass = expectedAt < hit;
            verdict = pass ? $"'{expect}' fired {hit - expectedAt:F1} s before the limit" : $"'{expect}' fired late (t={expectedAt:F1}s)";
        }
        else
        {
            pass = true;
            verdict = $"'{expect}' fired at t={expectedAt:F1}s";
        }
        if (!pass) _failures++;
        Console.WriteLine($"  {(pass ? "PASS" : "FAIL")}: {verdict}");
    }

    private static void StaleCheck()
    {
        Console.WriteLine("\n== Frozen sensor: reads succeed but data stops changing for 12 s");
        var engine = new AlertEngine();
        bool fired = false;
        engine.Raised += a => { if (a.Key == "stale") { fired = true; Console.WriteLine($"  {a.Severity} {a.Title}: {a.Detail}"); } };
        for (double t = 0; t <= 12; t += 0.1)
            engine.NoNewData(TimeSpan.FromSeconds(t), TimeSpan.FromSeconds(t));
        if (!fired) _failures++;
        Console.WriteLine($"  {(fired ? "PASS" : "FAIL")}: stale-data alert {(fired ? "fired" : "never fired")}");
    }

    /// <summary>Feeds a logged CSV (from --alerts --log) through the current alert engine.</summary>
    public static int Replay(string path)
    {
        var engine = new AlertEngine();
        int raised = 0;
        DateTime t = default;
        engine.Raised += a => { raised++; Console.WriteLine($"  {t:HH:mm:ss.f}  {a.Severity,-8} {a.Title}: {a.Detail}"); };
        engine.Cleared += k => Console.WriteLine($"  {t:HH:mm:ss.f}  cleared  {k}");

        var lines = File.ReadAllLines(path).Skip(1).Where(l => l.Length > 0).ToList();
        DateTime? first = null;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var line in lines)
        {
            var c = line.Split(',');
            t = DateTime.Parse(c[0], inv, System.Globalization.DateTimeStyles.RoundtripKind);
            first ??= t;
            var pins = Enumerable.Range(0, 6)
                .Select(i => new PinReading(i + 1, double.Parse(c[1 + 2 * i], inv), double.Parse(c[2 + 2 * i], inv), true))
                .ToList();
            var r = new ConnectorReading(pins);
            engine.Process(new ConnectorFrame(t, t - first.Value, r, r));
        }
        Console.WriteLine($"Replayed {lines.Count} frames over {(t - first.GetValueOrDefault()).TotalMinutes:F1} min: {raised} alert(s).");
        return raised;
    }

    /// <summary>GPU temperature alerts, sampled once a second like the app does.</summary>
    private static void TempCheck()
    {
        var gpu = new GpuInfo(0, "Simulated GPU", 0, 0, 0, 0);
        GpuSnapshot Snap(double core, double mem) => new(DateTime.Now, gpu, (uint)core, null, null, null, null, null, null,
                                                          [], null, null, null, CoreTempPreciseC: core, MemoryTempC: mem);

        Console.WriteLine("\n== Memory temperature climbs 1 °C/s from 70 °C (warn 90, critical 100)");
        var engine = new AlertEngine();
        double? warnAt = null, critAt = null;
        int t = 0;
        engine.Raised += a =>
        {
            if (a.Key != "temp:memory") return;
            Console.WriteLine($"  t={t,3}s  {a.Severity,-8} {a.Title}: {a.Detail}");
            if (a.Severity == AlertSeverity.Warning) warnAt ??= t;
            if (a.Severity == AlertSeverity.Critical) critAt ??= t;
        };
        for (t = 0; t <= 40; t++) engine.ProcessGpu(Snap(60, 70 + t));
        bool pass = warnAt is >= 20 and <= 24 && critAt is >= 30 && critAt <= 33;
        if (!pass) _failures++;
        Console.WriteLine($"  {(pass ? "PASS" : "FAIL")}: warning at {warnAt?.ToString() ?? "never"} s (crossed 90 °C at 20 s), " +
                          $"critical at {critAt?.ToString() ?? "never"} s (crossed 100 °C at 30 s)");

        Console.WriteLine("\n== One-second memory spike to 101 °C (no alert)");
        var quiet = new AlertEngine();
        int raised = 0;
        quiet.Raised += _ => raised++;
        for (t = 0; t <= 10; t++) quiet.ProcessGpu(Snap(60, t == 5 ? 101 : 70));
        if (raised != 0) _failures++;
        Console.WriteLine($"  {(raised == 0 ? "PASS" : "FAIL")}: {raised} alert(s)");

        HotspotCheck(gpu);
    }

    /// <summary>RTX 50 hotspot alerts (placeholder thresholds: warn 95, critical 105).</summary>
    private static void HotspotCheck(GpuInfo gpu)
    {
        GpuSnapshot Snap(double? hotspot) => new(DateTime.Now, gpu, 70, null, null, null, null, null, null,
                                                 [], null, null, null, CoreTempPreciseC: 70, MemoryTempC: 70, HotspotC: hotspot);
        var o = new AlertOptions();

        Console.WriteLine($"\n== Hotspot climbs 1 °C/s from 80 °C (warn {o.WarnHotspotTempC:F0}, critical {o.CriticalHotspotTempC:F0})");
        var engine = new AlertEngine();
        double? warnAt = null, critAt = null;
        int t = 0;
        engine.Raised += a =>
        {
            if (a.Key != "temp:hotspot") return;
            Console.WriteLine($"  t={t,3}s  {a.Severity,-8} {a.Title}: {a.Detail}");
            if (a.Severity == AlertSeverity.Warning) warnAt ??= t;
            if (a.Severity == AlertSeverity.Critical) critAt ??= t;
        };
        for (t = 0; t <= 35; t++) engine.ProcessGpu(Snap(80 + t));
        bool pass = warnAt is >= 15 and <= 19 && critAt is >= 25 and <= 28;
        if (!pass) _failures++;
        Console.WriteLine($"  {(pass ? "PASS" : "FAIL")}: warning at {warnAt?.ToString() ?? "never"} s (crossed 95 °C at 15 s), " +
                          $"critical at {critAt?.ToString() ?? "never"} s (crossed 105 °C at 25 s)");

        Console.WriteLine("\n== One-second hotspot spike to 110 °C (no alert)");
        var quiet = new AlertEngine();
        int raised = 0;
        quiet.Raised += _ => raised++;
        for (t = 0; t <= 10; t++) quiet.ProcessGpu(Snap(t == 5 ? 110 : 85));
        if (raised != 0) _failures++;
        Console.WriteLine($"  {(raised == 0 ? "PASS" : "FAIL")}: {raised} alert(s)");

        Console.WriteLine("\n== Hotspot hovers at 94.5 / 95.5 °C, then cools to 85 °C (hysteresis)");
        var hover = new AlertEngine();
        double? firedAt = null, clearedAt = null;
        hover.Raised += a => { if (a.Key == "temp:hotspot") firedAt ??= t; };
        hover.Cleared += k => { if (k == "temp:hotspot") clearedAt ??= t; };
        for (t = 0; t <= 12; t++) hover.ProcessGpu(Snap(t % 2 == 0 ? 95.5 : 94.5));
        int cooledFrom = t;
        for (; t < 40; t++) hover.ProcessGpu(Snap(85));
        pass = firedAt is not null && clearedAt == cooledFrom + o.ClearFrames - 1; // the 15th cool sample clears it
        if (!pass) _failures++;
        Console.WriteLine($"  {(pass ? "PASS" : "FAIL")}: warning at {firedAt?.ToString() ?? "never"} s, cleared at " +
                          $"{clearedAt?.ToString() ?? "never"} s (cooled at {cooledFrom} s, needs {o.ClearFrames} cool samples)");

        Console.WriteLine("\n== Hotspot reading lost (PawnIO unavailable) while critical: alert stays");
        var lost = new AlertEngine();
        bool cleared = false;
        lost.Cleared += k => cleared |= k == "temp:hotspot";
        for (t = 0; t < 5; t++) lost.ProcessGpu(Snap(108));
        for (; t < 30; t++) lost.ProcessGpu(Snap(null));
        pass = !cleared && lost.Active.Any(a => a.Key == "temp:hotspot" && a.Severity == AlertSeverity.Critical);
        if (!pass) _failures++;
        Console.WriteLine($"  {(pass ? "PASS" : "FAIL")}: {(pass ? "critical alert kept" : "alert cleared without a reading")}");
    }

    private static void HotspotDecodeCheck()
    {
        // Raw values in the format of GPU register 0xAD0A90 (bit 30 = valid, low 16 bits = 1/256 °C).
        var cases = new (long Raw, double? Expect, string What)[]
        {
            (0x40002600, 38.0, "valid 38.00 °C"),
            (0x400025CD, 37.8, "valid 37.80 °C"),
            (0x00002600, null, "valid bit clear"),
            (0x4000FF00, null, "255 °C: no sensor"),
            (0x40000000, null, "0 °C: implausible"),
        };
        Console.WriteLine("\n== Hotspot register decoding");
        foreach (var (raw, expect, what) in cases)
        {
            double? got = NvidiaHotspot.Decode(raw);
            bool pass = expect is null ? got is null : got is double g && Math.Abs(g - expect.Value) < 0.01;
            if (!pass) _failures++;
            Console.WriteLine($"  {(pass ? "PASS" : "FAIL")}: 0x{raw:X8} ({what}) -> {got?.ToString("F2") ?? "none"}");
        }
    }

    private static void DieSpreadCheck()
    {
        void Check(string what, bool pass, string detail)
        {
            if (!pass) _failures++;
            Console.WriteLine($"  {(pass ? "PASS" : "FAIL")}: {what} ({detail})");
        }
        Console.WriteLine("\n== Die spread and self-check of a read");
        // Real reads from the RTX 5080: idle, and a game at ~350 W.
        double?[] idle = [37.1, 34.3, 35.3, 38.7, 38.7, 35.6], game = [71.6, 52.3, 45.9, 67.4, 71.6, 63.9];
        Check("Spread is the hotspot minus the coolest sensor", Math.Abs(NvidiaHotspot.Spread(idle)!.Value - 4.4) < 0.01
              && Math.Abs(NvidiaHotspot.Spread(game)!.Value - 25.7) < 0.01, $"idle {NvidiaHotspot.Spread(idle):F1}, game {NvidiaHotspot.Spread(game):F1}");
        double?[] contradicts = [37.1, 34.3, 35.3, 38.7, 30.0, 35.6];
        Check("A maximum below the coolest sensor is rejected", NvidiaHotspot.Hotspot(contradicts) is null && NvidiaHotspot.Spread(contradicts) is null,
              $"hotspot {NvidiaHotspot.Hotspot(contradicts)?.ToString("F1") ?? "none"}");
        double?[] noMax = [37.1, 34.3, 35.3, 38.7, null, null], oneMissing = [37.1, null, 35.3, 38.7, 38.7, 35.6];
        Check("Without #5 the hottest sensor stands in; the spread needs all four sensors",
              NvidiaHotspot.Hotspot(noMax) == 38.7 && NvidiaHotspot.Spread(noMax) is double sp && Math.Abs(sp - 4.4) < 0.01
              && NvidiaHotspot.Spread(oneMissing) is null, $"spread with a sensor missing {NvidiaHotspot.Spread(oneMissing)?.ToString("F1") ?? "none"}");

        var window = new HotspotWindow(TimeSpan.FromSeconds(1));
        bool keptGood = window.Add(TimeSpan.FromMilliseconds(100), idle), keptBad = window.Add(TimeSpan.FromMilliseconds(200), contradicts);
        window.Add(TimeSpan.FromMilliseconds(300), [41.1, 34.3, 35.3, 38.7, 41.1, 36.0]);
        var sum = window.Summarize(TimeSpan.FromMilliseconds(300))!;
        Check("The window drops a bad read and averages the spread", keptGood && !keptBad && sum.Samples == 2
              && Math.Abs(sum.SpreadC!.Value - 5.6) < 0.01 && Math.Abs(sum.DieAverageC!.Value - 35.8) < 0.01,
              $"{sum.Samples} samples, spread {sum.SpreadC:F1}, die average {sum.DieAverageC:F1}");

        var gpu = new GpuInfo(0, "Simulated", 1, 0x2C02, 0x1043, 0x89DE);
        GpuSnapshot Snap(double? spread) => new(DateTime.Now, gpu, 70, null, null, null, null, null, null,
                                                [], null, null, null, CoreTempPreciseC: 70, HotspotC: 70, HotspotSpreadC: spread);
        var o = new AlertOptions();
        var alerts = new AlertEngine();
        for (int t = 0; t < 30; t++) alerts.ProcessGpu(Snap(31.6)); // the most a healthy card showed, at 380 W
        bool quiet = !alerts.Active.Any(a => a.Key == "temp:spread");
        for (int t = 0; t < 10; t++) alerts.ProcessGpu(Snap(45));
        var warn = alerts.Active.FirstOrDefault(a => a.Key == "temp:spread")?.Severity;
        for (int t = 0; t < 10; t++) alerts.ProcessGpu(Snap(55));
        var crit = alerts.Active.FirstOrDefault(a => a.Key == "temp:spread")?.Severity;
        Check($"Die-spread alert: none at a healthy 31.6 °C, warning over {o.WarnDieSpreadC:F0}, critical over {o.CriticalDieSpreadC:F0}",
              quiet && warn == AlertSeverity.Warning && crit == AlertSeverity.Critical, $"{warn}, then {crit}");
    }

    /// <summary>A fake card for <see cref="FanController"/>: records writes, can refuse them or be overridden.</summary>
    private sealed class FakeFans
    {
        public int? Speed;            // null = driver's automatic curve
        public int DriverSpeed = 35;  // what the driver's own curve is running at
        public int Writes, Restores;
        public string? Refuse;
        public int? ForeignTarget;    // another program's speed, if one is fighting
        public TimeSpan Now;
        public FanController Make(FanOptions o, (int, int)? range = null) => new(
            p => { Writes++; if (Refuse is not null) return Refuse; Speed = p; return null; },
            () => { Restores++; Speed = null; return null; },
            () => ForeignTarget ?? Speed ?? DriverSpeed, range ?? (0, 100), () => Now) { Options = o };

        /// <summary>One second passes, then a tick.</summary>
        public FanStatus Tick(FanController fc, double? core, double? hotspot = null, double? memory = null)
        {
            Now += TimeSpan.FromSeconds(1);
            return fc.Tick(new FanTemps(core, hotspot, memory));
        }
    }

    private static void Check(string name, bool pass, string detail)
    {
        if (!pass) _failures++;
        Console.WriteLine($"\n== {name}\n  {(pass ? "PASS" : "FAIL")}: {detail}");
    }

    private static void FanCheck()
    {
        // Pure curve logic: no ramping, smoothing or safety net. Balanced: 40/30 55/40 65/55 75/75 83/100, 3 °C hysteresis.
        var curve = new FanOptions { Mode = FanMode.Curve, RampUpPerSecond = 0, RampDownPerSecond = 0, SmoothingSeconds = 0, AutoAssist = false };

        var fake = new FakeFans();
        using (var fc = fake.Make(curve))
        {
            var up = Enumerable.Range(40, 41).Select(t => { fake.Tick(fc, t); return fake.Speed ?? -1; }).ToList();
            bool rising = up.Zip(up.Skip(1)).All(x => x.Second >= x.First);
            Check("Fan curve: core 40 -> 80 °C", rising && up[0] == 30 && up[25] == 55 && up[40] == 91,
                  $"speeds never drop, 30% at 40 °C ({up[0]}), 55% at 65 °C ({up[25]}), 91% at 80 °C ({up[40]})");

            fake.Tick(fc, 70);
            int at70 = fake.Speed ?? -1;
            int writes = fake.Writes;
            for (int i = 0; i < 20; i++) fake.Tick(fc, i % 2 == 0 ? 69 : 71); // ±1 °C wobble
            Check("Fan curve: core wobbles 69/71 °C after cooling from 80 °C", fake.Writes - writes <= 1,
                  $"{fake.Writes - writes} speed change(s) in 20 s with hysteresis (held {at70}%)");

            fake.Tick(fc, 60);
            Check("Fan curve: core cools to 60 °C", fake.Speed is >= 47 and <= 55,
                  $"stepped down to {fake.Speed}% (curve says 48% at 60 °C, 55% at 63 °C)");
        }
        Check("Fan control: dispose", fake.Speed is null && fake.Restores == 1, $"driver back in charge, {fake.Restores} restore(s)");

        fake = new FakeFans();
        using (var fc = fake.Make(curve with { Mode = FanMode.Fixed, FixedPercent = 35 }))
        {
            fake.Tick(fc, 60);
            int before = fake.Speed ?? -1;
            var st = fake.Tick(fc, 90);
            Check("Fixed 35% when core hits 90 °C (failsafe 88 °C)", before == 35 && fake.Speed == 100 && st.Severity == AlertSeverity.Warning,
                  $"{before}% -> {fake.Speed}% : {st.Text}");
        }

        fake = new FakeFans();
        using (var fc = fake.Make(curve))
        {
            fake.Tick(fc, 60);
            var st = fc.Status;
            for (int i = 0; i < FanController.MissedTempsBeforeRelease; i++) st = fake.Tick(fc, null);
            Check("Temperature reading lost for 3 s", fake.Speed is null && fake.Restores == 1, st.Text);
            fake.Tick(fc, 60);
            Check("Temperature reading back", fake.Speed == 48, $"curve resumed at {fake.Speed}%");
        }

        fake = new FakeFans { Refuse = "needs administrator rights" };
        using (var fc = fake.Make(curve))
        {
            var st = fake.Tick(fc, 60);
            for (int i = 0; i < 5; i++) st = fake.Tick(fc, 60);
            Check("Not running as administrator", st.NeedsAdmin && fake.Writes == 1 && fake.Restores == 1 && fake.Speed is null,
                  $"{fake.Writes} write attempt(s), fans on automatic: {st.Text}");
        }

        fake = new FakeFans();
        using (var fc = fake.Make(curve with { Curve = [new(40, 0), new(80, 100)] }, range: (30, 100)))
        {
            fake.Tick(fc, 40);
            Check("Curve asks 0% but the driver's minimum is 30%", fake.Speed == 30, $"set {fake.Speed}%");
        }

        fake = new FakeFans();
        using (var fc = fake.Make(curve))
        {
            fake.Tick(fc, 60);
            fc.Options = curve with { Mode = FanMode.Driver };
            fake.Tick(fc, 60);
            fake.Tick(fc, 60);
            Check("Switch Curve -> Auto", fake.Speed is null && fake.Restores == 1 && fake.Writes == 1,
                  $"{fake.Restores} restore(s), no further writes ({fake.Writes} total)");
        }

        fake = new FakeFans();
        using (var fc = fake.Make(curve))
        {
            fake.Tick(fc, 60);
            fake.ForeignTarget = 80; // e.g. GPU Tweak III's fan service still running
            var st = fc.Status;
            for (int i = 0; i < 4; i++) st = fake.Tick(fc, 60);
            Check("Another program keeps setting 80%", st.Severity == AlertSeverity.Warning && fake.Speed == 48, st.Text);
        }

        var bad = new FanOptions { Curve = [new(60, 50), new(50, 60)] }.Validate();
        Check("Curve with falling temperatures is rejected", bad is not null, bad ?? "accepted");

        // ---- Ramping ----
        var ramped = curve with { Mode = FanMode.Fixed, FixedPercent = 80, RampUpPerSecond = 10, RampDownPerSecond = 3 };
        fake = new FakeFans { DriverSpeed = 30 };
        using (var fc = fake.Make(ramped))
        {
            var seq = Enumerable.Range(0, 6).Select(_ => { fake.Tick(fc, 60); return fake.Speed ?? -1; }).ToList();
            Check("Ramp up 10 %/s: driver 30% -> fixed 80%", seq.SequenceEqual([40, 50, 60, 70, 80, 80]),
                  "speeds " + string.Join(", ", seq) + " (starts from the driver's speed)");
            fc.Options = ramped with { FixedPercent = 50 };
            var down = Enumerable.Range(0, 5).Select(_ => { fake.Tick(fc, 60); return fake.Speed ?? -1; }).ToList();
            Check("Ramp down 3 %/s: 80% -> 50%", down.SequenceEqual([77, 74, 71, 68, 65]), "speeds " + string.Join(", ", down));
            fake.Tick(fc, 90);
            Check("Failsafe skips the ramp", fake.Speed == 100, $"core 90 °C -> {fake.Speed}% in one tick");
        }

        // ---- Smoothing ----
        fake = new FakeFans();
        using (var fc = fake.Make(curve with { SmoothingSeconds = 2 }))
        {
            for (int i = 0; i < 5; i++) fake.Tick(fc, 60);
            int steady = fake.Speed ?? -1;
            fake.Tick(fc, 75); // a one-second spike
            int spike = fake.Speed ?? -1;
            Check("2 s smoothing: one-second spike 60 -> 75 °C", steady == 48 && spike <= 56,
                  $"{steady}% -> {spike}% (unsmoothed: 75%)");
        }

        // ---- Source ----
        fake = new FakeFans();
        using (var fc = fake.Make(curve with { Source = FanSource.Hotspot }))
        {
            var st = fake.Tick(fc, 50, hotspot: 65);
            bool fromHotspot = fake.Speed == 55;
            var st2 = fake.Tick(fc, 50, hotspot: null);
            Check("Curve follows hotspot; core when it's missing", fromHotspot && fake.Speed is >= 37 and <= 55 && st2.Text.Contains("core"),
                  $"hotspot 65 °C -> 55% ({st.Text}); no hotspot -> {st2.Text}");
        }
        fake = new FakeFans();
        using (var fc = fake.Make(curve with { Source = FanSource.Hottest }))
        {
            fake.Tick(fc, 50, hotspot: 60, memory: 70);
            Check("Curve follows the hottest of core/hotspot/memory", fake.Speed == 65, $"memory 70 °C -> {fake.Speed}%");
        }
        fake = new FakeFans();
        using (var fc = fake.Make(curve))
        {
            var st = fake.Tick(fc, 70, hotspot: 106);
            Check("Hotspot failsafe (105 °C) with the curve on core", fake.Speed == 100, st.Text);
        }

        // ---- Zero RPM ----
        fake = new FakeFans();
        using (var fc = fake.Make(curve with { ZeroRpm = true, ZeroRpmBelowC = 50 }))
        {
            var log = new List<string>();
            foreach (double t in new[] { 45.0, 51, 54, 51, 48 })
            {
                fake.Tick(fc, t);
                log.Add($"{t} °C: {(fake.Speed is int sp ? $"{sp}%" : "driver")}");
            }
            bool pass = log[0].EndsWith("driver") && log[1].EndsWith("driver") && !log[2].EndsWith("driver")
                     && !log[3].EndsWith("driver") && log[4].EndsWith("driver");
            Check("Zero RPM below 50 °C (takes over at 53 °C, hysteresis 3)", pass, string.Join("; ", log));
        }

        // ---- Auto safety net ----
        var auto = curve with { Mode = FanMode.Driver, AutoAssist = true };
        fake = new FakeFans { DriverSpeed = 40 };
        using (var fc = fake.Make(auto))
        {
            fake.Tick(fc, 70, hotspot: 90);
            bool idle = fake.Speed is null && fake.Writes == 0;
            var st = fake.Tick(fc, 70, hotspot: 96);
            int start = fake.Speed ?? -1;
            for (int i = 0; i < 10; i++) fake.Tick(fc, 70, hotspot: 96);
            int crept = fake.Speed ?? -1;
            for (int i = 0; i < 5; i++) fake.Tick(fc, 70, hotspot: 92); // in the release band: hold
            bool held = fake.Speed == crept;
            for (int i = 0; i < 30; i++) fake.Tick(fc, 72, hotspot: 85); // first cool reading starts the 30 s hold
            bool stillHeld = fake.Speed is not null;
            fake.Tick(fc, 72, hotspot: 85);
            Check("Auto safety net: hotspot 96 °C (driver at 40%)",
                  idle && start == 60 && crept == 80 && held && stillHeld && fake.Speed is null && st.Assisting,
                  $"90 °C: driver; 96 °C: {start}% then {crept}% after 10 s; held at 92 °C; back to the driver after 30 s cool ({st.Text})");
        }
        fake = new FakeFans { DriverSpeed = 40 };
        using (var fc = fake.Make(auto))
        {
            var st = fake.Tick(fc, 60, hotspot: 92);
            Check("Auto safety net: hotspot 32 °C above core", st.Assisting && fake.Speed == 60 && st.Text.Contains("cooler contact"), st.Text);
        }
        // A healthy RTX 5080 reached a 25.9 °C gap for a second at 300+ W (logs, 2026-10-21): not a contact problem.
        var healthy = new FakeFans { DriverSpeed = 40 };
        using (var fc = healthy.Make(auto))
        {
            var st = healthy.Tick(fc, 60, hotspot: 86);
            Check("Auto safety net: a healthy card's 26 °C gap doesn't trigger it", !st.Assisting && healthy.Speed is null, st.Text);
        }
        fake = new FakeFans { DriverSpeed = 40 };
        using (var fc = fake.Make(auto with { RampUpPerSecond = 10 }))
        {
            var st = fake.Tick(fc, 70, memory: 101);
            Check("Auto: memory failsafe (100 °C) jumps to full speed", fake.Speed == 100, st.Text);
        }
        fake = new FakeFans { Refuse = "needs administrator rights" };
        using (var fc = fake.Make(auto))
        {
            var st = fake.Tick(fc, 70, hotspot: 97);
            Check("Auto safety net without admin rights", st.NeedsAdmin && st.Severity == AlertSeverity.None && fake.Speed is null, st.Text);
        }

        var presets = FanOptions.Presets.Select(p => (p.Key, Problem: new FanOptions { Curve = p.Value }.Validate())).ToList();
        Check("Presets are valid curves", presets.All(p => p.Problem is null), string.Join(", ", presets.Select(p => $"{p.Key} {p.Problem ?? "ok"}")));
        Check("Default fan settings are valid", new FanOptions().Validate() is null, new FanOptions().Validate() ?? "ok");
        var lowered = new FanOptions { FailsafeTempC = 75, FailsafeHotspotC = 85, FailsafeMemoryC = 80 }.Validate();
        Check("Lowered failsafes (core 75, hotspot 85, memory 80 °C) can be saved", lowered is null, lowered ?? "accepted");

        // ---- Hotspot window: average to show, peak for safety ----
        fake = new FakeFans();
        using (var fc = fake.Make(curve with { Source = FanSource.Hotspot }))
        {
            fc.Tick(new FanTemps(60, HotspotC: 70, HotspotMaxC: 78));
            int fromAverage = fake.Speed ?? -1;
            var st = fc.Tick(new FanTemps(60, HotspotC: 70, HotspotMaxC: 106));
            Check("Hotspot failsafe uses the 1 s peak, the curve the average", fromAverage == 65 && fake.Speed == 100 && st.Text.Contains("106"),
                  $"average 70 °C, peak 78 °C -> curve {fromAverage}%; peak 106 °C -> {st.Text}");
        }
        fake = new FakeFans { DriverSpeed = 40 };
        using (var fc = fake.Make(curve with { Mode = FanMode.Driver, AutoAssist = true }))
        {
            var st = fc.Tick(new FanTemps(75, HotspotC: 88, HotspotMaxC: 96));
            Check("Auto safety net steps in on a 96 °C peak (average 88 °C)", st.Assisting && fake.Speed == 60, st.Text);
        }

        fake = new FakeFans { DriverSpeed = 40 };
        using (var fc = fake.Make(curve with { Mode = FanMode.Driver, AutoAssist = true }))
        {
            fake.Tick(fc, 70, hotspot: 96);
            for (int i = 0; i < FanController.MissedTempsBeforeRelease; i++) fake.Tick(fc, null);
            bool released = fake.Speed is null;
            var st = fake.Tick(fc, 60, hotspot: 65);
            Check("Readings drop out during an assist, then return cool", released && fake.Speed is null && !st.Assisting,
                  $"released on dropout: {released}; after: {st.Text}");
        }
        fake = new FakeFans();
        using (var fc = fake.Make(curve))
        {
            var st = fc.Tick(new FanTemps(null, HotspotC: 108, HotspotMaxC: 108));
            Check("No core reading but hotspot 108 °C", fake.Speed == 100, st.Text);
        }

        var window = new HotspotWindow(TimeSpan.FromSeconds(1));
        for (int i = 0; i < 10; i++) // 10 Hz, jumpy like the real register
        {
            double hot = i % 2 == 0 ? 40.0 : 46.0; // channel #5 = the max of sensors #1-#4
            window.Add(TimeSpan.FromMilliseconds(100 * i), [35.0, 34.0, 33.0, hot, hot, null]);
        }
        var sum = window.Summarize(TimeSpan.FromMilliseconds(950))!;
        bool windowOk = Math.Abs(sum.AverageC - 43) < 0.01 && sum.MaxC == 46 && sum.Samples == 10 && sum.Channels[0] == 35 && sum.Channels[5] is null;
        var stale = window.Summarize(TimeSpan.FromSeconds(5));
        double? fallback = NvidiaHotspot.Hotspot([35.0, 38.5, 36.0, 37.0, null, 34.0]);
        double? fromCh5 = NvidiaHotspot.Hotspot([35.9, 34.0, 35.0, 37.4, 37.4, 35.3]);
        Check("Hotspot is channel #5; hottest of #1-#4 if #5 is invalid", fromCh5 == 37.4 && fallback == 38.5,
              $"logged read -> {fromCh5} °C (HWMonitor's #1 would say 35.9); #5 invalid -> {fallback} °C from sensor #2");
        Check("Hotspot window: 1 s average and peak, empties when reads stop", windowOk && stale is null,
              $"40/46 °C alternating -> average {sum.AverageC:F1}, peak {sum.MaxC:F1}, {sum.Samples} samples; 4 s later: {(stale is null ? "empty" : "not empty")}");
    }

    /// <summary>
    /// A rough GPU thermal model for closed-loop tests: board power heats the die, airflow from
    /// the fans cools it. Calibrated so 300 W needs ~40% fan for an 80 °C hotspot and 30% fans let
    /// it reach ~90 °C, close to an RTX 5080 Astral. Readings get the hotspot's ±1.5 °C jitter.
    /// </summary>
    private sealed record PlantRun(List<double> HotspotC, List<int> FanPercent, List<FanStatus> Statuses, IReadOnlyList<double> Airflow);

    /// <param name="activity">Load/foreground readings for activity detection each second (null: none).</param>
    /// <param name="memoryChips">Memory-chip temperature given (second, hotspot, fan %), or null.</param>
    /// <param name="cooling">Scales the fans' cooling: below 1 is a card/case needing more airflow than expected.</param>
    /// <param name="airflow">A learned airflow table to start from (from an earlier run).</param>
    private static PlantRun RunPlant(FanOptions o, Func<int, double> watts, int seconds, double startC = 50, int driverSpeed = 30,
                                     bool hasHotspot = true, bool refuse = false, Func<int, ActivitySample>? activity = null,
                                     Func<int, double, int, double?>? memoryChips = null, double cooling = 1,
                                     IReadOnlyList<double>? airflow = null, double peakSpread = 1,
                                     double spreadPerWatt = 0)
    {
        const double ambient = 30, heatCapacity = 150, powerLimit = 400;
        var fake = new FakeFans { DriverSpeed = driverSpeed, Refuse = refuse ? "needs administrator rights" : null };
        var rng = new Random(42);
        var run = new PlantRun([], [], [], []);
        double hot = startC;
        using var fc = fake.Make(o, range: (30, 100));
        fc.Airflow.Load(airflow);
        for (int t = 0; t < seconds; t++)
        {
            double w = watts(t);
            int fan = fake.Speed ?? driverSpeed;
            double conductance = 2 + 10 * cooling * fan / 100.0; // W per °C above ambient
            hot += (w - conductance * (hot - ambient)) / heatCapacity;
            // spreadPerWatt: the hotspot sits above the die by an amount that follows power at once (an RTX 5080
            // measured ~25 °C at 320 W), so a load dip drops the reading within a second, unlike the slow die.
            double noisy = hot + spreadPerWatt * w + (rng.NextDouble() - 0.5) * 3;
            fake.Now += TimeSpan.FromSeconds(1);
            double peak = noisy + rng.NextDouble() * peakSpread; // the 1 s peak runs above the average by 0..spread °C
            run.Statuses.Add(fc.Tick(new FanTemps(hot - 7, hasHotspot ? noisy : null, null, hasHotspot ? peak : null, w / powerLimit,
                                                  MemoryChipMaxC: memoryChips?.Invoke(t, hot, fan), Activity: activity?.Invoke(t))));
            run.HotspotC.Add(hot);
            run.FanPercent.Add(fake.Speed ?? driverSpeed);
        }
        return run with { Airflow = fc.Airflow.Corrections.ToArray() };
    }

    /// <summary>The hail mary: power cut only when full fans can't hold a failsafe, and given back once cool.</summary>
    private static void PowerEmergencyCheck()
    {
        var e = new PowerEmergency();
        var acts = new List<PowerEmergency.Action>();
        TimeSpan T(int s) => TimeSpan.FromSeconds(s);
        for (int s = 0; s < 9; s++) acts.Add(e.Update(true, true, T(s)));           // 9 s: not yet
        acts.Add(e.Update(false, true, T(9)));                                        // a cooler second resets it
        for (int s = 10; s < 20; s++) acts.Add(e.Update(true, false, T(s)));          // hot, but fans not at max: never
        bool quietSoFar = acts.All(a => a == PowerEmergency.Action.None);
        PowerEmergency.Action cut = PowerEmergency.Action.None;
        for (int s = 20; s <= 30; s++) cut = e.Update(true, true, T(s)) is var a && a != PowerEmergency.Action.None ? a : cut;
        var stillHot = e.Update(true, true, T(60));                                   // no repeat while active
        var early = e.Update(false, true, T(61));
        var restore = PowerEmergency.Action.None;
        for (int s = 62; s <= 125; s++) restore = e.Update(false, false, T(s)) is var a && a != PowerEmergency.Action.None ? a : restore;
        // A cut must still end after a switch to Driver mode with the safety net off, where the tick returns early.
        var fans = new FakeFans();
        bool released = false;
        using (var fc = fans.Make(new FanOptions { Mode = FanMode.Fixed, FixedPercent = 100 }, range: (30, 100)))
        {
            var pe = new PowerEmergency();
            for (int s = 0; s <= 12; s++) { fans.Now += T(1); var st = fc.Tick(new FanTemps(95)); pe.Update(fc.FailsafeActive, st.Percent >= fc.Range.Max, T(s)); }
            bool cutOn = pe.Active;
            fc.Options = new FanOptions { Mode = FanMode.Driver, AutoAssist = false };
            for (int s = 13; s <= 80; s++) { fans.Now += T(1); var st = fc.Tick(new FanTemps(50)); released |= pe.Update(fc.FailsafeActive, st.Percent >= fc.Range.Max, T(s)) == PowerEmergency.Action.Restore; }
            Check("Hail mary: a cut still ends after switching to Driver mode (safety net off)", cutOn && released, $"cut {cutOn}, restored {released}");
        }
        Check("Hail mary: power cut after 10 s of failsafe at full fans, restored after 60 s cool",
              quietSoFar && cut == PowerEmergency.Action.Cut && stillHot == PowerEmergency.Action.None && early == PowerEmergency.Action.None
              && restore == PowerEmergency.Action.Restore && !e.Active,
              $"before: {(quietSoFar ? "nothing" : "fired early")}, at 30 s: {cut}, after cooling: {restore}");
    }

    /// <summary>Failure handling found in the 2026-10-20 sweep.</summary>
    private static void HardeningCheck()
    {
        var smart = new FanOptions { Mode = FanMode.Smart, AdaptToActivity = false };

        // A power fraction of NaN (0 W limit) used to stick in the look-ahead and integral for good.
        var fake = new FakeFans();
        using (var fc = fake.Make(smart, range: (30, 100)))
        {
            for (int t = 0; t < 30; t++) { fake.Now += TimeSpan.FromSeconds(1); fc.Tick(new FanTemps(60, 75, 50, 76, double.NaN)); }
            int hot = fake.Speed ?? 0;
            bool finite = fc.Airflow.Corrections.All(double.IsFinite);
            Check("Auto: an unreadable power limit (NaN) counts as moderate load, nothing gets poisoned", hot > 30 && finite,
                  $"fans {hot}% at hotspot 75 °C (target 70), learned table finite: {finite}");
        }

        var model = new AirflowModel();
        model.Load([.. Enumerable.Repeat(double.NaN, AirflowModel.Buckets)]);
        Check("A damaged airflow.json (NaN) loads as zeros", model.Corrections.All(c => c == 0) && double.IsFinite(model.Estimate(0.5)),
              $"estimate at 50% power {model.Estimate(0.5):F1}%");
        var learnedMid = new AirflowModel();
        learnedMid.Learn(0.8, 15); // only gaming power learned
        Check("The airflow estimate never falls as load rises", learnedMid.Estimate(1.1) >= learnedMid.Estimate(0.8),
              $"{learnedMid.Estimate(0.8):F1}% at 80% power, {learnedMid.Estimate(1.1):F1}% at 110%");

        // One failed write (a driver reset) used to stop fan control for the rest of the session.
        var flaky = new FakeFans { Refuse = "GPU is lost" };
        using (var fc = flaky.Make(new FanOptions { Mode = FanMode.Fixed, FixedPercent = 60 }))
        {
            var st1 = flaky.Tick(fc, 50);
            flaky.Refuse = null;
            for (int t = 0; t < 10; t++) flaky.Tick(fc, 50);
            bool stillOff = flaky.Speed is null;
            for (int t = 0; t < 25; t++) flaky.Tick(fc, 50);
            Check($"A failed write is retried after {FanController.BlockedRetry.TotalSeconds:F0} s", st1.Percent is null && stillOff && flaky.Speed == 60,
                  $"after the failure: {(stillOff ? "driver" : "manual")} at 10 s, {flaky.Speed?.ToString() ?? "driver"}% at 36 s");
        }
        var denied = new FakeFans { Refuse = "needs administrator rights" };
        using (var fc = denied.Make(new FanOptions { Mode = FanMode.Fixed, FixedPercent = 60 }))
        {
            for (int t = 0; t < 40; t++) denied.Tick(fc, 50);
            Check("A permission error isn't retried (it won't fix itself)", denied.Writes == 1, $"{denied.Writes} write attempt(s) in 40 s");
        }

        // A tick that throws in the app hands the fans back.
        var held = new FakeFans();
        using (var fc = held.Make(new FanOptions { Mode = FanMode.Fixed, FixedPercent = 70 }))
        {
            for (int t = 0; t < 5; t++) held.Tick(fc, 50); // ramps up from the driver's speed
            int before = held.Speed ?? 0;
            fc.ReleaseToDriver();
            Check("ReleaseToDriver gives a held speed back to the driver", before == 70 && held.Speed is null && held.Restores == 1,
                  $"{before}% -> {(held.Speed is null ? "driver" : held.Speed + "%")}");
        }
    }

    private static int SpeedChanges(IEnumerable<int> fan) => fan.Zip(fan.Skip(1)).Count(x => x.First != x.Second);

    private static void SmartCheck()
    {
        // The controller's own behaviour at an 80 °C operating point (the defaults are lower now; the ceiling is off here).
        var smart = new FanOptions { Mode = FanMode.Smart, SmartTargetHotspotC = 80, SmartTargetCoreC = 72, CeilingC = 0 };

        var steady = RunPlant(smart, _ => 300, 400);
        var tail = steady.HotspotC.Skip(240).ToList();
        double meanErr = tail.Average(h => Math.Abs(h - 80)), maxErr = tail.Max(h => Math.Abs(h - 80));
        Check("Auto (smart): steady 300 W, from 50 °C", meanErr <= 1.5 && maxErr <= 3,
              $"last 160 s: hotspot within {maxErr:F1} °C of 80 (mean {meanErr:F1}), fans {steady.FanPercent[^1]}%, " +
              $"{SpeedChanges(steady.FanPercent.Skip(240))} speed changes; {steady.Statuses[^1].Text}");

        // Holding the target at 250 W, then a heavy scene: the power term should move the fans before the heat arrives.
        var step = RunPlant(smart, t => t < 300 ? 250 : 400, 620, startC: 80);
        double peak = step.HotspotC.Skip(300).Max();
        double settled = step.HotspotC.Skip(500).Max(h => Math.Abs(h - 80));
        int fanAtStep = step.FanPercent[299], fanAfter = step.FanPercent[305];
        Check("Auto (smart): load step 250 -> 400 W while holding 80 °C", peak <= 85 && settled <= 3 && fanAfter >= fanAtStep + 10,
              $"fans {fanAtStep}% -> {fanAfter}% within 5 s of the step (power feed-forward), peak {peak:F1} °C, " +
              $"within {settled:F1} °C of 80 two minutes later");

        var drop = RunPlant(smart, t => t < 200 ? 400 : 150, 480, startC: 80);
        double dip = drop.HotspotC.Skip(200).Min();
        Check("Auto (smart): load drop 400 -> 150 W", drop.HotspotC.Skip(380).All(h => h <= 82) && dip >= 60,
              $"fans {drop.FanPercent[199]}% -> {drop.FanPercent[^1]}%, lowest {dip:F1} °C, no overshoot back above target");

        var light = RunPlant(smart, _ => 60, 300);
        Check("Auto (smart): light load stays at the driver's minimum", light.FanPercent.Skip(120).All(f => f == 30) && light.HotspotC[^1] < 80,
              $"fans {light.FanPercent[^1]}%, hotspot {light.HotspotC[^1]:F1} °C (below target, fans can't go under 30% manually)");

        var idle = RunPlant(smart with { SmartIdleStop = true }, _ => 40, 300, startC: 40);
        Check("Auto (smart) with 0 RPM: hands the fans to the driver at idle", idle.Statuses[^1].Percent is null && idle.Statuses[^1].Text.Contains("0 RPM"),
              idle.Statuses[^1].Text);

        // 0 RPM at 40 °C with a driver that really stops the fans: the card warms with no airflow, Auto comes back
        // at 45 °C, cools it, and lets go again. It should cycle slowly, not chatter.
        var zero = smart with { SmartIdleStop = true, SmartIdleStopBelowC = 40 };
        var cycle = RunPlant(zero, _ => 40, 1200, startC: 38, driverSpeed: 0);
        int stops = cycle.FanPercent.Zip(cycle.FanPercent.Skip(1)).Count(x => x.First > 0 && x.Second == 0);
        double hottest = cycle.HotspotC.Max();
        Check("Auto 0 RPM below 40 °C: fans stop, return near 45 °C, and cycle slowly", stops is >= 1 and <= 10 && hottest < 48
              && cycle.FanPercent.Count(f => f == 0) > 300,
              $"{stops} stops in 20 min, {cycle.FanPercent.Count(f => f == 0)} s at 0 RPM, hottest {hottest:F1} °C");

        var desk = RunPlant(zero, _ => 40, 300, startC: 36, driverSpeed: 0,
                            activity: _ => new ActivitySample(22, 10, 0, 0, 0.10, false, "explorer"));
        Check("Auto 0 RPM also on the desktop (the card idles at ~16% power)",
              desk.Statuses[30].Activity == ActivityKind.Desktop && desk.FanPercent.Take(40).Any(f => f == 0), desk.Statuses[30].Text);
        var busy = RunPlant(zero, t => t < 100 ? 40 : 140, 200, startC: 36, driverSpeed: 0);
        Check("Auto 0 RPM ends when the load passes 30% power", busy.FanPercent.Skip(110).All(f => f >= 30), $"fans {busy.FanPercent[^1]}% at 140 W");

        var core = RunPlant(smart, _ => 300, 400, hasHotspot: false);
        double coreErr = core.HotspotC.Skip(240).Select(h => h - 7).Max(c => Math.Abs(c - 72));
        Check("Auto (smart) without a hotspot holds the core at 72 °C", coreErr <= 3 && core.Statuses[^1].Text.Contains("core"),
              $"core within {coreErr:F1} °C of 72; {core.Statuses[^1].Text}");

        var quiet = RunPlant(smart with { SmartPriority = SmartPriority.Quiet }, t => 250 + 60 * Math.Sin(t / 20.0), 500);
        var tight = RunPlant(smart with { SmartPriority = SmartPriority.Tight }, t => 250 + 60 * Math.Sin(t / 20.0), 500);
        double Rms(PlantRun r) => Math.Sqrt(r.HotspotC.Skip(200).Average(h => (h - 80) * (h - 80)));
        Check("Auto (smart): Tight holds closer, Quiet changes speed less (varying load)",
              Rms(tight) < Rms(quiet) && SpeedChanges(quiet.FanPercent.Skip(200)) < SpeedChanges(tight.FanPercent.Skip(200)),
              $"RMS error Quiet {Rms(quiet):F1} / Tight {Rms(tight):F1} °C; speed changes Quiet {SpeedChanges(quiet.FanPercent.Skip(200))} / Tight {SpeedChanges(tight.FanPercent.Skip(200))}");

        var bumpless = RunPlant(smart, _ => 300, 3, startC: 80, driverSpeed: 55);
        Check("Auto (smart) takes over from wherever the driver had the fans", Math.Abs(bumpless.FanPercent[0] - 55) <= 10,
              $"driver at 55% -> first write {bumpless.FanPercent[0]}%");

        var hot = RunPlant(smart, _ => 300, 3, startC: 104);
        bool failsafeHit = hot.Statuses.Any(s => s.Text.StartsWith("Full speed")) && hot.FanPercent.Contains(100);
        Check("Auto (smart): hotspot failsafe still goes straight to 100%", failsafeHit, hot.Statuses.First(s => s.Text.StartsWith("Full") || true).Text);

        var denied = RunPlant(smart, _ => 300, 3, refuse: true);
        Check("Auto (smart) without administrator rights", denied.Statuses[^1].NeedsAdmin && denied.FanPercent.All(f => f == 30), denied.Statuses[^1].Text);

        // From a diagnostic log: taking over at idle (43 °C below target, driver at 46%) left the integral
        // pinned at +60, which would have run the fans near full speed once a game started.
        var cold = RunPlant(smart, t => t < 90 ? 30 : 330, 480, startC: 37, driverSpeed: 46);
        int reach = cold.HotspotC.FindIndex(90, h => h >= 78);
        int fanAtReach = reach >= 0 ? cold.FanPercent[reach] : -1;
        double undershoot = reach >= 0 ? cold.HotspotC.Skip(reach).Min() : double.NaN;
        double coldSettled = cold.HotspotC.Skip(380).Max(h => Math.Abs(h - 80));
        Check("Auto (smart): takes over at idle, then a game starts (no wound-up integral)",
              reach >= 0 && fanAtReach <= 75 && undershoot >= 74 && coldSettled <= 3,
              $"fans {fanAtReach}% when the hotspot first reached 78 °C (wound up would be ~90%), lowest after {undershoot:F1} °C, " +
              $"within {coldSettled:F1} °C of 80 at the end");

        // From the same log: ASUS's fan service set 0% every few seconds; never three overrides in a row.
        var fighter = new FakeFans();
        using (var fc = fighter.Make(new FanOptions { Mode = FanMode.Fixed, FixedPercent = 40, RampUpPerSecond = 0, RampDownPerSecond = 0 }))
        {
            FanStatus st = fc.Status;
            for (int i = 0; i < 12; i++)
            {
                fighter.ForeignTarget = i % 2 == 1 ? 0 : null; // every other tick another program sets 0%
                st = fighter.Tick(fc, 50);
            }
            Check("Another program changing the fans every other tick is reported", st.Text.Contains("another program"), st.Text);
        }

        Check("New installs default to Auto (smart) under an 80 °C ceiling",
              new FanOptions().Mode == FanMode.Smart && new FanOptions().Validate() is null && new FanOptions().CeilingC == 80
              && FanOptions.DefaultActivities.Values.All(p => p.TargetHotspotC < 80),
              $"default mode {new FanOptions().Mode}");
    }

    private static void AdaptiveCheck()
    {
        // ---- activity detection ----
        ActivityKind Feed(IEnumerable<ActivitySample> samples)
        {
            var c = new ActivityClassifier();
            var last = ActivityKind.Unknown;
            foreach (var s in samples) last = c.Update(s);
            return last;
        }
        IEnumerable<ActivitySample> Repeat(int n, Func<int, ActivitySample> f) => Enumerable.Range(0, n).Select(f);
        ActivitySample S(double gpu, double power, double dec = 0, double enc = 0, bool fs = false, string? app = null, double mem = 10) =>
            new(gpu, mem, dec, enc, power, fs, app);

        var cases = new (string Name, IEnumerable<ActivitySample> Stream, ActivityKind Expect)[]
        {
            ("idle desktop", Repeat(20, _ => S(1, 0.06)), ActivityKind.Idle),
            ("browsing", Repeat(20, i => S(12 + i % 3, 0.12)), ActivityKind.Desktop),
            ("video in a browser (decoder busy)", Repeat(20, _ => S(14, 0.14, dec: 22, app: "chrome")), ActivityKind.Video),
            ("fullscreen game", Repeat(20, i => S(72 + i % 9, 0.6 + (i % 4) * 0.06, fs: true, app: "Cyberpunk2077")), ActivityKind.Gaming),
            ("fullscreen Chrome at 25% load, decoder idle (a real log): not a game", Repeat(20, _ => S(25, 0.27, dec: 1, fs: true, app: "chrome")), ActivityKind.Desktop),
            ("browser game: fullscreen Chrome with swinging load still counts", Repeat(20, i => S(60 + i % 9, 0.5 + (i % 4) * 0.06, fs: true, app: "chrome")), ActivityKind.Gaming),
            ("windowed game (power swings frame to frame)", Repeat(20, i => S(65 + i % 15, 0.5 + (i % 3) * 0.08, app: "eldenring")), ActivityKind.Gaming),
            ("Blender render", Repeat(20, _ => S(97, 0.92, app: "blender")), ActivityKind.Compute),
            ("steady 99% job, not fullscreen", Repeat(20, _ => S(99, 0.95, app: "someworker")), ActivityKind.Compute),
            ("OBS encoding", Repeat(20, _ => S(30, 0.3, enc: 45, app: "explorer")), ActivityKind.Compute),
        };
        foreach (var (name, stream, expect) in cases)
        {
            var got = Feed(stream);
            Check($"Activity: {name}", got == expect, $"detected {got}");
        }

        // A loading screen in the middle of a game must not drop to Desktop; a real exit does, after 15 s.
        var cls = new ActivityClassifier();
        foreach (var s in Repeat(20, i => S(75 + i % 9, 0.62 + (i % 4) * 0.06, fs: true, app: "game"))) cls.Update(s);
        foreach (var s in Repeat(10, _ => S(8, 0.1, fs: true, app: "game"))) cls.Update(s);
        var afterLoading = cls.Current;
        foreach (var s in Repeat(25, _ => S(6, 0.08, app: "explorer"))) cls.Update(s);
        Check("Activity: a 10 s loading screen keeps Gaming; leaving the game drops it after a while",
              afterLoading == ActivityKind.Gaming && cls.Current is ActivityKind.Desktop or ActivityKind.Idle,
              $"after loading screen: {afterLoading}; after 25 s on the desktop: {cls.Current}");

        // ---- strategy per activity ----
        var auto = new FanOptions { Mode = FanMode.Smart };
        double gTarget = FanOptions.DefaultActivities[ActivityKind.Gaming].TargetHotspotC;
        double cTarget = FanOptions.DefaultActivities[ActivityKind.Compute].TargetHotspotC;
        ActivitySample Game(int t) => S(80 + t % 7, 0.85 + (t % 3) * 0.04, fs: true, app: "game");
        var gaming = RunPlant(auto, _ => 300, 500, activity: Game);
        double gamingErr = gaming.HotspotC.Skip(350).Max(h => Math.Abs(h - gTarget));
        Check($"Auto while gaming holds the Gaming target ({gTarget:F0} °C)", gamingErr <= 2.5 && gaming.Statuses[^1].Activity == ActivityKind.Gaming,
              $"within {gamingErr:F1} °C of {gTarget:F0}; {gaming.Statuses[^1].Text}");

        // A game at ~320 W with short dips to ~210 W (menus, lighter scenes), as in a real log where Auto then
        // swung the fans 100% → 52% → 100%. The hotspot follows the power at once (die spread), so each dip reads
        // as "well below target". The fans should ride through dips shorter than the hold.
        var dipping = auto with { Activities = new Dictionary<ActivityKind, ActivityProfile>(FanOptions.DefaultActivities)
            { [ActivityKind.Gaming] = new(72, SmartPriority.Balanced, 5) } }; // reachable, so the fans work mid-range
        double DipWatts(int t) => t >= 200 && t % 40 is >= 30 and < 36 ? 210 : 320;
        var dips = RunPlant(dipping, DipWatts, 600, startC: 45, activity: Game, spreadPerWatt: 0.075, cooling: 1.6);
        var steadyPart = dips.FanPercent.Skip(240).ToList();
        int dipLow = steadyPart.Min(), dipHigh = steadyPart.Max();
        Check("Auto in a game with short load dips keeps the fans steady (no 100 → 50 % swings)", dipHigh - dipLow <= 22,
              $"fans {dipLow}-{dipHigh}% after warm-up, {SpeedChanges(steadyPart)} changes in {steadyPart.Count} s");
        var dipsAvg = RunPlant(dipping with { SmartInput = SmartInput.Average, Activities = new Dictionary<ActivityKind, ActivityProfile>(FanOptions.DefaultActivities)
            { [ActivityKind.Gaming] = new(57, SmartPriority.Balanced, 5) } }, DipWatts, 600, startC: 45, activity: Game, spreadPerWatt: 0.075, cooling: 1.6);
        var avgPart = dipsAvg.FanPercent.Skip(240).ToList();
        Check("...and the same holding the average of core and hotspot", avgPart.Max() - avgPart.Min() <= 22 && SpeedChanges(avgPart) <= SpeedChanges(steadyPart),
              $"fans {avgPart.Min()}-{avgPart.Max()}%, {SpeedChanges(avgPart)} changes (hotspot: {SpeedChanges(steadyPart)})");

        // Holding the average, a cool core can't hide a hot hotspot: the ceiling still watches the hotspot itself.
        var hidden = new FakeFans();
        using (var fc = hidden.Make(auto with { SmartInput = SmartInput.Average, AdaptToActivity = false, SmartTargetHotspotC = 70 }, range: (30, 100)))
        {
            FanStatus st = default!;
            for (int t = 0; t < 15; t++) { hidden.Now += TimeSpan.FromSeconds(1); st = fc.Tick(new FanTemps(50, 82, 40, 83, 0.8)); }
            Check("Averaging: a hotspot over the ceiling still drives the fans (average 66 °C, under target)",
                  hidden.Speed == 100 && st.Steering == "hotspot ceiling", $"{hidden.Speed}%, steering {st.Steering}: {st.Text}");
        }
        // The deep kind from the same log: a 5 s loading screen at ~50 W in the middle of a ~310 W game.
        double SceneWatts(int t) => t >= 200 && t % 60 is >= 50 and < 55 ? 50 : 310;
        var scenes = RunPlant(dipping, SceneWatts, 600, startC: 45, activity: Game, spreadPerWatt: 0.075, cooling: 1.6);
        int worstDip = Enumerable.Range(240, 340).Where(t => t % 60 == 49).Max(t => scenes.FanPercent[t] - scenes.FanPercent.Skip(t).Take(12).Min());
        Check("...and a 5 s loading screen (50 W) barely moves them", worstDip <= 10, $"largest drop during a loading screen: {worstDip}%");

        var quit = RunPlant(dipping, t => t < 300 ? 320 : 40, 420, startC: 45, activity: t => t < 300 ? Game(t) : S(3, 0.1),
                            spreadPerWatt: 0.075, cooling: 1.6);
        int fanAtQuit = quit.FanPercent[299];
        int slowedAt = Enumerable.Range(300, 120).FirstOrDefault(t => quit.FanPercent[t] < fanAtQuit - 5, -1);
        Check("...but still slows down soon after the game ends", slowedAt is > 300 and <= 330,
              $"fans {fanAtQuit}% at the end of the game, slowing from {slowedAt - 300} s after, {quit.FanPercent[^1]}% at 2 min");

        // Activity switches (logs, 2026-10-21): at a game's start the first seconds still used the Desktop target, the
        // fans jumped, then dipped 97% -> 80% when the higher Gaming target arrived; after quitting, the Idle target
        // made them spike 42% -> 87% while the card cooled.
        // The user's own Auto: holding the core/hotspot average, a big jump between Desktop and Gaming targets.
        var mine = auto with { SmartInput = SmartInput.Average, Activities = new Dictionary<ActivityKind, ActivityProfile>(FanOptions.DefaultActivities)
        {
            [ActivityKind.Idle] = new(38, SmartPriority.Tight, 0), [ActivityKind.Desktop] = new(43, SmartPriority.Tight, 3),
            [ActivityKind.Gaming] = new(57, SmartPriority.Balanced, 5),
        } };
        var session = RunPlant(mine, t => t is >= 100 and < 400 ? 320 : 40, 520, startC: 40,
                               activity: t => t is >= 100 and < 400 ? Game(t) : S(12, 0.1), spreadPerWatt: 0.075, cooling: 1.6);
        int startPeak = session.FanPercent.Skip(100).Take(15).Max();
        int startDip = startPeak - session.FanPercent.Skip(100 + session.FanPercent.Skip(100).Take(15).ToList().IndexOf(startPeak)).Take(30).Min();
        int atQuit = session.FanPercent[405], afterQuitPeak = session.FanPercent.Skip(406).Take(60).Max();
        Check("Auto: no dip as a game starts, no spike after it ends (activity switches move the target gently)",
              startDip <= 10 && afterQuitPeak <= atQuit + 5,
              $"start: peak {startPeak}%, then down {startDip}%; end: {atQuit}% 5 s after, highest {afterQuitPeak}% in the next minute");

        var render = RunPlant(auto, _ => 320, 500, activity: _ => S(97, 0.95, app: "blender"));
        double renderErr = render.HotspotC.Skip(350).Max(h => Math.Abs(h - cTarget));
        Check($"Auto during a render holds the Compute target ({cTarget:F0} °C)", renderErr <= 2.5 && render.Statuses[^1].Activity == ActivityKind.Compute,
              $"within {renderErr:F1} °C of {cTarget:F0}; {render.Statuses[^1].Text}");

        // The ceiling: a heavy load whose swings would otherwise carry the hotspot over it.
        var tight = auto with { CeilingC = 75 };
        var heavy = RunPlant(tight, t => 360 + 50 * Math.Sin(t / 9.0), 600, startC: 60, activity: Game);
        double heavyPeak = heavy.HotspotC.Skip(150).Max();
        Check("Auto keeps the hotspot under a 75 °C ceiling through load spikes", heavyPeak < 75,
              $"peak {heavyPeak:F1} °C, fans up to {heavy.FanPercent.Skip(150).Max()}%");
        var tooMuch = RunPlant(tight, _ => 560, 400, startC: 70, activity: Game, peakSpread: 6);
        int flips = tooMuch.Statuses.Skip(100).Zip(tooMuch.Statuses.Skip(101)).Count(x => x.First.Severity != x.Second.Severity);
        Check("Auto says so (steadily) when even full fans can't hold the ceiling",
              tooMuch.Statuses[^1].Text.Contains("ceiling") && tooMuch.Statuses[^1].Severity == AlertSeverity.Warning && flips == 0,
              $"{tooMuch.HotspotC[^1]:F1} °C at {tooMuch.FanPercent[^1]}%, warning flickered {flips} times: {tooMuch.Statuses[^1].Text}");

        // From the game log (2026-10-20): Gaming near both its target and the ceiling, with the 1 s peak jumping
        // several degrees. Steering the ceiling on that peak made the fans hunt 67-96%.
        var near = RunPlant(auto with { CeilingC = 75 }, _ => 310, 500, startC: 68, activity: Game, peakSpread: 6);
        var held = near.FanPercent.Skip(300).ToList();
        Check("Auto doesn't hunt near the ceiling when the hotspot peak is jumpy",
              held.Max() - held.Min() <= 15 && near.HotspotC.Skip(300).Max() < 75,
              $"fans {held.Min()}-{held.Max()}% over the last 200 s (game log before the fix: 67-96%), hotspot peak {near.HotspotC.Skip(300).Max():F1} °C");

        // ---- every sensor votes: hot memory chips take over while the hotspot looks fine ----
        double? HotChips(int t, double hotC, int fan) => 70 + t * 0.12 - (fan - 30) * 0.25; // VRAM heating faster than the airflow keeps up
        var withChips = RunPlant(auto, _ => 160, 240, startC: 55, activity: Game, memoryChips: HotChips);
        var withoutChips = RunPlant(auto, _ => 160, 240, startC: 55, activity: Game);
        bool chipsSteer = withChips.Statuses.Skip(120).Any(s => s.Steering == "memory chips");
        Check("Auto: memory chips near their limit speed the fans up even with a cool hotspot",
              chipsSteer && withChips.FanPercent[^1] > withoutChips.FanPercent[^1] + 5,
              $"fans {withChips.FanPercent[^1]}% with the chip reading vs {withoutChips.FanPercent[^1]}% without; " +
              $"steering: {withChips.Statuses[^1].Steering}; {withChips.Statuses[^1].Text}");

        // ---- look-ahead: a power ramp starts the fans before the heat arrives ----
        // Holding the Gaming target at 220 W, then a heavy scene arriving within 4 s (to 340 W).
        Func<int, double> ramp = t => t < 300 ? 220 : Math.Min(340, 220 + (t - 300) * 30);
        var lead = RunPlant(auto, ramp, 420, startC: gTarget, activity: Game);
        var noLead = RunPlant(auto with { Activities = new Dictionary<ActivityKind, ActivityProfile>(FanOptions.DefaultActivities)
                               { [ActivityKind.Gaming] = FanOptions.DefaultActivities[ActivityKind.Gaming] with { LeadSeconds = 0 } } },
                           ramp, 420, startC: gTarget, activity: Game); // same tuning, only the look-ahead off
        Check("Auto: 5 s look-ahead on a rising load moves the fans earlier (peak no worse)",
              lead.FanPercent[305] > noLead.FanPercent[305] && lead.HotspotC.Skip(300).Max() <= noLead.HotspotC.Skip(300).Max() + 0.1,
              $"fans 5 s into the ramp: {lead.FanPercent[305]}% vs {noLead.FanPercent[305]}% without look-ahead; " +
              $"peak {lead.HotspotC.Skip(300).Max():F1} vs {noLead.HotspotC.Skip(300).Max():F1} °C");

        // ---- learning: a second session at the same load settles faster ----
        // A card whose airflow differs from the built-in estimate. Learn at 250 W, then compare a fresh
        // controller and a learned one when the load jumps from 120 W to 250 W.
        var first = RunPlant(auto, _ => 250, 900, startC: 55, activity: Game, cooling: 0.8);
        Func<int, double> jump = t => t < 200 ? 120 : 250;
        var fresh = RunPlant(auto, jump, 400, startC: 50, activity: Game, cooling: 0.8);
        var learned = RunPlant(auto, jump, 400, startC: 50, activity: Game, cooling: 0.8, airflow: first.Airflow);
        double Worst(PlantRun r) => r.HotspotC.Skip(200).Max(h => Math.Abs(h - gTarget));
        Check("Auto learns this card's airflow: a load jump at a learned power level is handled better",
              first.Airflow.Any(c => Math.Abs(c) >= 1) && Worst(learned) <= Worst(fresh) + 0.05,
              $"learned corrections {string.Join(" ", first.Airflow.Select(c => c.ToString("+0;-0;0")))}; " +
              $"worst miss after the jump: {Worst(fresh):F1} °C fresh, {Worst(learned):F1} °C with what it learned");

        // ---- without load readings Auto behaves exactly as before ----
        var plain = RunPlant(auto, _ => 300, 400);
        Check($"Auto without activity readings keeps the general {auto.SmartTargetHotspotC:F0} °C target",
              plain.HotspotC.Skip(240).Max(h => Math.Abs(h - auto.SmartTargetHotspotC)) <= 3 &&
              plain.Statuses[^1].Activity == ActivityKind.Unknown, plain.Statuses[^1].Text);
    }

    /// <summary>The Linux hotspot reader against a fake sysfs GPU folder: a resource0 file with known registers.</summary>
    private static void LinuxHotspotCheck()
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.WriteLine("  SKIP: Linux hotspot reader (Linux only)");
            return;
        }
        string dir = Path.Combine(Path.GetTempPath(), $"retweak-bar0-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "vendor"), "0x10de\n");
            double[] temps = [41.5, 44.25, 39.0, 47.75, 47.75, 45.0];
            using (var f = new FileStream(Path.Combine(dir, "resource0"), FileMode.CreateNew))
            {
                f.SetLength(0xAD1000); // sparse; the registers sit at 0xAD0A90 like on the card
                f.Position = 0xAD0A90;
                using var w = new BinaryWriter(f);
                foreach (double t in temps) w.Write((1u << 30) | (uint)(t * 256));
            }
            using (var reader = NvidiaHotspot.TryCreateLinux(dir, out var state, out string why))
            {
                var got = reader?.Read();
                bool same = got is { Length: 6 } && got.Zip(temps).All(p => p.First == p.Second);
                Check("Linux hotspot: reads the six registers from resource0", same && state == HotspotAvailability.Available,
                      got is null ? why : string.Join(", ", got));
                Check("Linux hotspot: no memory chips without PawnIO", reader?.ReadMemoryChips() is null, "");
            }

            File.WriteAllText(Path.Combine(dir, "vendor"), "0x1002\n");
            bool refused = NvidiaHotspot.TryCreateLinux(dir, out var other, out _) is null && other == HotspotAvailability.Unsupported;
            bool missing = NvidiaHotspot.TryCreateLinux(Path.Combine(dir, "nope"), out var gone, out _) is null && gone == HotspotAvailability.Unsupported;
            Check("Linux hotspot: refuses a non-NVIDIA device and a missing one", refused && missing, $"{other}, {gone}");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>The hardening helpers against a real junction (mklink /J works without administrator rights).</summary>
    private static void TrustedPathsCheck()
    {
        if (!OperatingSystem.IsWindows())
        {
            bool underLinux = TrustedPaths.IsUnder("/home/u/.local/share/ReTweak/x", "/home/u/.local/share/ReTweak")
                              && !TrustedPaths.IsUnder("/home/u/.local/share/ReTweak-other", "/home/u/.local/share/ReTweak")
                              && !TrustedPaths.IsUnder("/home/u/.local/share/retweak/x", "/home/u/.local/share/ReTweak"); // case counts
            Check("Trusted paths: 'inside folder' check uses Linux separators and case", underLinux, $"{underLinux}");
            Console.WriteLine("  SKIP: junction and system32 checks are Windows-only");
            return;
        }
        string root = Path.Combine(Path.GetTempPath(), $"retweak-paths-{Guid.NewGuid():N}");
        string real = Path.Combine(root, "real"), target = Path.Combine(root, "elsewhere"), link = Path.Combine(root, "link");
        Directory.CreateDirectory(real);
        Directory.CreateDirectory(target);
        try
        {
            using (var mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(TrustedPaths.SystemExe("cmd.exe"), $"/c mklink /J \"{link}\" \"{target}\"")
                   { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!) mk.WaitForExit();
            bool haveLink = Directory.Exists(link);

            bool plainOk = TrustedPaths.IsPlainPath(Path.Combine(real, "not-yet", "file.json"));
            bool linkCaught = haveLink && !TrustedPaths.IsPlainPath(Path.Combine(link, "file.json"));
            Check("Trusted paths: plain folders pass, a junction anywhere in the path is caught", plainOk && linkCaught,
                  haveLink ? $"plain: {plainOk}, through junction: {(linkCaught ? "refused" : "allowed")}" : "couldn't create a junction to test with");

            TrustedPaths.WriteFileSafely(Path.Combine(real, "settings.json"), "{}");
            bool wrote = File.ReadAllText(Path.Combine(real, "settings.json")) == "{}" && Directory.GetFiles(real, "*.tmp").Length == 0;
            bool refused;
            try { TrustedPaths.WriteFileSafely(Path.Combine(link, "settings.json"), "{}"); refused = false; }
            catch (IOException) { refused = true; }
            Check("Trusted paths: safe write replaces atomically and won't write through a junction",
                  wrote && refused && !File.Exists(Path.Combine(target, "settings.json")),
                  $"plain write ok: {wrote}; through junction: {(refused ? "refused" : "WRITTEN")}");

            bool under = TrustedPaths.IsUnder(Path.Combine(root, "real", "x"), root) && !TrustedPaths.IsUnder(root + "-other", root);
            Check("Trusted paths: 'inside folder' check isn't fooled by a shared prefix", under, $"{under}");

            string sysExe = TrustedPaths.SystemExe("schtasks.exe"), explorer = TrustedPaths.SystemExe("explorer.exe");
            Check("Trusted paths: system programs by full path", Path.IsPathRooted(sysExe) && File.Exists(sysExe) && File.Exists(explorer),
                  $"{sysExe}, {explorer}");
        }
        finally
        {
            try { if (Directory.Exists(link)) Directory.Delete(link); } catch (IOException) { } // removes the junction, not its target
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static ConnectorReading Pins(Func<int, double> v, Func<int, double> a) =>
        new(Enumerable.Range(1, 6).Select(p => new PinReading(p, Math.Round(v(p), 3), Math.Round(a(p), 2), true)).ToList());
}
