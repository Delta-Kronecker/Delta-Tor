# 🧅 Tor Client — Windows GUI

A desktop application for connecting to the Tor network with bridge support.


https://github.com/user-attachments/assets/961a0b81-64e4-4dd9-bf49-fe74105d2110


---

## 🔥 Features

- ** Auto-Connect** — Automatically cycles through up to 9 bridge configurations until a successful connection is established
- ** Bridge Management** — Fetches and manages obfs4, webtunnel, and vanilla bridges from the [Delta-Kronecker Tor-Bridges-Collector](https://github.com/Delta-Kronecker/Tor-Bridges-Collector)
- ** Automatic Tor Setup** — Downloads and extracts Tor Expert Bundle on first run with no manual steps
- ** HTTP Proxy** — Built-in HTTP-to-SOCKS5 proxy works with Chrome, Edge, Telegram, and most Windows apps out of the box
- ** System Proxy Integration** — Automatically configures Windows system proxy on successful connection
- ** New Circuit** — Request a fresh Tor circuit (new exit IP) without restarting
- ** Exit Node Filtering** — Optionally restrict exit nodes to specific countries
- ** Keep-Alive** — Periodic background requests prevent ISP from dropping idle Tor connections
- ** Watchdog** — Monitors and automatically restarts Tor if it crashes or stalls
- **️ Full Settings UI** — Configurable timeouts, circuit parameters, DNS-over-Tor, experimental torrc options, and more

---

## 🔥 Requirements

- Windows 10 / 11 (x86_64)
- **EXE:** no requirements — runs standalone
- **Source:** Python 3.8+ only — no third-party packages

---

## 🔥 Quick Start

###  Recommended: Download the EXE

The easiest way to run Tor Client is to download the pre-built executable directly from the **[Releases](../../releases)** page. No Python, no dependencies, no setup — just download and run.

1. Go to the **Releases** section of this repository.
2. Download the latest `TorClient.exe`.
3. Run it. That's it.

### Running from Source (No Dependencies Required)


```bash
python Tor-Windows.py
```

### First Launch

1. A folder selection dialog will appear. The default `AppData\Local\TorClient\tor_custom_client` is recommended.
2. All bridge files are downloaded automatically in parallel.
3. Tor Expert Bundle is downloaded and extracted automatically.

### Connecting to Tor

| Setting | Recommended Value |
|---|---|
| Bridge Source | Delta-Kronecker Tor-Bridges-Collector |
| Category | Tested & Active |
| Transport | obfs4 |
| IP Version | IPv4 |

Then click ** Auto Connect**.

---

## 🔥 Tor Expert Bundle

On first run, the application automatically downloads **Tor Expert Bundle v15.0.6** for Windows (x86_64):

**Primary source (GitHub mirror):**
```
https://github.com/Delta-Kronecker/Tor-Expert-Bundle/raw/refs/heads/main/tor-expert-bundle-windows-x86_64-15.0.6.tar.gz
```

**Fallback source (official Tor Project archive):**
```
https://archive.torproject.org/tor-package-archive/torbrowser/15.0.6/tor-expert-bundle-windows-x86_64-15.0.6.tar.gz
```

###  Verifying the Bundle

You can verify the authenticity of `tor-expert-bundle-windows-x86_64-15.0.6.tar.gz` using the official Tor Project signature:

1. Visit the [Tor Project download archive](https://archive.torproject.org/tor-package-archive/torbrowser/15.0.6/) and download the corresponding `.tar.gz.asc` signature file.
2. Import the [Tor Project signing key](https://support.torproject.org/little-t-tor/verify-little-t-tor/) from the official Tor Project website.
3. Verify:
   ```bash
   gpg --verify tor-expert-bundle-windows-x86_64-15.0.6.tar.gz.asc \
               tor-expert-bundle-windows-x86_64-15.0.6.tar.gz
   ```

A valid signature will show `Good signature from "Tor Browser Developers"`.

---

## 🔥 Auto-Connect & Connection Memory

### How Auto-Connect Works

Clicking ** Auto Connect** launches a background thread that works in two distinct phases without blocking the UI.

**Phase 1 — Connection Memory**

Before trying anything new, the app checks whether a previously successful configuration is saved in `tor_client_config.json` (keys: `last_success_cat`, `last_success_trans`, `last_success_ip`). If found, it tries that exact configuration first. This means on most launches the app reconnects immediately using the same bridge type that worked last time, without cycling through the full sequence.

```
Last session used: Tested & Active / obfs4 / IPv4
→ Try that first (up to timeout seconds)
  ├─  Connected → done, no further attempts
  └─  Timed out → move to Phase 2
```

**Phase 2 — Priority Sequence**

If the memory config fails (or no memory exists), the app cycles through up to 9 preset configurations in priority order. The previously-tried memory config is automatically skipped to avoid repeating it:

| # | Category | Transport | IP |
|---|---|---|---|
| 1 | Fresh (72h) | obfs4 | IPv4 |
| 2 | Fresh (72h) | vanilla | IPv4 |
| 3 | Fresh (72h) | webtunnel | IPv4 |
| 4 | Tested & Active | obfs4 | IPv4 |
| 5 | Tested & Active | vanilla | IPv4 |
| 6 | Tested & Active | webtunnel | IPv4 |
| 7 | Full Archive | obfs4 | IPv4 |
| 8 | Full Archive | vanilla | IPv4 |
| 9 | Full Archive | webtunnel | IPv4 |

Fresh bridges are tried first since recently collected bridges are more likely to be alive. Each configuration gets a full timeout window before moving to the next.

### Timeout Detection (Stall-Based, Not Elapsed-Time)

The timeout logic is smarter than a simple countdown. The timer **resets every time the bootstrap percentage changes**. A configuration is only abandoned if the bootstrap percentage stays completely frozen for longer than the configured timeout (default: **180 seconds**).

```
Bootstrap at 15% → progress moves to 20% → timer resets 
Bootstrap at 15% → no movement for 180s → "Stuck at 15%" → kill → next config 
```

This means a slow but progressing connection is never cut off prematurely. The app reads Tor's `stdout` line by line in a blocking loop — there is no polling or `sleep()`. Everything is event-driven from Tor's own log output.

### On Successful Connection

When bootstrap reaches **100%**, the following happen atomically:

1. `last_success_cat / trans / ip` are written to `tor_client_config.json` (connection memory updated)
2. Windows system proxy is enabled automatically (if setting is on)


## 🔥 Proxy Configuration

| Protocol | Address |
|---|---|
| HTTP Proxy | `127.0.0.1:19052` |
| SOCKS5 | `127.0.0.1:9050` |

**Automatic:** Chrome, Edge, Telegram, and most Windows applications use the system proxy automatically.

DNS is resolved remotely by Tor — no DNS leaks.


---

## 🔥 Bridge Update Behavior

**Fresh (72h) bridges are updated automatically every time the app launches.** On startup, all Fresh bridge files are fetched in parallel in the background before any connection attempt — no manual action needed. This ensures the most recently collected bridges are always available.

For all other bridge categories (Tested & Active and Full Archive), updates can be triggered manually at any time by clicking the **"Update All Bridges"** button in the main window. This downloads every bridge file in parallel and is recommended if Auto-Connect is struggling to establish a connection.

| Bridge Category | Update Method |
|---|---|
| Fresh (72h) |  Automatic on every launch |
| Tested & Active |  Manual — "Update All Bridges" button |
| Full Archive |  Manual — "Update All Bridges" button |

---



## 🔥 Bridge Categories

| Category | Description |
|---|---|
| **Tested & Active**  | Verified working bridges — best choice |
| **Fresh (72h)** | Bridges collected in the last 72 hours |
| **Full Archive** | Complete historical bridge list |
| **Default** | Bridges bundled inside Tor itself |

### Transports

| Transport | Use Case |
|---|---|
| `obfs4` | Best for Iran, China, Russia — traffic looks like random data |
| `webtunnel` | Disguised as HTTPS website traffic |
| `vanilla` | Plain Tor — use only if Tor is not blocked |

---

## 🔥 Settings

Settings are stored in `%LOCALAPPDATA%\TorClient\tor_client_config.json` and are accessible via the **⚙️ Settings** button in the main window. All changes take effect after clicking **Apply & Save**; changes to torrc-level options require restarting Tor.

---

###  Auto-Connect

**Timeout per config** — `default: 180s` — range: 30–600s
How many seconds to wait at a frozen bootstrap percentage before giving up on the current bridge group and moving to the next one. This is a stall-based timeout, not a total elapsed time — the timer resets every time the bootstrap percentage moves. Lower values make the auto-connect cycle faster but may abandon slow bridges prematurely.

**Auto-enable proxy on connect** — `default: ON`
Automatically activates the Windows system proxy (`127.0.0.1:19052`) as soon as Tor reaches 100%. Disable this if you want to control the proxy manually.

---

###  Bridges

**Bridges written to torrc** — `default: 100` — range: 5–300
How many bridge lines are written to the `torrc` file for each connection attempt. With shuffle enabled, a different random subset is selected each time.

**Shuffle bridge order** — `default: ON`
Randomises the order of bridges written to `torrc` each session. This ensures different bridges are attempted across restarts, improving the chance of finding a working one.

---

###  Privacy / DNS

**DNS over Tor (DNSPort 9053)** — `default: OFF`
Opens a local DNS port at `127.0.0.1:9053` that routes all DNS queries through the Tor network. Requires applications to be manually configured to use this DNS port. By default this is unnecessary — the built-in HTTP proxy already handles DNS resolution via Tor for most apps.

---

###  Circuit Building

These three settings are tuned for maximum stability and are written directly to `torrc`.

**MaxCircuitDirtiness** — `default: 1800s` — range: 60–7200s
How long a circuit can be reused for new streams before Tor builds a fresh one. A higher value means circuits live longer, resulting in fewer rebuilds and more stable connections. The Tor default is 600s; this app uses 1800s for better stability.

**NewCircuitPeriod** — `default: 10s` — range: 5–300s
How frequently Tor pre-builds new circuits in the background so one is always ready. Lower values mean a fresh circuit is available sooner. The Tor default is 30s; this app uses 10s.

**NumEntryGuards** — `default: 15` — range: 1–30
Number of guard nodes Tor keeps in rotation as entry points. More guards means more fallback options if a guard becomes unreachable. The Tor default is 1; this app uses 15 for resilience in censored environments.

---

###  Keep-Alive

**Keep-Alive enabled** — `default: ON`
Periodically sends a lightweight request through Tor to prevent idle connections from being dropped by ISPs or middleboxes that time out inactive TCP connections.

**Keep-Alive interval** — `default: 120s` — range: 30–600s
How often the keep-alive request is sent. 120 seconds is well within the timeout window of most ISPs while avoiding unnecessary bandwidth use.

---

###  Watchdog

**Watchdog enabled** — `default: ON`
Runs a background monitor that checks whether the Tor process is still alive. If Tor has crashed or stopped responding, the watchdog automatically restarts it.

**Check interval** — `default: 30s` — range: 10–300s
How often the watchdog checks the Tor process. 30 seconds is a good balance between responsiveness and overhead.

---

###  Exit Nodes

**Enable Exit Nodes filter** — `default: OFF`
When enabled, restricts which countries Tor may use as exit nodes. Useful when services like YouTube or Instagram block traffic from certain exit node countries.

**Countries (torrc format)** — `default: {nl},{de},{fr},{ch},{at},{se},{no},{fi},{is}`
Comma-separated list of two-letter country codes in torrc format (wrapped in curly braces). The default set covers Netherlands, Germany, France, Switzerland, Austria, Sweden, Norway, Finland, and Iceland — all generally less restricted by Google and Meta.

**StrictNodes** — `default: OFF`
When OFF, Tor falls back to any country if none of the preferred exit countries are available. When ON, Tor refuses to connect if no circuit through the specified countries can be built. Keep OFF unless a specific country is strictly required.

---

### ️ Maintenance

**Clear Data Directory** — clears Tor's cached state, consensus documents, and built circuits. Tor will rebuild everything from scratch on next start. Useful when Tor is stuck or behaving abnormally.

---

## 🔥 Experimental Settings

>  All options in this section are **OFF / 0 by default**. These map directly to `torrc` directives. Incorrect values can break connectivity. Restart Tor after any change.

---

### ― Connection & Padding

**ConnectionPadding** — `default: OFF`
Sends dummy traffic between Tor and its guards to make traffic patterns harder to analyse. Increases bandwidth usage noticeably. Enable only if traffic-shape analysis by your ISP is a concern.

**ReducedConnectionPadding** — `default: OFF`
A lighter version of ConnectionPadding. Provides some resistance to traffic analysis with less bandwidth overhead. Mutually exclusive with ConnectionPadding — enable one or the other, not both.

---

### ― Streams & Timeouts

**CircuitStreamTimeout** — `default: 0 (Tor default)` — range: 0–3600s
How long an idle stream can be attached to a circuit before it is closed. `0` lets Tor use its internal default. Common value: `60`.

**SocksTimeout** — `default: 0 (Tor default ≈ 120s)` — range: 0–600s
How long Tor waits for an unanswered SOCKS connection before giving up. `0` lets Tor use its internal default of approximately 120 seconds. Common value: `60`.

---

### ― Stream Isolation

**IsolateDestAddr** — `default: OFF`
Assigns a separate circuit to each distinct destination IP address. Increases privacy by preventing correlation between connections to different sites, but uses significantly more circuits and memory. Common use: privacy-sensitive multi-tab browsing.

**IsolateDestPort** — `default: OFF`
Assigns a separate circuit to each distinct destination port. Can be combined with IsolateDestAddr for maximum stream isolation.

---

### ― Security & Disk

**SafeLogging** — `default: OFF`
Scrubs potentially identifying information (IP addresses, hostnames) from Tor's log output. Recommended if log files might be inspected by a third party.

**AvoidDiskWrites** — `default: OFF`
Minimises how often Tor writes state to disk. Useful on SSDs to reduce write wear, and adds a small privacy benefit by keeping less on disk.

**HardwareAccel** — `default: OFF`
Enables CPU hardware AES acceleration for Tor's cryptographic operations. Can improve throughput on CPUs with AES-NI. Most modern CPUs support this; try enabling it if CPU usage is high.

**ClientDNSRejectInternalAddresses** — `default: OFF`
Rejects DNS responses that resolve to private or internal IP ranges (e.g. 192.168.x.x, 10.x.x.x). Protects against DNS rebinding attacks where a malicious site's DNS resolves to a local network address.

---

### ― Firewall & Network

**FascistFirewall** — `default: OFF`
Restricts Tor to only connect through ports 80 and 443. Use this if you are behind a corporate or government firewall that blocks all non-web ports. Must be combined with the FirewallPorts setting.

**FirewallPorts** — `default: "80,443"` (only active when FascistFirewall is ON)
Comma-separated list of allowed outbound ports when FascistFirewall is enabled. Common value: `80,443`.

**ReachableAddresses** — `default: empty (all addresses allowed)`
Restricts Tor to only connect to IP ranges specified in CIDR notation. Leave empty unless your network blocks outbound connections to specific IP ranges. Common value: `*:80,*:443`.

**NumCPUs** — `default: 0 (auto-detect)`
Number of CPU threads Tor may use. `0` lets Tor detect the available core count automatically. Set to a specific number only to cap CPU usage.

---

### ― Node Selection

**ExcludeNodes** — `default: empty`
Countries or specific nodes that Tor should never use in any position in a circuit. Example: `{ru},{cn},{ir}`. Use with caution — excluding too many countries reduces the available node pool.

**ExcludeExitNodes** — `default: empty`
Same as ExcludeNodes but only applies to the exit position. Useful for blocking specific country exit nodes without affecting guard or middle nodes. Example: `{ru},{cn}`.

**Reject exit ports** — `default: empty`
Comma-separated list of destination ports Tor should refuse to exit to. Example: `25,587` blocks outbound email ports, which are commonly abused and may get the exit node flagged.

**UseEntryGuardsAsDirGuards** — `default: OFF`
Reuses entry guard nodes for directory fetches instead of contacting separate directory servers. Reduces the number of distinct Tor nodes your client contacts, improving fingerprinting resistance slightly.

**PathBiasCircThreshold** — `default: 0 (Tor default)`
Number of circuits Tor builds before its path bias detection algorithm begins evaluating whether certain paths are suspiciously unreliable. `0` uses Tor's internal default. Common value: `20`.

---


##🔥  File Structure

```
<extract_dir>/
├── tor/
│   └── tor.exe              # Tor binary (auto-downloaded)
├── bridges/                 # Downloaded bridge list files
├── logs/                    # Tor log files
└── tor_client_config.json   # App configuration
```

---

## 🔥 Related Projects

- [Delta-Kronecker/Tor-Bridges-Collector](https://github.com/Delta-Kronecker/Tor-Bridges-Collector) — Bridge source used by this app
- [Delta-Kronecker/Tor-Expert-Bundle](https://github.com/Delta-Kronecker/Tor-Expert-Bundle) — GitHub mirror of Tor Expert Bundle
- [Tor Project](https://www.torproject.org/) — Official Tor Project website

---

## 🔥 Keep This Project Going!

If you're finding this useful, please show your support:

⭐ **Star the repository on GitHub**

⭐ **Star our [Telegram posts](https://t.me/DeltaKroneckerGithub)** 

Your stars fuel our motivation to keep improving!

---

## ⚠️ Disclaimer

This project is for educational purposes. It does not provide anonymity guarantees beyond what the Tor network itself offers. Use responsibly and in accordance with your local laws.
