# DeltaTor tunnel DLL diagnostic — run from the app folder (next to
# DeltaTor.exe) and paste the whole output into the bug report. It loads
# each shipped DLL the way the app does and reports the exact Win32 error,
# which separates "file missing" (126) from "runtime refused to initialize"
# (1114, the 0x8007045A users see) and from antivirus/MotW interference.
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
        Write-Host ("{0,-24} Mark-of-the-Web: YES — right-click the file, Properties, tick Unblock, Apply" -f $name)
    }
    # hev-socks5-tunnel.dll gets LOAD_WITH_ALTERED_SEARCH_PATH (0x8) so its
    # dependency (msys-2.0.dll) resolves from this folder, exactly like when
    # DeltaTor.exe loads it. The others load by full path, no flags.
    $flags = if ($name -eq 'hev-socks5-tunnel.dll') { 0x8 } else { 0 }
    $handle = [DeltaTorDiag.Loader]::LoadLibraryExW($path, [IntPtr]::Zero, $flags)
    if ($handle -eq [IntPtr]::Zero) {
        $err = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        Write-Host ("{0,-24} LOAD FAILED — Win32 error {1} (126=missing dependency, 1114=init failed)" -f $name, $err)
    }
    else {
        Write-Host ("{0,-24} load OK ({1})" -f $name, $handle)
        [void][DeltaTorDiag.Loader]::FreeLibrary($handle)
    }
    Write-Host ""
}
Write-Host "Paste this whole window's text into the bug report."
