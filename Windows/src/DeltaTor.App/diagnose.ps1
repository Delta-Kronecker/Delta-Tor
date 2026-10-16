# DeltaTor tunnel DLL diagnostic. Run from the app folder (next to
# DeltaTor.exe) and paste the whole output into the bug report. It loads
# each shipped DLL the way the app does and reports the exact Win32 error,
# which separates "file missing" (126) from "runtime refused to initialize"
# (1114, the 0x8007045A users see) and from antivirus/MotW interference.
# ASCII only on purpose: Windows PowerShell 5.1 reads .ps1 files without a
# BOM as the system ANSI codepage, and any non-ASCII character then breaks
# the parser on non-English systems.
$ErrorActionPreference = 'Continue'
Add-Type -Namespace DeltaTorDiag -Name Loader -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
public static extern IntPtr LoadLibraryExW(string path, System.IntPtr reserved, uint flags);
[System.Runtime.InteropServices.DllImport("kernel32.dll")]
public static extern bool FreeLibrary(System.IntPtr handle);
'@

Write-Host "=== DeltaTor tunnel DLL diagnostic ==="
Write-Host "Date:      $(Get-Date -Format s)"
Write-Host "OS:        $([Environment]::OSVersion.VersionString)"
Write-Host "Arch:      $env:PROCESSOR_ARCHITECTURE"
$isAdmin = (New-Object Security.Principal.WindowsPrincipal(
    [Security.Principal.WindowsIdentity]::GetCurrent()
)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Host "Elevated:  $isAdmin"
Write-Host "Folder:    $PSScriptRoot"
Write-Host ""

foreach ($name in 'msys-2.0.dll', 'hev-socks5-tunnel.dll', 'wintun.dll') {
    $path = Join-Path $PSScriptRoot $name
    if (-not (Test-Path $path)) {
        Write-Host ("{0,-24} MISSING" -f $name)
        Write-Host ""
        continue
    }
    $item = Get-Item $path
    $hash = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host ("{0,-24} {1} bytes, sha256 {2}" -f $name, $item.Length, $hash)
    $zone = Get-Content -Path $path -Stream Zone.Identifier -ErrorAction SilentlyContinue
    if ($zone) {
        Write-Host ("{0,-24} Mark-of-the-Web: YES. Right-click the file, Properties, tick Unblock, Apply." -f $name)
    }
    # hev-socks5-tunnel.dll gets LOAD_WITH_ALTERED_SEARCH_PATH (0x8) so its
    # dependency (msys-2.0.dll) resolves from this folder, exactly like when
    # DeltaTor.exe loads it. The others load by full path, no flags.
    $flags = if ($name -eq 'hev-socks5-tunnel.dll') { 0x8 } else { 0 }
    $handle = [DeltaTorDiag.Loader]::LoadLibraryExW($path, [IntPtr]::Zero, $flags)
    if ($handle -eq [IntPtr]::Zero) {
        $err = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        Write-Host ("{0,-24} LOAD FAILED - Win32 error {1} (126=missing dependency, 1114=init failed)" -f $name, $err)
    }
    else {
        Write-Host ("{0,-24} load OK ({1})" -f $name, $handle)
        [void][DeltaTorDiag.Loader]::FreeLibrary($handle)
    }
    Write-Host ""
}
Write-Host "--- 2. DeltaTor.exe --native-probe (the app's own load path) ---"
$exe = Join-Path $PSScriptRoot 'DeltaTor.exe'
if (Test-Path $exe) {
    $probe = Join-Path $PSScriptRoot 'native-probe.txt'
    if (Test-Path $probe) { Remove-Item $probe -Force }
    try {
        $proc = Start-Process -FilePath $exe -ArgumentList '--native-probe' -Wait -PassThru
        Write-Host "exit code: $($proc.ExitCode)  (0 = all loads OK, 4 = P/Invoke failed)"
        if (Test-Path $probe) {
            Get-Content $probe | ForEach-Object { Write-Host $_ }
        }
        else {
            Write-Host "native-probe.txt was not written (process may have been blocked at start)"
        }
    }
    catch {
        Write-Host "could not start DeltaTor.exe: $($_.Exception.Message)"
    }
}
else {
    Write-Host "DeltaTor.exe not found next to this script"
}
Write-Host ""

Write-Host "--- 3. security products ---"
try {
    $av = Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntivirusProduct -ErrorAction Stop
    foreach ($v in $av) {
        Write-Host ("AV product:              {0}" -f $v.displayName)
    }
}
catch {
    Write-Host "AV product:              (SecurityCenter2 query failed)"
}
try {
    $excl = (Get-MpPreference -ErrorAction Stop).ExclusionPath
    if ($excl) {
        foreach ($e in $excl) { Write-Host ("Defender exclusion:      {0}" -f $e) }
    }
    else {
        Write-Host "Defender exclusion:      (none)"
    }
}
catch {
    Write-Host "Defender exclusion:      (query failed - Defender may be disabled or managed)"
}
Write-Host ""
Write-Host "Paste this whole window's text into the bug report."
