# DeltaTor

DeltaTor is a free Tor client for Android and Windows built on one idea: **connect should
just work, even on networks that try to block Tor.** Instead of gambling on a single bridge,
it races several connection methods at the same time and keeps whichever one actually gets
through on the network you are on.

No account, no registration, no setup: install, tap the ring, you are on Tor.

## What you get

- **A connect button that works** — four Tor clients start in parallel (direct bridges,
  obfs4, webtunnel, and the bridges that worked for you last time); the first one to reach
  a working circuit wins and the rest are stopped. You watch the race live in the
  notification.
- **It remembers what worked** — every bridge that survived a real connection is kept and
  raced first on the next connect, so the app gets faster at connecting *on your network*,
  not in general.
- **Pick your exit country** — select any countries you want the connection to exit from;
  the app verifies the real exit IP after connecting and shows you the country and address,
  so a bad exit is visible at a glance.
- **Proxy chain over VPN Gate** — add a second hop: the app chains your Tor connection
  through a public VPN Gate server over SSTP, with a country-sorted server list, live
  latency tests of every server, and automatic fallback to the fastest working one.
- **Split tunneling** — decide per installed app whether it goes through the tunnel or
  stays on the plain network.
- **Proxy-only mode** — no VPN at all: start Tor and point any SOCKS5-capable app at
  `127.0.0.1:9050`.
- **Everything is bundled** — Tor, the pluggable transports and the tunnel are compiled
  into the app. Nothing to install next to it, and a bridge list ships inside the app so a
  fresh install connects before its first download.
- **Bring your own bridges** — paste custom bridge lines, or export the working bridge
  list as a zip to share with another device.
- **Stays out of your way** — live up/down speed and totals in the notification even when
  the app is swiped away, an in-app log with per-transport filters, and a built-in updater
  that downloads and installs new versions by itself.
- **A portable Windows twin** — the same app, same race engine and same settings on
  Windows 10/11, shipped as a zip you unpack anywhere.
- **No accounts, no analytics, no telemetry, no crash reporting** on the main builds.

## Install

Grab the asset you need from the
[releases page](https://github.com/Delta-Kronecker/Delta-Tor/releases).

| Asset | Use it on | Size |
|---|---|---|
| `deltator-vX.Y.Z-arm64-v8a.apk` | almost every phone and tablet made in the last decade | ~22 MB |
| `deltator-vX.Y.Z-armeabi-v7a.apk` | 32-bit ARM devices | ~22 MB |
| `deltator-vX.Y.Z-universal.apk` | anything else, carries every ABI | ~38 MB |
| `deltator-vX.Y.Z-windows-x64.zip` | Windows 10/11 on 64-bit Intel/AMD | ~40 MB |

Requirements: Android 7.0 (API 24) or newer, or 64-bit Windows 10/11. `x86` Android payloads
only contain the tunnel component, so the app is meant for real ARM devices, not emulators.

The Windows zip is portable: unpack it anywhere and run `DeltaTor.exe`. The full-tunnel
mode asks Windows for administrator rights when it starts (the route table and the Wintun
adapter need them); Proxy Only runs without elevation.

## Using the app (Android)

### Day to day

- **The ring** is the whole interface: tap to connect, tap again to reconnect. Everything
  else lives behind the hamburger drawer.
- **Notification** shows live down/up speed and running totals — swipe the app away and you
  still see the throughput.
- **Stop vs disconnect**: *Stop VPN* tears the tunnel down but keeps Tor warm (next connect
  is fast); *Disconnect* ends the session and the next connect is a fresh race.

### Choosing how to connect (ADVANCED)

- **CONNECT VIA** offers `auto` (the race — recommended), a single transport (`vanilla`,
  `obfs4`, `webtunnel`, `snowflake`), `direct` (no bridge, straight to a guard), or
  `custom` (paste your own bridge lines).
- **AUTO RACERS** decides which transports take part in `auto` — for example drop
  `webtunnel` on a network without IPv6. At least one must stay ticked. The memory runner
  joins automatically once it has bridges that provably worked for you.
- If a race ends with nothing working and Snowflake was not among the racers, the app
  offers to enable it in one tap — Snowflake is often the one transport that still gets
  through on heavily filtered networks.
- The selection applies on the next connect.

### Exit country (drawer)

Select any number of countries; the most-used ones are offered first. After connecting, the
app resolves the actual exit through Tor and shows it, so you can immediately see whether
you got what you asked for.

### The proxy chain (PROXY CHAIN card)

Once Tor is connected, the PROXY CHAIN card opens a country-sorted list of public VPN Gate
servers. Pick one (or run TEST to measure real handshake latency for every server) and your
traffic travels Tor → SSTP → VPN Gate server → internet, changing your exit country and
adding a hop that is not a Tor relay. If the chosen server dies mid-session, the engine
falls back to the next best measured server, then back to plain Tor — never to a dead
connection.

### Split tunneling (drawer)

Every installed app can be set to go through the tunnel or to bypass it. Apps on the bypass
list keep using the normal network while everything else stays on Tor.

### Proxy-only mode (drawer)

For when a VPN interface is not wanted: Tor runs, transports race as usual, and any app
that speaks SOCKS5 points at the endpoint shown on screen. Nothing else on the device is
affected.

### Bridges and updates

- The bridge lists refresh from
  [Delta-Kronecker/Tor-Bridges-Collector](https://github.com/Delta-Kronecker/Tor-Bridges-Collector)
  (cached copy first, bundled copy as the offline fallback), and can be refreshed manually
  from ADVANCED.
- The working bridge list can be **exported as a zip** from ADVANCED to move a known-good
  configuration to another device.
- The built-in updater checks for new releases, downloads in the background and hands the
  APK to the installer — no browser needed.

## Windows

The Windows client is the same app — same race engine, same state machine, same settings —
ported to .NET 8 / WinForms and shipped as a portable zip:

- **Full tunnel** routes the whole machine through Tor over a Wintun adapter, with bypass
  routes for Tor's own traffic so the tunnel never loops into itself. Asks for elevation
  when it starts.
- **Proxy Only** starts Tor and gives you `socks5://127.0.0.1:9050` for any app that
  supports it. No elevation needed.
- **Known limitation:** Snowflake does not work through the Windows full tunnel (its
  STUN/DTLS peers cannot be bypassed reliably); use Proxy Only for Snowflake on Windows.

## Technical notes

Everything below is implementation detail; none of it is needed to use the app.

### How the race works

On connect, up to four Tor clients are started in parallel, each on its own port with its
own `torrc`:

| Runner | Pluggable transport | Bridges |
|---|---|---|
| `vanilla` | none | direct bridges |
| `obfs4` | lyrebird | obfs4 bridges |
| `webtunnel` | lyrebird | webtunnel bridges |
| `memory` | inherited per bridge | the bridges that worked last time |

The first runner to bootstrap to 100% wins, the others are stopped, and the VPN is brought
up on the winner. Progress is reported per runner in the notification; a runner that
stalls is dropped by the bootstrap timeout.

### The memory runner

`memory` has no bridges of its own. Whenever a runner reports `Done` or `Bootstrapped 100%`,
the descriptor Tor printed (`bridge (cached): …` / `bridge (fresh): …`) is read back out of
the log. Those fingerprints are stored per transport, together with every bridge that
already worked for the other transports, in a bounded list (60 per transport); the next
connect starts a runner over exactly those bridges. The first connect therefore runs three
runners; every connect after the first success runs four. A memory bridge that stops
working simply drops out of the race.

### Bridge sources

Lists come from the collector repo above and resolve in this order: the cached list on the
device, then the copy bundled inside the APK, then the network. `webtunnel` is assembled
from `webtunnel.txt` and `webtunnel_ipv6.txt`, merged by fingerprint and alternating so both
sources survive the per-runner line cap; a republished bridge with a newer `ver=` replaces
its older twin in place, because a stale webtunnel protocol version fails the handshake.
Bridge counts shown in the app are distinct bridges, not file lines.

### Why exit-country pinning actually works

Three settings, all required together: `StrictNodes 0` (with `1`, Tor refuses every circuit
not exiting through the selected nodes and bootstrap stalls at 50%; with `0` the list is a
strong preference that always allows a circuit to complete); a generated geoip database
(the bundled Tor binary ships none — it is built on demand from the APK's own range table,
for the selected countries only, as both `geoip` and `geoip6`); and
`LearnCircuitBuildTimeout 1` with a 180 s `CircuitBuildTimeout` appended after the template
so it wins (the shipped 20 s timeout abandoned country-pinned circuits before they could
finish).

### The proxy chain engine

Every server probe is a full SSTP negotiation — TCP to Tor's SOCKS bridge, TLS, the SSTP
HTTP request, LCP, auth, IPCP — never a bare TCP connect, because a port that answers but
refuses the hub is not a working server. The chain's live delay is measured with a real HTTP
request bound to the chain's own VPN network. The host list updates itself once a day in
the background, direct first and from behind Tor if the direct route is censored.

### What is inside the apps

On Android, `libtor` (Tor), `libobfs4proxy` (lyrebird) and `hev-socks5-tunnel` (tun2socks)
are compiled into the APK with the NDK — no external Tor install, no companion app. On
Windows, the official Tor Expert Bundle binaries and `wintun.dll` are fetched and SHA-256
verified at build time (`Windows/fetch-binaries.ps1`), never committed, and
`hev-socks5-tunnel.dll` is built in CI from the same vendored sources. The app's own
traffic is excluded from its tunnel on both platforms (`addDisallowedApplication` on
Android, per-destination `/32` bypass routes on Windows).

### Building

Everything is built by Gradle and the NDK from `Android/`; the toolchain is pinned in the
build files (`compileSdk 36`, `ndk 29.0.14206865`, JDK 17).

```bash
./Android/gradlew -p Android assembleDebug     # per-ABI debug APKs
./Android/gradlew -p Android assembleRelease   # signed per-ABI release APKs + universal
```

Native code lives in `Android/app/src/main/cpp`: `hev-socks5-tunnel` is vendored and built
with `ndk-build`; `Application.mk` sets `APP_ABI := arm64-v8a armeabi-v7a x86 x86_64`.

Release signing reads the keystore from `ANDROID_KEYSTORE_FILE` plus the
`ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_PASSWORD`, `ANDROID_KEY_ALIAS` and
`ANDROID_KEYSTORE_STORE_TYPE` environment variables. For a local build, drop an ignored
`keystore.properties` at the Gradle root (`Android/keystore.properties`). Never commit a
keystore.

The Windows app builds with the .NET SDK 8 after the binaries and the native tunnel DLL
exist:

```powershell
./Windows/fetch-binaries.ps1              # Tor Expert Bundle + wintun, SHA-256 verified
bash Windows/native/build-hev.sh          # from an MSYS2 shell: builds hev-socks5-tunnel.dll
dotnet build Windows/DeltaTor.sln -c Release
```

Windows release code signing reads `WINDOWS_CERT_BASE64` plus `WINDOWS_CERT_PASSWORD`; the
signer certificate's SHA-256 is pinned in `Windows/release-signing-cert.sha256` and the
release workflow fails closed until it matches. Never commit the pfx.

### CI

| Workflow | Trigger | Does |
|---|---|---|
| `android-build.yml` | push to `main`/`android`/`test`/`ad`, manual | debug APK, sources and native symbols as artifacts |
| `windows-build.yml` | push to `main`/`test`, PRs, manual | portable Windows zip, sources and native symbols as artifacts |
| `release.yml` | tag `v*`, manual with a tag input | signed APKs, signature verification, GitHub release |
| `windows-release.yml` | tag `v*`, manual with a tag input | signed Windows zip (Authenticode + signer pin), GitHub release |

All workflows install their own SDK/build tools and pinned toolchain, so no runner setup is
needed. One `v*` tag publishes both platforms' assets to the same GitHub release.

### Privacy

- No account, no analytics, no telemetry, no crash reporting on the main builds.
- The only outbound requests are the bridge list download, the VPN Gate host list, the
  update check, and a single one-time request per installation used to count installs.
- All traffic goes through Tor once the tunnel is up; the app's own requests (bridges,
  updates, host list) go direct, never through the tunnel.
- The ad-supported build (the `ad` branch) additionally includes the Adivery ad SDK, which
  collects advertising identifiers and ad-interaction data under
  [Adivery's privacy policy](https://docs.adivery.com/privacy-policy); the main builds
  carry no such SDK.

## License

DeltaTor is licensed under the **MIT License** — see [`LICENSE`](LICENSE).

Third-party components keep their own licenses: Tor and lyrebird are BSD 3-Clause,
`hev-socks5-tunnel` is MIT, and the exact terms of every vendored component are in its own
`LICENSE` file.
