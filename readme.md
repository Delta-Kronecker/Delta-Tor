# 🧅 Tor Client — Windows GUI

A desktop application for connecting to the Tor network with bridge support.


https://github.com/user-attachments/assets/961a0b81-64e4-4dd9-bf49-fe74105d2110


---

## ✨ Features

- **🤖 Auto-Connect** — Automatically cycles through up to 9 bridge configurations until a successful connection is established
- **🌉 Bridge Management** — Fetches and manages obfs4, webtunnel, and vanilla bridges from the [Delta-Kronecker Tor-Bridges-Collector](https://github.com/Delta-Kronecker/Tor-Bridges-Collector)
- **📦 Automatic Tor Setup** — Downloads and extracts Tor Expert Bundle on first run with no manual steps
- **🌐 HTTP Proxy** — Built-in HTTP-to-SOCKS5 proxy works with Chrome, Edge, Telegram, and most Windows apps out of the box
- **🔒 System Proxy Integration** — Automatically configures Windows system proxy on successful connection
- **🔄 New Circuit** — Request a fresh Tor circuit (new exit IP) without restarting
- **🌍 Exit Node Filtering** — Optionally restrict exit nodes to specific countries
- **💓 Keep-Alive** — Periodic background requests prevent ISP from dropping idle Tor connections
- **🐕 Watchdog** — Monitors and automatically restarts Tor if it crashes or stalls
- **⚙️ Full Settings UI** — Configurable timeouts, circuit parameters, DNS-over-Tor, experimental torrc options, and more

---

## 🖥️ Requirements

- Windows 10 / 11 (x86_64)
- **EXE:** no requirements — runs standalone
- **Source:** Python 3.8+ only — no third-party packages

---

## 🚀 Quick Start

### ⬇️ Recommended: Download the EXE

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

Then click **🤖 Auto Connect**.

---

## 📦 Tor Expert Bundle

On first run, the application automatically downloads **Tor Expert Bundle v15.0.6** for Windows (x86_64):

**Primary source (GitHub mirror):**
```
https://github.com/Delta-Kronecker/Tor-Expert-Bundle/raw/refs/heads/main/tor-expert-bundle-windows-x86_64-15.0.6.tar.gz
```

**Fallback source (official Tor Project archive):**
```
https://archive.torproject.org/tor-package-archive/torbrowser/15.0.6/tor-expert-bundle-windows-x86_64-15.0.6.tar.gz
```

### ✅ Verifying the Bundle

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

## 🤖 Auto-Connect & Connection Memory

### How Auto-Connect Works

Clicking **🤖 Auto Connect** launches a background thread that works in two distinct phases without blocking the UI.

**Phase 1 — Connection Memory**

Before trying anything new, the app checks whether a previously successful configuration is saved in `tor_client_config.json` (keys: `last_success_cat`, `last_success_trans`, `last_success_ip`). If found, it tries that exact configuration first. This means on most launches the app reconnects immediately using the same bridge type that worked last time, without cycling through the full sequence.

```
Last session used: Tested & Active / obfs4 / IPv4
→ Try that first (up to timeout seconds)
  ├─ ✅ Connected → done, no further attempts
  └─ ❌ Timed out → move to Phase 2
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
Bootstrap at 15% → progress moves to 20% → timer resets ✅
Bootstrap at 15% → no movement for 180s → "Stuck at 15%" → kill → next config ❌
```

This means a slow but progressing connection is never cut off prematurely. The app reads Tor's `stdout` line by line in a blocking loop — there is no polling or `sleep()`. Everything is event-driven from Tor's own log output.

### On Successful Connection

When bootstrap reaches **100%**, the following happen atomically:

1. `last_success_cat / trans / ip` are written to `tor_client_config.json` (connection memory updated)
2. Windows system proxy is enabled automatically (if setting is on)


## 🌐 Proxy Configuration

| Protocol | Address |
|---|---|
| HTTP Proxy | `127.0.0.1:19052` |
| SOCKS5 | `127.0.0.1:9050` |

**Automatic:** Chrome, Edge, Telegram, and most Windows applications use the system proxy automatically.

DNS is resolved remotely by Tor — no DNS leaks.


---

## 🔄 Bridge Update Behavior

**Fresh (72h) bridges are updated automatically every time the app launches.** On startup, all Fresh bridge files are fetched in parallel in the background before any connection attempt — no manual action needed. This ensures the most recently collected bridges are always available.

For all other bridge categories (Tested & Active and Full Archive), updates can be triggered manually at any time by clicking the **"Update All Bridges"** button in the main window. This downloads every bridge file in parallel and is recommended if Auto-Connect is struggling to establish a connection.

| Bridge Category | Update Method |
|---|---|
| Fresh (72h) | ✅ Automatic on every launch |
| Tested & Active | 🔘 Manual — "Update All Bridges" button |
| Full Archive | 🔘 Manual — "Update All Bridges" button |

---



## 🔎 Bridge Categories

| Category | Description |
|---|---|
| **Tested & Active** ⭐ | Verified working bridges — best choice |
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

## ⚙️ Settings

- Auto-connect timeout per configuration
- Number of bridges written to `torrc`
- Bridge shuffling
- DNS-over-Tor (port 9053)
- `MaxCircuitDirtiness`, `NewCircuitPeriod`, `NumEntryGuards`
- Keep-alive interval
- Watchdog check interval
- Exit node country filter + StrictNodes

---

## 🧪 Experimental Settings

Advanced torrc options (all `OFF` by default) are available in Settings → Experimental. These map directly to torrc directives. Incorrect values may break connectivity — restart Tor after any change.

---


## 📁 File Structure

```
<extract_dir>/
├── tor/
│   └── tor.exe              # Tor binary (auto-downloaded)
├── bridges/                 # Downloaded bridge list files
├── logs/                    # Tor log files
└── tor_client_config.json   # App configuration
```

---

## 🔗 Related Projects

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
