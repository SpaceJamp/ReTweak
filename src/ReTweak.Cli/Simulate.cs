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

    private static ConnectorReading Pins(Func<int, double> v, Func<int, double> a) =>
        new(Enumerable.Range(1, 6).Select(p => new PinReading(p, Math.Round(v(p), 3), Math.Round(a(p), 2), true)).ToList());
}
