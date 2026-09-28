# Fetches the official Tor Project tor binary for Android (guardianproject/tor-android AAR)
# and drops libtor.so into app/src/main/jniLibs. Verifies the artifact checksum.
# Usage:  powershell -ExecutionPolicy Bypass -File scripts/fetch-official-tor.ps1 [-Version 0.4.9.13]
param(
    [string]$Version = "0.4.9.13"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$tmp  = Join-Path $env:TEMP "tor-android-$Version"
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

$url = "https://github.com/guardianproject/gpmaven/raw/master/info/guardianproject/tor-android/$Version/tor-android-$Version.aar"
$aar = Join-Path $tmp "tor-android-$Version.aar"
Write-Host "Downloading $url"
# GitHub raw can be flaky; GitHub's API needs a token, so retry a few times.
for ($i = 1; $i -le 5; $i++) {
    curl.exe -L --retry 3 -o $aar $url
    if ((Get-Item $aar).Length -gt 1000000) { break }
    Write-Host "Retry $i..."
    Start-Sleep -Seconds 5
}

$expect = @{
    "0.4.9.13" = @{
        "arm64-v8a"    = "59398e39a1332608fc87660b233dddd88e0792fc7b58724338d9df36bac82fb2"
        "armeabi-v7a"  = "8948b5e5d94f5332c0d5ffa223c06a6614001dc4289cf4d4c63488700445f341"
    }
}
if (-not $expect.ContainsKey($Version)) { throw "No pinned checksums for tor-android $Version" }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($aar)
try {
    foreach ($abi in @("arm64-v8a", "armeabi-v7a")) {
        $entry = $zip.GetEntry("jni/$abi/libtor.so")
        if (-not $entry) { throw "jni/$abi/libtor.so missing from AAR" }
        $out  = Join-Path $tmp "libtor-$abi.so"
        $dest = Join-Path $root "app/src/main/jniLibs/$abi/libtor.so"
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $out, $true)
        $h = (Get-FileHash $out -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($h -ne $expect[$Version][$abi]) { throw "SHA256 mismatch for $abi: got $h" }
        Copy-Item $out $dest -Force
        Write-Host "OK $abi -> $dest ($h)"
    }
} finally {
    $zip.Dispose()
}
Write-Host "tor-android $Version binaries installed."