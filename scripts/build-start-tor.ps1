<#
# DeltaTor Core License v1.0 (see LICENSE). Using this Core in another program
# requires the mandatory attribution of https://github.com/Delta-Kronecker/DeltaTor.
.SYNOPSIS
  Compile scripts\start-tor.cs using the .NET
  Framework csc. start-tor.cs -> DeltaTor.exe (windowed exe) + DeltaTorCli.exe.

.PARAMETER OutFile
  Output path for DeltaTor.exe (default: scripts\DeltaTor.exe).

.PARAMETER Version
  Version embedded into the launcher and shown in the menu
  (e.g. "1.1.16" from the release tag). Falls back to the DELTATOR_VERSION
  environment variable, then "dev".
#>
param(
    [string]$OutFile = (Join-Path $PSScriptRoot "DeltaTor.exe"),
    [string]$Version = ""
)

if (-not $Version) { $Version = $env:DELTATOR_VERSION }
if (-not $Version) { $Version = "dev" }
$Version = $Version.TrimStart('v', 'V')
$Version = ($Version -replace '[^0-9A-Za-z._-]', '_')

$candidates = @(
    (Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"),
    (Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe")
)
$csc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { Write-Host "[x] csc.exe ('.NET Framework 4.x') not found."; exit 1 }

$src = Join-Path $PSScriptRoot "start-tor.cs"
$srcUi = Join-Path $PSScriptRoot "DeltaTorUi.cs"
$versionSrc = Join-Path $env:TEMP "deltator-version.g.cs"
$versionCode = @"
namespace StartTor
{
    internal static class DeltaTorVersion
    {
        public const string App = "$Version";
    }
}
"@
Set-Content -Path $versionSrc -Value $versionCode -Encoding UTF8
$icoPath = Join-Path $PSScriptRoot "DeltaTor.ico"
$iconArg = ""
if (Test-Path $icoPath) { $iconArg = "-win32icon:$icoPath" }
$resArg = ""
if (Test-Path $icoPath) { $resArg = "-resource:$icoPath,DeltaTor.ico" }
try {
    & $csc -nologo -optimize+ -target:winexe `
        -r:System.Windows.Forms.dll -r:System.Drawing.dll `
        $iconArg $resArg `
        -out:$OutFile $src $srcUi $versionSrc
    if ($LASTEXITCODE -ne 0) { Write-Host "[x] compile failed ($LASTEXITCODE)"; exit $LASTEXITCODE }
    Write-Host "[ok] built $OutFile (version $Version)"

    # Console companion: the shell waits on this one, so CLI sessions
    # (auto race, hotkeys) keep working even though DeltaTor.exe is windowed.
    $cliDir = Join-Path (Split-Path -Parent $OutFile) "data"
    New-Item -ItemType Directory -Path $cliDir -Force | Out-Null
    $cliOut = Join-Path $cliDir "DeltaTorCli.exe"
    & $csc -nologo -optimize+ -target:exe -define:CONSOLE_BUILD `
        -r:System.Windows.Forms.dll -r:System.Drawing.dll `
        -out:$cliOut $src $srcUi $versionSrc
    if ($LASTEXITCODE -ne 0) { Write-Host "[x] DeltaTorCli compile failed ($LASTEXITCODE)"; exit $LASTEXITCODE }
    Write-Host "[ok] built $cliOut (version $Version)"
} finally {
    Remove-Item $versionSrc -ErrorAction SilentlyContinue
}
