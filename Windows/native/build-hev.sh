#!/usr/bin/env bash
# build-hev.sh — build hev-socks5-tunnel.dll (Windows/Wintun backend) from
# the vendored source tree.
#
# Runs in an MSYS2 shell (the CI "msys2 {0}" step, or a local MSYS2
# install). MSYS2 is the only Windows toolchain the upstream tree supports:
# the tunnel sources are POSIX (daemon, setrlimit, pthreads) and both the
# Windows backend (hev-tunnel-windows.c) and the IOCP reactor are gated on
# __MSYS__. The resulting DLL therefore links the msys-2.0.dll runtime,
# which is copied alongside it and shipped next to DeltaTor.exe.
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

cp /usr/bin/msys-2.0.dll "$OUT/"

echo "[build-hev] OK:"
ls -l "$OUT"
