using System.Diagnostics;
using ReTweak.Core;

/// <summary>Measures connector read latency, failure rate, quantisation step and noise.</summary>
static class Bench
{
    public static void Run(GpuMonitor monitor, int samples = 300)
    {
        var gpu = monitor.Gpus.FirstOrDefault(g => monitor.HasConnectorSensor(g.Index));
        if (gpu is null) { Console.WriteLine("No connector sensor found."); return; }

        var readings = new List<ConnectorReading>();
        var times = new List<double>();
        int failures = 0;
        var sw = new Stopwatch();
        for (int i = 0; i < samples; i++)
        {
            sw.Restart();
            var r = monitor.ReadConnector(gpu.Index);
            times.Add(sw.Elapsed.TotalMilliseconds);
            if (r is null || r.Pins.Any(p => !p.Valid)) failures++;
            else readings.Add(r);
        }

        if (readings.Count < 2) { Console.WriteLine($"Only {readings.Count} valid reads out of {samples}; can't compute stats."); return; }
        times.Sort();
        Console.WriteLine($"{samples} back-to-back reads on {gpu.Name}");
        Console.WriteLine($"  latency ms: median {times[times.Count / 2]:F2}  p95 {times[(int)(times.Count * 0.95)]:F2}  max {times[^1]:F2}");
        Console.WriteLine($"  failed/invalid reads: {failures}");

        // How often does the chip publish a new frame? Count reads where any value changed.
        int changes = 0;
        for (int i = 1; i < readings.Count; i++)
            if (!readings[i].Pins.SequenceEqual(readings[i - 1].Pins)) changes++;
        double totalMs = times.Sum();
        Console.WriteLine($"  frames changed {changes}/{readings.Count - 1} reads -> chip refresh ~{changes / (totalMs / 1000):F0} Hz " +
                          $"(every ~{totalMs / Math.Max(1, changes):F1} ms)");

        for (int pin = 0; pin < 6; pin++)
        {
            var v = readings.Select(r => r.Pins[pin].Volts).ToList();
            var a = readings.Select(r => r.Pins[pin].Amps).ToList();
            Console.WriteLine($"  pin {pin + 1}: V mean {v.Average():F4} sd {Sd(v) * 1000:F1} mV step {Step(v) * 1000:F1} mV | " +
                              $"A mean {a.Average():F3} sd {Sd(a) * 1000:F1} mA step {Step(a) * 1000:F1} mA");
        }
    }

    private static double Sd(List<double> x)
    {
        double m = x.Average();
        return Math.Sqrt(x.Sum(v => (v - m) * (v - m)) / Math.Max(1, x.Count - 1));
    }

    /// <summary>Smallest non-zero difference between distinct observed values (ADC step estimate).</summary>
    private static double Step(List<double> x)
    {
        var d = x.Select(v => Math.Round(v, 4)).Distinct().Order().ToList();
        return d.Count < 2 ? double.NaN : d.Zip(d.Skip(1), (p, q) => q - p).Min();
    }
}
