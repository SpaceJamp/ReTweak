using System.Text.Json;
using ReTweak.Core;

namespace ReTweak.App;

/// <summary>
/// Keeps what Auto learned about each card's airflow (<see cref="AirflowModel"/>) between sessions, in
/// %LocalAppData%\ReTweak\airflow.json keyed by GPU name. Losing it only means relearning.
/// </summary>
internal static class AirflowStore
{
    private static string FilePath => Path.Combine(AppSettings.Folder, "airflow.json");

    public static IReadOnlyList<double>? Load(string gpu)
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var all = JsonSerializer.Deserialize<Dictionary<string, double[]>>(File.ReadAllText(FilePath));
            return all is not null && all.TryGetValue(gpu, out var c) ? c : null;
        }
        catch (Exception ex)
        {
            Diag.Warn(DiagCategory.Fan, "couldn't read learned airflow; starting fresh", new { ex.Message });
            return null;
        }
    }

    public static void Save(string gpu, IReadOnlyList<double> corrections)
    {
        try
        {
            Dictionary<string, double[]> all = [];
            if (File.Exists(FilePath))
                all = JsonSerializer.Deserialize<Dictionary<string, double[]>>(File.ReadAllText(FilePath)) ?? [];
            all[gpu] = [.. corrections.Select(c => Math.Round(c, 3))];
            TrustedPaths.WriteFileSafely(FilePath, JsonSerializer.Serialize(all));
            Diag.Info(DiagCategory.Fan, "saved learned airflow", new { gpu, corrections = all[gpu] });
        }
        catch (Exception ex) { Diag.Warn(DiagCategory.Fan, "couldn't save learned airflow", new { ex.Message }); }
    }
}
