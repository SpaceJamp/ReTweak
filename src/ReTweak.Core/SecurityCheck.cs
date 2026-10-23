using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32;

namespace ReTweak.Core;

public enum CheckStatus { Pass, Info, Warn, Fail }

/// <param name="Advice">What to do about it; empty when nothing's needed.</param>
public sealed record SecurityFinding(string Area, string Title, CheckStatus Status, string Detail, string Advice = "");

/// <summary>
/// A self-audit of the things that matter when a tool runs elevated and loads a kernel driver:
/// ReTweak's own files and autostart, the PawnIO driver and module, ReTweak's data folders,
/// known UAC-bypass registry entries in the user's account, other risky hardware drivers, and the
/// Windows protections around all of it. Read-only: it reports and advises, it never changes anything.
/// </summary>
public static partial class SecurityCheck
{
    /// <summary>SHA-256 of the signed PawnIO NVIDIA module bundled in ReTweak.Core (release 0.2.11).</summary>
    public const string ExpectedModuleSha256 = "2a51262b7215bf3ea82c0f507bc4af50243141f82542dfe611a885c71ce2629a";

    /// <summary>Hardware-access drivers abused by malware or exploitable in versions still in circulation.</summary>
    private static readonly (string Service, string What)[] RiskyDrivers =
    [
        ("WinRing0_1_2_0", "WinRing0 (old hardware monitors, e.g. older Fan Control / OpenHardwareMonitor): lets any program read and write hardware; on Microsoft's vulnerable driver blocklist"),
        ("WinRing0x64", "WinRing0 (x64 build): lets any program read and write hardware; on Microsoft's vulnerable driver blocklist"),
        ("RTCore64", "RTCore64 (MSI Afterburner / RivaTuner): versions before 4.6.3 let any program read and write kernel memory"),
        ("AsIO2", "AsIO2 (ASUS utilities): older versions exposed raw hardware access to any program"),
        ("AsIO3", "AsIO3 (ASUS utilities): older versions exposed raw hardware access to any program"),
        // The services' real names (Armoury Crate installs AsIO3.sys as "Asusgio3"); the file names above never matched.
        ("Asusgio2", "AsIO2 (ASUS utilities, e.g. Armoury Crate): older versions exposed raw hardware access to any program"),
        ("Asusgio3", "AsIO3 (ASUS utilities, e.g. Armoury Crate): older versions exposed raw hardware access to any program"),
        ("IOMap", "IOMap (ASUS GPU Tweak): older versions exposed raw hardware access to any program"),
    ];

    private static readonly string[] FanTools = ["GPU Tweak III", "ASUSGPUFanService", "MSIAfterburner", "FanControl"];

    public static IReadOnlyList<SecurityFinding> Run(string? exePath = null, string? dataFolder = null, string? diagnosticsFolder = null,
                                                     string? logFolder = null)
    {
        var f = new List<SecurityFinding>();
        exePath ??= Environment.ProcessPath;
        void Add(Func<SecurityFinding> check)
        {
            try { f.Add(check()); }
            catch (Exception ex) { f.Add(new("Check", "A check couldn't run", CheckStatus.Info, $"{ex.GetType().Name}: {ex.Message}")); }
        }

        // ---- ReTweak itself ----
        f.Add(new("ReTweak", $"Version {BuildInfo.Display}", CheckStatus.Info, BuildInfo.Dirty ? "Built with uncommitted changes." : ""));
        Add(() => TrustedPaths.IsElevated
            ? new("ReTweak", "Running as administrator", CheckStatus.Info, "Needed for the hotspot (PawnIO) and fan control. The checks below are what keeps that safe.")
            : new("ReTweak", "Running without administrator rights", CheckStatus.Info, "Monitoring works; the hotspot and fan control need ReTweak to run as administrator."));
        if (exePath is not null)
        {
            string dir = Path.GetDirectoryName(exePath)!;
            Add(() => NonAdminCanWrite(dir) is { } who
                ? new("ReTweak", "Program folder can be changed without administrator rights", AutostartElevated(exePath) ? CheckStatus.Warn : CheckStatus.Info,
                      $"{who} can modify {dir}. Anything running as you could replace ReTweak's files" +
                      (AutostartElevated(exePath) ? ", and the elevated logon task would then run them as administrator at your next logon." : "."),
                      "Install (publish) ReTweak to a folder only administrators can change, e.g. under C:\\Program Files, and point Start with Windows there.")
                : new("ReTweak", "Program folder is protected", CheckStatus.Pass, $"Only administrators can change {dir}."));
            Add(() => VerifySignature(exePath) is { } sig && sig == 0
                ? new("ReTweak", "ReTweak is signed", CheckStatus.Pass, $"{Path.GetFileName(exePath)} has a valid digital signature.")
                : new("ReTweak", "ReTweak isn't signed", CheckStatus.Info, "Builds from source aren't code-signed; that's expected for a development build."));
        }
        Add(() =>
        {
            using var s = typeof(SecurityCheck).Assembly.GetManifestResourceStream("ReTweak.Core.PawnIo.Nvidia.bin");
            if (s is null) return new("PawnIO", "Bundled NVIDIA module missing", CheckStatus.Fail, "The build doesn't contain PawnIo/Nvidia.bin.");
            string hash = Convert.ToHexStringLower(SHA256.HashData(s));
            return hash == ExpectedModuleSha256
                ? new("PawnIO", "Bundled NVIDIA module is the official signed release", CheckStatus.Pass, $"SHA-256 {hash[..16]}… matches PawnIO.Modules 0.2.11.")
                : new("PawnIO", "Bundled NVIDIA module doesn't match the official release", CheckStatus.Fail,
                      $"SHA-256 {hash} (expected {ExpectedModuleSha256}).", "Rebuild from a clean checkout; PawnIO itself also refuses unsigned modules.");
        });

        // ---- autostart ----
        Add(() =>
        {
            var task = new LogonTask("ReTweak").Query();
            if (task is null) return new("Autostart", "No elevated logon task", CheckStatus.Info, "Start with Windows is off.");
            bool ours = exePath is not null && string.Equals(task.Value.Command, exePath, StringComparison.OrdinalIgnoreCase);
            return ours
                ? new("Autostart", "Logon task runs this ReTweak", CheckStatus.Pass, $"{task.Value.Command} {task.Value.Arguments}, at highest privileges.")
                : new("Autostart", "Logon task runs a different program", CheckStatus.Warn, $"The ReTweak task starts {task.Value.Command} {task.Value.Arguments}.",
                      "If you didn't set that up, turn Start with Windows off and on again in Settings, or delete the task in Task Scheduler.");
        });

        // ---- PawnIO driver ----
        Add(() =>
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var svc = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            if (svc?.GetValue("ImagePath") is not string image)
                return new("PawnIO", "PawnIO isn't installed", CheckStatus.Info, "The hotspot isn't available; nothing else depends on it.");
            string path = DriverPath(image);
            using var un = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
            string version = un?.GetValue("DisplayVersion") as string ?? "unknown version";
            if (!File.Exists(path)) return new("PawnIO", "PawnIO driver file not found", CheckStatus.Warn, $"Service points at {path}.", "Reinstall PawnIO from pawnio.eu.");
            int sig = VerifySignature(path);
            return sig == 0
                ? new("PawnIO", "PawnIO driver is validly signed", CheckStatus.Pass, $"{path} ({version}).")
                : new("PawnIO", "PawnIO driver signature didn't verify", CheckStatus.Fail, $"{path} ({version}), WinVerifyTrust 0x{sig:X8}.",
                      "Reinstall PawnIO from pawnio.eu and don't use ReTweak's hotspot until this passes.");
        });

        // ---- data folders ----
        foreach (var (name, folder) in new[] { ("ReTweak data folder", dataFolder), ("Connector log folder", logFolder) })
        {
            if (folder is null) continue;
            Add(() => TrustedPaths.IsPlainPath(folder)
                ? new("Data", $"{name} is a plain folder", CheckStatus.Pass, folder)
                : new("Data", $"{name} is (or is inside) a junction or symlink", CheckStatus.Warn, folder,
                      "ReTweak refuses to write there while elevated. If you didn't create the link, remove it."));
        }
        if (diagnosticsFolder is not null && Directory.Exists(diagnosticsFolder) && TrustedPaths.IsUnder(diagnosticsFolder,
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)))
            Add(() => NonAdminCanWrite(diagnosticsFolder) is { } who
                ? new("Data", "Diagnostics folder can be changed without administrator rights", CheckStatus.Warn, $"{who} can modify {diagnosticsFolder}.",
                      "Run ReTweak as administrator once with diagnostics on; it repairs the permissions.")
                : new("Data", "Diagnostics folder is admin-only", CheckStatus.Pass, diagnosticsFolder));

        // ---- hijack indicators in the user's own registry ----
        Add(() =>
        {
            string[] keys =
            [
                @"Software\Classes\Folder\shell\open\command", @"Software\Classes\Directory\shell\open\command",
                @"Software\Classes\exefile\shell\open\command", @"Software\Classes\exefile\shell\runas\command",
                @"Software\Classes\ms-settings\shell\open\command", @"Software\Classes\mscfile\shell\open\command",
                @"Software\Microsoft\Windows\CurrentVersion\App Paths\explorer.exe", @"Software\Microsoft\Windows\CurrentVersion\App Paths\schtasks.exe",
                @"Software\Microsoft\Windows\CurrentVersion\App Paths\control.exe",
            ];
            var found = keys.Where(k => { using var key = Registry.CurrentUser.OpenSubKey(k); return key is not null; }).ToList();
            return found.Count == 0
                ? new("Account", "No known UAC-bypass registry entries", CheckStatus.Pass, "None of the per-user handlers malware uses to elevate silently are set.")
                : new("Account", "Per-user entries used by UAC-bypass malware are present", CheckStatus.Warn, string.Join("; ", found.Select(k => "HKCU\\" + k)),
                      "Some software sets these legitimately; if you don't recognise them, scan with Microsoft Defender and remove them.");
        });

        // Environment variables that make every .NET program load extra code at startup. Elevated programs get the
        // user's environment, so user-level code can set these and run inside an elevated ReTweak. A self-contained
        // install ignores DOTNET_ROOT and ReTweak turns startup hooks off, but a profiler loads before any of
        // ReTweak's code runs: it can only be detected.
        Add(() =>
        {
            string[] names =
            [
                "CORECLR_ENABLE_PROFILING", "CORECLR_PROFILER", "CORECLR_PROFILER_PATH", "CORECLR_PROFILER_PATH_64",
                "COR_ENABLE_PROFILING", "COR_PROFILER", "COR_PROFILER_PATH", "DOTNET_STARTUP_HOOKS",
                "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE",
            ];
            using var userEnv = Registry.CurrentUser.OpenSubKey("Environment");
            // The saved user environment (what user-level code can change) for all of them; this process's own for the
            // ones nothing legitimate sets (`dotnet run` sets DOTNET_ROOT for the program it starts).
            bool live(string n) => (n.Contains("PROFIL", StringComparison.Ordinal) || n == "DOTNET_STARTUP_HOOKS")
                                   && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(n));
            var set = names.Where(n => live(n) || userEnv?.GetValue(n) is string { Length: > 0 }).ToList();
            bool profiler = set.Any(n => n.Contains("PROFIL", StringComparison.Ordinal));
            return set.Count == 0
                ? new("Account", "No code-injection environment variables", CheckStatus.Pass, "None of the variables that load extra code into .NET programs are set.")
                : new("Account", "Environment variables that load code into .NET programs are set", profiler ? CheckStatus.Fail : CheckStatus.Warn,
                      string.Join(", ", set) + (profiler ? ". A profiler runs inside every .NET program you start, including ReTweak as administrator." : "."),
                      "Unless a developer tool you use set them on purpose, remove them (System Properties > Environment Variables) and scan with Microsoft Defender.");
        });

        // ---- other hardware drivers and tools ----
        Add(() =>
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            var present = RiskyDrivers.Select(d =>
            {
                using var k = hklm.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{d.Service}");
                if (k is null) return null;
                // Which file and version, and whether it's loaded now: what the user needs to judge it.
                string file = k.GetValue("ImagePath") is string image ? DriverPath(image) : "";
                string version = File.Exists(file) ? FileVersionInfo.GetVersionInfo(file).FileVersion ?? "?" : "file missing";
                bool loaded = DriverRunning(d.Service);
                return $"{d.What} [{Path.GetFileName(file)} {version}, {(loaded ? "loaded now" : "not loaded")}]";
            }).OfType<string>().ToList();
            return present.Count == 0
                ? new("Drivers", "No known risky hardware drivers installed", CheckStatus.Pass, string.Join(", ", RiskyDrivers.Select(d => d.Service)) + " not found.")
                : new("Drivers", "Risky hardware drivers installed", CheckStatus.Warn, string.Join(" · ", present),
                      "While installed, any program with administrator rights can load it, and a loaded one may be usable by others. " +
                      "If you use ReTweak instead of the tool that installed it, uninstall that tool (Settings > Apps > Installed apps); " +
                      "otherwise keep it updated. ReTweak only needs PawnIO.");
        });
        Add(() =>
        {
            var running = Process.GetProcesses().Select(p => p.ProcessName).Where(n => FanTools.Any(t => n.Contains(t, StringComparison.OrdinalIgnoreCase))).Distinct().ToList();
            return running.Count == 0
                ? new("Drivers", "No other fan tools running", CheckStatus.Pass, "")
                : new("Drivers", "Other fan tools are running", CheckStatus.Info, string.Join(", ", running),
                      "They fight ReTweak over the fans (and some load their own drivers). Close them while ReTweak controls the fans.");
        });

        // ---- Windows protections ----
        using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        {
            int? Dword(string key, string value) { using var k = hklm.OpenSubKey(key); return k?.GetValue(value) is int v ? v : null; }
            const string policies = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
            Add(() => Dword(policies, "EnableLUA") == 0
                ? new("Windows", "UAC is off", CheckStatus.Fail, "Every program you run has full administrator rights.", "Turn User Account Control back on.")
                : Dword(policies, "ConsentPromptBehaviorAdmin") == 0
                    ? new("Windows", "UAC elevates without asking", CheckStatus.Warn, "Administrator prompts are set to 'elevate without prompting'.",
                          "Set UAC back to at least the default level.")
                    : new("Windows", "UAC is on and asks before elevating", CheckStatus.Pass, ""));
            Add(() => Dword(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled") == 1
                ? new("Windows", "Memory Integrity is on", CheckStatus.Pass, "Kernel drivers must be compatible and signed; vulnerable ones are harder to abuse.")
                : new("Windows", "Memory Integrity is off", CheckStatus.Info, "Core isolation's Memory Integrity adds protection against malicious or vulnerable drivers.",
                      "Windows Security → Device security → Core isolation. Check your other drivers are compatible first."));
            Add(() => Dword(@"SYSTEM\CurrentControlSet\Control\CI\Config", "VulnerableDriverBlocklistEnable") == 0
                ? new("Windows", "Vulnerable driver blocklist is off", CheckStatus.Warn, "Windows will load drivers Microsoft knows are exploitable.",
                      "Windows Security → Device security → Core isolation → Microsoft Vulnerable Driver Blocklist.")
                : new("Windows", "Vulnerable driver blocklist is on", CheckStatus.Pass, ""));
            Add(() => Dword(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled") == 1
                ? new("Windows", "Secure Boot is on", CheckStatus.Pass, "")
                : new("Windows", "Secure Boot is off or unknown", CheckStatus.Info, "Secure Boot stops tampered boot loaders and drivers from starting.", "Enable it in the UEFI/BIOS setup."));
        }
        return f;
    }

    /// <summary>Plain text, one finding per line, for the CLI and the diagnostics zip.</summary>
    public static string Format(IEnumerable<SecurityFinding> findings) => string.Join(Environment.NewLine, findings.Select(x =>
        $"[{x.Status.ToString().ToUpperInvariant(),-4}] {x.Area}: {x.Title}" +
        (x.Detail.Length > 0 ? $"{Environment.NewLine}       {x.Detail}" : "") +
        (x.Advice.Length > 0 ? $"{Environment.NewLine}       → {x.Advice}" : "")));

    private static bool AutostartElevated(string exePath)
    {
        try { return new LogonTask("ReTweak").Query() is { } t && string.Equals(t.Command, exePath, StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; }
    }

    /// <summary>Who other than administrators/SYSTEM/TrustedInstaller can write to this folder, or null.</summary>
    private static string? NonAdminCanWrite(string folder)
    {
        var acl = new DirectoryInfo(folder).GetAccessControl();
        const FileSystemRights write = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete |
                                       FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | FileSystemRights.WriteAttributes;
        var trusted = new[]
        {
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"), // TrustedInstaller
            new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null),
        };
        foreach (FileSystemAccessRule r in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (r.AccessControlType != AccessControlType.Allow || (r.FileSystemRights & write) == 0) continue;
            if (r.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) continue; // applies to children only
            var sid = (SecurityIdentifier)r.IdentityReference;
            if (trusted.Contains(sid)) continue;
            try { return sid.Translate(typeof(NTAccount)).Value; } catch (Exception) { return sid.Value; }
        }
        // The owner can always rewrite the ACL.
        if (acl.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && !trusted.Contains(owner))
            try { return owner.Translate(typeof(NTAccount)).Value + " (owner)"; } catch (Exception) { return owner.Value + " (owner)"; }
        return null;
    }

    /// <summary>"\SystemRoot\System32\drivers\x.sys" / "system32\drivers\x.sys" / "\??\C:\..." to a real path.</summary>
    /// <summary>True if the driver service is running (loaded). Asks the service manager, which works unelevated.</summary>
    private static bool DriverRunning(string service)
    {
        nint scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == 0) return false;
        try
        {
            nint svc = OpenServiceW(scm, service, ServiceQueryStatus);
            if (svc == 0) return false;
            try { return QueryServiceStatus(svc, out var st) && st.CurrentState == ServiceRunning; }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus { public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint; }
    private const uint ScManagerConnect = 0x1, ServiceQueryStatus = 0x4, ServiceRunning = 4;

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenSCManagerW(string? machine, string? database, uint access);
    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenServiceW(nint scm, string name, uint access);
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatus(nint service, out ServiceStatus status);
    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(nint handle);

    private static string DriverPath(string image)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        image = image.Trim('"');
        if (image.StartsWith(@"\??\", StringComparison.Ordinal)) return image[4..];
        if (image.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) return Path.Combine(windows, image[12..]);
        if (!Path.IsPathRooted(image)) return Path.Combine(windows, image);
        return image;
    }

    // ---- Authenticode via WinVerifyTrust ----

    /// <summary>0 when the file has a valid, trusted Authenticode signature; otherwise the WinVerifyTrust error.</summary>
    public static int VerifySignature(string path)
    {
        var file = new WintrustFileInfo { Size = (uint)Marshal.SizeOf<WintrustFileInfo>(), Path = path };
        nint pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(file, pFile, false);
            var data = new WintrustData
            {
                Size = (uint)Marshal.SizeOf<WintrustData>(), UiChoice = 2 /* none */, RevocationChecks = 0 /* none: works offline */,
                UnionChoice = 1 /* file */, File = pFile, StateAction = 0, ProvFlags = 0x1000 /* cache only URL retrieval */,
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrust(-1, ref action, ref data);
        }
        finally { Marshal.FreeHGlobal(pFile); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WintrustFileInfo
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string Path;
        public nint File;
        public nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint Size;
        public nint PolicyCallbackData, SipClientData;
        public uint UiChoice, RevocationChecks, UnionChoice;
        public nint File;
        public uint StateAction;
        public nint StateData, UrlReference;
        public uint ProvFlags, UiContext;
        public nint SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(nint hwnd, ref Guid action, ref WintrustData data);
}
