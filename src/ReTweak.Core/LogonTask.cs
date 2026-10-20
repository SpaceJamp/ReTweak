using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace ReTweak.Core;

/// <summary>
/// A per-user Task Scheduler task that starts a program at logon with highest privileges,
/// so it starts elevated without a UAC prompt. Registered through schtasks.exe: creating
/// (and usually deleting) a highest-privileges task needs administrator rights once, so an
/// unelevated caller can pass <c>elevate: true</c> to run schtasks.exe through a UAC prompt.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class LogonTask(string name)
{
    public string Name { get; } = name;

    public static bool IsElevated { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>The registered program and arguments, or null if the task doesn't exist.</summary>
    public (string Command, string Arguments)? Query()
    {
        var (code, output) = Run(["/Query", "/TN", Name, "/XML"]);
        if (code != 0) return null;
        var cmd = CommandRegex().Match(output);
        var args = ArgumentsRegex().Match(output);
        return (Unescape(cmd.Groups[1].Value), args.Success ? Unescape(args.Groups[1].Value) : "");
    }

    /// <summary>Creates or replaces the task. Returns false if it couldn't (UAC declined, access denied).</summary>
    public bool Register(string exePath, string arguments, bool elevate, out string? error)
    {
        // The XML sits in the user's temp folder while an elevated schtasks reads it. Create it new under an
        // unpredictable name and keep it open without write/delete sharing until schtasks is done, so nothing
        // can swap its contents; then check that what got registered really is this program.
        string xml = Path.Combine(Path.GetTempPath(), $"{Name}-task-{Guid.NewGuid():N}.xml");
        try
        {
            using var hold = new FileStream(xml, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Xml(exePath, arguments))).ToArray(); // UTF-16 with a BOM
            hold.Write(bytes);
            hold.Flush(flushToDisk: true);
            if (!Schtasks(["/Create", "/TN", Name, "/XML", xml, "/F"], elevate, out error)) return false;
            if (Query() is not { } t || !string.Equals(t.Command, exePath, StringComparison.OrdinalIgnoreCase) || t.Arguments != arguments)
            {
                Remove(elevate, out _); // not what we asked for: don't leave it to run at highest privileges
                error = "The registered task didn't match ReTweak, so it was removed.";
                return false;
            }
            return true;
        }
        finally { try { File.Delete(xml); } catch (IOException) { } }
    }

    /// <summary>Deletes the task; true if it's gone afterwards (including if it never existed).</summary>
    public bool Remove(bool elevate, out string? error)
    {
        error = null;
        if (Query() is null) return true;
        if (Schtasks(["/Delete", "/TN", Name, "/F"], elevate: false, out error)) return true;
        return elevate && Schtasks(["/Delete", "/TN", Name, "/F"], elevate: true, out error);
    }

    private static bool Schtasks(string[] args, bool elevate, out string? error)
    {
        error = null;
        if (!elevate || IsElevated)
        {
            var (code, output) = Run(args);
            if (code != 0) error = output.Trim();
            return code == 0;
        }
        try
        {
            // Output can't be redirected through a UAC prompt; the exit code is enough.
            using var p = Process.Start(new ProcessStartInfo(TrustedPaths.SystemExe("schtasks.exe"), Quote(args))
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            })!;
            p.WaitForExit();
            if (p.ExitCode != 0) error = $"schtasks.exe exited with code {p.ExitCode}.";
            return p.ExitCode == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: UAC prompt declined
        {
            error = "The administrator prompt was cancelled.";
            return false;
        }
    }

    private static (int Code, string Output) Run(string[] args)
    {
        var psi = new ProcessStartInfo(TrustedPaths.SystemExe("schtasks.exe")) // full path, never a PATH lookup
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        string stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout + stderr.Result);
    }

    private static string Quote(IEnumerable<string> args) =>
        string.Join(' ', args.Select(a => a.Length > 0 && !a.Any(c => c is ' ' or '"') ? a : $"\"{a}\""));

    private static string Unescape(string s) =>
        s.Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");

    /// <summary>
    /// Logon trigger and principal for the current user only; highest privileges; runs on battery;
    /// no time limit; normal priority (the Task Scheduler default of 7 would start it below normal).
    /// </summary>
    private string Xml(string exePath, string arguments)
    {
        string sid = WindowsIdentity.GetCurrent().User!.Value;
        string e(string s) => SecurityElement.Escape(s);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts ReTweak in the tray at logon, with administrator rights (GPU hotspot, fan control).</Description>
                <URI>\{e(Name)}</URI>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{sid}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{sid}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{e(exePath)}</Command>
                  <Arguments>{e(arguments)}</Arguments>
                  <WorkingDirectory>{e(Path.GetDirectoryName(exePath) ?? "")}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    [GeneratedRegex(@"<Command>(.*?)</Command>", RegexOptions.Singleline)]
    private static partial Regex CommandRegex();
    [GeneratedRegex(@"<Arguments>(.*?)</Arguments>", RegexOptions.Singleline)]
    private static partial Regex ArgumentsRegex();
}
