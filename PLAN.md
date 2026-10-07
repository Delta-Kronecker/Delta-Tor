# PLAN — DeltaTor for Windows (1:1 with the Android app, two documented cuts)

> Method: read every Android file/group → understand its purpose → write its exact
> Windows counterpart into this plan. Nothing may be lost.
> Status legend: `[ ]` not read yet · `[~]` reading · `[x]` read + captured in plan

---

## 0. Summary

- Android: DeltaTor — a Tor client that races several pluggable transports at once.
- Goal: a `Windows/` folder with identical features, look and behavior; use official
  pre-built binaries (Tor Expert Bundle, ...); build & release via GitHub Workflow
  like the Android one.

**Decisions locked (2026-10-07):**
1. **Split tunnelling is CUT in v1** — no per-app routing (WFP dropped), no
   SplitTunnelScreen/card, no `split_tunnel_*` config; Proxy Only mode is the
   selective-routing escape hatch. `DomainRouter` stays as dormant bridge code.
2. **Core = Tor Expert Bundle for Windows x86_64-15.0.24**, fetched by
   `fetch-binaries.ps1` (pinned version + SHA-256), NOT committed to the repo
   (local copy `tor-expert-bundle-windows-x86_64-15.0.24/` = gitignored cache).
   Official URL:
   `https://dist.torproject.org/torbrowser/15.0.24/tor-expert-bundle-windows-x86_64-15.0.24.tar.gz`
3. **snowflake runs through `lyrebird.exe`** — the bundle's `torrc-defaults`/
   `pt_config.json` prove `ClientTransportPlugin snowflake exec lyrebird.exe`;
   no separate snowflake binary, no gomobile lib.
4. **GeoIP = official `data/geoip` + `geoip6` from the bundle** — `GeoIpFile.kt`
   is not ported; generated torrc points at the bundle files.

Overall plan status: **ALL ANDROID FILES READ · PLAN UPDATED FOR DECISIONS 1–4**
(sections 1–6 complete; open items: UI stack confirmation C#/WinForms (.NET 8),
pin SHA-256 values in fetch script, first build)

---

## 1. Android file map (progress)

- [x] build: build.gradle.kts (root+app), settings.gradle.kts, gradle.properties, proguard-rules.pro
- [x] AndroidManifest.xml
- [x] AppState.kt
- [x] DeltaTorApp.kt
- [x] InstallCounter.kt
- [x] ReleaseChecker.kt
- [x] util/AppLog.kt
- [x] ui/DeltaTorTheme.kt + res/ (strings, themes, drawables)
- [x] MainActivity.kt (4093 lines)
- [x] TorVpnService.kt (1734 lines)
- [x] tunnel/ParallelTorManager.kt
- [x] tunnel/TorRunner.kt
- [x] tunnel/TorSocksBridge.kt
- [x] tunnel/SnowflakeBridge.kt
- [x] tunnel/BridgeStore.kt
- [x] tunnel/BridgeMemory.kt
- [x] tunnel/BridgeCountries.kt
- [x] tunnel/BridgeExport.kt
- [x] tunnel/DomainRouter.kt + DomainRoutingMode.kt
- [x] tunnel/ExitNodes.kt / ExitLocator.kt / ExitCapacity.kt / GeoIpFile.kt (GeoIpFile read — NOT ported: official bundle geoip)
- [x] tunnel/HevSocks5Tunnel.kt
- [x] tunnel/InstalledApps.kt — read, NOT ported (split tunnelling cut)
- [x] tunnel/LocalProxyAuth.kt
- [x] tunnel/ProtocolSniffer.kt
- [x] tunnel/TorrcSettings.kt
- [x] cpp/: hev_jni.c, Android.mk, Application.mk
- [x] jniLibs/: prebuilt libtor.so + libobfs4proxy.so (per ABI)
- [x] assets/: bridges/*, geoip/*
- [x] CI: android-build.yml, release.yml

---

## 2. Findings in read order

### [x] 2.1 Build files

**What the Android build does (read: root build.gradle.kts, app/build.gradle.kts,
gradle.properties, proguard-rules.pro, AndroidManifest.xml):**

- Toolchain: AGP 8.13.2, Kotlin 2.2.21 (+ Compose compiler plugin), JDK 17,
  compileSdk 36, minSdk 24, targetSdk 35, NDK 29.0.14206865.
- App id / namespace: `io.deltator`.
- **Version comes from the release tag**, not a hardcoded number:
  `-PdeltatorVersion` or env `DELTATOR_VERSION`, fallback `2.0.0`.
  versionCode = major*10000 + minor*100 + patch (minor/patch must be < 100, enforced).
  Version string must match `^\d+\.\d+(\.\d+)?(-/+pre)?$`.
- Release signing from env vars (`ANDROID_KEYSTORE_FILE`, `ANDROID_KEYSTORE_PASSWORD`,
  `ANDROID_KEY_PASSWORD`, `ANDROID_KEY_ALIAS`, `ANDROID_KEYSTORE_STORE_TYPE`) or a
  local git-ignored `keystore.properties`.
- Release build: resource shrinking + R8 full mode + minify; debug keeps minify off.
- ABI splits: `arm64-v8a`, `armeabi-v7a` + universal APK fallback.
- Native build: ndk-build via `src/main/cpp/Android.mk` (hev-socks5-tunnel).
- Dependencies: `libs/golibs-full.aar` (Snowflake Go gomobile bindings), Compose BOM
  2026.01.01 (ui, material3, material-icons-extended), activity-compose 1.12.4,
  lifecycle 2.10.0, kotlinx-coroutines-android 1.10.2, core-ktx 1.17.0.
- **Tor and lyrebird are PREBUILT binaries committed to the repo**, not compiled
  by CI: `src/main/jniLibs/arm64-v8a/libtor.so` (8.6 MB) +
  `libobfs4proxy.so` (17.0 MB), `src/main/jniLibs/armeabi-v7a/libtor.so`
  (5.4 MB) + `libobfs4proxy.so` (16.3 MB). Only hev-socks5-tunnel is actually
  built (ndk-build from `src/main/cpp`). This is direct precedent for the
  Windows policy of shipping pinned official pre-built `tor.exe`/`lyrebird.exe`.
- proguard: keeps `snowflake.**`, `go.**`, and all native methods.
- Manifest: permissions INTERNET, ACCESS_NETWORK_STATE, FOREGROUND_SERVICE(+SPECIAL_USE),
  POST_NOTIFICATIONS, QUERY_ALL_PACKAGES (split tunnelling app list), WAKE_LOCK.
  Components: `DeltaTorApp` (Application), `MainActivity` (launcher),
  `TorVpnService` (VpnService, foreground type vpn, BIND_VPN_SERVICE),
  FileProvider for bridge-export zip (content:// uri), `allowBackup=false`,
  theme `Theme.DeltaTor`.

**Windows counterpart:**

- Same versioning rule: version derived from the git tag by the release workflow
  (`DELTATOR_VERSION`), fallback `2.0.0`; validate with the same regex; embed the
  version into the exe/metadata and the artifact name (parity with the
  "filename must match the manifest" rule).
- Signing: code-sign the Windows binaries with a cert passed via env vars
  (`WINDOWS_CERT_*` secrets), analogous to the keystore env vars.
- No ABI splits on Windows — but build per-arch packages if we ship x64 (and
  optionally arm64), plus a "universal"/portable zip equivalent.
- Dependencies replaced by the Windows stack chosen in section 3 (UI toolkit +
  bundled official binaries instead of .so files + golibs aar).
- Manifest equivalents: nothing to request at install time on Windows; the app
  still needs to (a) run a service/sync "foreground" equivalent (tray icon +
  tray notification while connected), (b) an "export zip via share" equivalent
  = plain "Save as…" dialog, (c) keep process alive during bootstrap
  (PowerSetRequest/execution state instead of WAKE_LOCK).

### [x] 2.2 Root: AppState, DeltaTorApp, InstallCounter, ReleaseChecker

**AppState.kt** — single shared state store between UI and the VPN service:
- `VpnState`: connecting, torRunning, connected, reconnecting, stopping,
  transports (runner name → bootstrap %), transport, error, notice, tx/rx bytes,
  tx/rx speed, connectedAtMillis, exitCode/exitName/exitIp, socksEndpoint.
- `Notice` (modal, id-based ack) with kinds `AutoRecovery` and `FirstRun`;
  `NoticeBlock(text, rtl, lead)` so a notice can mix LTR/RTL scripts
  (first-run explainer = English + Persian + Russian, each block laid out in its
  own direction).
- `BridgeState`: updating, lastUpdateMillis, counts vanilla/obfs4/webtunnel/
  snowflake/fresh/combined, per-transport `memory` counts, error.
- `ReleaseState`: checking, latestVersion, latestUrl, newer.
- Narrow flows to avoid over-recomposition: `socksEndpoint`, `mode`
  (real mode can differ from the drawer's form after auto-recovery).
- `markStopped()` keeps `stopping`, `error` and `notice` across teardown.

**Windows plan — state store:** identical model in the chosen UI framework:
a single observable `AppState` (same fields, same names, same semantics),
`VpnState`, `BridgeState`, `ReleaseState`, `Notice`/`NoticeBlock` with ids,
narrow `socksEndpoint`/`mode` channels, `markStopped()` preserving
stopping/error/notice.

**DeltaTorApp.kt** — Application startup + `Config` (SharedPreferences):
- On create: `Config.init`, `AppLog.enabled = loggingEnabled`,
  `TorrcSettings.init`, `ExitNodes.init`, create 2 notification channels
  (`vpn status` low importance/no badge, `updates` default/badge),
  `BridgeStore.refreshState`, async: `BridgeStore.autoUpdateIfStale`,
  `ReleaseChecker.check`, `InstallCounter.countInstall`.
- `Config` prefs (store `deltator`): `proxy_port` (default 9050, clamped
  1024..65535), `debug_mode` (false), `logging_enabled` (false),
  `transport_mode` (default **"combined"**, values:
  auto/vanilla/obfs4/webtunnel/snowflake/direct/custom), `first_run_notice_v1`
  (false), `proxy_only_mode` (false), `run_memory` (true),
  `auto_recovery` (false), `split_tunnel_enabled` (false),
  `split_tunnel_mode` (bypass|vpn, default bypass),
  `split_tunnel_selected` (set, with legacy `split_tunnel_excluded` migration),
  `custom_bridges` (text), `auto_transports` (csv; choices
  vanilla/obfs4/webtunnel/snowflake; default = vanilla,obfs4,webtunnel —
  snowflake deliberately off; empty/invalid falls back to defaults).

**Windows plan — startup:** same startup sequence on app launch: init config,
init log flag, init torrc template + exit-node module, "channels" = tray/notification
primitives, `BridgeStore.refreshState` + background auto-update, release check,
install counter. Store config in a single settings file (e.g.
`%APPDATA%\DeltaTor\config.json` or Windows registry) with **the same keys and
defaults** (proxy_port 9050 clamp, logging false, transport_mode "combined",
run_memory true, auto_recovery false, proxy_only false,
auto_transports default vanilla,obfs4,webtunnel). The `split_tunnel_*`
keys are **not ported** (v1 cut, section 3): unknown keys are ignored, the
card/screen do not exist.

**InstallCounter.kt** — one-time install count by GETting a 1-byte release asset
(`https://github.com/Delta-Kronecker/ForInstallationStatistics/releases/download/ForInstallationStatistics/DeltaTorAndroid`);
GitHub counts downloads. Prefs `deltator_install`: `counted_v1`, `attempts_v1`,
max 5 attempts, 8s timeouts, UA `DeltaTor-Android`, flag only on HTTP 2xx/3xx.

**Windows plan:** identical mechanism, UA `DeltaTor-Windows`, own counter asset
name (e.g. `DeltaTorWindows`) so platforms are distinguishable, same prefs keys
in a local state file.

**ReleaseChecker.kt** — GET `https://api.github.com/repos/Delta-Kronecker/Delta-Tor/releases/latest`,
compare tag (strip `v`) vs current version with numeric segment compare
(`compareVersions`), sets `ReleaseState`, and if newer raises a notification
("DeltaTor X.Y.Z available" / "A new release is out — tap to open it on GitHub.",
id 42, channel `updates`, click opens release URL) — repeated on every launch,
no dismiss state. `openInBrowser()` opens URL.

**Windows plan:** same API endpoint + same `compareVersions` + same
`ReleaseState` + an in-app banner (same wording) and a Windows toast/tray
notification with the same title/text that opens the GitHub URL in the default
browser.

### [x] 2.3 AppLog / Theme / res

**util/AppLog.kt** — in-app log ring buffer (replaces logcat):
- `LogEntry(id, raw, level, session, transport)`; `LogSession(id, label,
  startedAtMillis, outcome)` — each connect attempt is a numbered session
  (header `=== connection #N · label ===`, footer with outcome, last 12 kept).
- Buffer cap 4500 lines; **fair trimming**: drop the oldest line of whichever
  transport currently holds the most (so noisy vanilla cannot evict obfs4/
  webtunnel). Transport derived from tag `TorRunner[<name>]` where name ∈
  `vanilla, obfs4, webtunnel, snowflake, memory, direct, custom`.
- `enabled` (from `Config.loggingEnabled`, default OFF) — recording only; Tor
  output is always parsed. `redactSensitive` flag suppresses sensitive tags from
  the in-app buffer (list includes TorSocksBridge, HevSocks5Tunnel,
  DomainRouter, ...).
- Snapshot flow for the UI: `lines` StateFlow refreshed by `flushIfDirty()` on a
  ~100 ms timer only while `observerCount > 0`; timestamps `MM-dd HH:mm:ss.SSS`
  only added when observed. `clear()`, `session()`, `transport()` (stamp a line
  into a specific session/transport), `beginSession/endSession`.

**Windows plan:** port AppLog 1:1 (same cap, same fair-trim algorithm, same
session model/labels/outcomes, same transport list, same enabled/redact flags,
same timestamp format, same flush-on-timer behavior).

**ui/DeltaTorTheme.kt** — the exact palette (dark, borderless, owner-drawn;
comment says colors are copied 1:1 from a former WinForms `TorJetUi.cs` theme):
- `Bg #12141C`, `Surface #1A1D26`, `SurfaceAlt #232732`, `SurfaceLight #2C313E`,
  `Border #2D3241`, `BorderLight #3C4252`, `Text #F5F7FC`, `Muted #78829B`,
  `Accent #8A5CF6`, `AccentLight #B79CFF`, `AccentSoft #6040BE`,
  `AccentDark #4830A0`, `Green #34D399`, `GreenLight #7DF3C0`,
  `GreenDark #269E76`, `Red #EF5C70`, `Amber #F5B23C`, `AmberLight #FFD58A`.
- Material3 dark color scheme mapping: primary=Accent, secondary=Green,
  background=Bg, surface=Surface, surfaceVariant=SurfaceAlt, error=Red,
  outline=Border, outlineVariant=BorderLight, onPrimary/onSurface=Text,
  onSurfaceVariant=Muted, onSecondary=#001611, onError=#FFFFFF.
- Typography (WinForms pt sizes noted in comments): Big 18pt→28sp bold,
  Title 11pt→17sp bold, H2 9.5pt→15sp bold, Body 9.25pt→14sp,
  Small 8pt→12sp, Caption 7.25pt→10sp bold.

**Windows plan:** reuse these **exact hex values and size mapping** — the theme
was originally a Windows theme, so the Windows UI must render it natively:
same background `#12141C`, window background in themes.xml `FF12141C`,
same font-size ladder (18/11/9.5/9.25/8/7.25 pt equivalents).

**res/values/strings.xml**: `app_name = "DeltaTor"`; notification channel names:
`VPN Status` / "Shows current DeltaTor connection status",
`DeltaTor Updates` / "Alerts when a new DeltaTor release is published";
launcher background color `#05091A`.
**res/values/themes.xml**: window bg `#12141C`, transparent status/nav bars.
**res/xml/file_paths.xml**: only `cache/exports/` is shared (bridge export zips).

**res/drawable notification action icons** (3 vectors, 24dp, solid white fill,
**no `android:tint`** so the ROM tints them for light/dark status bars —
comment: they exist because `addAction(0, …)` rows were being dropped on some
ROMs):
- `ic_notification_disconnect.xml` — hollow power symbol (rounded body with
  top face cut out + filled stem bar); shown for the always-present
  **Disconnect** action.
- `ic_notification_stop.xml` — filled rounded square; the **Stop VPN** action
  (paired meaning: engine stays up, device leaves Tor).
- `ic_notification_start.xml` — filled triangle (play, mirror of the stop
  square); the **Start VPN** action.

**Icons/branding:** `res/drawable-*/ic_tor.png` (5 densities, status/small
icon) and `res/mipmap-*/ic_launcher.png` + `ic_launcher_round.png` (5
densities); launcher background color `#05091A`.

**Windows plan:** same three notification actions in the tray menu with the
same glyphs (draw them as .ico variants: hollow power / square / triangle,
monochrome so the shell can tint); ship `DeltaTor.ico` (app + tray, multiple
sizes) derived from `ic_tor`.

**Windows plan:** same app name and same notification wording; window
background #12141C (already in palette); "share via content uri" → plain
file-save dialog for exported zips (same exports folder inside app data).

### [x] 2.4 MainActivity.kt (4094 lines)

**Navigation & entry (MainActivity class):**
- 3 screens with Crossfade: `Main`, `Log`, `SplitTunnel`.
- Primary ring button logic (order matters): `stopping` → ignore;
  `socksEndpoint != ""` (proxy mode) → `ACTION_DISCONNECT`;
  `connecting||connected` → `ACTION_DISCONNECT`; `torRunning` →
  `ACTION_START_VPN`; else ask VPN permission (skipped entirely in
  `proxyOnlyMode`) then `ACTION_CONNECT`.
- Also: `ACTION_STOP_VPN` (stopVpn), `ACTION_DISCONNECT` (disconnect),
  `onUpdateBridges` = `BridgeStore.update`.
- On create: notification permission request (API 33+), `ExitNodes.loadDirectory`,
  first-run notice (flag `first_run_notice_v1` written BEFORE posting; no title;
  3 blocks: Persian RTL with lead "لطفاً در اولین اتصال صبور باشید", English
  lead "Please be patient on your first connection", Russian lead
  "Пожалуйста, будьте терпеливы при первом подключении").

**MainScreen layout (top→bottom):**
1. `AirBackground`: vertical gradient `#1B2030 → Bg #12141C → #0C0E15`; white
   radial key-light top-left (alpha .05, r = 0.55·w at 16%/10%); state-colored
   halo behind ring (alpha .16·pulse, r = 0.58·w, center 50%/50%, pulses only
   while connecting, 2600 ms FastOutSlowIn); accent bloom bottom edge (alpha .07).
2. `Header`: 34dp rounded menu button (hamburger icon, AccentLight) → opens
   drawer; wordmark "DELTA"(Text)+"TOR"(AccentLight), 16sp letterspacing 2.6;
   `StatusChip` pill (9dp dot + label) right; gradient divider below.
   statusLabel: `CONNECTING` / `CONNECTED` / `READY` (torRunning) /
   `OFFLINE` / "" (stopping).
3. `UpdateBanner` (when release.newer), offset y=98dp: green dot +
   "NEW RELEASE v<ver>" + "An update is available · tap anywhere to open on
   GitHub", gradient bg `#2A2140→Surface`, accent border, opens GitHub.
4. `StateBlock` (offset −112dp): big word 30sp bold, letterspacing 2.5, shadow;
   word: `STOPPING`/`ERROR`/`CONNECTING`/`LINK LOST`/`CONNECTED`/`READY`/
   `OFFLINE`; subline 10sp bold: `BOOTSTRAP FAILED` /
   `TUNNEL BOOTSTRAPPING · <peak>%` / `RESTORING THE TUNNEL` /
   `<TRANSPORT> · GATEWAY ACTIVE` / `TOR RUNNING · VPN PAUSED` /
   `YOUR PRIVATE GATEWAY`. Peak = max runner %, reset per connect.
5. `RingButton` (152dp, dead-center): pulsing radial halo (or dim
   BorderLight halo when idle); rotating sweep beam only while connecting
   (0→360°, 9000 ms linear loop); 2dp track ring (BorderLight 35%);
   6dp progress arc (state color, starts top); matte radial disc
   (`Surface→#0F1119`) + 1dp Border ring; power glyph = 9dp arc 310°+280°
   sweep + vertical line; scale "breathe" 1→1.035 (2400 ms) while connecting;
   halo pulse 0.55→1 (2200 ms, rest 0.78) while glow active. Disabled while
   `stopping`.
6. Label under ring (y=+106dp): `CONNECT` / `CANCEL` / `DISCONNECT` /
   `START VPN` / error text (Red) / "" while stopping. BodyLarge, ls 1.4.
7. `BottomPanel` (scrollable):
   - Pills row when connected/torRunning/stopping and NOT proxy mode:
     `STOP VPN`|`START VPN` (left, relabels; dimmed+disabled while busy) and
     `DISCONNECT` (right). In proxy mode: `SOCKS5 host:port` pill (no-op) +
     `DISCONNECT`.
   - StatCards: `SPEED DOWN` (Green, ↓) / `SPEED UP` (Accent, ↑) —
     `fmt/s` when connected else `--`; `DOWNLOADED` / `UPLOADED` totals.
   - InfoPills: `UP TIME` (mm:ss / h:mm:ss) and `EXIT` (flag+name,
     `Locating …` while connected, `--` otherwise).

**Drawer (`ControlDrawer`, bg #14171F, full-screen):**
- Header `CONTROLS` (19sp, ls 2.4) + "Everything here applies on the next
  connect" + close (X) button; gradient divider.
- **LOCATION** section (expandable, "SHOW/HIDE" + chevron rotating 180°):
  - Amber warning card: `USE ONLY WHEN NEEDED` + `CLEAR` (if selection) +
    "Picking a country sends your traffic through a relay there. It can lower
    your speed and make the connection less stable."
  - Row `🌐 Any location · default` (code `--`).
  - "Reading country list …" while empty.
  - Group `COUNTRIES WITH THE MOST EXIT BANDWIDTH` / `TOP 25 OF <n>`
    (`EXIT_PICKER_TOP = 25`) — rows: flag emoji, name, exit share `%`
    (1 decimal), exit count, 2-letter code, round check (AccentLight when
    selected, row bg Accent 12%).
  - Group `REST OF WORLD · MOST HAVE NO EXIT` with `SHOW ALL`/`HIDE`
    (clears selection if a picked country lives there); hidden unless shown
    or holds a selection.
  - If capacity not loaded: group `COUNTRIES` / `EXIT DATA NOT LOADED`.
- Divider, **ADVANCED** section (summary "Transport, bridges, torrc and the
  log") → `AdvancedItems` (one lazy item per card; `AdvancedForm` hoisted so
  edits survive scroll):
  1. **TRANSPORT** card: `CONNECT VIA` dropdown, 9 options:
     Auto, Fresh, Combined-Bridge, Vanilla, obfs4, WebTunnel, Snowflake,
     Direct, Custom — each with a description line below (exact strings in
     code); if mode ∈ TWINNED_MODES an extra note about `<mode>-memory`;
     `AUTO RECOVERY` row (ON/OFF button + long On/Off description);
     `RUN MEMORY` row (ON/OFF button + description).
  2. **AUTO RACERS** card (only when mode==auto): checkboxes for
     vanilla/obfs4/webtunnel/snowflake (at least one; last one shows amber
     "only one left"); footer "Auto starts every ticked transport at once and
     keeps the first that reaches 100%."
  3. **CUSTOM BRIDGES** card (mode==custom): monospace field (4–12 lines,
     placeholder shows snowflake/obfs4 examples), count line
     "No bridges yet — paste at least one line." / "N bridge line(s)…".
  4. **PROXY ONLY** card: title "No VPN Only Proxy" + ToggleSwitch; text
     "Tor will bootstrap normally and listen on a local address. No tunnel is
     created and nothing on this device is routed automatically — you point
     each app at the address yourself."; when on: endpoint in mono 15sp bold
     (`socks5://127.0.0.1:<port>` fallback, Green when live), "Live. Set this
     as SOCKS5…" / "Not running yet…", "Tap to copy this address" (copies
     host:port), trailing note about reconnect to turn off.
  5. **SPLIT TUNNELLING** card: ToggleSwitch (turning on opens the screen);
     status text (Off→green "Every app on this device goes through Tor." /
     on+empty→amber / VPN mode→green "N app(s) go through Tor…" /
     bypass→amber "N app(s) bypass Tor…"); `CHOOSE APPS` row with
     "N PICKED".
  6. **TORRC TEMPLATE** card: `RESET` action; description "A ready-made
     torrc template. Bridges and pluggable transports are appended
     automatically · applied on next connect."; mono field 10–18 lines;
     `SAVE` button (gradient AccentDark→Accent) with "SAVED ✓" for 1.5 s;
     hint "One directive per line · lines starting with # are ignored".
  7. **BRIDGE STORE** card (`BridgeCard`): header + `EXPORT` (share) /
     `UPDATE` buttons or spinner+"UPDATING …"; 2 rows × 3 `StatCell`s:
     VANILLA / OBFS4 / WEBTUNNEL, then SNOWFLAKE / FRESH / COMBINED-BRIDGE —
     each dot+count and "N mem" (GreenLight if >0); footer dot +
     "Updated <rel time> · auto every 24h" or "Update failed · <error>".
  8. **CONNECTION LOG** card: "View connection log" + `VIEW LOG` button;
     "Record the log" ToggleSwitch wired to `Config.loggingEnabled` +
     `AppLog.enabled` with on/off descriptions.
- Footer: `GITHUB` gradient pill → opens REPO_URL
  (`https://github.com/Delta-Kronecker/Delta-Tor`).

**LogScreen:** top bar `CONNECTION LOG` + `COPY`→`COPIED` (green, 1.5 s)
copies all visible rows; recording-off banner ("Recording is off · nothing
here yet" / "…this log stops here"); transport chip row (ALL +
vanilla/obfs4/webtunnel/snowflake/memory/direct/custom with line counts);
severity chip row (ALL/ERRORS/WARNINGS/INFO/DEBUG/VERBOSE = order "EWIDV");
`groupLog()` groups by session → level, session header `CONNECTION #id ·
label · outcome · N lines` (Accent 16% bg), level header (dot + label + count),
rows mono 10sp Muted; reverse layout (newest on top); flush loop 250 ms;
empty states: "Logging is off. Turn it on under ADVANCED · CONNECTION LOG." /
"No log lines captured yet. Start a connection." / "No X lines captured
yet." / "No entries for this level."

**SplitTunnelScreen:** top bar `SPLIT TUNNELLING` + status
(`OFF · ALL VPN` / `ON · NOTHING PICKED` / `ON · n VPN` / `ON · n BYPASS`,
color Muted/Green/Amber); `PICKED APPS GO` label; `SplitModeToggle` with two
named chips `VPN` (Green) / `BYPASS` (Amber) — never a bare checkbox;
`CLEAR PICKS`; search field "Search apps"; app list rows (38dp icon tile,
label, package, 20dp check colored by mode, ✓ glyph), divider between rows;
states: off → "Everything through Tor" + hint; loading → "Reading installed
apps …"; empty → "No apps found." / `Nothing matches "q".`

**Shared helpers/widgets:** `ToggleSwitch` (46×26, knob 22/4, Accent when
on), `GradientPill` (40dp, filled = AccentSoft→Accent gradient, disabled =
Text 40%), `StatCard`, `InfoPill`, `DividerLine` (gradient transparent→Border→
transparent), `SettingsCard` (18dp radius, `#20242F→Surface`), `CardTitle`,
`DrawerRow`, `DrawerGroupHeader`, `CountryRow`, `Spinner` (900 ms arc),
`SeverityChip`, `TransportChip`, `SettingsDropdown` (Surface box, ▲/▼ 9sp),
`NoticeDialog` (22dp radius, `#20242F→Surface`, amber WarnIcon tile, per-block
RTL/LTR layout, bold lead, `OK` Accent button 44dp/13dp), icons drawn on
Canvas (Menu/Close/Warn/Chevron/Back/Check/Arrow/Power), `stateColor`
(amber while connecting/stopping/torRunning, AmberLight reconnecting, Green
connected, else Muted), `formatBytes` (`B/KB/MB/GB/TB`, 1 decimal),
`formatDuration`, `relativeTime` (never/just now/Nm ago/Nh ago/Nd ago),
`flagEmoji` (regional indicators), `Float.format1`.

**Windows plan — UI:** implement a window (desktop) that reproduces this
layout 1:1 with the same palette, strings, sizes and animations: dark
borderless window bg `#12141C`, the same gradient background + glow, the
animated power ring as the centered primary control, same header/chips,
same drawer ("CONTROLS") with LOCATION/ADVANCED sections and **7 cards**
in the same order with identical labels/descriptions (**SPLIT TUNNELLING
card is CUT** — see section 3), same Log screen with
transport+severity filters and session grouping, same
notice dialogs incl. the trilingual first-run explainer with per-block
direction, same update banner/toast. Since the Android theme comments say the
palette was copied 1:1 from a former WinForms theme, target a Windows-native
UI stack that can draw all of this owner-drawn (custom-painted controls, no
default OS widgets).

### [x] 2.5 TorVpnService.kt (1735 lines)

**Service contract:** actions `CONNECT`, `DISCONNECT`, `START_VPN`,
`STOP_VPN`; channels `vpn_status`, `deltator_updates`; notification id 1.
TUN: MTU 1280, address `10.255.255.1/32`, route `0.0.0.0/0`, DNS `8.8.8.8`,
session name "DeltaTor", non-blocking.

**Connect flow:**
1. `connect()` — guards on `stopping`/already started; `markStarted()`;
   `Log.beginSession("connect")`; clears recoveries; resets state;
   `AppState.setMode(Config.transportMode)`; foreground notification
   "Connecting…"; `holdCpu()` (counted PARTIAL_WAKE_LOCK, 10 min backstop,
   held only during bootstrap/rebuild windows); `runConnectFlow()` loop:
   attempt → optional recovery → retry.
2. `runConnectAttempt()`:
   - mode validated against `ParallelTorManager.MODES` (fallback auto);
     auto racer list filtered by `AUTO_SOURCES`; mode label string
     (`"a / b / c / memory"` or `"mode + memory"`); notification
     `"Connecting via <label> …"`.
   - Stall watchdog per attempt: high-water mark of live runner %; if no new
     max for `STALL_RECOVERY_AFTER_MS = 60 s` and < 100%:
     if `autoRecovery` off → just log; if on and mode != auto and not yet
     spent → `ParallelTorManager.stopAll()` + throw `RestartInAuto`.
     Recoveries are one-shot per kind (`recoveriesSpent`).
   - `ParallelTorManager.race(context, basePort=proxyPort, sessionId,
     transportMode, customBridges, autoTransports, runMemory)` with a
     progress callback: publishes `transports` map (failed = −1), updates
     notification with `name=NN%` detail + max progress bar, feeds watchdog.
   - Winner: state → `transports={name:100}`, `transport=name`;
     `awaitPortFree`; `TorSocksBridge.start(torSocksPort=winner,
     listen=127.0.0.1:proxyPort)`; `torRunning = true`.
   - `establishTunnel()`.
3. `applyRecovery(AutoRecovery)`: `Config.transportMode = auto`,
   `AppState.setMode`, clears transports/error, new log session, posts
   notice titled **"DELTATOR CHANGED THE CONNECTION MODE"** with the exact
   body ("…made no progress for a minute…switched the connection mode to
   Auto and started again…Auto is now your connection mode…"), notification
   "Restarting in auto …".
4. `establishTunnel()`:
   - **proxy mode**: no TUN; state `socksEndpoint = socks5://host:port`,
     notification "Proxy ready · <endpoint>", session end
     "proxy ready via <name>", starts only the exit locator (no stats/link
     watch).
   - else: notification "Establishing VPN …"; `establishVpnInterface()`
     (see below); delay 200 ms; `HevSocks5Tunnel.start(tunFd, socks,
     udp=true, mtu, ipv4)`; state connected (clears exit fields);
     notification "Connected via <name> · Tor Network"; starts stats
     polling, link watch, exit locator; session end "connected via <name>".
5. `establishVpnInterface()`: Builder with split-tunnelling semantics —
   self excluded (disallowed) unless VPN-only allow-list is used (then
   self is excluded by omission); BYPASS mode → `addDisallowedApplication`
   per pick; VPN mode → `addAllowedApplication` per pick; always
   `addRoute(0.0.0.0/0)`; each pick resolved+logged (`tun |` section log
   with header: self/device/proxyOnly/splitEnabled/mode/meaning/picks…);
   VPN-only with 0 applied → refuses with a specific reason
   (`establishFailureReason`); `setBlocking(false)`; `establish()`.

**stopVpn()** (Tor stays up): guards; ignores in proxy mode; state
`stopping` + notification "Turning the VPN off …"; `teardownVpn()`
(cancels stats/link/exit jobs, `HevSocks5Tunnel.stop()`, close TUN,
`markStopped()`); then `stopping=false, torRunning=(runner!=null)`;
session end "VPN off · Tor still running"; notification "Tor running ·
VPN off" or full stop if no runner.

**startVpn()**: refuses while stopping/connected/proxy mode; requires
runner ready + bridge running else `fail("Tor is not running. Reconnect.")`;
re-establishes tunnel on the same core.

**Stats polling:** `HevSocks5Tunnel.getStats()` → tx/rx bytes + computed
speeds; republish every 1 s while moving (or reconnecting), else 5 s;
notification text only reposted when it actually changed. Traffic text:
`"DeltaTor — Connected|↑ x/s  ↓ y/s\nTotal: ↑ a  ↓ b"` or
`"DeltaTor — Reconnecting|Restoring the tunnel…"`.

**Link watch + probe:** network callback (only "no active network at all"
counts as lost); probe = SOCKS5 handshake + CONNECT to `1.1.1.1:80` through
`127.0.0.1:proxyPort`, 6 s timeout → `Ok` / `Live` (SOCKS reply ≠ 0x00,
not a failure) / `Dead`. Intervals: urgent 5 s; healthy 20 s → 45 s after
2 quiet probes → 90 s after 5. Rebuild after `PROBES_BEFORE_RECOVERY = 2`
failures AND (`deadFor >= probeGraceMs()` OR `failed >= 6`), where
`probeGraceMs = (CircuitBuildTimeout + SocksTimeout) * 1000 + 5000`
read from the torrc template; 90 s rebuild cooldown; quiet/idle detection
from byte deltas.

**recoverTransport():** restart the SAME runner name via
`ParallelTorManager.restartTransport`, then `TorSocksBridge.repoint`,
state → 100% / reconnecting=false, notification "Reconnected via <name> ·
Ns", restarts exit locator; on failure → `escalateToFullReconnect`
(teardown + full connect). `markReconnecting(reason)` ignored while
stopping.

**Exit locator:** after connect/recovery; if exit countries selected →
wait 5 s then up to 6 tries (8 s timeout, 8 s apart) until located country
matches selection; 1 try when none selected; updates
`exitIp/exitCode/exitName` in state.

**fail(message):** ignored while stopping; log + session end
"failed · message"; state error; stop foreground/self; `markStopped()`;
`teardown()`.

**disconnect():** ignored while stopping / when nothing is up; sets
`stopping`; notification "Stopping" with progress; `teardown()`; holds
STOPPING at least `STOPPING_MIN_MS = 3 s`; session end
"disconnected by user"; stop foreground/self.

**teardownVpn() vs teardown():** vpn-only = stats/link/exit jobs +
tun2socks + TUN close; full = also `TorSocksBridge.stop()` +
`ParallelTorManager.stopAllAndWait(proxyPort)` (waits for process exit +
port release) + `dropCpu()` + `markStopped()`.

**Notification:** title "DeltaTor", text per state, progress bar while
connecting, ongoing/low priority, click → MainActivity; actions context
sensitive: always **Disconnect**; **Stop VPN** when connected;
**Start VPN** when `torRunning && !proxyOnly` (real action icons
`ic_notification_disconnect/stop/start`).

**Windows plan — service equivalent:** a background "engine controller"
(singleton, could live in the app process or a Windows service/tray
process) implementing the exact same state machine and constants:
CONNECT/DISCONNECT/START VPN/STOP VPN commands, stall watchdog 60 s
high-water mark, one-shot AutoRecovery notice with the same title/body,
same probe algorithm (SOCKS5 CONNECT probe, 6 s timeout, Ok/Live/Dead,
2-failure + grace rule, grace = CircuitBuildTimeout+SocksTimeout+5 s,
90 s cooldown, escalating intervals 5/20/45/90 s), same stats cadence
1 s/5 s with change-only repaint, same exit-locator retry policy
(5 s delay + 6 tries when a selection exists), same STOPPING ≥3 s hold,
same teardown ordering (bridge → cores → wait ports free), same
notification texts/actions (as Windows toast/tray tooltip menu:
Disconnect / Stop VPN / Start VPN). **Split tunnelling is NOT ported in
v1** (see section 3): no per-app routing, the engine always establishes
the full tunnel; `proxy-only` mode remains the selective-routing escape
hatch. Wake lock is a no-op on
desktop (optionally execution-state APIs). TUN/tun2socks → see
section 3 architecture.

### [x] 2.6a ParallelTorManager.kt (928 lines) + TorRunner.kt (725 lines)

**ParallelTorManager — the race engine:**
- Runner/mode names: `vanilla`, `obfs4`, `webtunnel`, `snowflake`, `fresh`,
  `combined`, `direct`, `memory`, `auto`, `custom`; memory twins are
  `<transport>-memory` (`MEMORY_SUFFIX`), `memoryNameFor()`,
  `isMemoryRunner()`, `baseTransportOf()`.
- `MODES` (selectable): auto, fresh, combined, vanilla, obfs4, webtunnel,
  snowflake, direct, custom. `AUTO_SOURCES`: vanilla, obfs4, webtunnel,
  snowflake (auto fallback = all four). `COMBINED_SOURCES`: vanilla, obfs4,
  webtunnel (NOT fresh/snowflake). `TWINNED_MODES` = BRIDGE_SOURCES keys +
  combined.
- Bridge sources (base URL `https://raw.githubusercontent.com/Delta-Kronecker/
  Tor-Bridges-Collector/refs/heads/main/bridge`): vanilla.txt, obfs4.txt,
  webtunnel.txt+webtunnel_ipv6.txt, snowflake.txt, fresh =
  {webtunnel,vanilla,obfs4}_{72h} + {obfs4,webtunnel,vanilla}_ipv6_72h (6
  files). Bundled copies in `assets/bridges/` (same file names).
- Constants: `RACE_TIMEOUT_MS` 30 min, `RECOVERY_TIMEOUT_MS` 120 s,
  poll 1 s, port-free timeout 15 s / poll 250 ms, `MAX_PORT_OFFSET` 15,
  `MEMORY_RUNNER_LINES` 50 (newest, never shuffled).
- **Port map** (function of basePort only): vanilla+1, obfs4+2, webtunnel+3,
  memory+4, snowflake+5, custom+6, direct+7, vanilla-memory+8,
  obfs4-memory+9, webtunnel-memory+10, snowflake-memory+11, fresh+12,
  fresh-memory+13, combined+14, combined-memory+15. basePort itself is
  reserved for the app's SOCKS bridge.
- `race(...)`: stopAll → validate mode (unknown → auto) → parse custom lines
  (reject empty custom) → resolve only needed lists (cache → bundled →
  network) → assemble `combined` by `mergeBridgeLists` → build runner plans:
  mode's runner(s) + mixed `memory` runner (auto with >1 racer + runMemory)
  or `<mode>-memory` twin (single/twinned mode + runMemory); shuffle every
  non-memory plan per attempt (memory keeps pool order, newest 50) →
  remember lastPlans/lastPorts → beginRunners (generation bump) → start each
  `TorRunner` (failure → `runner.failed`) → poll loop: progress callback,
  detect dead processes (`failureSummary`), **bank proven bridges into
  BridgeMemory as soon as each runner hits 100%**, first-ready wins → stop
  losers; all-failed → throw `All transports failed (name=reason, …)` after
  dumping last 12 log lines per runner; timeout → throw; finally records
  memory from every observed runner (proof survives failure/timeout/cancel).
- `restartTransport(...)` (recovery): uses cached lastPlans/lastPorts on the
  SAME ports; re-shuffles (memory keeps order); re-checks the twin fresh;
  twin falls back to its reserved port if never raced; merges new plans into
  lastPlans/lastPorts; stopAll + awaitPortFree for each port; 120 s deadline;
  same winner/memory logic; logs `*** RECOVERED ***`.
- `stopAll()` / `detachRunners()` (generation++), `checkGeneration()` (throws
  "Start was superseded by a stop or a newer connect"), `awaitPortFree`
  (bind test with reuseAddress), `stopAllAndWait(basePort)` (ports
  basePort..+15, logs confirmed-dead counts, returns all-free).
- `fetchBridgeLines`: per transport — disk cache (`BridgeStore.lines`) →
  bundled asset (merged, then saved) → download (merged, saved if non-blank);
  then `BridgeStore.refreshState`.
- `mergeBridgeLists(bodies)`: drop blanks/comments; fingerprint-keyed
  LinkedHashMap; round-robin across sources by index; a re-published
  webtunnel line with a newer `ver=x.y.z` replaces the kept one in place
  (`newerWebtunnelVersion` compares triples); single list passes through.
- Download: 20 s timeouts, UA `DeltaTor/1.0`, HTTP 200 only.

**TorRunner — one Tor instance per transport:**
- Owns `filesDir/tor_data_<name>/` (+ `pt_state/`), Tor process, lyrebird
  process, SOCKS port, optional control port.
- `start()`: reset state; parse/clean lines (strip `bridge ` prefix, drop
  malformed via `isValidBridgeLine` — needs `addr:port` + 40-hex
  fingerprint at the right token index), cap **`MAX_BRIDGE_LINES = 150`**
  (taken from the shuffled list); detect PT names from first token among
  `{obfs4, webtunnel, snowflake, meek_lite, meek}` (vanilla/direct → none);
  start lyrebird when needed; **drop lines for transports lyrebird cannot
  serve** (only fail if nothing usable remains); `prepareGeoIp()`;
  `writeTorrc()`; run `<nativeLibraryDir>/libtor.so -f torrc` with
  `HOME=dataDir`, merged stderr; reader thread parses:
  - `Bootstrapped N%` → bootstrapPercent, ≥100 → `ready` + `applyExitNodesLater()`;
  - `bridge descriptor … (cached): $FP` / `(fresh): $FP` → record
    healthyBridge fingerprint (exactly 40 hex, lowercased) — this feeds
    BridgeMemory;
  - every line to `recentLog` ring (60) + AppLog at D level.
- **lyrebird (managed PT):** env `TOR_PT_MANAGED_TRANSPORT_VER=1`,
  `TOR_PT_CLIENT_TRANSPORTS=<list>`, `TOR_PT_STATE_LOCATION=pt_state/`,
  `TOR_PT_EXIT_ON_STDIN_CLOSE=1`; parse stdout: `VERSION`, `CMETHOD m … addr`,
  `CMETHOD-ERROR`, `CMETHODS DONE`, `ENV-ERROR`, `VERSION-ERROR`; wait ≤10 s
  for CMETHODS DONE; stderr → log. Binary = `nativeLibraryDir/libobfs4proxy.so`.
- **torrc written per runner:**
  `SocksPort 127.0.0.1:<port>`, `DataDirectory`, `UseBridges 0|1`,
  `GeoIPFile`/`GeoIPv6File` (if built), `Log info stdout`,
  `KeepalivePeriod 30`, `ClientUseIPv4 1`, `ClientUseIPv6 1`,
  `ClientPreferIPv6ORPort auto`, `DormantClientTimeout 2419200`,
  `ClientBootstrapConsensusAuthorityDownloadInitialDelay 0`,
  `ReducedConnectionPadding 0` → **then the user's template lines**
  (`TorrcSettings.templateLines()`) → `ClientTransportPlugin <t> socks5 <addr>`
  per CMETHOD → `Bridge <line>` per line → `ControlPort 127.0.0.1:<ephemeral>`
  only when exit countries selected. Also copied to `filesDir/tor_last.torrc`.
- **Exit steering AFTER bootstrap** via control port: `AUTHENTICATE`,
  `SETCONF ExitNodes="{cc},…"` (mandatory), `SETCONF StrictNodes=0`
  (mandatory), optional `MaxCircuitDirtiness=600`, `ConfluxEnabled=0`,
  `CircuitBuildTimeout=180`, `LearnCircuitBuildTimeout=1`,
  `SIGNAL NEWNYM`, then `GETCONF ExitNodes` + `GETCONF StrictNodes`;
  4 s timeouts; ISO-8859-1; replies parsed for `250` (supports `250-`
  continuation and `250+` data blocks); runs on a daemon thread with a
  latch (`awaitExitApplied`).
- **GeoIP:** `GeoIpFile.ensure()` per runner when countries selected,
  `discard()` otherwise (bundled binary ships none — see GeoIpFile section).
- `stop()`: SIGTERM → wait 2 s → destroyForcibly → wait 2 s, per process;
  sets `confirmedStopped`; clears CMETHODs.
- `failureSummary()`: prefers the last error-ish line from recentLog, plus
  exit code. `healthyBridges()` set for memory recording.

**Windows plan:** port both classes essentially as-is (they are pure JVM
logic): same names/constants/timeouts/port map/shuffle rules/memory
banking/merge algorithm/webtunnel ver comparison/150-line cap/150+50 pool
rules/1 s poll/30 min race timeout/120 s recovery timeout/generation
guard/port-free waits. Differences only at the process boundary:
- Tor binary = official `tor.exe` (Tor Expert Bundle) instead of libtor.so;
- lyrebird = official `lyrebird.exe` (obfs4proxy build) with the same
  managed-PT env vars and CMETHOD protocol (identical on Windows);
- data dirs under `%LOCALAPPDATA%\DeltaTor\tor_data_<name>` (or app dir),
  `HOME` env set likewise;
- `nativeLibraryDir/lib*.so` paths → `bin\tor.exe`, `bin\lyrebird.exe`;
- control-port exit steering, torrc template composition, geoip
  preparation, bridge descriptor parsing, failure summaries: unchanged.
- Snowflake on Android comes from golibs aar; on Windows it needs **no
  separate binary — lyrebird.exe serves the snowflake transport too**
  (verified from the Expert Bundle's `torrc-defaults` and `pt_config.json`;
  see SnowflakeBridge section). TorRunner's lyrebird path already handles it.

### [x] 2.6b TorSocksBridge.kt (805 lines) + SnowflakeBridge.kt (754 lines)

**TorSocksBridge — the app-facing SOCKS5 bridge (single public entry):**
- One `object`, state: `running`, `listenAddr` (user-facing
  `127.0.0.1:proxyPort`), `upstreamAddr` (winning runner's Tor SOCKS port),
  `bindRetries`, `debugLogging` flag, thread pool.
- `start(torSocksPort, listenHost, listenPort, ...)`: stops any previous
  instance, binds a `ServerSocket` on the listen address with up to
  `BIND_MAX_RETRIES = 10` attempts (interval `BIND_RETRY_DELAY_MS`), and
  an **acceptor thread** loop; each accepted client gets its **own daemon
  thread** (tracked in a `CopyOnWriteArrayList`, pruned when dead);
  DNS queries are the only pooled work: a fixed **8-thread
  `dnsExecutor`** (recreated on start).
- Protocol handled: SOCKS5 no-auth only (methods `0x00`; `LocalProxyAuth`
  may require RFC 1929 first), **CONNECT (TCP)** and **FWD_UDP (cmd 0x05)**:
  FWD_UDP to port 53 is re-sent as DNS-over-TCP through Tor's SOCKS5
  CONNECT (resolver:53); FWD_UDP to any other port is **dropped silently**
  so the browser falls back to plain TCP CONNECT. No UDP ASSOCIATE.
- TCP CONNECT: parse target (IPv4/IPv6/domain), open socket to
  **upstream Tor SOCKS5** (`upstreamAddr`), do a full client-side SOCKS5
  handshake with it (auth none, CONNECT, reply code checked), then splice
  bytes both directions with two pump threads, 32768-byte buffers, idle
  close on either side finishing.
- **DNS pool**: `DnsForwarder` — 8 threads, resolves domain targets by
  sending the DNS query **through Tor** (SOCKS5 CONNECT to target :53 then
  raw DNS-over-TCP framing with 2-byte length prefix), 5-minute positive
  cache (per-name), failover across upstream paths; `forwardDnsTcp` used
  when the app sends `DNS`-looking traffic.
- Domain routing hook: `DomainRouter` consulted before connecting — if the
  domain is on the local-direct list, the socket goes direct (bypasses
  Tor); otherwise through upstream. (Split-tunnelling by domain.)
- `repoint(newUpstream)`: swaps the upstream addr without dropping the
  listener (used by `recoverTransport()`); `stop()` closes listener +
  kills active pumps; `isRunning()`, `endpoint()`.
- `ProtocolSniffer` used in `debugLogging` mode to tag log lines.
- Errors: never crashes the accept loop; per-connection failures logged at
  D/W; client socket failures just close that client.

**Windows plan:** port 1:1 as a pure-JVM (or C#) SOCKS5 server — it is
network code with no Android dependency: same listen endpoint
`127.0.0.1:<proxy_port>`, same SOCKS5 subset (no-auth, CONNECT, domain/
IPv4/IPv6), same 32768 buffers + two-way pump threads, same 8-thread DNS
pool with 5-min cache and DNS-over-TCP through the upstream Tor SOCKS,
same `repoint()` semantics for recovery, same DomainRouter bypass hook,
same debug sniffer flag. Upstream = whichever runner won (basePort+N).

**SnowflakeBridge — legacy single-Tor launcher (still present, used only
for standalone/lower-level starts):**
- One `object` managing three processes: Snowflake PT (Go lib), lyrebird,
  Tor.
- Hard-coded config (must be copied verbatim):
  - Broker `https://1098762253.rsc.cdn77.org/`, front `www.cdn77.com`
  - AMP: broker `https://snowflake-broker.torproject.net/`, front
    `www.google.com`, cache `https://cdn.ampproject.org/`
  - 14 non-Google STUN URLs (3478/443/10000), uTLS id
    `hellorandomizedalpn`, bridge fingerprint
    `2B280B23E1107BB62ABFC40DDCC8824814F80A72`, snowflake bridge line
    `snowflake 192.0.2.3:80 <FP>`.
- `startClient(...)` mode strings: `""` (built-in snowflake), `DIRECT`,
  `SNOWFLAKE_AMP`, `SMART`, or bridge lines → transport auto-detect
  (`obfs4`/`webtunnel`/`meek_lite`/`snowflake`, unknown → `obfs4`).
- Starts Snowflake Go PT (`Snowflake.newClient`, port = proxyPort+2) when
  built-in; else launches lyrebird as managed PT (same env + CMETHOD
  parsing as TorRunner, 10 s CMETHODS DONE wait) and feeds the resulting
  `socks5 host:port` CMETHODs into torrc; optional `TOR_PT_PROXY` from
  `upstreamSocksAddr`.
- Writes its own torrc (common block: SocksPort, DataDirectory, UseBridges
  1/0, GeoIP if present, `Log info stdout`, CircuitBuildTimeout 60/120 for
  slow transports, LearnCircuitBuildTimeout 0, KeepalivePeriod 30,
  NumEntryGuards 1, ClientUseIPv4/IPv6, ClientPreferIPv6ORPort auto,
  SafeLogging 0, AvoidDiskWrites 1, DormantClientTimeout 2419200,
  ClientBootstrapConsensusAuthorityDownloadInitialDelay 0,
  ConnectionPadding 1, ReducedConnectionPadding 0, Socks5Proxy only when
  no ClientTransportPlugin — Tor rejects the combination) + transport
  lines (`ClientTransportPlugin … socks5` + `Bridge …`); `DIRECT` omits
  UseBridges.
- Runs `libtor.so -f torrc` with `HOME=tor_data`, reads `Bootstrapped N%`
  → `torBootstrapProgress`/`isTorReady`; clears `state`/`lock` first,
  keeps descriptor caches; extracts geoip/geoip6 from assets.
- `stopClient()`: Tor destroy→destroyForcibly, snowflake stop, lyrebird
  stdin-close→destroy→destroyForcibly; `isRunning()`/`isClientHealthy()`.

**Windows plan:** the race path (ParallelTorManager/TorRunner) is primary;
port SnowflakeBridge too for parity (proxy/manual starts): same constants
(broker/front/STUN/uTLS/fingerprint/AMP trio — verify still current),
same mode-string dispatch and transport auto-detect, same managed-lyrebird
launch, same torrc generation incl. the Socks5Proxy-without-PT rule, same
bootstrap parsing. **Snowflake on Windows = lyrebird** (verified: the Tor
Expert Bundle 15.0.24 ships
`ClientTransportPlugin snowflake exec lyrebird.exe` in `data/torrc-defaults`
and the same mapping in `tor/pluggable_transports/pt_config.json`, and
`lyrebird.exe` contains the snowflake code): no `snowflake-client.exe`,
no gomobile Go lib. The built-in zero-config / AMP / SMART modes become a
synthetic bridge line handed to lyrebird's managed-PT process —
`snowflake 192.0.2.3:80 <BRIDGE_FINGERPRINT>` plus the same
`url= fronts= ice= utls-imitate=` parameters the Android constants carry
(`BROKER_URL`, `FRONT_DOMAINS`, `STUN_URLS`, `UTLS_CLIENT_ID`); for a
custom snowflake line the parameters come from the line itself, exactly
as on Android. Everything else (mode dispatch, transport auto-detect,
managed-lyrebird launch, torrc rules) ports unchanged.

### [x] 2.6c BridgeStore.kt (207 lines) + BridgeMemory.kt (234 lines)

**BridgeStore — disk cache + daily auto-update of the bridge lists:**
- Cache dir `filesDir/bridges/<name>.txt` (one file per source name from
  `ParallelTorManager.BRIDGE_SOURCES`: vanilla, obfs4, webtunnel, snowflake,
  fresh).
- Atomic save: write `<name>.txt.tmp` then rename (fallback direct write).
- Prefs key `bridges_last_update_v4` (version-suffixed — bump invalidates
  old caches); `UPDATE_INTERVAL_MS` = 24 h; download timeouts 20 s/20 s,
  UA `DeltaTor/1.0`, HTTP 200 only, empty bodies skipped with a warning.
- `updateInternal`: per source, download all URLs → merge via
  `ParallelTorManager.mergeBridgeLists` → save → bump timestamp if ≥1 saved.
- `stats()`: non-blank non-`#` line count per source; `combinedCount()` =
  sum of `COMBINED_SOURCES` counts (each counted once).
- `shouldAutoUpdate()`: any source empty OR last update 0 OR ≥24 h old.
- `refreshState()`: pushes counts (vanilla/obfs4/webtunnel/snowflake/fresh/
  combined + `BridgeMemory.countsByTransport` as `memory`) into
  `AppState.BridgeState` with `updating=false`; `refreshMemory()`: memory
  counts only (used right after a connect proves bridges).
- `update()`: single-flight (`updateInProgress` guard), sets
  `updating=true`, runs on an IO `CoroutineScope`, on failure stores the
  error message in `AppState`; `autoUpdateIfStale()`: best-effort wrapper.

**Windows plan:** port 1:1 — same cache folder
(`%LOCALAPPDATA%\DeltaTor\bridges\<name>.txt`), same `_v4` timestamp key
and 24 h rule, same atomic tmp+rename, same UA/timeouts, same merge via
the shared `mergeBridgeLists`, same `refreshState`/`refreshMemory` split,
same single-flight background update wired to the identical `BridgeState`
fields (so the BRIDGE STORE card shows the same numbers/labels).

**BridgeMemory — proven-bridge pool ("memory" runners):**
- Prefs store `deltator_bridge_memory`, keys `healthy_<transport>` =
  comma-separated lowercase fingerprints in proof order.
- `TRANSPORTS` pools: vanilla, obfs4, webtunnel, snowflake, fresh,
  combined, memory. `MIXED_TRANSPORTS` = {fresh, combined} (their lines
  are not filtered by prefix when building a twin's list).
- `MAX_PER_TRANSPORT = 300` (two attempts' worth, TorRunner cap is 150);
  `remember()` merges newly proven fingerprints **at the front** (order of
  proof), de-dupes within the call, keeps 300, returns count of new ones;
  `healthy()`/`count()`/`countsByTransport()`/`countAll()`.
- `clear()`: sweeps **all** keys with prefix `healthy_` (covers `custom`
  pools that are written but never counted).
- `bridgeLinesFor(sources, transport?)`: intersects the remembered
  fingerprint order with source-list lines; with `transport` set, both the
  pool and the lines are restricted to that transport (twin runner) and
  non-matching prefixes are dropped (vanilla = line WITHOUT a PT prefix);
  without it, all pools merged (auto's mixed memory runner). Returns null
  if nothing proven → runner skipped.
- `fingerprintOf(line)`: token index 2 for PT-prefixed lines
  (obfs4/webtunnel/snowflake/meek_lite/meek), index 1 for vanilla; must be
  exactly 40 hex chars (same rule as TorRunner's `isValidBridgeLine`).
- Fed by ParallelTorManager banking `healthyBridge` fingerprints at 100 %.

**Windows plan:** port 1:1 — same pool names, 300 cap, proof-order
front-insert, prefix sweep clear, mixed-transport rules, fingerprint
extraction (40-hex, index 1/2), `bridgeLinesFor` ordering; store in the
same local state file (e.g.
`%LOCALAPPDATA%\DeltaTor\bridge_memory.json`, key-per-transport lists).



### [x] 2.6d BridgeCountries.kt (163) + BridgeExport.kt (156) + DomainRouter.kt (262) + DomainRoutingMode.kt (17)

**BridgeCountries — exit-country picker data + offline IP→country:**
- Bundled db-ip country-lite dataset (CC BY 4.0): asset
  `geoip/country.csv.gz` (gzip, falls back to plain `country.csv`) — rows
  `start,end,CC` (dotted quads); loaded once per process into parallel
  `LongArray` starts/ends + code index, **sorted by start** for binary
  search (`CountryDb.country(ip)`).
- Country names: asset `geoip/countries.tsv` (`CC\tName`, UTF-8), cached.
- `countryInfo(ip)`: strip `:port`, `ipv4ToLong`, binary search → (code,
  name) — offline fallback for the exit locator.
- `topSync()`: all countries from the name table, alphabetical by name —
  the raw picker list (the "TOP 25"/capacity sorting comes from
  ExitCapacity on top of this).
- `ExitCountry(code, name)` data class.

**Windows plan:** ship the same two assets (`geoip/country.csv.gz|csv`,
`geoip/countries.tsv`) in the Windows build and port `CountryDb` (binary
search over sorted ranges) + `topSync`/`countryInfo` unchanged.

**BridgeExport — zip export of everything the app holds:**
- Output `cache/exports/deltator-bridges-yyyyMMdd-HHmmss.zip`; previous
  exports in that dir are deleted first (never accumulates).
- `bridges/` entries (in order): vanilla, obfs4, webtunnel, snowflake,
  fresh, combined — combined is **rebuilt at export time** via
  `mergeBridgeLists(COMBINED_SOURCES)` (it has no cache of its own);
  blank lines filtered, one file per list, "as used by Tor".
- `memory/` entries: same 6 + `auto-mixed.txt` (transport `memory`) —
  remembered fingerprints are resolved back into full bridge lines via
  `BridgeMemory.bridgeLinesFor(cached)`; pools whose lines vanished from
  the lists are dropped; log shows `N of M remembered`.
- Always adds `README.txt` (exact wording in file: title, explanation of
  bridges/ vs memory/, file list with counts, "Any bridges/*.txt file can
  be added to Tor Browser as-is.", total count).
- Returns null (and deletes the zip) when nothing was written.
- `share()`: FileProvider content uri → `ACTION_SEND` chooser
  (type `application/zip`, subject "Delta Tor bridges", title
  "Export bridges") — Android share sheet.

**Windows plan:** identical zip layout/name stamp/README (byte-for-byte
same text), same null-when-empty rule; "share sheet" → Windows
Save-File/Explorer dialog defaulting to e.g. Desktop (plus optional
"Open containing folder"). Combined rebuilt the same way at export time.

**DomainRouter + DomainRoutingMode — split routing decisions:**
- `DomainRoutingMode`: `BYPASS` (listed domains go direct) / `ONLY_VPN`
  (only listed go through Tor), `fromValue` fallback BYPASS; stored value
  strings `bypass`/`only_vpn` (matches Config's split_tunnel_mode).
- `GeoBypassCountry` enum: IR/CN/RU (`fromCode` default IR).
- `GeoBypassData`: sorted CIDR `starts[]/ends[]` + domestic `domains` set
  loaded from assets `geo/<cc>.cidr` (one CIDR per line, `parseCidr`
  → start/end via mask) and `geo/<cc>.domains` (lowercase, one per line);
  `EMPTY` singleton.
- `DomainRouter(enabled, mode, domains, geoBypassEnabled, geoBypass)`:
  `shouldBypass(host)` checks in order — (1) domain list (exact or
  `.suffix` match, both sides lowercased/trailing-dot trimmed) under
  BYPASS/ONLY_VPN semantics; (2) geo-bypass domestic domain suffix match
  (supports `.ir` TLD-style rules) for non-IP hosts; (3) geo-bypass CIDR
  binary search for IPv4 literals. `DISABLED` constant;
  `isIpAddress()` (4-octet dotted regex or any `:`);
  `createDirectConnection(host, port, timeout=10 s)` plain `Socket` with
  `tcpNoDelay` (on Android it goes direct because the app is excluded
  from the VPN; comment references `addDisallowedApplication`).

**Windows plan (v1):** port `shouldBypass`/`parseCidr`/`ipInRanges`/domain
matchers as library code (pure JVM logic, cheap), but the feature ships
**dormant**: `DomainRouter.DISABLED` remains the default, no UI exposes
it, and the `geo/` assets stay unshipped (they do not exist on Android
either — the loader fails soft). Reasons: (1) per-app/geo split routing
is the cut feature (section 3); (2) with a full-tunnel Wintun adapter a
"direct" socket from the bridge process would follow the route table back
into the tunnel, so a correct bypass needs explicit route handling that
belongs with the split-tunnelling work, not before it. The hook inside
TorSocksBridge stays wired exactly as on Android so enabling it later is
a config/UI change only. The `DomainRoutingMode` values (`bypass`/
`only_vpn`) remain part of the config schema for future use.



### [x] 2.6e ExitNodes / ExitLocator / ExitCapacity / GeoIpFile

**ExitNodes.kt (129) — user-selected exit countries:**
- Prefs `deltator`: `exit_ccs` (csv of 2-letter codes, order = selection
  order) + `exit_names` (parallel csv); both validated on load
  (uppercase, length 2, A–Z, distinct).
- StateFlows `codes`, `names`, `isSelected`, `toggle(code,name)` (keeps
  order), `clear()` (removes both keys), `currentCodes()` sync read for
  torrc generation.
- `directory: StateFlow<List<ExitCountry>>` = `combine(_all,
  ExitCapacityIndex.byCountry) → order()`; `order()` = countries with
  exits sorted by `weight` desc first, then the rest alphabetically; with
  no capacity data just alphabetical.
- `loadDirectory()`: one-shot (AtomicBoolean), starts
  `ExitCapacityIndex.load` + background thread filling `_all` from
  `BridgeCountries.topSync`.

**Windows plan:** identical state (same keys `exit_ccs`/`exit_names` in
the config file), same `directory` ordering rule, same one-shot load on
startup (MainActivity already calls it), same `currentCodes()` feed into
torrc/control-port steering (TorRunner already ports).

**ExitLocator.kt (129) — where traffic leaves the network:**
- `ExitInfo(ip, countryCode, countryName, city, asn, fromNetwork)` with
  `label()` = `"BE · Belgium"` / name / code / `"unknown"`.
- `locate(context, socksHost, socksPort, timeout=15 s)` runs everything
  **through the app's SOCKS5** (java `Proxy.Type.SOCKS` → DNS resolved at
  the exit too):
  1. primary: `https://api.ip2location.io/` (regex-mined JSON keys `ip`,
     `country_code`, `country_name`, `city_name`, `as`; `fromNetwork=true`);
  2. fallback: echo URLs in order `https://api.ipify.org/`,
     `http://icanhazip.com/`, `http://ifconfig.me/` → first line matching
     the IPv4 regex → offline country via `BridgeCountries.countryInfo`
     (`fromNetwork=false`).
- `fetch()`: UA `DeltaTor/2.0`, `Accept: */*`, `Connection: close`,
  redirects disabled on HTTPS, connect+read timeout = timeoutMs.

**Windows plan:** port verbatim (same two stages, same URLs, same UA,
same regex JSON miner, same 15 s default); the SOCKS `Proxy` equivalent
on JVM/.NET is available (SOCKS5 with remote DNS), so the exact same
sequence works. Result feeds `AppState.exit*` (EXIT pill, `Locating …`).

**ExitCapacity.kt (85) — bundled per-country exit capacity:**
- `ExitCapacity(exits, weight)`; index loaded **once** from asset
  `geoip/exit-capacity.tsv` (tab-separated, ≥4 cols: cc, name, exits,
  weight; skip blanks/`#`; require cc length 2 and exits > 0), off-main
  thread, exposed as `byCountry: StateFlow<Map<String, ExitCapacity>>`.
- Ships as an asset on purpose (data drifts on a months scale; recipe in
  the file header).

**Windows plan:** same asset + same parser + same "read once at startup"
behavior; drives the LOCATION section (TOP 25 BY EXIT BANDWIDTH group,
share % with 1 decimal, exit counts, `REST OF WORLD` group).

**GeoIpFile.kt (180) — builds Tor's geoip/geoip6 from the bundled table:**
- Why: Tor binary ships no geoip data, so `ExitNodes {cc}` silently
  matched nothing (and with StrictNodes could pin bootstrap at 50 %).
- Input: same `geoip/country.csv.gz` / `country.csv` asset (rows
  `low,high,CC`, dotted quads or IPv6 text).
- Output per data dir: `geoip` (IPv4 bounds converted to unsigned 32-bit
  numbers) + `geoip6` (IPv6 text bounds, skip `high < low`), **only the
  selected countries** (a few hundred KB, not the full 31 MB), first line
  `# countries: US,NL,…` signature; regenerated whenever the set changes
  (header compare = cache); atomic `*.tmp` + rename; `kept4 == 0` →
  failure (caller must not claim enforcement); `discard()` deletes both.
- `toUint32()` same dotted-quad → long conversion.

**Windows plan — NOT ported (decision: official geoip).** The Tor Expert
Bundle already ships the authoritative full databases
(`data/geoip` 9.7 MB + `data/geoip6` 16.3 MB), which is exactly why
Android had to generate them (its `libtor.so` ships none). On Windows
`TorRunner.prepareGeoIp()` becomes a no-op: the generated torrc always
sets `GeoIPFile <bundle>/data/geoip` and `GeoIPv6File <bundle>/data/geoip6`
(exists-check kept, path swap only). `ExitNodes {cc}` steering therefore
works against Tor's own curated data — more accurate than the db-ip
derived file, and `ensure`/`discard`/`header`/`toUint32` disappear from
the port. `BridgeCountries` (picker names, exit-locator offline fallback)
still uses `geoip/country.csv.gz` — unaffected.

### [x] 2.6f HevSocks5Tunnel.kt (234) + InstalledApps.kt (101)

**HevSocks5Tunnel — JNI bridge to hev-socks5-tunnel (tun2socks):**
- Loads `libhev-socks5-tunnel.so` + `libhev-tunnel-jni.so` (see 2.7).
- `start(tunFd, socksAddress, socksPort, user?, pass?, enableUdpTunneling,
  mtu=1280, ipv4="10.255.255.1", ipv6="fd00::1", disableQuic=true,
  rejectNonDnsUdp=false)`: stops a running instance first (500 ms), builds
  a **YAML config** and passes it + the TUN fd to `nativeStart`:
  `tunnel: {mtu, ipv4, ipv6}` · `socks5: {address, port, udp: 'tcp' if
  UDP-over-TCP (FWD_UDP cmd 0x05), username/password single-quoted with
  '' escaping}` · `misc: {task-stack-size: 32768, connect-timeout: 8000,
  tcp-read-write-timeout: 120000, udp-read-write-timeout: 60000,
  log-level: warning}`; also `nativeSetRejectQuic(true)`,
  `nativeSetRejectNonDnsUdp(...)`.
- `stop()`: `nativeStop()` then poll `isRunning()` every 100 ms up to
  3000 ms; `isRunning()`; `getStats()` → `TrafficStats(txPackets,
  txBytes, rxPackets, rxBytes)` from `nativeGetStats()` (4 longs) — this
  is the source of the UI/notification speed + total counters.

**Windows plan:** the library itself is cross-platform C (hev-socks5-
tunnel) and **already ships a Windows/Wintun backend in the vendored tree**
(`hev-tunnel-windows.c`, `third-part/wintun/`), so the decision for
section 3 is the Wintun-based tun2socks full-tunnel: same wrapper API
(`start/stop/isRunning/getStats` with the same YAML config, same
timeouts/MTU) over `hev-socks5-tunnel.dll`, with the Wintun adapter
carrying system traffic into it. The UI must still show the same
tx/rx/speed numbers, so the stats producer keeps
the same shape (`TrafficStats` + 1 s/5 s polling cadence).

**InstalledApps — the Split Tunnelling app picker list:**
- `App(packageName, label, system, icon)`; live read of
  `getInstalledApplications` (never cached), launchable apps only
  (`getLaunchIntentForPackage != null`), DeltaTor itself dropped (the VPN
  service cannot disallow its own package), icon failures tolerated
  (icon nullable).
- Sort: user apps before system (`!system` first), then label
  case-insensitive; log "Listed N launchable apps".

**Windows plan — NOT ported (split tunnelling cut, section 3).** The
picker, the screen, the VPN/BYPASS toggle and the
`split_tunnel_*` config keys do not exist in v1; `InstalledApps.load()`
stays Android-only. Selective routing on Windows = the Proxy Only mode
(user points chosen apps at `socks5://127.0.0.1:<port>` manually), which
is already an exact 1:1 feature.

### [x] 2.6g LocalProxyAuth.kt (104) + ProtocolSniffer.kt (176) + TorrcSettings.kt (160)

**LocalProxyAuth — RFC 1929 username/password for the local SOCKS5:**
- `handleGreeting(methods, in, out, username?, password?)`: auth off
  (either credential null/empty) → reply `05 00`, accept; auth on → no
  `0x02` offered → `05 FF` reject (readiness probes only offer NO_AUTH);
  select `05 02`; RFC 1929 subnegotiation (ver must be 0x01, length-
  prefixed UTF-8 user/pass, `readExactly`), match → `01 00` else `01 01`
  with a warning log naming the attempted user.
- Purpose (comment): stop other local apps from abusing the proxy to
  bypass VPN/split-tunnel rules.

**Windows plan:** port as-is; used by TorSocksBridge when proxy auth is
enabled (same handler, same byte replies). On a multi-user Windows box
this matters the same way; wire the credentials from the same config
fields the Android build exposes.

**ProtocolSniffer — domain recovery from IP-only CONNECTs:**
- `sniff(input, timeout=3 s)`: reads up to `MAX_SNIFF_SIZE = 4096` bytes
  once; returns `SniffResult(domain?, bufferedData, bufferedLength)` — the
  buffered bytes must be prepended when forwarding.
- TLS: parses a ClientHello (record 0x16, handshake 0x01) walking Session
  ID → CipherSuites → Compression → Extensions, SNI ext type 0x0000,
  nameType 0x00 → lowercase ASCII hostname. HTTP: first bytes must start
  with a known method (GET/POST/PUT/DELETE/HEAD/OPTIONS/PATCH/CONNECT),
  then case-insensitive `\r\nHost:` scan, strip `:port` when the suffix is
  all digits (IPv6-safe), lowercase.
- Why needed on Android: TUN only sees IPs. **On Windows, if the SOCKS
  bridge receives domain CONNECTs directly from apps (system proxy mode),
  sniffing is a no-op fallback** — but keep the code for the
  IP-literal/path where domains are unavailable.

**Windows plan:** port unchanged (pure JVM bytes logic); invoked from
TorSocksBridge's debug/domain-routing path exactly where Android does it.

**TorrcSettings — the user-editable torrc template:**
- Prefs `deltator`, key **`torrc_template_v5`** (version suffix: bump =
  hand every install the new default). `init`, `template()` (default
  fallback), `setTemplate` (trimmed), `resetTemplate`, `templateLines()`
  (trim, drop blanks and `#` comments), `intValue(key, fallback)` (last
  occurrence wins — same rule as Tor).
- **`defaultTemplate` (exact text, must be byte-identical on Windows):**
  SocksPolicy accept 127.0.0.1 / reject * · CircuitPadding 0 ·
  ConnectionPadding 0 · UseMicrodescriptors 1 · DormantOnFirstStartup 0 ·
  DormantCanceledByStartup 1 · LearnCircuitBuildTimeout 0 ·
  CircuitBuildTimeout 30 · MaxCircuitDirtiness 3600 · NumEntryGuards 10 ·
  NumDirectoryGuards 6 · MaxClientCircuitsPending 64 · SocksTimeout 60 ·
  three ClientBootstrapConsensus*InitialDelay 0 lines ·
  ClientBootstrapConsensusMaxInProgressTries 6 · FetchDirInfoEarly 1 ·
  FetchDirInfoExtraEarly 1 · PathsNeededToBuildCircuits 0.25 ·
  DisableDebuggerAttachment 1 · SafeLogging 1 · ConfluxEnabled 0 · then
  the reconnect tail: MaxCircuitDirtiness 600 · NewCircuitPeriod 10 ·
  SocksTimeout 30 · CircuitsAvailableTimeout 4320 ·
  CircuitStreamTimeout 60 · CircuitBuildTimeout 40 · NumPrimaryGuards 15 ·
  Schedulers Vanilla · MaxClientCircuitsPending 128.
- Assembly order in TorRunner (documented in the header — last wins):
  forced defaults → template → ClientTransportPlugin → Bridge →
  ControlPort. The template therefore overrides forced defaults, and the
  Snowflake runner's ConnectionPadding/SafeLogging/NumEntryGuards unless
  the template declares them.
- Design rules encoded in the header: no fixed Socks/HTTP/DNS/Control
  ports (3 parallel Tors; app owns the only SOCKS listener), only
  `ConfluxEnabled 0` (ConfluxNum* would abort this build), intentional
  duplicate directives for last-wins reconnect tuning, `Schedulers
  Vanilla` chosen over `KISTLite,Vanilla`, no CircuitPriorityHalflife,
  PathsNeeded 0.25 (floor), CircuitStreamTimeout 60 (10 was a floor),
  NumEntryGuards 10 ≤ NumPrimaryGuards 15 (Tor refuses to start
  otherwise).

**Windows plan:** port `TorrcSettings` 1:1 — same key `torrc_template_v5`
in the same config store, same default template text verbatim (no
comments), same `templateLines`/`intValue` semantics, same reset; the
TORRC TEMPLATE card in the drawer shows/edits/saves it with the same
hints. Verify each directive against the official tor.exe version's
manpage (the Android binary is a specific build — same directives are
expected to exist; `DisableDebuggerAttachment` behaves the same).



### [x] 2.7 cpp/ (hev_jni.c, Android.mk, Application.mk + vendored source)

**What's here:**
- `hev-socks5-tunnel/hev_jni.c` (163 lines): JNI wrapper. A dedicated
  pthread runs `hev_socks5_tunnel_main_from_str(config, len, tun_fd)`;
  `nativeStart` = strdup config + `pthread_create`; `nativeStop` =
  `hev_socks5_tunnel_quit()` + `pthread_join`; plus
  `hev_socks5_tunnel_set_reject_quic`, `..._set_reject_non_dns_udp`,
  `hev_socks5_tunnel_stats(&txp,&txb,&rxp,&rxb)` (4 size_t → jlong[4]).
  Logging via `__android_log_print` (tag `HevTunnel`).
- `Android.mk`: includes vendored `hev-socks5-tunnel-src/Android.mk`,
  builds `hev-tunnel-jni` shared lib against
  `hev-socks5-tunnel-src/include`, links `-llog`, gc-sections/strip,
  16 KB page-size linker flags.
- `Application.mk`: ABIs arm64-v8a/armeabi-v7a/x86_64, android-24,
  release, `APP_STL := none`, `-O3`, flexible page sizes.
- **Vendored `hev-socks5-tunnel-src/` (full upstream tree):** core
  `hev-socks5-tunnel.c`, session/tcp/udp, mapped-DNS, config, task system
  (`third-part/hev-task-system`) — and, importantly for the port:
  `src/hev-tunnel-windows.c`/`.h`, `src/misc/hev-wintun.c` and
  `third-part/wintun/` (wintun.h + LICENSE). Upstream supports **Windows
  with Wintun** natively; also linux/macos/freebsd/netbsd backends,
  `Makefile`/`build.mk`/`Dockerfile`.

**Windows plan:** build the same vendored library for Windows as a DLL
(`hev-socks5-tunnel.dll`) using its own Makefile/CMake for MSVC or MinGW,
with the Wintun backend (`hev-tunnel-windows.c` + `wintun.dll/.sys`
shipped next to the exe). Replace the JNI wrapper with an equivalent thin
C/C# wrapper exporting the same five operations: `start(configYaml,
wintunSession?)`, `quit()`, `set_reject_quic`, `set_reject_non_dns_udp`,
`stats` — the YAML config from HevSocks5Tunnel.kt is consumed unchanged
(the library parses the same format on every platform). Stats feed the
same `TrafficStats` shape. This makes the Windows app a full 1:1 tunnel:
Wintun adapter with the same MTU 1280 / addresses 10.255.255.1 + fd00::1,
system routes pointing at it, tun2socks → TorSocksBridge (127.0.0.1:
proxy_port), same QUIC/UDP rejection toggles.

### [x] 2.8 assets + CI workflows

**Assets shipped in the APK:**
- `assets/bridges/` (11 bundled lists, same names as the remote sources):
  `vanilla.txt`, `vanilla_72h.txt`, `vanilla_ipv6_72h.txt`, `obfs4.txt`,
  `obfs4_72h.txt`, `obfs4_ipv6_72h.txt`, `webtunnel.txt`,
  `webtunnel_72h.txt`, `webtunnel_ipv6.txt`, `webtunnel_ipv6_72h.txt`,
  `snowflake.txt` — fallback when the disk cache is empty and the network
  is unreachable (fetchBridgeLines order: cache → bundled → download).
  NOTE: `fresh` has no single bundled file; it merges the six `*_72h`
  files (mirrors BRIDGE_SOURCES).
- `assets/geoip/`: `country.csv.gz` (db-ip ranges, also the GeoIpFile
  source), `countries.tsv` (CC→name for the picker/exit labels),
  `exit-capacity.tsv` (per-country exits+weight for the LOCATION order).
- `assets/geo/` (ir/cn/ru `.cidr`+`.domains` for DomainRouter geo-bypass)
  **is not present in the tree** — `loadGeoData` fails soft (logs, empty
  data). Geo-bypass ships data-less today; keep the same tolerant loader
  on Windows and optionally add the assets there.
- Also referenced: `assets/geoip/country.csv` plain fallback (not shipped;
  code prefers `.gz`).

**`.github/workflows/android-build.yml` (Build APK):**
- Triggers: push to `main`/`android`, `workflow_dispatch`.
- ubuntu-latest → checkout → JDK 17 (temurin) → sdkmanager install
  `platforms;android-36`, `build-tools;36.0.0`, `ndk;29.0.14206865` →
  `chmod +x Android/gradlew` → `./Android/gradlew -p Android assembleDebug
  --no-daemon` → package 3 artifacts:
  1. `deltator-debug-apk.zip` (all built .apk; zip with `zip` or python
     zipfile fallback),
  2. `deltator-sources.zip` (`git archive HEAD` — sources of the exact
     commit built),
  3. `deltator-native-symbols.zip` (unstripped `.so` from
     `intermediates/merged_native_libs|cxx|staged_native_libs`).
- Uploads all 3 via `actions/upload-artifact@v4`, `if-no-files-found:
  error`.

**`.github/workflows/release.yml` (Release):**
- Triggers: push tags `v*`, or `workflow_dispatch` with inputs `tag` +
  `allow_retag` (default false). `permissions: contents: write`.
- Steps: checkout → JDK 17 → SDK/NDK install (same) → **tag guard**:
  input tag or `GITHUB_REF_NAME` (only when ref type is tag), shape must
  be `v[0-9]*.[0-9]*.[0-9]*` **before** pushing; peel both sides to
  commits (`^{commit}`) — same commit → reuse; different → fail unless
  `allow_retag=true` (force-move + warn); absent → create+push.
  `github-actions[bot]` as committer.
- **Version step**: `VERSION=${TAG#v}`, must be exactly
  `MAJOR.MINOR.PATCH` (three numeric parts), output `version`.
- **Keystore decode**: secrets `ANDROID_KEYSTORE_BASE64` (+passwords/
  alias/store type), all four required non-empty; base64 →
  `keystore-release.jks`, non-empty check, `keytool -list` with
  `-storepass:env` verifying store password AND alias presence (outputs
  captured, not discarded).
- **Build**: `assembleRelease --no-daemon -PdeltatorVersion=$version`
  with keystore env vars.
- **Collect**: rename per-ABI APKs → `deltator-<tag>-arm64-v8a.apk`,
  `-armeabi-v7a.apk`, `-universal.apk`; assert all three exist.
- **Version check**: aapt2 `dump badging` per APK; `versionName` must
  equal expected, `versionCode` must equal `major*10000+minor*100+patch`.
- **Signature checks**: `apksigner verify --verbose --print-certs` for
  every APK; then **pin check** against `Android/release-signing-cert.sha256`
  (first 64-hex run; CRLF/BOM/comments/colons tolerated; normalized
  lower-case) — fail closed if no pin or mismatch (digests extracted from
  both apksigner output shapes). The pin file IS populated today
  (cert `CN=DeltaTor, OU=Release, O=Delta-Kronecker, C=IR`, digest
  `F5:28:75:47:…:F7:EB`), i.e. the check is live, not placeholder.
- **Publish**: upload-artifact `deltator-release-apks` + 
  `softprops/action-gh-release@v2` with `files: dist/*.apk`,
  `generate_release_notes: true`.

**Windows plan — two mirrored workflows:**
1. `.github/workflows/windows-build.yml` (analog of android-build):
   triggers push `main` + dispatch; `windows-latest`; setup the chosen
   toolchain (e.g. JDK/… or MSVC/MinGW + CMake per section 3); build the
   `Windows/` project; package the **same three artifact kinds**:
   `deltator-debug-windows.zip` (portable app zip), `deltator-sources.zip`
   (git archive), `deltator-native-symbols.zip` (unstripped exe/pdb from
   hev-socks5-tunnel + any native parts) → upload-artifact with the same
   `if-no-files-found: error`.
2. `.github/workflows/windows-release.yml` (analog of release.yml):
   same tag guard logic verbatim (v-shape check before push, peel to
   commits, allow_retag opt-in), same version derivation
   (`MAJOR.MINOR.PATCH`), same fail-closed philosophy; replace the
   Android signing block with Windows code signing (Authenticode cert
   from secrets e.g. `WINDOWS_CERT_BASE64`/`WINDOWS_CERT_PASSWORD`,
   `signtool` with an RFC3161 timestamp) — applied to **our own binaries
   only** (`DeltaTor.exe`, `hev-socks5-tunnel.dll`); upstream signatures on
   `tor.exe`/`lyrebird.exe`/`wintun.dll`/`msys-2.0.dll` stay intact because
   erasing them would destroy the upstream authors' attestations, version check = exe
   FileVersion (first three parts) vs tag,
   pin check = SHA-256 of the signer DER cert read back out of the shipped
   exe, expected digest in
   `Windows/release-signing-cert.sha256` (fail closed; first signing run
   prints the actual digest into the step summary), publish with
   `softprops/action-gh-release@v2` attaching
   `deltator-<tag>-windows-x64.zip` (same tag releases may also carry the
   Android APKs, so the existing single `ReleaseChecker` endpoint keeps
   serving both platforms).
   Download/verify assets must include the official pre-built binaries —
   the **Tor Expert Bundle for Windows x86_64-15.0.24** (contains
   `tor.exe`, `lyrebird.exe` = all 5 transports incl. snowflake, and the
   official `data/geoip`+`geoip6`) and `wintun.dll` — pinned by
   version+SHA-256 in the workflow, checksum
   verified before packaging (reproducible "official binaries only"
   policy from section 0).



---

## 3. Proposed Windows architecture

**Stack decision**

- **Language/UI:** C# on .NET 8, **WinForms with owner-drawn controls**.
  Rationale: the Android theme was itself copied 1:1 from the old WinForms
  `TorJetUi.cs` (documented in `DeltaTorTheme.kt`), the whole UI is custom-
  painted already (ring button, gradients, glow, chips), and WinForms
  gives direct GDI+ control + trivial CI on `windows-latest`. WPF is the
  fallback if hardware-accelerated animation is wanted; the palette,
  strings, layout and behavior stay identical either way.
- **Core:** a single class library (`DeltaTor.Core`) holding the ported
  logic: `AppState`, `Config` (same keys/defaults), `AppLog`, `TorrcSettings`,
  `BridgeStore`, `BridgeMemory`, `BridgeCountries`, `BridgeExport`,
  `ExitNodes`, `ExitCapacity`, `ExitLocator`,
  `ParallelTorManager`, `TorRunner`, `SnowflakeBridge`, `TorSocksBridge`,
  `DomainRouter`, `ProtocolSniffer`, `LocalProxyAuth`, `BridgeRace` service
  (the TorVpnService state machine). No Android types leak in — `Context`
  parameters become paths/`IAppEnvironment`.
  NOT ported: `GeoIpFile` (official bundle geoip instead, 2.6e),
  `InstalledApps` + SplitTunnelScreen (split cut, 2.6f).

**Process/traffic model (1:1 mapping)**

| Android | Windows |
|---|---|
| `TorVpnService` (VpnService + TUN 10.255.255.1, MTU 1280) | in-process engine controller + **Wintun adapter** (same MTU/addresses) created by the vendored `hev-socks5-tunnel` Windows backend (`hev-tunnel-windows.c` already in-tree) |
| TUN routes 0.0.0.0/0 → tun2socks → TorSocksBridge | Wintun routes (system route table, added/removed on connect) → tun2socks DLL → TorSocksBridge on `127.0.0.1:<proxy_port>` |
| Split tunnelling: `addAllowed/DisallowedApplication` | **NOT PORTED (v1 cut)** — full tunnel always; selective routing = Proxy Only mode (apps pointed at the SOCKS manually) |
| Proxy-only mode: no TUN, `socks5://127.0.0.1:<port>` | identical: tunnel subsystem stays off, ring/label/notification show the same endpoint strings |
| Foreground notification (actions Disconnect/Stop VPN/Start VPN) | tray icon + tray tooltip/status menu + Windows toast with the same texts/actions |
| Wake lock during bootstrap | `SetThreadExecutionState`/power request (optional; desktops don't sleep mid-connect by default) |
| FileProvider share sheet for bridge zip | Save-File dialog / "Open folder" for the same zip |
| install counter / release check / bridge download | unchanged HTTP code (same URLs, UA `DeltaTor-Windows` for the counter) |

**Full-tunnel routing on Windows (stage 4, implemented):**

- **Catch-all:** `0.0.0.0/1` + `128.0.0.0/1` on-link via the Wintun adapter
  (same v4-only reach as Android's `addRoute(0.0.0.0/0)` — v6 stays direct,
  exactly as on Android), adapter DNS `8.8.8.8` (Android `DEFAULT_DNS`) with
  a low interface metric so the resolver prefers it. All installed through
  one PowerShell (`Add-NetRoute`/`Set-DnsClientServerAddress`) after hev
  creates the adapter.
- **Self-exclusion:** Android keeps its own sockets out of the tunnel with
  `addDisallowedApplication(packageName)` (TorVpnService.kt); Windows has no
  per-process routing, so tor.exe/lyrebird.exe/our probe sockets would be
  swallowed by the catch-all and re-enter the tunnel — a deadlock loop (the
  winning Tor can only build circuits over the very outbound connections
  being captured). The Windows equivalent is destination-based bypass: a
  `/32` host route out the physical default gateway for every remote IPv4
  destination tor or lyrebird actually uses, learned from the live TCP table
  (`GetExtendedTcpTable`, in-process, 1 s tick): guards are already
  connected when `TunnelEngine.Start` runs, so a seed pass covers them;
  anything later surfaces as `SYN_SENT` within a tick and Tor retries, so a
  missed first packet self-heals in about a second. **Consequence: full
  tunnel + Snowflake does not work in v1** — Snowflake's STUN/DTLS peer UDP
  has no TCP state to learn and `TorSocksBridge.FWD_UDP` drops non-53 UDP
  anyway; Snowflake stays a Proxy-Only-mode transport on Windows (documented
  limitation, Android unaffected by its own exclusion).
- **Elevation:** route table + Wintun adapter need admin, so the app runs
  with `requireAdministrator` (`app.manifest`) — the standard shape for
  Windows VPN clients.
- **Crash safety:** installed prefixes are persisted to
  `%LOCALAPPDATA%\DeltaTor\tun-routes.txt` before the routes go in and
  deleted only on clean removal; startup `CleanupLeftovers` reclaims
  leftovers so a killed run can never leave a black-holing `0.0.0.0/1`
  behind until reboot.
- **Native build:** `Windows/native/build-hev.sh` compiles the vendored tree
  (`make static` + one `gcc -shared` link) in the CI MSYS2 step — the only
  Windows toolchain upstream supports (POSIX sources + `__MSYS__`-gated
  backend/IOCP reactor) — producing `hev-socks5-tunnel.dll` plus its
  `msys-2.0.dll` runtime, both shipped next to the exe; `wintun.dll` comes
  pinned from `fetch-binaries.ps1`. Adapter name comes from a Windows-only
  `name:` line in the YAML config (Android omits it; the Windows backend
  cannot take a null name).

**Binaries (official, pre-built, pinned)**

- **Tor Expert Bundle for Windows x86_64-15.0.24** — the single core
  source, fetched by `Windows/fetch-binaries.ps1` from the official URL
  `https://dist.torproject.org/torbrowser/15.0.24/tor-expert-bundle-windows-x86_64-15.0.24.tar.gz`
  (a `.tar.gz` — extract with `tar -xzf`, available on Windows 10+ and
  GitHub runners). NOT committed; the
  local copy at repo root `tor-expert-bundle-windows-x86_64-15.0.24/` is
  gitignored and serves as a cache. Contents used:
  - `tor/tor.exe` (10.2 MB) — Tor core, spawned per runner.
  - `tor/pluggable_transports/lyrebird.exe` (17.9 MB) — serves **obfs4,
    webtunnel, meek_lite, meek/scramblesuit/obfs2/obfs3 AND snowflake**
    (verified via `data/torrc-defaults`:
    `ClientTransportPlugin snowflake exec lyrebird.exe`, `pt_config.json`,
    and snowflake strings inside the binary). Managed-PT env vars/CMETHOD
    protocol identical → TorRunner ports unchanged. **No separate
    snowflake binary.**
  - `data/geoip` (9.7 MB) + `data/geoip6` (16.3 MB) — official Tor
    databases referenced directly from torrc (GeoIpFile not ported).
  - `docs/*.txt` — third-party licenses, shipped alongside.
  - `data/torrc-defaults` — reference only (our `TorrcSettings` template
    wins; it already contains AvoidDiskWrites/Log notice etc.).
- `wintun.dll` (+ license) — official wintun.net build, pinned download.
- `hev-socks5-tunnel.dll` — **built in this repo** from the vendored
  source (it is our code, MIT, and the Windows backend ships in-tree);
  thin P/Invoke wrapper mirrors the JNI surface (start/quit/reject-quic/
  reject-non-dns-udp/stats) with the same YAML config.
- All downloads pinned by version + SHA-256 in `Windows/fetch-binaries.ps1`,
  verified at build time; the workflow fails if a checksum mismatches.

**Per-runner model (unchanged):** N Tor processes racing on
`basePort+1..+15`, winner's SOCKS becomes the bridge upstream, losers
killed, memory banking, control-port exit steering, official bundle geoip
referenced from torrc,
template-appended torrc — all ported logic is byte-for-byte equivalent;
only process spawn paths (`tor.exe`, `%LOCALAPPDATA%\DeltaTor\tor_data_<name>`,
`HOME` env) differ.

**Not ported / adapted:** notification channels (→ tray/toast),
SplitTunnelScreen + `InstalledApps` (cut, see decision above; proxy-only
is the escape hatch), `GeoIpFile` (→ official bundle geoip), VpnService
permission flow (→ first-run route-setup prompt), `QUERY_ALL_PACKAGES`
(no Windows equivalent needed). Wake lock → optional
`SetThreadExecutionState`.

---

## 4. Windows/ folder structure

```
Windows/
├── DeltaTor.sln
├── .gitignore                  # ignores local tor-expert-bundle cache + bin/ + obj/
├── fetch-binaries.ps1          # downloads pinned official binaries:
│                               #   Tor Expert Bundle x86_64-15.0.24 (.tar.gz from
│                               #   https://dist.torproject.org/torbrowser/15.0.24/tor-expert-bundle-windows-x86_64-15.0.24.tar.gz)
│                               #   + wintun.dll — verifies SHA-256, uses ../tor-expert-bundle-* as cache
├── VERSION                     # fallback version (workflow overrides from tag)
├── src/
│   ├── DeltaTor.Core/          # ported logic (AppState, Config, AppLog, race engine,
│   │   ├── AppState.cs         #   TorRunner, TorSocksBridge, BridgeStore/Memory,
│   │   ├── Config.cs           #   Exit*, TorrcSettings, DomainRouter, …)
│   │   ├── AppLog.cs
│   │   ├── …
│   │   └── Race/ParallelTorManager.cs, TorRunner.cs, SnowflakeBridge.cs
│   ├── DeltaTor.App/           # WinForms UI: MainForm (ring, header, drawer,
│   │   ├── MainForm.cs         #   7 advanced cards), LogForm — NO SplitTunnelForm,
│   │   ├── Controls/*.cs       #   NoticeDialog, ToggleSwitch, GradientPill, …
│   │   └── Program.cs          #   startup sequence = DeltaTorApp.onCreate
│   └── DeltaTor.Native/        # P/Invoke to hev-socks5-tunnel.dll + wintun helpers
├── native/
│   └── hev-socks5-tunnel/      # built DLL output (+ wrapper .c if needed)
├── assets/
│   ├── bridges/                # same 11 bundled .txt lists as Android
│   └── geoip/                  # country.csv(.gz), countries.tsv, exit-capacity.tsv
│                               #   (NO geo/ dir — geo-bypass ships data-less, as on Android)
├── bin/                        # build output: DeltaTor.exe + hev-socks5-tunnel.dll,
│                               #   + fetched: tor.exe, lyrebird.exe, data/geoip(+geoip6),
│                               #   wintun.dll, README-licenses.txt → zipped artifact
└── release-signing-cert.sha256 # pinned Authenticode signer digest (parity with Android)
```

Config/data at runtime (parity with Android's `filesDir`):
`%LOCALAPPDATA%\DeltaTor\` — `config.json` (same keys as SharedPreferences),
`bridges/`, `bridge_memory.json`, `tor_data_<name>/`, `exports/`,
`deltator.log` ring snapshot optional.

---

## 5. Build & release (GitHub Workflow)

Two new workflows mirroring the Android ones 1:1 (full step mapping in
section 2.8):

1. **`.github/workflows/windows-build.yml`** — push `main` + dispatch →
   `windows-latest` → toolchain setup → `fetch-binaries.ps1` (downloads
   `https://dist.torproject.org/torbrowser/15.0.24/tor-expert-bundle-windows-x86_64-15.0.24.tar.gz`
   + `wintun.dll`, SHA-256 verified) → build `Windows/DeltaTor.sln`
   (Release) → package `deltator-debug-windows.zip` (portable app dir),
   `deltator-sources.zip` (`git archive HEAD`), 
   `deltator-native-symbols.zip` (unstripped DLLs + PDBs) → upload-artifact
   (`if-no-files-found: error`).
2. **`.github/workflows/windows-release.yml`** — tag `v*` / dispatch with
   `tag`+`allow_retag` inputs → **same tag-guard script verbatim**
   (shape check before push, peel-to-commit compare, opt-in retag) →
   version from tag (3-part rule) → Authenticode-sign every shipped
   exe/dll from secrets (`WINDOWS_CERT_BASE64`, password) → verify
   version resource (FileVersion == tag version) → verify signer
   thumbprint against `Windows/release-signing-cert.sha256` (fail closed,
   same normalization rules) → upload artifacts →
   `softprops/action-gh-release@v2` attaching
   `deltator-<tag>-windows-x64.zip` (same tag releases may also carry the
   Android APKs, so the existing single `ReleaseChecker` endpoint keeps
   serving both platforms).

---

## 6. Final 1:1 parity checklist

To be verified item-by-item against the Android app before calling the port done:

- [ ] Versioning: tag → `MAJOR.MINOR.PATCH`, fallback 2.0.0, regex gate, exe version resource
- [ ] Config: every key/default from `DeltaTorApp.Config` (proxy_port 9050 clamp, transport_mode combined, auto_transports defaults, run_memory, auto_recovery, …; `split_tunnel_*` keys absent — ignored if present)
- [ ] First-run trilingual notice (Persian RTL / English / Russian, exact leads) + id-ack behavior
- [ ] UI: palette hexes, 6-step type ladder, gradients, AirBackground, ring button animations (sweep/breathe/pulse), status chip words, state block words + sublines
- [ ] Drawer: LOCATION (warning card, TOP 25 by exit capacity, REST OF WORLD, Any location) + ADVANCED with **7 cards** (no SPLIT TUNNELLING card), same labels/descriptions/buttons incl. "SAVED ✓" 1.5 s
- [ ] Log screen: session grouping, transport chips + counts, EWIDV severity chips, copy button, recording-off banner, empty states
- [ ] Split tunnelling CUT: no card, no screen, no picker, no `split_tunnel_*` config — and Proxy Only remains the documented way to route selected apps
- [ ] Race engine: modes/port map (+1..+15), shuffle rules, 150-line cap, 30-min/120-s timeouts, 1-s poll, generation guard, memory banking at 100 %, combined merge + webtunnel ver replace, all-failed error text
- [ ] BridgeStore: 24-h auto update, `_v4` key, UA `DeltaTor/1.0`, atomic saves, stats/combined counts
- [ ] BridgeMemory: 300/transport, proof-order front insert, prefix sweep clear, twin filtering, 40-hex fingerprints
- [ ] Torrc: `torrc_template_v5` default text verbatim, assembly order (defaults → template → CTP → Bridge → ControlPort), `intValue` last-wins
- [ ] Control-port exit steering (SETCONF ExitNodes + StrictNodes 0, NEWNYM, GETCONF verify, 4-s timeouts)
- [ ] GeoIP: generated torrc references the bundle's official `data/geoip` + `data/geoip6`; `ExitNodes {cc}` actually steers (verify exit country after connect); `GeoIpFile` generation NOT present
- [ ] Bridge export zip: same file set, stamp name, README text, null-when-empty
- [ ] Exit locator: ip2location.io → ipify/icanhazip/ifconfig fallback, UA `DeltaTor/2.0`, offline geo fallback, retry policy (5 s + 6×8 s with selection)
- [ ] Service state machine: CONNECT/DISCONNECT/START_VPN/STOP_VPN, stall watchdog 60 s, one-shot AutoRecovery notice (exact title/body), probe Ok/Live/Dead + grace formula + 90-s cooldown + 5/20/45/90 intervals, stats 1 s/5 s change-only, STOPPING ≥3 s, teardown order
- [ ] Proxy-only mode: endpoint string, no tunnel, locator only, same ring logic
- [ ] TorSocksBridge: no-auth SOCKS5 CONNECT, 32768 buffers, 8-thread DNS pool + 5-min cache over Tor, repoint on recovery, DomainRouter bypass, LocalProxyAuth RFC 1929, ProtocolSniffer fallback
- [ ] Snowflake: runs **through lyrebird** (managed-PT `CMETHOD snowflake`, no separate binary); built-in/AMP/SMART modes expressed as a synthetic bridge line with the same broker/front/STUN/uTLS/fingerprint constants; torrc rules incl. no Socks5Proxy+CTP combo
- [ ] Binaries: fetch script downloads Tor Expert Bundle **x86_64-15.0.24** + `wintun.dll`, SHA-256 verified, fail on mismatch; `tor.exe`/`lyrebird.exe` versions match the pin
- [ ] Full tunnel: `hev-socks5-tunnel.dll` built in CI (MSYS2) + `msys-2.0.dll` + `wintun.dll` land next to the exe; `TunnelEngine.Impl = WindowsTunnel`; Wintun adapter `DeltaTor`, MTU 1280, 10.255.255.1 + fd00::1; catch-all `0.0.0.0/1` + `128.0.0.0/1`, DNS 8.8.8.8; bypass `/32`s for tor/lyrebird destinations with seed-then-watch learning; `tun-routes.txt` crash recovery; elevation manifest present; stats tx/rx feed the same UI counters
- [ ] Full tunnel known limitation (documented, not a bug): Snowflake does not work through the tunnel on Windows — Proxy Only is the supported mode for it
- [ ] DomainRouter dormant: `DISABLED` default, no UI exposure, no `geo/` assets shipped (hooks still wired in the bridge)
- [ ] Notifications/toasts: same wording (Connecting…, Connected via X · Tor Network, traffic line, update banner "NEW RELEASE vX.Y.Z", release notification id-equivalent), tray menu actions
- [ ] ReleaseChecker: same endpoint, compareVersions, banner + toast open GitHub
- [ ] InstallCounter: same mechanism/limits, UA `DeltaTor-Windows`, own asset
- [ ] Bridge download: same base URL set, merge algorithm, 20-s timeouts, UA
- [ ] CI parity: debug build workflow + release workflow with tag guard, version check, signer pin check, artifact names in the `deltator-*` family
- [ ] No Persian/RTL text issues: RTL blocks render correctly on Win32 (per-block direction)
- [ ] Privacy: no telemetry; only bridge download, update check, one-time install count (README contract)
