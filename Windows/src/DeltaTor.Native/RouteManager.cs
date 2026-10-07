using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DeltaTor.Core;

namespace DeltaTor.Native;

/// <summary>
/// System routing for full-tunnel mode: catch-all routes into the Wintun
/// adapter, the adapter's DNS, and — critically — bypass routes for the
/// app's own outbound connections.
///
/// Why bypass routes exist: Android keeps the VPN app's own sockets out of
/// the tunnel with <c>addDisallowedApplication(packageName)</c>
/// (TorVpnService.kt), and Windows has no per-process routing at all. tor.exe
/// and lyrebird.exe talk to relays, bridges and brokers whose destinations
/// change constantly; if the catch-all routes swallowed those connections
/// they would re-enter the tunnel through hev-socks5-tunnel, ask
/// TorSocksBridge for a connection back to the same relay, and loop forever
/// (a deadlock, since the winning Tor can only build circuits through the
/// outbound connections that are being captured).
///
/// The Windows equivalent is destination-based: a /32 host route out the
/// physical default gateway for every destination tor or lyrebird actually
/// uses. Destinations are learned from the live TCP table
/// (GetExtendedTcpTable): guards are already connected before the tunnel
/// starts — BridgeRace only calls TunnelEngine.Start after the winner
/// bootstrapped — so the seed pass catches them; anything later surfaces as
/// SYN_SENT within a watcher tick and Tor retries its connections, so a
/// missed first packet self-heals in about a second.
///
/// Crash safety: every prefix that has been installed is persisted to
/// tun-routes.txt in the app data root before the routes go in, and the file
/// is deleted only on clean removal — a run that dies leaves the file behind
/// and <see cref="CleanupLeftovers"/> (called at startup) takes the routes
/// back. Without that, a 0.0.0.0/1 pointing at a dead adapter would black-
/// hole system traffic until reboot.
/// </summary>
public static class RouteManager
{
    /// <summary>Wintun adapter name — hev's Windows backend creates it from tunnel.name.</summary>
    public const string AdapterName = "DeltaTor";

    private const string Tag = "RouteManager";

    /// <summary>Android TorVpnService DEFAULT_DNS (queries follow 0.0.0.0/1 into the tunnel).</summary>
    private const string DefaultDns = "8.8.8.8";

    private static readonly object Lock = new();

    private static readonly List<string> InstalledPrefixes = new();
    private static readonly HashSet<string> Bypassed = new(StringComparer.Ordinal);
    private static string? _gateway;
    private static bool _installed;
    private static volatile bool _watchRunning;
    private static Thread? _watchThread;

    private static string RouteFile => Path.Combine(AppPaths.Root, "tun-routes.txt");

    /// <summary>
    /// Remove routes an earlier run left behind (crash/kill). Cheap no-op when
    /// the previous shutdown was clean: the marker file only exists while
    /// routes do.
    /// </summary>
    public static void CleanupLeftovers()
    {
        try
        {
            if (!File.Exists(RouteFile)) return;
            var prefixes = new List<string>();
            foreach (var line in File.ReadAllLines(RouteFile))
                if (!string.IsNullOrWhiteSpace(line))
                    prefixes.Add(line.Trim());
            prefixes.Add("0.0.0.0/1");
            prefixes.Add("128.0.0.0/1");

            var sb = new StringBuilder();
            sb.AppendLine("$ErrorActionPreference='SilentlyContinue'");
            foreach (var p in prefixes.Distinct(StringComparer.Ordinal))
                sb.AppendLine($"Remove-NetRoute -DestinationPrefix '{p}' | Out-Null");
            sb.AppendLine(AdapterRemovalScript());
            sb.AppendLine("'CLEAN_OK'");
            RunPowerShell(sb.ToString(), 15000);
            File.Delete(RouteFile);
            AppLog.I(Tag, $"cleaned leftovers from a previous run ({prefixes.Count} prefixes)");
        }
        catch (Exception e)
        {
            AppLog.W(Tag, $"leftover cleanup failed: {e.Message}");
        }
    }

    /// <summary>
    /// Install the full-tunnel routing: seed bypass routes from tor's live
    /// connections, wait for hev's Wintun adapter, add the catch-all routes,
    /// set the adapter's DNS, start the bypass watcher. Null on success.
    /// </summary>
    public static Exception? InstallTunnel()
    {
        lock (Lock)
        {
            if (_installed) return null;
            try
            {
                var gateway = DiscoverGateway();
                if (gateway == null)
                    return new InvalidOperationException(
                        "No IPv4 default gateway found; full-tunnel bypass routes are impossible");

                // Record the catch-all prefixes BEFORE adding them, so a crash
                // anywhere below is recoverable by CleanupLeftovers.
                InstalledPrefixes.Clear();
                InstalledPrefixes.Add("0.0.0.0/1");
                InstalledPrefixes.Add("128.0.0.0/1");
                Bypassed.Clear();
                _gateway = gateway;
                PersistLocked();

                // Seed: tor's guard connections are already up at this point.
                // A failed seed is not fatal — the watcher retries every tick.
                var seed = CollectWatchedRemotes();
                if (seed.Count > 0) AddBypassLocked(seed);

                var res = RunPowerShell(WaitAndInstallTunRoutes(), 15000);
                if (!res.Ok || !res.Output.Contains("TUN_ROUTES_OK"))
                {
                    var detail = string.IsNullOrWhiteSpace(res.Output) ? "no output" : res.Output.Trim();
                    RemoveLocked();
                    return new InvalidOperationException($"Tunnel routes failed: {detail}");
                }

                _installed = true;
                StartWatching();
                AppLog.I(Tag,
                    $"routes up: 0.0.0.0/1 + 128.0.0.0/1 via {AdapterName}, " +
                    $"gateway {gateway}, bypass seeded from {seed.Count} live destination(s)");
                return null;
            }
            catch (Exception e)
            {
                RemoveLocked();
                return e;
            }
        }
    }

    /// <summary>Stop the watcher and take everything back (routes, DNS, adapter). Idempotent.</summary>
    public static void RemoveTunnel()
    {
        // Join outside the lock: the watcher takes the lock itself.
        StopWatching();
        lock (Lock)
        {
            if (!_installed && InstalledPrefixes.Count == 0) return;
            RemoveLocked();
            AppLog.I(Tag, "routes removed");
        }
    }

    /// <summary>Watcher running? (diagnostics/tests.)</summary>
    public static bool IsWatching => _watchThread is { IsAlive: true };

    // ---------------------------------------------------------------------
    // Watcher
    // ---------------------------------------------------------------------

    private static void StartWatching()
    {
        if (_watchThread is { IsAlive: true }) return;
        _watchRunning = true;
        _watchThread = new Thread(WatchLoop)
        {
            IsBackground = true,
            Name = "tunnel-bypass-routes",
        };
        _watchThread.Start();
    }

    /// <summary>Stop only the bypass watcher (routes stay up). Idempotent.</summary>
    public static void StopWatching()
    {
        _watchRunning = false;
        var t = _watchThread;
        _watchThread = null;
        if (t is { IsAlive: true }) t.Join(2000);
    }

    private static void WatchLoop()
    {
        while (_watchRunning)
        {
            try
            {
                Thread.Sleep(1000);
                if (!_watchRunning) break;
                lock (Lock)
                {
                    if (!_installed || !_watchRunning) break;
                    var fresh = CollectWatchedRemotes()
                        .Where(ip => !Bypassed.Contains(ip))
                        .ToList();
                    if (fresh.Count > 0) AddBypassLocked(fresh);
                }
            }
            catch (Exception e)
            {
                AppLog.W(Tag, $"route watch tick failed: {e.Message}");
            }
        }
    }

    // ---------------------------------------------------------------------
    // Live destination discovery (tor, lyrebird, our own probe sockets)
    // ---------------------------------------------------------------------

    private const int AfInet = 2;
    private const int TcpTableOwnerPidAll = 5;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint StateSynSent = 3;
    private const uint StateEstablished = 5;
    private const int TcpRowOwnerPidSize = 24;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

    /// <summary>Remote IPv4 destinations of every TCP connection owned by tor/lyrebird/us.</summary>
    private static HashSet<string> CollectWatchedRemotes()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var pids = WatchedPids();

        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (size <= 0) break;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var rc = GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
                if (rc == ErrorInsufficientBuffer) continue;
                if (rc != 0) break;

                var count = Marshal.ReadInt32(buffer);
                var rows = IntPtr.Add(buffer, sizeof(uint));
                for (var i = 0; i < count; i++)
                {
                    var off = i * TcpRowOwnerPidSize;
                    var state = (uint)Marshal.ReadInt32(rows, off);
                    if (state != StateSynSent && state != StateEstablished) continue;
                    var remote = (uint)Marshal.ReadInt32(rows, off + 12);
                    var pid = (uint)Marshal.ReadInt32(rows, off + 20);
                    if (!pids.Contains((int)pid)) continue;
                    var ip = FormatV4(remote);
                    if (ip != null) result.Add(ip);
                }
                break;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return result;
    }

    private static HashSet<int> WatchedPids()
    {
        var pids = new HashSet<int> { Environment.ProcessId };
        foreach (var name in new[] { "tor", "lyrebird" })
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { continue; }
            foreach (var p in procs)
            {
                try { pids.Add(p.Id); }
                finally { p.Dispose(); }
            }
        }
        return pids;
    }

    /// <summary>MIB row addresses are network-order octets; reject loopback/multicast/anything unpublishable.</summary>
    private static string? FormatV4(uint addr)
    {
        if (addr == 0) return null;
        var a0 = addr & 0xFF;
        var a1 = (addr >> 8) & 0xFF;
        var a2 = (addr >> 16) & 0xFF;
        var a3 = (addr >> 24) & 0xFF;
        if (a0 == 0 || a0 == 127 || a0 >= 224) return null;
        return $"{a0}.{a1}.{a2}.{a3}";
    }

    // ---------------------------------------------------------------------
    // PowerShell plumbing
    // ---------------------------------------------------------------------

    private static void AddBypassLocked(IReadOnlyCollection<string> ips)
    {
        if (ips.Count == 0 || _gateway == null) return;
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference='Stop'");
        foreach (var ip in ips)
            sb.AppendLine(
                $"Add-NetRoute -DestinationPrefix '{ip}/32' -NextHop '{_gateway}' " +
                "-PolicyStore ActiveStore -ErrorAction Stop | Out-Null");
        sb.AppendLine("'BYPASS_OK'");

        var res = RunPowerShell(sb.ToString(), 8000);
        if (!res.Ok || !res.Output.Contains("BYPASS_OK"))
        {
            // Not recorded: the next watcher tick retries this batch.
            AppLog.E(Tag, $"bypass route install failed for {ips.Count} destination(s): {res.Output.Trim()}");
            return;
        }
        foreach (var ip in ips)
        {
            var prefix = ip + "/32";
            if (InstalledPrefixes.Add(prefix)) Bypassed.Add(ip);
        }
        PersistLocked();
        AppLog.D(Tag, $"bypass routes: +{ips.Count} (total {InstalledPrefixes.Count})");
    }

    private static void RemoveLocked()
    {
        if (InstalledPrefixes.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine("$ErrorActionPreference='SilentlyContinue'");
            foreach (var p in InstalledPrefixes)
                sb.AppendLine($"Remove-NetRoute -DestinationPrefix '{p}' | Out-Null");
            // Belt and braces for the catch-alls even if the list was lost.
            sb.AppendLine($"Remove-NetRoute -DestinationPrefix '0.0.0.0/1' -InterfaceAlias '{AdapterName}' | Out-Null");
            sb.AppendLine($"Remove-NetRoute -DestinationPrefix '128.0.0.0/1' -InterfaceAlias '{AdapterName}' | Out-Null");
            sb.AppendLine(AdapterRemovalScript());
            sb.AppendLine("'ROUTES_REMOVED'");
            RunPowerShell(sb.ToString(), 15000);
        }
        InstalledPrefixes.Clear();
        Bypassed.Clear();
        _gateway = null;
        _installed = false;
        try { File.Delete(RouteFile); }
        catch { /* next startup's CleanupLeftovers retries */ }
    }

    private static string AdapterRemovalScript() =>
        $"Get-NetAdapter -Name '{AdapterName}' -ErrorAction SilentlyContinue | " +
        "Remove-NetAdapter -Confirm:$false -ErrorAction SilentlyContinue";

    private static void PersistLocked()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllLines(RouteFile, InstalledPrefixes);
        }
        catch (Exception e)
        {
            AppLog.W(Tag, $"persist route file failed: {e.Message}");
        }
    }

    private static string? DiscoverGateway()
    {
        const string script = """
            $ErrorActionPreference='SilentlyContinue'
            $r = Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' |
                 Where-Object { $_.NextHop -ne '0.0.0.0' } |
                 Sort-Object InterfaceMetric, RouteMetric |
                 Select-Object -First 1
            if ($r) { 'GW|' + $r.NextHop } else { 'GW|NONE' }
            """;
        var res = RunPowerShell(script, 15000);
        if (!res.Ok) return null;
        foreach (var line in res.Output.Split('\n'))
        {
            var t = line.Trim();
            if (!t.StartsWith("GW|", StringComparison.Ordinal)) continue;
            var gw = t.Substring(3).Trim();
            return gw.Length > 0 && gw != "NONE" ? gw : null;
        }
        return null;
    }

    // Not an interpolated raw string: a single-$ interpolated raw literal
    // cannot contain {{ (it would need $$-level escaping), and the braces
    // below are PowerShell's, so the two holes are plain @TOKEN@ replaces.
    private static string WaitAndInstallTunRoutes() =>
        """
        $ErrorActionPreference='Stop'
        $deadline=(Get-Date).AddSeconds(8)
        $ad=$null
        while((Get-Date) -lt $deadline){ $ad=Get-NetAdapter -Name '@ADAPTER@' -ErrorAction SilentlyContinue; if($ad){break}; Start-Sleep -Milliseconds 100 }
        if(-not $ad){ 'ADAPTER_TIMEOUT'; exit 1 }
        Set-NetIPInterface -InterfaceAlias '@ADAPTER@' -AutomaticMetric Disabled -InterfaceMetric 5 -ErrorAction Stop
        if(-not (Get-NetRoute -DestinationPrefix '0.0.0.0/1' -ErrorAction SilentlyContinue)){ Add-NetRoute -DestinationPrefix '0.0.0.0/1' -InterfaceAlias '@ADAPTER@' -PolicyStore ActiveStore -ErrorAction Stop | Out-Null }
        if(-not (Get-NetRoute -DestinationPrefix '128.0.0.0/1' -ErrorAction SilentlyContinue)){ Add-NetRoute -DestinationPrefix '128.0.0.0/1' -InterfaceAlias '@ADAPTER@' -PolicyStore ActiveStore -ErrorAction Stop | Out-Null }
        Set-DnsClientServerAddress -InterfaceAlias '@ADAPTER@' -ServerAddresses '@DNS@' -ErrorAction Stop
        if((Get-NetRoute -DestinationPrefix '0.0.0.0/1' -ErrorAction SilentlyContinue) -and (Get-NetRoute -DestinationPrefix '128.0.0.0/1' -ErrorAction SilentlyContinue)){ 'TUN_ROUTES_OK' } else { 'TUN_ROUTES_FAIL'; exit 1 }
        """
        .Replace("@ADAPTER@", AdapterName)
        .Replace("@DNS@", DefaultDns);

    private readonly record struct PsResult(bool Ok, string Output);

    private static PsResult RunPowerShell(string script, int timeoutMs)
    {
        try
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return new PsResult(false, "failed to start powershell.exe");
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); }
                catch { /* already gone */ }
                return new PsResult(false, "powershell timed out");
            }
            return new PsResult(p.ExitCode == 0, stdout + stderr);
        }
        catch (Exception e)
        {
            return new PsResult(false, e.Message);
        }
    }
}
