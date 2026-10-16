#!/usr/bin/env bash
# build-hev.sh — build hev-socks5-tunnel.dll (Windows/Wintun backend) from
# the vendored source tree.
#
# Runs in an MSYS2 shell (the CI "msys2 {0}" step, or a local MSYS2
# install). MSYS2 is the only Windows toolchain the upstream tree supports:
# the tunnel sources are POSIX (daemon, setrlimit, pthreads) and both the
# Windows backend (hev-tunnel-windows.c) and the IOCP reactor are gated on
# __MSYS__. The resulting DLL therefore links the msys-2.0.dll runtime,
# which is fetched alongside it (a pinned build, see below) and shipped
# next to DeltaTor.exe.
#
# Output (gitignored, produced by CI before dotnet build):
#   Windows/native/hev-socks5-tunnel/hev-socks5-tunnel.dll
#   Windows/native/hev-socks5-tunnel/msys-2.0.dll
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SRC="$ROOT/Android/app/src/main/cpp/hev-socks5-tunnel-src"
OUT="$ROOT/Windows/native/hev-socks5-tunnel"

# The tree builds with -Wall -Werror; newer MSYS2 gcc releases flag things
# upstream has not seen yet, and -fcommon keeps legacy tentative definitions
# linking. Neither weakens the shipped code paths.
export CFLAGS="-Wno-error -fcommon"

echo "[build-hev] building static libraries in $SRC"
make -C "$SRC" static

echo "[build-hev] linking $OUT/hev-socks5-tunnel.dll"
mkdir -p "$OUT"

# ENABLE_LIBRARY (set by the static target) compiles hev-main.c without its
# main() and exports the same five operations the Android JNI wrapper uses:
# main_from_str, quit, stats, set_reject_quic, set_reject_non_dns_udp.
gcc -shared -o "$OUT/hev-socks5-tunnel.dll" \
    -Wl,--export-all-symbols \
    -Wl,--start-group \
    "$SRC/bin/libhev-socks5-tunnel.a" \
    "$SRC/third-part/yaml/bin/libyaml.a" \
    "$SRC/third-part/lwip/bin/liblwip.a" \
    "$SRC/third-part/hev-task-system/bin/libhev-task-system.a" \
    -Wl,--end-group \
    -lmsys-2.0 -lws2_32 -lIphlpapi -lpthread

# The shipped msys runtime is NOT the image's /usr/bin/msys-2.0.dll and
# NOT the newest MSYS2 runtime either. The CI image's runtime tracks
# latest-MSYS2: its DllMain needs recent Windows 10 APIs and can refuse
# to initialize on older client installs (Win32 0x8007045A,
# ERROR_DLL_INIT_FAILED — the user-visible failure is DllNotFoundException
# at the first P/Invoke), and fresh runtimes trip AV heuristics that have
# never seen them. The DLL shipped here comes from the official
# msys2-base 2024-07-27 snapshot (msys2-runtime 3.5.x — the
# Git-for-Windows era, Win7+ compatible, present on hundreds of millions
# of machines). msys-2.0's ABI is stable across these releases; CI's
# smoke test loads the tunnel DLL against this pairing on every build and
# fails the build if the two ever stop matching.
MSYS_RELEASE=20240727
curl -fsSL -o /tmp/msys2-base.tar.xz \
    "https://github.com/msys2/msys2-installer/releases/download/2024-07-27/msys2-base-x86_64-$MSYS_RELEASE.tar.xz"
tar -xJf /tmp/msys2-base.tar.xz -C /tmp
find /tmp/msys64/usr/bin -name msys-2.0.dll -exec cp {} "$OUT/msys-2.0.dll" \;
rm -rf /tmp/msys2-base.tar.xz /tmp/msys64
test -f "$OUT/msys-2.0.dll"

echo "[build-hev] OK:"
ls -l "$OUT"
