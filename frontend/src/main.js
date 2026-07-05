import './style.css';

const app = document.getElementById('app');

let currentMode = 'normal';
let autoProxyOn = false;
let autoConnecting = false;

// Persistent state
let appState = {
    isRunning: false,
    progress: 0,
    torConnected: false,
    exitIp: '\u2014',
    country: '\u2014',
    uptime: '\u2014',
    download: '\u2014',
    upload: '\u2014',
    proxyOn: false,
    logLines: [],
};

function slotPorts(index) {
    return { socks: 9051 + index, http: 9061 + index };
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
    <div class="multi-top-spacer"></div>
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
                    <button class="btn btn-auto" id="autoBtn" onclick="toggleAuto()">&#9889; Auto</button>
                    <button class="btn btn-proxy-toggle" id="proxyBtn" onclick="toggleProxy()">System Proxy : OFF</button>
                </div>
            </div>
            <div class="btn-group">
                <div class="btn-group-title">TOOLS</div>
                <div class="btn-group-row">
                    <button class="btn btn-secondary" onclick="switchToScanner()">Scanner</button>
                    <button class="btn btn-secondary" onclick="runManualTest()">Test Connection</button>
                    <button class="btn btn-secondary" onclick="requestNewCircuit()">New Circuit</button>
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
        <div class="card-accent-thin"></div>
        <div class="card-inner">
            <div class="stats-card-title">Connection Status <span class="port-badges" id="portBadges" style="display:none"><span class="port-badge">SOCKS : 9050</span><span class="port-badge">HTTP : 9060</span></span></div>
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
let multiRunning = false;
let multiSlotLabels = [];
let multiSlotState = {};
let multiSlotsData = [];

async function loadMultiSlots() {
    try {
        const slots = await window.go.main.App.GetMultiSlots();
        if (slots && slots.length > 0) {
            multiSlotsData = slots.map(s => ({
                label: s.label,
                source: s.source === 'builtin' ? 'Default (Built-in)' : s.source === 'delta-kronecker' ? 'Delta-Kronecker' : s.source,
                cat: s.category || null,
                trans: s.transport,
                ip: s.ip || null,
                noBridge: s.noBridge || false,
                enabled: s.enabled !== false,
            }));
        } else {
            multiSlotsData = [];
        }
    } catch(e) {
        multiSlotsData = [];
    }
}

function renderMultiMode() {
    const slots = multiSlotsData;
    multiSlotLabels = slots.map(s => s.label);
    const slotsHtml = slots.map((s, i) => renderSlotCard(s, i)).join('');
    return `
<div class="multi-toolbar">
    <div class="card-accent"></div>
    <div class="multi-toolbar-inner">
        <div class="multi-toolbar-left">
            <button class="btn btn-primary" onclick="switchToNormal()">&#9664; Normal</button>
            <button class="btn btn-start" id="multiStartBtn" onclick="multiStartAll()">&#9654; Start</button>
            <button class="btn btn-stop" onclick="multiStopAll()">&#9209; Stop</button>
        </div>
        <button class="btn btn-auto-proxy" id="autoProxyBtn" onclick="toggleAutoProxy()">Auto Proxy : OFF</button>
    </div>
</div>
<div class="multi-separator"></div>
<div class="multi-scroll"><div class="multi-list" id="slotGrid">${slotsHtml}</div></div>
<div class="multi-log-area" id="multiLogArea" style="display:none">
    <div class="log-panel-header">
        <span class="log-panel-title" id="multiLogTitle">Slot Log</span>
        <span class="spacer"></span>
        <button class="log-panel-btn" onclick="clearMultiLog()">Clear</button>
        <button class="log-panel-btn" onclick="closeMultiLog()">&#10005;</button>
    </div>
    <div class="log-panel-body" id="multiLogOutput" style="height:200px;overflow-y:auto;font-family:Consolas,monospace;font-size:12px;padding:8px;background:var(--panel);color:var(--fg2)"></div>
</div>
<div class="multi-bottom"><button class="btn-add-mode" onclick="addConnectionMode()">+ Add Connection Mode</button></div>`;
}

function restoreMultiSlotState() {
    for (const [label, st] of Object.entries(multiSlotState)) {
        const card = document.querySelector(`.slot-toggle[data-label="${label}"]`);
        if (!card) continue;
        const cardEl = card.closest('.slot-card-full');
        if (!cardEl) continue;
        if (st.pct !== undefined) {
            const fill = cardEl.querySelector('.slot-progress-fill');
            const pctLabel = cardEl.querySelector('.slot-progress-pct-inline');
            if (fill) fill.style.width = st.pct + '%';
            if (pctLabel) pctLabel.textContent = 'Progress : ' + st.pct + '%';
        }
        if (st.status) {
            const statusEl = cardEl.querySelector('.slot-stat-box:first-child .slot-stat-val');
            if (statusEl) {
                statusEl.textContent = st.status;
                if (st.connected) statusEl.style.color = 'var(--grn)';
                else if (st.failed) statusEl.style.color = 'var(--red)';
                else statusEl.style.color = 'var(--ylw)';
            }
        }
        if (st.health) {
            const healthBoxes = cardEl.querySelectorAll('.slot-stat-box .slot-stat-val');
            if (healthBoxes[0]) {
                healthBoxes[0].textContent = st.health;
                healthBoxes[0].style.color = st.healthOnline ? 'var(--grn)' : 'var(--red)';
            }
        }
        if (st.exitIp) {
            const boxes = cardEl.querySelectorAll('.slot-stat-box .slot-stat-val');
            if (boxes[1]) boxes[1].textContent = st.exitIp;
        }
        if (st.country) {
            const boxes = cardEl.querySelectorAll('.slot-stat-box .slot-stat-val');
            if (boxes[2]) boxes[2].textContent = st.country;
        }
        if (st.uptime) {
            const boxes = cardEl.querySelectorAll('.slot-stat-box .slot-stat-val');
            if (boxes[3]) boxes[3].textContent = st.uptime;
        }
        if (st.download) {
            const boxes = cardEl.querySelectorAll('.slot-stat-box .slot-stat-val');
            if (boxes[4]) boxes[4].textContent = st.download;
        }
        if (st.upload) {
            const boxes = cardEl.querySelectorAll('.slot-stat-box .slot-stat-val');
            if (boxes[5]) boxes[5].textContent = st.upload;
        }
    }
    if (multiRunning) {
        const btn = document.getElementById('multiStartBtn');
        if (btn) { btn.textContent = '\u23F9 Stop'; btn.disabled = false; }
    }
}

/* ===== SLOT CARD ===== */
function renderSlotCard(slot, index) {
    const ports = slotPorts(index);
    return `
    <div class="slot-card-full">
        <div class="slot-accent-bar"></div>
        <div class="slot-main-area">
            <div class="slot-top-row">
                <div class="slot-toggle" data-label="${slot.label}"><div class="toggle-box toggle-on"></div></div>
                <span class="slot-label">${slot.label}</span>
                <span class="spacer"></span>
                <div class="slot-actions-inline">
                    <button class="slot-btn-sm" onclick="multiSetProxy('${slot.label.replace(/'/g, "\\'")}')">Set Proxy</button>
                    <button class="slot-btn-sm" onclick="multiRetrySlot('${slot.label.replace(/'/g, "\\'")}')">Retry</button>
                    <button class="slot-btn-sm" onclick="multiCheckHealth('${slot.label.replace(/'/g, "\\'")}')">Health</button>
                    <button class="slot-btn-sm" onclick="multiShowLog('${slot.label.replace(/'/g, "\\'")}')">Log</button>
                    <button class="slot-btn-sm slot-btn-del" onclick="multiDeleteSlot('${slot.label.replace(/'/g, "\\'")}')">Delete</button>
                </div>
            </div>
            <div class="slot-meta-row">
                <span class="slot-meta">${slot.source} &middot; ${slot.cat || '&mdash;'} &middot; ${slot.trans} &middot; ${slot.ip || 'auto'} &nbsp;|&nbsp; SOCKS ${ports.socks} &middot; HTTP ${ports.http}</span>
                <span class="slot-progress-pct-inline">Progress : 0%</span>
            </div>
            <div class="slot-progress-bar"><div class="slot-progress-fill" style="width:0%"></div></div>
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
    <div class="scanner-btns">
        <button class="btn btn-start" onclick="startScan()">&#9654; Start Scan</button>
        <button class="btn btn-stop" onclick="stopScan()">&#9209; Stop</button>
        <button class="btn btn-cyan" onclick="useAsCustomBridge()">Use as Custom Bridge</button>
        <button class="btn btn-cyan" onclick="exportScan()">Export Working</button>
    </div>
    <div class="scanner-summary" id="scanSummary"></div>
    <div class="scanner-progress-area"><div class="scanner-progress-label" id="scanProgressLabel">Ready.</div><div class="scanner-progress-bar"><div class="scanner-progress-fill" id="scanProgressFill" style="width:0%"></div></div></div>
    <div class="scanner-table-wrap"><table class="scanner-table"><thead><tr><th>Bridge Type</th><th>Host</th><th>Port</th><th>Ping (ms)</th><th>Status</th><th>Action</th></tr></thead><tbody id="scanResults"></tbody></table></div>
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
    let html = '';
    if (currentMode === 'multi') { html = renderMultiMode(); }
    else if (currentMode === 'settings') { html = renderSettingsMode(); }
    else if (currentMode === 'help') { html = renderHelpMode(); }
    else if (currentMode === 'scanner') { html = renderScannerMode(); }
    else if (currentMode === 'bridgeinfo') { html = renderBridgeInfoMode(); }
    else { html = renderNormalMode(); }

    app.innerHTML = html;

    // Restore state
    if (currentMode === 'normal') {
        const startBtn = document.getElementById('startBtn');
        const proxyBtn = document.getElementById('proxyBtn');
        if (startBtn) {
            if (appState.isRunning) {
                startBtn.textContent = '\u23F9 Stop';
                startBtn.className = 'btn btn-stop-lg';
            }
        }
        if (proxyBtn && appState.proxyOn) {
            proxyBtn.textContent = 'System Proxy : ON';
            proxyBtn.classList.add('proxy-on');
        }

        // Restore progress
        const pctEl = document.getElementById('conn-pct');
        const fillEl = document.getElementById('conn-progress');
        if (pctEl) pctEl.textContent = appState.progress + '%';
        if (fillEl) fillEl.style.width = appState.progress + '%';

        // Restore stats
        const ipEl = document.getElementById('stat-ip');
        const countryEl = document.getElementById('stat-country');
        const uptimeEl = document.getElementById('stat-uptime');
        const torEl = document.getElementById('stat-tor');
        const dlEl = document.getElementById('stat-download');
        const ulEl = document.getElementById('stat-upload');

        if (ipEl) ipEl.textContent = appState.exitIp;
        if (countryEl) countryEl.textContent = appState.country;
        if (uptimeEl) uptimeEl.textContent = appState.uptime;
        if (dlEl) dlEl.textContent = appState.download;
        if (ulEl) ulEl.textContent = appState.upload;

        if (torEl) {
            if (appState.torConnected) {
                torEl.textContent = 'Connected';
                torEl.style.color = 'var(--grn)';
            } else {
                torEl.textContent = '\u2014';
            }
        }

        // Restore log
        const logEl = document.getElementById('logOutput');
        if (logEl && appState.logLines.length > 0) {
            appState.logLines.forEach(line => {
                const div = document.createElement('div');
                div.className = 'log-line';
                if (line.includes('[err]') || line.includes('Error')) div.classList.add('log-err');
                else if (line.includes('[warn]')) div.classList.add('log-warn');
                else if (line.includes('Bootstrapped')) div.classList.add('log-ok');
                else if (line.includes('[auto]')) div.classList.add('log-auto');
                div.textContent = line;
                logEl.appendChild(div);
            });
            logEl.scrollTop = logEl.scrollHeight;
        }
    }

    if (currentMode === 'multi') { bindSlotToggles(); restoreMultiSlotState(); }
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
window.switchToMulti = async function() {
    currentMode = 'multi';
    await loadMultiSlots();
    render();
};
window.switchToNormal = function() { currentMode = 'normal'; render(); };
window.switchToSettings = function() { currentMode = 'settings'; render(); };
window.switchToHelp = function() { currentMode = 'help'; render(); };
window.switchToScanner = function() { currentMode = 'scanner'; render(); };
window.switchToBridgeInfo = function() { currentMode = 'bridgeinfo'; render(); };

let proxyOn = false;
window.toggleProxy = async function() {
    proxyOn = !proxyOn;
    const btn = document.getElementById('proxyBtn');
    if (btn) btn.classList.remove('proxy-blink');
    if (proxyOn) {
        try { await window.go.main.App.SetSystemProxy(); } catch(e) {}
        if (btn) {
            btn.textContent = 'System Proxy : ON';
            btn.classList.add('proxy-on');
        }
    } else {
        try { await window.go.main.App.UnsetSystemProxy(); } catch(e) {}
        if (btn) {
            btn.textContent = 'System Proxy : OFF';
            btn.classList.remove('proxy-on');
        }
    }
};

let isRunning = false;
window.toggleStart = async function() {
    const btn = document.getElementById('startBtn');
    if (isRunning) {
        btn.textContent = '\u23F9 Stopping...';
        btn.disabled = true;
        if (autoConnecting) {
            try { await window.go.main.App.StopAutoConnect(); } catch(e) {}
            autoConnecting = false;
        }
        try { await window.go.main.App.StopTor(); } catch(e) {}
        isRunning = false;
        appState.isRunning = false;
        btn.textContent = '\u25B6 Start';
        btn.className = 'btn btn-start-lg';
        btn.disabled = false;
        document.getElementById('conn-pct').textContent = '0%';
        document.getElementById('conn-progress').style.width = '0%';
        const autoBtn = document.getElementById('autoBtn');
        if (autoBtn) {
            autoBtn.textContent = '\u26A1 Auto';
            autoBtn.className = 'btn btn-auto';
        }
    } else {
        isRunning = true;
        appState.isRunning = true;
        btn.textContent = '\u23F9 Stop';
        btn.className = 'btn btn-stop-lg';
        document.getElementById('stat-tor').textContent = 'Connecting...';
        document.getElementById('stat-tor').style.color = 'var(--ylw)';

        const source = document.getElementById('source').value;
        const cat = document.getElementById('category').value;
        const transport = document.getElementById('transport').value;
        const ip = document.getElementById('ipversion').value;

        let src = 'delta-kronecker';
        if (source === 'Default (Built-in)') src = 'builtin';
        else if (source === 'Custom Bridges') src = 'custom';

        try {
            const err = await window.go.main.App.StartTor(cat, transport, ip, src);
            if (err) {
                console.error('Start error:', err);
                isRunning = false;
                btn.textContent = '\u25B6 Start';
                btn.className = 'btn btn-start-lg';
            }
        } catch(e) {
            console.error(e);
            isRunning = false;
            btn.textContent = '\u25B6 Start';
            btn.className = 'btn btn-start-lg';
        }
    }
};

window.toggleAuto = async function() {
    const btn = document.getElementById('autoBtn');
    if (autoConnecting) {
        // Stop auto-connect
        btn.textContent = '\u26A1 Auto';
        btn.className = 'btn btn-auto';
        btn.disabled = false;
        autoConnecting = false;
        try { await window.go.main.App.StopAutoConnect(); } catch(e) {}
        try { await window.go.main.App.StopTor(); } catch(e) {}
        isRunning = false;
        appState.isRunning = false;
        document.getElementById('conn-pct').textContent = '0%';
        document.getElementById('conn-progress').style.width = '0%';
        document.getElementById('stat-tor').textContent = '\u2014';
        document.getElementById('stat-tor').style.color = '';
    } else {
        // Start auto-connect
        autoConnecting = true;
        isRunning = true;
        appState.isRunning = true;
        btn.textContent = '\u26A1 Auto (active)';
        btn.className = 'btn btn-auto-active';
        document.getElementById('startBtn').textContent = '\u23F9 Stop';
        document.getElementById('startBtn').className = 'btn btn-stop-lg';
        document.getElementById('stat-tor').textContent = 'Auto-connecting...';
        document.getElementById('stat-tor').style.color = 'var(--ylw)';
        document.getElementById('conn-pct').textContent = '0%';
        document.getElementById('conn-progress').style.width = '0%';
        try {
            await window.go.main.App.StartAutoConnect();
        } catch(e) {
            console.error(e);
            autoConnecting = false;
            isRunning = false;
            btn.textContent = '\u26A1 Auto';
            btn.className = 'btn btn-auto';
        }
    }
};

// Listen for Tor events from Go backend
window.runtime.EventsOn('tor:progress', (pct) => {
    appState.progress = pct;
    const pctEl = document.getElementById('conn-pct');
    const fillEl = document.getElementById('conn-progress');
    if (pctEl) pctEl.textContent = pct + '%';
    if (fillEl) fillEl.style.width = pct + '%';
});

window.runtime.EventsOn('tor:connected', () => {
    appState.torConnected = true;
    appState.isRunning = true;
    autoConnecting = false;
    const torEl = document.getElementById('stat-tor');
    if (torEl) {
        torEl.textContent = 'Connected';
        torEl.style.color = 'var(--grn)';
    }
    const portBadges = document.getElementById('portBadges');
    if (portBadges) portBadges.style.display = 'inline';
    const autoBtn = document.getElementById('autoBtn');
    if (autoBtn) {
        autoBtn.textContent = '\u26A1 Auto';
        autoBtn.className = 'btn btn-auto';
    }
    const proxyBtn = document.getElementById('proxyBtn');
    if (proxyBtn && !proxyOn) {
        proxyBtn.classList.add('proxy-blink');
    }
    startUptimeTimer();
    startAutoTest();
    startTrafficMonitor();
});

window.runtime.EventsOn('tor:log', (line) => {
    appState.logLines.push(line);
    if (appState.logLines.length > 500) {
        appState.logLines = appState.logLines.slice(-500);
    }
    const el = document.getElementById('logOutput');
    if (!el) return;
    const div = document.createElement('div');
    div.className = 'log-line';
    if (line.includes('[err]') || line.includes('Error')) div.classList.add('log-err');
    else if (line.includes('[warn]') || line.includes('Warn')) div.classList.add('log-warn');
    else if (line.includes('Bootstrapped')) div.classList.add('log-ok');
    else if (line.includes('[auto]')) div.classList.add('log-auto');
    div.textContent = line;
    el.appendChild(div);
    el.scrollTop = el.scrollHeight;
});

// Auto-connect events
window.runtime.EventsOn('auto:step', (data) => {
    const torEl = document.getElementById('stat-tor');
    if (torEl) {
        torEl.textContent = data.label || 'Auto-connecting...';
        torEl.style.color = 'var(--ylw)';
    }
    const logEl = document.getElementById('logOutput');
    if (logEl) {
        const div = document.createElement('div');
        div.className = 'log-line log-auto';
        div.textContent = '[Auto] Trying ' + data.label;
        logEl.appendChild(div);
        logEl.scrollTop = logEl.scrollHeight;
    }
});

window.runtime.EventsOn('auto:progress', (pct) => {
    appState.progress = pct;
    const pctEl = document.getElementById('conn-pct');
    const fillEl = document.getElementById('conn-progress');
    if (pctEl) pctEl.textContent = pct + '%';
    if (fillEl) fillEl.style.width = pct + '%';
});

window.runtime.EventsOn('auto:done', (data) => {
    autoConnecting = false;
    appState.torConnected = true;
    const autoBtn = document.getElementById('autoBtn');
    if (autoBtn) {
        autoBtn.textContent = '\u26A1 Auto';
        autoBtn.className = 'btn btn-auto';
    }
    const torEl = document.getElementById('stat-tor');
    if (torEl) {
        torEl.textContent = 'Connected';
        torEl.style.color = 'var(--grn)';
    }
    const proxyBtn = document.getElementById('proxyBtn');
    if (proxyBtn && !proxyOn) {
        proxyBtn.classList.add('proxy-blink');
    }
    startUptimeTimer();
    startAutoTest();
    startTrafficMonitor();
});

window.runtime.EventsOn('auto:failed', (data) => {
    autoConnecting = false;
    isRunning = false;
    appState.isRunning = false;
    const autoBtn = document.getElementById('autoBtn');
    if (autoBtn) {
        autoBtn.textContent = '\u26A1 Auto';
        autoBtn.className = 'btn btn-auto';
    }
    const startBtn = document.getElementById('startBtn');
    if (startBtn) {
        startBtn.textContent = '\u25B6 Start';
        startBtn.className = 'btn btn-start-lg';
    }
    const torEl = document.getElementById('stat-tor');
    if (torEl) {
        torEl.textContent = '\u2014';
        torEl.style.color = '';
    }
    document.getElementById('conn-pct').textContent = '0%';
    document.getElementById('conn-progress').style.width = '0%';
    const logEl = document.getElementById('logOutput');
    if (logEl) {
        const div = document.createElement('div');
        div.className = 'log-line log-err';
        div.textContent = '[Auto] ❌ ' + (data.message || 'All bridge groups exhausted.');
        logEl.appendChild(div);
        logEl.scrollTop = logEl.scrollHeight;
    }
});

window.runtime.EventsOn('auto:log', (msg) => {
    const logEl = document.getElementById('logOutput');
    if (logEl) {
        const div = document.createElement('div');
        div.className = 'log-line log-auto';
        div.textContent = msg;
        logEl.appendChild(div);
        logEl.scrollTop = logEl.scrollHeight;
    }
});

window.runtime.EventsOn('tor:stopped', () => {
    isRunning = false;
    autoConnecting = false;
    appState.torConnected = false;
    appState.isRunning = false;
    appState.progress = 0;
    appState.exitIp = '\u2014';
    appState.country = '\u2014';
    appState.uptime = '\u2014';
    appState.download = '0 B/s';
    appState.upload = '0 B/s';
    appState.logLines = [];
    document.getElementById('stat-tor').textContent = '\u2014';
    document.getElementById('stat-tor').style.color = '';
    document.getElementById('stat-ip').textContent = '\u2014';
    document.getElementById('stat-country').textContent = '\u2014';
    document.getElementById('stat-uptime').textContent = '\u2014';
    document.getElementById('stat-download').textContent = '\u2014';
    document.getElementById('stat-upload').textContent = '\u2014';
    document.getElementById('conn-pct').textContent = '0%';
    document.getElementById('conn-progress').style.width = '0%';
    const portBadges = document.getElementById('portBadges');
    if (portBadges) portBadges.style.display = 'none';
    const autoBtn = document.getElementById('autoBtn');
    if (autoBtn) {
        autoBtn.textContent = '\u26A1 Auto';
        autoBtn.className = 'btn btn-auto';
    }
    stopUptimeTimer();
    stopAutoTest();
    stopTrafficMonitor();
});

window.runtime.EventsOn('tor:speed', (data) => {
    if (data.download) {
        appState.download = data.download;
        const dlEl = document.getElementById('stat-download');
        if (dlEl) dlEl.textContent = data.download;
    }
    if (data.upload) {
        appState.upload = data.upload;
        const ulEl = document.getElementById('stat-upload');
        if (ulEl) ulEl.textContent = data.upload;
    }
});

let uptimeInterval = null;
function startUptimeTimer() {
    stopUptimeTimer();
    const startTime = Date.now();
    uptimeInterval = setInterval(() => {
        const elapsed = Math.floor((Date.now() - startTime) / 1000);
        const h = Math.floor(elapsed / 3600);
        const m = Math.floor((elapsed % 3600) / 60);
        const s = elapsed % 60;
        document.getElementById('stat-uptime').textContent =
            String(h).padStart(2,'0') + ':' + String(m).padStart(2,'0') + ':' + String(s).padStart(2,'0');
    }, 1000);
}
function stopUptimeTimer() { if (uptimeInterval) { clearInterval(uptimeInterval); uptimeInterval = null; } }

let autoTestInterval = null;
function startAutoTest() {
    stopAutoTest();
    console.log('[Test] Starting auto test (every 30s)');

    // First test immediately
    runAutoTest();

    autoTestInterval = setInterval(() => {
        runAutoTest();
    }, 30000);
}

async function runAutoTest() {
    try {
        const result = await window.go.main.App.TestConnection();
        if (result && result.ip && result.ip !== '\u2014' && result.ip !== '—') {
            appState.exitIp = result.ip;
            const ipEl = document.getElementById('stat-ip');
            if (ipEl) ipEl.textContent = result.ip;
        }
        if (result && result.country && result.country !== '\u2014' && result.country !== '?' && result.country !== '—') {
            appState.country = result.country;
            const countryEl = document.getElementById('stat-country');
            if (countryEl) countryEl.textContent = result.country;
        }
    } catch(e) {
        console.error('[Test] Error:', e);
    }
}

window.runManualTest = async function() {
    try {
        await runAutoTest();
    } catch(e) {
        console.error(e);
    }
};

window.requestNewCircuit = async function() {
    if (!appState.torConnected) {
        appendLog('[Circuit] Not connected.\n', 'warn');
        return;
    }
    try {
        await window.go.main.App.RequestNewCircuit();
    } catch(e) {
        console.error(e);
    }
};
function stopAutoTest() { if (autoTestInterval) { clearInterval(autoTestInterval); autoTestInterval = null; } }

let trafficInterval = null;
let prevDl = 0, prevUl = 0, prevTime = 0;

function startTrafficMonitor() {
    stopTrafficMonitor();
    prevDl = 0; prevUl = 0; prevTime = Date.now();
    trafficInterval = setInterval(async () => {
        try {
            const stats = await window.go.main.App.GetTrafficStats();
            const now = Date.now();
            const elapsed = (now - prevTime) / 1000;
            prevTime = now;
            if (stats.download !== undefined) {
                document.getElementById('stat-download').textContent = stats.download;
            }
            if (stats.upload !== undefined) {
                document.getElementById('stat-upload').textContent = stats.upload;
            }
        } catch(e) {}
    }, 2000);
}

function stopTrafficMonitor() {
    if (trafficInterval) { clearInterval(trafficInterval); trafficInterval = null; }
    prevDl = 0; prevUl = 0; prevTime = 0;
}

let autoProxyInterval = null;

window.toggleAutoProxy = async function() {
    autoProxyOn = !autoProxyOn;
    const btn = document.getElementById('autoProxyBtn');
    if (autoProxyOn) {
        btn.textContent = 'Auto Proxy : ON';
        btn.classList.add('auto-proxy-on');
        autoProxyCheck();
        autoProxyInterval = setInterval(autoProxyCheck, 15000);
    } else {
        btn.textContent = 'Auto Proxy : OFF';
        btn.classList.remove('auto-proxy-on');
        if (autoProxyInterval) { clearInterval(autoProxyInterval); autoProxyInterval = null; }
        if (activeProxyLabel) {
            try { await window.go.main.App.SetProxyToSlot(activeProxyLabel); } catch(e) {}
            activeProxyLabel = null;
            document.querySelectorAll('.slot-btn-proxy-active').forEach(b => {
                b.classList.remove('slot-btn-proxy-active');
                b.textContent = 'Set Proxy';
            });
        }
    }
};

async function autoProxyCheck() {
    if (!autoProxyOn || !multiRunning) return;
    try {
        const best = await window.go.main.App.GetBestAutoProxySlot();
        if (!best) return;
        if (best === activeProxyLabel) return;
        activeProxyLabel = best;
        await window.go.main.App.SetProxyToSlot(best);
        document.querySelectorAll('.slot-btn-proxy-active').forEach(b => {
            b.classList.remove('slot-btn-proxy-active');
            b.textContent = 'Set Proxy';
        });
        const card = document.querySelector(`.slot-toggle[data-label="${best}"]`);
        if (card) {
            const cardEl = card.closest('.slot-card-full');
            if (cardEl) {
                const proxyBtn = cardEl.querySelector('.slot-btn-sm');
                if (proxyBtn) {
                    proxyBtn.classList.add('slot-btn-proxy-active');
                    proxyBtn.textContent = 'Proxy ON';
                }
            }
        }
        appendLog('[Auto Proxy] Switched to ' + best, 'ok');
    } catch(e) {}
}

let activeProxyLabel = null;

window.multiStartAll = async function() {
    if (multiRunning) return;
    multiRunning = true;
    const btn = document.getElementById('multiStartBtn');
    if (btn) { btn.textContent = '\u23F9 Starting...'; btn.disabled = true; }
    // Reset all slot cards
    document.querySelectorAll('.slot-progress-fill').forEach(el => el.style.width = '0%');
    document.querySelectorAll('.slot-progress-pct-inline').forEach(el => el.textContent = 'Progress : 0%');
    try { await window.go.main.App.StartAllSlots(); } catch(e) { console.error(e); }
    if (btn) { btn.textContent = '\u23F9 Stop'; btn.disabled = false; }
};

window.multiStopAll = async function() {
    multiRunning = false;
    autoProxyOn = false;
    activeProxyLabel = null;
    if (autoProxyInterval) { clearInterval(autoProxyInterval); autoProxyInterval = null; }
    const proxyBtn = document.getElementById('autoProxyBtn');
    if (proxyBtn) { proxyBtn.textContent = 'Auto Proxy : OFF'; proxyBtn.classList.remove('auto-proxy-on'); }
    try { await window.go.main.App.StopAllSlots(); } catch(e) { console.error(e); }
    document.querySelectorAll('.slot-btn-proxy-active').forEach(b => {
        b.classList.remove('slot-btn-proxy-active');
        b.textContent = 'Set Proxy';
    });
};

window.multiRetrySlot = async function(label) {
    try { await window.go.main.App.RetrySlot(label); } catch(e) { console.error(e); }
};

window.multiCheckHealth = async function(label) {
    try { await window.go.main.App.CheckSlotHealthNow(label); } catch(e) { console.error(e); }
};

window.multiSetProxy = async function(label) {
    const card = document.querySelector(`.slot-toggle[data-label="${label}"]`);
    const cardEl = card ? card.closest('.slot-card-full') : null;
    const proxyBtn = cardEl ? cardEl.querySelector('.slot-btn-sm') : null;

    try {
        await window.go.main.App.SetProxyToSlot(label);
        if (proxyBtn) {
            if (proxyBtn.classList.contains('slot-btn-proxy-active')) {
                proxyBtn.classList.remove('slot-btn-proxy-active');
                proxyBtn.textContent = 'Set Proxy';
                activeProxyLabel = null;
            } else {
                document.querySelectorAll('.slot-btn-proxy-active').forEach(b => {
                    b.classList.remove('slot-btn-proxy-active');
                    b.textContent = 'Set Proxy';
                });
                proxyBtn.classList.add('slot-btn-proxy-active');
                proxyBtn.textContent = 'Proxy ON';
                activeProxyLabel = label;
            }
        }
    } catch(e) { console.error(e); }
};

window.multiShowLog = function(label) {
    const area = document.getElementById('multiLogArea');
    const title = document.getElementById('multiLogTitle');
    const logEl = document.getElementById('multiLogOutput');
    if (!area || !logEl) return;
    area.style.display = 'block';
    if (title) title.textContent = 'Log \u2014 ' + label;
    logEl.innerHTML = '';
    window.go.main.App.GetSlotLogs(label).then(logs => {
        if (logs) {
            logs.forEach(line => {
                const d = document.createElement('div');
                d.style.cssText = 'padding:2px 0;border-bottom:1px solid rgba(255,255,255,0.05)';
                if (line.includes('[err]') || line.includes('Error')) d.style.color = '#D95555';
                else if (line.includes('[warn]') || line.includes('Warn')) d.style.color = '#C9A020';
                else if (line.includes('Bootstrapped')) d.style.color = '#2EB87A';
                else d.style.color = '#6B7A94';
                d.textContent = line;
                logEl.appendChild(d);
            });
            logEl.scrollTop = logEl.scrollHeight;
        }
        if (!logs || logs.length === 0) {
            const d = document.createElement('div');
            d.style.color = '#6B7A94';
            d.textContent = 'No log yet. Start the connection first.';
            logEl.appendChild(d);
        }
    }).catch(e => {});
    multiLogActiveLabel = label;
};

window.clearMultiLog = function() {
    const logEl = document.getElementById('multiLogOutput');
    if (logEl) logEl.innerHTML = '';
};

window.closeMultiLog = function() {
    const area = document.getElementById('multiLogArea');
    if (area) area.style.display = 'none';
    multiLogActiveLabel = null;
};

let multiLogActiveLabel = null;

window.multiDeleteSlot = async function(label) {
    try { await window.go.main.App.DeleteMultiSlot(label); } catch(e) { console.error(e); }
};

window.addConnectionMode = function() {
    const overlay = document.createElement('div');
    overlay.className = 'modal-overlay';
    overlay.innerHTML = `
    <div class="modal-box">
        <div class="modal-accent"></div>
        <div class="modal-title">+ Add Connection Mode</div>
        <div class="modal-body">
            <div class="modal-field">
                <label>Name</label>
                <input type="text" id="modal-name" value="New Mode" />
            </div>
            <div class="modal-field">
                <label>Source</label>
                <select id="modal-source">
                    <option value="delta-kronecker">Delta-Kronecker</option>
                    <option value="builtin">Default (Built-in)</option>
                    <option value="direct">Direct (No Bridge)</option>
                </select>
            </div>
            <div class="modal-field" id="modal-cat-field">
                <label>Category</label>
                <select id="modal-cat">
                    <option>Tested &amp; Active</option>
                    <option>Fresh (72h)</option>
                    <option>Full Archive</option>
                </select>
            </div>
            <div class="modal-field" id="modal-trans-field">
                <label>Transport</label>
                <select id="modal-trans">
                    <option value="obfs4">obfs4</option>
                    <option value="webtunnel">webtunnel</option>
                    <option value="vanilla">vanilla</option>
                </select>
            </div>
            <div class="modal-field" id="modal-ip-field">
                <label>IP Version</label>
                <select id="modal-ip">
                    <option>IPv4</option>
                    <option>IPv6</option>
                    <option>Both</option>
                </select>
            </div>
        </div>
        <div class="modal-btns">
            <button class="modal-btn modal-btn-cancel" id="modal-cancel">Cancel</button>
            <button class="modal-btn modal-btn-add" id="modal-add">Add</button>
        </div>
    </div>`;

    document.body.appendChild(overlay);

    const srcSelect = overlay.querySelector('#modal-source');
    const catField = overlay.querySelector('#modal-cat-field');
    const transSelect = overlay.querySelector('#modal-trans');
    const ipField = overlay.querySelector('#modal-ip-field');

    function updateFields() {
        const src = srcSelect.value;
        if (src === 'builtin') {
            transSelect.innerHTML = '<option value="snowflake">snowflake</option><option value="meek">meek</option>';
            catField.style.display = 'none';
            ipField.style.display = 'none';
        } else if (src === 'direct') {
            catField.style.display = 'none';
            transSelect.innerHTML = '';
            ipField.style.display = 'none';
        } else {
            transSelect.innerHTML = '<option value="obfs4">obfs4</option><option value="webtunnel">webtunnel</option><option value="vanilla">vanilla</option>';
            catField.style.display = '';
            ipField.style.display = '';
        }
    }
    srcSelect.addEventListener('change', updateFields);
    updateFields();

    overlay.querySelector('#modal-cancel').addEventListener('click', () => overlay.remove());
    overlay.addEventListener('click', (e) => { if (e.target === overlay) overlay.remove(); });

    overlay.querySelector('#modal-add').addEventListener('click', async () => {
        const name = overlay.querySelector('#modal-name').value.trim() || 'New Mode';
        const src = srcSelect.value;
        const cat = src === 'delta-kronecker' ? (overlay.querySelector('#modal-cat').value || 'Tested & Active') : '';
        const trans = overlay.querySelector('#modal-trans').value || '';
        const ip = src === 'delta-kronecker' ? (overlay.querySelector('#modal-ip').value || 'IPv4') : '';
        const noBridge = src === 'direct';
        try {
            await window.go.main.App.AddMultiSlot({ label: name, source: src, category: cat, transport: trans, ip: ip, noBridge: noBridge, enabled: true });
            await loadMultiSlots();
            render();
        } catch(e) { console.error(e); }
        overlay.remove();
    });
};

window.multiDeleteSlot = async function(label) {
    try {
        await window.go.main.App.DeleteMultiSlot(label);
        await loadMultiSlots();
        render();
    } catch(e) { console.error(e); }
};

// Multi-connect events
window.runtime.EventsOn('multi:slot:progress', (data) => {
    if (!multiSlotState[data.label]) multiSlotState[data.label] = {};
    multiSlotState[data.label].pct = data.pct || 0;
    multiSlotState[data.label].status = data.status || '—';
    multiSlotState[data.label].connected = !!data.connected;
    multiSlotState[data.label].failed = !!data.failed;

    const card = document.querySelector(`.slot-toggle[data-label="${data.label}"]`);
    if (!card) return;
    const cardEl = card.closest('.slot-card-full');
    if (!cardEl) return;

    const fill = cardEl.querySelector('.slot-progress-fill');
    const pctLabel = cardEl.querySelector('.slot-progress-pct-inline');
    const statusEl = cardEl.querySelector('.slot-stat-box:first-child .slot-stat-val');

    if (fill) fill.style.width = (data.pct || 0) + '%';
    if (pctLabel) pctLabel.textContent = 'Progress : ' + (data.pct || 0) + '%';
    if (statusEl) {
        statusEl.textContent = data.status || '—';
        if (data.connected) statusEl.style.color = 'var(--grn)';
        else if (data.failed) statusEl.style.color = 'var(--red)';
        else statusEl.style.color = 'var(--ylw)';
    }

    if (data.connected) {
        startSlotTestLoop(data.label);
        startSlotTrafficLoop(data.label);
        startSlotUptime(data.label);
    }
});

window.runtime.EventsOn('multi:slot:health', (data) => {
    if (!multiSlotState[data.label]) multiSlotState[data.label] = {};
    multiSlotState[data.label].health = data.text || '—';
    multiSlotState[data.label].healthOnline = !!data.online;

    const card = document.querySelector(`.slot-toggle[data-label="${data.label}"]`);
    if (!card) return;
    const cardEl = card.closest('.slot-card-full');
    if (!cardEl) return;
    const statusEl = cardEl.querySelector('.slot-stat-box:first-child .slot-stat-val');
    if (statusEl) {
        statusEl.textContent = data.text || '—';
        statusEl.style.color = data.online ? 'var(--grn)' : 'var(--red)';
    }
});

window.runtime.EventsOn('multi:slot:stopped', (data) => {
    if (multiSlotState[data.label]) {
        delete multiSlotState[data.label];
        stopSlotTestLoop(data.label);
        stopSlotTrafficLoop(data.label);
    }
    const card = document.querySelector(`.slot-toggle[data-label="${data.label}"]`);
    if (!card) return;
    const cardEl = card.closest('.slot-card-full');
    if (!cardEl) return;
    const statusEl = cardEl.querySelector('.slot-stat-box:first-child .slot-stat-val');
    const fill = cardEl.querySelector('.slot-progress-fill');
    const pctLabel = cardEl.querySelector('.slot-progress-pct-inline');
    if (statusEl) { statusEl.textContent = 'Stopped'; statusEl.style.color = ''; }
    if (fill) fill.style.width = '0%';
    if (pctLabel) pctLabel.textContent = 'Progress : 0%';
});

window.runtime.EventsOn('multi:stopped', () => {
    multiRunning = false;
    multiSlotState = {};
    activeProxyLabel = null;
    autoProxyOn = false;
    if (autoProxyInterval) { clearInterval(autoProxyInterval); autoProxyInterval = null; }
    stopAllSlotTimers();
    const btn = document.getElementById('multiStartBtn');
    if (btn) { btn.textContent = '\u25B6 Start'; btn.disabled = false; }
    const proxyBtn = document.getElementById('autoProxyBtn');
    if (proxyBtn) { proxyBtn.textContent = 'Auto Proxy : OFF'; proxyBtn.classList.remove('auto-proxy-on'); }
    document.querySelectorAll('.slot-progress-fill').forEach(el => el.style.width = '0%');
    document.querySelectorAll('.slot-progress-pct-inline').forEach(el => el.textContent = 'Progress : 0%');
    document.querySelectorAll('.slot-stat-box .slot-stat-val').forEach(el => { el.textContent = '\u2014'; el.style.color = ''; });
    document.querySelectorAll('.slot-btn-proxy-active').forEach(b => {
        b.classList.remove('slot-btn-proxy-active');
        b.textContent = 'Set Proxy';
    });
});

window.runtime.EventsOn('multi:proxy:on', (data) => {
    appendLog('[Multi] Proxy \u2192 ' + data.label + ' (HTTP ' + data.httpPort + ')', 'ok');
});

window.runtime.EventsOn('multi:proxy:off', (data) => {
    appendLog('[Multi] Proxy disabled for ' + data.label, 'auto');
});

window.runtime.EventsOn('multi:slot:log', (data) => {
    if (multiLogActiveLabel === data.label) {
        const logEl = document.getElementById('multiLogOutput');
        if (logEl) {
            const d = document.createElement('div');
            d.style.cssText = 'padding:2px 0;border-bottom:1px solid rgba(255,255,255,0.05)';
            if (data.line.includes('[err]') || data.line.includes('Error')) d.style.color = '#D95555';
            else if (data.line.includes('[warn]') || data.line.includes('Warn')) d.style.color = '#C9A020';
            else if (data.line.includes('Bootstrapped')) d.style.color = '#2EB87A';
            else d.style.color = '#6B7A94';
            d.textContent = data.line;
            logEl.appendChild(d);
            logEl.scrollTop = logEl.scrollHeight;
        }
    }
});

// Per-slot test and traffic timers
const slotTestIntervals = {};
const slotTrafficIntervals = {};
const slotUptimeIntervals = {};

function startSlotTestLoop(label) {
    if (slotTestIntervals[label]) return;
    runSlotTest(label);
    slotTestIntervals[label] = setInterval(() => runSlotTest(label), 30000);
}

function stopSlotTestLoop(label) {
    if (slotTestIntervals[label]) { clearInterval(slotTestIntervals[label]); delete slotTestIntervals[label]; }
}

function stopAllSlotTimers() {
    for (const k in slotTestIntervals) { clearInterval(slotTestIntervals[k]); delete slotTestIntervals[k]; }
    for (const k in slotTrafficIntervals) { clearInterval(slotTrafficIntervals[k]); delete slotTrafficIntervals[k]; }
    for (const k in slotUptimeIntervals) { clearInterval(slotUptimeIntervals[k]); delete slotUptimeIntervals[k]; }
}

async function runSlotTest(label) {
    try {
        const result = await window.go.main.App.TestSlotConnection(label);
        if (!result) return;
        if (!multiSlotState[label]) multiSlotState[label] = {};
        if (result.ip && result.ip !== '\u2014') {
            multiSlotState[label].exitIp = result.ip;
            updateSlotStat(label, 1, result.ip);
        }
        if (result.country && result.country !== '\u2014' && result.country !== '?') {
            multiSlotState[label].country = result.country;
            updateSlotStat(label, 2, result.country);
        }
    } catch(e) {}
}

function startSlotTrafficLoop(label) {
    if (slotTrafficIntervals[label]) return;
    slotTrafficIntervals[label] = setInterval(async () => {
        try {
            const stats = await window.go.main.App.GetSlotTrafficStats(label);
            if (stats) {
                if (stats.download) {
                    multiSlotState[label] = multiSlotState[label] || {};
                    multiSlotState[label].download = stats.download;
                    updateSlotStat(label, 4, stats.download);
                }
                if (stats.upload) {
                    multiSlotState[label] = multiSlotState[label] || {};
                    multiSlotState[label].upload = stats.upload;
                    updateSlotStat(label, 5, stats.upload);
                }
            }
        } catch(e) {}
    }, 2000);
}

function stopSlotTrafficLoop(label) {
    if (slotTrafficIntervals[label]) { clearInterval(slotTrafficIntervals[label]); delete slotTrafficIntervals[label]; }
}

function startSlotUptime(label) {
    if (slotUptimeIntervals[label]) return;
    const startTime = Date.now();
    slotUptimeIntervals[label] = setInterval(() => {
        const elapsed = Math.floor((Date.now() - startTime) / 1000);
        const h = Math.floor(elapsed / 3600);
        const m = Math.floor((elapsed % 3600) / 60);
        const s = elapsed % 60;
        const txt = String(h).padStart(2,'0') + ':' + String(m).padStart(2,'0') + ':' + String(s).padStart(2,'0');
        multiSlotState[label] = multiSlotState[label] || {};
        multiSlotState[label].uptime = txt;
        updateSlotStat(label, 3, txt);
    }, 1000);
}

function updateSlotStat(label, index, value) {
    const card = document.querySelector(`.slot-toggle[data-label="${label}"]`);
    if (!card) return;
    const cardEl = card.closest('.slot-card-full');
    if (!cardEl) return;
    const boxes = cardEl.querySelectorAll('.slot-stat-box .slot-stat-val');
    if (boxes[index]) boxes[index].textContent = value;
}

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

window.updateBridges = async function() {
    try {
        await window.go.main.App.DownloadAllBridges();
    } catch(e) {
        console.error(e);
    }
};

window.showBridgeInfo = function() {
    currentMode = 'bridgeinfo';
    render();
    loadBridgeInfo();
};

async function loadBridgeInfo() {
    try {
        const info = await window.go.main.App.GetBridgeInfo();
        if (!info) return;
        const groups = {};
        if (info.bridges) {
            for (const b of info.bridges) {
                if (!groups[b.category]) groups[b.category] = [];
                groups[b.category].push(b);
            }
        }
        const map = { 'Tested & Active': 'TestedActive', 'Fresh (72h)': 'Fresh72h', 'Full Archive': 'FullArchive' };
        for (const [cat, id] of Object.entries(map)) {
            const tbody = document.getElementById('bridge-table-' + id);
            if (!tbody) continue;
            const bridges = groups[cat] || [];
            let html = '';
            for (const b of bridges) {
                html += '<tr><td>' + b.transport + '</td><td>' + b.ip + '</td><td>' + b.filename + '</td><td>' + b.count + '</td><td>' + b.updated + '</td></tr>';
            }
            tbody.innerHTML = html;
        }
        const ov = document.getElementById('bridge-overview');
        if (ov && info.totalFiles !== undefined) {
            ov.innerHTML = '<div class="bridgeinfo-stat"><div class="bridgeinfo-stat-val">' + info.totalFiles + '</div><div class="bridgeinfo-stat-lbl">Total Files</div></div>' +
                '<div class="bridgeinfo-stat"><div class="bridgeinfo-stat-val">' + info.totalBridges + '</div><div class="bridgeinfo-stat-lbl">Total Bridges</div></div>' +
                '<div class="bridgeinfo-stat"><div class="bridgeinfo-stat-val">' + info.transports + '</div><div class="bridgeinfo-stat-lbl">Transports</div></div>' +
                '<div class="bridgeinfo-stat"><div class="bridgeinfo-stat-val">' + info.categories + '</div><div class="bridgeinfo-stat-lbl">Categories</div></div>';
        }
    } catch(e) {
        console.error('BridgeInfo error:', e);
    }
}

window.updateBridges = async function() {
    try {
        await window.go.main.App.DownloadAllBridges();
        loadBridgeInfo();
    } catch(e) {
        console.error(e);
    }
};

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
    <div class="bridgeinfo-overview" id="bridge-overview">
        <div class="bridgeinfo-stat"><div class="bridgeinfo-stat-val">...</div><div class="bridgeinfo-stat-lbl">Loading...</div></div>
    </div>
    <div class="bridgeinfo-section">Full Archive</div>
    <div class="bridgeinfo-table-wrap"><table class="bridgeinfo-table"><thead><tr><th>Transport</th><th>IP</th><th>File</th><th>Bridges</th><th>Updated</th></tr></thead><tbody id="bridge-table-FullArchive"></tbody></table></div>
    <div class="bridgeinfo-section">Tested &amp; Active</div>
    <div class="bridgeinfo-table-wrap"><table class="bridgeinfo-table"><thead><tr><th>Transport</th><th>IP</th><th>File</th><th>Bridges</th><th>Updated</th></tr></thead><tbody id="bridge-table-TestedActive"></tbody></table></div>
    <div class="bridgeinfo-section">Fresh (72h)</div>
    <div class="bridgeinfo-table-wrap"><table class="bridgeinfo-table"><thead><tr><th>Transport</th><th>IP</th><th>File</th><th>Bridges</th><th>Updated</th></tr></thead><tbody id="bridge-table-Fresh72h"></tbody></table></div>
    <div class="bridgeinfo-source">Source: <a href="https://github.com/Delta-Kronecker/Tor-Bridges-Collector" target="_blank">Delta-Kronecker/Tor-Bridges-Collector</a></div>
    <div style="height:20px"></div>
</div>`;
}

/* ===== CUSTOM BRIDGES MODAL ===== */
window.showCustomBridges = function() {
    const overlay = document.createElement('div');
    overlay.className = 'modal-overlay';
    overlay.innerHTML = `
    <div class="modal-box" style="width:700px;max-height:85vh;display:flex;flex-direction:column">
        <div class="modal-accent"></div>
        <div class="modal-title">Custom Bridge Lines</div>
        <div style="padding:0 20px 4px;font-size:12px;color:var(--fg2)">Enter one bridge per line. Format: obfs4 1.2.3.4:1234 FINGERPRINT cert=... iat-mode=0</div>
        <div class="modal-body" style="flex:1;overflow:hidden;display:flex;flex-direction:column">
            <label style="font-size:12px;color:var(--fg2);margin-bottom:6px;display:flex;align-items:center;gap:6px">
                <input type="checkbox" id="cb-use" /> Use custom bridges (overrides category selection)
            </label>
            <textarea id="cb-text" style="flex:1;min-height:200px;background:var(--blk);color:#2EB87A;font-family:Consolas,monospace;font-size:12px;border:1px solid var(--border);border-radius:4px;padding:10px;resize:none;outline:none" placeholder="obfs4 1.2.3.4:1234 ABCDEF... cert=... iat-mode=0"></textarea>
            <div style="margin-top:8px;display:flex;gap:6px">
                <button class="modal-btn modal-btn-add" id="cb-ping" style="background:var(--btn2);color:var(--cyan)">Ping All Bridges</button>
            </div>
            <div style="margin-top:6px;font-size:12px;font-weight:bold;color:var(--fg2)">Ping Results:</div>
            <div id="cb-results" style="height:120px;overflow-y:auto;background:var(--card);border-radius:4px;padding:8px;font-family:Consolas,monospace;font-size:11px;color:var(--fg2)"></div>
        </div>
        <div class="modal-btns">
            <button class="modal-btn modal-btn-cancel" id="cb-cancel">Cancel</button>
            <button class="modal-btn modal-btn-add" id="cb-save">Save</button>
        </div>
    </div>`;
    document.body.appendChild(overlay);

    window.go.main.App.GetCustomBridges().then(data => {
        if (data) {
            overlay.querySelector('#cb-text').value = data.text || '';
            overlay.querySelector('#cb-use').checked = data.useCustom || false;
        }
    });

    overlay.querySelector('#cb-cancel').addEventListener('click', () => overlay.remove());
    overlay.addEventListener('click', (e) => { if (e.target === overlay) overlay.remove(); });

    overlay.querySelector('#cb-ping').addEventListener('click', async () => {
        const text = overlay.querySelector('#cb-text').value;
        const resEl = overlay.querySelector('#cb-results');
        resEl.innerHTML = '<div style="color:var(--cyan)">Pinging...</div>';
        try {
            const results = await window.go.main.App.PingCustomBridges(text);
            resEl.innerHTML = '';
            if (!results || results.length === 0) {
                resEl.innerHTML = '<div style="color:var(--fg2)">No bridges to ping.</div>';
                return;
            }
            let okCount = 0;
            for (const r of results) {
                const d = document.createElement('div');
                d.style.cssText = 'padding:1px 0';
                if (r.ok) {
                    okCount++;
                    d.style.color = r.latency < 500 ? 'var(--grn)' : 'var(--ylw)';
                    d.textContent = `\u2714 ${r.host}:${r.port}  ${r.latency} ms`;
                } else {
                    d.style.color = 'var(--red)';
                    d.textContent = `\u2718 ${r.host || '?'}:${r.port || '?'}  ${r.error || 'Failed'}`;
                }
                resEl.appendChild(d);
            }
            const summary = document.createElement('div');
            summary.style.cssText = 'margin-top:4px;color:var(--cyan);font-weight:bold';
            summary.textContent = `Done. ${okCount}/${results.length} reachable.`;
            resEl.appendChild(summary);
        } catch(e) {
            resEl.innerHTML = '<div style="color:var(--red)">Error: ' + e + '</div>';
        }
    });

    overlay.querySelector('#cb-save').addEventListener('click', async () => {
        const text = overlay.querySelector('#cb-text').value.trim();
        const useCustom = overlay.querySelector('#cb-use').checked;
        try {
            await window.go.main.App.SaveCustomBridges(text, useCustom);
        } catch(e) { console.error(e); }
        overlay.remove();
    });
};

/* ===== BRIDGE SCANNER ===== */
let scanRunning = false;

window.startScan = async function() {
    const cat = document.getElementById('scan_cat')?.value || 'Tested & Active';
    const trans = document.getElementById('scan_trans')?.value || 'obfs4';
    const ip = document.getElementById('scan_ip')?.value || 'IPv4';
    const workers = parseInt(document.getElementById('scan_workers')?.value) || 20;
    const timeout = parseInt(document.getElementById('scan_timeout')?.value) || 5;
    const resultsEl = document.getElementById('scanResults');
    const progressLabel = document.getElementById('scanProgressLabel');
    const progressFill = document.getElementById('scanProgressFill');
    const summaryEl = document.getElementById('scanSummary');

    scanRunning = true;
    if (resultsEl) resultsEl.innerHTML = '';
    if (progressLabel) progressLabel.textContent = 'Scanning...';
    if (progressFill) progressFill.style.width = '0%';
    if (summaryEl) summaryEl.textContent = '';

    try {
        await window.go.main.App.ScanBridges(cat, trans, ip, workers, timeout);
    } catch(e) {
        console.error(e);
        scanRunning = false;
        if (progressLabel) progressLabel.textContent = 'Error: ' + e;
    }
};

window.stopScan = async function() {
    scanRunning = false;
    try { await window.go.main.App.StopScan(); } catch(e) {}
    const progressLabel = document.getElementById('scanProgressLabel');
    if (progressLabel) progressLabel.textContent = 'Stopped.';
};

window.runtime.EventsOn('scan:progress', (data) => {
    const progressLabel = document.getElementById('scanProgressLabel');
    const progressFill = document.getElementById('scanProgressFill');
    const resultsEl = document.getElementById('scanResults');
    if (progressLabel) progressLabel.textContent = `Scanning... ${data.done}/${data.total}`;
    if (progressFill) progressFill.style.width = data.pct + '%';
    if (resultsEl && data.result) {
        const r = data.result;
        const tr = document.createElement('tr');
        const pingColor = r.ping < 0 ? 'var(--red)' : r.ping < 500 ? 'var(--grn)' : 'var(--ylw)';
        const statusColor = r.ping < 0 ? 'var(--red)' : r.ping < 500 ? 'var(--grn)' : 'var(--ylw)';
        tr.innerHTML = `<td>${r.bridgeType}</td><td>${r.host}</td><td>${r.port}</td><td style="color:${pingColor}">${r.ping >= 0 ? r.ping + ' ms' : '\u2014'}</td><td style="color:${statusColor}">${r.status}</td><td><button class="scan-copy-btn" onclick="copyScanBridge(this)" data-line="${r.fullLine.replace(/"/g, '&quot;')}">Copy</button></td>`;
        resultsEl.appendChild(tr);
    }
});

window.runtime.EventsOn('scan:done', (data) => {
    scanRunning = false;
    const progressLabel = document.getElementById('scanProgressLabel');
    const summaryEl = document.getElementById('scanSummary');
    if (progressLabel) progressLabel.textContent = 'Done.';
    if (summaryEl) summaryEl.textContent = `\u2714 ${data.reachable} reachable  /  ${data.unreachable} unreachable  /  ${data.total} total`;
});

window.runtime.EventsOn('scan:error', (msg) => {
    scanRunning = false;
    const progressLabel = document.getElementById('scanProgressLabel');
    if (progressLabel) progressLabel.textContent = 'Error: ' + msg;
});

window.copyScanBridge = function(btn) {
    const line = btn.getAttribute('data-line');
    navigator.clipboard.writeText(line).then(() => {
        btn.textContent = 'Copied!';
        setTimeout(() => btn.textContent = 'Copy', 1500);
    });
};

window.useAsCustomBridge = async function() {
    try {
        const text = await window.go.main.App.GetWorkingBridgesText();
        if (!text) {
            alert('No working bridges. Run a scan first.');
            return;
        }
        await window.go.main.App.SaveCustomBridges(text, true);
        alert('Working bridges saved as Custom Bridges. Select "Custom Bridges" as source to use them.');
    } catch(e) { console.error(e); }
};

window.exportScan = async function() {
    try {
        const text = await window.go.main.App.GetWorkingBridgesText();
        if (!text) {
            alert('No working bridges. Run a scan first.');
            return;
        }
        const blob = new Blob([text], { type: 'text/plain' });
        const a = document.createElement('a');
        a.href = URL.createObjectURL(blob);
        a.download = 'working_bridges_' + new Date().toISOString().slice(0,19).replace(/[:T]/g,'-') + '.txt';
        a.click();
    } catch(e) { console.error(e); }
};

render();
