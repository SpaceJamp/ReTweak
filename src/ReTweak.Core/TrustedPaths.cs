using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ReTweak.Core;

/// <summary>
/// Hardening for an elevated ReTweak working next to files the unelevated user controls
/// (%LocalAppData%, the Desktop, a log folder from settings.json):
/// <list type="bullet">
/// <item>never follow a junction or symlink the user may have planted (<see cref="IsPlainPath"/>);</item>
/// <item>keep files an elevated process writes, deletes or later trusts in a folder only administrators
/// can change (<see cref="EnsureAdminOnlyFolder"/>);</item>
/// <item>start Windows programs by full path without ShellExecute, so user-writable registry keys
/// (HKCU\Software\Classes, App Paths) can't redirect them (<see cref="SystemExe"/>, <see cref="OpenInExplorer"/>).</item>
/// </list>
/// </summary>
public static class TrustedPaths
{
    public static bool IsElevated { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>%SystemRoot%\System32\<paramref name="name"/> (or %SystemRoot%\ for explorer.exe), never a PATH lookup.</summary>
    public static string SystemExe(string name)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(windows, name)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name);
    }

    /// <summary>
    /// True if no existing component of <paramref name="path"/> (the path itself or any parent) is a
    /// junction, symlink or other reparse point. Parts that don't exist yet are fine; they will be
    /// created as plain directories. Checked again right before each sensitive operation.
    /// </summary>
    public static bool IsPlainPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            for (var d = new DirectoryInfo(full); d is not null; d = d.Parent)
            {
                if (d.Parent is null) break; // the drive root
                if ((d.Exists || File.Exists(d.FullName)) && (File.GetAttributes(d.FullName) & FileAttributes.ReparsePoint) != 0)
                    return false;
            }
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>True if <paramref name="path"/> is inside <paramref name="root"/> (after normalising both).</summary>
    public static bool IsUnder(string path, string root)
    {
        string p = Path.GetFullPath(path).TrimEnd('\\') + "\\", r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Elevated: may this folder from user-controlled settings be written to? Only plain folders inside the
    /// user's profile, so a planted setting can't make the elevated app create files in system folders.
    /// Unelevated: anything (the app has the user's rights only).
    /// </summary>
    public static bool IsAllowedUserFolder(string folder) =>
        !IsElevated || (IsUnder(folder, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) && IsPlainPath(folder));

    /// <summary>
    /// Creates (or repairs) <paramref name="folder"/> under %ProgramData% so that only SYSTEM and
    /// Administrators can change it (users can read). Requires elevation. Refuses if the folder or a
    /// parent is a reparse point, and replaces whatever owner and ACL a user may have pre-created it with.
    /// Returns false if it can't guarantee that.
    /// </summary>
    public static bool EnsureAdminOnlyFolder(string folder)
    {
        if (!IsElevated) return false;
        try
        {
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!IsUnder(folder, programData)) return false;
            // Secure each level below ProgramData, top down, before trusting what's inside it.
            var levels = new List<DirectoryInfo>();
            for (var d = new DirectoryInfo(Path.GetFullPath(folder)); d is not null && !string.Equals(d.FullName.TrimEnd('\\'), programData.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); d = d.Parent)
                levels.Insert(0, d);
            foreach (var dir in levels)
            {
                if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                if (!dir.Exists) dir.Create();
                var acl = new DirectorySecurity();
                var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                var users = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
                acl.SetOwner(admins);
                acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                const InheritanceFlags all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                acl.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
                acl.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
                acl.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, all, PropagationFlags.None, AccessControlType.Allow));
                dir.SetAccessControl(acl);
                if ((new DirectoryInfo(dir.FullName).Attributes & FileAttributes.ReparsePoint) != 0) return false; // swapped while we worked
            }
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Replaces <paramref name="path"/> atomically: writes a uniquely named new temp file (CreateNew, so a
    /// planted file or link with that name is refused, never followed), then moves it over the target
    /// (a move replaces a link itself rather than writing through it). Refuses if the folder is, or sits
    /// inside, a junction or symlink.
    /// </summary>
    public static void WriteFileSafely(string path, string contents)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (!IsPlainPath(folder)) throw new IOException($"{folder} is (or is inside) a junction or symlink; not writing there.");
        Directory.CreateDirectory(folder);
        string tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        using (var f = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var w = new StreamWriter(f)) w.Write(contents);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Opens a folder in Explorer, or with <paramref name="select"/> shows a file selected. Starts
    /// %SystemRoot%\explorer.exe directly (no ShellExecute, so no registry lookup of handlers); Explorer
    /// hands the window to the user's running, unelevated shell.
    /// </summary>
    public static void OpenInExplorer(string path, bool select = false)
    {
        var psi = new ProcessStartInfo(SystemExe("explorer.exe")) { UseShellExecute = false };
        psi.Arguments = select ? $"/select,\"{path}\"" : $"\"{path}\"";
        Process.Start(psi)?.Dispose();
    }

    /// <summary>Opens an https link the same way, through Explorer rather than ShellExecute in this process.</summary>
    public static void OpenLink(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("Only https links.", nameof(uri));
        var psi = new ProcessStartInfo(SystemExe("explorer.exe")) { UseShellExecute = false };
        psi.ArgumentList.Add(uri.AbsoluteUri);
        Process.Start(psi)?.Dispose();
    }
}
