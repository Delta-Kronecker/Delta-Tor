#!/usr/bin/env bash
#
# fetch-tor.sh - fetch the Delta-Kronecker/tor Android cores (libtor.so) and the
# fork's default torrc, and drop them into the app:
#
#   Android/app/src/main/jniLibs/arm64-v8a/libtor.so
#   Android/app/src/main/jniLibs/armeabi-v7a/libtor.so
#   Android/app/src/main/assets/torrc
#
#   Release assets (moving 'latest' tag):
#     https://github.com/Delta-Kronecker/tor/releases/download/latest/tor-android-<abi>.zip
#
# Each zip carries libtor.so and torrc at its ROOT (alongside a `tor` executable
# and data/geoip*). libtor.so lands in jniLibs and torrc in the app's assets,
# where it becomes the default template the settings screen edits; the tor
# executable and the geoip tables are unused (the app execs libtor.so itself and
# generates its own geoip from its bundled range table), so they stay in the zip.
#
# The 'latest' tag moves with every fork build, so the pin is the SHA-256 the
# GitHub API publishes for each asset. Every archive is verified against it and
# a mismatch always fails.
#
# libobfs4proxy.so (lyrebird) is NOT part of these releases and stays a
# committed binary; this script never touches it.
#
# Resolution per ABI (first hit wins):
#   1. already extracted and the recorded digest still matches -> skip
#   2. cached archive in the repo root or Android/ -> verify -> extract
#   3. download from the pinned URL -> verify -> extract
#
# Usage: Android/fetch-tor.sh [--ci]
#   --ci  refuse to run without a resolved digest; a CI build must never fall
#         back to a stale or missing core.
#
set -euo pipefail

TOR_REPO="Delta-Kronecker/tor"
TOR_TAG="latest"
API_URL="https://api.github.com/repos/${TOR_REPO}/releases/tags/${TOR_TAG}"
DOWNLOAD_BASE="https://github.com/${TOR_REPO}/releases/download/${TOR_TAG}"
ABIS=(arm64-v8a armeabi-v7a)

CI=0
for arg in "$@"; do
  case "$arg" in
    --ci) CI=1 ;;
    *) echo "fetch-tor: unknown argument '$arg'" >&2; exit 2 ;;
  esac
done

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"
JNILIBS="$SCRIPT_DIR/app/src/main/jniLibs"
ASSETS="$SCRIPT_DIR/app/src/main/assets"
TORRC_ASSET="$ASSETS/torrc"
MARKER="$JNILIBS/.libtor-digest"

log()  { echo "[fetch-tor] $*"; }
fail() { echo "[fetch-tor] error: $*" >&2; exit 1; }

sha256_of() {
  # One of the two is present on every Linux runner and on macOS/MSYS.
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | awk '{print tolower($1)}'
  elif command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$1" | awk '{print tolower($1)}'
  else
    fail "neither sha256sum nor shasum is available"
  fi
}

# Digest of one release asset, straight from the API. Prints nothing and exits
# non-zero when it cannot be resolved (offline, rate-limited, asset renamed).
resolve_digest() {
  local asset="$1" json
  local hdrs=(-H 'Accept: application/vnd.github+json')
  if [ -n "${GITHUB_TOKEN:-}" ]; then
    hdrs+=(-H "Authorization: Bearer ${GITHUB_TOKEN}")
  fi
  json="$(curl -fsSL --retry 3 --retry-delay 2 "${hdrs[@]}" "$API_URL" 2>/dev/null)" || return 1
  if command -v python3 >/dev/null 2>&1; then
    printf '%s' "$json" | python3 -c '
import json, sys
name = sys.argv[1]
try:
    rel = json.load(sys.stdin)
except Exception:
    sys.exit(1)
for a in rel.get("assets", []):
    if a.get("name") == name:
        d = a.get("digest") or ""
        if d.startswith("sha256:"):
            print(d[len("sha256:"):].lower())
            sys.exit(0)
sys.exit(1)
' "$asset"
  else
    printf '%s' "$json" | tr -d '\n' \
      | grep -o "\"name\": *\"$asset\".\{0,200\}\"digest\": *\"sha256:[0-9a-f]\{64\}\"" \
      | grep -o '[0-9a-f]\{64\}' | tail -1
  fi
}

# Extract libtor.so into the ABI's jniLibs dir and torrc into the app's assets.
extract_core() {
  local zip="$1" so_dest="$2"
  mkdir -p "$so_dest" "$ASSETS"
  if command -v unzip >/dev/null 2>&1; then
    unzip -o -j -q "$zip" libtor.so -d "$so_dest"
    unzip -o -j -q "$zip" torrc -d "$ASSETS"
  elif command -v python3 >/dev/null 2>&1; then
    python3 - "$zip" "$so_dest" "$ASSETS" <<'PY'
import os, sys, zipfile
zf, so_dest, assets = sys.argv[1], sys.argv[2], sys.argv[3]
with zipfile.ZipFile(zf) as z:
    so = z.read("libtor.so")
    torrc = z.read("torrc")
os.makedirs(so_dest, exist_ok=True)
os.makedirs(assets, exist_ok=True)
with open(os.path.join(so_dest, "libtor.so"), "wb") as fh:
    fh.write(so)
with open(os.path.join(assets, "torrc"), "wb") as fh:
    fh.write(torrc)
PY
  else
    fail "no unzip and no python3 to extract $zip"
  fi
}

command -v curl >/dev/null 2>&1 || fail "curl is required"

mkdir -p "$JNILIBS"

# The archive digest recorded by the previous successful run for one ABI.
prev_digest() {
  [ -f "$MARKER" ] || return 1
  awk -v abi="$1" '$1 == abi { print $2 }' "$MARKER" | tail -1
}

: > "$MARKER.new"
for abi in "${ABIS[@]}"; do
  asset="tor-android-${abi}.zip"
  dest="$JNILIBS/$abi"
  target="$dest/libtor.so"

  digest="$(resolve_digest "$asset" || true)"
  if [ -z "$digest" ]; then
    if [ "$CI" = 1 ]; then
      fail "cannot resolve the SHA-256 of $asset from the GitHub API; refusing to build unverified"
    fi
    if [ -f "$target" ] && [ -f "$TORRC_ASSET" ]; then
      log "$abi: warning: no digest for $asset; keeping the existing $target"
      echo "$abi $(prev_digest "$abi" || echo unknown)" >> "$MARKER.new"
      continue
    fi
    fail "cannot resolve the SHA-256 of $asset and $target does not exist; cannot continue"
  fi

  if [ -f "$target" ] && [ -f "$TORRC_ASSET" ] && [ "$(prev_digest "$abi" || true)" = "$digest" ]; then
    log "$abi: already up to date ($digest)"
    echo "$abi $digest" >> "$MARKER.new"
    continue
  fi

  archive=""
  for dir in "$REPO_ROOT" "$SCRIPT_DIR"; do
    if [ -f "$dir/$asset" ]; then
      archive="$dir/$asset"
      break
    fi
  done

  if [ -n "$archive" ]; then
    if [ "$(sha256_of "$archive")" = "$digest" ]; then
      log "$abi: using cached archive $archive"
    else
      log "$abi: cached archive $archive does not match the release digest; downloading"
      archive=""
    fi
  fi

  if [ -z "$archive" ]; then
    archive="$REPO_ROOT/$asset"
    log "$abi: downloading $DOWNLOAD_BASE/$asset"
    curl -fsSL --retry 3 --retry-delay 2 -o "$archive" "$DOWNLOAD_BASE/$asset" \
      || fail "download failed: $DOWNLOAD_BASE/$asset"
  fi

  actual="$(sha256_of "$archive")"
  if [ "$actual" != "$digest" ]; then
    fail "SHA-256 mismatch for $archive: release says $digest, got $actual"
  fi
  log "$abi: SHA-256 verified $digest"

  extract_core "$archive" "$dest"
  [ -s "$target" ] || fail "$target missing or empty after extraction"
  [ -s "$TORRC_ASSET" ] || fail "$TORRC_ASSET missing or empty after extraction"
  log "$abi: libtor.so -> $target"
  log "$abi: torrc -> $TORRC_ASSET"
  echo "$abi $digest" >> "$MARKER.new"
done

mv "$MARKER.new" "$MARKER"
log "done"
