using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

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
public static partial class TrustedPaths
{
    // Administrator on Windows, root on Linux.
    public static bool IsElevated { get; } = Environment.IsPrivilegedProcess;

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
        // The platform's separator and case rules: Linux paths use '/' and are case-sensitive.
        char sep = Path.DirectorySeparatorChar;
        string p = Path.GetFullPath(path).TrimEnd(sep) + sep, r = Path.GetFullPath(root).TrimEnd(sep) + sep;
        return p.StartsWith(r, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
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
        using var hold = HoldPlainFolder(folder); // nothing can swap the folders between the check and the write
        string tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        using (var f = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var w = new StreamWriter(f)) w.Write(contents);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Elevated: opens every folder from the user's profile down to <paramref name="folder"/> without delete
    /// sharing, and checks each is a real folder, not a junction or symlink. While the returned handle is held
    /// none of them can be renamed, deleted or replaced, so a path checked once stays the same path: this closes
    /// the race where a process running as the user swaps a folder for a junction between the check and the
    /// write. Throws if a folder is a link or can't be held (someone else holds it open for deleting).
    /// Unelevated: nothing to defend against (the app has the user's rights only); holds nothing.
    /// </summary>
    public static IDisposable HoldPlainFolder(string folder)
    {
        var held = new FolderHold();
        // shortcut: the hold uses Windows file handles; on Linux, root writes without it. Add an openat()-based hold if
        // ReTweak ever runs as root against a user-writable home.
        if (!IsElevated || !OperatingSystem.IsWindows()) return held;
        try
        {
            string full = Path.GetFullPath(folder).TrimEnd('\\');
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
            if (!IsUnder(full, profile)) throw new UnauthorizedAccessException($"{folder} isn't in your profile; not writing there as administrator.");
            var chain = new List<string>();
            for (var d = new DirectoryInfo(full); d is not null; d = d.Parent)
            {
                chain.Insert(0, d.FullName.TrimEnd('\\'));
                if (string.Equals(d.FullName.TrimEnd('\\'), profile, StringComparison.OrdinalIgnoreCase)) break;
            }
            foreach (var dir in chain)
            {
                // List + read-attributes access, sharing read/write but not delete. The list access matters: Windows
                // doesn't enforce sharing for an attributes-only open, so that alone wouldn't stop a rename (tested).
                // OPEN_REPARSE_POINT opens a link itself instead of following it, so the attribute check below sees it.
                var h = CreateFileW(@"\\?\" + dir, FileListDirectory | FileReadAttributes, FileShareRead | FileShareWrite, 0, OpenExisting,
                                    FileFlagBackupSemantics | FileFlagOpenReparsePoint, 0);
                if (h.IsInvalid)
                {
                    int err = Marshal.GetLastPInvokeError();
                    h.Dispose();
                    throw new IOException($"Couldn't hold {dir} (error {err}); not writing there as administrator.");
                }
                held.Handles.Add(h);
                if (!GetFileInformationByHandleEx(h, FileAttributeTagInfoClass, out var tag, (uint)Marshal.SizeOf<AttributeTagInfo>()))
                    throw new IOException($"Couldn't check {dir} (error {Marshal.GetLastPInvokeError()}).");
                var attrs = (FileAttributes)tag.FileAttributes;
                if ((attrs & FileAttributes.ReparsePoint) != 0 || (attrs & FileAttributes.Directory) == 0)
                    throw new IOException($"{dir} is a junction, symlink or not a folder; not writing there as administrator.");
            }
            return held;
        }
        catch
        {
            held.Dispose();
            throw;
        }
    }

    private sealed class FolderHold : IDisposable
    {
        public readonly List<SafeFileHandle> Handles = [];
        public void Dispose()
        {
            foreach (var h in Handles) h.Dispose();
            Handles.Clear();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTagInfo { public uint FileAttributes, ReparseTag; }

    private const uint FileListDirectory = 0x1, FileReadAttributes = 0x80, FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000, FileFlagOpenReparsePoint = 0x00200000;
    private const int FileAttributeTagInfoClass = 9;

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out AttributeTagInfo info, uint size);

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
