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
	"syscall"
	"time"
	"unsafe"

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
	TorSOCKSPort = 9050
	TorCtrlPort  = 9051
	HTTPProxyPort = 9060
)

type App struct {
	ctx         context.Context
	dataDir     string
	configPath  string
	bridgesDir  string
	torProcess  *os.Process
	torMu       sync.Mutex
	connected   bool
	uptimeStart time.Time
	stopCh      chan struct{}
	proxyServer net.Listener
	proxyMu     sync.Mutex
	dlBytes     int64
	ulBytes     int64
	dlPrev      int64
	ulPrev      int64
	trafficMu   sync.Mutex
}

type Config struct {
	AutoConnectTimeout    int    `json:"auto_connect_timeout"`
	BridgesInTorrc        int    `json:"bridges_in_torrc"`
	ShuffleBridges        bool   `json:"shuffle_bridges"`
	DNSOverTor            bool   `json:"dns_over_tor"`
	MaxCircuitDirtiness   int    `json:"max_circuit_dirtiness"`
	NewCircuitPeriod      int    `json:"new_circuit_period"`
	NumEntryGuards        int    `json:"num_entry_guards"`
	KeepAliveEnabled      bool   `json:"keep_alive_enabled"`
	KeepAliveInterval     int    `json:"keep_alive_interval"`
	WatchdogEnabled       bool   `json:"watchdog_enabled"`
	WatchdogInterval      int    `json:"watchdog_interval"`
	ExitNodesEnabled      bool   `json:"exit_nodes_enabled"`
	ExitNodesCountries    string `json:"exit_nodes_countries"`
	StrictExitNodes       bool   `json:"strict_exit_nodes"`
	AutoProxyOnConnect    bool   `json:"auto_proxy_on_connect"`
	SNIEnabled            bool   `json:"sni_enabled"`
	SNIHost               string `json:"sni_host"`
	LastSuccessCat        string `json:"last_success_cat"`
	LastSuccessTrans      string `json:"last_success_trans"`
	LastSuccessIP         string `json:"last_success_ip"`
	ExtractDir            string `json:"extract_dir,omitempty"`
}

var defaultConfig = Config{
	AutoConnectTimeout:  180,
	BridgesInTorrc:      100,
	ShuffleBridges:      true,
	MaxCircuitDirtiness: 1800,
	NewCircuitPeriod:    10,
	NumEntryGuards:      15,
	KeepAliveEnabled:    true,
	KeepAliveInterval:   120,
	WatchdogEnabled:     true,
	WatchdogInterval:    30,
	ExitNodesCountries:  "{nl},{de},{fr},{ch},{at},{se},{no},{fi},{is}",
	SNIHost:             "www.google.com",
	ExtractDir:          "",
}

func NewApp() *App {
	return &App{}
}

func (a *App) startup(ctx context.Context) {
	a.ctx = ctx
	a.dataDir = a.resolveDataDir()
	a.configPath = filepath.Join(a.dataDir, "tor_client_config.json")
	a.bridgesDir = filepath.Join(a.dataDir, "bridges")
	os.MkdirAll(a.bridgesDir, 0755)
	os.MkdirAll(filepath.Join(a.dataDir, "data"), 0755)
	runtime.LogInfo(ctx, fmt.Sprintf("Data directory: %s", a.dataDir))
}

func (a *App) resolveDataDir() string {
	appdata := os.Getenv("LOCALAPPDATA")
	if appdata == "" {
		home, _ := os.UserHomeDir()
		appdata = filepath.Join(home, "AppData", "Local")
	}
	ptrFile := filepath.Join(appdata, "DeltaTor", "datadir.txt")
	if data, err := os.ReadFile(ptrFile); err == nil {
		path := strings.TrimSpace(string(data))
		if path != "" {
			os.MkdirAll(path, 0755)
			return path
		}
	}
	defaultDir := filepath.Join(appdata, "DeltaTor")
	os.MkdirAll(defaultDir, 0755)
	os.WriteFile(ptrFile, []byte(defaultDir), 0644)
	return defaultDir
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
			info, _ := e.Info()
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
	safe := strings.ReplaceAll(cat, " ", "_")
	safe = strings.ReplaceAll(safe, "&", "and")
	safe = strings.ReplaceAll(safe, "(", "")
	safe = strings.ReplaceAll(safe, ")", "")
	return fmt.Sprintf("%s_%s_%s.txt", safe, trans, ip)
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

func (a *App) GenerateTorrc(cat, trans, ip, source string) string {
	dataDir := filepath.Join(a.dataDir, "data")
	os.MkdirAll(dataDir, 0755)

	geoipFile := filepath.Join(dataDir, "geoip")
	geoip6File := filepath.Join(dataDir, "geoip6")
	if _, err := os.Stat(geoipFile); os.IsNotExist(err) {
		srcGeoip := filepath.Join(a.dataDir, "geoip")
		if _, err := os.Stat(srcGeoip); err == nil {
			copyFile(srcGeoip, geoipFile)
		}
	}
	if _, err := os.Stat(geoip6File); os.IsNotExist(err) {
		srcGeoip6 := filepath.Join(a.dataDir, "geoip6")
		if _, err := os.Stat(srcGeoip6); err == nil {
			copyFile(srcGeoip6, geoip6File)
		}
	}

	cfg := a.LoadConfig()

	var bridgeLines []string
	useBridges := "0"
	if source != "direct" {
		bridgeLines = a.GetBridgeLines(cat, trans, ip)
		if len(bridgeLines) > 0 {
			useBridges = "1"
		}
	}

	var sb strings.Builder
	sb.WriteString("Log notice stdout\n")
	sb.WriteString(fmt.Sprintf("DataDirectory %s\n", dataDir))
	sb.WriteString(fmt.Sprintf("GeoIPFile %s\n", geoipFile))
	sb.WriteString(fmt.Sprintf("GeoIPv6File %s\n", geoip6File))
	sb.WriteString(fmt.Sprintf("SOCKSPort 127.0.0.1:%d\n", TorSOCKSPort))
	sb.WriteString(fmt.Sprintf("ControlPort 127.0.0.1:%d\n", TorCtrlPort))
	sb.WriteString("CookieAuthentication 1\n")
	sb.WriteString("DormantClientTimeout 24 hours\n")
	sb.WriteString("DormantOnFirstStartup 0\n")
	sb.WriteString("DormantCanceledByStartup 1\n")
	sb.WriteString(fmt.Sprintf("UseBridges %s\n", useBridges))
	sb.WriteString(fmt.Sprintf("MaxCircuitDirtiness %d\n", cfg.MaxCircuitDirtiness))
	sb.WriteString(fmt.Sprintf("NewCircuitPeriod %d\n", cfg.NewCircuitPeriod))
	sb.WriteString(fmt.Sprintf("NumEntryGuards %d\n", cfg.NumEntryGuards))
	sb.WriteString("AllowNonRFC953Hostnames 1\n")
	sb.WriteString("EnforceDistinctSubnets 0\n")
	sb.WriteString("MaxClientCircuitsPending 64\n")
	sb.WriteString("CircuitBuildTimeout 30\n")
	sb.WriteString("LearnCircuitBuildTimeout 0\n")
	sb.WriteString("GuardLifetime 90 days\n")
	sb.WriteString("NumDirectoryGuards 6\n")
	sb.WriteString("TokenBucketRefillInterval 10 msec\n")

	if cfg.DNSOverTor {
		sb.WriteString("DNSPort 127.0.0.1:9053\n")
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

	torDir := filepath.Join(a.dataDir, "tor")
	ptDir := filepath.Join(torDir, "pluggable_transports")
	lyrebird := filepath.Join(ptDir, "lyrebird.exe")
	conjure := filepath.Join(ptDir, "conjure-client.exe")

	sb.WriteString(fmt.Sprintf("ClientTransportPlugin meek_lite,obfs2,obfs3,obfs4,scramblesuit,webtunnel exec %s\n", lyrebird))
	sb.WriteString(fmt.Sprintf("ClientTransportPlugin snowflake exec %s\n", lyrebird))
	sb.WriteString(fmt.Sprintf("ClientTransportPlugin conjure exec %s -registerURL \"https://registration.refraction.network/api\"\n", conjure))

	sb.WriteString("\n")

	if useBridges == "1" {
		for _, line := range bridgeLines {
			sb.WriteString(fmt.Sprintf("Bridge %s\n", line))
		}
	}

	torrcPath := filepath.Join(torDir, "torrc")
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

	a.stopCh = make(chan struct{})
	a.connected = false
	a.uptimeStart = time.Now()

	runtime.LogInfo(a.ctx, fmt.Sprintf("Starting Tor with config: %s", torrc))

	cmd := exec.Command(torExe, "-f", torrc)
	cmd.SysProcAttr = &syscall.SysProcAttr{CreationFlags: 0x08000000}
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		return fmt.Errorf("failed to create stdout pipe: %v", err)
	}
	cmd.Stderr = cmd.Stdout

	if err := cmd.Start(); err != nil {
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
		select {
		case <-a.stopCh:
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

			if pct == 100 && !a.connected {
				a.connected = true
				a.uptimeStart = time.Now()
				a.ResetTrafficStats()
				runtime.EventsEmit(a.ctx, "tor:connected", true)
				runtime.LogInfo(a.ctx, "Tor fully connected!")
			}
		}
	}

	cmd.Wait()
	a.torMu.Lock()
	a.torProcess = nil
	a.torMu.Unlock()

	if a.connected {
		a.connected = false
		a.StopHTTPProxy()
		a.unsetSystemProxy()
		runtime.EventsEmit(a.ctx, "tor:disconnected", true)
	}
}

func (a *App) StopTor() error {
	a.stopTor()
	return nil
}

func (a *App) stopTor() {
	if a.stopCh != nil {
		close(a.stopCh)
		a.stopCh = nil
	}
	a.torMu.Lock()
	if a.torProcess != nil {
		a.torProcess.Signal(os.Interrupt)
		time.Sleep(2 * time.Second)
		if a.torProcess != nil {
			a.torProcess.Kill()
		}
		a.torProcess = nil
	}
	a.connected = false
	a.torMu.Unlock()

	a.StopHTTPProxy()
	a.unsetSystemProxy()
	runtime.EventsEmit(a.ctx, "tor:stopped", true)
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
	if !a.connected {
		return "—"
	}
	d := time.Since(a.uptimeStart)
	h := int(d.Hours())
	m := int(d.Minutes()) % 60
	s := int(d.Seconds()) % 60
	return fmt.Sprintf("%02d:%02d:%02d", h, m, s)
}

type TestResult struct {
	IP      string `json:"ip"`
	Country string `json:"country"`
	IsTor   bool   `json:"isTor"`
}

func (a *App) TestConnection() TestResult {
	runtime.EventsEmit(a.ctx, "tor:log", "[Test] Checking connection...")

	if !a.connected {
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
	conn.SetDeadline(time.Now().Add(time.Duration(timeout) * time.Second))

	// SOCKS5 handshake
	conn.Write([]byte{0x05, 0x01, 0x00})
	handshakeResp := make([]byte, 2)
	if _, err := io.ReadFull(conn, handshakeResp); err != nil {
		conn.Close()
		return "", fmt.Errorf("socks5 handshake read: %v", err)
	}
	if handshakeResp[1] != 0x00 {
		conn.Close()
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
		conn.Close()
		return "", fmt.Errorf("socks5 connect read: %v", err)
	}
	if connectResp[1] != 0x00 {
		conn.Close()
		return "", fmt.Errorf("socks5 connect error: %d", connectResp[1])
	}

	if useSSL {
		tlsConn := tls.Client(conn, &tls.Config{ServerName: host})
		if err := tlsConn.Handshake(); err != nil {
			conn.Close()
			return "", fmt.Errorf("TLS handshake: %v", err)
		}
		defer tlsConn.Close()
		tlsConn.Write([]byte(fmt.Sprintf("GET %s HTTP/1.1\r\nHost: %s\r\nConnection: close\r\nUser-Agent: Mozilla/5.0\r\n\r\n", path, host)))
		var result strings.Builder
		buf := make([]byte, 4096)
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
	buf := make([]byte, 4096)
	for {
		n, err := conn.Read(buf)
		if n > 0 {
			result.Write(buf[:n])
		}
		if err != nil {
			break
		}
	}
	conn.Close()
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
	if json.Unmarshal([]byte(resp), &data); err != nil {
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
	req, _ := http.NewRequest("GET", url, nil)
	req.Header.Set("User-Agent", "Mozilla/5.0")
	resp, err := client.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	f, err := os.Create(dest)
	if err != nil {
		return err
	}
	defer f.Close()
	_, err = io.Copy(f, resp.Body)
	return err
}

func (a *App) DownloadAllBridges() {
	runtime.EventsEmit(a.ctx, "tor:log", "[Bridges] Starting bridge download...")
	os.MkdirAll(a.bridgesDir, 0755)

	total := len(bridgeData)
	done := 0

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

			done++
			pct := done * 100 / total
			runtime.EventsEmit(a.ctx, "tor:log", fmt.Sprintf("[Bridges] Downloaded %s %s %s (%d/%d)", cat, trans, ip, done, total))
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
	}
}

func (a *App) handleHTTPConnect(clientConn net.Conn, initialData []byte) {
	lines := strings.Split(string(initialData), "\r\n")
	if len(lines) == 0 {
		return
	}

	firstLine := strings.SplitN(lines[0], " ", 3)
	if len(firstLine) < 3 {
		return
	}

	target := firstLine[1]
	host := target
	port := 443
	if strings.Contains(target, ":") {
		parts := strings.SplitN(target, ":", 2)
		host = parts[0]
		port, _ = strconv.Atoi(parts[1])
	}

	// Connect to Tor SOCKS
	torConn, err := net.DialTimeout("tcp", "127.0.0.1:"+strconv.Itoa(TorSOCKSPort), 10*time.Second)
	if err != nil {
		runtime.EventsEmit(a.ctx, "tor:log", fmt.Sprintf("[HTTP Proxy] SOCKS connect failed: %v", err))
		return
	}

	// SOCKS5 handshake with Tor
	torConn.Write([]byte{0x05, 0x01, 0x00})
	torResp := make([]byte, 2)
	torConn.Read(torResp)

	// SOCKS5 CONNECT to target
	hostBytes := []byte(host)
	connReq := make([]byte, 0, 7+len(hostBytes))
	connReq = append(connReq, 0x05, 0x01, 0x00, 0x03, byte(len(hostBytes)))
	connReq = append(connReq, hostBytes...)
	connReq = append(connReq, byte(port>>8), byte(port&0xff))
	torConn.Write(connReq)

	torResp10 := make([]byte, 10)
	torConn.Read(torResp10)

	// Send 200 to client
	clientConn.Write([]byte("HTTP/1.1 200 Connection established\r\n\r\n"))

	// Relay data
	a.relayData(clientConn, torConn)
}

func (a *App) handleHTTPRequest(clientConn net.Conn, initialData []byte) {
	lines := strings.Split(string(initialData), "\r\n")
	if len(lines) == 0 {
		return
	}

	firstLine := strings.SplitN(lines[0], " ", 3)
	if len(firstLine) < 3 {
		return
	}

	method := firstLine[0]
	target := firstLine[1]

	parsed, err := url.Parse(target)
	if err != nil {
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

	// Connect to Tor SOCKS
	torConn, err := net.DialTimeout("tcp", "127.0.0.1:"+strconv.Itoa(TorSOCKSPort), 10*time.Second)
	if err != nil {
		return
	}

	// SOCKS5 handshake
	torConn.Write([]byte{0x05, 0x01, 0x00})
	torResp := make([]byte, 2)
	torConn.Read(torResp)

	// SOCKS5 CONNECT
	hostBytes := []byte(host)
	connReq := make([]byte, 0, 7+len(hostBytes))
	connReq = append(connReq, 0x05, 0x01, 0x00, 0x03, byte(len(hostBytes)))
	connReq = append(connReq, hostBytes...)
	connReq = append(connReq, byte(port>>8), byte(port&0xff))
	torConn.Write(connReq)

	torResp10 := make([]byte, 10)
	torConn.Read(torResp10)

	// Rewrite request line and forward
	headerEnd := strings.Index(string(initialData), "\r\n\r\n")
	body := []byte{}
	if headerEnd != -1 {
		body = initialData[headerEnd+4:]
	}

	lines[0] = fmt.Sprintf("%s %s HTTP/1.1", method, path)
	newHeaders := strings.Join(lines, "\r\n") + "\r\n\r\n"

	torConn.Write([]byte(newHeaders))
	torConn.Write(body)

	// Relay response
	a.relayData(clientConn, torConn)
}

func (a *App) relayData(clientConn net.Conn, torConn net.Conn) {
	var wg sync.WaitGroup
	wg.Add(2)

	go func() {
		defer wg.Done()
		buf := make([]byte, 65536)
		for {
			n, err := torConn.Read(buf)
			if n > 0 {
				a.trafficMu.Lock()
				a.dlBytes += int64(n)
				a.trafficMu.Unlock()
				clientConn.Write(buf[:n])
			}
			if err != nil {
				return
			}
		}
	}()

	go func() {
		defer wg.Done()
		buf := make([]byte, 65536)
		for {
			n, err := clientConn.Read(buf)
			if n > 0 {
				a.trafficMu.Lock()
				a.ulBytes += int64(n)
				a.trafficMu.Unlock()
				torConn.Write(buf[:n])
			}
			if err != nil {
				return
			}
		}
	}()

	wg.Wait()
}

func (a *App) GetTrafficStats() map[string]string {
	a.trafficMu.Lock()
	dl := a.dlBytes
	ul := a.ulBytes
	dlPrev := a.dlPrev
	ulPrev := a.ulPrev
	a.dlPrev = dl
	a.ulPrev = ul
	a.trafficMu.Unlock()

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
	a.trafficMu.Lock()
	a.dlBytes = 0
	a.ulBytes = 0
	a.trafficMu.Unlock()
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
	if !a.connected {
		return nil
	}

	downloadSpeed := testDownloadSpeed(a)
	uploadSpeed := testUploadSpeed(a)

	return &SpeedResult{
		Download: downloadSpeed,
		Upload:   uploadSpeed,
	}
}

func testDownloadSpeed(a *App) string {
	start := time.Now()
	totalBytes := 0
	host := "check.torproject.org"

	conn, err := net.DialTimeout("tcp", "127.0.0.1:"+strconv.Itoa(TorSOCKSPort), 5*time.Second)
	if err != nil {
		return "\u2014"
	}
	defer conn.Close()

	conn.Write([]byte{0x05, 0x01, 0x00})
	handshakeResp := make([]byte, 2)
	if _, err := io.ReadFull(conn, handshakeResp); err != nil {
		return "\u2014"
	}

	hostBytes := []byte(host)
	req := make([]byte, 0, 7+len(hostBytes))
	req = append(req, 0x05, 0x01, 0x00, 0x03, byte(len(hostBytes)))
	req = append(req, hostBytes...)
	req = append(req, byte(443>>8), byte(443&0xff))
	conn.Write(req)

	connectResp := make([]byte, 10)
	if _, err := io.ReadFull(conn, connectResp); err != nil {
		return "\u2014"
	}

	tlsConn := tls.Client(conn, &tls.Config{ServerName: host})
	if err := tlsConn.Handshake(); err != nil {
		return "\u2014"
	}
	defer tlsConn.Close()

	tlsConn.Write([]byte("GET /api/ip HTTP/1.1\r\nHost: " + host + "\r\nConnection: close\r\n\r\n"))
	buf := make([]byte, 8192)
	for {
		n, err := tlsConn.Read(buf)
		if n > 0 {
			totalBytes += n
		}
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
	totalBytes := 0
	host := "httpbin.org"

	conn, err := net.DialTimeout("tcp", "127.0.0.1:"+strconv.Itoa(TorSOCKSPort), 5*time.Second)
	if err != nil {
		return "\u2014"
	}
	defer conn.Close()

	conn.Write([]byte{0x05, 0x01, 0x00})
	handshakeResp := make([]byte, 2)
	if _, err := io.ReadFull(conn, handshakeResp); err != nil {
		return "\u2014"
	}

	hostBytes := []byte(host)
	req := make([]byte, 0, 7+len(hostBytes))
	req = append(req, 0x05, 0x01, 0x00, 0x03, byte(len(hostBytes)))
	req = append(req, hostBytes...)
	req = append(req, byte(443>>8), byte(443&0xff))
	conn.Write(req)

	connectResp := make([]byte, 10)
	if _, err := io.ReadFull(conn, connectResp); err != nil {
		return "\u2014"
	}

	tlsConn := tls.Client(conn, &tls.Config{ServerName: host})
	if err := tlsConn.Handshake(); err != nil {
		return "\u2014"
	}
	defer tlsConn.Close()

	// Upload 10KB of data
	postData := strings.Repeat("X", 10240)
	tlsConn.Write([]byte("POST /post HTTP/1.1\r\nHost: " + host + "\r\nContent-Length: " + strconv.Itoa(len(postData)) + "\r\nConnection: close\r\n\r\n" + postData))

	buf := make([]byte, 8192)
	for {
		n, err := tlsConn.Read(buf)
		if n > 0 {
			totalBytes += n
		}
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
