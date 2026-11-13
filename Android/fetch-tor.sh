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
# Each zip carries, at its ROOT, a `libtor.so` that is NOT runnable, a `tor`
# executable, a `torrc` and data/geoip*. The fork's Android build links libtor.so
# with `clang -shared`, so it is a shared library with no entry point (e_entry = 0)
# and no PT_INTERP; exec'ing it jumps to address 0 and the process dies with
# SIGSEGV (exit 139). The `tor` entry is the real, self-contained PIE executable
# (DT_NEEDED is only libc/libdl/libm). The app execs the file named libtor.so
# straight out of nativeLibraryDir, so THE `tor` ENTRY IS INSTALLED AS libtor.so
# and the zip's library is ignored ([assert_runnable] fails the build if the
# installed file ever stops being runnable). torrc lands in the app's assets,
# where it becomes the default template the settings screen edits. The geoip
# tables are unused (the app generates its own from its bundled range table).
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
# Bump when the extraction recipe changes (e.g. which zip entry becomes
# libtor.so). An older marker then no longer suppresses a re-extract, so a dev
# checkout fixes itself instead of keeping the file the old recipe produced.
MARKER_VERSION=2

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

# An executable ELF (PIE program) has a non-zero entry point; a plain shared
# library has e_entry = 0. e_entry sits at byte offset 24 in both ELF32 and
# ELF64 headers, so one 4-byte read decides it.
assert_runnable() {
  local f="$1" entry
  if command -v python3 >/dev/null 2>&1; then
    python3 - "$f" <<'PY'
import struct, sys
with open(sys.argv[1], "rb") as fh:
    d = fh.read(32)
if d[:4] != b"\x7fELF":
    sys.exit(1)
entry = struct.unpack_from("<Q", d, 24)[0] if d[4] == 2 else struct.unpack_from("<I", d, 24)[0]
sys.exit(0 if entry else 1)
PY
  else
    entry="$(od -An -tx1 -j24 -N4 "$f" 2>/dev/null | tr -d ' \n')"
    [ -n "$entry" ] && [ "$entry" != "00000000" ]
  fi
}

# Install the runnable `tor` entry as libtor.so (see the header) and the torrc
# into the app's assets.
extract_core() {
  local zip="$1" so_dest="$2"
  mkdir -p "$so_dest" "$ASSETS"
  if command -v unzip >/dev/null 2>&1; then
    unzip -o -j -q "$zip" tor -d "$so_dest"
    mv -f "$so_dest/tor" "$so_dest/libtor.so"
    unzip -o -j -q "$zip" torrc -d "$ASSETS"
  elif command -v python3 >/dev/null 2>&1; then
    python3 - "$zip" "$so_dest" "$ASSETS" <<'PY'
import os, sys, zipfile
zf, so_dest, assets = sys.argv[1], sys.argv[2], sys.argv[3]
with zipfile.ZipFile(zf) as z:
    so = z.read("tor")
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

# The recipe version recorded by the previous run; a mismatch forces a re-extract.
marker_version() {
  [ -f "$MARKER" ] || return 1
  head -1 "$MARKER" | awk '{print $2}'
}

: > "$MARKER.new"
echo "# v$MARKER_VERSION" >> "$MARKER.new"
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

  if [ "$(marker_version || true)" = "v$MARKER_VERSION" ] \
    && [ -f "$target" ] && [ -f "$TORRC_ASSET" ] \
    && [ "$(prev_digest "$abi" || true)" = "$digest" ]; then
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
  chmod +x "$target" 2>/dev/null || true
  assert_runnable "$target" \
    || fail "$target is not a runnable executable (e_entry = 0); the release packaging changed"
  [ -s "$TORRC_ASSET" ] || fail "$TORRC_ASSET missing or empty after extraction"
  log "$abi: tor -> $target (runnable)"
  log "$abi: torrc -> $TORRC_ASSET"
  echo "$abi $digest" >> "$MARKER.new"
done

mv "$MARKER.new" "$MARKER"
log "done"
