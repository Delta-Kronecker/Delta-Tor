# fetch-binaries.ps1 — fetch the pinned official Tor Expert Bundle for Windows.
#
#   Source archive (official, pinned):
#   https://dist.torproject.org/torbrowser/15.0.25/tor-expert-bundle-windows-x86_64-15.0.25.tar.gz
#
#   The archive is FLAT (no top-level directory): tor/, data/, docs/ sit at
#   the archive root. This script extracts it into
#   Windows/vendor/tor-expert-bundle-windows-x86_64-15.0.25/ anyway, and
#   Find-BundleRoot also tolerates any other layout by locating the directory
#   that actually contains tor/tor.exe.
#
# Resolution order (first hit wins):
#   1. already extracted -> Windows/vendor/**/ (bundle root with tor+lyrebird+geoip)
#   2. local archive cache (repo root or Windows/) -> verify SHA-256 -> extract
#   3. repo-root extracted cache (../tor-expert-bundle-windows-x86_64-15.0.25/) -> copy
#   4. download from the pinned URL -> verify SHA-256 -> extract
#
# Hash policy: SHA-256 pinned below (from the official signed checksums at
# https://dist.torproject.org/torbrowser/15.0.25/sha256sums-signed-build.txt).
# -CI refuses to run unpinned; a mismatch always fails.
#
# Se7enTun (WinTUN + tun2socks engine): pinned GitHub release, verified by
# SHA-256, extracted to Windows/libs/se7entun/. The app csproj copies
# Se7enTun.dll and its data plane (Resources\tun2socks\*) into the output.

[CmdletBinding()]
param(
    [switch]$CI,
    [string]$CacheRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

$Version   = '15.0.25'
$BaseName  = "tor-expert-bundle-windows-x86_64-$Version"
$Archive   = "$BaseName.tar.gz"
$Url       = "https://dist.torproject.org/torbrowser/$Version/$Archive"
$VendorDir = Join-Path $PSScriptRoot 'vendor'
$TargetDir = Join-Path $VendorDir $BaseName

# SHA-256 from the official signed checksums:
# https://dist.torproject.org/torbrowser/15.0.25/sha256sums-signed-build.txt
$ExpectedSha256 = 'c1988c701de2f24f7d098d1d6d7f507b58f27740d097f29c246e0524d639b252'

function Write-Step([string]$Message) { Write-Host "[fetch] $Message" }

function Test-Extraction([string]$Root) {
    if ([string]::IsNullOrWhiteSpace($Root)) { return $false }
    # Layout inside the official archive:
    #   <root>/tor/tor.exe
    #   <root>/tor/pluggable_transports/lyrebird.exe
    #   <root>/data/geoip, data/geoip6, data/torrc-defaults
    $tor      = Join-Path $Root 'tor/tor.exe'
    $lyrebird = Join-Path $Root 'tor/pluggable_transports/lyrebird.exe'
    $geoip    = Join-Path $Root 'data/geoip'
    return (Test-Path $tor) -and (Test-Path $lyrebird) -and (Test-Path $geoip)
}

# Locate an already-extracted bundle root regardless of top-level layout:
# preferred target first, then any child of vendor, then vendor itself.
function Find-BundleRoot {
    if (Test-Extraction $TargetDir) { return $TargetDir }
    if (Test-Path $VendorDir) {
        foreach ($d in (Get-ChildItem -Directory $VendorDir | Sort-Object Name)) {
            if (Test-Extraction $d.FullName) { return $d.FullName }
        }
        if (Test-Extraction $VendorDir) { return $VendorDir }
    }
    return $null
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

# ---------------------------------------------------------------------------
# Se7enTun (WinTUN + tun2socks engine) - pinned GitHub release, verified by
# SHA-256. Runs before the bundle short-circuit so it is fetched on every path.
# The zip extracts Se7enTun.dll and Resources\tun2socks\* at its root.
# ---------------------------------------------------------------------------
$Se7enVersion = '1.0.0'
$Se7enArchive = "Se7enTun-$Se7enVersion-win-x64.zip"
$Se7enUrl     = "https://github.com/Delta-Kronecker/tun/releases/download/v$Se7enVersion/$Se7enArchive"
# SHA-256 of the release asset above, recorded when it was first pinned.
$Se7enSha256  = 'cc6a23e5481ec3ac0f123870941e7146dba53595f464bc133edc39e8c6c23f06'
$Se7enLibDir  = Join-Path (Split-Path -Parent $PSScriptRoot) 'libs/se7entun'
$Se7enDll     = Join-Path $Se7enLibDir 'Se7enTun.dll'
$Se7enPlane   = Join-Path $Se7enLibDir 'Resources/tun2socks/tun2socks.exe'

if ((Test-Path $Se7enDll) -and (Test-Path $Se7enPlane)) {
    Write-Step "Se7enTun already extracted: $Se7enLibDir"
}
else {
    New-Item -ItemType Directory -Force -Path $Se7enLibDir | Out-Null
    $se7enArchivePath = $null
    foreach ($dir in @($CacheRoot, $PSScriptRoot)) {
        $candidate = Join-Path $dir $Se7enArchive
        if (Test-Path $candidate) { $se7enArchivePath = $candidate; break }
    }
    if (-not $se7enArchivePath) {
        $se7enArchivePath = Join-Path $CacheRoot $Se7enArchive
        Write-Step "Downloading $Se7enUrl"
        curl.exe -fsSL --retry 3 --retry-delay 2 -o $se7enArchivePath $Se7enUrl
        if ($LASTEXITCODE -ne 0) { throw "Download failed (curl exit $LASTEXITCODE): $Se7enUrl" }
    }
    $se7enActual = (Get-FileHash -Algorithm SHA256 -Path $se7enArchivePath).Hash.ToLowerInvariant()
    if ($se7enActual -ne $Se7enSha256) {
        throw "SHA-256 mismatch for $se7enArchivePath. Expected $Se7enSha256 but got $se7enActual"
    }
    Write-Step "Se7enTun SHA-256 verified: $se7enActual"
    tar -xf $se7enArchivePath -C $Se7enLibDir
    if ($LASTEXITCODE -ne 0) { throw "Se7enTun extraction failed (tar exit $LASTEXITCODE)" }
    if (-not (Test-Path $Se7enDll)) { throw "Se7enTun.dll missing after extraction: $Se7enDll" }
    if (-not (Test-Path $Se7enPlane)) { throw "tun2socks.exe missing after extraction: $Se7enPlane" }
    Write-Step "Se7enTun -> $Se7enLibDir"
}

# 1) already extracted
$found = Find-BundleRoot
if ($found) {
    $TargetDir = $found
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
    if (Test-Extraction $rootCache) {
        Write-Step "No archive found; copying extracted cache: $rootCache"
        New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null
        Copy-Item -Recurse -Force (Join-Path $rootCache '*') $TargetDir
        $found = Find-BundleRoot
        if (-not $found) { Show-BundleLayout; throw "Repo-root cache is incomplete: $rootCache" }
        $TargetDir = $found
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

# 5) extract into the preferred target (tar ships with Windows 10+ and every
#    GitHub windows runner); the archive itself is flat.
Write-Step "Extracting $Archive -> $TargetDir"
New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null
tar -xzf $archivePath -C $TargetDir
if ($LASTEXITCODE -ne 0) { throw "Extraction failed (tar exit $LASTEXITCODE)" }

$found = Find-BundleRoot
if (-not $found) {
    Show-BundleLayout
    throw "Bundle incomplete after extraction: $TargetDir"
}
$TargetDir = $found
Write-Step "Bundle root: $TargetDir"
Write-Step "OK -> $TargetDir"
