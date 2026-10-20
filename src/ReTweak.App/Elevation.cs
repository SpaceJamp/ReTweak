using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace ReTweak.App;

/// <summary>Fan control needs administrator rights; monitoring doesn't, so ReTweak only asks when needed.</summary>
public static class Elevation
{
    /// <summary>Passed to the elevated copy so it waits for this one to exit instead of deferring to it.</summary>
    public const string RestartArg = "--elevated-restart";

    public static bool IsAdmin { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>Starts an elevated copy (UAC prompt). False if the user declined.</summary>
    public static bool StartElevatedCopy()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, RestartArg) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception) { return false; } // UAC prompt cancelled
    }
}
