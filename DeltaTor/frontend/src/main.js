import './style.css';

const app = document.getElementById('app');

let currentMode = 'normal';
let autoProxyOn = false;

const defaultSlots = [
    { label: 'Snowflake',              source: 'Default (Built-in)',  cat: null,         trans: 'snowflake',  ip: null,   noBridge: false },
    { label: 'obfs4 · Tested IPv4',    source: 'Delta-Kronecker',     cat: 'Tested & Active', trans: 'obfs4',    ip: 'IPv4', noBridge: false },
    { label: 'Vanilla · Tested IPv4',  source: 'Delta-Kronecker',     cat: 'Tested & Active', trans: 'vanilla',  ip: 'IPv4', noBridge: false },
    { label: 'WebTunnel · Tested',     source: 'Delta-Kronecker',     cat: 'Tested & Active', trans: 'webtunnel', ip: 'IPv4', noBridge: false },
];

function slotPorts(index) {
    return { socks: 9061 + index, ctrl: 9071 + index, http: 19061 + index };
}

/* ===== NORMAL MODE ===== */
function renderNormalMode() {
    return `
<div class="accent-strip"></div>
<div class="navbar">
    <span class="title">Delta Tor</span>
    <span class="spacer"></span>
    <button class="nav-btn" onclick="switchToSettings()">Settings</button>
    <button class="nav-btn" onclick="switchToHelp()">Help</button>
</div>

<div class="main-content">

    <!-- Multi-Connect Top -->
    <div class="multi-top-card" onclick="switchToMulti()">
        <div class="multi-top-glow"></div>
        <div class="multi-top-content">
            <div class="multi-top-icon">&#9889;</div>
            <div class="multi-top-text">Multi-Connect</div>
            <div class="multi-top-sub">Click to use the full power of Tor network</div>
        </div>
    </div>

    <!-- Bridge Configuration -->
    <div class="card">
        <div class="card-accent"></div>
        <div class="card-inner">
            <div class="card-title-row">
                <span class="card-title">Bridge Configuration</span>
                <div class="card-title-btns">
                    <button class="btn btn-cyan" onclick="showBridgeInfo()">Bridge Info</button>
                    <button class="btn btn-cyan" onclick="updateBridges()">&#8634; Update Bridges</button>
                </div>
            </div>
            <div class="option-row"><span class="option-label">Source:</span><select class="option-select" id="source"><option>Default (Built-in)</option><option selected>Delta-Kronecker Tor-Bridges-Collector</option><option>Custom Bridges</option></select></div>
            <div class="option-row"><span class="option-label">Category:</span><select class="option-select" id="category"><option selected>Tested &amp; Active</option><option>Fresh (72h)</option><option>Full Archive</option></select></div>
            <div class="option-row"><span class="option-label">Transport:</span><select class="option-select" id="transport"><option selected>obfs4</option><option>webtunnel</option><option>vanilla</option></select></div>
            <div class="option-row"><span class="option-label">IP Version:</span><select class="option-select" id="ipversion"><option>Both</option><option selected>IPv4</option><option>IPv6</option></select></div>
        </div>
    </div>

    <!-- Buttons -->
    <div class="card">
        <div class="card-accent"></div>
        <div class="card-inner">
            <div class="btn-group">
                <div class="btn-group-title">CONNECTION</div>
                <div class="btn-group-row btn-row-3">
                    <button class="btn btn-start-lg" id="startBtn" onclick="toggleStart()">&#9654; Start</button>
                    <button class="btn btn-auto">&#9889; Auto</button>
                    <button class="btn btn-proxy-toggle" id="proxyBtn" onclick="toggleProxy()">System Proxy : OFF</button>
                </div>
            </div>
            <div class="btn-group">
                <div class="btn-group-title">TOOLS</div>
                <div class="btn-group-row">
                    <button class="btn btn-secondary" onclick="switchToScanner()">Scanner</button>
                    <button class="btn btn-secondary">Test Connection</button>
                    <button class="btn btn-secondary">New Circuit</button>
                </div>
            </div>
        </div>
    </div>

    <!-- Progress -->
    <div class="card">
        <div class="card-accent"></div>
        <div class="card-inner">
            <div class="progress-row">
                <span class="progress-label">Progress:</span>
                <span class="progress-pct" id="conn-pct">0%</span>
                <div class="progress-bar"><div class="progress-fill" id="conn-progress"></div></div>
            </div>
        </div>
    </div>

    <!-- Stats -->
    <div class="stats-card">
        <div class="card-accent"></div>
        <div class="card-inner">
            <div class="stats-card-title">Connection Status</div>
            <div class="stats-dashboard">
                <div class="stat-box">
                    <div class="stat-box-icon" style="color:var(--cyan)">IP</div>
                    <div class="stat-box-val" id="stat-ip">&mdash;</div>
                    <div class="stat-box-lbl">Exit IP</div>
                </div>
                <div class="stat-box">
                    <div class="stat-box-icon" style="color:var(--grn)">&#127758;</div>
                    <div class="stat-box-val" id="stat-country">&mdash;</div>
                    <div class="stat-box-lbl">Country</div>
                </div>
                <div class="stat-box">
                    <div class="stat-box-icon" style="color:var(--grn)" id="stat-tor-icon">&#9679;</div>
                    <div class="stat-box-val" id="stat-tor">&mdash;</div>
                    <div class="stat-box-lbl">Status</div>
                </div>
                <div class="stat-box">
                    <div class="stat-box-icon" style="color:var(--ylw)">&#9201;</div>
                    <div class="stat-box-val" id="stat-uptime">&mdash;</div>
                    <div class="stat-box-lbl">Uptime</div>
                </div>
                <div class="stat-box">
                    <div class="stat-box-icon" style="color:var(--acc)">&#11015;</div>
                    <div class="stat-box-val stat-speed" id="stat-download">&mdash;</div>
                    <div class="stat-box-lbl">Download</div>
                </div>
                <div class="stat-box">
                    <div class="stat-box-icon" style="color:var(--org)">&#11014;</div>
                    <div class="stat-box-val stat-speed" id="stat-upload">&mdash;</div>
                    <div class="stat-box-lbl">Upload</div>
                </div>
            </div>
        </div>
    </div>

    <!-- Log Panel -->
    <div class="log-panel" id="logPanel">
        <div class="log-panel-header">
            <span class="log-panel-title">Tor Logs</span>
            <span class="spacer"></span>
            <button class="log-panel-btn" onclick="clearLog()">Clear</button>
            <button class="log-panel-btn" onclick="saveLog()">Save</button>
            <button class="log-panel-toggle" onclick="toggleLogPanel()">&#9660;</button>
        </div>
        <div class="log-panel-body" id="logOutput"></div>
    </div>
    <button class="log-fab" id="logFab" onclick="toggleLogPanel()">Log</button>

</div>`;
}

/* ===== MULTI MODE ===== */
function renderSlotCard(slot, index) {
    const ports = slotPorts(index);
    return `
    <div class="slot-card-full">
        <div class="slot-accent-bar"></div>
        <div class="slot-main-area">
            <div class="slot-top-row">
                <div class="slot-toggle" data-index="${index}"><div class="toggle-box toggle-on"></div></div>
                <span class="slot-label">${slot.label}</span>
                <span class="spacer"></span>
                <div class="slot-actions-inline">
                    <button class="slot-btn-sm">Set Proxy</button>
                    <button class="slot-btn-sm">Retry</button>
                    <button class="slot-btn-sm">Health</button>
                    <button class="slot-btn-sm">Log</button>
                    <button class="slot-btn-sm slot-btn-del">Delete</button>
                </div>
            </div>
            <div class="slot-meta">${slot.source} &middot; ${slot.cat || '&mdash;'} &middot; ${slot.trans} &middot; ${slot.ip || 'auto'} &nbsp;|&nbsp; SOCKS ${ports.socks} &middot; HTTP ${ports.http}</div>
                <div class="slot-progress-row">
                    <span class="slot-progress-label">Progress:</span>
                    <span class="slot-progress-pct">0%</span>
                    <div class="slot-progress-bar"><div class="slot-progress-fill" style="width:0%"></div></div>
                </div>
            <div class="slot-stats-grid">
                <div class="slot-stat-box"><div class="slot-stat-icon" style="color:var(--grn)">&#9679;</div><div class="slot-stat-val">&mdash;</div><div class="slot-stat-lbl">Status</div></div>
                <div class="slot-stat-box"><div class="slot-stat-icon" style="color:var(--cyan)">IP</div><div class="slot-stat-val">&mdash;</div><div class="slot-stat-lbl">Exit IP</div></div>
                <div class="slot-stat-box"><div class="slot-stat-icon" style="color:var(--grn)">&#127758;</div><div class="slot-stat-val">&mdash;</div><div class="slot-stat-lbl">Country</div></div>
                <div class="slot-stat-box"><div class="slot-stat-icon" style="color:var(--ylw)">&#9201;</div><div class="slot-stat-val">&mdash;</div><div class="slot-stat-lbl">Uptime</div></div>
                <div class="slot-stat-box"><div class="slot-stat-icon" style="color:var(--acc)">&#11015;</div><div class="slot-stat-val">&mdash;</div><div class="slot-stat-lbl">Down</div></div>
                <div class="slot-stat-box"><div class="slot-stat-icon" style="color:var(--org)">&#11014;</div><div class="slot-stat-val">&mdash;</div><div class="slot-stat-lbl">Up</div></div>
            </div>
        </div>
    </div>`;
}

function renderMultiMode() {
    const slotsHtml = defaultSlots.map((s, i) => renderSlotCard(s, i)).join('');
    return `
<div class="multi-toolbar">
    <div class="card-accent"></div>
    <div class="multi-toolbar-inner">
        <div class="multi-toolbar-left">
            <button class="btn btn-primary" onclick="switchToNormal()">&#9664; Normal</button>
            <button class="btn btn-start">&#9654; Start</button>
            <button class="btn btn-stop">&#9209; Stop</button>
        </div>
        <button class="btn btn-auto-proxy" id="autoProxyBtn" onclick="toggleAutoProxy()">Auto Proxy : OFF</button>
    </div>
</div>
<div class="multi-separator"></div>
<div class="multi-scroll"><div class="multi-list" id="slotGrid">${slotsHtml}</div></div>
<div class="multi-bottom"><button class="btn-add-mode">+ Add Connection Mode</button></div>`;
}

/* ===== SCANNER MODE ===== */
function renderScannerMode() {
    return `
<div class="scanner-header"><div class="accent-strip"></div>
<div class="scanner-top"><button class="btn btn-primary" onclick="switchToNormal()">&#9664; Back</button><span class="scanner-title">Bridge Scanner</span></div></div>
<div class="scanner-desc">TCP-ping each bridge in the selected file. Green = reachable, Red = unreachable.</div>
<div class="scanner-scroll">
    <div class="scanner-controls">
        <div class="scanner-ctrl-row"><span class="scanner-ctrl-label">Category:</span><select class="scanner-select" id="scan_cat"><option>Tested &amp; Active</option><option>Fresh (72h)</option><option>Full Archive</option></select></div>
        <div class="scanner-ctrl-row"><span class="scanner-ctrl-label">Transport:</span><select class="scanner-select" id="scan_trans"><option>obfs4</option><option>webtunnel</option><option>vanilla</option></select></div>
        <div class="scanner-ctrl-row"><span class="scanner-ctrl-label">IP:</span><select class="scanner-select" id="scan_ip"><option>IPv4</option><option>IPv6</option></select></div>
        <div class="scanner-ctrl-row"><span class="scanner-ctrl-label">Workers:</span><input type="number" class="scanner-input-sm" id="scan_workers" min="1" max="50" value="20"/></div>
        <div class="scanner-ctrl-row"><span class="scanner-ctrl-label">Timeout(s):</span><input type="number" class="scanner-input-sm" id="scan_timeout" min="1" max="30" value="5"/></div>
    </div>
    <div class="scanner-progress-area"><div class="scanner-progress-label" id="scanProgressLabel">Ready.</div><div class="scanner-progress-bar"><div class="scanner-progress-fill" id="scanProgressFill" style="width:0%"></div></div></div>
    <div class="scanner-table-wrap"><table class="scanner-table"><thead><tr><th>Bridge Type</th><th>Host</th><th>Port</th><th>Ping (ms)</th><th>Status</th></tr></thead><tbody id="scanResults"></tbody></table></div>
    <div class="scanner-summary" id="scanSummary"></div>
    <div class="scanner-btns">
        <button class="btn btn-start" onclick="startScan()">&#9654; Start Scan</button>
        <button class="btn btn-stop" onclick="stopScan()">&#9209; Stop</button>
        <button class="btn btn-cyan" onclick="exportScan()">Export Working</button>
    </div>
</div>`;
}

/* ===== SETTINGS MODE ===== */
function renderSettingsMode() {
    return `
<div class="settings-header"><div class="accent-strip"></div>
<div class="settings-top"><button class="btn btn-primary" onclick="switchToNormal()">&#9664; Back</button><span class="settings-title">Settings</span><span class="spacer"></span><button class="btn btn-apply" onclick="applySettings()">&#10003; Apply &amp; Save</button></div></div>
<div class="settings-scroll">
    <div class="settings-section">Auto-Connect</div>
    <div class="settings-row"><span class="settings-label">Timeout per config (sec):</span><input type="number" class="settings-input" id="s_auto_timeout" min="30" max="600" value="180"/></div>
    <div class="settings-hint">How long to wait at a stuck bootstrap % before trying next bridge group.</div>
    <div class="settings-row"><span class="settings-label">Auto-enable proxy on connect:</span><label class="settings-toggle"><input type="checkbox" id="s_auto_proxy"/><span class="toggle-slider"></span></label></div>
    <div class="settings-hint">Automatically turns on System Proxy when Tor reaches 100%.</div>

    <div class="settings-section">Bridges</div>
    <div class="settings-row"><span class="settings-label">Bridges written to torrc:</span><input type="number" class="settings-input" id="s_bridges_count" min="5" max="300" value="100"/></div>
    <div class="settings-hint">Number of bridge lines written into the Tor config file.</div>
    <div class="settings-row"><span class="settings-label">Shuffle bridge order:</span><label class="settings-toggle"><input type="checkbox" id="s_shuffle" checked/><span class="toggle-slider"></span></label></div>
    <div class="settings-hint">Randomising ensures different bridges are tried each session.</div>

    <div class="settings-section">SNI Settings</div>
    <div class="settings-row"><span class="settings-label">Enable SNI override:</span><label class="settings-toggle"><input type="checkbox" id="s_sni_enable"/><span class="toggle-slider"></span></label></div>
    <div class="settings-hint">Overrides the TLS SNI hostname sent during bridge handshake. Useful to mimic popular HTTPS traffic and bypass DPI/censorship.</div>
    <div class="settings-row"><span class="settings-label">SNI hostname:</span><input type="text" class="settings-input settings-input-text" id="s_sni_host" value="www.google.com"/></div>
    <div class="settings-hint">Example: www.google.com | cloudflare.com | cdn.jsdelivr.net</div>

    <div class="settings-section">Privacy / DNS</div>
    <div class="settings-row"><span class="settings-label">DNS over Tor (DNSPort 9053):</span><label class="settings-toggle"><input type="checkbox" id="s_dns_tor"/><span class="toggle-slider"></span></label></div>
    <div class="settings-hint">Routes DNS queries through Tor. Requires apps to use 127.0.0.1:9053.</div>

    <div class="settings-section">Circuit Building</div>
    <div class="settings-row"><span class="settings-label">MaxCircuitDirtiness (sec):</span><input type="number" class="settings-input" id="s_max_circuit" min="60" max="7200" value="1800"/></div>
    <div class="settings-hint">How long a circuit stays alive before a new one is built. Lower = more frequent IP changes.</div>
    <div class="settings-row"><span class="settings-label">NewCircuitPeriod (sec):</span><input type="number" class="settings-input" id="s_new_circuit" min="5" max="300" value="10"/></div>
    <div class="settings-hint">How often Tor checks if a new circuit should be built.</div>
    <div class="settings-row"><span class="settings-label">NumEntryGuards:</span><input type="number" class="settings-input" id="s_entry_guards" min="1" max="30" value="15"/></div>
    <div class="settings-hint">Number of entry guard nodes. More guards = more resilience but slightly slower.</div>

    <div class="settings-section">Keep-Alive</div>
    <div class="settings-row"><span class="settings-label">Keep-Alive enabled:</span><label class="settings-toggle"><input type="checkbox" id="s_keepalive" checked/><span class="toggle-slider"></span></label></div>
    <div class="settings-hint">Sends periodic requests to prevent ISP from dropping idle Tor connections.</div>
    <div class="settings-row"><span class="settings-label">Keep-Alive interval (sec):</span><input type="number" class="settings-input" id="s_keepalive_interval" min="30" max="600" value="120"/></div>
    <div class="settings-hint">How often to ping Tor to keep the connection alive.</div>

    <div class="settings-section">Watchdog</div>
    <div class="settings-row"><span class="settings-label">Watchdog enabled:</span><label class="settings-toggle"><input type="checkbox" id="s_watchdog" checked/><span class="toggle-slider"></span></label></div>
    <div class="settings-hint">Automatically restarts Tor if the process crashes.</div>
    <div class="settings-row"><span class="settings-label">Check interval (sec):</span><input type="number" class="settings-input" id="s_watchdog_interval" min="10" max="300" value="30"/></div>
    <div class="settings-hint">How often to check if Tor is still running.</div>

    <div class="settings-section">Exit Nodes</div>
    <div class="settings-row"><span class="settings-label">Enable Exit Nodes filter:</span><label class="settings-toggle"><input type="checkbox" id="s_exit_enable"/><span class="toggle-slider"></span></label></div>
    <div class="settings-hint">Restrict which countries your exit node can be in.</div>
    <div class="settings-row"><span class="settings-label">Countries (torrc format):</span><input type="text" class="settings-input settings-input-text" id="s_exit_countries" value="{nl},{de},{fr},{ch},{at},{se},{no},{fi},{is}"/></div>
    <div class="settings-hint">Comma-separated country codes in torrc format. Example: {nl},{de},{fr}</div>
    <div class="settings-row"><span class="settings-label">StrictNodes:</span><label class="settings-toggle"><input type="checkbox" id="s_strict_nodes"/><span class="toggle-slider"></span></label></div>
    <div class="settings-hint">If ON, Tor will ONLY use nodes in the specified countries. If OFF, it prefers them but may use others.</div>

    <div class="settings-section">Maintenance</div>
    <div class="settings-hint">Manage cached Tor data and application data directory.</div>
    <div class="settings-row"><button class="btn-clear-data" onclick="clearData()">Clear Data Directory</button></div>
    <div class="settings-row"><button class="btn-data-folder" onclick="changeDataFolder()">Change Data Folder</button></div>

    <div class="settings-section settings-section-exp">Experimental (Advanced torrc)</div>
    <div class="settings-warning">All options below are OFF by default. Wrong settings can break connectivity. Use with caution.</div>

    <div class="settings-subsection">Connection &amp; Padding</div>
    <div class="settings-row"><span class="settings-label">ConnectionPadding:</span><label class="settings-toggle"><input type="checkbox" id="s_conn_pad"/><span class="toggle-slider"></span></label></div>
    <div class="settings-hint">Adds dummy traffic to defend against traffic shape analysis.</div>
    <div class="settings-row"><span class="settings-label">ReducedConnectionPadding:</span><label class="settings-toggle"><input type="checkbox" id="s_reduced_pad"/><span class="toggle-slider"></span></label></div>
    <div class="settings-hint">Lighter version of connection padding.</div>

    <div class="settings-subsection">Streams &amp; Timeouts</div>
    <div class="settings-row"><span class="settings-label">CircuitStreamTimeout (sec):</span><input type="number" class="settings-input" id="s_stream_timeout" min="0" max="3600" value="0"/></div>
    <div class="settings-hint">Idle stream timeout. 0 = use Tor default.</div>
    <div class="settings-row"><span class="settings-label">SocksTimeout (sec):</span><input type="number" class="settings-input" id="s_socks_timeout" min="0" max="600" value="0"/></div>
    <div class="settings-hint">SOCKS connection timeout. 0 = use Tor default.</div>

    <div class="settings-subsection">Stream Isolation</div>
    <div class="settings-row"><span class="settings-label">IsolateDestAddr:</span><label class="settings-toggle"><input type="checkbox" id="s_isolate_addr"/><span class="toggle-slider"></span></label></div>
    <div class="settings-hint">Separate Tor circuit for each destination IP address.</div>
    <div class="settings-row"><span class="settings-label">IsolateDestPort:</span><label class="settings-toggle"><input type="checkbox" id="s_isolate_port"/><span class="toggle-slider"></span></label></div>
    <div class="settings-hint">Separate Tor circuit for each destination port.</div>

    <div class="settings-subsection">Security &amp; Disk</div>
    <div class="settings-row"><span class="settings-label">SafeLogging:</span><label class="settings-toggle"><input type="checkbox" id="s_safe_log"/><span class="toggle-slider"></span></label></div>
    <div class="settings-hint">Scrub IP addresses from Tor logs for privacy.</div>
    <div class="settings-row"><span class="settings-label">AvoidDiskWrites:</span><label class="settings-toggle"><input type="checkbox" id="s_avoid_disk"/><span class="toggle-slider"></span></label></div>
    <div class="settings-hint">Minimise disk writes. Useful for live USB environments.</div>
    <div class="settings-row"><span class="settings-label">HardwareAccel:</span><label class="settings-toggle"><input type="checkbox" id="s_hw_accel"/><span class="toggle-slider"></span></label></div>
    <div class="settings-hint">Enable AES-NI CPU acceleration for faster encryption.</div>
    <div class="settings-row"><span class="settings-label">ClientDNSRejectInternalAddresses:</span><label class="settings-toggle"><input type="checkbox" id="s_dns_reject"/><span class="toggle-slider"></span></label></div>
    <div class="settings-hint">Block DNS rebinding attacks.</div>

    <div class="settings-subsection">Firewall &amp; Network</div>
    <div class="settings-row"><span class="settings-label">FascistFirewall:</span><label class="settings-toggle"><input type="checkbox" id="s_fascist"/><span class="toggle-slider"></span></label></div>
    <div class="settings-hint">Only allow connections to ports 80 and 443.</div>
    <div class="settings-row"><span class="settings-label">FirewallPorts:</span><input type="text" class="settings-input settings-input-text" id="s_fw_ports" value="80,443"/></div>
    <div class="settings-hint">Allowed ports when FascistFirewall is ON.</div>
    <div class="settings-row"><span class="settings-label">ReachableAddresses:</span><input type="text" class="settings-input settings-input-text" id="s_reachable" value=""/></div>
    <div class="settings-hint">Restrict outbound IP ranges for Tor connections.</div>
    <div class="settings-row"><span class="settings-label">NumCPUs:</span><input type="number" class="settings-input" id="s_num_cpus" min="0" max="32" value="0"/></div>
    <div class="settings-hint">Number of CPU threads for Tor. 0 = auto.</div>

    <div class="settings-subsection">Node Selection</div>
    <div class="settings-row"><span class="settings-label">ExcludeNodes:</span><input type="text" class="settings-input settings-input-text" id="s_exclude_nodes" value=""/></div>
    <div class="settings-hint">Nodes to never use in any circuit position.</div>
    <div class="settings-row"><span class="settings-label">ExcludeExitNodes:</span><input type="text" class="settings-input settings-input-text" id="s_exclude_exit" value=""/></div>
    <div class="settings-hint">Nodes to never use as exit nodes.</div>
    <div class="settings-row"><span class="settings-label">Reject exit ports:</span><input type="text" class="settings-input settings-input-text" id="s_reject_ports" value=""/></div>
    <div class="settings-hint">Destination ports to block (comma-separated).</div>
    <div class="settings-row"><span class="settings-label">UseEntryGuardsAsDirGuards:</span><label class="settings-toggle"><input type="checkbox" id="s_guards_dir"/><span class="toggle-slider"></span></label></div>
    <div class="settings-hint">Reuse entry guards for directory fetches.</div>
    <div class="settings-row"><span class="settings-label">PathBiasCircThreshold:</span><input type="number" class="settings-input" id="s_path_bias" min="0" max="200" value="0"/></div>
    <div class="settings-hint">Path bias detection threshold. 0 = disabled.</div>
    <div style="height: 20px"></div>
</div>`;
}

/* ===== HELP MODE ===== */
function renderHelpMode() {
    return `
<div class="help-header"><div class="accent-strip"></div>
<div class="help-top"><button class="btn btn-primary" onclick="switchToNormal()">&#9664; Back</button><span class="help-title">How to Use — Delta Tor</span></div></div>
<div class="help-scroll">
    <div class="help-section">QUICK START</div>
    <div class="help-content">
        <div class="help-step"><span class="help-num">1.</span> Category &rarr; Tested &amp; Active</div>
        <div class="help-step"><span class="help-num">2.</span> Transport &rarr; obfs4</div>
        <div class="help-step"><span class="help-num">3.</span> IP &rarr; IPv4</div>
        <div class="help-step"><span class="help-num">4.</span> Click Auto Connect</div>
    </div>
    <div class="help-section">FEATURES</div>
    <div class="help-content">
        <div class="help-feature"><div class="help-feature-title">Custom Bridges</div><div class="help-feature-desc">Enter your own bridge lines and ping each one to see latency before connecting.</div></div>
        <div class="help-feature"><div class="help-feature-title">SNI Override</div><div class="help-feature-desc">Settings &rarr; SNI Settings. Enter a hostname like www.google.com to disguise TLS traffic. Helps against DPI.</div></div>
        <div class="help-feature"><div class="help-feature-title">Bridge Scanner</div><div class="help-feature-desc">Scan any bridge file — TCP-pings every entry, shows reachability and latency.</div></div>
        <div class="help-feature"><div class="help-feature-title">Multi-Connect</div><div class="help-feature-desc">Launches all connection types simultaneously on separate ports. The fastest one wins.</div></div>
        <div class="help-feature"><div class="help-feature-title">Auto Connect</div><div class="help-feature-desc">9-step priority sequence that tries different bridge types until one connects.</div></div>
    </div>
    <div class="help-section">SYSTEM PROXY</div>
    <div class="help-content">
        <div class="help-text">HTTP proxy: <code>127.0.0.1:19052</code></div>
        <div class="help-text">SOCKS5: <code>127.0.0.1:9050</code></div>
        <div class="help-text help-ok">Chrome, Edge, Telegram — automatic.</div>
        <div class="help-text help-bad">Firefox: Settings &rarr; Network &rarr; SOCKS5 manually.</div>
    </div>
    <div class="help-section">BRIDGE TYPES</div>
    <div class="help-content">
        <div class="help-bridge"><span class="help-bridge-name">obfs4</span> &rarr; Best for Iran/China — random data</div>
        <div class="help-bridge"><span class="help-bridge-name">webtunnel</span> &rarr; Looks like HTTPS — bypasses DPI</div>
        <div class="help-bridge"><span class="help-bridge-name">vanilla</span> &rarr; Plain Tor — only if not blocked</div>
        <div class="help-bridge"><span class="help-bridge-name">snowflake</span> &rarr; Uses WebRTC — hard to block</div>
    </div>
    <div class="help-section">TROUBLESHOOTING</div>
    <div class="help-content">
        <div class="help-troubleshoot"><span class="help-problem">Stuck below 100%?</span> &rarr; Update bridges, try Auto.</div>
        <div class="help-troubleshoot"><span class="help-problem">Port 9050 busy?</span> &rarr; Another Tor is running.</div>
        <div class="help-troubleshoot"><span class="help-problem">No bridges?</span> &rarr; Click Update Bridges.</div>
    </div>
    <div class="help-section">COMMUNITY</div>
    <div class="help-links">
        <a href="https://github.com/Delta-Kronecker/Tor-Windows" target="_blank" class="help-link"><span>GitHub — Source Code &amp; Releases</span></a>
        <a href="https://t.me/DeltaKroneckerGithub" target="_blank" class="help-link"><span>Telegram — Updates &amp; Support</span></a>
        <a href="https://github.com/Delta-Kronecker/Tor-Bridges-Collector" target="_blank" class="help-link"><span>Tor Bridges Collector</span></a>
    </div>
    <div class="help-section help-section-donate">SUPPORT THE PROJECT</div>
    <div class="help-donate">
        <div class="help-donate-label">USDT BEP20 (BNB Smart Chain):</div>
        <div class="help-donate-addr" id="donateAddr">0x2a434FF74737be5B94634040D010a458507b0741</div>
        <button class="btn-donate-copy" onclick="copyDonateAddr()">Copy Address</button>
        <div class="help-donate-warning">BEP20 network only — send only USDT on BNB Smart Chain.</div>
    </div>
    <div class="help-disclaimer">For educational and personal privacy purposes only. Use responsibly.</div>
    <div style="height: 20px"></div>
</div>`;
}

/* ===== RENDER ===== */
function render() {
    if (currentMode === 'multi') { app.innerHTML = renderMultiMode(); bindSlotToggles(); }
    else if (currentMode === 'settings') { app.innerHTML = renderSettingsMode(); }
    else if (currentMode === 'help') { app.innerHTML = renderHelpMode(); }
    else if (currentMode === 'scanner') { app.innerHTML = renderScannerMode(); }
    else if (currentMode === 'bridgeinfo') { app.innerHTML = renderBridgeInfoMode(); }
    else { app.innerHTML = renderNormalMode(); }
}

function bindSlotToggles() {
    document.querySelectorAll('.slot-toggle').forEach(el => {
        el.addEventListener('click', () => {
            const box = el.querySelector('.toggle-box');
            box.classList.toggle('toggle-on');
            box.classList.toggle('toggle-off');
        });
    });
}

/* ===== WINDOW FUNCTIONS ===== */
window.switchToMulti = function() { currentMode = 'multi'; render(); };
window.switchToNormal = function() { currentMode = 'normal'; render(); };
window.switchToSettings = function() { currentMode = 'settings'; render(); };
window.switchToHelp = function() { currentMode = 'help'; render(); };
window.switchToScanner = function() { currentMode = 'scanner'; render(); };
window.switchToBridgeInfo = function() { currentMode = 'bridgeinfo'; render(); };

let proxyOn = false;
window.toggleProxy = function() {
    proxyOn = !proxyOn;
    const btn = document.getElementById('proxyBtn');
    if (proxyOn) { btn.textContent = 'System Proxy : ON'; btn.classList.add('proxy-on'); }
    else { btn.textContent = 'System Proxy : OFF'; btn.classList.remove('proxy-on'); }
};

let isRunning = false;
window.toggleStart = function() {
    isRunning = !isRunning;
    const btn = document.getElementById('startBtn');
    if (isRunning) {
        btn.textContent = '\u23F9 Stop';
        btn.className = 'btn btn-stop-lg';
    } else {
        btn.textContent = '\u25B6 Start';
        btn.className = 'btn btn-start-lg';
    }
};

window.toggleAutoProxy = function() {
    autoProxyOn = !autoProxyOn;
    const btn = document.getElementById('autoProxyBtn');
    if (autoProxyOn) { btn.textContent = 'Auto Proxy : ON'; btn.classList.add('auto-proxy-on'); }
    else { btn.textContent = 'Auto Proxy : OFF'; btn.classList.remove('auto-proxy-on'); }
};

let logPanelOpen = false;
window.toggleLogPanel = function() {
    logPanelOpen = !logPanelOpen;
    const panel = document.getElementById('logPanel');
    const fab = document.getElementById('logFab');
    if (panel) {
        if (logPanelOpen) { panel.classList.add('log-panel-open'); fab.classList.add('log-fab-hidden'); }
        else { panel.classList.remove('log-panel-open'); fab.classList.remove('log-fab-hidden'); }
    }
};

window.clearLog = function() { const el = document.getElementById('logOutput'); if (el) el.innerHTML = ''; };

window.saveLog = function() {
    const el = document.getElementById('logOutput');
    if (!el) return;
    const blob = new Blob([el.innerText], { type: 'text/plain' });
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = 'tor_log_' + new Date().toISOString().slice(0,19).replace(/[:T]/g,'-') + '.txt';
    a.click();
};

window.appendLog = function(msg, type) {
    const el = document.getElementById('logOutput');
    if (!el) return;
    const line = document.createElement('div');
    line.className = 'log-line';
    if (type) line.classList.add('log-' + type);
    line.textContent = msg;
    el.appendChild(line);
    el.scrollTop = el.scrollHeight;
};

window.showBridgeInfo = function() { currentMode = 'bridgeinfo'; render(); };
window.updateBridges = function() { console.log('Update bridges requested'); };
window.copyDonateAddr = function() {
    const addr = document.getElementById('donateAddr').textContent;
    navigator.clipboard.writeText(addr).then(() => {
        const btn = document.querySelector('.btn-donate-copy');
        btn.textContent = 'Copied!';
        setTimeout(() => { btn.textContent = 'Copy Address'; }, 2000);
    });
};

let scanRunning = false;
window.startScan = function() { scanRunning = true; document.getElementById('scanProgressLabel').textContent = 'Scanning...'; document.getElementById('scanResults').innerHTML = ''; document.getElementById('scanSummary').textContent = ''; };
window.stopScan = function() { scanRunning = false; document.getElementById('scanProgressLabel').textContent = 'Stopped.'; };
window.exportScan = function() { console.log('Export scan results'); };

window.applySettings = function() {
    const s = {};
    document.querySelectorAll('.settings-input, .settings-input-text').forEach(el => { s[el.id] = el.value; });
    document.querySelectorAll('.settings-toggle input[type=checkbox]').forEach(el => { s[el.id] = el.checked; });
    console.log('Settings saved:', s);
    switchToNormal();
};

window.clearData = function() { if (confirm('Clear cached Tor circuits and state?')) console.log('Clear data requested'); };
window.changeDataFolder = function() { console.log('Change data folder requested'); };

/* ===== BRIDGE INFO MODE ===== */
function renderBridgeInfoMode() {
    return `
<div class="bridgeinfo-header"><div class="accent-strip"></div>
<div class="bridgeinfo-top"><button class="btn btn-primary" onclick="switchToNormal()">&#9664; Back</button><span class="bridgeinfo-title">Bridge Information</span></div></div>
<div class="bridgeinfo-scroll">
    <div class="bridgeinfo-section">Overview</div>
    <div class="bridgeinfo-overview">
        <div class="bridgeinfo-stat"><div class="bridgeinfo-stat-val">15</div><div class="bridgeinfo-stat-lbl">Total Files</div></div>
        <div class="bridgeinfo-stat"><div class="bridgeinfo-stat-val">142</div><div class="bridgeinfo-stat-lbl">Total Bridges</div></div>
        <div class="bridgeinfo-stat"><div class="bridgeinfo-stat-val">3</div><div class="bridgeinfo-stat-lbl">Transports</div></div>
        <div class="bridgeinfo-stat"><div class="bridgeinfo-stat-val">3</div><div class="bridgeinfo-stat-lbl">Categories</div></div>
    </div>
    <div class="bridgeinfo-section">Tested &amp; Active</div>
    <div class="bridgeinfo-table-wrap"><table class="bridgeinfo-table"><thead><tr><th>Transport</th><th>IP</th><th>File</th><th>Bridges</th><th>Updated</th></tr></thead><tbody>
        <tr><td>obfs4</td><td>IPv4</td><td>obfs4_tested.txt</td><td>45</td><td>2025-06-29</td></tr>
        <tr><td>vanilla</td><td>IPv4</td><td>vanilla_tested.txt</td><td>32</td><td>2025-06-29</td></tr>
        <tr><td>webtunnel</td><td>IPv4</td><td>webtunnel_tested.txt</td><td>28</td><td>2025-06-29</td></tr>
    </tbody></table></div>
    <div class="bridgeinfo-section">Fresh (72h)</div>
    <div class="bridgeinfo-table-wrap"><table class="bridgeinfo-table"><thead><tr><th>Transport</th><th>IP</th><th>File</th><th>Bridges</th><th>Updated</th></tr></thead><tbody>
        <tr><td>obfs4</td><td>IPv4</td><td>obfs4_72h.txt</td><td>38</td><td>2025-06-29</td></tr>
        <tr><td>obfs4</td><td>IPv6</td><td>obfs4_ipv6_72h.txt</td><td>12</td><td>2025-06-29</td></tr>
        <tr><td>webtunnel</td><td>IPv4</td><td>webtunnel_72h.txt</td><td>25</td><td>2025-06-29</td></tr>
        <tr><td>webtunnel</td><td>IPv6</td><td>webtunnel_ipv6_72h.txt</td><td>8</td><td>2025-06-29</td></tr>
        <tr><td>vanilla</td><td>IPv4</td><td>vanilla_72h.txt</td><td>41</td><td>2025-06-29</td></tr>
        <tr><td>vanilla</td><td>IPv6</td><td>vanilla_ipv6_72h.txt</td><td>15</td><td>2025-06-29</td></tr>
    </tbody></table></div>
    <div class="bridgeinfo-section">Full Archive</div>
    <div class="bridgeinfo-table-wrap"><table class="bridgeinfo-table"><thead><tr><th>Transport</th><th>IP</th><th>File</th><th>Bridges</th><th>Updated</th></tr></thead><tbody>
        <tr><td>obfs4</td><td>IPv4</td><td>obfs4.txt</td><td>220</td><td>2025-06-29</td></tr>
        <tr><td>obfs4</td><td>IPv6</td><td>obfs4_ipv6.txt</td><td>85</td><td>2025-06-29</td></tr>
        <tr><td>webtunnel</td><td>IPv4</td><td>webtunnel.txt</td><td>160</td><td>2025-06-29</td></tr>
        <tr><td>webtunnel</td><td>IPv6</td><td>webtunnel_ipv6.txt</td><td>45</td><td>2025-06-29</td></tr>
        <tr><td>vanilla</td><td>IPv4</td><td>vanilla.txt</td><td>310</td><td>2025-06-29</td></tr>
        <tr><td>vanilla</td><td>IPv6</td><td>vanilla_ipv6.txt</td><td>95</td><td>2025-06-29</td></tr>
    </tbody></table></div>
    <div class="bridgeinfo-section">Port Mapping (Multi-Connect)</div>
    <div class="bridgeinfo-table-wrap"><table class="bridgeinfo-table"><thead><tr><th>Slot</th><th>SOCKS5</th><th>Control</th><th>HTTP Proxy</th></tr></thead><tbody>
        <tr><td>Slot 0</td><td>9061</td><td>9071</td><td>19061</td></tr>
        <tr><td>Slot 1</td><td>9062</td><td>9072</td><td>19062</td></tr>
        <tr><td>Slot 2</td><td>9063</td><td>9073</td><td>19063</td></tr>
        <tr><td>Slot 3</td><td>9064</td><td>9074</td><td>19064</td></tr>
    </tbody></table></div>
    <div class="bridgeinfo-source">Source: <a href="https://github.com/Delta-Kronecker/Tor-Bridges-Collector" target="_blank">Delta-Kronecker/Tor-Bridges-Collector</a></div>
    <div style="height:20px"></div>
</div>`;
}

render();
