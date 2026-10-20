using System.Globalization;

namespace ReTweak.Core;

/// <summary>Appends every raw connector frame to a CSV file for later analysis. Thread-safe.</summary>
public sealed class CsvLogger : IDisposable
{
    private readonly StreamWriter _w;
    private bool _disposed;

    public string Path { get; }

    public CsvLogger(string path)
    {
        Path = path;
        if (System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) is { Length: > 0 } dir)
        {
            // The folder comes from user-editable settings: an elevated ReTweak only writes inside the user's
            // profile, never through a junction, and never appends through a planted link.
            if (!TrustedPaths.IsAllowedUserFolder(dir))
                throw new UnauthorizedAccessException($"Not logging to {dir}: running as administrator, logs must go to a plain folder in your profile.");
            Directory.CreateDirectory(dir);
        }
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"{path} is a link; not appending to it.");
        bool fresh = !File.Exists(path);
        _w = new StreamWriter(path, append: true) { AutoFlush = true };
        if (fresh)
            _w.WriteLine("time," + string.Join(",", Enumerable.Range(1, 6).Select(i => $"v{i},a{i}")) + ",total_a,total_w");
    }

    /// <summary>A file in <paramref name="folder"/> named for today, e.g. retweak-2026-10-18.csv (appended if it exists).</summary>
    public static CsvLogger Daily(string folder) =>
        new(System.IO.Path.Combine(folder, $"retweak-{DateTime.Now:yyyy-MM-dd}.csv"));

    public void Write(ConnectorFrame f)
    {
        var inv = CultureInfo.InvariantCulture;
        var cells = f.Raw.Pins.SelectMany(p => new[] { p.Volts.ToString("F3", inv), p.Amps.ToString("F3", inv) });
        string line = $"{f.Time:O},{string.Join(",", cells)},{f.Raw.TotalAmps.ToString("F3", inv)},{f.Raw.TotalWatts.ToString("F1", inv)}";
        lock (_w)
        {
            if (!_disposed) _w.WriteLine(line); // may race with Dispose when logging is switched off
        }
    }

    public void Dispose()
    {
        lock (_w)
        {
            if (_disposed) return;
            _disposed = true;
            _w.Dispose();
        }
    }
}
