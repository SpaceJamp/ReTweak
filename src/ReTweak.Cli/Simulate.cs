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
        FanCheck();

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
            fc.Options = curve with { Mode = FanMode.Auto };
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
        var auto = curve with { Mode = FanMode.Auto, AutoAssist = true };
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
            var st = fake.Tick(fc, 60, hotspot: 87);
            Check("Auto safety net: hotspot 27 °C above core", st.Assisting && fake.Speed == 60 && st.Text.Contains("cooler contact"), st.Text);
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
        using (var fc = fake.Make(curve with { Mode = FanMode.Auto, AutoAssist = true }))
        {
            var st = fc.Tick(new FanTemps(75, HotspotC: 88, HotspotMaxC: 96));
            Check("Auto safety net steps in on a 96 °C peak (average 88 °C)", st.Assisting && fake.Speed == 60, st.Text);
        }

        fake = new FakeFans { DriverSpeed = 40 };
        using (var fc = fake.Make(curve with { Mode = FanMode.Auto, AutoAssist = true }))
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

    private static ConnectorReading Pins(Func<int, double> v, Func<int, double> a) =>
        new(Enumerable.Range(1, 6).Select(p => new PinReading(p, Math.Round(v(p), 3), Math.Round(a(p), 2), true)).ToList());
}
