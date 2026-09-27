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

`webtunnel` is assembled from two files, `webtunnel.txt` and `webtunnel_ipv6.txt`. They are merged
by fingerprint, alternating between the two so both sources are represented once Tor's per-runner
line cap applies. A bridge that is republished with a newer `ver=` replaces its older twin in
place, because a stale webtunnel protocol version is rejected during the handshake.

Bridge counts in the app are therefore the number of *distinct* bridges, not the number of lines
in the files.

### Exit country

Any number of countries can be selected; the app writes them as `ExitNodes` and enables
`StrictNodes`. The most used countries are offered first. After connecting, the app resolves the
real exit IP through Tor and shows the country and the address, so a bad exit is visible at a
glance.

Note that strict exit nodes make bootstrapping noticeably slower, because Tor has to find a usable
guard in the selected countries.

## Using it

- **Ring button** — connect, or reconnect. It is the only thing you need for day to day use.
- **Live speed** — shown in the notification, together with the running totals. Swipe the app away
  and you still see the throughput.
- **ADVANCED** — exit countries, bridge counts, an in-app log with per-transport filters, and a
  manual bridge refresh.
- **Log filters** — `ALL`, `VANILLA`, `OBFS4`, `WEBTUNNEL` and `MEMORY`; the last one shows what
  the memory runner was given and whether it updated.

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
