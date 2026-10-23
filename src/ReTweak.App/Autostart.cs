using ReTweak.Core;

namespace ReTweak.App;

public enum AutostartState
{
    Off,
    /// <summary>Logon task registered for this exe.</summary>
    On,
    /// <summary>Logon task points at a different exe (the app was moved or rebuilt elsewhere).</summary>
    Stale,
}

/// <summary>
/// Start with Windows through a per-user logon task with highest privileges, so ReTweak starts
/// elevated (GPU hotspot, fan control) without a UAC prompt at every logon. Creating the task
/// needs administrator rights once.
/// </summary>
public static class Autostart
{
    private const string Arguments = "--minimized";
    private static readonly LogonTask LogonTask = new("ReTweak");

    private static string Exe => Environment.ProcessPath!;
    /// <summary>This exe is under Program Files, where only administrators can change it.</summary>
    public static bool IsInstalledCopy => TrustedPaths.IsUnder(Exe, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));

    public static bool IsAdmin => ReTweak.Core.LogonTask.IsElevated;

    /// <summary>Runs schtasks.exe, so call it once per use rather than per property read.</summary>
    public static AutostartState State
    {
        get
        {
            if (LogonTask.Query() is { } t)
                return string.Equals(t.Command, Exe, StringComparison.OrdinalIgnoreCase) && t.Arguments == Arguments
                    ? AutostartState.On : AutostartState.Stale;
            return AutostartState.Off;
        }
    }

    /// <summary>
    /// Registers (pointing at this exe) or removes the logon task.
    /// When unelevated, <paramref name="elevate"/> lets schtasks.exe ask for administrator rights (UAC).
    /// Blocks until done, including while the UAC prompt is up. Returns null on success, else the reason.
    /// </summary>
    public static string? Set(bool enabled, bool elevate)
    {
        string? error;
        bool ok = enabled ? LogonTask.Register(Exe, Arguments, elevate, out error) : LogonTask.Remove(elevate, out error);
        return ok ? null : error;
    }

    /// <summary>
    /// Elevated, from Program Files, with the logon task still starting a copy elsewhere (the build folder):
    /// move the task here. Any other stale task is left for the Settings dialog to fix when the user saves.
    /// </summary>
    public static void MigrateIfElevated()
    {
        if (!IsAdmin) return;
        try
        {
            // Only toward a protected copy, never away from one, so running a dev build doesn't undo an install.
            if (LogonTask.Query() is { } task && !string.Equals(task.Command, Exe, StringComparison.OrdinalIgnoreCase) && IsInstalledCopy)
            {
                string? why = Set(true, elevate: false);
                Diag.Info(DiagCategory.App, why is null ? "Start with Windows moved to the installed copy" : "couldn't move Start with Windows to the installed copy",
                          new { from = task.Command, to = Exe, why });
            }
        }
        catch (Exception) { /* best effort: the old task keeps working until the next try */ }
    }
}
