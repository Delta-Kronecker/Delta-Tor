# DeltaTor

DeltaTor is a free Tor client for Android and Windows built on one idea: **connecting should
just work, and it should be fast — even on networks that try to block Tor.** Instead of
gambling on a single bridge, it races several connection methods at the same time, it
remembers which bridges actually worked for you, and it runs a speed-tuned Tor core with
multi-path streams.

No account, no registration, no setup: install, tap the ring, you are on Tor.

## What you get

### A connect that actually happens

- **Parallel racing** — up to four full Tor clients start at once, each on its own ports
  with its own bridges: direct (`vanilla`), `obfs4`, `webtunnel`, and a fourth over *the
  bridges that already worked for you*. The first one to reach a working circuit wins, the
  rest are stopped, and the VPN is brought up on the winner. You watch the race live,
  per runner, in the notification.
- **Memory bridges** — every bridge that survived a real connection is stored per
  transport, together with per-bridge success counts, and raced again on the next connect.
  The app gets faster at connecting *on your network*, not in general. A fresh install is
  seeded with a shared list of known-good bridges, so even the very first connect benefits.
- **Big-list modes when the network is hostile** — **Fresh** fires all six 72-hour
  collector bridge lists at once; **Combined** (the default) races every cached list
  together. Bridge order is reshuffled on every attempt, so a bad bridge never poisons two
  connects in a row.
- **Failure never dead-ends** — if a connect fails, the app retries once automatically
  with a conservative fallback configuration; a stalled racer is dropped by a timeout, and
  the next recovery reuses what the last one learned. After a network drop the tunnel is
  recovered in place instead of reconnecting from zero.

### A faster Tor core

The bundled Tor is the [Delta-Kronecker/tor](https://github.com/Delta-Kronecker/tor) fork,
built for speed rather than parity with stock defaults:

- **Conflux multi-path streams** — traffic flows over linked multi-leg circuits; the fork
  exposes knobs stock Tor has no equivalent for (set count, legs per set, RTT-ranked set
  selection), so streams ride the fastest combinations instead of a single path.
- **Bootstrap tuning** — padding off, microdescriptors, zero initial consensus-download
  delays, early directory fetch, more parallel bootstrap tries, and a fixed circuit-build
  timeout instead of Tor's slow self-learning. The directory consensus — normally the
  slowest part of a cold connect — is fetched the moment Tor starts.
- **Circuit economics** — more entry guards and pending circuits, longer circuit reuse,
  and stream timeouts tuned so a slow circuit is abandoned instead of stalling a page.

The stock-like conservative template stays on hand: the automatic failure retry falls back
to it (with Conflux off), and every value is editable in the in-app torrc template editor.

### Eleven ways to connect

`auto` (the race — recommended) · `vanilla` · `obfs4` · `webtunnel` · `snowflake` ·
`dnstt` (DNS-tunneling transport) · `meek_lite` (domain-fronted transport) · `fresh` ·
`combined` · `direct` (no bridge at all) · `custom` (paste your own bridge lines).

**AUTO RACERS** decides which transports take part in `auto` — drop `webtunnel` on a
network without IPv6, for example. At least one stays ticked; the memory runner joins by
itself once it has proven bridges, and **RUN MEMORY** can turn the memory twin off for a
single-transport connect. If a race ends with nothing working and Snowflake was not among
the racers, the app offers to enable it in one tap — Snowflake is often the one transport
that still gets through on heavily filtered networks.

### Everything else

- **Exit country, live** — pick any countries; the app steers even *existing* circuits
  over the control port, so you can change country without reconnecting, and it verifies
  the real exit IP afterwards and shows the country and address.
- **Proxy chain over VPN Gate** — add a second hop: Tor → SSTP → a public VPN Gate server
  → internet, with a country-sorted server list, live latency tests of every server, and
  automatic fallback to the next best server — never to a dead connection.
- **Split tunneling** — per installed app: through the tunnel, or bypass.
- **Proxy-only mode** — no VPN interface: Tor runs and any SOCKS5-capable app points at
  the endpoint shown on screen.
- **Bridges under your control** — lists refresh daily from
  [Delta-Kronecker/Tor-Bridges-Collector](https://github.com/Delta-Kronecker/Tor-Bridges-Collector)
  with a bundled offline copy, per-mode bridge stats, and a zip export of the working
  list to move a known-good setup to another device.
- **Built-in updater** — checks for new releases, downloads in the background with a
  progress banner, and installs by itself; no browser needed.
- **Stays out of your way** — live up/down speed and totals in the notification (even
  with the app swiped away, with disconnect actions right in the notification), a
  per-transport log viewer with filters, and a first-run notice in Persian, English and
  Russian.
- **A portable Windows twin** — the same app, same race engine, same memory and settings,
  with system-proxy and full-tunnel modes and a tray icon, shipped as a zip you unpack
  anywhere.
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

The Windows zip is portable: unpack it anywhere and run `DeltaTor.exe` — all state lives
next to the exe. Full-tunnel mode asks Windows for administrator rights when it starts
(the route table and the TUN adapter need them); the other modes run without elevation.

## Using the app (Android)

- **The ring** is the whole interface: tap to connect, tap again to reconnect. Everything
  else lives behind the hamburger drawer.
- **Notification** shows live down/up speed and running totals, plus disconnect actions —
  including a chain-aware disconnect when a proxy chain is running.
- **Stop vs disconnect**: *Stop VPN* tears the tunnel down but keeps Tor warm, so the next
  connect skips the slow parts; *Disconnect* ends the session and the next connect is a
  fresh race.
- **ADVANCED** holds the connection mode dropdown, the AUTO RACERS list, custom bridge
  input, exit countries, per-mode bridge stats with the zip export, the torrc template
  editor, and the connection log with per-transport filters.
- **Drawer** holds the location picker, the connection mode (VPN / proxy-only), split
  tunneling and the rest.

## Windows

The Windows client is the same app — same race engine, same memory runner, same settings —
ported to .NET 8 / WinForms and shipped as a portable zip:

- **System proxy** (default) — Tor runs and the system points at it; no elevation needed.
- **Full tunnel** — the whole machine routes through Tor over a TUN adapter, with bypass
  routes for Tor's own traffic so the tunnel never loops into itself. Asks for elevation.
- **Tray icon** with live state, actions and update notices; minimize-to-tray.
- **Known limitation:** Snowflake does not work through the Windows full tunnel (its
  STUN/DTLS peers cannot be bypassed reliably); use system-proxy mode for Snowflake.

## Technical notes

Everything below is implementation detail; none of it is needed to use the app.

### How the race works

On connect, up to four Tor clients start in parallel, each on its own port and data
directory with its own `torrc`:

| Runner | Pluggable transport | Bridges |
|---|---|---|
| `vanilla` | none | direct bridges |
| `obfs4` | lyrebird | obfs4 bridges |
| `webtunnel` | lyrebird | webtunnel bridges |
| `memory` | inherited per bridge | the bridges that worked last time |

The first runner to bootstrap to 100% wins, the others are stopped, and the VPN is brought
up on the winner. `fresh` and `combined` are the same machinery with bigger lists: fresh
is all six 72-hour collector lists at once, combined is every cached list except fresh.

### The memory runner

`memory` has no list of its own. Whenever a runner proves a bridge (win or loss — every
bridge a runner provened, including in races it lost, is kept), it is stored per transport
with a success count, in a bounded pool (300 per transport; runners draw 150 lines,
memory takes the newest fifty unshuffled, and re-proven bridges move to the front). The
next connect starts a runner over exactly those bridges. A fresh install is seeded from
`CombinedMemory.txt`, a shared snapshot of known-good bridges with counts. `RUN MEMORY`
turns the memory twin off for single-transport connects.

### The Tor fork

The core is built from [Delta-Kronecker/tor](https://github.com/Delta-Kronecker/tor).
Beyond stock Tor it carries the Conflux management surface — `ConfluxEnabled`,
`ConfluxClientUX`, `ConfluxNumSets`, `ConfluxNumLinkedSets`, `ConfluxNumLegs`,
`ConfluxSetSelection`, `ConfluxSetRttPct` as real torrc options — which the shipped
template uses to keep 32 prebuilt sets of 2 legs each and to pick sets by best RTT. The
speed template also zeroes the bootstrap download delays, fetches directory info early,
raises guards and pending circuits, disables padding and switches to microdescriptors. A
conservative error-fallback template (Conflux off, longer timeouts) is applied
automatically when a connect fails, and every value is editable in the app's torrc
template editor.

### Bridge sources

Lists come from the collector repo and resolve in this order: the cached list on the
device, then the copy bundled inside the APK, then the network. `webtunnel` is assembled
from `webtunnel.txt` and `webtunnel_ipv6.txt`, merged by fingerprint and alternating so
both sources survive the per-runner line cap; a republished bridge with a newer `ver=`
replaces its older twin in place, because a stale webtunnel protocol version fails the
handshake. Bridge counts shown in the app are distinct bridges, not file lines.

### Why exit-country pinning actually works

Three settings, all required together: `StrictNodes 0` (with `1`, Tor refuses every circuit
not exiting through the selected nodes and bootstrap stalls at 50%; with `0` the list is a
strong preference that always allows a circuit to complete); a generated geoip database
(the bundled core ships none — it is built on demand from the APK's own range table, for
the selected countries only, as both `geoip` and `geoip6`); and a circuit-build timeout
that is long enough for country-pinned circuits to finish (the stock-style 20 s timeout
abandoned them). Once bootstrapped, exit countries are re-applied live over the control
port, steering existing circuits without a reconnect.

### The proxy chain engine

Every server probe is a full SSTP negotiation — TCP to Tor's SOCKS bridge, TLS, the SSTP
HTTP request, LCP, auth, IPCP — never a bare TCP connect, because a port that answers but
refuses the hub is not a working server. The chain's live delay is measured with a real
HTTP request bound to the chain's own VPN network. The host list updates itself once a
day in the background, direct first and from behind Tor if the direct route is censored;
opening the sheet with an empty list triggers a fetch, with a retry line if both routes
fail.

### What is inside the apps

On Android, `libtor` (the fork), `libobfs4proxy` (lyrebird) and `hev-socks5-tunnel`
(tun2socks) are compiled into the APK with the NDK — no external Tor install, no companion
app. On Windows, the pinned Tor Expert Bundle binaries and `wintun.dll` are fetched and
SHA-256 verified at build time (`Windows/fetch-binaries.ps1`), never committed, and
`hev-socks5-tunnel.dll` is built in CI from the same vendored sources with MinGW. The
app's own traffic is excluded from its tunnel on both platforms
(`addDisallowedApplication` on Android, per-destination `/32` bypass routes on Windows).

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
