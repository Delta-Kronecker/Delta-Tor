<#
# DeltaTor Core License v1.0 (see LICENSE). Using this Core in another program
# requires the mandatory attribution of https://github.com/Delta-Kronecker/DeltaTor.
.SYNOPSIS
  DeltaTor benchmark orchestrator (step 0 harness).

  Works against a scratch copy of the release folder so your Downloads copy is
  never modified. Each run boots tor, waits for 100% bootstrap, runs a battery
  of downloads (1 / 4 / 8 parallel streams) through the HTTP proxy and appends
  one CSV row per measurement.

.PARAMETER Release
  Pristine release folder (default: Downloads\deltator-win64-v2.0.0).

.PARAMETER WorkDir
  Scratch folder created from Release. Reused across runs (keeps cached
  consensus state so bootstrap is fast); delete it to force a clean state.

.PARAMETER Mode
  Connection mode: direct | webtunnel | obfs4 | vanilla | snowflake.

.PARAMETER Iters
  Iterations per stream count (default 3).

.PARAMETER Streams
  Comma-separated stream counts (default "1,4,8").

.PARAMETER OutCSV
  CSV output path, absolute or relative to this script (default results\bench.csv).

.PARAMETER VariantName
  Label stored in the CSV "variant" column (default "baseline").

.PARAMETER Strategy
  Built-in strategy level applied via the launcher (standard | balanced |
  aggressive | ultimate). Overrides/joins ExtraTorrc in the written torrc.
  (default: standard)

.PARAMETER ExtraTorrc
  Extra torrc lines (newline-separated) appended to torrc.template for this run.
  Leave empty for a pure baseline.

.PARAMETER ConfluxModes
  Comma-separated modes to run the quick conflux-engagement check for, then exit.
  (e.g. "direct,webtunnel,obfs4,vanilla"). Each boot waits 100% + 30s and prints
  conflux set/leg counts.
#>
param(
    [string]$Release = "C:\Users\agolb\Downloads\deltator-win64-v2.0.0",
    [string]$WorkDir = "$env:TEMP\deltator-bench",
    [string]$Mode = "direct",
    [int]$Iters = 3,
    [string]$Streams = "1,4,8",
    [string]$OutCSV = "results\bench.csv",
    [string]$VariantName = "baseline",
    [string]$Strategy = "standard",
    [string]$ExtraTorrc = "",
    [string]$ConfluxModes = "",
    # Conflux/monitor overrides: written as data\*.txt settings in the scratch
    # folder before the run (values < 0 leave the corresponding file untouched).
    [int]$ConfluxSets = -1,
    [int]$ConfluxLegs = -1,
    [int]$Selection = -1,
    [int]$RttMax = -1,
    [int]$RttPct = -1,
    [int]$WatchPct = -1
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
$newExe = Join-Path $scriptDir "DeltaTor.exe"

if (-not (Test-Path $Release)) { Write-Host "[x] release folder not found: $Release" -ForegroundColor Red; exit 1 }
if (-not (Test-Path $newExe))  { Write-Host "[x] new DeltaTor.exe not built: $newExe" -ForegroundColor Red; exit 1 }
if (-not (Test-Path (Join-Path $Release "data\tor.exe"))) { Write-Host "[x] invalid release folder (no data\tor.exe)" -ForegroundColor Red; exit 1 }

# --- (re)build scratch work dir from the release, keeping cached consensus -----
if (-not (Test-Path (Join-Path $WorkDir "data\tor.exe"))) {
    Write-Host "creating scratch bench folder: $WorkDir"
    New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null
    # Recursive "*" already copies the data\ subtree; no second copy needed.
    Copy-Item (Join-Path $Release "*") -Destination $WorkDir -Recurse -Force
}
# always refresh the freshly compiled launcher into the scratch folder
Copy-Item $newExe -Destination $WorkDir -Force

# apply conflux/monitor variant overrides as launcher settings files
$settings = @(
    @{ v = $ConfluxSets; f = "conflux-sets.txt" },
    @{ v = $ConfluxLegs;  f = "conflux-legs.txt" },
    @{ v = $Selection;    f = "conflux-selection.txt" },
    @{ v = $RttMax;       f = "conflux-set-rtt.txt" },
    @{ v = $RttPct;       f = "conflux-set-rtt-pct.txt" },
    @{ v = $WatchPct;     f = "circuit-watch-pct.txt" }
)
foreach ($s in $settings) {
    if ($s.v -ge 0) {
        Set-Content -Path (Join-Path $WorkDir "data\$($s.f)") -Value $s.v -Encoding ASCII
        Write-Host "variant setting: $($s.f) = $($s.v)"
    }
}

# pre-flight: kill any tor leaked from this workdir and drop a stale lock file.
# A tor whose path cannot be queried is LEFT ALONE: killing it might hit the
# user's unrelated tor instance, and DeltaTor.exe itself stops previous runs
# holding our ports at startup anyway.
Get-Process tor -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -like "$WorkDir*" } catch { $false }
} | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600
Remove-Item (Join-Path $WorkDir "data\data\lock") -Force -ErrorAction SilentlyContinue

# Absolute paths: drive-lettered AND UNC (\\server\share) are both left as-is.
if ($OutCSV -notmatch '^[A-Za-z]:\\|^\\\\') { $OutCSV = Join-Path $repoRoot $OutCSV }
New-Item -ItemType Directory -Path (Split-Path $OutCSV) -Force | Out-Null

$torrcTemplate = Join-Path $WorkDir "data\torrc.template"
$torrcBackup  = "$torrcTemplate.bench-bak"

function Invoke-Bench {
    param([string]$Name, [string]$Extra)
    $env:BENCH_VARIANT = $Name
    $env:BENCH_STRATEGY = $Strategy
    if (-not (Test-Path $torrcBackup)) { Copy-Item $torrcTemplate $torrcBackup -Force }
    if ($Extra.Trim().Length -gt 0) {
        $base = Get-Content $torrcBackup -Raw
        $augmented = $base.TrimEnd() + "`r`n`r`n# --- bench variant: $Name ---`r`n" + ($Extra -replace "`n", "`r`n") + "`r`n"
        # BOM-free UTF-8 (Set-Content -Encoding UTF8 writes a BOM on PS5).
        [IO.File]::WriteAllText($torrcTemplate, $augmented, (New-Object System.Text.UTF8Encoding($false)))
    } else {
        Copy-Item $torrcBackup $torrcTemplate -Force
    }

    Write-Host "`n=== variant: $Name (mode=$Mode strategy=$Strategy iters=$Iters streams=$Streams) ===" -ForegroundColor Cyan
    & (Join-Path $WorkDir "DeltaTor.exe") --bench $Mode --iters $Iters --streams $Streams --csv $OutCSV
    if ($LASTEXITCODE -ne 0) { Write-Host "[!] bench exited with code $LASTEXITCODE" -ForegroundColor Yellow }
}

if ($ConfluxModes.Trim().Length -gt 0) {
    $worst = 0
    try {
        $modes = $ConfluxModes -split "," | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne "" }
        foreach ($m in $modes) {
            Write-Host "`n=== conflux check: $m ===" -ForegroundColor Cyan
            & (Join-Path $WorkDir "DeltaTor.exe") --conflux-check $m
            # Propagate the child's exit code (0 = engaged, 1 = failed) instead
            # of always exiting 0 and masking failures.
            if ($LASTEXITCODE -gt $worst) { $worst = $LASTEXITCODE }
        }
    } finally {
        if (Test-Path $torrcBackup) { Copy-Item $torrcBackup $torrcTemplate -Force }
        Remove-Item Env:BENCH_VARIANT, Env:BENCH_STRATEGY -ErrorAction SilentlyContinue
    }
    exit $worst
}

try {
    Invoke-Bench -Name $VariantName -Extra $ExtraTorrc
    Write-Host "`n=== last rows of $OutCSV ===" -ForegroundColor Green
    if (Test-Path $OutCSV) { Get-Content $OutCSV | Select-Object -Last ($Iters * (@($Streams -split ",").Count) + 1) }
} finally {
    # Always restore the pristine template (and drop the backup) even when a
    # run dies mid-variant, so a later run never augments an already-modified
    # template.
    if (Test-Path $torrcBackup) {
        Copy-Item $torrcBackup $torrcTemplate -Force
        Remove-Item $torrcBackup -Force -ErrorAction SilentlyContinue
    }
    Remove-Item Env:BENCH_VARIANT, Env:BENCH_STRATEGY -ErrorAction SilentlyContinue
}
