package main

import (
	"bufio"
	"context"
	"crypto/tls"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"syscall"
	"time"
	"unsafe"

	"fyne.io/systray"
	"github.com/wailsapp/wails/v2/pkg/runtime"
)

// Windows Registry API helpers (same as Python's winreg)
var (
	advapi32         = syscall.NewLazyDLL("advapi32.dll")
	procRegOpenKeyEx = advapi32.NewProc("RegOpenKeyExW")
	procRegSetValue  = advapi32.NewProc("RegSetValueExW")
	procRegCloseKey  = advapi32.NewProc("RegCloseKey")

	wininet          = syscall.NewLazyDLL("wininet.dll")
	procInternetSet  = wininet.NewProc("InternetSetOptionW")
)

const (
	HKEY_CURRENT_USER    = 0x80000001
	KEY_ALL_ACCESS       = 0xF003F
	REG_DWORD            = 4
	REG_SZ               = 1
)

func regOpenKey(subkey string) (uintptr, error) {
	var h uintptr
	subkeyPtr, _ := syscall.UTF16PtrFromString(`Software\Microsoft\Windows\CurrentVersion\Internet Settings` + subkey)
	r, _, err := procRegOpenKeyEx.Call(
		HKEY_CURRENT_USER,
		uintptr(unsafe.Pointer(subkeyPtr)),
		0,
		KEY_ALL_ACCESS,
		uintptr(unsafe.Pointer(&h)),
	)
	if r != 0 {
		return 0, fmt.Errorf("RegOpenKeyEx failed: %d %v", r, err)
	}
	return h, nil
}

func regSetString(h uintptr, name string, value string) {
	namePtr, _ := syscall.UTF16PtrFromString(name)
	valBytes, _ := syscall.UTF16FromString(value)
	procRegSetValue.Call(
		h,
		uintptr(unsafe.Pointer(namePtr)),
		0,
		REG_SZ,
		uintptr(unsafe.Pointer(&valBytes[0])),
		uintptr(len(valBytes)*2),
	)
}

func regSetDword(h uintptr, name string, value int) {
	namePtr, _ := syscall.UTF16PtrFromString(name)
	val := uint32(value)
	procRegSetValue.Call(
		h,
		uintptr(unsafe.Pointer(namePtr)),
		0,
		REG_DWORD,
		uintptr(unsafe.Pointer(&val)),
		4,
	)
}

func regCloseKey(h uintptr) {
	procRegCloseKey.Call(h)
}

func internetSetOptionSet(option uintptr) {
	procInternetSet.Call(0, option, 0, 0)
}

const (
	TorSOCKSPort  = 9050
	TorCtrlPort   = 9051
	HTTPProxyPort = 9060
)

type AutoStep struct {
	Category  string
	Transport string
	IP        string
}

var autoSequence = []AutoStep{
	{"Tested & Active", "vanilla", "IPv4"},
	{"Tested & Active", "obfs4", "IPv4"},
	{"Tested & Active", "webtunnel", "IPv4"},
	{"Fresh (72h)", "vanilla", "IPv4"},
	{"Fresh (72h)", "obfs4", "IPv4"},
	{"Fresh (72h)", "webtunnel", "IPv4"},
	{"Full Archive", "vanilla", "IPv4"},
	{"Full Archive", "obfs4", "IPv4"},
	{"Full Archive", "webtunnel", "IPv4"},
}

type App struct {
	ctx              context.Context
	dataDir          string
	exeDir           string
	configPath       string
	bridgesDir       string
	torProcess       *os.Process
	torMu            sync.Mutex
	connected        bool
	uptimeStart      time.Time
	stopCh           chan struct{}
	proxyServer      net.Listener
	proxyMu          sync.Mutex
	dlBytes          int64
	ulBytes          int64
	dlPrev           int64
	ulPrev           int64
	autoConnectActive bool
	autoConnectMu    sync.Mutex
	autoConnectStop  chan struct{}
	watchdogTicker   *time.Ticker
	watchdogStop     chan struct{}
	keepaliveTicker  *time.Ticker
	keepaliveStop    chan struct{}

	// Multi-Connect
	multiSlots       []SlotDef
	multiSlotStates  map[string]*SlotState
	multiMu          sync.Mutex
	multiRunning     bool
	multiStopCh      chan struct{}
	multiProxyLabel  string
	multiProxyStop   chan struct{}
	multiHealthData  map[string]*HealthData
	multiTraffic     map[int]*SlotTraffic

	// Balancer
	multiBalancerMode    string // "" | "least_ping" | "balancer"
	multiBalancerStop    chan struct{}
	multiBalancerCounter int64
}

type SlotDef struct {
	Label    string `json:"label"`
	Source   string `json:"source"`
	Category string `json:"category"`
	Transport string `json:"transport"`
	IP       string `json:"ip"`
	NoBridge bool   `json:"noBridge"`
	Enabled  bool   `json:"enabled"`
}

type SlotState struct {
	Process    *os.Process
	Connected  bool
	Progress   int
	Status     string
	StopCh     chan struct{}
	Logs       []string
	StartTime  time.Time
}

type HealthData struct {
	Online  bool
	Latency float64
	AvgLat  float64
	History []float64
}

type SlotTraffic struct {
	DlBytes    int64
	UlBytes    int64
	DlPrev     int64
	UlPrev     int64
}

type Config struct {
	Version             float64   `json:"version"`
	AutoConnectTimeout  int       `json:"auto_connect_timeout"`
	BridgesInTorrc      int       `json:"bridges_in_torrc"`
	ShuffleBridges      bool      `json:"shuffle_bridges"`
	DNSOverTor          bool      `json:"dns_over_tor"`
	MaxCircuitDirtiness int       `json:"max_circuit_dirtiness"`
	NewCircuitPeriod    int       `json:"new_circuit_period"`
	NumEntryGuards      int       `json:"num_entry_guards"`
	KeepAliveEnabled    bool      `json:"keep_alive_enabled"`
	KeepAliveInterval   int       `json:"keep_alive_interval"`
	WatchdogEnabled     bool      `json:"watchdog_enabled"`
	WatchdogInterval    int       `json:"watchdog_interval"`
	ExitNodesEnabled    bool      `json:"exit_nodes_enabled"`
	ExitNodesCountries  string    `json:"exit_nodes_countries"`
	StrictExitNodes     bool      `json:"strict_exit_nodes"`
	AutoProxyOnConnect  bool      `json:"auto_proxy_on_connect"`
	SNIEnabled          bool      `json:"sni_enabled"`
	SNIHost             string    `json:"sni_host"`
	LastSuccessCat      string    `json:"last_success_cat"`
	LastSuccessTrans    string    `json:"last_success_trans"`
	LastSuccessIP       string    `json:"last_success_ip"`
	MultiSlots          []SlotDef `json:"multi_slots"`
	CustomBridges       string    `json:"custom_bridges"`
	UseCustomBridges    bool      `json:"use_custom_bridges"`
	CircuitBuildTimeout int       `json:"circuit_build_timeout"`
	ConnectionPadding   bool      `json:"connection_padding"`
	HardwareAccel       bool      `json:"hardware_accel"`
	ExtractDir          string    `json:"extract_dir,omitempty"`

	ExpConnectionPadding         bool   `json:"exp_connection_padding"`
	ExpReducedConnectionPadding  bool   `json:"exp_reduced_connection_padding"`
	ExpCircuitStreamTimeout      int    `json:"exp_circuit_stream_timeout"`
	ExpSocksTimeout              int    `json:"exp_socks_timeout"`
	ExpSafeLogging               bool   `json:"exp_safe_logging"`
	ExpAvoidDiskWrites           bool   `json:"exp_avoid_disk_writes"`
	ExpHardwareAccel             bool   `json:"exp_hardware_accel"`
	ExpClientDNSRejectInternal   bool   `json:"exp_client_dns_reject_internal"`
	ExpFascistFirewall           bool   `json:"exp_fascist_firewall"`
	ExpFirewallPorts             string `json:"exp_firewall_ports"`
	ExpReachableAddresses        string `json:"exp_reachable_addresses"`
	ExpNumCPUs                   int    `json:"exp_num_cpus"`
	ExpExcludeNodes              string `json:"exp_exclude_nodes"`
	ExpExcludeExitNodes          string `json:"exp_exclude_exit_nodes"`
	ExpUseEntryGuardsAsDirGuards bool   `json:"exp_use_entry_guards_as_dir_guards"`
	ExpPathBiasCircThreshold     int    `json:"exp_path_bias_circ_threshold"`
	ExpIsolateDestAddr           bool   `json:"exp_isolate_dest_addr"`
	ExpIsolateDestPort           bool   `json:"exp_isolate_dest_port"`
	ExpNoExitStreamPorts         string `json:"exp_no_exit_stream_ports"`
}

var defaultConfig = Config{
	AutoConnectTimeout:  180,
	BridgesInTorrc:      200,
	ShuffleBridges:      true,
	MaxCircuitDirtiness: 300,
	NewCircuitPeriod:    5,
	NumEntryGuards:      5,
	KeepAliveEnabled:    true,
	KeepAliveInterval:   60,
	WatchdogEnabled:     true,
	WatchdogInterval:    60,
	ExitNodesCountries:  "{nl},{de},{fr},{ch},{at},{se},{no},{fi},{is}",
	SNIHost:             "www.google.com",
	CircuitBuildTimeout: 60,
	ConnectionPadding:   false,
	HardwareAccel:       true,
	ExtractDir:          "",
}

func NewApp() *App {
	return &App{}
}

func (a *App) startup(ctx context.Context) {
	a.ctx = ctx
	a.exeDir = getExeDir()
	a.dataDir = filepath.Join(a.exeDir, "data")
	a.configPath = filepath.Join(a.dataDir, "tor_client_config.json")
	a.bridgesDir = filepath.Join(a.dataDir, "bridges")
	os.MkdirAll(a.bridgesDir, 0755)
	os.MkdirAll(filepath.Join(a.dataDir, "data"), 0755)
	runtime.LogInfo(ctx, fmt.Sprintf("Data directory: %s", a.dataDir))
}

func getExeDir() string {
	exe, err := os.Executable()
	if err != nil {
		return "."
	}
	return filepath.Dir(exe)
}

func (a *App) GetDataDir() string {
	return a.dataDir
}

func (a *App) LoadConfig() Config {
	cfg := defaultConfig
	cfg.ExtractDir = a.dataDir
	data, err := os.ReadFile(a.configPath)
	if err == nil {
		json.Unmarshal(data, &cfg)
	}
	if cfg.ExtractDir == "" {
		cfg.ExtractDir = a.dataDir
	}
	return cfg
}

func (a *App) SaveConfig(cfg Config) error {
	cfg.ExtractDir = a.dataDir
	data, err := json.MarshalIndent(cfg, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(a.configPath, data, 0644)
}

func (a *App) ClearTorData() error {
	dataDir := filepath.Join(a.dataDir, "data")
	return os.RemoveAll(dataDir)
}

func (a *App) GetTorExePath() string {
	return filepath.Join(a.dataDir, "tor", "tor.exe")
}

func (a *App) IsTorInstalled() bool {
	_, err := os.Stat(a.GetTorExePath())
	return err == nil
}

func (a *App) IsPortFree(port int) bool {
	ln, err := net.Listen("tcp", "127.0.0.1:"+strconv.Itoa(port))
	if err != nil {
		return false
	}
	ln.Close()
	return true
}

func (a *App) GetBridgeFiles() []map[string]string {
	var files []map[string]string
	entries, err := os.ReadDir(a.bridgesDir)
	if err != nil {
		return files
	}
	for _, e := range entries {
		if !e.IsDir() && strings.HasSuffix(e.Name(), ".txt") {
			info, err := e.Info()
			if err != nil {
				continue
			}
			count := 0
			path := filepath.Join(a.bridgesDir, e.Name())
			if f, err := os.Open(path); err == nil {
				scanner := bufio.NewScanner(f)
				for scanner.Scan() {
					line := strings.TrimSpace(scanner.Text())
					if line != "" && !strings.HasPrefix(line, "#") {
						count++
					}
				}
				f.Close()
			}
			files = append(files, map[string]string{
				"name":  e.Name(),
				"count": strconv.Itoa(count),
				"size":  strconv.FormatInt(info.Size(), 10),
				"time":  info.ModTime().Format("2006-01-02 15:04"),
			})
		}
	}
	return files
}

func (a *App) GetSafeFilename(cat, trans, ip string) string {
	return a.getSafeFilename(cat, trans, ip)
}

func (a *App) GetBridgeLines(cat, trans, ip string) []string {
	filename := a.GetSafeFilename(cat, trans, ip)
	path := filepath.Join(a.bridgesDir, filename)
	data, err := os.ReadFile(path)
	if err != nil {
		return nil
	}
	cfg := a.LoadConfig()
	var lines []string
	limit := cfg.BridgesInTorrc
	for _, line := range strings.Split(string(data), "\n") {
		line = strings.TrimSpace(line)
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		lines = append(lines, line)
		if limit > 0 && len(lines) >= limit {
			break
		}
	}
	return lines
}

type torrcParams struct {
	dataDir    string
	socksPort  int
	ctrlPort   int
	useBridges bool
	bridgeLines []string
	writePT    bool
}

func (a *App) writeTorrcContent(sb *strings.Builder, p torrcParams) {
	cfg := a.LoadConfig()

	sb.WriteString("Log notice stdout\n")
	sb.WriteString(fmt.Sprintf("DataDirectory %s\n", p.dataDir))
	geoipFile := filepath.Join(p.dataDir, "geoip")
	geoip6File := filepath.Join(p.dataDir, "geoip6")
	sb.WriteString(fmt.Sprintf("GeoIPFile %s\n", geoipFile))
	sb.WriteString(fmt.Sprintf("GeoIPv6File %s\n", geoip6File))
	sb.WriteString(fmt.Sprintf("SOCKSPort 127.0.0.1:%d\n", p.socksPort))
	sb.WriteString(fmt.Sprintf("ControlPort 127.0.0.1:%d\n", p.ctrlPort))
	sb.WriteString("CookieAuthentication 1\n")
	sb.WriteString("DormantClientTimeout 30 minutes\n")
	sb.WriteString("DormantOnFirstStartup 0\n")
	sb.WriteString("DormantCanceledByStartup 1\n")

	ub := "0"
	if p.useBridges {
		ub = "1"
	}
	sb.WriteString(fmt.Sprintf("UseBridges %s\n", ub))
	sb.WriteString(fmt.Sprintf("MaxCircuitDirtiness %d\n", cfg.MaxCircuitDirtiness))
	sb.WriteString(fmt.Sprintf("NewCircuitPeriod %d\n", cfg.NewCircuitPeriod))
	sb.WriteString(fmt.Sprintf("NumEntryGuards %d\n", cfg.NumEntryGuards))
	sb.WriteString("AllowNonRFC953Hostnames 1\n")
	sb.WriteString("EnforceDistinctSubnets 0\n")
	sb.WriteString("MaxClientCircuitsPending 128\n")
	sb.WriteString(fmt.Sprintf("CircuitBuildTimeout %d\n", cfg.CircuitBuildTimeout))
	sb.WriteString("LearnCircuitBuildTimeout 1\n")
	sb.WriteString("GuardLifetime 30 days\n")
	sb.WriteString("NumDirectoryGuards 4\n")
	sb.WriteString("TokenBucketRefillInterval 10 msec\n")
	sb.WriteString("OptimisticData 1\n")

	if cfg.ExpConnectionPadding {
		sb.WriteString("ConnectionPadding 1\n")
		if cfg.ExpReducedConnectionPadding {
			sb.WriteString("ReducedConnectionPadding 1\n")
		} else {
			sb.WriteString("ReducedConnectionPadding 0\n")
		}
	} else if cfg.ConnectionPadding {
		sb.WriteString("ConnectionPadding 1\n")
		sb.WriteString("ReducedConnectionPadding 0\n")
	}
	if cfg.HardwareAccel || cfg.ExpHardwareAccel {
		sb.WriteString("HardwareAccel 1\n")
	}
	if cfg.ExpCircuitStreamTimeout > 0 {
		sb.WriteString(fmt.Sprintf("CircuitStreamTimeout %d\n", cfg.ExpCircuitStreamTimeout))
	}
	if cfg.ExpSocksTimeout > 0 {
		sb.WriteString(fmt.Sprintf("SocksTimeout %d\n", cfg.ExpSocksTimeout))
	}
	if cfg.ExpAvoidDiskWrites {
		sb.WriteString("AvoidDiskWrites 1\n")
	}
	if cfg.ExpNumCPUs > 0 {
		sb.WriteString(fmt.Sprintf("NumCPUs %d\n", cfg.ExpNumCPUs))
	}
	if cfg.ExpSafeLogging {
		sb.WriteString("SafeLogging 1\n")
	}
	if cfg.ExpClientDNSRejectInternal {
		sb.WriteString("ClientDNSRejectInternalAddresses 1\n")
	}
	if cfg.ExpFascistFirewall {
		sb.WriteString("FascistFirewall 1\n")
		if cfg.ExpFirewallPorts != "" {
			sb.WriteString(fmt.Sprintf("ReachableDirPorts %s\n", cfg.ExpFirewallPorts))
			sb.WriteString(fmt.Sprintf("ReachableORPorts %s\n", cfg.ExpFirewallPorts))
		}
	}
	if cfg.ExpReachableAddresses != "" {
		sb.WriteString(fmt.Sprintf("ReachableAddresses %s\n", cfg.ExpReachableAddresses))
	}
	if cfg.ExpExcludeNodes != "" {
		sb.WriteString(fmt.Sprintf("ExcludeNodes %s\n", cfg.ExpExcludeNodes))
	}
	if cfg.ExpExcludeExitNodes != "" {
		sb.WriteString(fmt.Sprintf("ExcludeExitNodes %s\n", cfg.ExpExcludeExitNodes))
	}
	if cfg.ExpUseEntryGuardsAsDirGuards {
		sb.WriteString("UseEntryGuardsAsDirGuards 1\n")
	}
	if cfg.ExpPathBiasCircThreshold > 0 {
		sb.WriteString(fmt.Sprintf("PathBiasCircThreshold %d\n", cfg.ExpPathBiasCircThreshold))
	}
	if cfg.ExpNoExitStreamPorts != "" {
		sb.WriteString(fmt.Sprintf("RejectPlaintextPorts %s\n", cfg.ExpNoExitStreamPorts))
	}
	if cfg.ExpIsolateDestAddr {
		sb.WriteString("IsolateDestAddr 1\n")
	}
	if cfg.ExpIsolateDestPort {
		sb.WriteString("IsolateDestPort 1\n")
	}

	if cfg.DNSOverTor {
		sb.WriteString("DNSPort 127.0.0.1:9053\n")
		sb.WriteString("CacheDNS 1\n")
	}
	if cfg.ExitNodesEnabled && cfg.ExitNodesCountries != "" {
		sb.WriteString(fmt.Sprintf("ExitNodes %s\n", cfg.ExitNodesCountries))
		if cfg.StrictExitNodes {
			sb.WriteString("StrictNodes 1\n")
		} else {
			sb.WriteString("StrictNodes 0\n")
		}
	}
	if cfg.SNIEnabled && cfg.SNIHost != "" {
		sb.WriteString(fmt.Sprintf("# SNI override active: %s\n", cfg.SNIHost))
	}

	if p.writePT {
		torDir := filepath.Join(a.dataDir, "tor")
		ptDir := filepath.Join(torDir, "pluggable_transports")
		lyrebird := filepath.Join(ptDir, "lyrebird.exe")
		conjure := filepath.Join(ptDir, "conjure-client.exe")
		sb.WriteString(fmt.Sprintf("ClientTransportPlugin meek_lite,obfs2,obfs3,obfs4,scramblesuit,webtunnel exec %s\n", lyrebird))
		sb.WriteString(fmt.Sprintf("ClientTransportPlugin snowflake exec %s\n", lyrebird))
		sb.WriteString(fmt.Sprintf("ClientTransportPlugin conjure exec %s -registerURL \"https://registration.refraction.network/api\"\n", conjure))
	}

	sb.WriteString("\n")
	for _, line := range p.bridgeLines {
		sb.WriteString(fmt.Sprintf("Bridge %s\n", line))
	}
}

func (a *App) copyGeoIPFiles(dataDir string) {
	os.MkdirAll(dataDir, 0755)
	geoipFile := filepath.Join(dataDir, "geoip")
	geoip6File := filepath.Join(dataDir, "geoip6")
	if _, err := os.Stat(geoipFile); os.IsNotExist(err) {
		if src, err := os.Stat(filepath.Join(a.dataDir, "geoip")); err == nil {
			_ = src
			copyFile(filepath.Join(a.dataDir, "geoip"), geoipFile)
		}
	}
	if _, err := os.Stat(geoip6File); os.IsNotExist(err) {
		if src, err := os.Stat(filepath.Join(a.dataDir, "geoip6")); err == nil {
			_ = src
			copyFile(filepath.Join(a.dataDir, "geoip6"), geoip6File)
		}
	}
}

func (a *App) loadBridgeLines(cat, trans, ip, source string, noBridge bool) []string {
	if noBridge || source == "direct" {
		return nil
	}
	return a.GetBridgeLines(cat, trans, ip)
}

func (a *App) GenerateTorrc(cat, trans, ip, source string) string {
	dataDir := filepath.Join(a.dataDir, "data")
	a.copyGeoIPFiles(dataDir)

	bridgeLines := a.loadBridgeLines(cat, trans, ip, source, false)

	var sb strings.Builder
	a.writeTorrcContent(&sb, torrcParams{
		dataDir:     dataDir,
		socksPort:   TorSOCKSPort,
		ctrlPort:    TorCtrlPort,
		useBridges:  len(bridgeLines) > 0,
		bridgeLines: bridgeLines,
		writePT:     true,
	})

	torrcPath := filepath.Join(filepath.Join(a.dataDir, "tor"), "torrc")
	os.WriteFile(torrcPath, []byte(sb.String()), 0644)
	return torrcPath
}

func (a *App) StartTor(cat, trans, ip, source string) error {
	a.torMu.Lock()
	if a.torProcess != nil {
		a.torMu.Unlock()
		return fmt.Errorf("tor is already running")
	}
	a.torMu.Unlock()

	if !a.IsPortFree(TorSOCKSPort) {
		return fmt.Errorf("port %d is already in use", TorSOCKSPort)
	}

	torExe := a.GetTorExePath()
	if _, err := os.Stat(torExe); os.IsNotExist(err) {
		return fmt.Errorf("tor.exe not found at %s", torExe)
	}

	torrc := a.GenerateTorrc(cat, trans, ip, source)

	a.torMu.Lock()
	a.stopCh = make(chan struct{})
	a.connected = false
	a.uptimeStart = time.Now()
	a.torMu.Unlock()

	runtime.LogInfo(a.ctx, fmt.Sprintf("Starting Tor with config: %s", torrc))

	cmd := exec.Command(torExe, "-f", torrc)
	cmd.SysProcAttr = &syscall.SysProcAttr{CreationFlags: 0x08000000}
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		return fmt.Errorf("failed to create stdout pipe: %v", err)
	}
	cmd.Stderr = cmd.Stdout

	if err := cmd.Start(); err != nil {
		stdout.Close()
		return fmt.Errorf("failed to start tor: %v", err)
	}

	a.torMu.Lock()
	a.torProcess = cmd.Process
	a.torMu.Unlock()

	go a.readTorOutput(stdout, cmd)

	return nil
}

func (a *App) readTorOutput(stdout io.ReadCloser, cmd *exec.Cmd) {
	scanner := bufio.NewScanner(stdout)
	re := regexp.MustCompile(`Bootstrapped (\d+)%`)

	for scanner.Scan() {
		a.torMu.Lock()
		stopCh := a.stopCh
		a.torMu.Unlock()

		select {
		case <-stopCh:
			return
		default:
		}

		line := scanner.Text()
		runtime.LogInfo(a.ctx, line)
		runtime.EventsEmit(a.ctx, "tor:log", line)

		if strings.Contains(line, "Reading config failed") || strings.Contains(line, "Failed to parse/validate config") {
			runtime.EventsEmit(a.ctx, "tor:error", "Tor config error")
			a.stopTor()
			return
		}

		if m := re.FindStringSubmatch(line); m != nil {
			pct, _ := strconv.Atoi(m[1])
			runtime.EventsEmit(a.ctx, "tor:progress", pct)

			a.torMu.Lock()
			if pct == 100 && !a.connected {
				a.connected = true
				a.uptimeStart = time.Now()
				a.torMu.Unlock()
				a.ResetTrafficStats()
				runtime.EventsEmit(a.ctx, "tor:connected", true)
				runtime.LogInfo(a.ctx, "Tor fully connected!")
				a.NotifyDesktop("Delta Tor", "Tor is fully connected!")
				a.StartWatchdog()
				a.StartKeepAlive()
			} else {
				a.torMu.Unlock()
			}
		}
	}

	cmd.Wait()
	a.torMu.Lock()
	a.torProcess = nil
	wasConnected := a.connected
	a.connected = false
	a.torMu.Unlock()

	if wasConnected {
		a.StopWatchdog()
		a.StopKeepAlive()
		a.StopHTTPProxy()
		a.unsetSystemProxy()
		a.NotifyDesktop("Delta Tor", "Tor has stopped.")
		runtime.EventsEmit(a.ctx, "tor:disconnected", true)
	}
}

func (a *App) KillAllProcesses() {
	var procs []*os.Process

	a.torMu.Lock()
	if a.stopCh != nil {
		close(a.stopCh)
		a.stopCh = nil
	}
	if a.torProcess != nil {
		procs = append(procs, a.torProcess)
		a.torProcess = nil
	}
	a.connected = false
	a.torMu.Unlock()

	a.autoConnectMu.Lock()
	a.autoConnectActive = false
	if a.autoConnectStop != nil {
		close(a.autoConnectStop)
		a.autoConnectStop = nil
	}
	a.autoConnectMu.Unlock()

	a.StopWatchdog()
	a.StopKeepAlive()

	a.mu()
	a.multiRunning = false
	states := a.multiSlotStates
	a.multiSlotStates = make(map[string]*SlotState)
	a.multiHealthData = make(map[string]*HealthData)
	a.multiTraffic = make(map[int]*SlotTraffic)
	if a.multiBalancerStop != nil {
		select {
		case <-a.multiBalancerStop:
		default:
			close(a.multiBalancerStop)
		}
		a.multiBalancerStop = nil
	}
	a.multiBalancerMode = ""
	a.muUnlock()

	for _, st := range states {
		if st.StopCh != nil {
			select {
			case <-st.StopCh:
			default:
				close(st.StopCh)
			}
		}
		if st.Process != nil {
			procs = append(procs, st.Process)
		}
	}

	a.StopHTTPProxy()
	a.unsetSystemProxy()

	for _, p := range procs {
		p.Kill()
	}
}

func (a *App) StopTor() error {
	a.stopTor()
	return nil
}

func (a *App) stopTor() {
	a.torMu.Lock()
	if a.stopCh != nil {
		close(a.stopCh)
		a.stopCh = nil
	}
	proc := a.torProcess
	a.torProcess = nil
	wasConnected := a.connected
	a.connected = false
	a.torMu.Unlock()

	if proc != nil {
		proc.Signal(os.Interrupt)
		go func() {
			time.Sleep(2 * time.Second)
			proc.Kill()
		}()
	}

	if wasConnected {
		a.StopWatchdog()
		a.StopKeepAlive()
		a.StopHTTPProxy()
		a.unsetSystemProxy()
		a.NotifyDesktop("Delta Tor", "Tor has stopped.")
		runtime.EventsEmit(a.ctx, "tor:disconnected", true)
	}
	runtime.EventsEmit(a.ctx, "tor:stopped", true)
}

func (a *App) StartAutoConnect() error {
	a.autoConnectMu.Lock()
	if a.autoConnectActive {
		a.autoConnectMu.Unlock()
		return fmt.Errorf("auto-connect is already running")
	}
	a.autoConnectActive = true
	a.autoConnectStop = make(chan struct{})
	a.autoConnectMu.Unlock()

	go a.runAutoConnect()
	return nil
}

func (a *App) StopAutoConnect() {
	a.autoConnectMu.Lock()
	a.autoConnectActive = false
	if a.autoConnectStop != nil {
		close(a.autoConnectStop)
		a.autoConnectStop = nil
	}
	a.autoConnectMu.Unlock()

	a.torMu.Lock()
	proc := a.torProcess
	a.torProcess = nil
	a.connected = false
	a.torMu.Unlock()

	if proc != nil {
		proc.Signal(os.Interrupt)
		go func() {
			time.Sleep(2 * time.Second)
			proc.Kill()
		}()
	}
}

func (a *App) isAutoConnectActive() bool {
	a.autoConnectMu.Lock()
	defer a.autoConnectMu.Unlock()
	return a.autoConnectActive
}

func (a *App) runAutoConnect() {
	defer func() {
		a.autoConnectMu.Lock()
		a.autoConnectActive = false
		a.autoConnectMu.Unlock()
	}()

	cfg := a.LoadConfig()
	timeoutS := cfg.AutoConnectTimeout

	// Try direct (no bridge) first if no_bridge is not relevant here — skip, go straight to sequence
	// But first, try last successful config from memory
	lastCat := cfg.LastSuccessCat
	lastTrans := cfg.LastSuccessTrans
	lastIP := cfg.LastSuccessIP

	if lastCat != "" && lastTrans != "" && lastIP != "" {
		runtime.EventsEmit(a.ctx, "auto:step", map[string]interface{}{
			"step":      0,
			"total":     len(autoSequence),
			"category":  lastCat,
			"transport": lastTrans,
			"ip":        lastIP,
			"label":     fmt.Sprintf("[Memory] %s / %s / %s", lastCat, lastTrans, lastIP),
		})
		runtime.LogInfo(a.ctx, fmt.Sprintf("[Auto] Trying last successful: %s / %s / %s", lastCat, lastTrans, lastIP))

		if a.tryBridgeConfigAuto(lastCat, lastTrans, lastIP, timeoutS) {
			runtime.EventsEmit(a.ctx, "auto:done", map[string]interface{}{
				"category":  lastCat,
				"transport": lastTrans,
				"ip":        lastIP,
				"label":     fmt.Sprintf("[Memory] %s / %s / %s", lastCat, lastTrans, lastIP),
			})
			runtime.LogInfo(a.ctx, fmt.Sprintf("[Auto] ✅ Connected with memory config: %s / %s / %s", lastCat, lastTrans, lastIP))
			return
		}
		if !a.isAutoConnectActive() {
			return
		}
		runtime.EventsEmit(a.ctx, "auto:log", "[Auto] Memory config timed out — continuing sequence.")
	}

	// Build sequence, skipping the one that already failed
	type stepEntry struct {
		step AutoStep
		idx  int
	}
	var steps []stepEntry
	for i, s := range autoSequence {
		if s.Category == lastCat && s.Transport == lastTrans && s.IP == lastIP {
			continue
		}
		steps = append(steps, stepEntry{step: s, idx: i + 1})
	}

	total := len(steps)
	for i, entry := range steps {
		if !a.isAutoConnectActive() {
			return
		}
		s := entry.step
		label := fmt.Sprintf("[%d/%d] %s / %s / %s", i+1, total, s.Category, s.Transport, s.IP)

		runtime.EventsEmit(a.ctx, "auto:step", map[string]interface{}{
			"step":      i + 1,
			"total":     total,
			"category":  s.Category,
			"transport": s.Transport,
			"ip":        s.IP,
			"label":     label,
		})
		runtime.LogInfo(a.ctx, fmt.Sprintf("[Auto] Trying %s", label))

		if a.tryBridgeConfigAuto(s.Category, s.Transport, s.IP, timeoutS) {
			runtime.EventsEmit(a.ctx, "auto:done", map[string]interface{}{
				"category":  s.Category,
				"transport": s.Transport,
				"ip":        s.IP,
				"label":     label,
			})
			runtime.LogInfo(a.ctx, fmt.Sprintf("[Auto] ✅ Connected with %s", label))
			return
		}
	}

	if a.isAutoConnectActive() {
		runtime.EventsEmit(a.ctx, "auto:failed", map[string]interface{}{
			"message": "All bridge groups exhausted.",
		})
		runtime.LogInfo(a.ctx, "[Auto] ❌ All bridge groups exhausted.")
	}
}

func (a *App) tryBridgeConfigAuto(cat, trans, ip string, timeoutS int) bool {
	if !a.IsPortFree(TorSOCKSPort) {
		runtime.EventsEmit(a.ctx, "auto:log", fmt.Sprintf("[Auto] Port %d already in use.", TorSOCKSPort))
		return false
	}

	torExe := a.GetTorExePath()
	if _, err := os.Stat(torExe); os.IsNotExist(err) {
		runtime.EventsEmit(a.ctx, "auto:log", "[Auto] tor.exe not found.")
		return false
	}

	torrc := a.GenerateTorrc(cat, trans, ip, "delta-kronecker")

	stopCh := make(chan struct{})
	a.torMu.Lock()
	a.stopCh = stopCh
	a.connected = false
	a.uptimeStart = time.Now()
	a.torMu.Unlock()

	cmd := exec.Command(torExe, "-f", torrc)
	cmd.SysProcAttr = &syscall.SysProcAttr{CreationFlags: 0x08000000}
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		runtime.EventsEmit(a.ctx, "auto:log", fmt.Sprintf("[Auto] stdout pipe error: %v", err))
		return false
	}
	cmd.Stderr = cmd.Stdout

	if err := cmd.Start(); err != nil {
		stdout.Close()
		runtime.EventsEmit(a.ctx, "auto:log", fmt.Sprintf("[Auto] Launch error: %v", err))
		return false
	}

	a.torMu.Lock()
	a.torProcess = cmd.Process
	a.torMu.Unlock()

	runtime.EventsEmit(a.ctx, "auto:log", fmt.Sprintf("[Auto] Tor started with %s / %s / %s", cat, trans, ip))

	scanner := bufio.NewScanner(stdout)
	re := regexp.MustCompile(`Bootstrapped (\d+)%`)
	lastPct := -1
	lastMove := time.Now()
	connected := false

	for scanner.Scan() {
		select {
		case <-stopCh:
			cmd.Process.Signal(os.Interrupt)
			go func() {
				time.Sleep(2 * time.Second)
				cmd.Process.Kill()
			}()
			return false
		default:
		}

		line := scanner.Text()
		runtime.EventsEmit(a.ctx, "tor:log", line)

		if strings.Contains(line, "Reading config failed") || strings.Contains(line, "Failed to parse/validate config") {
			runtime.EventsEmit(a.ctx, "auto:log", "[Auto] Config error.")
			cmd.Process.Signal(os.Interrupt)
			go func() {
				time.Sleep(2 * time.Second)
				cmd.Process.Kill()
			}()
			return false
		}

		if m := re.FindStringSubmatch(line); m != nil {
			pct, _ := strconv.Atoi(m[1])
			runtime.EventsEmit(a.ctx, "auto:progress", pct)
			lastMove = time.Now()

			if pct != lastPct {
				lastPct = pct
			}

			if pct == 100 && !connected {
				connected = true
				a.torMu.Lock()
				a.connected = true
				a.uptimeStart = time.Now()
				a.torMu.Unlock()
				a.ResetTrafficStats()

				// Save last success
				cfg := a.LoadConfig()
				cfg.LastSuccessCat = cat
				cfg.LastSuccessTrans = trans
				cfg.LastSuccessIP = ip
				a.SaveConfig(cfg)

				a.StartWatchdog()
				a.StartKeepAlive()
				return true
			}
		}

		if lastPct >= 0 && time.Now().Sub(lastMove).Seconds() > float64(timeoutS) {
			runtime.EventsEmit(a.ctx, "auto:log", fmt.Sprintf("[Auto] Stuck at %d%% for %ds → next", lastPct, timeoutS))
			cmd.Process.Signal(os.Interrupt)
			go func() {
				time.Sleep(2 * time.Second)
				cmd.Process.Kill()
			}()
			a.torMu.Lock()
			a.torProcess = nil
			a.connected = false
			a.torMu.Unlock()
			return false
		}
	}

	cmd.Wait()
	a.torMu.Lock()
	a.torProcess = nil
	a.connected = false
	a.torMu.Unlock()
	return false
}

func (a *App) setSystemProxy(port int) {
	proxyStr := fmt.Sprintf("127.0.0.1:%d", port)

	runtime.EventsEmit(a.ctx, "tor:log", fmt.Sprintf("[Proxy] Setting system proxy to %s", proxyStr))

	key, err := regOpenKey("")
	if err != nil {
		runtime.EventsEmit(a.ctx, "tor:log", fmt.Sprintf("[Proxy] RegOpenKey failed: %v", err))
		return
	}
	defer regCloseKey(key)

	regSetDword(key, "ProxyEnable", 1)
	regSetString(key, "ProxyServer", proxyStr)
	regSetString(key, "ProxyOverride", "127.0.0.1;localhost;<local>")

	internetSetOptionSet(39)
	internetSetOptionSet(37)

	runtime.EventsEmit(a.ctx, "tor:log", "[Proxy] System proxy applied via Windows API")
	runtime.EventsEmit(a.ctx, "proxy:set", proxyStr)
}

func (a *App) unsetSystemProxy() {
	runtime.EventsEmit(a.ctx, "tor:log", "[Proxy] Disabling system proxy")

	key, err := regOpenKey("")
	if err != nil {
		runtime.EventsEmit(a.ctx, "tor:log", fmt.Sprintf("[Proxy] RegOpenKey failed: %v", err))
		return
	}
	defer regCloseKey(key)

	regSetDword(key, "ProxyEnable", 0)
	regSetString(key, "ProxyServer", "")
	regSetString(key, "ProxyOverride", "")

	internetSetOptionSet(39)
	internetSetOptionSet(37)

	runtime.EventsEmit(a.ctx, "tor:log", "[Proxy] System proxy disabled via Windows API")
	runtime.EventsEmit(a.ctx, "proxy:unset", true)
}

func (a *App) IsTorConnected() bool {
	a.torMu.Lock()
	defer a.torMu.Unlock()
	return a.connected
}

func (a *App) SetSystemProxy() error {
	a.setSystemProxy(HTTPProxyPort)
	a.StartHTTPProxy()
	return nil
}

func (a *App) UnsetSystemProxy() {
	a.StopHTTPProxy()
	a.unsetSystemProxy()
}

func (a *App) GetUptime() string {
	a.torMu.Lock()
	conn := a.connected
	start := a.uptimeStart
	a.torMu.Unlock()
	if !conn {
		return "—"
	}
	d := time.Since(start)
	h := int(d.Hours())
	m := int(d.Minutes()) % 60
	s := int(d.Seconds()) % 60
	return fmt.Sprintf("%02d:%02d:%02d", h, m, s)
}

// ===================== WATCHDOG =====================

func (a *App) StartWatchdog() {
	a.StopWatchdog()
	cfg := a.LoadConfig()
	if !cfg.WatchdogEnabled {
		return
	}
	interval := time.Duration(cfg.WatchdogInterval) * time.Second
	if interval < 10 {
		interval = 10 * time.Second
	}
	stopCh := make(chan struct{})
	a.torMu.Lock()
	a.watchdogTicker = time.NewTicker(interval)
	a.watchdogStop = stopCh
	a.torMu.Unlock()
	go func() {
		for {
			select {
			case <-stopCh:
				return
			case <-a.watchdogTicker.C:
				a.watchdogTick()
			}
		}
	}()
}

func (a *App) StopWatchdog() {
	a.torMu.Lock()
	ticker := a.watchdogTicker
	stop := a.watchdogStop
	a.watchdogTicker = nil
	a.watchdogStop = nil
	a.torMu.Unlock()

	if ticker != nil {
		ticker.Stop()
	}
	if stop != nil {
		close(stop)
	}
}

func (a *App) watchdogTick() {
	cfg := a.LoadConfig()
	if !cfg.WatchdogEnabled {
		return
	}
	a.torMu.Lock()
	proc := a.torProcess
	a.torMu.Unlock()

	if proc == nil {
		return
	}

	// Check if process is still alive by sending signal 0
	err := proc.Signal(syscall.Signal(0))
	if err == nil {
		return
	}

	// Process died
	runtime.EventsEmit(a.ctx, "tor:log", "[Watchdog] Tor process died — restarting…\n")
	a.NotifyDesktop("Delta Tor", "Tor process died — restarting…")
	a.torMu.Lock()
	a.torProcess = nil
	a.connected = false
	a.torMu.Unlock()
	time.Sleep(500 * time.Millisecond)
	a.runTorInternal()
}

func (a *App) runTorInternal() {
	a.torMu.Lock()
	hasStopCh := a.stopCh != nil
	a.torMu.Unlock()
	if !hasStopCh {
		return
	}

	if !a.IsPortFree(TorSOCKSPort) {
		runtime.EventsEmit(a.ctx, "tor:log", fmt.Sprintf("[Watchdog] Port %d busy, skipping restart.\n", TorSOCKSPort))
		return
	}

	cfg := a.LoadConfig()
	cat := cfg.LastSuccessCat
	trans := cfg.LastSuccessTrans
	ip := cfg.LastSuccessIP

	torrc := a.GenerateTorrc(cat, trans, ip, "delta-kronecker")
	torExe := a.GetTorExePath()
	if _, err := os.Stat(torExe); os.IsNotExist(err) {
		runtime.EventsEmit(a.ctx, "tor:log", "[Watchdog] tor.exe not found.\n")
		return
	}

	a.torMu.Lock()
	a.stopCh = make(chan struct{})
	a.connected = false
	a.uptimeStart = time.Now()
	a.torMu.Unlock()

	cmd := exec.Command(torExe, "-f", torrc)
	cmd.SysProcAttr = &syscall.SysProcAttr{CreationFlags: 0x08000000}
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		runtime.EventsEmit(a.ctx, "tor:log", fmt.Sprintf("[Watchdog] stdout pipe error: %v\n", err))
		return
	}
	cmd.Stderr = cmd.Stdout

	if err := cmd.Start(); err != nil {
		stdout.Close()
		runtime.EventsEmit(a.ctx, "tor:log", fmt.Sprintf("[Watchdog] Launch error: %v\n", err))
		return
	}

	a.torMu.Lock()
	a.torProcess = cmd.Process
	a.torMu.Unlock()

	runtime.EventsEmit(a.ctx, "tor:log", "[Watchdog] Tor restarted.\n")
	go a.readTorOutput(stdout, cmd)
}

// ===================== KEEP-ALIVE =====================

func (a *App) StartKeepAlive() {
	a.StopKeepAlive()
	cfg := a.LoadConfig()
	if !cfg.KeepAliveEnabled {
		return
	}
	interval := time.Duration(cfg.KeepAliveInterval) * time.Second
	if interval < 30 {
		interval = 30 * time.Second
	}
	stopCh := make(chan struct{})
	a.torMu.Lock()
	a.keepaliveTicker = time.NewTicker(interval)
	a.keepaliveStop = stopCh
	a.torMu.Unlock()
	go func() {
		for {
			select {
			case <-stopCh:
				return
			case <-a.keepaliveTicker.C:
				a.keepaliveTick()
			}
		}
	}()
}

func (a *App) StopKeepAlive() {
	a.torMu.Lock()
	ticker := a.keepaliveTicker
	stop := a.keepaliveStop
	a.keepaliveTicker = nil
	a.keepaliveStop = nil
	a.torMu.Unlock()

	if ticker != nil {
		ticker.Stop()
	}
	if stop != nil {
		close(stop)
	}
}

func (a *App) keepaliveTick() {
	a.torMu.Lock()
	conn := a.connected
	a.torMu.Unlock()
	if !conn {
		return
	}
	go func() {
		_, err := socks5Request("check.torproject.org", 443, "/api/ip", TorSOCKSPort, true, 10)
		if err != nil {
			runtime.EventsEmit(a.ctx, "tor:log", "[KeepAlive] Ping failed.\n")
		}
	}()
}

// ===================== NEW CIRCUIT =====================

func (a *App) RequestNewCircuit() error {
	a.torMu.Lock()
	conn := a.connected
	a.torMu.Unlock()
	if !conn {
		runtime.EventsEmit(a.ctx, "tor:log", "[Circuit] Not connected.\n")
		return fmt.Errorf("not connected")
	}

	go a.sendNewNym()
	return nil
}

func (a *App) sendNewNym() {
	cookieFile := filepath.Join(a.dataDir, "data", "control_auth_cookie")
	cookieData, err := os.ReadFile(cookieFile)
	if err != nil {
		runtime.EventsEmit(a.ctx, "tor:log", fmt.Sprintf("[Circuit] Failed to read cookie: %v\n", err))
		return
	}
	cookieHex := fmt.Sprintf("%x", cookieData)

	conn, err := net.DialTimeout("tcp", fmt.Sprintf("127.0.0.1:%d", TorCtrlPort), 5*time.Second)
	if err != nil {
		runtime.EventsEmit(a.ctx, "tor:log", fmt.Sprintf("[Circuit] Failed to connect: %v\n", err))
		return
	}
	defer conn.Close()
	conn.SetDeadline(time.Now().Add(5 * time.Second))

	conn.Write([]byte(fmt.Sprintf("AUTHENTICATE %s\r\n", cookieHex)))
	buf := make([]byte, 256)
	conn.Read(buf)

	conn.Write([]byte("SIGNAL NEWNYM\r\n"))
	n, _ := conn.Read(buf)
	resp := string(buf[:n])

	if strings.Contains(resp, "250") {
		runtime.EventsEmit(a.ctx, "tor:log", "[Circuit] New circuit requested ✅\n")
		a.NotifyDesktop("Delta Tor", "New circuit obtained.")
	} else {
		runtime.EventsEmit(a.ctx, "tor:log", fmt.Sprintf("[Circuit] Response: %s\n", strings.TrimSpace(resp)))
	}
}

type TestResult struct {
	IP      string `json:"ip"`
	Country string `json:"country"`
	IsTor   bool   `json:"isTor"`
}

func (a *App) TestConnection() TestResult {
	runtime.EventsEmit(a.ctx, "tor:log", "[Test] Checking connection...")

	if !a.IsTorConnected() {
		runtime.EventsEmit(a.ctx, "tor:log", "[Test] Tor not connected")
		return TestResult{IP: "—", Country: "—", IsTor: false}
	}

	resp, err := socks5Request("check.torproject.org", 443, "/api/ip", TorSOCKSPort, true, 15)
	if err != nil {
		runtime.EventsEmit(a.ctx, "tor:log", "[Test] Error: "+err.Error())
		return TestResult{IP: "—", Country: "—", IsTor: false}
	}

	resp = strings.TrimSpace(resp)
	runtime.EventsEmit(a.ctx, "tor:log", "[Test] Response: "+truncate(resp, 300))

	if !strings.HasPrefix(resp, "{") {
		runtime.EventsEmit(a.ctx, "tor:log", "[Test] Not JSON response")
		return TestResult{IP: "—", Country: "—", IsTor: false}
	}

	var result struct {
		IP    string `json:"IP"`
		IsTor bool   `json:"IsTor"`
	}
	if err := json.Unmarshal([]byte(resp), &result); err != nil {
		runtime.EventsEmit(a.ctx, "tor:log", "[Test] JSON error: "+err.Error())
		return TestResult{IP: "—", Country: "—", IsTor: false}
	}

	runtime.EventsEmit(a.ctx, "tor:log", "[Test] Exit IP: "+result.IP+"  Tor: "+fmt.Sprintf("%v", result.IsTor))

	country := lookupCountry(result.IP)
	runtime.EventsEmit(a.ctx, "tor:log", "[Test] Country: "+country)

	return TestResult{IP: result.IP, Country: country, IsTor: result.IsTor}
}

func truncate(s string, n int) string {
	if len(s) <= n {
		return s
	}
	return s[:n] + "..."
}

func socks5Request(host string, port int, path string, proxyPort int, useSSL bool, timeout int) (string, error) {
	conn, err := net.DialTimeout("tcp", "127.0.0.1:"+strconv.Itoa(proxyPort), time.Duration(timeout)*time.Second)
	if err != nil {
		return "", fmt.Errorf("dial to proxy %d: %v", proxyPort, err)
	}
	defer conn.Close()
	conn.SetDeadline(time.Now().Add(time.Duration(timeout) * time.Second))

	// SOCKS5 handshake
	conn.Write([]byte{0x05, 0x01, 0x00})
	handshakeResp := make([]byte, 2)
	if _, err := io.ReadFull(conn, handshakeResp); err != nil {
		return "", fmt.Errorf("socks5 handshake read: %v", err)
	}
	if handshakeResp[1] != 0x00 {
		return "", fmt.Errorf("socks5 handshake failed: %d", handshakeResp[1])
	}

	// SOCKS5 CONNECT
	hostBytes := []byte(host)
	req := make([]byte, 0, 7+len(hostBytes))
	req = append(req, 0x05, 0x01, 0x00, 0x03, byte(len(hostBytes)))
	req = append(req, hostBytes...)
	req = append(req, byte(port>>8), byte(port&0xff))
	conn.Write(req)

	connectResp := make([]byte, 10)
	if _, err := io.ReadFull(conn, connectResp); err != nil {
		return "", fmt.Errorf("socks5 connect read: %v", err)
	}
	if connectResp[1] != 0x00 {
		return "", fmt.Errorf("socks5 connect error: %d", connectResp[1])
	}

	if useSSL {
		tlsConn := tls.Client(conn, &tls.Config{ServerName: host})
		if err := tlsConn.Handshake(); err != nil {
			return "", fmt.Errorf("TLS handshake: %v", err)
		}
		defer tlsConn.Close()
		tlsConn.Write([]byte(fmt.Sprintf("GET %s HTTP/1.1\r\nHost: %s\r\nConnection: close\r\nUser-Agent: Mozilla/5.0\r\n\r\n", path, host)))
		var result strings.Builder
		buf := make([]byte, 65536)
		for {
			n, err := tlsConn.Read(buf)
			if n > 0 {
				result.Write(buf[:n])
			}
			if err != nil {
				break
			}
		}
		full := result.String()
		sep := strings.Index(full, "\r\n\r\n")
		if sep != -1 {
			return full[sep+4:], nil
		}
		return full, nil
	}

	conn.Write([]byte(fmt.Sprintf("GET %s HTTP/1.1\r\nHost: %s\r\nConnection: close\r\nUser-Agent: Mozilla/5.0\r\n\r\n", path, host)))
	var result strings.Builder
	buf := make([]byte, 65536)
	for {
		n, err := conn.Read(buf)
		if n > 0 {
			result.Write(buf[:n])
		}
		if err != nil {
			break
		}
	}
	full := result.String()
	sep := strings.Index(full, "\r\n\r\n")
	if sep != -1 {
		return full[sep+4:], nil
	}
	return full, nil
}

func lookupCountry(ip string) string {
	if ip == "" || ip == "—" {
		return "—"
	}
	resp, err := socks5Request("api.ip2location.io", 443, "/?ip="+ip, TorSOCKSPort, true, 12)
	if err != nil {
		return "?"
	}
	resp = strings.TrimSpace(resp)
	if !strings.HasPrefix(resp, "{") {
		return "?"
	}
	var data struct {
		CountryName string `json:"country_name"`
	}
	if err := json.Unmarshal([]byte(resp), &data); err != nil {
		return "?"
	}
	if data.CountryName == "" {
		return "?"
	}
	return data.CountryName
}

func copyFile(src, dst string) error {
	data, err := os.ReadFile(src)
	if err != nil {
		return err
	}
	return os.WriteFile(dst, data, 0644)
}

type BridgeInfo struct {
	Category string `json:"category"`
	Transport string `json:"transport"`
	IP       string `json:"ip"`
	Filename string `json:"filename"`
	Count    int    `json:"count"`
	Updated  string `json:"updated"`
	URL      string `json:"url"`
}

type BridgeOverview struct {
	TotalFiles   int          `json:"totalFiles"`
	TotalBridges int          `json:"totalBridges"`
	Transports   int          `json:"transports"`
	Categories   int          `json:"categories"`
	Bridges      []BridgeInfo `json:"bridges"`
}

var bridgeData = []struct {
	Category string
	Transport string
	IP       string
	URL      string
}{
	{"Tested & Active", "obfs4", "IPv4", "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/obfs4_tested.txt"},
	{"Tested & Active", "webtunnel", "IPv4", "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/webtunnel_tested.txt"},
	{"Tested & Active", "vanilla", "IPv4", "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/vanilla_tested.txt"},
	{"Fresh (72h)", "obfs4", "IPv4", "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/obfs4_72h.txt"},
	{"Fresh (72h)", "obfs4", "IPv6", "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/obfs4_ipv6_72h.txt"},
	{"Fresh (72h)", "webtunnel", "IPv4", "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/webtunnel_72h.txt"},
	{"Fresh (72h)", "webtunnel", "IPv6", "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/webtunnel_ipv6_72h.txt"},
	{"Fresh (72h)", "vanilla", "IPv4", "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/vanilla_72h.txt"},
	{"Fresh (72h)", "vanilla", "IPv6", "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/vanilla_ipv6_72h.txt"},
	{"Full Archive", "obfs4", "IPv4", "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/obfs4.txt"},
	{"Full Archive", "obfs4", "IPv6", "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/obfs4_ipv6.txt"},
	{"Full Archive", "webtunnel", "IPv4", "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/webtunnel.txt"},
	{"Full Archive", "webtunnel", "IPv6", "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/webtunnel_ipv6.txt"},
	{"Full Archive", "vanilla", "IPv4", "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/vanilla.txt"},
	{"Full Archive", "vanilla", "IPv6", "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/vanilla_ipv6.txt"},
}

func (a *App) getSafeFilename(cat, trans, ip string) string {
	safe := strings.ReplaceAll(cat, " ", "_")
	safe = strings.ReplaceAll(safe, "&", "and")
	safe = strings.ReplaceAll(safe, "(", "")
	safe = strings.ReplaceAll(safe, ")", "")
	return fmt.Sprintf("%s_%s_%s.txt", safe, trans, ip)
}

func (a *App) downloadFile(url, dest string) error {
	client := &http.Client{Timeout: 30 * time.Second}
	req, err := http.NewRequest("GET", url, nil)
	if err != nil {
		return err
	}
	req.Header.Set("User-Agent", "Mozilla/5.0")
	resp, err := client.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("HTTP %d", resp.StatusCode)
	}
	tmp := dest + ".tmp"
	f, err := os.Create(tmp)
	if err != nil {
		return err
	}
	if _, err = io.Copy(f, resp.Body); err != nil {
		f.Close()
		os.Remove(tmp)
		return err
	}
	f.Close()
	return os.Rename(tmp, dest)
}

func (a *App) DownloadAllBridges() {
	runtime.EventsEmit(a.ctx, "tor:log", "[Bridges] Starting bridge download...")
	os.MkdirAll(a.bridgesDir, 0755)

	total := len(bridgeData)
	var done int64

	var wg sync.WaitGroup
	sem := make(chan struct{}, 3)

	for _, entry := range bridgeData {
		wg.Add(1)
		sem <- struct{}{}
		go func(cat, trans, ip, url string) {
			defer wg.Done()
			defer func() { <-sem }()

			filename := a.getSafeFilename(cat, trans, ip)
			fpath := filepath.Join(a.bridgesDir, filename)

			for attempt := 0; attempt < 4; attempt++ {
				err := a.downloadFile(url, fpath)
				if err == nil {
					break
				}
				if attempt < 3 {
					time.Sleep(time.Duration(min(1<<attempt, 16)) * time.Second)
				}
			}

			current := atomic.AddInt64(&done, 1)
			pct := int(current) * 100 / total
			runtime.EventsEmit(a.ctx, "tor:log", fmt.Sprintf("[Bridges] Downloaded %s %s %s (%d/%d)", cat, trans, ip, current, total))
			runtime.EventsEmit(a.ctx, "bridge:progress", pct)
		}(entry.Category, entry.Transport, entry.IP, entry.URL)
	}

	wg.Wait()
	runtime.EventsEmit(a.ctx, "tor:log", fmt.Sprintf("[Bridges] All %d bridge files downloaded", total))
	runtime.EventsEmit(a.ctx, "bridge:done", true)
}

func (a *App) GetBridgeInfo() BridgeOverview {
	runtime.EventsEmit(a.ctx, "tor:log", fmt.Sprintf("[BridgeInfo] Reading from: %s", a.bridgesDir))

	var bridges []BridgeInfo
	totalBridges := 0
	transports := make(map[string]bool)
	categories := make(map[string]bool)

	for _, entry := range bridgeData {
		filename := a.getSafeFilename(entry.Category, entry.Transport, entry.IP)
		fpath := filepath.Join(a.bridgesDir, filename)

		count := 0
		updated := "—"
		if info, err := os.Stat(fpath); err == nil {
			updated = info.ModTime().Format("2006-01-02 15:04:05")
			if f, err := os.Open(fpath); err == nil {
				scanner := bufio.NewScanner(f)
				for scanner.Scan() {
					line := strings.TrimSpace(scanner.Text())
					if line != "" && !strings.HasPrefix(line, "#") {
						count++
					}
				}
				f.Close()
			}
		}

		totalBridges += count
		transports[entry.Transport] = true
		categories[entry.Category] = true

		bridges = append(bridges, BridgeInfo{
			Category:  entry.Category,
			Transport: entry.Transport,
			IP:        entry.IP,
			Filename:  filename,
			Count:     count,
			Updated:   updated,
		})
	}

	runtime.EventsEmit(a.ctx, "tor:log", fmt.Sprintf("[BridgeInfo] Found %d bridges in %d files", totalBridges, len(bridgeData)))

	return BridgeOverview{
		TotalFiles:   len(bridgeData),
		TotalBridges: totalBridges,
		Transports:   len(transports),
		Categories:   len(categories),
		Bridges:      bridges,
	}
}

func (a *App) StartHTTPProxy() error {
	a.proxyMu.Lock()
	if a.proxyServer != nil {
		a.proxyMu.Unlock()
		return nil
	}
	a.proxyMu.Unlock()

	listener, err := net.Listen("tcp", "127.0.0.1:"+strconv.Itoa(HTTPProxyPort))
	if err != nil {
		runtime.EventsEmit(a.ctx, "tor:log", fmt.Sprintf("[HTTP Proxy] Failed to start: %v", err))
		return fmt.Errorf("failed to start HTTP proxy: %v", err)
	}

	a.proxyMu.Lock()
	a.proxyServer = listener
	a.proxyMu.Unlock()

	runtime.EventsEmit(a.ctx, "tor:log", fmt.Sprintf("[HTTP Proxy] Started on 127.0.0.1:%d", HTTPProxyPort))

	go a.acceptHTTPConnections(listener)
	return nil
}

func (a *App) StopHTTPProxy() {
	a.proxyMu.Lock()
	if a.proxyServer != nil {
		a.proxyServer.Close()
		a.proxyServer = nil
	}
	a.proxyMu.Unlock()
}

func (a *App) acceptHTTPConnections(listener net.Listener) {
	for {
		conn, err := listener.Accept()
		if err != nil {
			return
		}
		go a.handleHTTPConnection(conn)
	}
}

func (a *App) handleHTTPConnection(clientConn net.Conn) {
	defer clientConn.Close()

	buf := make([]byte, 65536)
	clientConn.SetReadDeadline(time.Now().Add(15 * time.Second))
	n, err := clientConn.Read(buf)
	if err != nil || n == 0 {
		return
	}

	firstLine := string(buf[:n])
	if strings.HasPrefix(firstLine, "CONNECT") {
		runtime.EventsEmit(a.ctx, "tor:log", fmt.Sprintf("[HTTP Proxy] CONNECT %s", truncate(firstLine, 100)))
		a.handleHTTPConnect(clientConn, buf[:n])
	} else if strings.HasPrefix(firstLine, "GET ") || strings.HasPrefix(firstLine, "POST ") || strings.HasPrefix(firstLine, "PUT ") || strings.HasPrefix(firstLine, "HEAD ") {
		runtime.EventsEmit(a.ctx, "tor:log", fmt.Sprintf("[HTTP Proxy] %s", truncate(firstLine, 100)))
		a.handleHTTPRequest(clientConn, buf[:n])
	} else {
		clientConn.Write([]byte("HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n"))
	}
}

func (a *App) handleHTTPConnect(clientConn net.Conn, initialData []byte) {
	lines := strings.Split(string(initialData), "\r\n")
	if len(lines) == 0 {
		return
	}

	firstLine := strings.SplitN(lines[0], " ", 3)
	if len(firstLine) < 3 {
		clientConn.Write([]byte("HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n"))
		return
	}

	target := firstLine[1]
	host := target
	port := 443
	if h, p, err := net.SplitHostPort(target); err == nil {
		host = h
		port, _ = strconv.Atoi(p)
	}

	torConn, err := net.DialTimeout("tcp", "127.0.0.1:"+strconv.Itoa(TorSOCKSPort), 10*time.Second)
	if err != nil {
		runtime.EventsEmit(a.ctx, "tor:log", fmt.Sprintf("[HTTP Proxy] SOCKS connect failed: %v", err))
		clientConn.Write([]byte("HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\n\r\n"))
		return
	}
	defer torConn.Close()

	if err := socks5Handshake(torConn, host, port); err != nil {
		runtime.EventsEmit(a.ctx, "tor:log", fmt.Sprintf("[HTTP Proxy] SOCKS5 handshake failed: %v", err))
		clientConn.Write([]byte("HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\n\r\n"))
		return
	}

	clientConn.Write([]byte("HTTP/1.1 200 Connection established\r\n\r\n"))
	a.relayData(clientConn, torConn)
}

func (a *App) handleHTTPRequest(clientConn net.Conn, initialData []byte) {
	lines := strings.Split(string(initialData), "\r\n")
	if len(lines) == 0 {
		return
	}

	firstLine := strings.SplitN(lines[0], " ", 3)
	if len(firstLine) < 3 {
		clientConn.Write([]byte("HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n"))
		return
	}

	method := firstLine[0]
	target := firstLine[1]

	parsed, err := url.Parse(target)
	if err != nil {
		clientConn.Write([]byte("HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n"))
		return
	}
	host := parsed.Hostname()
	portStr := parsed.Port()
	port := 80
	if portStr != "" {
		port, _ = strconv.Atoi(portStr)
	}
	path := parsed.Path
	if path == "" {
		path = "/"
	}
	if parsed.RawQuery != "" {
		path += "?" + parsed.RawQuery
	}

	torConn, err := net.DialTimeout("tcp", "127.0.0.1:"+strconv.Itoa(TorSOCKSPort), 10*time.Second)
	if err != nil {
		clientConn.Write([]byte("HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\n\r\n"))
		return
	}
	defer torConn.Close()

	if err := socks5Handshake(torConn, host, port); err != nil {
		runtime.EventsEmit(a.ctx, "tor:log", fmt.Sprintf("[HTTP Proxy] SOCKS5 handshake failed: %v", err))
		clientConn.Write([]byte("HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\n\r\n"))
		return
	}

	headerEnd := strings.Index(string(initialData), "\r\n\r\n")
	headerPart := initialData
	if headerEnd != -1 {
		headerPart = initialData[:headerEnd]
	}
	body := []byte{}
	if headerEnd != -1 {
		body = initialData[headerEnd+4:]
	}
	headerLines := strings.Split(string(headerPart), "\r\n")
	headerLines[0] = fmt.Sprintf("%s %s HTTP/1.1", method, path)
	torConn.Write([]byte(strings.Join(headerLines, "\r\n") + "\r\n\r\n"))
	torConn.Write(body)

	a.relayData(clientConn, torConn)
}

type countingReader struct {
	net.Conn
	counter *int64
}

func (r *countingReader) Read(b []byte) (int, error) {
	n, err := r.Conn.Read(b)
	if n > 0 {
		atomic.AddInt64(r.counter, int64(n))
	}
	return n, err
}

func (a *App) relayData(clientConn net.Conn, torConn net.Conn) {
	if tc, ok := clientConn.(*net.TCPConn); ok {
		tc.SetNoDelay(true)
		tc.SetReadBuffer(524288)
		tc.SetWriteBuffer(524288)
	}
	if tc, ok := torConn.(*net.TCPConn); ok {
		tc.SetNoDelay(true)
		tc.SetReadBuffer(524288)
		tc.SetWriteBuffer(524288)
	}

	var wg sync.WaitGroup
	wg.Add(2)

	buf := make([]byte, 262144)

	go func() {
		defer wg.Done()
		io.CopyBuffer(clientConn, &countingReader{torConn, &a.dlBytes}, buf)
	}()

	go func() {
		defer wg.Done()
		io.CopyBuffer(torConn, &countingReader{clientConn, &a.ulBytes}, buf)
	}()

	wg.Wait()
	torConn.Close()
}

func (a *App) GetTrafficStats() map[string]string {
	dl := atomic.LoadInt64(&a.dlBytes)
	ul := atomic.LoadInt64(&a.ulBytes)
	dlPrev := atomic.LoadInt64(&a.dlPrev)
	ulPrev := atomic.LoadInt64(&a.ulPrev)
	atomic.StoreInt64(&a.dlPrev, dl)
	atomic.StoreInt64(&a.ulPrev, ul)

	dlSpeed := float64(dl-dlPrev) / 2.0
	ulSpeed := float64(ul-ulPrev) / 2.0

	return map[string]string{
		"download": formatSpeed(dlSpeed),
		"upload":   formatSpeed(ulSpeed),
	}
}

func formatSpeed(bytesPerSec float64) string {
	if bytesPerSec < 1024 {
		return fmt.Sprintf("%.1f B/s", bytesPerSec)
	} else if bytesPerSec < 1024*1024 {
		return fmt.Sprintf("%.1f KB/s", bytesPerSec/1024)
	}
	return fmt.Sprintf("%.1f MB/s", bytesPerSec/(1024*1024))
}

func (a *App) ResetTrafficStats() {
	atomic.StoreInt64(&a.dlBytes, 0)
	atomic.StoreInt64(&a.ulBytes, 0)
	atomic.StoreInt64(&a.dlPrev, 0)
	atomic.StoreInt64(&a.ulPrev, 0)
}

func formatBytes(bytes int64) string {
	if bytes < 1024 {
		return fmt.Sprintf("%d B", bytes)
	} else if bytes < 1024*1024 {
		return fmt.Sprintf("%.1f KB", float64(bytes)/1024)
	} else if bytes < 1024*1024*1024 {
		return fmt.Sprintf("%.1f MB", float64(bytes)/(1024*1024))
	}
	return fmt.Sprintf("%.1f GB", float64(bytes)/(1024*1024*1024))
}

type SpeedResult struct {
	Download string `json:"download"`
	Upload   string `json:"upload"`
}

func (a *App) TestSpeed() *SpeedResult {
	if !a.IsTorConnected() {
		return nil
	}

	downloadSpeed := testDownloadSpeed(a)
	uploadSpeed := testUploadSpeed(a)

	return &SpeedResult{
		Download: downloadSpeed,
		Upload:   uploadSpeed,
	}
}

func dialThroughSocks(host string, port int, proxyPort int) (*tls.Conn, error) {
	conn, err := net.DialTimeout("tcp", "127.0.0.1:"+strconv.Itoa(proxyPort), 5*time.Second)
	if err != nil {
		return nil, err
	}
	if err := socks5Handshake(conn, host, port); err != nil {
		conn.Close()
		return nil, err
	}
	tlsConn := tls.Client(conn, &tls.Config{ServerName: host})
	if err := tlsConn.Handshake(); err != nil {
		conn.Close()
		return nil, err
	}
	return tlsConn, nil
}

func testDownloadSpeed(a *App) string {
	start := time.Now()
	host := "check.torproject.org"

	tlsConn, err := dialThroughSocks(host, 443, TorSOCKSPort)
	if err != nil {
		return "\u2014"
	}
	defer tlsConn.Close()

	tlsConn.Write([]byte("GET /api/ip HTTP/1.1\r\nHost: " + host + "\r\nConnection: close\r\n\r\n"))
	totalBytes := 0
	buf := make([]byte, 65536)
	for {
		n, err := tlsConn.Read(buf)
		totalBytes += n
		if err != nil {
			break
		}
	}

	elapsed := time.Since(start).Seconds()
	if elapsed < 0.1 {
		return "\u2014"
	}
	speed := float64(totalBytes) / elapsed / 1024
	return fmt.Sprintf("%.1f KB/s", speed)
}

func testUploadSpeed(a *App) string {
	start := time.Now()
	host := "httpbin.org"

	tlsConn, err := dialThroughSocks(host, 443, TorSOCKSPort)
	if err != nil {
		return "\u2014"
	}
	defer tlsConn.Close()

	postData := strings.Repeat("X", 1048576)
	tlsConn.Write([]byte("POST /post HTTP/1.1\r\nHost: " + host + "\r\nContent-Length: " + strconv.Itoa(len(postData)) + "\r\nConnection: close\r\n\r\n" + postData))

	totalBytes := 0
	buf := make([]byte, 65536)
	for {
		n, err := tlsConn.Read(buf)
		totalBytes += n
		if err != nil {
			break
		}
	}

	elapsed := time.Since(start).Seconds()
	if elapsed < 0.1 {
		return "\u2014"
	}
	speed := float64(totalBytes) / elapsed / 1024
	return fmt.Sprintf("%.1f KB/s", speed)
}

// ===================== MULTI-CONNECT =====================

func (a *App) slotPorts(index int) (socksPort int, httpPort int) {
	return 9070 + index, 9100 + index
}

func (a *App) slotCtrlPort(index int) int {
	return 9120 + index
}

func (a *App) GetMultiSlots() []SlotDef {
	cfg := a.LoadConfig()
	return cfg.MultiSlots
}

func (a *App) SaveMultiSlots(slots []SlotDef) error {
	a.mu()
	a.multiSlots = slots
	a.muUnlock()
	cfg := a.LoadConfig()
	cfg.MultiSlots = slots
	return a.SaveConfig(cfg)
}

func (a *App) mu()       { a.multiMu.Lock() }
func (a *App) muUnlock() { a.multiMu.Unlock() }

func (a *App) AddMultiSlot(slot SlotDef) error {
	slots := a.GetMultiSlots()
	for _, s := range slots {
		if s.Label == slot.Label {
			slot.Label = fmt.Sprintf("%s (%d)", slot.Label, len(slots)+1)
		}
	}
	slots = append(slots, slot)
	return a.SaveMultiSlots(slots)
}

func (a *App) DeleteMultiSlot(label string) error {
	slots := a.GetMultiSlots()
	var newSlots []SlotDef
	for _, s := range slots {
		if s.Label != label {
			newSlots = append(newSlots, s)
		}
	}
	return a.SaveMultiSlots(newSlots)
}

func (a *App) GenerateSlotTorrc(socksPort, ctrlPort int, cat, trans, ip, source string, noBridge bool) string {
	dataDir := filepath.Join(a.dataDir, fmt.Sprintf("data_par_%d", socksPort))
	ptStateDir := filepath.Join(dataDir, "pt_state")
	os.MkdirAll(ptStateDir, 0755)
	a.copyGeoIPFiles(dataDir)

	bridgeLines := a.loadBridgeLines(cat, trans, ip, source, noBridge)

	var sb strings.Builder
	a.writeTorrcContent(&sb, torrcParams{
		dataDir:     dataDir,
		socksPort:   socksPort,
		ctrlPort:    ctrlPort,
		useBridges:  len(bridgeLines) > 0,
		bridgeLines: bridgeLines,
		writePT:     len(bridgeLines) > 0,
	})

	torrcPath := filepath.Join(dataDir, "torrc")
	os.WriteFile(torrcPath, []byte(sb.String()), 0644)
	return torrcPath
}

func (a *App) StartAllSlots() error {
	a.mu()
	if a.multiRunning {
		a.muUnlock()
		return fmt.Errorf("multi-connect already running")
	}
	a.multiRunning = true
	a.multiSlotStates = make(map[string]*SlotState)
	a.multiHealthData = make(map[string]*HealthData)
	a.multiTraffic = make(map[int]*SlotTraffic)
	a.muUnlock()

	slots := a.GetMultiSlots()
	for i, slot := range slots {
		if !slot.Enabled {
			continue
		}
		socks, http := a.slotPorts(i)
		ctrl := a.slotCtrlPort(i)
		go a.runSlot(slot, socks, ctrl, http, 0, 5)
	}
	return nil
}

func (a *App) StopAllSlots() {
	a.mu()
	a.multiRunning = false
	states := a.multiSlotStates
	a.multiSlotStates = make(map[string]*SlotState)
	a.multiHealthData = make(map[string]*HealthData)
	a.multiTraffic = make(map[int]*SlotTraffic)
	a.muUnlock()

	for _, st := range states {
		if st.StopCh != nil {
			select {
			case <-st.StopCh:
			default:
				close(st.StopCh)
			}
		}
		if st.Process != nil {
			st.Process.Signal(os.Interrupt)
			p := st.Process
			go func() {
				time.Sleep(2 * time.Second)
				p.Kill()
			}()
		}
	}

	a.StopHTTPProxy()
	a.unsetSystemProxy()

	a.mu()
	a.multiProxyLabel = ""
	a.multiProxyStop = nil
	if a.multiBalancerStop != nil {
		select {
		case <-a.multiBalancerStop:
		default:
			close(a.multiBalancerStop)
		}
		a.multiBalancerStop = nil
	}
	a.multiBalancerMode = ""
	a.multiBalancerCounter = 0
	a.muUnlock()

	a.cleanupMultiDataDirs()
	runtime.EventsEmit(a.ctx, "multi:stopped", true)
}

func (a *App) StopSlot(label string) {
	a.mu()
	st, ok := a.multiSlotStates[label]
	if ok {
		if st.StopCh != nil {
			select {
			case <-st.StopCh:
			default:
				close(st.StopCh)
			}
		}
		if st.Process != nil {
			st.Process.Signal(os.Interrupt)
			p := st.Process
			go func() {
				time.Sleep(2 * time.Second)
				p.Kill()
			}()
		}
		delete(a.multiSlotStates, label)
	}
	if a.multiProxyLabel == label {
		a.StopHTTPProxy()
		a.unsetSystemProxy()
		a.multiProxyLabel = ""
	}
	a.muUnlock()

	runtime.EventsEmit(a.ctx, "multi:slot:stopped", map[string]interface{}{"label": label})
}

func (a *App) cleanupMultiDataDirs() {
	entries, err := os.ReadDir(a.dataDir)
	if err != nil {
		return
	}
	for _, e := range entries {
		if e.IsDir() && strings.HasPrefix(e.Name(), "data_par_") {
			os.RemoveAll(filepath.Join(a.dataDir, e.Name()))
		}
	}
}

func runHTTPProxyServer(stopCh chan struct{}, socksHost string, socksPort int, httpPort int, traffic *SlotTraffic) {
	ln, err := net.Listen("tcp", fmt.Sprintf("127.0.0.1:%d", httpPort))
	if err != nil {
		return
	}
	defer ln.Close()

	for {
		select {
		case <-stopCh:
			return
		default:
		}
		ln.(*net.TCPListener).SetDeadline(time.Now().Add(1 * time.Second))
		conn, err := ln.Accept()
		if err != nil {
			if netErr, ok := err.(net.Error); ok && netErr.Timeout() {
				continue
			}
			return
		}
		go handleHTTPProxyConn(conn, socksHost, socksPort, traffic)
	}
}

func handleHTTPProxyConn(clientConn net.Conn, socksHost string, socksPort int, traffic *SlotTraffic) {
	defer clientConn.Close()
	clientConn.SetReadDeadline(time.Now().Add(15 * time.Second))

	buf := make([]byte, 65536)
	n, err := clientConn.Read(buf)
	if err != nil || n == 0 {
		return
	}
	data := buf[:n]

	firstLine := strings.SplitN(string(data), "\r\n", 2)[0]
	parts := strings.SplitN(firstLine, " ", 3)
	if len(parts) < 2 {
		return
	}

	method := parts[0]
	target := parts[1]

	if method == "CONNECT" {
		handleHTTPConnectProxy(clientConn, data, target, socksHost, socksPort, traffic)
	} else {
		handleHTTPRequestProxy(clientConn, data, method, target, socksHost, socksPort, traffic)
	}
}

func socks5Handshake(conn net.Conn, host string, port int) error {
	conn.Write([]byte{0x05, 0x01, 0x00})
	resp := make([]byte, 2)
	if _, err := io.ReadFull(conn, resp); err != nil {
		return fmt.Errorf("socks5 auth: %v", err)
	}
	if resp[1] != 0x00 {
		return fmt.Errorf("socks5 auth failed: %d", resp[1])
	}
	hostBytes := []byte(host)
	req := make([]byte, 0, 7+len(hostBytes))
	req = append(req, 0x05, 0x01, 0x00, 0x03, byte(len(hostBytes)))
	req = append(req, hostBytes...)
	req = append(req, byte(port>>8), byte(port&0xff))
	conn.Write(req)
	connResp := make([]byte, 10)
	if _, err := io.ReadFull(conn, connResp); err != nil {
		return fmt.Errorf("socks5 connect: %v", err)
	}
	if connResp[1] != 0x00 {
		return fmt.Errorf("socks5 connect error: %d", connResp[1])
	}
	return nil
}

func handleHTTPConnectProxy(clientConn net.Conn, initialData []byte, target, socksHost string, socksPort int, traffic *SlotTraffic) {
	host := target
	port := 443
	if h, p, err := net.SplitHostPort(target); err == nil {
		host = h
		port, _ = strconv.Atoi(p)
	}

	torConn, err := net.DialTimeout("tcp", fmt.Sprintf("%s:%d", socksHost, socksPort), 10*time.Second)
	if err != nil {
		clientConn.Write([]byte("HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\n\r\n"))
		return
	}
	defer torConn.Close()

	if err := socks5Handshake(torConn, host, port); err != nil {
		clientConn.Write([]byte("HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\n\r\n"))
		return
	}

	clientConn.Write([]byte("HTTP/1.1 200 Connection established\r\n\r\n"))
	relayHTTPData(clientConn, torConn, traffic)
}

func handleHTTPRequestProxy(clientConn net.Conn, initialData []byte, method, target, socksHost string, socksPort int, traffic *SlotTraffic) {
	parsed, err := url.Parse(target)
	if err != nil {
		clientConn.Write([]byte("HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n"))
		return
	}
	host := parsed.Hostname()
	port := 80
	if p := parsed.Port(); p != "" {
		port, _ = strconv.Atoi(p)
	}
	path := parsed.Path
	if path == "" {
		path = "/"
	}
	if parsed.RawQuery != "" {
		path += "?" + parsed.RawQuery
	}

	torConn, err := net.DialTimeout("tcp", fmt.Sprintf("%s:%d", socksHost, socksPort), 10*time.Second)
	if err != nil {
		clientConn.Write([]byte("HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\n\r\n"))
		return
	}
	defer torConn.Close()

	if err := socks5Handshake(torConn, host, port); err != nil {
		clientConn.Write([]byte("HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\n\r\n"))
		return
	}

	headerEnd := strings.Index(string(initialData), "\r\n\r\n")
	headerPart := initialData
	if headerEnd != -1 {
		headerPart = initialData[:headerEnd]
	}
	body := []byte{}
	if headerEnd != -1 {
		body = initialData[headerEnd+4:]
	}
	headerLines := strings.Split(string(headerPart), "\r\n")
	headerLines[0] = fmt.Sprintf("%s %s HTTP/1.1", method, path)
	torConn.Write([]byte(strings.Join(headerLines, "\r\n") + "\r\n\r\n"))
	torConn.Write(body)

	relayHTTPData(clientConn, torConn, traffic)
}

func relayHTTPData(clientConn net.Conn, torConn net.Conn, traffic *SlotTraffic) {
	if tc, ok := clientConn.(*net.TCPConn); ok {
		tc.SetNoDelay(true)
		tc.SetReadBuffer(524288)
		tc.SetWriteBuffer(524288)
	}
	if tc, ok := torConn.(*net.TCPConn); ok {
		tc.SetNoDelay(true)
		tc.SetReadBuffer(524288)
		tc.SetWriteBuffer(524288)
	}

	var wg sync.WaitGroup
	wg.Add(2)
	buf := make([]byte, 262144)

	if traffic != nil {
		go func() {
			defer wg.Done()
			io.CopyBuffer(clientConn, &countingReader{torConn, &traffic.DlBytes}, buf)
		}()
		go func() {
			defer wg.Done()
			io.CopyBuffer(torConn, &countingReader{clientConn, &traffic.UlBytes}, buf)
		}()
	} else {
		go func() {
			defer wg.Done()
			io.CopyBuffer(clientConn, torConn, buf)
		}()
		go func() {
			defer wg.Done()
			io.CopyBuffer(torConn, clientConn, buf)
		}()
	}

	wg.Wait()
	torConn.Close()
}

func (a *App) runSlot(slot SlotDef, socksPort, ctrlPort, httpPort int, retryCount, maxRetries int) {
	a.mu()
	if !a.multiRunning {
		a.muUnlock()
		return
	}
	a.muUnlock()

	torExe := a.GetTorExePath()
	if _, err := os.Stat(torExe); os.IsNotExist(err) {
		runtime.EventsEmit(a.ctx, "multi:slot:error", map[string]interface{}{
			"label": slot.Label, "message": "tor.exe not found",
		})
		return
	}

	if !a.IsPortFree(socksPort) {
		runtime.EventsEmit(a.ctx, "multi:slot:status", map[string]interface{}{
			"label": slot.Label, "status": fmt.Sprintf("Port %d busy — waiting…", socksPort),
		})
		for i := 0; i < 15; i++ {
			a.mu()
			if !a.multiRunning {
				a.muUnlock()
				return
			}
			a.muUnlock()
			time.Sleep(2 * time.Second)
			if a.IsPortFree(socksPort) {
				break
			}
		}
	}

	a.mu()
	if old, ok := a.multiSlotStates[slot.Label]; ok {
		if old.Process != nil {
			old.Process.Signal(os.Interrupt)
			p := old.Process
			go func() {
				time.Sleep(2 * time.Second)
				p.Kill()
			}()
			old.Process = nil
		}
	}
	a.muUnlock()

	torrc := a.GenerateSlotTorrc(socksPort, ctrlPort, slot.Category, slot.Transport, slot.IP, slot.Source, slot.NoBridge)

	dataDir := filepath.Join(a.dataDir, fmt.Sprintf("data_par_%d", socksPort))
	ptStateDir := filepath.Join(dataDir, "pt_state")
	os.MkdirAll(ptStateDir, 0755)

	stopCh := make(chan struct{})
	state := &SlotState{StopCh: stopCh, StartTime: time.Now()}
	a.mu()
	a.multiSlotStates[slot.Label] = state
	a.muUnlock()

	runtime.EventsEmit(a.ctx, "multi:slot:progress", map[string]interface{}{
		"label": slot.Label, "pct": 0, "status": "Connecting…",
	})

	slotEnv := os.Environ()
	slotEnv = append(slotEnv, "TOR_PT_STATE_LOCATION="+ptStateDir)

	cmd := exec.Command(torExe, "-f", torrc)
	cmd.SysProcAttr = &syscall.SysProcAttr{CreationFlags: 0x08000000}
	cmd.Env = slotEnv
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		runtime.EventsEmit(a.ctx, "multi:slot:error", map[string]interface{}{
			"label": slot.Label, "message": fmt.Sprintf("stdout pipe error: %v", err),
		})
		return
	}
	cmd.Stderr = cmd.Stdout

	if err := cmd.Start(); err != nil {
		stdout.Close()
		runtime.EventsEmit(a.ctx, "multi:slot:error", map[string]interface{}{
			"label": slot.Label, "message": fmt.Sprintf("Launch error: %v", err),
		})
		return
	}

	a.mu()
	if st, ok := a.multiSlotStates[slot.Label]; ok {
		st.Process = cmd.Process
	}
	a.muUnlock()

	scanner := bufio.NewScanner(stdout)
	re := regexp.MustCompile(`Bootstrapped (\d+)%`)
	lastPct := -1
	connected := false

	for scanner.Scan() {
		select {
		case <-stopCh:
			if cmd.Process != nil {
				cmd.Process.Signal(os.Interrupt)
				p := cmd.Process
				go func() {
					time.Sleep(2 * time.Second)
					p.Kill()
				}()
			}
			return
		default:
		}

		a.mu()
		if !a.multiRunning {
			a.muUnlock()
			return
		}
		a.muUnlock()

		line := scanner.Text()

		a.mu()
		if st, ok := a.multiSlotStates[slot.Label]; ok {
			st.Logs = append(st.Logs, line)
			if len(st.Logs) > 500 {
				st.Logs = st.Logs[len(st.Logs)-500:]
			}
		}
		a.muUnlock()

		runtime.EventsEmit(a.ctx, "multi:slot:log", map[string]interface{}{
			"label": slot.Label, "line": line,
		})

		if m := re.FindStringSubmatch(line); m != nil {
			pct, _ := strconv.Atoi(m[1])
			if pct != lastPct {
				lastPct = pct
			}

			a.mu()
			if st, ok := a.multiSlotStates[slot.Label]; ok {
				st.Progress = pct
				st.Status = fmt.Sprintf("Bootstrapped %d%%", pct)
			}
			a.muUnlock()

			runtime.EventsEmit(a.ctx, "multi:slot:progress", map[string]interface{}{
				"label": slot.Label, "pct": pct, "status": fmt.Sprintf("Bootstrapped %d%%", pct),
			})

			if pct == 100 && !connected {
				connected = true
				a.mu()
				if st, ok := a.multiSlotStates[slot.Label]; ok {
					st.Connected = true
					st.Status = "✔ Connected!"
				}
				a.muUnlock()

				runtime.EventsEmit(a.ctx, "multi:slot:progress", map[string]interface{}{
					"label": slot.Label, "pct": 100, "status": "✔ Connected!", "connected": true,
				})

				healthStop := make(chan struct{})
				a.mu()
				if st, ok := a.multiSlotStates[slot.Label]; ok {
					st.StopCh = healthStop
				}
				a.muUnlock()
				go a.slotHealthLoop(slot.Label, socksPort, healthStop)
			}
		}
	}

	cmd.Wait()

	a.mu()
	if st, ok := a.multiSlotStates[slot.Label]; ok {
		st.Process = nil
	}
	a.muUnlock()

	if !connected {
		a.mu()
		running := a.multiRunning
		a.muUnlock()
		if !running {
			return
		}
		if retryCount >= maxRetries {
			runtime.EventsEmit(a.ctx, "multi:slot:progress", map[string]interface{}{
				"label": slot.Label, "status": fmt.Sprintf("❌ Failed after %d retries", maxRetries), "failed": true,
			})
			return
		}
		delay := 30 + retryCount*15
		if delay > 90 {
			delay = 90
		}
		runtime.EventsEmit(a.ctx, "multi:slot:progress", map[string]interface{}{
			"label": slot.Label, "status": fmt.Sprintf("↺ Retry %d/%d in %ds…", retryCount+1, maxRetries, delay),
		})
		for remaining := delay; remaining > 0; remaining -= 10 {
			a.mu()
			if !a.multiRunning {
				a.muUnlock()
				return
			}
			a.muUnlock()
			time.Sleep(time.Duration(min(10, remaining)) * time.Second)
		}
		a.mu()
		running = a.multiRunning
		a.muUnlock()
		if running {
			a.runSlot(slot, socksPort, ctrlPort, httpPort, retryCount+1, maxRetries)
		}
	} else {
		a.mu()
		running := a.multiRunning
		a.muUnlock()
		if !running {
			return
		}
		runtime.EventsEmit(a.ctx, "multi:slot:progress", map[string]interface{}{
			"label": slot.Label, "status": "Died — restarting in 120s…", "failed": true,
		})
		for remaining := 120; remaining > 0; remaining -= 10 {
			a.mu()
			if !a.multiRunning {
				a.muUnlock()
				return
			}
			a.muUnlock()
			time.Sleep(time.Duration(min(10, remaining)) * time.Second)
		}
		a.mu()
		running = a.multiRunning
		a.muUnlock()
		if running {
			a.runSlot(slot, socksPort, ctrlPort, httpPort, 0, maxRetries)
		}
	}
}

func (a *App) RetrySlot(label string) {
	slots := a.GetMultiSlots()
	for i, s := range slots {
		if s.Label == label {
			socks, http := a.slotPorts(i)
			ctrl := a.slotCtrlPort(i)
			a.StopSlot(label)
			time.Sleep(500 * time.Millisecond)
			a.mu()
			if a.multiSlotStates == nil {
				a.multiSlotStates = make(map[string]*SlotState)
			}
			a.muUnlock()
			go a.runSlot(s, socks, ctrl, http, 0, 5)
			return
		}
	}
}

func (a *App) slotHealthLoop(label string, socksPort int, stopCh chan struct{}) {
	ticker := time.NewTicker(60 * time.Second)
	defer ticker.Stop()

	lastOnline := false
	lastLat := 0.0
	lastAvg := 0.0

	for {
		select {
		case <-stopCh:
			return
		case <-ticker.C:
			ok, lat := a.checkSlotHealth(socksPort, 15)
			if !ok {
				lat = 15000.0
			}

			a.mu()
			hd, exists := a.multiHealthData[label]
			if !exists {
				hd = &HealthData{}
				a.multiHealthData[label] = hd
			}
			hd.Online = ok
			hd.Latency = lat
			if len(hd.History) < 10 {
				hd.History = append(hd.History, lat)
			} else {
				hd.History = append(hd.History[1:], lat)
			}
			total := 0.0
			for _, v := range hd.History {
				total += v
			}
			hd.AvgLat = total / float64(len(hd.History))
			avgLat := hd.AvgLat
			a.muUnlock()

			if ok != lastOnline || lat != lastLat || avgLat != lastAvg {
				txt := ""
				if ok {
					txt = fmt.Sprintf("⬤ Online  %d ms  (avg %d ms)", int(lat), int(avgLat))
				} else {
					txt = fmt.Sprintf("⬤ Offline  (avg %d ms)", int(avgLat))
				}
				runtime.EventsEmit(a.ctx, "multi:slot:health", map[string]interface{}{
					"label": label, "online": ok, "latency": int(lat), "avgLat": int(avgLat), "text": txt,
				})
				lastOnline = ok
				lastLat = lat
				lastAvg = avgLat
			}
		}
	}
}

func (a *App) checkSlotHealth(socksPort int, timeout int) (bool, float64) {
	start := time.Now()
	host := "www.gstatic.com"

	tlsConn, err := dialThroughSocks(host, 443, socksPort)
	if err != nil {
		return false, float64(timeout * 1000)
	}
	defer tlsConn.Close()
	tlsConn.SetDeadline(time.Now().Add(time.Duration(timeout) * time.Second))

	tlsConn.Write([]byte("GET /generate_204 HTTP/1.1\r\nHost: www.gstatic.com\r\nConnection: close\r\nUser-Agent: Mozilla/5.0\r\n\r\n"))
	buf := make([]byte, 512)
	n, _ := tlsConn.Read(buf)
	latency := time.Since(start).Seconds() * 1000.0

	respStr := string(buf[:n])
	if strings.Contains(respStr, "204") || strings.Contains(respStr, "HTTP/1.") {
		return true, latency
	}
	return false, float64(timeout * 1000)
}

func (a *App) CheckSlotHealthNow(label string) {
	slots := a.GetMultiSlots()
	for i, s := range slots {
		if s.Label == label {
			socks, _ := a.slotPorts(i)
			go func() {
				ok, lat := a.checkSlotHealth(socks, 15)
				txt := ""
				if ok {
					txt = fmt.Sprintf("⬤ Online  %d ms", int(lat))
				} else {
					txt = "⬤ Offline"
				}
				runtime.EventsEmit(a.ctx, "multi:slot:health", map[string]interface{}{
					"label": label, "online": ok, "text": txt,
				})
			}()
			return
		}
	}
}

func (a *App) GetSlotLogs(label string) []string {
	a.mu()
	defer a.muUnlock()
	if st, ok := a.multiSlotStates[label]; ok {
		return st.Logs
	}
	return nil
}

func (a *App) TestSlotConnection(label string) TestResult {
	a.mu()
	st, ok := a.multiSlotStates[label]
	a.muUnlock()
	if !ok || !st.Connected {
		return TestResult{IP: "\u2014", Country: "\u2014", IsTor: false}
	}

	slots := a.GetMultiSlots()
	for i, s := range slots {
		if s.Label == label {
			socks, _ := a.slotPorts(i)
			resp, err := socks5Request("check.torproject.org", 443, "/api/ip", socks, true, 15)
			if err != nil {
				return TestResult{IP: "\u2014", Country: "\u2014", IsTor: false}
			}
			resp = strings.TrimSpace(resp)
			if !strings.HasPrefix(resp, "{") {
				return TestResult{IP: "\u2014", Country: "\u2014", IsTor: false}
			}
			var result struct {
				IP    string `json:"IP"`
				IsTor bool   `json:"IsTor"`
			}
			if err := json.Unmarshal([]byte(resp), &result); err != nil {
				return TestResult{IP: "\u2014", Country: "\u2014", IsTor: false}
			}
			country := lookupCountryViaProxy(result.IP, socks)
			return TestResult{IP: result.IP, Country: country, IsTor: result.IsTor}
		}
	}
	return TestResult{IP: "\u2014", Country: "\u2014", IsTor: false}
}

func lookupCountryViaProxy(ip string, socksPort int) string {
	if ip == "" || ip == "\u2014" {
		return "\u2014"
	}
	resp, err := socks5Request("api.ip2location.io", 443, "/?ip="+ip, socksPort, true, 12)
	if err != nil {
		return "?"
	}
	resp = strings.TrimSpace(resp)
	if !strings.HasPrefix(resp, "{") {
		return "?"
	}
	var data struct {
		CountryName string `json:"country_name"`
	}
	if err := json.Unmarshal([]byte(resp), &data); err != nil {
		return "?"
	}
	if data.CountryName == "" {
		return "?"
	}
	return data.CountryName
}

type SlotTrafficResult struct {
	Download string `json:"download"`
	Upload   string `json:"upload"`
}

func (a *App) GetSlotTrafficStats(label string) SlotTrafficResult {
	slots := a.GetMultiSlots()
	for i, s := range slots {
		if s.Label == label {
			_, httpPort := a.slotPorts(i)
			a.mu()
			t, ok := a.multiTraffic[httpPort]
			a.muUnlock()
			if !ok {
				return SlotTrafficResult{Download: "\u2014", Upload: "\u2014"}
			}
			dl := atomic.LoadInt64(&t.DlBytes)
			ul := atomic.LoadInt64(&t.UlBytes)
			dlPrev := atomic.LoadInt64(&t.DlPrev)
			ulPrev := atomic.LoadInt64(&t.UlPrev)
			atomic.StoreInt64(&t.DlPrev, dl)
			atomic.StoreInt64(&t.UlPrev, ul)
			dlSpeed := float64(dl-dlPrev) / 2.0
			ulSpeed := float64(ul-ulPrev) / 2.0
			return SlotTrafficResult{
				Download: formatSpeed(dlSpeed),
				Upload:   formatSpeed(ulSpeed),
			}
		}
	}
	return SlotTrafficResult{Download: "\u2014", Upload: "\u2014"}
}

func (a *App) GetBestAutoProxySlot() string {
	a.mu()
	defer a.muUnlock()
	if len(a.multiHealthData) == 0 {
		return ""
	}
	bestLabel := ""
	bestScore := float64(999999)
	for lbl, hd := range a.multiHealthData {
		if !hd.Online {
			continue
		}
		variance := 0.0
		if len(hd.History) > 1 {
			sum := 0.0
			for _, v := range hd.History {
				sum += (v - hd.AvgLat) * (v - hd.AvgLat)
			}
			variance = sum / float64(len(hd.History))
		}
		score := hd.AvgLat * (1 + variance/10000.0)
		if score < bestScore {
			bestScore = score
			bestLabel = lbl
		}
	}
	return bestLabel
}

func (a *App) SetProxyToSlot(label string) error {
	a.mu()
	if a.multiProxyLabel == label {
		a.StopHTTPProxy()
		a.unsetSystemProxy()
		a.multiProxyLabel = ""
		a.muUnlock()
		runtime.EventsEmit(a.ctx, "multi:proxy:off", map[string]interface{}{"label": label})
		return nil
	}

	if a.multiProxyStop != nil {
		select {
		case <-a.multiProxyStop:
		default:
			close(a.multiProxyStop)
		}
		a.multiProxyStop = nil
	}
	a.muUnlock()

	slots := a.GetMultiSlots()
	for i, s := range slots {
		if s.Label == label {
			socksPort, httpPort := a.slotPorts(i)

			stopEv := make(chan struct{})
			traffic := &SlotTraffic{}
			a.mu()
			a.multiProxyStop = stopEv
			a.multiProxyLabel = label
			if a.multiTraffic == nil {
				a.multiTraffic = make(map[int]*SlotTraffic)
			}
			a.multiTraffic[httpPort] = traffic
			a.muUnlock()

			go runHTTPProxyServer(stopEv, "127.0.0.1", socksPort, httpPort, traffic)
			a.setSystemProxy(httpPort)

			runtime.EventsEmit(a.ctx, "multi:proxy:on", map[string]interface{}{
				"label": label, "httpPort": httpPort, "socksPort": socksPort,
			})
			return nil
		}
	}
	return fmt.Errorf("slot not found: %s", label)
}

func (a *App) GetProxyStrategy() string {
	a.mu()
	defer a.muUnlock()
	return a.multiBalancerMode
}

func (a *App) SetProxyStrategy(mode string) error {
	a.mu()
	defer a.muUnlock()

	// Stop any existing proxy
	if a.multiBalancerStop != nil {
		select {
		case <-a.multiBalancerStop:
		default:
			close(a.multiBalancerStop)
		}
		a.multiBalancerStop = nil
	}
	if a.multiProxyStop != nil {
		select {
		case <-a.multiProxyStop:
		default:
			close(a.multiProxyStop)
		}
		a.multiProxyStop = nil
	}
	a.unsetSystemProxy()
	a.StopHTTPProxy()

	a.multiBalancerMode = mode
	a.multiProxyLabel = ""

	if mode == "balancer" {
		stopCh := make(chan struct{})
		a.multiBalancerStop = stopCh
		go runBalancerProxy(stopCh, a)
		a.setSystemProxy(9099)

		runtime.EventsEmit(a.ctx, "multi:proxy:balancer:on", map[string]interface{}{})
		return nil
	}

	if mode == "least_ping" {
		runtime.EventsEmit(a.ctx, "multi:proxy:balancer:off", map[string]interface{}{})
		return nil
	}

	return nil
}

func runBalancerProxy(stopCh chan struct{}, app *App) {
	ln, err := net.Listen("tcp", "127.0.0.1:9099")
	if err != nil {
		runtime.EventsEmit(app.ctx, "tor:log", fmt.Sprintf("[Balancer] Failed to start: %v", err))
		return
	}
	defer ln.Close()
	runtime.EventsEmit(app.ctx, "tor:log", "[Balancer] Started on 127.0.0.1:9099")

	for {
		select {
		case <-stopCh:
			runtime.EventsEmit(app.ctx, "tor:log", "[Balancer] Stopped")
			return
		default:
		}
		ln.(*net.TCPListener).SetDeadline(time.Now().Add(1 * time.Second))
		conn, err := ln.Accept()
		if err != nil {
			if netErr, ok := err.(net.Error); ok && netErr.Timeout() {
				continue
			}
			return
		}
		go handleBalancerConn(conn, app)
	}
}

func handleBalancerConn(clientConn net.Conn, app *App) {
	defer clientConn.Close()
	clientConn.SetReadDeadline(time.Now().Add(15 * time.Second))

	buf := make([]byte, 65536)
	n, err := clientConn.Read(buf)
	if err != nil || n == 0 {
		return
	}
	data := buf[:n]

	firstLine := strings.SplitN(string(data), "\r\n", 2)[0]
	parts := strings.SplitN(firstLine, " ", 3)
	if len(parts) < 2 {
		return
	}

	method := parts[0]
	target := parts[1]

	// Get connected slots
	app.mu()
	states := app.multiSlotStates
	slots := app.GetMultiSlots()
	app.muUnlock()

	var connectedSlots []int
	for i, s := range slots {
		if st, ok := states[s.Label]; ok && st.Connected {
			connectedSlots = append(connectedSlots, i)
		}
	}

	if len(connectedSlots) == 0 {
		clientConn.Write([]byte("HTTP/1.1 503 Service Unavailable\r\nConnection: close\r\n\r\n"))
		return
	}

	// Round-robin pick
	idx := atomic.AddInt64(&app.multiBalancerCounter, 1)
	slotIdx := connectedSlots[int(idx)%len(connectedSlots)]
	socksPort, httpPort := app.slotPorts(slotIdx)

	if method == "CONNECT" {
		handleBalancerConnect(clientConn, data, target, "127.0.0.1", socksPort, httpPort, app)
	} else {
		handleBalancerHTTPRequest(clientConn, data, method, target, "127.0.0.1", socksPort, httpPort, app)
	}
}

func handleBalancerConnect(clientConn net.Conn, initialData []byte, target, socksHost string, socksPort int, httpPort int, app *App) {
	host := target
	port := 443
	if h, p, err := net.SplitHostPort(target); err == nil {
		host = h
		port, _ = strconv.Atoi(p)
	}

	torConn, err := net.DialTimeout("tcp", fmt.Sprintf("%s:%d", socksHost, socksPort), 10*time.Second)
	if err != nil {
		clientConn.Write([]byte("HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\n\r\n"))
		return
	}
	defer torConn.Close()

	if err := socks5Handshake(torConn, host, port); err != nil {
		clientConn.Write([]byte("HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\n\r\n"))
		return
	}

	clientConn.Write([]byte("HTTP/1.1 200 Connection established\r\n\r\n"))

	app.mu()
	traffic := app.multiTraffic[httpPort]
	if traffic == nil {
		traffic = &SlotTraffic{}
		app.multiTraffic[httpPort] = traffic
	}
	app.muUnlock()

	relayHTTPData(clientConn, torConn, traffic)
}

func handleBalancerHTTPRequest(clientConn net.Conn, initialData []byte, method, target, socksHost string, socksPort int, httpPort int, app *App) {
	parsed, err := url.Parse(target)
	if err != nil {
		clientConn.Write([]byte("HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n"))
		return
	}
	host := parsed.Hostname()
	port := 80
	if p := parsed.Port(); p != "" {
		port, _ = strconv.Atoi(p)
	}
	path := parsed.Path
	if path == "" {
		path = "/"
	}
	if parsed.RawQuery != "" {
		path += "?" + parsed.RawQuery
	}

	torConn, err := net.DialTimeout("tcp", fmt.Sprintf("%s:%d", socksHost, socksPort), 10*time.Second)
	if err != nil {
		clientConn.Write([]byte("HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\n\r\n"))
		return
	}
	defer torConn.Close()

	if err := socks5Handshake(torConn, host, port); err != nil {
		clientConn.Write([]byte("HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\n\r\n"))
		return
	}

	headerEnd := strings.Index(string(initialData), "\r\n\r\n")
	headerPart := initialData
	if headerEnd != -1 {
		headerPart = initialData[:headerEnd]
	}
	body := []byte{}
	if headerEnd != -1 {
		body = initialData[headerEnd+4:]
	}
	headerLines := strings.Split(string(headerPart), "\r\n")
	headerLines[0] = fmt.Sprintf("%s %s HTTP/1.1", method, path)
	torConn.Write([]byte(strings.Join(headerLines, "\r\n") + "\r\n\r\n"))
	torConn.Write(body)

	app.mu()
	traffic := app.multiTraffic[httpPort]
	if traffic == nil {
		traffic = &SlotTraffic{}
		app.multiTraffic[httpPort] = traffic
	}
	app.muUnlock()

	relayHTTPData(clientConn, torConn, traffic)
}

// ===================== CUSTOM BRIDGES =====================

type PingResult struct {
	Host    string `json:"host"`
	Port    int    `json:"port"`
	OK      bool   `json:"ok"`
	Latency int    `json:"latency"`
	Error   string `json:"error"`
	Line    string `json:"line"`
}

func (a *App) PingBridge(host string, port int, timeout int) PingResult {
	t0 := time.Now()
	conn, err := net.DialTimeout("tcp", fmt.Sprintf("%s:%d", host, port), time.Duration(timeout)*time.Second)
	if err != nil {
		return PingResult{Host: host, Port: port, OK: false, Error: err.Error()}
	}
	conn.Close()
	ms := int(time.Since(t0).Seconds() * 1000)
	return PingResult{Host: host, Port: port, OK: true, Latency: ms}
}

func (a *App) PingCustomBridges(text string) []PingResult {
	lines := strings.Split(text, "\n")
	var validLines []string
	for _, l := range lines {
		l = strings.TrimSpace(l)
		if l != "" && !strings.HasPrefix(l, "#") {
			validLines = append(validLines, l)
		}
	}

	results := make([]PingResult, len(validLines))
	var wg sync.WaitGroup
	sem := make(chan struct{}, 8)

	for i, line := range validLines {
		wg.Add(1)
		sem <- struct{}{}
		go func(idx int, l string) {
			defer wg.Done()
			defer func() { <-sem }()
			host, port := parseBridgeHostPort(l)
			if host == "" {
				results[idx] = PingResult{Line: l, Error: "Could not parse bridge"}
				return
			}
			r := a.PingBridge(host, port, 5)
			r.Line = l
			results[idx] = r
		}(i, line)
	}
	wg.Wait()

	runtime.EventsEmit(a.ctx, "custom:ping:done", true)
	return results
}

func parseBridgeHostPort(line string) (string, int) {
	re4 := regexp.MustCompile(`(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}):(\d+)`)
	if m := re4.FindStringSubmatch(line); m != nil {
		p, _ := strconv.Atoi(m[2])
		return m[1], p
	}
	re6 := regexp.MustCompile(`\[([0-9a-fA-F:]+)\]:(\d+)`)
	if m := re6.FindStringSubmatch(line); m != nil {
		port, _ := strconv.Atoi(m[2])
		return m[1], port
	}
	return "", 0
}

func (a *App) SaveCustomBridges(text string, useCustom bool) error {
	cfg := a.LoadConfig()
	cfg.CustomBridges = text
	cfg.UseCustomBridges = useCustom
	return a.SaveConfig(cfg)
}

func (a *App) GetCustomBridges() map[string]interface{} {
	cfg := a.LoadConfig()
	return map[string]interface{}{
		"text":      cfg.CustomBridges,
		"useCustom": cfg.UseCustomBridges,
	}
}

// ===================== BRIDGE SCANNER =====================

type ScanResult struct {
	BridgeType string `json:"bridgeType"`
	Host       string `json:"host"`
	Port       int    `json:"port"`
	Ping       int    `json:"ping"`
	Status     string `json:"status"`
	FullLine   string `json:"fullLine"`
}

var (
	scanWorking []string
	scanMu      sync.Mutex
	scanStop    bool
)

func (a *App) ScanBridges(cat, trans, ip string, workers, timeout int) {
	scanMu.Lock()
	scanWorking = nil
	scanStop = false
	scanMu.Unlock()

	filename := a.getSafeFilename(cat, trans, ip)
	path := filepath.Join(a.bridgesDir, filename)
	data, err := os.ReadFile(path)
	if err != nil {
		runtime.EventsEmit(a.ctx, "scan:error", fmt.Sprintf("Bridge file not found: %s", filename))
		return
	}

	var lines []string
	for _, l := range strings.Split(string(data), "\n") {
		l = strings.TrimSpace(l)
		if l != "" {
			lines = append(lines, l)
		}
	}
	if len(lines) == 0 {
		runtime.EventsEmit(a.ctx, "scan:error", "Bridge file is empty")
		return
	}

	total := len(lines)
	doneCount := 0
	var doneMu sync.Mutex
	var results []ScanResult
	var resultsMu sync.Mutex

	sem := make(chan struct{}, workers)
	var wg sync.WaitGroup

	for _, line := range lines {
		wg.Add(1)
		sem <- struct{}{}
		go func(l string) {
			defer wg.Done()
			defer func() { <-sem }()

			scanMu.Lock()
			stopped := scanStop
			scanMu.Unlock()
			if stopped {
				return
			}

			host, port := parseBridgeHostPort(l)
			if host == "" {
				doneMu.Lock()
				doneCount++
				doneMu.Unlock()
				return
			}

			t0 := time.Now()
			conn, err := net.DialTimeout("tcp", fmt.Sprintf("%s:%d", host, port), time.Duration(timeout)*time.Second)
			var ms int
			var status string
			if err != nil {
				ms = -1
				status = fmt.Sprintf("✘ %s", truncate(err.Error(), 30))
			} else {
				conn.Close()
				ms = int(time.Since(t0).Seconds() * 1000)
				if ms < 500 {
					status = fmt.Sprintf("✔ %d ms", ms)
				} else {
					status = fmt.Sprintf("✔ %d ms (slow)", ms)
				}
				scanMu.Lock()
				scanWorking = append(scanWorking, l)
				scanMu.Unlock()
			}

			result := ScanResult{
				BridgeType: trans,
				Host:       host,
				Port:       port,
				Ping:       ms,
				Status:     status,
				FullLine:   l,
			}
			resultsMu.Lock()
			results = append(results, result)
			resultsMu.Unlock()

			doneMu.Lock()
			doneCount++
			pct := doneCount * 100 / total
			current := doneCount
			doneMu.Unlock()
			runtime.EventsEmit(a.ctx, "scan:progress", map[string]interface{}{
				"pct":      pct,
				"done":     current,
				"total":    total,
				"result":   result,
			})
		}(line)
	}

	wg.Wait()
	scanMu.Lock()
	ok := len(scanWorking)
	scanMu.Unlock()
	runtime.EventsEmit(a.ctx, "scan:done", map[string]interface{}{
		"reachable":   ok,
		"unreachable": total - ok,
		"total":       total,
	})
}

func (a *App) StopScan() {
	scanMu.Lock()
	scanStop = true
	scanMu.Unlock()
}

func (a *App) GetWorkingBridges() []string {
	scanMu.Lock()
	defer scanMu.Unlock()
	cp := make([]string, len(scanWorking))
	copy(cp, scanWorking)
	return cp
}

func (a *App) GetWorkingBridgesText() string {
	scanMu.Lock()
	defer scanMu.Unlock()
	return strings.Join(scanWorking, "\n")
}

// ===================== DESKTOP NOTIFICATIONS =====================

var (
	notifyShell32    = syscall.NewLazyDLL("shell32.dll")
	notifyUser32     = syscall.NewLazyDLL("user32.dll")
	procShellNotify  = notifyShell32.NewProc("Shell_NotifyIconW")
	procGetModuleH   = syscall.NewLazyDLL("kernel32.dll").NewProc("GetModuleHandleW")
	procRegisterCl   = notifyUser32.NewProc("RegisterClassW")
	procCreateWin    = notifyUser32.NewProc("CreateWindowExW")
	procDestroyWin   = notifyUser32.NewProc("DestroyWindow")
	procLoadIcon     = notifyUser32.NewProc("LoadIconW")
)

type _notifyIconData struct {
	cbSize           uint32
	hWnd             uintptr
	uID              uint32
	uFlags           uint32
	uCallbackMessage uint32
	hIcon            uintptr
	szTip            [128]uint16
	dwState          uint32
	dwStateMask      uint32
	szInfo           [256]uint16
	uTimeout         uint32
	szInfoTitle      [64]uint16
	dwInfoFlags      uint32
}

func (a *App) NotifyDesktop(title, msg string) {
	go a.showNotification(title, msg)
}

func (a *App) showNotification(title, msg string) {
	className, _ := syscall.UTF16PtrFromString("DeltaTorNotif")
	hInst, _, _ := procGetModuleH.Call(0)

	wc := struct {
		style, lpfnWndProc, cbClsExtra, cbWndExtra                    uintptr
		hInstance, hIcon, hCursor, hbrBackground                       uintptr
		lpszMenuName, lpszClassName                                    uintptr
	}{
		style:         0x0003,
		hInstance:     hInst,
		lpszClassName: uintptr(unsafe.Pointer(className)),
	}
	procRegisterCl.Call(uintptr(unsafe.Pointer(&wc)))

	hwnd, _, _ := procCreateWin.Call(
		0, uintptr(unsafe.Pointer(className)), uintptr(unsafe.Pointer(className)),
		0, 0, 0, 0, 0, 0, 0, hInst, 0,
	)
	if hwnd == 0 {
		return
	}

	hIcon, _, _ := procLoadIcon.Call(0, 32512)

	tipStr, _ := syscall.UTF16FromString("Delta Tor")
	nd := _notifyIconData{
		cbSize:  uint32(unsafe.Sizeof(_notifyIconData{})),
		hWnd:    hwnd,
		uID:     1,
		uFlags:  0x02 | 0x04 | 0x10,
		hIcon:   hIcon,
	}
	copy(nd.szTip[:], tipStr)

	titleU16, _ := syscall.UTF16FromString(title)
	for i, c := range titleU16 {
		if i < 63 {
			nd.szInfoTitle[i] = c
		}
	}
	msgU16, _ := syscall.UTF16FromString(msg)
	for i, c := range msgU16 {
		if i < 255 {
			nd.szInfo[i] = c
		}
	}
	nd.dwInfoFlags = 0x01

	procShellNotify.Call(0x00000000, uintptr(unsafe.Pointer(&nd)))
	time.Sleep(4 * time.Second)
	procShellNotify.Call(0x00000002, uintptr(unsafe.Pointer(&nd)))
	procDestroyWin.Call(hwnd)
}

func (a *App) ShowWindow() {
	runtime.WindowShow(a.ctx)
	runtime.WindowCenter(a.ctx)
}

func (a *App) RestartApp() {
	exe, _ := os.Executable()
	a.KillAllProcesses()
	systray.Quit()
	cmd := exec.Command(exe)
	cmd.SysProcAttr = &syscall.SysProcAttr{CreationFlags: 0x00000008}
	cmd.Start()
	os.Exit(0)
}
