# 🧅 Delta Tor — Windows GUI

> The most bridge-rich Tor client for Windows. Multi-Connect runs up to 4 simultaneous Tor tunnels, continuously pings each one, and can automatically route your traffic through the fastest — no configuration needed.

---

## ⬇️ Download

### Pre-built Release (Recommended)

Download the latest release archive directly:

**[⬇️ DeltaTor-1.2.2.rar](https://github.com/Delta-Kronecker/Delta-Tor/releases/download/1.2.2/DeltaTor-1.2.2.rar)**

The archive contains `DeltaTor_Setup.exe`, the Tor core binaries, and everything else needed. Extract and run `DeltaTor_Setup.exe`.

### Running from Source

Download the repository ZIP and run with Python:

**[⬇️ Source Code (ZIP)](https://github.com/Delta-Kronecker/Delta-Tor/archive/refs/heads/main.zip)**

```bash
python DeltaTor.py
```

No third-party packages — Python 3.8+ and standard library only.

---


## ⚡ Multi-Connect — The Core Feature

**Multi-Connect is what sets Delta Tor apart from every other Tor client.**

Instead of trying one bridge type and hoping it works, Multi-Connect launches **4 independent Tor instances simultaneously** — each using a completely different protocol. They all race to connect. As each slot reaches 100% bootstrap, Delta Tor starts **continuously pinging it every 15 seconds** to measure live latency and confirm it is still healthy.

### Continuous Ping & Smart Proxy

This is what makes Multi-Connect truly different from just running multiple Tor instances:

```
Every 15 seconds, for each connected slot:
  → Send a real HTTPS request through that Tor tunnel
  → Measure round-trip latency in milliseconds
  → Update the health indicator on the card

  Snowflake     ⬤ Online  142 ms
  obfs4 IPv4    ⬤ Online   38 ms   ← lowest latency
  Vanilla IPv4  ⬤ Online   61 ms
  WebTunnel     ⬤ Offline
```

With **Auto Proxy** enabled, the system proxy is **automatically switched to the slot with the lowest latency** the moment a healthier connection is detected. If your best connection drops, the proxy moves to the next healthy slot without any interruption and without any input from you.

> **Auto Proxy is OFF by default.** Enable it from the Multi-Connect panel when you want fully automatic proxy management. When it is off, you choose which slot to use by clicking **Set Proxy** on any card.

### How the race works

```
Launch Multi-Connect →

  [Snowflake]      ████░░░░░░  40%   ping: —
  [obfs4 IPv4]     ██████████  100%  ping: 38ms  ✔ proxy assigned
  [Vanilla IPv4]   ████████░░  80%   ping: —
  [WebTunnel]      ██████░░░░  60%   ping: —

  → obfs4 reached 100% first → system proxy → 127.0.0.1:19062
  → Other slots continue bootstrapping and come online as hot standby
  → 15s later: health check runs on all connected slots
  → If Vanilla comes in at 22ms, Auto Proxy switches proxy to Vanilla
```

Every slot runs in its own thread with its own Tor process, its own data directory, and its own ports. Nothing is shared. If your ISP blocks obfs4 entirely, WebTunnel or Snowflake wins the race — automatically.

### Default slots

| Slot | Source | Protocol | Notes |
|---|---|---|---|
| **Snowflake** | Default (Built-in) | snowflake | Uses WebRTC — extremely hard to block |
| **obfs4** | Delta-Kronecker · Tested & Active | obfs4 | Best for Iran, Russia, China |
| **Vanilla** | Delta-Kronecker · Tested & Active | vanilla | Plain Tor — fastest when unblocked |
| **WebTunnel** | Delta-Kronecker · Tested & Active | webtunnel | Disguised as HTTPS — bypasses DPI |

### Per-slot controls

Every slot has its own card showing live status, bootstrap progress, latency, and port info:

- **Enable / Disable** — pause or resume any slot while others keep running
- **Set Proxy** — manually point the system proxy at this specific slot
- **Retry** — manually restart a failed slot
- **Health** — instant on-demand latency check
- **Log** — full-screen live Tor log viewer for that slot
- **🗑** — remove this slot permanently

### Adding custom slots

Click **➕ Add Connection Mode** at the bottom. Choose:

| Source | Transports | Category |
|---|---|---|
| Default (Built-in) | snowflake, meek | — |
| Delta-Kronecker | obfs4, webtunnel, vanilla | Tested & Active / Fresh (72h) / Full Archive |
| Direct (No Bridge) | — | — |

### Isolated ports per slot

| Slot index | SOCKS5 | HTTP Proxy |
|---|---|---|
| 0 | 9061 | 19061 |
| 1 | 9062 | 19062 |
| 2 | 9063 | 19063 |
| 3 | 9064 | 19064 |

---

## 🌉 Bridge Collection — Richest Source Available

Delta Tor pulls all bridges from **[Delta-Kronecker/Tor-Bridges-Collector](https://github.com/Delta-Kronecker/Tor-Bridges-Collector)** — the largest continuously-updated Tor bridge repository available. **15 bridge files** across 3 tiers and 3 transport types, covering both IPv4 and IPv6.

### Fresh (72h) — Auto-updated on every launch

The moment Delta Tor opens, it silently downloads all 6 Fresh bridge files in parallel before your first connection attempt. By the time you click Connect, you already have the newest bridges available anywhere.

| File | Content |
|---|---|
| `obfs4_72h.txt` | obfs4 · collected last 72h · IPv4 |
| `obfs4_ipv6_72h.txt` | obfs4 · collected last 72h · IPv6 |
| `webtunnel_72h.txt` | webtunnel · collected last 72h · IPv4 |
| `webtunnel_ipv6_72h.txt` | webtunnel · collected last 72h · IPv6 |
| `vanilla_72h.txt` | vanilla · collected last 72h · IPv4 |
| `vanilla_ipv6_72h.txt` | vanilla · collected last 72h · IPv6 |

### Tested & Active — Highest quality

These bridges are not just collected — they are **tested and confirmed working** before being listed. The obfs4 Tested & Active file is the single best choice for most users.

| File | Content |
|---|---|
| `obfs4_tested.txt` | Confirmed working obfs4 bridges |
| `webtunnel_tested.txt` | Confirmed working webtunnel bridges |
| `vanilla_tested.txt` | Confirmed working vanilla bridges |

### Full Archive — Maximum fallback

When everything else fails, the Full Archive gives you the largest possible pool.

| File | Content |
|---|---|
| `obfs4.txt` / `obfs4_ipv6.txt` | Complete obfs4 archive — IPv4 + IPv6 |
| `webtunnel.txt` / `webtunnel_ipv6.txt` | Complete webtunnel archive — IPv4 + IPv6 |
| `vanilla.txt` / `vanilla_ipv6.txt` | Complete vanilla archive — IPv4 + IPv6 |

### Update behavior

| Category | When updated |
|---|---|
| **Fresh (72h)** | **Automatically on every launch — no action needed** |
| Tested & Active | Manual — **↺ Update Bridges** button |
| Full Archive | Manual — **↺ Update Bridges** button |

Clicking **↺ Update Bridges** downloads all 15 files in parallel in the background. The UI stays fully responsive.

---

## 🔄 Auto-Connect

### Phase 1 — Connection Memory

`tor_client_config.json` stores the last successful bridge configuration. On next launch, that exact configuration is tried first. Most users reconnect immediately on the second launch without cycling.

### Phase 2 — Priority Sequence

| # | Category | Transport | IP |
|---|---|---|---|
| 1 | Tested & Active | obfs4 | IPv4 |
| 2 | Tested & Active | vanilla | IPv4 |
| 3 | Tested & Active | webtunnel | IPv4 |
| 4 | Fresh (72h) | obfs4 | IPv4 |
| 5 | Fresh (72h) | vanilla | IPv4 |
| 6 | Fresh (72h) | webtunnel | IPv4 |
| 7 | Full Archive | obfs4 | IPv4 |
| 8 | Full Archive | vanilla | IPv4 |
| 9 | Full Archive | webtunnel | IPv4 |

### Stall-based timeout

The timer resets every time bootstrap percentage moves. A config is abandoned only if bootstrap stays completely frozen for the full timeout (default: 180s). Slow but progressing connections are never cut off.

---

## 🔥 Requirements

- Windows 10 / 11 (x86_64)
- **Release archive:** nothing — extract and run
- **Source:** Python 3.8+, no third-party packages

---

## 🔥 All Features

- **Multi-Connect** — 4 parallel Tor tunnels with continuous live ping and auto proxy switching
- **Auto-Connect** — 9-step sequence with connection memory
- **Bridge auto-update** — Fresh (72h) bridges fetched in parallel on every launch
- **15 bridge files** across Tested, Fresh, and Full Archive tiers
- **Bridge Scanner** — scan any bridge file for reachable bridges with configurable workers and timeout
- **Custom Bridges** — enter your own bridge lines with built-in ping tester
- **HTTP Proxy** on `127.0.0.1:19052` and **SOCKS5** on `127.0.0.1:9050`
- **System Proxy Integration** — one-click or automatic toggle
- **New Circuit** — fresh exit IP without restarting Tor
- **Exit Node Filtering** — restrict exit nodes to specific countries
- **Keep-Alive** — prevents ISP from dropping idle connections
- **Watchdog** — auto-restarts Tor on crash
- **System Tray** — minimise to tray, right-click menu, desktop notifications
- **SNI Override** — override TLS SNI hostname during bridge handshake
- **Per-Monitor DPI awareness** — sharp text on Windows 11
- **Dark title bar** — title bar matches app theme
- **Full Settings UI** — all torrc options configurable

---

## 🔥 Proxy Addresses

| Protocol | Address |
|---|---|
| HTTP Proxy | `127.0.0.1:19052` |
| SOCKS5 | `127.0.0.1:9050` |

Chrome, Edge, Telegram, and all Windows apps use the system proxy automatically. DNS resolved by Tor — no leaks.

---

## 🔥 Settings Reference

### Auto-Connect
- **Timeout per config** `default: 180s`
- **Auto-enable proxy on connect** `default: OFF`

### Bridges
- **Bridges written to torrc** `default: 100`
- **Shuffle bridge order** `default: ON`

### SNI
- **Enable SNI override** `default: OFF` · **hostname** `default: www.google.com`

### Privacy / DNS
- **DNS over Tor (DNSPort 9053)** `default: OFF`

### Circuit Building
- **MaxCircuitDirtiness** `default: 1800s` · **NewCircuitPeriod** `default: 10s` · **NumEntryGuards** `default: 15`

### Keep-Alive
- **Enabled** `default: ON` · **Interval** `default: 120s`

### Watchdog
- **Enabled** `default: ON` · **Interval** `default: 30s`

### Exit Nodes
- **Filter** `default: OFF` · **Countries** `default: {nl},{de},{fr},{ch},{at},{se},{no},{fi},{is}` · **StrictNodes** `default: OFF`

---

## 🔥 Experimental Settings

All **OFF / 0 by default**. Restart Tor after changes.

| Setting | Description |
|---|---|
| ConnectionPadding | Dummy traffic against traffic shape analysis |
| ReducedConnectionPadding | Lighter padding |
| CircuitStreamTimeout | Idle stream timeout |
| SocksTimeout | SOCKS connection timeout |
| IsolateDestAddr | Separate circuit per destination IP |
| IsolateDestPort | Separate circuit per destination port |
| SafeLogging | Scrub IPs from logs |
| AvoidDiskWrites | Minimise disk writes |
| HardwareAccel | AES-NI CPU acceleration |
| ClientDNSRejectInternalAddresses | Block DNS rebinding |
| FascistFirewall | Ports 80 and 443 only |
| FirewallPorts | Ports when FascistFirewall is ON |
| ReachableAddresses | Restrict outbound IP ranges |
| NumCPUs | CPU threads (0 = auto) |
| ExcludeNodes | Never use in any circuit position |
| ExcludeExitNodes | Never use as exit |
| Reject exit ports | Destination ports to block |
| UseEntryGuardsAsDirGuards | Reuse guards for directory fetches |
| PathBiasCircThreshold | Path bias detection threshold |

---

## 🔥 File Structure

```
<data_dir>/
├── tor/
│   └── tor.exe                  # Tor binary
├── bridges/                     # 15 bridge list files
├── logs/                        # Tor log files
└── tor_client_config.json       # App configuration

AppData\Local\DeltaTor\
└── datadir.txt                  # Points to chosen data directory
```

---

## 🔥 Related Projects

- [Delta-Kronecker/Tor-Bridges-Collector](https://github.com/Delta-Kronecker/Tor-Bridges-Collector) — bridge source powering this app
- [Delta-Kronecker/Tor-Expert-Bundle](https://github.com/Delta-Kronecker/Tor-Expert-Bundle) — Tor Expert Bundle mirror
- [Tor Project](https://www.torproject.org/)

---

## 💎 Support the Project

### ⭐ Free
- **Star this repository**
- **Share** with others who need it
- **Follow** [@DeltaKroneckerGithub](https://t.me/DeltaKroneckerGithub)

### 💰 Donate

**USDT BEP20** (BNB Smart Chain):
```
0x2a434FF74737be5B94634040D010a458507b0741
```
> ⚠️ BEP20 network only — send only USDT on BNB Smart Chain.

Or click **💎 Support the Project** inside the app.

---

## ⚠️ Disclaimer

For educational and personal privacy purposes only. Does not provide anonymity guarantees beyond what the Tor network itself offers. Use responsibly and in accordance with your local laws.
