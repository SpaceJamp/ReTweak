using System.Diagnostics;
using ReTweak.Core;

/// <summary>
/// Checks TrustedPaths' folder holds on this PC: held folders can't be renamed, junctions are refused. Needs
/// administrator rights (unelevated, the hold does nothing by design). Works in %LocalAppData%\ReTweakHoldTest.
/// </summary>
static class HoldTest
{
    public static int Run()
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("--hold-test is Windows-only (the hold uses Windows file handles)."); return 9; }
        if (!TrustedPaths.IsElevated) { Console.WriteLine("Run as administrator: the hold only applies to an elevated process."); return 9; }
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReTweakHoldTest");
        int fails = 0;
        void Check(string what, bool ok) { if (!ok) fails++; Console.WriteLine($"{(ok ? "PASS" : "FAIL")}: {what}"); }
        bool Renames(string from, string to)
        {
            try { Directory.Move(from, to); Directory.Move(to, from); return true; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        }

        if (Directory.Exists(root)) Directory.Delete(root, true);
        string a = Path.Combine(root, "a"), b = Path.Combine(a, "b"), j = Path.Combine(root, "j");
        Directory.CreateDirectory(b);
        try
        {
            using (TrustedPaths.HoldPlainFolder(b))
            {
                Check("a held folder's parent can't be renamed", !Renames(a, a + "2"));
                Check("the held folder itself can't be renamed", !Renames(b, b + "2"));
                File.WriteAllText(Path.Combine(b, "inside.txt"), "x");
                Check("files inside can still be written", File.Exists(Path.Combine(b, "inside.txt")));
            }
            Check("after release, renaming works again", Renames(a, a + "2"));

            // mklink /J needs no admin and no developer mode: the kind of link user-level code can plant.
            string temp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
            using (var mk = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c mklink /J \"{j}\" \"{temp}\"")
                   { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!) mk.WaitForExit();
            bool held;
            try { using (TrustedPaths.HoldPlainFolder(j)) held = true; } catch (IOException) { held = false; }
            Check("a junction can't be held", !held);
            bool wrote;
            try { TrustedPaths.WriteFileSafely(Path.Combine(j, "x.json"), "{}"); wrote = true; } catch (IOException) { wrote = false; }
            Check("WriteFileSafely refuses to write through a junction", !wrote && !File.Exists(Path.Combine(temp, "x.json")));

            string real = Path.Combine(b, "settings.json");
            TrustedPaths.WriteFileSafely(real, "{\"ok\":true}");
            Check("WriteFileSafely writes a normal file", File.ReadAllText(real) == "{\"ok\":true}");

            bool outside;
            try { using (TrustedPaths.HoldPlainFolder(Environment.GetFolderPath(Environment.SpecialFolder.Windows))) outside = true; }
            catch (UnauthorizedAccessException) { outside = false; }
            Check("a folder outside the profile is refused", !outside);
        }
        finally
        {
            if (Directory.Exists(j)) Directory.Delete(j); // removes the junction itself, not its target
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        Console.WriteLine(fails == 0 ? "All hold checks passed." : $"{fails} hold check(s) FAILED.");
        return fails == 0 ? 0 : 1;
    }
}
