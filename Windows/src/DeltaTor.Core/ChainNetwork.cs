using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DeltaTor.Core.Util;

namespace DeltaTor.Core;

/// <summary>The physical IPv4 path the machine's own traffic takes.</summary>
public sealed record PhysicalPath(string Address, string Adapter, string Gateway);

/// <summary>
/// Route and interface helpers the proxy chain needs, kept beside the engine
/// rather than inside the Wintun adapter so the exact same commands are used
/// by the loop-prevention code and the data plane.
///
/// The chain replaces the machine's default route with one through its own
/// adapter. Two things would then eat their own tail: Tor's outbound
/// connections (it is the hop *under* the tunnel) and lyrebird's bridge
/// connections (a separate process, so Tor's OutboundBindAddress cannot reach
/// it). Tor is pinned with SETCONF OutboundBindAddress to the physical
/// address; lyrebird's bridge endpoints get explicit /32 routes through the
/// physical gateway before the chain's default route is added.
/// </summary>
public static class ChainNetwork
{
    private const string Tag = "ChainNet";

    /// <summary>Wintun adapter alias the chain session creates.</summary>
    public const string AdapterName = "DeltaTor SSTP";

    private static readonly object Gate = new();
    private static readonly List<(string Prefix, string Adapter, string Gateway, string Metric)> HostRoutes = new();
    private static PhysicalPath? _physical;
    private static bool _blackholes;
    private static bool _defaultRoute;

    /// <summary>
    /// The IPv4 address, adapter alias and default gateway of the interface
    /// that currently carries the machine's internet. Null when nothing has a
    /// default gateway (offline), which the chain treats as "cannot start".
    /// </summary>
    public static PhysicalPath? FindPhysicalPath()
    {
        try
        {
            PhysicalPath? best = null;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (IsTunnelLike(nic)) continue;

                var props = nic.GetIPProperties();
                var gateway = props.GatewayAddresses
                    .Select(g => g.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork &&
                                         !a.Equals(IPAddress.Any));
                if (gateway is null) continue;

                var address = props.UnicastAddresses
                    .Select(u => u.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork &&
                                         !IPAddress.IsLoopback(a));
                if (address is null) continue;

                best = new PhysicalPath(address.ToString(), nic.Name, gateway.ToString());
                break;
            }

            return best;
        }
        catch (Exception e)
        {
            AppLog.W(Tag, $"physical path lookup failed: {e.Message}");
            return null;
        }
    }

    private static bool IsTunnelLike(NetworkInterface nic)
    {
        if (nic.Name.StartsWith("DeltaTor", StringComparison.OrdinalIgnoreCase)) return true;
        var description = nic.Description;
        return description.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("TAP-Windows", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("TUN", StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------- loop prevention

    /// <summary>
    /// Resolve every bridge endpoint lyrebird will dial and pin it to the
    /// physical gateway. Returns the physical path it found, or null when
    /// there is no default route to preserve. Must run before the chain's
    /// default route exists.
    /// </summary>
    public static PhysicalPath? PrepareHostRoutes(IEnumerable<string> bridgeLines)
    {
        lock (Gate)
        {
            ReleaseHostRoutesLocked();

            _physical = FindPhysicalPath();
            if (_physical is null)
            {
                AppLog.W(Tag, "no physical default route; cannot pin bridge endpoints");
                return null;
            }

            foreach (var address in ExtractBridgeAddresses(bridgeLines))
            {
                AddHostRouteLocked(address);
            }

            return _physical;
        }
    }

    public static void ReleaseHostRoutes()
    {
        lock (Gate) ReleaseHostRoutesLocked();
    }

    private static void ReleaseHostRoutesLocked()
    {
        foreach (var route in HostRoutes)
        {
            RunNetsh(
                $"interface ipv4 delete route prefix={route.Prefix} " +
                $"interface=\"{route.Adapter}\" nexthop={route.Gateway}");
        }

        HostRoutes.Clear();
    }

    private static void AddHostRouteLocked(string address)
    {
        var physical = _physical;
        if (physical is null) return;
        if (HostRoutes.Any(r => r.Prefix == address + "/32")) return;

        var ok = RunNetsh(
            $"interface ipv4 add route prefix={address}/32 " +
            $"interface=\"{physical.Adapter}\" nexthop={physical.Gateway} metric=1 store=active");
        if (ok)
        {
            HostRoutes.Add((address + "/32", physical.Adapter, physical.Gateway, "1"));
            AppLog.D(Tag, $"pinned {address} to {physical.Adapter} via {physical.Gateway}");
        }
    }

    /// <summary>
    /// The bridge host:port endpoints out of the winning runner's bridge
    /// lines. obfs4 / vanilla / meek lines carry <c>host:port</c>; webtunnel
    /// carries a URL. Anything that is not an IPv4 literal is resolved with
    /// the system resolver while the physical route is still the default.
    /// </summary>
    public static IEnumerable<string> ExtractBridgeAddresses(IEnumerable<string> bridgeLines)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in bridgeLines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var type = parts[0].ToLowerInvariant();
            string? host = null;
            switch (type)
            {
                case "webtunnel":
                    if (Uri.TryCreate(parts[1], UriKind.Absolute, out var uri)) host = uri.Host;
                    break;
                case "snowflake":
                    // Snowflake speaks UDP to its broker and STUN servers; a
                    // host route cannot pin a UDP association usefully, so it
                    // stays best-effort (the relay is chosen at runtime).
                    continue;
                default:
                    host = StripPort(parts[1]);
                    break;
            }

            if (string.IsNullOrWhiteSpace(host)) continue;
            if (!IPAddress.TryParse(host, out var parsed))
            {
                try
                {
                    parsed = Dns.GetHostAddresses(host)
                        .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                }
                catch (Exception e)
                {
                    AppLog.D(Tag, $"bridge host {host} did not resolve: {e.Message}");
                    continue;
                }
            }

            if (parsed is null || parsed.AddressFamily != AddressFamily.InterNetwork) continue;
            if (seen.Add(parsed.ToString())) yield return parsed.ToString();
        }
    }

    private static string StripPort(string endpoint)
    {
        if (endpoint.StartsWith('['))
        {
            var close = endpoint.IndexOf(']');
            return close > 0 ? endpoint[1..close] : endpoint;
        }

        var colon = endpoint.LastIndexOf(':');
        if (colon <= 0) return endpoint;
        var suffix = endpoint[(colon + 1)..];
        return suffix.Length > 0 && suffix.All(char.IsDigit) ? endpoint[..colon] : endpoint;
    }

    // -------------------------------------------------------------- adapter I/O

    /// <summary>Configure the chain adapter address (a /32 from IPCP).</summary>
    public static bool SetAdapterAddress(string address) =>
        // set, not add: a stale adapter left by a crashed run already carries an
        // address, and "add" would fail with ERROR_OBJECT_ALREADY_EXISTS until
        // the retries ran out.
        Retry(() => RunNetsh(
            $"interface ipv4 set address name=\"{AdapterName}\" " +
            $"static {address} mask=255.255.255.255"));

    public static bool SetAdapterMtu(int mtu) =>
        RunNetsh($"interface ipv4 set subinterface \"{AdapterName}\" mtu={mtu} store=persistent");

    public static bool SetAdapterDns(IReadOnlyList<string> servers)
    {
        if (servers.Count == 0) return true;

        var ok = RunNetsh(
            $"interface ipv4 set dnsservers name=\"{AdapterName}\" source=static " +
            $"address={servers[0]} register=primary validate=no");
        for (var i = 1; i < servers.Count; i++)
        {
            RunNetsh(
                $"interface ipv4 add dnsservers name=\"{AdapterName}\" address={servers[i]} " +
                $"index={i + 1} validate=no");
        }

        return ok;
    }

    /// <summary>
    /// Send everything into the chain adapter. The address is a /32 with no
    /// gateway, so the default route is on-link; metric 1 outranks the
    /// physical default route without deleting it.
    /// </summary>
    public static bool AddDefaultRoute()
    {
        lock (Gate)
        {
            if (_defaultRoute) return true;
            _defaultRoute = Retry(() => RunNetsh(
                $"interface ipv4 add route prefix=0.0.0.0/0 interface=\"{AdapterName}\" metric=1 store=active"));
            return _defaultRoute;
        }
    }

    public static void RemoveDefaultRoute()
    {
        lock (Gate)
        {
            if (!_defaultRoute) return;
            RunNetsh($"interface ipv4 delete route prefix=0.0.0.0/0 interface=\"{AdapterName}\"");
            _defaultRoute = false;
        }
    }

    /// <summary>
    /// Stop IPv6 leaking around a v4-only tunnel. With no IPv6 address on the
    /// chain adapter, pointing both halves of the space at it blackholes every
    /// IPv6 destination that would otherwise take the physical route.
    /// </summary>
    public static void AddIpv6Blackholes()
    {
        lock (Gate)
        {
            if (_blackholes) return;
            var a = Retry(() => RunNetsh(
                $"interface ipv6 add route prefix=::/1 interface=\"{AdapterName}\" metric=1 store=active"));
            var b = RunNetsh(
                $"interface ipv6 add route prefix=8000::/1 interface=\"{AdapterName}\" metric=1 store=active");
            _blackholes = a || b;
        }
    }

    public static void RemoveIpv6Blackholes()
    {
        lock (Gate)
        {
            if (!_blackholes) return;
            RunNetsh($"interface ipv6 delete route prefix=::/1 interface=\"{AdapterName}\"");
            RunNetsh($"interface ipv6 delete route prefix=8000::/1 interface=\"{AdapterName}\"");
            _blackholes = false;
        }
    }

    /// <summary>Drop cached resolver answers that were learned on the old path.</summary>
    public static void FlushDns() => Run("ipconfig", "/flushdns");

    // ------------------------------------------------------------------ process

    private static bool Retry(Func<bool> action, int attempts = 6)
    {
        for (var i = 0; i < attempts; i++)
        {
            if (action()) return true;
            Thread.Sleep(250);
        }

        return false;
    }

    private static bool RunNetsh(string arguments) => Run("netsh", arguments);

    private static bool Run(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(psi);
            if (process is null) return false;

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(15_000))
            {
                try { process.Kill(); } catch { /* gone */ }
                AppLog.W(Tag, $"{fileName} {arguments} timed out");
                return false;
            }

            if (process.ExitCode == 0) return true;

            AppLog.W(Tag,
                $"{fileName} {arguments} -> {process.ExitCode}: {stderr.Trim()} {stdout.Trim()}".Trim());
            return false;
        }
        catch (Exception e)
        {
            AppLog.W(Tag, $"{fileName} failed: {e.Message}");
            return false;
        }
    }
}