# DeltaTor for Android

An Android VPN client that runs a real Tor client in-process, picks a working bridge by racing
several pluggable transports at once, and remembers which bridges actually worked.

The whole client is bundled: `libtor` (Tor), `libobfs4proxy` (lyrebird) and `hev-socks5-tunnel`
(tun2socks) are compiled into the APK. There is no external Tor installation and no companion app
to install alongside it.

## Install

Grab the APK that matches your device from the [releases page](https://github.com/Delta-Kronecker/Delta-Tor/releases):

| Asset | Use it on | Size |
|---|---|---|
| `deltator-v2.0.0-arm64-v8a.apk` | almost every phone and tablet made in the last decade | ~22 MB |
| `deltator-v2.0.0-armeabi-v7a.apk` | 32-bit ARM devices | ~22 MB |
| `deltator-v2.0.0-universal.apk` | anything else, carries every ABI | ~38 MB |

`x86` and `x86_64` payloads only contain tun2socks, because Tor and lyrebird are not built for
x86. The app is therefore meant for real ARM devices; x86 emulators will not work.

Requirements: Android 7.0 (API 24) or newer. The APK is signed with the release key, so Android
will ask you to allow installs from your browser the first time.

## How it works

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
brought up on the winner. Progress is reported per runner in the notification, and a runner that
stalls is skipped by the bootstrap timeout.

### The memory runner

`memory` has no bridges of its own. It is populated from what the app learns at runtime:

- Whenever a runner reports `Done` or `Bootstrapped 100%`, the descriptor Tor printed
  (`bridge (cached): …` / `bridge (fresh): …`) is read back out of the log.
- Those fingerprints are stored per transport, together with every bridge that already worked
  for the other transports, in a bounded list (60 per transport).
- The next connect starts a fourth runner over exactly those bridges.

The first connect therefore runs three runners. Every connect after the first one that succeeded
runs four. If a memory bridge stops working it simply drops out of the race and the normal
transports take over.

### Bridge sources

Lists come from [Delta-Kronecker/Tor-Bridges-Collector](https://github.com/Delta-Kronecker/Tor-Bridges-Collector)
and are resolved in this order:

1. the cached list on the device,
2. the copy bundled inside the APK (so a fresh install works before the first download),
3. the network.

`snowflake` comes from the collector's `snowflake.txt`, like the others, with a bundled copy as the
offline fallback.

`webtunnel` is assembled from two files, `webtunnel.txt` and `webtunnel_ipv6.txt`. They are merged
by fingerprint, alternating between the two so both sources are represented once Tor's per-runner
line cap applies. A bridge that is republished with a newer `ver=` replaces its older twin in
place, because a stale webtunnel protocol version is rejected during the handshake.

Bridge counts in the app are therefore the number of *distinct* bridges, not the number of lines
in the files.

### Transport selection

`ADVANCED` has a **CONNECT VIA** dropdown: `auto`, `vanilla`, `obfs4`, `webtunnel`, `snowflake`,
`direct` (no bridge at all, straight to a guard) or `custom`, where you paste your own bridge
lines. Every mode except `auto` runs exactly one transport.

`auto` races several transports at once and keeps the first that reaches 100%. The **AUTO RACERS**
list below the dropdown decides which ones take part, so you can, for example, drop `webtunnel` on
a network without IPv6. At least one must stay ticked. The `memory` runner joins automatically once
it has bridges that provably worked, and it is not part of that list.

The selection is applied on the next connect.

### Exit country

Any number of countries can be selected; the app writes them as `ExitNodes` with `StrictNodes 0`.
The most used countries are offered first. After connecting, the app resolves the real exit IP
through Tor and shows the country and the address, so a bad exit is visible at a glance.

Three things make a pinned exit country actually work, and all three are needed:

- `StrictNodes 0`, not `1`. With `1` Tor refuses to build any circuit that does not exit through
  one of the selected nodes, so when those relays are slow, guarded or unreachable no circuit can
  ever complete and bootstrap stops at 50%. With `0` the list is a strong preference: Tor still
  steers into those countries, but a circuit can always be finished.
- A geoip database. Tor can only resolve `ExitNodes {us}` if it knows which country a relay is in,
  and the bundled Tor binary ships no geoip, so the rule used to match nothing and the selection was
  ignored in silence. The database is generated on demand from the range table the APK already
  carries (`assets/geoip/country.csv.gz`), converted to Tor's `low,high,CC` format, for the selected
  countries only. Both `geoip` and `geoip6` are written, and they are rebuilt when the selection
  changes.
- `LearnCircuitBuildTimeout 1` with a 180 second `CircuitBuildTimeout`, appended after the template
  so they win. A circuit whose exit has to come from a chosen country is a much narrower draw than
  a random one, and the shipped template ends with a 20 second timeout and learning switched off,
  which abandoned every attempt before it could finish.

## Using it

- **Ring button** — connect, or reconnect. It is the only thing you need for day to day use.
- **Live speed** — shown in the notification, together with the running totals. Swipe the app away
  and you still see the throughput.
- **ADVANCED** — transport mode, the auto racer list, custom bridges, exit countries, bridge
  counts, an in-app log with per-transport filters, and a manual bridge refresh. The screen is a
  lazy list, so it opens without waiting on the country list or the torrc editor.
- **Log filters** — `ALL`, `VANILLA`, `OBFS4`, `WEBTUNNEL`, `SNOWFLAKE`, `MEMORY`, `DIRECT` and
  `CUSTOM`; `MEMORY` shows what the memory runner was given and whether it updated.

Stopping the VPN and disconnecting are separate actions: stopping tears the tunnel down but keeps
Tor alive, disconnecting starts a fresh race.

## Building

Everything is built by Gradle and the NDK; the toolchain is pinned in the build files
(`compileSdk 36`, `ndk 29.0.14206865`, JDK 17).

```bash
./gradlew assembleDebug     # per-ABI debug APKs
./gradlew assembleRelease   # signed per-ABI release APKs + universal
```

Native code lives in `app/src/main/cpp`: `hev-socks5-tunnel` is vendored and built with
`ndk-build`, and `Application.mk` sets `APP_ABI := arm64-v8a armeabi-v7a x86 x86_64`.

Release signing reads the keystore from `ANDROID_KEYSTORE_FILE` plus the
`ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_PASSWORD`, `ANDROID_KEY_ALIAS` and
`ANDROID_KEYSTORE_STORE_TYPE` environment variables. For a local build, drop an ignored
`keystore.properties` next to the module instead. Never commit a keystore.

The bridge list files are plain text, one bridge per line, in Tor's own format, and are also
committed under `app/src/main/assets/bridges/` so the app has something to work with before its
first download.

### Official Tor and lyrebird binaries

The Tor core and the pluggable transports in `app/src/main/jniLibs/` come from official upstream
builds, not a fork:

- **`libtor.so` — tor 0.4.9.13**, the official Tor Project Android build shipped by Guardian
  Project as `info.guardianproject:tor-android` (compiled from unmodified Tor). OpenSSL,
  libevent, zlib and zstd are linked in statically; the binary only needs Android's `libc`,
  `libm`, `liblog`, `libdl`, so the app can `exec` it the same way it always has.
  Update with `powershell -File scripts/fetch-official-tor.ps1` — the script downloads the AAR
  from `gpmaven`, extracts `jni/<abi>/libtor.so`, and verifies the pinned SHA-256 below.
- **`libobfs4proxy.so` — official lyrebird** (obfs4 + webtunnel + snowflake transports) built
  straight from `gitlab.torproject.org/tpo/anti-censorship/pluggable-transports/lyrebird`
  (v0.8.1) with `go build` for each ABI. arm64 is a pure-Go static binary; armv7 needs the NDK
  because Go requires cgo there, so the `.github/workflows/build-lyrebird.yml` workflow rebuilds
  both ABIs in CI. Run it after bumping lyrebird and commit the new binaries.

Pinned checksums (`scripts/fetch-official-tor.ps1` for tor, `build-lyrebird.yml` run logs for lyrebird):

| ABI | libtor.so 0.4.9.13 | libobfs4proxy.so lyrebird (v0.8.1) |
|---|---|---|
| arm64-v8a | `59398e39a1332608fc87660b233dddd88e0792fc7b58724338d9df36bac82fb2` | `c17f92e833bb71786a385e57d1afb694e8973e2f1e7b12d49233d145e5b40d6f` |
| armeabi-v7a | `8948b5e5d94f5332c0d5ffa223c06a6614001dc4289cf4d4c63488700445f341` | `0624128f07b5d0a3f5045f1394b5fb9ce8cf73b4a9d111526e5ef699b1ad1a96` |

Because they are official builds, the fork-only `ConfluxEnabled`/`ConfluxClientUX` knobs the old
custom tor added are gone from the torrc template (official Tor would refuse to start on them).

## CI

| Workflow | Trigger | Does |
|---|---|---|
| `android-build.yml` | push to `main`/`android`, manual | debug APKs as an artifact |
| `release.yml` | tag `v*`, manual with a tag input | signed APKs, signature verification, GitHub release |

Both workflows install the SDK, build-tools and the pinned NDK themselves, so no runner setup is
needed.

## Privacy

- No account, no analytics, no telemetry, no crash reporting.
- The only outbound requests are the bridge list download, the update check, and a single
  one-time request per installation used to count installs.
- All traffic goes through Tor once the VPN is up.

## License

Source is provided as is. Tor and lyrebird are BSD 3-Clause, `hev-socks5-tunnel` is MIT; the exact
terms of every vendored component are in its own `LICENSE` file under
`app/src/main/cpp/hev-socks5-tunnel-src/`.
