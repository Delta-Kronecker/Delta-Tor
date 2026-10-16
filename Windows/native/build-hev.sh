#!/usr/bin/env bash
# build-hev.sh — build hev-socks5-tunnel.dll (Windows/Wintun backend) from
# the vendored source tree.
#
# Runs in an MSYS2 MINGW64 shell (the CI "msys2 {0}" step, or a local MSYS2
# install) with the MinGW-w64 toolchain. Upstream only documents an MSYS
# (Cygwin-runtime) build, which links msys-2.0.dll; that runtime's DllMain
# is what antivirus products refuse inside third-party processes
# (0x8007045A / ERROR_DLL_INIT_FAILED at the first P/Invoke), so this port
# compiles the same sources for MinGW instead:
#   - src/misc/hev-mingw.h (force-included) redirects the POSIX socket API
#     to winsock and provides the few missing names (socketpair, daemon, ...);
#   - src/misc/win-compat-include/ supplies the POSIX headers MinGW lacks;
#   - the __MSYS__-gated backends (tunnel/wintun/IOCP reactor) now also open
#     for __MINGW32__.
# The result is a plain Windows DLL with no runtime beyond the system DLLs.
#
# Output (gitignored, produced by CI before dotnet build):
#   Windows/native/hev-socks5-tunnel/hev-socks5-tunnel.dll
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SRC="$ROOT/Android/app/src/main/cpp/hev-socks5-tunnel-src"
OUT="$ROOT/Windows/native/hev-socks5-tunnel"
COMPAT_INC="$SRC/src/misc/win-compat-include"
MINGW_COMPAT="$SRC/src/misc/hev-mingw.h"

if [ "${MSYSTEM:-}" != "MINGW64" ]; then
    echo "[build-hev] error: MINGW64 toolchain required (MSYSTEM=${MSYSTEM:-unset})" >&2
    exit 1
fi

# -Wno-error: the tree builds with -Wall -Werror against upstream's older gcc,
# and newer MinGW-w64 releases flag things upstream has not seen yet.
# -fcommon keeps legacy tentative definitions linking. -include loads the
# compatibility layer into every translation unit before any system header.
export CFLAGS="-Wno-error -fcommon -D_WIN32_WINNT=0x0A00 -DWIN32_LEAN_AND_MEAN -DNOMINMAX -I$COMPAT_INC -include $MINGW_COMPAT"

echo "[build-hev] building static libraries in $SRC"
make -C "$SRC" static

echo "[build-hev] linking $OUT/hev-socks5-tunnel.dll"
mkdir -p "$OUT"

# ENABLE_LIBRARY (set by the static target) compiles hev-main.c without its
# main() and exports the same five operations the Android JNI wrapper uses:
# main_from_str, quit, stats, set_reject_quic, set_reject_non_dns_udp.
#
# Linking: -static-libgcc and the static winpthreads keep libgcc/libwinpthread
# inside the DLL, so the only remaining dependencies are Windows system DLLs
# (ws2_32, iphlpapi, kernel32, ...). No msys-2.0.dll is fetched or shipped.
gcc -shared -o "$OUT/hev-socks5-tunnel.dll" \
    -Wl,--export-all-symbols \
    -Wl,--start-group \
    "$SRC/bin/libhev-socks5-tunnel.a" \
    "$SRC/third-part/yaml/bin/libyaml.a" \
    "$SRC/third-part/lwip/bin/liblwip.a" \
    "$SRC/third-part/hev-task-system/bin/libhev-task-system.a" \
    -Wl,--end-group \
    -static-libgcc -Wl,-Bstatic -lpthread -Wl,-Bdynamic \
    -lws2_32 -lIphlpapi

test -f "$OUT/hev-socks5-tunnel.dll"
echo "[build-hev] OK:"
ls -l "$OUT"
