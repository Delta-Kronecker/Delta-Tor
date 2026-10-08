using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using DeltaTor.Core.Util;

namespace DeltaTor.Core;

/// <summary>
/// Standalone single-Tor launcher with Snowflake support (the counterpart of
/// Android SnowflakeBridge). The race path (<see cref="ParallelTorManager"/> /
/// <see cref="TorRunner"/>) is the primary way this app connects; this class
/// exists for parity so a proxy/manual start can run one Tor with one chosen
/// bridge configuration.
///
/// Windows note: there is no separate snowflake binary and no Go library. The
/// official Tor Expert Bundle's lyrebird serves the snowflake transport, so
/// every mode (built-in zero-config, SNOWFLAKE_AMP, SMART, custom lines) runs
/// through one managed-lyrebird process; the zero-config modes are expressed
/// as a synthetic bridge line carrying the same broker/front/STUN/uTLS
/// constants the Android build bakes into its Go PT.
///
/// Port map (Android kept a dedicated listener):
/// - torSocksPort: Tor SOCKS5 (TorSocksBridge chains to this)
/// - snowflake: lyrebird picks its own SOCKS5 port, reported via CMETHOD
/// </summary>
public static partial class SnowflakeBridge
{
    private const string TAG = "SnowflakeBridge";

    // CDN77 broker (matches latest Tor Browser defaults, optimized for Iran)
    // www.phpmyadmin.net removed — CDN77's TLS cert doesn't cover it
    private const string BrokerUrl = "https://1098762253.rsc.cdn77.org/";
    private const string FrontDomains = "www.cdn77.com";

    // Diverse non-Google STUN servers (Google STUN blocked in Iran).
    // Includes port 443 and 10000 variants (harder to block than 3478).
    private const string StunUrls =
        "stun:stun.antisip.com:3478," +
        "stun:stun.epygi.com:3478," +
        "stun:stun.uls.co.za:3478," +
        "stun:stun.voipgate.com:3478," +
        "stun:stun.mixvoip.com:3478," +
        "stun:stun.nextcloud.com:3478," +
        "stun:stun.bethesda.net:3478," +
        "stun:stun.nextcloud.com:443," +
        "stun:stun.sipgate.net:3478," +
        "stun:stun.sipgate.net:10000," +
        "stun:stun.sonetel.com:3478," +
        "stun:stun.voipia.net:3478," +
        "stun:stun.ucsb.edu:3478," +
        "stun:stun.schlund.de:3478";

    // Randomized TLS fingerprint to evade DPI
    private const string UtlsClientId = "hellorandomizedalpn";
    private const string BridgeFingerprint = "2B280B23E1107BB62ABFC40DDCC8824814F80A72";

    // AMP cache rendezvous config (for Snowflake AMP mode)
    private const string AmpBrokerUrl = "https://snowflake-broker.torproject.net/";
    private const string AmpFrontDomain = "www.google.com";
    private const string AmpCacheUrl = "https://cdn.ampproject.org/";

    private static volatile Process? _lyrebirdProcess;
    private static volatile Process? _torProcess;

    private static readonly ConcurrentDictionary<string, string> LyrebirdCmethods =
        new(StringComparer.Ordinal);

    public static volatile bool IsTorReady;
    public static volatile int TorBootstrapProgress;

    /// <summary>
    /// Start the Tor process with the appropriate pluggable transport.
    ///
    /// Transport is auto-detected from bridge lines:
    /// - Empty bridgeLines / SNOWFLAKE_AMP / SMART → built-in Snowflake
    ///   (zero-config) served by lyrebird with a synthetic bridge line
    /// - Lines starting with "obfs4", "webtunnel", "meek_lite", "snowflake"
    ///   → managed lyrebird
    /// - "DIRECT" → no bridges, no transport plugins
    ///
    /// <paramref name="upstreamSocksAddr"/> is an optional "host:port" SOCKS5
    /// proxy for all outbound connections (Tor's own + lyrebird PTs, which
    /// receives it as TOR_PT_PROXY — including snowflake, unlike Android's Go
    /// PT). Set when this layer is chained behind a DoH/SOCKS5 layer so bridge
    /// contact rides that layer instead of going direct.
    /// </summary>
    /// <returns>null on success, the failure otherwise (Android: Result).</returns>
    public static Exception? StartClient(
        int torSocksPort,
        string listenHost = "127.0.0.1",
        string bridgeLines = "",
        string? upstreamSocksAddr = null)
    {
        var trimmed = bridgeLines.Trim();
        var isDirect = trimmed == "DIRECT";
        var isAmp = trimmed == "SNOWFLAKE_AMP";
        var isSmart = trimmed == "SMART";

        // Use built-in Snowflake for: empty lines, AMP mode, or SMART fallback
        var useSnowflakePt = string.IsNullOrWhiteSpace(bridgeLines) || isAmp || isSmart;
        var detectedTransport = isDirect
            ? "direct"
            : useSnowflakePt
                ? "snowflake"
                : DetectTransport(bridgeLines);

        AppLog.I(TAG, "========================================");
        AppLog.I(TAG, $"Starting Tor with {detectedTransport} transport");
        if (useSnowflakePt)
        {
            AppLog.I(TAG, "  Snowflake PT: lyrebird (managed, port from CMETHOD)");
            if (isAmp) AppLog.I(TAG, "  AMP cache rendezvous enabled");
        }
        if (isDirect) AppLog.I(TAG, "  Direct connection (no bridges)");
        AppLog.I(TAG, $"  Tor SOCKS5: {listenHost}:{torSocksPort}");
        if (upstreamSocksAddr != null)
            AppLog.I(TAG, $"  Upstream SOCKS5: {upstreamSocksAddr}");
        AppLog.I(TAG, "========================================");

        StopClient();
        IsTorReady = false;
        TorBootstrapProgress = 0;

        if (!WaitForPortAvailable(torSocksPort))
            return new Exception($"Port {torSocksPort} is in use");

        try
        {
            // Determine which lyrebird transports are needed. Unlike Android,
            // snowflake comes from lyrebird too, so it is collected here.
            var lyrebirdTransports = new List<string>();
            if (!isDirect)
            {
                if (useSnowflakePt)
                {
                    lyrebirdTransports.Add("snowflake");
                }
                else
                {
                    foreach (var raw in ParallelTorManager.SplitLines(bridgeLines))
                    {
                        if (string.IsNullOrWhiteSpace(raw)) continue;
                        var line = CleanBridgeLine(raw);
                        var transport = FirstToken(line);
                        if (transport == null) continue;
                        if (transport is "obfs4" or "webtunnel" or "meek_lite" or "snowflake" &&
                            !lyrebirdTransports.Contains(transport))
                        {
                            lyrebirdTransports.Add(transport);
                        }
                    }
                }
            }

            // Map of transport name -> SOCKS5 address (filled by PT startup)
            var lyrebirdMethods = new Dictionary<string, string>(StringComparer.Ordinal);

            if (lyrebirdTransports.Count > 0)
            {
                if (!TorBinaries.LyrebirdAvailable)
                {
                    return new Exception(
                        "obfs4proxy (lyrebird) binary not found. " +
                        "It ships with the Tor Expert Bundle (tor/pluggable_transports/lyrebird.exe).");
                }
                var ptInfo = new FileInfo(TorBinaries.LyrebirdExe);
                AppLog.I(TAG, $"PT binary: {TorBinaries.LyrebirdExe} (size={ptInfo.Length})");

                var lyrebirdFailure = StartLyrebird(TorBinaries.LyrebirdExe, lyrebirdTransports, upstreamSocksAddr);
                if (lyrebirdFailure != null) return lyrebirdFailure;
                foreach (var kv in LyrebirdCmethods) lyrebirdMethods[kv.Key] = kv.Value;
                AppLog.I(TAG,
                    $"Lyrebird PT started with transports: {{{string.Join(", ", lyrebirdMethods.Select(kv => $"{kv.Key}={kv.Value}"))}}}");

                if (useSnowflakePt && lyrebirdMethods.TryGetValue("snowflake", out var sfAddr))
                {
                    var idx = sfAddr.LastIndexOf(':');
                    if (idx > 0 && int.TryParse(sfAddr[(idx + 1)..], out var sfPort) &&
                        !VerifyTcpListening(sfAddr[..idx], sfPort))
                    {
                        AppLog.W(TAG, "Snowflake PT not listening, but client reports running");
                    }
                    AppLog.I(TAG, $"Snowflake PT started on {sfAddr}");
                }
            }

            // Setup Tor data directory and config
            var torDataDir = AppPaths.TorDataDir("snowflake");
            Directory.CreateDirectory(Path.Combine(torDataDir, "pt_state"));

            // Clear guard state and lock (not descriptor caches — those help Tor
            // reconnect faster if the bridge connection drops mid-bootstrap)
            foreach (var name in new[] { "state", "lock" })
            {
                var file = Path.Combine(torDataDir, name);
                if (File.Exists(file))
                {
                    File.Delete(file);
                    AppLog.D(TAG, $"Cleared Tor state: {name}");
                }
            }

            // GeoIP: Windows references the official databases inside the
            // bundle (Android extracted geoip/geoip6 assets per run).
            var torrcPath = WriteTorrc(
                torDataDir,
                listenHost,
                torSocksPort,
                bridgeLines,
                lyrebirdMethods,
                upstreamSocksAddr);

            // Start Tor process
            var torBinary = TorBinaries.TorExe;
            if (!File.Exists(torBinary))
            {
                StopLyrebird();
                return new Exception($"Tor binary not found at {torBinary}");
            }

            var psi = new ProcessStartInfo
            {
                FileName = torBinary,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = torDataDir
            };
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(torrcPath);
            psi.Environment["HOME"] = torDataDir;

            var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null");
            _torProcess = process;

            // Monitor Tor output for bootstrap progress
            var reader = new Thread(() =>
            {
                try
                {
                    string? line;
                    while ((line = process.StandardOutput.ReadLine()) != null)
                    {
                        AppLog.D(TAG, $"Tor: {line}");
                        var match = BootstrapRegex().Match(line);
                        if (match.Success)
                        {
                            TorBootstrapProgress = int.Parse(match.Groups[1].Value);
                            AppLog.I(TAG, $"Tor bootstrap: {TorBootstrapProgress}%");
                            if (TorBootstrapProgress >= 100) IsTorReady = true;
                        }
                    }
                }
                catch (Exception e)
                {
                    if (_torProcess != null)
                        AppLog.W(TAG, $"Tor output reader error: {e.Message}");
                }
            });
            reader.IsBackground = true;
            reader.Name = "tor-output-reader";
            reader.Start();

            AppLog.I(TAG, "Tor process started, waiting for bootstrap...");
            return null;
        }
        catch (Exception e)
        {
            AppLog.E(TAG, $"Failed to start Tor with {detectedTransport}", e);
            StopClient();
            return e;
        }
    }

    // --- Lyrebird managed transport ---

    /// <summary>
    /// Launch lyrebird as a managed transport process.
    /// Sets up the PT protocol environment, launches the binary, and parses
    /// CMETHOD lines to discover which SOCKS5 ports it's listening on.
    /// </summary>
    private static Exception? StartLyrebird(
        string ptBinaryPath,
        IReadOnlyList<string> transports,
        string? upstreamSocksAddr)
    {
        LyrebirdCmethods.Clear();

        var ptStateDir = Path.Combine(AppPaths.TorDataDir("snowflake"), "pt_state");
        Directory.CreateDirectory(ptStateDir);

        var transportList = string.Join(",", transports);
        AppLog.I(TAG, $"Launching lyrebird for transports: {transportList}");

        var psi = new ProcessStartInfo
        {
            FileName = ptBinaryPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        psi.Environment["TOR_PT_MANAGED_TRANSPORT_VER"] = "1";
        psi.Environment["TOR_PT_CLIENT_TRANSPORTS"] = transportList;
        psi.Environment["TOR_PT_STATE_LOCATION"] = ptStateDir + Path.DirectorySeparatorChar;
        // Exit when stdin is closed (parent dies)
        psi.Environment["TOR_PT_EXIT_ON_STDIN_CLOSE"] = "1";
        // PT spec: TOR_PT_PROXY tells the transport to relay its outbound
        // traffic through the given SOCKS5. Lyrebird honors this for
        // meek_lite/obfs4/webtunnel/snowflake, so a DoH layer providing SOCKS5
        // can carry bridge/CDN connections when direct contact is blocked.
        if (upstreamSocksAddr != null)
        {
            psi.Environment["TOR_PT_PROXY"] = $"socks5://{upstreamSocksAddr}";
            AppLog.I(TAG, $"Lyrebird: TOR_PT_PROXY=socks5://{upstreamSocksAddr}");
        }

        Process process;
        try
        {
            process = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Exception e)
        {
            AppLog.E(TAG, $"Failed to launch lyrebird: {e.Message}");
            return new Exception($"Failed to launch obfs4proxy (lyrebird): {e.Message}");
        }
        _lyrebirdProcess = process;

        // Read stderr in background for diagnostics
        var stderrThread = new Thread(() =>
        {
            try
            {
                string? line;
                while ((line = process.StandardError.ReadLine()) != null)
                    AppLog.D(TAG, $"Lyrebird stderr: {line}");
            }
            catch { /* process ended */ }
        });
        stderrThread.IsBackground = true;
        stderrThread.Name = "lyrebird-stderr";
        stderrThread.Start();

        // Parse stdout for PT protocol messages (CMETHOD, VERSION, etc.)
        // Deliberately not disposed: the stdout reader thread may still call
        // Set() long after this method returns (when the process finally
        // closes its pipe), and a disposed event would crash that thread.
        var cmethodsDone = new ManualResetEventSlim(initialState: false);
        var protocolError = new string?[1];

        var stdoutThread = new Thread(() =>
        {
            try
            {
                string? raw;
                while ((raw = process.StandardOutput.ReadLine()) != null)
                {
                    var l = raw.Trim();
                    AppLog.D(TAG, $"Lyrebird PT: {l}");
                    if (l.StartsWith("VERSION ", StringComparison.Ordinal))
                    {
                        AppLog.I(TAG, $"Lyrebird protocol: {l}");
                    }
                    else if (l.StartsWith("CMETHOD ", StringComparison.Ordinal))
                    {
                        // Format: CMETHOD <transport> socks5 <host:port>
                        var parts = WhitespaceRegex().Split(l);
                        if (parts.Length >= 4)
                        {
                            var name = parts[1];
                            var addr = parts[3];
                            LyrebirdCmethods[name] = addr;
                            AppLog.I(TAG, $"Lyrebird registered: {name} at {addr}");
                        }
                    }
                    else if (l.StartsWith("CMETHOD-ERROR ", StringComparison.Ordinal))
                    {
                        AppLog.E(TAG, $"Lyrebird transport error: {l}");
                    }
                    else if (l == "CMETHODS DONE")
                    {
                        AppLog.I(TAG, "Lyrebird: all transports registered");
                        cmethodsDone.Set();
                    }
                    else if (l.StartsWith("ENV-ERROR ", StringComparison.Ordinal))
                    {
                        protocolError[0] = l;
                        AppLog.E(TAG, $"Lyrebird env error: {l}");
                        cmethodsDone.Set();
                    }
                    else if (l.StartsWith("VERSION-ERROR ", StringComparison.Ordinal))
                    {
                        protocolError[0] = l;
                        AppLog.E(TAG, $"Lyrebird version error: {l}");
                        cmethodsDone.Set();
                    }
                }
            }
            catch (Exception e)
            {
                if (_lyrebirdProcess != null)
                    AppLog.W(TAG, $"Lyrebird stdout reader error: {e.Message}");
            }
            // If the process exits without CMETHODS DONE, unblock the wait
            cmethodsDone.Set();
        });
        stdoutThread.IsBackground = true;
        stdoutThread.Name = "lyrebird-stdout";
        stdoutThread.Start();

        // Wait for CMETHODS DONE (up to 10 seconds)
        if (!cmethodsDone.Wait(TimeSpan.FromSeconds(10)))
        {
            AppLog.E(TAG, "Lyrebird timed out waiting for CMETHODS DONE");
            StopLyrebird();
            return new Exception("obfs4proxy (lyrebird) timed out during PT protocol setup");
        }

        if (protocolError[0] != null)
        {
            StopLyrebird();
            return new Exception($"obfs4proxy (lyrebird) protocol error: {protocolError[0]}");
        }

        if (process.HasExited)
        {
            var exitCode = process.ExitCode;
            AppLog.E(TAG, $"Lyrebird exited prematurely with code {exitCode}");
            _lyrebirdProcess = null;
            return new Exception($"obfs4proxy (lyrebird) exited with code {exitCode}");
        }

        // Verify all requested transports were registered
        var missing = transports.Where(t => !LyrebirdCmethods.ContainsKey(t)).ToList();
        if (missing.Count > 0)
            AppLog.W(TAG, $"Lyrebird did not register transports: [{string.Join(", ", missing)}]");

        return null;
    }

    private static void StopLyrebird()
    {
        var p = _lyrebirdProcess;
        _lyrebirdProcess = null;
        if (p != null)
        {
            try
            {
                AppLog.D(TAG, "Stopping lyrebird process...");
                // Close stdin to signal graceful shutdown (TOR_PT_EXIT_ON_STDIN_CLOSE=1)
                try { p.StandardInput.Close(); } catch { /* already closed */ }
                if (!p.WaitForExit(500))
                {
                    try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                    p.WaitForExit(300);
                }
                AppLog.D(TAG, "Lyrebird process stopped");
            }
            catch (Exception e)
            {
                AppLog.E(TAG, "Error stopping lyrebird", e);
            }
        }
        LyrebirdCmethods.Clear();
    }

    // --- Transport detection ---

    /// <summary>
    /// Detect the pluggable transport type from the first bridge line's prefix.
    /// Returns the transport name (e.g., "obfs4", "webtunnel", "meek_lite",
    /// "snowflake"); unrecognized prefixes default to "obfs4".
    /// </summary>
    private static string DetectTransport(string bridgeLines)
    {
        var firstLine = ParallelTorManager.SplitLines(bridgeLines)
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))
            ?.Trim();
        if (firstLine == null) return "snowflake";
        var firstWord = FirstToken(firstLine) ?? "";
        return firstWord switch
        {
            "obfs4" => "obfs4",
            "webtunnel" => "webtunnel",
            "meek_lite" => "meek_lite",
            "snowflake" => "snowflake",
            _ => "obfs4" // Default to obfs4 if prefix is unrecognized (could be IP:PORT format)
        };
    }

    // --- Stop / status ---

    /// <summary>Stop the Tor process and lyrebird.</summary>
    public static void StopClient()
    {
        // Stop Tor first
        var p = _torProcess;
        _torProcess = null;
        if (p != null)
        {
            try
            {
                AppLog.D(TAG, "Stopping Tor process...");
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(500);
                }
                AppLog.D(TAG, "Tor process stopped");
            }
            catch (Exception e)
            {
                AppLog.E(TAG, "Error stopping Tor", e);
            }
        }
        IsTorReady = false;
        TorBootstrapProgress = 0;

        // Stop PTs (no separate Snowflake PT on Windows: lyrebird owns it)
        StopLyrebird();
    }

    public static bool IsRunning()
    {
        // For the Windows port there is no separate Snowflake client process —
        // lyrebird also needs to be alive if it was started.
        var lyrebirdOk = _lyrebirdProcess == null || !_lyrebirdProcess.HasExited;
        var torOk = _torProcess is { HasExited: false };
        return lyrebirdOk && torOk;
    }

    public static bool IsClientHealthy() => IsRunning() && IsTorReady;

    // --- torrc ---

    /// <summary>
    /// Write torrc config file.
    /// If bridgeLines is empty (or AMP/SMART), uses built-in Snowflake through
    /// lyrebird with a synthetic bridge line. Otherwise, auto-detects transport
    /// from bridge line prefixes and generates appropriate
    /// ClientTransportPlugin directives.
    ///
    /// <paramref name="lyrebirdMethods"/>: map of transport name -&gt;
    /// "host:port" from pre-started lyrebird. When present, uses `socks5`
    /// instead of `exec` directives.
    /// </summary>
    private static string WriteTorrc(
        string torDataDir,
        string listenHost,
        int torSocksPort,
        string bridgeLines,
        IReadOnlyDictionary<string, string> lyrebirdMethods,
        string? upstreamSocksAddr)
    {
        var torrcFile = Path.Combine(torDataDir, "torrc");
        var trimmed = bridgeLines.Trim();
        var isDirect = trimmed == "DIRECT";
        var isAmp = trimmed == "SNOWFLAKE_AMP";
        var isSmart = trimmed == "SMART";

        // Detect if webtunnel or meek is involved (these have higher latency)
        var hasSlowTransport = !isDirect && ParallelTorManager.SplitLines(bridgeLines).Any(line =>
        {
            var cleaned = CleanBridgeLine(line);
            var transport = FirstToken(cleaned) ?? "";
            return transport is "webtunnel" or "meek_lite";
        });

        // Whether the final torrc will contain a ClientTransportPlugin line.
        // Tor rejects `Socks5Proxy` + `ClientTransportPlugin` together (it
        // treats the combination as an "external proxy with another proxy
        // type" and refuses to start), so we only emit `Socks5Proxy` when no
        // PT is in use. When a PT is in use, lyrebird's `TOR_PT_PROXY` env
        // handles upstream routing for meek/obfs4/webtunnel/snowflake traffic,
        // and Tor's own fetches ride through the bridge anyway.
        var willEmitClientTransportPlugin = !isDirect && (
            string.IsNullOrWhiteSpace(bridgeLines) ||
            isAmp ||
            isSmart ||
            ParallelTorManager.SplitLines(bridgeLines).Any(line =>
            {
                var cleaned = CleanBridgeLine(line);
                var transport = FirstToken(cleaned) ?? "";
                return transport is "snowflake" or "obfs4" or "webtunnel" or "meek_lite";
            }));

        // Common torrc settings (UseBridges omitted for direct mode)
        var common = new StringBuilder();
        common.Append($"SocksPort {listenHost}:{torSocksPort}\n");
        common.Append($"DataDirectory {Quote(torDataDir)}\n");
        if (!isDirect) common.Append("UseBridges 1\n");
        // Only reference GeoIP files if they exist (the official bundle
        // databases, the Windows counterpart of Android's extracted assets)
        if (File.Exists(TorBinaries.GeoIp))
            common.Append($"GeoIPFile {Quote(TorBinaries.GeoIp)}\n");
        if (File.Exists(TorBinaries.GeoIp6))
            common.Append($"GeoIPv6File {Quote(TorBinaries.GeoIp6)}\n");
        common.Append("Log info stdout\n");
        // Webtunnel/meek add HTTP overhead per round trip — need generous
        // timeout for the multi-hop CREATE→EXTEND→EXTEND circuit handshake.
        //
        // This line does not actually reach Tor. The user template is
        // appended after this block and ends with its own CircuitBuildTimeout,
        // so last-wins overrides whatever is chosen here. Kept as-is rather
        // than deleted so the intent is still visible, but a per-transport
        // timeout has to be enforced from the template or the control port,
        // not from here.
        common.Append($"CircuitBuildTimeout {(hasSlowTransport ? 120 : 60)}\n");
        common.Append("LearnCircuitBuildTimeout 0\n");
        // Shorter keepalive to prevent HTTP-based transport idle timeouts
        // from closing the bridge connection between keepalive cells
        common.Append("KeepalivePeriod 30\n");
        // A single guard is the point of a Snowflake runner: one long-lived
        // WebRTC relay, so the circuit sticks to it instead of hopping
        // between guards that all have to re-negotiate. It is also inert,
        // because the template that follows declares NumEntryGuards 10 and
        // wins. To make this stick it has to be the last writer.
        common.Append("NumEntryGuards 1\n");
        common.Append("ClientUseIPv4 1\n");
        // Must allow IPv6: webtunnel bridges use 2001:db8:: placeholder
        // addresses (PT connects via URL, not the IP, but Tor checks this
        // when selecting bridges for circuit building)
        common.Append("ClientUseIPv6 1\n");
        common.Append("ClientPreferIPv6ORPort auto\n");
        common.Append("SafeLogging 0\n");

        // --- Iran / heavily-censored network tuning ---
        // Minimize writes: Tor caches stay in RAM and only flush to disk on
        // clean shutdown (Android: flash-wear/battery concern, kept here for
        // the same wire footprint).
        common.Append("AvoidDiskWrites 1\n");
        // Default is 24h: Tor shuts down after a day of inactivity and
        // re-bootstraps on next launch. For Iran users where bootstrap can
        // take minutes over Snowflake/WebTunnel, 4 weeks avoids the
        // "reopened app, have to wait again" case.
        common.Append("DormantClientTimeout 2419200\n");
        // Skip the 6s stagger before first authority contact. Consensus
        // download is the long pole on censored networks; we want it ASAP.
        common.Append("ClientBootstrapConsensusAuthorityDownloadInitialDelay 0\n");
        // Force full connection padding. "auto" already enables it for
        // bridge clients, but making it explicit guards against any future
        // default change and makes the wire footprint less distinctive.
        common.Append("ConnectionPadding 1\n");
        common.Append("ReducedConnectionPadding 0\n");
        // When chained behind a SOCKS5 layer (e.g. DoH), route Tor's own
        // outbound connections through it. Skipped when a PT is configured
        // because Tor rejects Socks5Proxy + ClientTransportPlugin together;
        // in that case lyrebird's TOR_PT_PROXY env var covers PT traffic.
        if (upstreamSocksAddr != null && !willEmitClientTransportPlugin)
            common.Append($"Socks5Proxy {upstreamSocksAddr}\n");

        // Transport-specific lines
        string transportLines;
        if (isDirect)
        {
            // Direct mode: no bridges, no transport plugins
            transportLines = "";
        }
        else if (string.IsNullOrWhiteSpace(bridgeLines) || isAmp || isSmart)
        {
            // Built-in Snowflake or AMP or SMART fallback (zero-config), via
            // lyrebird's snowflake transport. The bridge line carries the
            // broker/front/STUN/uTLS constants Android bakes into its Go PT.
            var plugin = new StringBuilder();
            if (lyrebirdMethods.TryGetValue("snowflake", out var sfAddr))
            {
                plugin.Append($"ClientTransportPlugin snowflake socks5 {sfAddr}\n");
            }
            else
            {
                AppLog.W(TAG, "No lyrebird CMETHOD for transport: snowflake");
            }
            var args = isAmp
                ? $"url={AmpBrokerUrl} fronts={AmpFrontDomain} ice={StunUrls} utls-imitate={UtlsClientId} ampcache={AmpCacheUrl}"
                : $"url={BrokerUrl} fronts={FrontDomains} ice={StunUrls} utls-imitate={UtlsClientId}";
            plugin.Append($"Bridge snowflake 192.0.2.3:80 {BridgeFingerprint} {args}\n");
            transportLines = plugin.ToString().Trim();
        }
        else
        {
            // Auto-detect transports from bridge lines and generate config.
            // Strip "Bridge" prefix if users copy-pasted from BridgeDB.
            var transportsNeeded = new List<string>();
            var bridgeDirectives = new StringBuilder();
            foreach (var raw in ParallelTorManager.SplitLines(bridgeLines))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var line = CleanBridgeLine(raw);
                var transport = FirstToken(line);
                if (transport == null) continue;
                if (!transportsNeeded.Contains(transport)) transportsNeeded.Add(transport);
                bridgeDirectives.Append($"Bridge {line}\n");
            }

            var pluginDirectives = new StringBuilder();
            foreach (var transport in transportsNeeded)
            {
                switch (transport)
                {
                    case "snowflake":
                    case "obfs4":
                    case "webtunnel":
                    case "meek_lite":
                    {
                        // Use pre-started lyrebird SOCKS5 (launched by us, not by Tor)
                        if (lyrebirdMethods.TryGetValue(transport, out var addr))
                            pluginDirectives.Append($"ClientTransportPlugin {transport} socks5 {addr}\n");
                        else
                            AppLog.W(TAG, $"No lyrebird CMETHOD for transport: {transport}");
                        break;
                    }
                }
            }

            transportLines = $"{pluginDirectives.ToString().Trim()}\n{bridgeDirectives.ToString().Trim()}";
        }

        var torrcContent = $"{common.ToString().Trim()}\n{transportLines}\n";
        File.WriteAllText(torrcFile, torrcContent);

        // Log torrc for debugging PT issues
        AppLog.D(TAG, "--- Generated torrc ---");
        foreach (var line in ParallelTorManager.SplitLines(torrcContent))
        {
            if (!string.IsNullOrWhiteSpace(line)) AppLog.D(TAG, $"torrc: {line}");
        }
        AppLog.D(TAG, "--- End torrc ---");

        return torrcFile;
    }

    // --- Helper functions ---

    /// <summary>Drop a pasted "Bridge " prefix and surrounding whitespace.</summary>
    private static string CleanBridgeLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed.StartsWith("bridge ", StringComparison.OrdinalIgnoreCase)
            ? trimmed[7..].Trim()
            : trimmed;
    }

    private static string? FirstToken(string line) =>
        WhitespaceRegex().Split(line).FirstOrDefault(t => t.Length > 0)?.ToLowerInvariant();

    /// <summary>Quote a path for torrc when it contains spaces (common on Windows).</summary>
    private static string Quote(string path) =>
        path.Contains(' ') ? $"\"{path}\"" : path;

    private static bool WaitForPortAvailable(int port, int maxWaitMs = 5_000)
    {
        var start = Environment.TickCount64;
        while (Environment.TickCount64 - start < maxWaitMs)
        {
            if (!IsPortInUse(port)) return true;
            AppLog.D(TAG, $"Waiting for port {port} to be released...");
            Thread.Sleep(200);
        }
        return !IsPortInUse(port);
    }

    private static bool IsPortInUse(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static bool VerifyTcpListening(string host, int port)
    {
        try
        {
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            return socket.ConnectAsync(host, port).Wait(2_000);
        }
        catch
        {
            return false;
        }
    }

    [GeneratedRegex(@"Bootstrapped (\d+)%")]
    private static partial Regex BootstrapRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
