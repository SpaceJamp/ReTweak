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
     C:\Program Files\ReTweak and starts it. If Start with Windows is on, the installed copy moves the
     logon task to itself.

  Run it again to update. Settings and learned data stay in %LocalAppData%\ReTweak.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\install.ps1
#>
param(
    [string]$Source,        # internal: the elevated half gets the published folder
    [switch]$Elevated
)
$ErrorActionPreference = 'Stop'
$target = Join-Path $env:ProgramFiles 'ReTweak'

if (-not $Elevated) {
    $repo = Split-Path -Parent $PSScriptRoot
    $publish = Join-Path $repo 'artifacts\publish'
    Write-Host "Building ReTweak (Release, self-contained)..."
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    & dotnet publish (Join-Path $repo 'src\ReTweak.App') -c Release -r win-x64 --self-contained true -p:Platform=x64 -o $publish -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }
    if (-not (Test-Path (Join-Path $publish 'ReTweak.exe'))) { throw "The build has no ReTweak.exe." }
    Write-Host "Installing to $target (Windows will ask for administrator rights)..."
    $p = Start-Process powershell -Verb RunAs -Wait -PassThru -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Elevated', '-Source', "`"$publish`"")
    if ($p.ExitCode -ne 0) { throw "Install failed (exit code $($p.ExitCode)); see the messages above." }
    Write-Host "Installed. ReTweak is running from $target."
    exit 0
}

# ---- elevated half ----
try {
    if (-not (Test-Path (Join-Path $Source 'ReTweak.exe'))) { throw "No build at $Source." }

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

    # Program Files inherits an admin-only ACL; a folder someone pre-created with other rights is refused.
    if (Test-Path $target) {
        $item = Get-Item $target -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "$target is a link; remove it and try again." }
    } else { New-Item -ItemType Directory -Path $target | Out-Null }
    $acl = Get-Acl $target
    # Any right that changes the folder or what's in it: write/append data, attributes, delete, delete children,
    # change permissions, take ownership, and GENERIC_WRITE / GENERIC_ALL (inherited entries use those).
    $changeMask = 0x2 -bor 0x4 -bor 0x10 -bor 0x40 -bor 0x100 -bor 0x10000 -bor 0x40000 -bor 0x80000 -bor 0x40000000 -bor 0x10000000
    $writers = $acl.Access | Where-Object {
        $_.AccessControlType -eq 'Allow' -and (([int64][int]$_.FileSystemRights) -band $changeMask) -and
        $_.IdentityReference.Value -notmatch '^(NT AUTHORITY\\SYSTEM|BUILTIN\\Administrators|NT SERVICE\\TrustedInstaller|CREATOR OWNER|APPLICATION PACKAGE AUTHORITY\\.*)$' }
    if ($writers) { throw "$target can be changed by $(($writers.IdentityReference | Select-Object -Unique) -join ', '); fix its permissions or delete it, then run again." }

    Write-Host "Copying..."
    & "$env:SystemRoot\System32\robocopy.exe" $Source $target /MIR /NJH /NJS /NP /NFL /NDL | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copy failed (robocopy $LASTEXITCODE)." }

    Write-Host "Starting ReTweak from $target..."
    Start-Process (Join-Path $target 'ReTweak.exe') -ArgumentList '--minimized'
    exit 0
}
catch {
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    Read-Host "Press Enter to close"
    exit 1
}
