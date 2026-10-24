<#
.SYNOPSIS
  Builds ReTweak and installs it to C:\Program Files\ReTweak, where only administrators can change it.

.DESCRIPTION
  ReTweak runs as administrator (PawnIO hotspot, fan control) and can start with Windows at highest
  privileges. Run from the build folder, anything running as you could replace its files and get
  administrator rights at your next logon. Installed here, it can't.

  1. Publishes a self-contained Release build as you (no admin needed), into artifacts\publish. Self-contained
     means ReTweak carries its own .NET runtime, so the DOTNET_ROOT environment variable can't redirect it.
  2. Asks for administrator rights once (UAC), closes a running ReTweak, copies the build to
     C:\Program Files\ReTweak, adds it to the Start menu and starts it. If Start with Windows is on, the
     installed copy moves the logon task to itself.

  Run it again to update. Settings and learned data stay in %LocalAppData%\ReTweak.

  -Uninstall closes ReTweak (the fans go back to the driver) and removes what the installer and the app set up:
  the Program Files folder, the Start menu entry, the Start with Windows task and the notification registration.
  Your settings, learned data and diagnostic logs stay; the script says where.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\install.ps1
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\install.ps1 -Uninstall
#>
param(
    [string]$Source,        # internal: the elevated half gets the published folder
    [switch]$Elevated,
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
$target = Join-Path $env:ProgramFiles 'ReTweak'
$shortcut = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\ReTweak.lnk'

if (-not $Elevated -and $Uninstall) {
    Write-Host "Uninstalling ReTweak (Windows will ask for administrator rights)..."
    $p = Start-Process powershell -Verb RunAs -PassThru -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Elevated', '-Uninstall')
    $p.WaitForExit()
    if ($p.ExitCode -ne 0) { throw "Uninstall failed (exit code $($p.ExitCode)); see the messages above." }
    Write-Host "Uninstalled. Settings and learned data are still in $env:LOCALAPPDATA\ReTweak, logs in $env:ProgramData\ReTweak; delete those folders too if you want them gone."
    exit 0
}

if (-not $Elevated) {
    $repo = Split-Path -Parent $PSScriptRoot
    $publish = Join-Path $repo 'artifacts\publish'
    Write-Host "Building ReTweak (Release, self-contained)..."
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    & dotnet publish (Join-Path $repo 'src\ReTweak.App') -c Release -r win-x64 --self-contained true -p:Platform=x64 -o $publish -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }
    if (-not (Test-Path (Join-Path $publish 'ReTweak.exe'))) { throw "The build has no ReTweak.exe." }
    Write-Host "Installing to $target (Windows will ask for administrator rights)..."
    # Not -Wait: it also waits for every process the elevated half starts, and that starts ReTweak, which keeps running.
    $p = Start-Process powershell -Verb RunAs -PassThru -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Elevated', '-Source', "`"$publish`"")
    $p.WaitForExit()
    if ($p.ExitCode -ne 0) { throw "Install failed (exit code $($p.ExitCode)); see the messages above." }
    Write-Host "Installed. ReTweak is running from $target."
    exit 0
}

# ---- elevated half ----
try {
    if (-not $Uninstall -and -not (Test-Path (Join-Path $Source 'ReTweak.exe'))) { throw "No build at $Source." }

    # Close a running ReTweak the way its tray menu does, so it hands the fans back first.
    Add-Type -Namespace ReTweakInstall -Name Native -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string n);
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
'@
    $tray = [ReTweakInstall.Native]::FindWindow('ReTweak.TrayWindow', [NullString]::Value)
    if ($tray -ne [IntPtr]::Zero) {
        Write-Host "Closing the running ReTweak..."
        [void][ReTweakInstall.Native]::PostMessage($tray, 0x8003, [IntPtr]::Zero, [IntPtr]::Zero)
    }
    for ($i = 0; $i -lt 60 -and (Get-Process ReTweak -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
    if (Get-Process ReTweak -ErrorAction SilentlyContinue) { throw "ReTweak is still running; exit it from the tray icon and run the installer again." }

    if ($Uninstall) {
        $schtasks = "$env:SystemRoot\System32\schtasks.exe"
        & $schtasks /Query /TN ReTweak *> $null
        if ($LASTEXITCODE -eq 0) {
            & $schtasks /Delete /TN ReTweak /F | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Couldn't remove the Start with Windows task (schtasks $LASTEXITCODE)." }
            Write-Host "Removed Start with Windows."
        }
        if (Test-Path $shortcut) { Remove-Item $shortcut; Write-Host "Removed the Start menu entry." }
        if (Test-Path $target) {
            if ((Get-Item $target -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "$target is a link; not deleting through it." }
            Remove-Item $target -Recurse -Force
            Write-Host "Removed $target."
        }
        # The notification registration the installed copy made (its taskbar identity and toast icon). HKCU is the
        # account that ran the script. shortcut: an admin elevating as a different account leaves it, under that user.
        $byExe = 'HKCU:\Software\Classes\AppUserModelId\' + ((Join-Path $target 'ReTweak.exe') -replace '\\', '.')
        if (Test-Path $byExe) {
            $guid = (Get-ItemProperty $byExe).NotificationGUID
            Remove-Item $byExe -Recurse
            if ($guid) { Remove-Item "HKCU:\Software\Classes\AppUserModelId\$guid" -Recurse -ErrorAction SilentlyContinue }
            Write-Host "Removed the notification registration."
        }
        exit 0
    }

    # Program Files inherits an admin-only ACL; a folder someone pre-created with other rights is refused.
    if (Test-Path $target) {
        $item = Get-Item $target -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "$target is a link; remove it and try again." }
    } else { New-Item -ItemType Directory -Path $target | Out-Null }
    $acl = Get-Acl $target
    # Any right that changes the folder or what's in it: write/append data, attributes, delete, delete children,
    # change permissions, take ownership, and GENERIC_WRITE / GENERIC_ALL (inherited entries use those).
    $changeMask = 0x2 -bor 0x4 -bor 0x10 -bor 0x40 -bor 0x100 -bor 0x10000 -bor 0x40000 -bor 0x80000 -bor 0x40000000 -bor 0x10000000
    # By SID, not name: account names are translated on non-English Windows ("VORDEFINIERT\Administratoren").
    # SYSTEM, Administrators, TrustedInstaller, CREATOR OWNER, ALL (RESTRICTED) APPLICATION PACKAGES.
    $trusted = 'S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464', 'S-1-3-0', 'S-1-15-2-1', 'S-1-15-2-2'
    $writers = $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) | Where-Object {
        $_.AccessControlType -eq 'Allow' -and (([int64][int]$_.FileSystemRights) -band $changeMask) -and $trusted -notcontains $_.IdentityReference.Value }
    if ($writers) {
        $who = $writers | ForEach-Object { try { $_.IdentityReference.Translate([Security.Principal.NTAccount]).Value } catch { $_.IdentityReference.Value } } | Select-Object -Unique
        throw "$target can be changed by $($who -join ', '); fix its permissions or delete it, then run again."
    }

    Write-Host "Copying..."
    & "$env:SystemRoot\System32\robocopy.exe" $Source $target /MIR /NJH /NJS /NP /NFL /NDL | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copy failed (robocopy $LASTEXITCODE)." }

    # Start menu, for all users: that folder is admin-only too, so nothing running as you can repoint the shortcut.
    # Opening it while ReTweak runs in the tray brings that window up (single instance).
    $exe = Join-Path $target 'ReTweak.exe'
    $lnk = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
    $lnk.TargetPath = $exe
    $lnk.WorkingDirectory = $target
    $lnk.IconLocation = "$exe,0"
    $lnk.Description = 'GPU monitoring, connector safety and fan control'
    $lnk.Save()

    Write-Host "Starting ReTweak from $target..."
    Start-Process (Join-Path $target 'ReTweak.exe') -ArgumentList '--minimized'
    exit 0
}
catch {
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    Read-Host "Press Enter to close"
    exit 1
}
