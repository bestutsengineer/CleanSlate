<#
.SYNOPSIS
    Builds bin\CleanSlate.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
    No Visual Studio or .NET SDK needed.
#>
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path $fw)) { $fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
$csc = Join-Path $fw 'csc.exe'
if (-not (Test-Path $csc)) { throw ".NET Framework 4.x compiler not found at $csc" }

$assets = Join-Path $root 'assets'
$icon = Join-Path $assets 'CleanSlate.ico'
$iconOn = Join-Path $assets 'CleanSlate-on.ico'
if (-not ((Test-Path $icon) -and (Test-Path $iconOn))) { & (Join-Path $root 'tools\make-icon.ps1') -OutDir $assets }

$out = Join-Path $root 'bin'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$exe = Join-Path $out 'CleanSlate.exe'
$sources = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }

$cscArgs = @(
    '/nologo', '/noconfig', '/target:winexe', '/optimize+', '/platform:anycpu',
    "/out:$exe",
    "/win32icon:$icon",
    "/win32manifest:$(Join-Path $root 'src\app.manifest')",
    "/resource:$icon,CleanSlate.ico",
    "/resource:$iconOn,CleanSlate-on.ico",
    "/r:$fw\System.dll",
    "/r:$fw\System.Core.dll",
    "/r:$fw\System.Drawing.dll",
    "/r:$fw\System.Windows.Forms.dll"
) + $sources

# The in-box compiler prints a banner about only supporting C# 5; that is expected.
$output = & $csc @cscArgs 2>&1
$output | Where-Object { $_ -notmatch 'only supports language versions up to C# 5|fwlink/\?LinkID=533240|^\s*$|This compiler is provided as part' } | ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -ne 0) { throw "Build failed (csc exit code $LASTEXITCODE)" }
Write-Host "Built $exe"
