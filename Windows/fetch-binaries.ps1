# fetch-binaries.ps1 — fetch the pinned official Tor Expert Bundle for Windows.
#
#   Source archive (official, pinned):
#   https://dist.torproject.org/torbrowser/15.0.24/tor-expert-bundle-windows-x86_64-15.0.24.tar.gz
#
# Resolution order (first hit wins):
#   1. already extracted -> Windows/vendor/tor-expert-bundle-windows-x86_64-15.0.24/
#   2. local archive cache (repo root or Windows/) -> verify SHA-256 -> extract
#   3. repo-root extracted cache (../tor-expert-bundle-windows-x86_64-15.0.24/) -> copy
#   4. download from the pinned URL -> verify SHA-256 -> extract
#
# Hash policy: $ExpectedSha256 empty => local run warns and prints the computed
# hash so it can be pinned; -CI always fails (fail closed, no unpinned CI).
#
# wintun.dll is added here in stage 4 (full-tunnel work), not yet.

[CmdletBinding()]
param(
    [switch]$CI,
    [string]$CacheRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

$Version   = '15.0.24'
$BaseName  = "tor-expert-bundle-windows-x86_64-$Version"
$Archive   = "$BaseName.tar.gz"
$Url       = "https://dist.torproject.org/torbrowser/$Version/$Archive"
$VendorDir = Join-Path $PSScriptRoot 'vendor'
$TargetDir = Join-Path $VendorDir $BaseName

# SHA-256 from the official signed checksums:
# https://dist.torproject.org/torbrowser/15.0.24/sha256sums-signed-build.txt
$ExpectedSha256 = 'e9dc6ccc93cd6afa507193f4de284d6424233ff5102155cd2c94b259e8a22b65'

function Write-Step([string]$Message) { Write-Host "[fetch] $Message" }

function Test-Extraction {
    # Layout inside the official archive:
    #   <root>/tor/tor.exe
    #   <root>/tor/pluggable_transports/lyrebird.exe
    #   <root>/data/geoip, data/geoip6, data/torrc-defaults
    $tor      = Join-Path $TargetDir 'tor/tor.exe'
    $lyrebird = Join-Path $TargetDir 'tor/pluggable_transports/lyrebird.exe'
    $geoip    = Join-Path $TargetDir 'data/geoip'
    return (Test-Path $tor) -and (Test-Path $lyrebird) -and (Test-Path $geoip)
}

function Show-BundleLayout {
    Write-Step "Actual layout under ${VendorDir}:"
    if (Test-Path $VendorDir) {
        Get-ChildItem -Recurse -File $VendorDir | Select-Object -First 40 | ForEach-Object {
            Write-Step ("  " + $_.FullName.Substring($VendorDir.Length))
        }
    }
    else {
        Write-Step "  (vendor directory does not exist)"
    }
}

function Assert-Hash([string]$Path) {
    $actual = (Get-FileHash -Algorithm SHA256 -Path $Path).Hash.ToLowerInvariant()
    if ([string]::IsNullOrWhiteSpace($ExpectedSha256)) {
        if ($CI) {
            throw "Archive hash is not pinned in fetch-binaries.ps1 (computed: $actual). Refusing to run unpinned in CI."
        }
        Write-Warning "Archive hash NOT pinned yet. Computed SHA-256: $actual. Set ExpectedSha256 in this script."
    }
    elseif ($actual -ne $ExpectedSha256.ToLowerInvariant()) {
        throw "SHA-256 mismatch for $Path. Expected $ExpectedSha256 but got $actual"
    }
    else {
        Write-Step "SHA-256 verified: $actual"
    }
}

# 1) already extracted
if (Test-Extraction) {
    Write-Step "Using existing extraction: $TargetDir"
    exit 0
}

New-Item -ItemType Directory -Force -Path $VendorDir | Out-Null

# 2) local archive cache
$archivePath = $null
foreach ($dir in @($CacheRoot, $PSScriptRoot)) {
    $candidate = Join-Path $dir $Archive
    if (Test-Path $candidate) { $archivePath = $candidate; break }
}

# 3) repo-root extracted cache (offline machines), tried before any download
if (-not $archivePath) {
    $rootCache = Join-Path $CacheRoot $BaseName
    if (Test-Path (Join-Path (Join-Path $rootCache 'tor') 'tor.exe')) {
        Write-Step "No archive found; copying extracted cache: $rootCache"
        Copy-Item -Recurse -Force $rootCache $TargetDir
        if (-not (Test-Extraction)) { throw "Repo-root cache is incomplete: $rootCache" }
        Write-Step "OK -> $TargetDir (cache copy; hash only applies to the archive)"
        exit 0
    }
}

if ($archivePath) {
    Write-Step "Found cached archive: $archivePath"
}
else {
    # 4) download
    $archivePath = Join-Path $CacheRoot $Archive
    Write-Step "Downloading $Url"
    Write-Step "Destination: $archivePath"
    curl.exe -fsSL --retry 3 --retry-delay 2 -o $archivePath $Url
    if ($LASTEXITCODE -ne 0) { throw "Download failed (curl exit $LASTEXITCODE): $Url" }
}

Assert-Hash $archivePath

# 5) extract (tar ships with Windows 10+ and every GitHub windows runner)
Write-Step "Extracting $Archive -> $VendorDir"
tar -xzf $archivePath -C $VendorDir
if ($LASTEXITCODE -ne 0) { throw "Extraction failed (tar exit $LASTEXITCODE)" }

if (-not (Test-Extraction)) {
    Show-BundleLayout
    throw "Bundle incomplete after extraction: $TargetDir"
}

Write-Step "OK -> $TargetDir"
