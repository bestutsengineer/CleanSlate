<#
.SYNOPSIS
    Installs CleanSlate for the current user (no admin rights needed), or removes it with -Uninstall.

.DESCRIPTION
    - copies CleanSlate.exe to %LOCALAPPDATA%\Programs\CleanSlate
    - adds a Start menu entry (to start it again after "Exit")
    - turns on "Start with Windows" (can be switched off from the tray menu, or install with -NoAutoStart)
    - starts it and shows its icon in the visible part of the notification area instead of the ^ overflow
#>
param(
    [switch]$Uninstall,
    [switch]$NoAutoStart
)
$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\CleanSlate'
$exe = Join-Path $installDir 'CleanSlate.exe'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'CleanSlate.lnk'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

function Stop-CleanSlate {
    # Ask a running copy to remove its gray and quit, then make sure it is gone.
    if (Test-Path $exe) { Start-Process $exe -ArgumentList '--exit' -Wait }
    Start-Sleep -Milliseconds 500
    Get-Process CleanSlate -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $exe } |
        Stop-Process -Force -ErrorAction SilentlyContinue
}

if ($Uninstall) {
    Stop-CleanSlate
    Remove-ItemProperty -Path $runKey -Name 'CleanSlate' -ErrorAction SilentlyContinue
    Remove-Item $shortcut -ErrorAction SilentlyContinue
    Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'CleanSlate removed.'
    Write-Host "Settings ($env:APPDATA\CleanSlate) and log ($env:LOCALAPPDATA\CleanSlate) were left in place; delete them if you like."
    return
}

$built = Join-Path $PSScriptRoot 'bin\CleanSlate.exe'
if (-not (Test-Path $built)) { & (Join-Path $PSScriptRoot 'build.ps1') }

Stop-CleanSlate
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Copy-Item $built $exe -Force

$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $installDir
$link.Description = 'Close every window on the taskbar and gray out the other screens'
$link.IconLocation = "$exe,0"
$link.Save()

if ($NoAutoStart) {
    Remove-ItemProperty -Path $runKey -Name 'CleanSlate' -ErrorAction SilentlyContinue
} else {
    Set-ItemProperty -Path $runKey -Name 'CleanSlate' -Value "`"$exe`""
}

Start-Process $exe

# Windows hides new tray icons behind the ^ arrow. Once Windows has registered the icon,
# mark it as shown on the taskbar (same as Settings > Personalization > Taskbar > Other system tray icons).
$promoted = $false
for ($i = 0; $i -lt 40 -and -not $promoted; $i++) {
    Start-Sleep -Milliseconds 250
    Get-ChildItem 'HKCU:\Control Panel\NotifyIconSettings' -ErrorAction SilentlyContinue | ForEach-Object {
        $path = (Get-ItemProperty $_.PSPath).ExecutablePath
        if ($path -and $path -like '*\Programs\CleanSlate\CleanSlate.exe') {
            Set-ItemProperty -Path $_.PSPath -Name 'IsPromoted' -Value 1 -Type DWord
            $promoted = $true
        }
    }
}

Write-Host "Installed: $exe"
if ($promoted) {
    Write-Host 'CleanSlate is running: its icon is at the right end of the taskbar.'
} else {
    Write-Host 'CleanSlate is running. If its icon is hidden behind the ^ arrow, drag it onto the taskbar.'
}
Write-Host 'Left-click the icon for your chosen action, right-click it for the menu.'
