# Changelog — Delta Tor

## Version 1.3.0 (from 1.2.2)

---

## 1. ARCHITECTURE REWRITE

### Before (v1.2.2)
- Single file Python application: `DeltaTor.py` (4233 lines)
- GUI: tkinter with custom dark theme
- Tor management: subprocess.Popen
- System proxy: ctypes.windll.wininet + winreg
- Bridge storage: hardcoded paths in Python
- Config: JSON file read/written with Python json module
- All logic mixed in one file (GUI, Tor, proxy, bridges, settings)

### After (v1.3.0)
- Go + Wails v2 + HTML/CSS/JS
- Backend: `app.go` (1314 lines) — all system operations
- Frontend: `main.js` (884 lines) + `style.css` (2127 lines)
- Clean separation: Go handles OS, JS handles UI
- Wails framework bridges Go ↔ JavaScript

---

## 2. MAIN WINDOW (800×1000 px)

### Navigation Bar
- Height: 46px
- Left: "Delta Tor" title (Segoe UI 16px bold, blue #3A72B0)
- Right: Help, Settings buttons (Segoe UI 13px, #6B7A94)
- Dark background #1A1F2B
- Border bottom: 1px #2C3347

### Accent Strip
- 3px gradient bar (top of window)
- Color: #3A72B0 → #4D88C8
- Shimmer animation

### Status Bar (removed in final version)
- Was 32px height, showed "Initializing..."
- Removed per user request

### Bridge Configuration Card
- Source dropdown: Default (Built-in), Delta-Kronecker, Custom Bridges
- Category dropdown: Tested & Active, Fresh (72h), Full Archive
- Transport dropdown: obfs4, webtunnel, vanilla
- IP Version dropdown: Both, IPv4, IPv6
- Buttons: [Bridge Info] [Update Bridges]
- All dropdowns have hover/focus effects with border glow

### Button Card — CONNECTION Group
- Start/Stop toggle button (green gradient, glow animation)
- Auto button
- System Proxy toggle button (blinks after 100% connection)

### Button Card — TOOLS Group
- Scanner, Test Connection, New Circuit (smaller, secondary style)

### Progress Bar
- Height: 24px, gradient fill (#3A72B0)
- Pulse animation on fill
- "Progress :" label + percentage

### Connection Status Dashboard
- 6 stat boxes in 3×2 grid
- Status (green dot), Exit IP, Country, Uptime, Download, Upload
- Each box has hover effect with elevation
- Bounce animation on icons
- Scale-in animation on page load

---

## 3. MULTI-CONNECT MODE

### Golden Multi-Connect Card (top of main page)
- Gradient background: #0F2840 → #1A3A5C → #1F4878
- Border: 1px #2A5A8C
- Text: "Multi-Connect" in gold #D4A840
- Subtitle: "Click to use the full power of Tor network"
- Permanent glow animation (multiGlow)
- Rotating radial gradient background (multiRotate)
- Hover: translateY(-3px) + gold box-shadow

### Toolbar
- "Normal" button (back to main)
- "Start" button (green gradient)
- "Stop" button (red text)
- "Auto Proxy: OFF/ON" toggle

### Slot Cards (full width, vertical list)
- Blue accent bar (4px, position: absolute, full height)
- Top row: Toggle checkbox + Label + Action buttons (inline)
- Meta row: Source · Category · Transport · IP | SOCKS port · HTTP port
- Progress row: "Progress : 0%" + progress bar (14px height)
- Stats grid: 6 boxes (Status, Exit IP, Country, Uptime, Down, Up)
- Toggle: green square with pulse animation when ON
- Buttons: Set Proxy, Retry, Health, Log, Delete (inline, small)
- Scale-in animation with staggered delays per card

### Add Connection Mode Dialog
- Name input
- Source dropdown (Built-in, Delta-Kronecker, Direct)
- Category, Transport, IP Version dropdowns
- Dynamic enable/disable based on source selection

---

## 4. SETTINGS PAGE

### Structure
- Back button + "Settings" title + Apply & Save button
- Scrollable content with sections

### Sections Implemented
1. **Auto-Connect**: Timeout per config (30-600s), Auto-enable proxy
2. **Bridges**: Bridges written to torrc (5-300), Shuffle bridge order
3. **SNI Settings**: Enable SNI override, SNI hostname
4. **Privacy/DNS**: DNS over Tor (DNSPort 9053)
5. **Circuit Building**: MaxCircuitDirtiness, NewCircuitPeriod, NumEntryGuards
6. **Keep-Alive**: Enabled, Interval (30-600s)
7. **Watchdog**: Enabled, Check interval (10-300s)
8. **Exit Nodes**: Enable filter, Countries (torrc format), StrictNodes
9. **Maintenance**: Clear Data Directory, Change Data Folder
10. **Experimental**: ConnectionPadding, ReducedConnectionPadding, CircuitStreamTimeout, SocksTimeout, IsolateDestAddr, IsolateDestPort, SafeLogging, AvoidDiskWrites, HardwareAccel, ClientDNSRejectInternalAddresses, FascistFirewall, FirewallPorts, ReachableAddresses, NumCPUs, ExcludeNodes, ExcludeExitNodes, Reject exit ports, UseEntryGuardsAsDirGuards, PathBiasCircThreshold

### Visual Design
- Section headers with left border (blue, 3px)
- Toggle switches with smooth animation
- Input fields with focus glow
- Warning banner for experimental settings (yellow border)
- Hover effects on rows

---

## 5. HELP PAGE

### Sections
1. **QUICK START**: 4-step guide
2. **FEATURES**: Custom Bridges, SNI Override, Bridge Scanner, Multi-Connect, Auto Connect
3. **SYSTEM PROXY**: HTTP proxy 127.0.0.1:9060, SOCKS5 127.0.0.1:9050
4. **BRIDGE TYPES**: obfs4, webtunnel, vanilla, snowflake descriptions
5. **TROUBLESHOOTING**: Common issues and solutions
6. **COMMUNITY**: GitHub, Telegram, Bridges Collector links
7. **SUPPORT THE PROJECT**: USDT BEP20 wallet address with copy button
8. **Disclaimer**: Educational use only

---

## 6. BRIDGE SCANNER

### Controls
- Category: Tested & Active, Fresh (72h), Full Archive
- Transport: obfs4, webtunnel, vanilla
- IP: IPv4, IPv6
- Workers: 1-50 (default 20)
- Timeout: 1-30s (default 5s)

### Results
- Progress bar with label
- Table: Bridge Type, Host, Port, Ping (ms), Status
- Green = reachable, Red = unreachable, Yellow = slow
- Summary: X reachable / Y unreachable / Z total
- Start Scan, Stop, Export Working buttons

---

## 7. BRIDGE INFO

### Overview Stats
- 4 stat boxes: Total Files, Total Bridges, Transports, Categories
- Hover effect with elevation

### 3 Tables (in order)
1. **Full Archive**: obfs4 IPv4/IPv6, webtunnel IPv4/IPv6, vanilla IPv4/IPv6
2. **Tested & Active**: obfs4 IPv4, vanilla IPv4, webtunnel IPv4
3. **Fresh (72h)**: obfs4 IPv4/IPv6, webtunnel IPv4/IPv6, vanilla IPv4/IPv6

### Table Columns
- Transport, IP, File, Bridges (count), Updated (YYYY-MM-DD HH:MM:SS)

### Data Source
- Reads from `{dataDir}/bridges/` directory
- Counts non-empty, non-comment lines in each file
- Gets file modification time for Updated column
- Overview calculates totals across all files

### Update Bridges
- Downloads 15 files from GitHub (Delta-Kronecker/Tor-Bridges-Collector)
- 3 concurrent workers
- Retry logic (4 attempts with exponential backoff)
- Progress reported in Tor Logs

---

## 8. TOR CONNECTION (Go Backend)

### Start/Stop
- `StartTor(cat, trans, ip, source)`: Launches tor.exe with generated torrc
- `StopTor()`: Sends SIGINT, waits 2s, then kills process
- Reads stdout line by line for bootstrap progress
- Emits `tor:progress`, `tor:connected`, `tor:log` events

### torrc Generation
- `GenerateTorrc()`: Creates torrc with all settings from config
- SOCKS port: 9050, Control port: 9051
- Bridge lines from files in `{dataDir}/bridges/`
- Transport plugins: lyrebird.exe, conjure-client.exe
- All experimental settings applied if enabled

### Connection Monitoring
- Regex: `Bootstrapped (\d+)%`
- At 100%: emits `tor:connected` event
- On disconnect: emits `tor:stopped` event

---

## 9. TEST CONNECTION (Go Backend)

### Exit IP Detection
- `TestConnection()` → `TestResult{ip, country, isTor}`
- Connects through Tor SOCKS5 to `check.torproject.org:443/api/ip`
- Parses JSON response: `{"IsTor":true,"IP":"x.x.x.x"}`

### Country Lookup
- `lookupCountry(ip)` → country name
- Uses `api.ip2location.io/?ip={ip}` through Tor
- Returns full country name (e.g., "Germany" not "DE")

### SOCKS5 Request Helper
- `socks5Request()`: Full SOCKS5 handshake + HTTP request through Tor
- Handles both SSL and non-SSL connections
- Strips HTTP headers, returns only body

---

## 10. SYSTEM PROXY (Go Backend)

### Set Proxy
- Uses Windows API directly (advapi32.dll)
- `RegOpenKeyExW` → `RegSetValueExW` for ProxyEnable=1
- `RegSetValueExW` for ProxyServer=127.0.0.1:9060
- `RegSetValueExW` for ProxyOverride=127.0.0.1;localhost;<local>
- `InternetSetOptionW(39)` and `InternetSetOptionW(37)` to notify system
- All done through `syscall.Syscall` with `unsafe.Pointer`

### Unset Proxy
- Same API calls with ProxyEnable=0 and empty strings
- Proper cleanup on Tor disconnect

### Manual Toggle
- Frontend button calls `SetSystemProxy()` / `UnsetSystemProxy()`
- No automatic proxy setting (removed per user request)

---

## 11. HTTP PROXY (Go Backend)

### Server
- Listens on `127.0.0.1:9060`
- Accepts both HTTP and SOCKS5 connections
- Started when user clicks System Proxy button

### HTTP CONNECT Handler
- Parses CONNECT request
- Connects to target through Tor SOCKS5 (port 9050)
- Sends `HTTP/1.1 200 Connection established\r\n\r\n` to client
- Relays data bidirectionally

### HTTP GET/POST Handler
- Parses request URL
- Rewrites first line (removes absolute URL)
- Connects to target through Tor SOCKS5
- Relays modified request + body
- Relays response back to client

### Traffic Monitoring
- `relayData()`: Counts bytes in both directions
- `GetTrafficStats()`: Returns speed (bytes/sec since last call)
- `formatSpeed()`: Formats as B/s, KB/s, or MB/s
- Updated every 2 seconds in frontend

---

## 12. BRIDGE MANAGEMENT (Go Backend)

### Bridge Data
- 15 bridge files from Delta-Kronecker/Tor-Bridges-Collector
- 3 categories: Tested & Active, Fresh (72h), Full Archive
- 3 transports: obfs4, webtunnel, vanilla
- 2 IP versions: IPv4, IPv6

### File Naming
- `{Category}_{Transport}_{IP}.txt`
- Example: `Tested_and_Active_obfs4_IPv4.txt`

### Download
- `DownloadAllBridges()`: Downloads all 15 files concurrently (3 workers)
- Retry: 4 attempts with exponential backoff (1s, 2s, 4s, 8s)
- Progress events emitted to frontend

### Reading
- `GetBridgeInfo()`: Reads all 15 files
- Counts non-empty, non-comment lines
- Gets file modification time
- Returns structured data to frontend

---

## 13. CONFIGURATION (Go Backend)

### Config File
- Path: `{dataDir}/tor_client_config.json`
- Format: JSON with all settings
- Auto-creates with defaults on first run

### Settings Managed
- Auto-connect timeout, bridges count, shuffle
- DNS over Tor, circuit building params
- Keep-alive, watchdog intervals
- Exit nodes filter
- SNI override
- All experimental torrc options
- Last successful connection memory

### Data Directory
- `AppData\Local\DeltaTor\datadir.txt` points to actual data dir
- Default: `AppData\Local\DeltaTor\`
- Creates bridges/, tor/, data/ subdirectories

---

## 14. VISUAL DESIGN

### Color Palette
| Name | Hex | Usage |
|------|-----|-------|
| BG | #13171F | Window background |
| PANEL | #1A1F2B | Cards, nav bar |
| CARD | #1F2535 | Stats, status bar |
| BORDER | #2C3347 | Borders |
| FG | #C8D0DC | Primary text |
| FG2 | #6B7A94 | Secondary text |
| ACC | #3A72B0 | Accent, links |
| ACC2 | #4D88C8 | Accent hover |
| GRN | #2EB87A | Success, connected |
| RED | #D95555 | Error, stop |
| YLW | #C9A020 | Warning, donate |
| CYAN | #3AA8C0 | Ports, bridges |
| BTN | #1E2535 | Button bg |
| BTN2 | #273048 | Button hover |

### Animations (CSS)
- `fadeIn`: Page load opacity
- `slideUp`: Cards slide up with fade
- `slideLeft`: Settings sections slide from left
- `scaleIn`: Stat boxes scale in
- `shimmer`: Accent strip shimmer effect
- `bounce`: Icons bounce continuously
- `pulse`: Status dot pulse
- `fillPulse`: Progress bar brightness pulse
- `multiGlow`: Multi-Connect gold glow
- `multiRotate`: Multi-Connect rotating gradient
- `blinkProxy`: System Proxy button blink after 100%
- `glow`: Start button green glow
- `glowRed`: Stop button red glow

### Button Styles
- **Start**: Green gradient, glow animation, translateY hover
- **Stop**: Red gradient, red glow
- **Auto**: Dark with border hover
- **System Proxy**: Toggle with blink animation when ready
- **Multi-Connect**: Gold gradient with permanent glow
- **Secondary**: Small, muted, subtle hover
- **All buttons**: `active: scale(0.95)`, `hover: translateY(-2px)`

### Cards
- Border-radius: 6px
- Box-shadow with hover elevation
- Staggered slide-up animation on load
- Shimmer accent strip at top

---

## 15. WHAT'S IMPLEMENTED (Go Backend)

| Feature | Status |
|---------|--------|
| Tor process start/stop | Done |
| torrc generation | Done |
| Bridge file reading | Done |
| Bridge download from GitHub | Done |
| System proxy (Windows Registry API) | Done |
| HTTP proxy (CONNECT/GET/POST) | Done |
| SOCKS5 relay | Done |
| Exit IP detection | Done |
| Country lookup (api.ip2location.io) | Done |
| Traffic byte counting | Done |
| Config load/save (JSON) | Done |
| Data directory management | Done |

---

## 16. WHAT'S REMAINING (Not Yet Implemented)

| Feature | Status |
|---------|--------|
| Multi-Connect slot start/stop | Not started |
| Bridge Scanner TCP-ping backend | Not started |
| Auto-Connect 9-step sequence | Not started |
| Watchdog auto-restart | Not started |
| Keep-Alive ping | Not started |
| New Circuit (SIGNAL NEWNYM) | Not started |
| System tray integration | Not started |
| Desktop notifications | Not started |
| Custom bridge input UI | Not started |
| Bridge file selection dropdown | Not started |
| Port info display | Not started |
| Save log to file | Not started |
| Connection memory (last success) | Not started |

---

## 17. VERSION HISTORY

| Version | Date | Changes |
|---------|------|---------|
| 1.2.2 | Original | Python/tkinter, single file, full features |
| 1.3.0 | Current | Go/Wails rewrite, main window, multi-connect, settings, help, scanner, bridge info, system proxy, HTTP proxy, tor connection, test connection, traffic monitoring |
