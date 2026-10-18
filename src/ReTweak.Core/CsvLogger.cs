using System.Globalization;

namespace ReTweak.Core;

/// <summary>Appends every raw connector frame to a CSV file for later analysis.</summary>
public sealed class CsvLogger : IDisposable
{
    private readonly StreamWriter _w;

    public CsvLogger(string path)
    {
        bool fresh = !File.Exists(path);
        _w = new StreamWriter(path, append: true) { AutoFlush = true };
        if (fresh)
            _w.WriteLine("time," + string.Join(",", Enumerable.Range(1, 6).Select(i => $"v{i},a{i}")) + ",total_a,total_w");
    }

    public void Write(ConnectorFrame f)
    {
        var inv = CultureInfo.InvariantCulture;
        var cells = f.Raw.Pins.SelectMany(p => new[] { p.Volts.ToString("F3", inv), p.Amps.ToString("F3", inv) });
        lock (_w)
            _w.WriteLine($"{f.Time:O},{string.Join(",", cells)},{f.Raw.TotalAmps.ToString("F3", inv)},{f.Raw.TotalWatts.ToString("F1", inv)}");
    }

    public void Dispose() { lock (_w) _w.Dispose(); }
}
