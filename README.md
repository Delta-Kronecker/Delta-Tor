# DeltaTor

DeltaTor is an Android and Windows Tor client built on one idea: **race several pluggable
transports at the same time and keep whichever one actually works on the network you are on.**

On Android, Tor, lyrebird and tun2socks are compiled into the APK — there is no external
Tor install and no companion app. On Windows, the same client ships as a portable zip that
runs the official Tor Expert Bundle binaries next to its own exe and builds its tun2socks
in CI. The bridge lists come from
[Delta-Kronecker/Tor-Bridges-Collector](https://github.com/Delta-Kronecker/Tor-Bridges-Collector),
the transports are `obfs4`, `webtunnel` and `snowflake`, and the `auto` mode races them
side by side. There are no accounts, analytics, telemetry or crash reporting.

## Contents

- [Install](#install)
- [Android](#android)
  - [Racing the transports](#racing-the-transports)
  - [The memory runner](#the-memory-runner)
  - [Bridge sources](#bridge-sources)
  - [Transport selection](#transport-selection)
  - [Exit country](#exit-country)
  - [Using it](#using-it)
- [Windows](#windows)
- [Building](#building)
- [CI](#ci)
- [Privacy](#privacy)
- [License](#license)

## Install

Grab the asset you need from the
[releases page](https://github.com/Delta-Kronecker/Delta-Tor/releases).

| Asset | Use it on | Size |
|---|---|---|
| `deltator-vX.Y.Z-arm64-v8a.apk` | almost every phone and tablet made in the last decade | ~22 MB |
| `deltator-vX.Y.Z-armeabi-v7a.apk` | 32-bit ARM devices | ~22 MB |
| `deltator-vX.Y.Z-universal.apk` | anything else, carries every ABI | ~38 MB |
| `deltator-vX.Y.Z-windows-x64.zip` | Windows 10/11 on 64-bit Intel/AMD | ~40 MB |

`x86` and `x86_64` payloads only contain tun2socks, because Tor and lyrebird are not built
for x86. The app is therefore meant for real ARM devices; x86 emulators will not work.

Requirements: Android 7.0 (API 24) or newer. The APK is signed with the release key, so
Android will ask you to allow installs from your browser the first time.

The Windows zip is portable: unpack it anywhere and run `DeltaTor.exe`. The full-tunnel
mode asks Windows for administrator rights when it starts (the route table and the Wintun
adapter need them); Proxy Only runs without elevation.

## Android

The Android client runs a real Tor client in-process, picks a working bridge by racing
several pluggable transports at once, and remembers which bridges actually worked.

The whole client is bundled: `libtor` (Tor), `libobfs4proxy` (lyrebird) and
`hev-socks5-tunnel` (tun2socks) are compiled into the APK. There is no external Tor
installation and no companion app to install alongside it.

### Racing the transports

On connect, four Tor clients are started in parallel, each on its own port, each with its own
`torrc`:

| Runner | Pluggable transport | Bridges |
|---|---|---|
| `vanilla` | none | direct bridges |
| `obfs4` | lyrebird | obfs4 bridges |
| `webtunnel` | lyrebird | webtunnel bridges |
| `memory` | inherited per bridge | the bridges that worked last time |

The first runner that reaches 100% bootstraps wins, the others are stopped, and the VPN is
brought up on the winner. Progress is reported per runner in the notification, and a runner
that stalls is skipped by the bootstrap timeout.

### The memory runner

`memory` has no bridges of its own. It is populated from what the app learns at runtime:

- Whenever a runner reports `Done` or `Bootstrapped 100%`, the descriptor Tor printed
  (`bridge (cached): …` / `bridge (fresh): …`) is read back out of the log.
- Those fingerprints are stored per transport, together with every bridge that already worked
  for the other transports, in a bounded list (60 per transport).
- The next connect starts a fourth runner over exactly those bridges.

The first connect therefore runs three runners. Every connect after the first one that
succeeded runs four. If a memory bridge stops working it simply drops out of the race and the
normal transports take over.

### Bridge sources

Lists come from
[Delta-Kronecker/Tor-Bridges-Collector](https://github.com/Delta-Kronecker/Tor-Bridges-Collector)
and are resolved in this order:

1. the cached list on the device,
2. the copy bundled inside the APK (so a fresh install works before the first download),
3. the network.

`snowflake` comes from the collector's `snowflake.txt`, like the others, with a bundled copy
as the offline fallback.

`webtunnel` is assembled from two files, `webtunnel.txt` and `webtunnel_ipv6.txt`. They are
merged by fingerprint, alternating between the two so both sources are represented once Tor's
per-runner line cap applies. A bridge that is republished with a newer `ver=` replaces its
older twin in place, because a stale webtunnel protocol version is rejected during the
handshake.

Bridge counts in the app are therefore the number of *distinct* bridges, not the number of
lines in the files.

### Transport selection

`ADVANCED` has a **CONNECT VIA** dropdown: `auto`, `vanilla`, `obfs4`, `webtunnel`,
`snowflake`, `direct` (no bridge at all, straight to a guard) or `custom`, where you paste
your own bridge lines. Every mode except `auto` runs exactly one transport.

`auto` races several transports at once and keeps the first that reaches 100%. The **AUTO
RACERS** list below the dropdown decides which ones take part, so you can, for example, drop
`webtunnel` on a network without IPv6. At least one must stay ticked. The `memory` runner
joins automatically once it has bridges that provably worked, and it is not part of that list.

If a race ends with nothing working and Snowflake was not among the racers, the app says so
in a sheet and offers to take you straight to `AUTO RACERS` — snowflake is often the one
transport that gets through where the others are blocked, but it is off by default because it
depends on a live WebRTC broker rather than a static bridge list.

The selection is applied on the next connect.

### Exit country

Any number of countries can be selected; the app writes them as `ExitNodes` with
`StrictNodes 0`. The most used countries are offered first. After connecting, the app
resolves the real exit IP through Tor and shows the country and the address, so a bad exit is
visible at a glance.

Three things make a pinned exit country actually work, and all three are needed:

- `StrictNodes 0`, not `1`. With `1` Tor refuses to build any circuit that does not exit
  through one of the selected nodes, so when those relays are slow, guarded or unreachable no
  circuit can ever complete and bootstrap stops at 50%. With `0` the list is a strong
  preference: Tor still steers into those countries, but a circuit can always be finished.
- A geoip database. Tor can only resolve `ExitNodes {us}` if it knows which country a relay is
  in, and the bundled Tor binary ships no geoip, so the rule used to match nothing and the
  selection was ignored in silence. The database is generated on demand from the range table
  the APK already carries (`assets/geoip/country.csv.gz`), converted to Tor's `low,high,CC`
  format, for the selected countries only. Both `geoip` and `geoip6` are written, and they
  are rebuilt when the selection changes.
- `LearnCircuitBuildTimeout 1` with a 180 second `CircuitBuildTimeout`, appended after the
  template so they win. A circuit whose exit has to come from a chosen country is a much
  narrower draw than a random one, and the shipped template ends with a 20 second timeout and
  learning switched off, which abandoned every attempt before it could finish.

### Using it

- **Ring button** — connect, or reconnect. It is the only thing you need for day to day use.
- **Live speed** — shown in the notification, together with the running totals. Swipe the app
  away and you still see the throughput.
- **Drawer** — location picker, connection mode and everything else, behind the hamburger.
- **ADVANCED** — transport mode, the auto racer list, custom bridges, exit countries, bridge
  counts, an in-app log with per-transport filters, and a manual bridge refresh. The screen is
  a lazy list, so it opens without waiting on the country list or the torrc editor.
- **Log filters** — `ALL`, `VANILLA`, `OBFS4`, `WEBTUNNEL`, `SNOWFLAKE`, `MEMORY`, `DIRECT`
  and `CUSTOM`; `MEMORY` shows what the memory runner was given and whether it updated.

Stopping the VPN and disconnecting are separate actions: stopping tears the tunnel down but
keeps Tor alive, disconnecting starts a fresh race.

## Windows

The Windows client is the same app with the same race engine, state machine and settings,
ported to .NET 8 / WinForms and shipped as a portable zip (`Windows/` in this repo):

- **Official binaries, fetched not committed.** `tor.exe`, `lyrebird.exe` (all five
  transports, including snowflake) and the official `data/geoip` come from the pinned
  Tor Expert Bundle x86_64-15.0.24; `wintun.dll` comes from wintun.net. Both are
  downloaded and SHA-256 verified by `Windows/fetch-binaries.ps1`.
- **Full tunnel** runs `hev-socks5-tunnel.dll` (built in CI with MSYS2 from the vendored
  upstream sources) over a Wintun adapter named `DeltaTor`, with a catch-all default route
  plus per-destination `/32` bypass routes for tor's own traffic and a crash-recovery
  route list. This is the Windows counterpart of Android's `addDisallowedApplication`.
- **Proxy Only** is unchanged: start Tor, race the transports, point a browser at
  `socks5://127.0.0.1:9050`. It needs no elevation.
- **Known limitation:** snowflake does not work through the Windows full tunnel (its
  STUN/DTLS peers cannot be bypassed reliably and the local proxy drops non-DNS UDP);
  use Proxy Only for snowflake on Windows. Everything else works through the tunnel.

## Building

Everything is built by Gradle and the NDK from `Android/`; the toolchain is pinned in the
build files (`compileSdk 36`, `ndk 29.0.14206865`, JDK 17).

```bash
./Android/gradlew -p Android assembleDebug     # per-ABI debug APKs
./Android/gradlew -p Android assembleRelease   # signed per-ABI release APKs + universal
```

Native code lives in `Android/app/src/main/cpp`: `hev-socks5-tunnel` is vendored and built with
`ndk-build`, and `Application.mk` sets `APP_ABI := arm64-v8a armeabi-v7a x86 x86_64`.

Release signing reads the keystore from `ANDROID_KEYSTORE_FILE` plus the
`ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_PASSWORD`, `ANDROID_KEY_ALIAS` and
`ANDROID_KEYSTORE_STORE_TYPE` environment variables. For a local build, drop an ignored
`keystore.properties` at the Gradle root (`Android/keystore.properties`). Never commit a keystore.

The Windows app builds with the .NET SDK 8; the pinned binaries and the native tunnel DLL
must exist first (CI runs these two steps before `dotnet build`):

```powershell
./Windows/fetch-binaries.ps1              # Tor Expert Bundle + wintun, SHA-256 verified
bash Windows/native/build-hev.sh          # from an MSYS2 shell: builds hev-socks5-tunnel.dll
dotnet build Windows/DeltaTor.sln -c Release
```

Release code signing on Windows reads `WINDOWS_CERT_BASE64` plus
`WINDOWS_CERT_PASSWORD`; the signer certificate's SHA-256 is pinned in
`Windows/release-signing-cert.sha256` and the release workflow fails closed until it
matches. Never commit the pfx.

The bridge list files are plain text, one bridge per line, in Tor's own format, and are also
committed under `Android/app/src/main/assets/bridges/` so the app has something to work with before
its first download.

## CI

| Workflow | Trigger | Does |
|---|---|---|
| `android-build.yml` | push to `main`/`android`, manual | debug APKs as an artifact |
| `windows-build.yml` | push to `main`/`test`, PRs, manual | portable Windows zip, sources and native symbols as artifacts |
| `release.yml` | tag `v*`, manual with a tag input | signed APKs, signature verification, GitHub release |
| `windows-release.yml` | tag `v*`, manual with a tag input | signed Windows zip (Authenticode + signer pin), GitHub release |

All workflows install their own SDK/build tools and pinned toolchain, so no runner setup is
needed. One `v*` tag publishes both platforms' assets to the same GitHub release.

## Privacy

- No account, no analytics, no telemetry, no crash reporting.
- The only outbound requests are the bridge list download, the update check, and a single
  one-time request per installation used to count installs.
- All traffic goes through Tor once the tunnel is up.

## License

DeltaTor is licensed under the **MIT License** — see [`LICENSE`](LICENSE).

Third-party components keep their own licenses: Tor and lyrebird are BSD 3-Clause,
`hev-socks5-tunnel` is MIT, and the exact terms of every vendored component are in its own
`LICENSE` file.