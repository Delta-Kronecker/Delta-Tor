using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DeltaTor.Core.Util;

namespace DeltaTor.Core;

/// <summary>
/// Points the machine's resolvers at Tor and puts them back afterwards.
///
/// Windows can only be told "use this address, port 53", never "use this port".
/// Tor's own resolver (torrc <c>DNSPort</c>) therefore cannot be the thing the
/// adapters name directly — the port is dynamic and differs per runner and per
/// connection. Instead every adapter is pointed at the loopback relay
/// <see cref="TorDns"/> runs, and the relay forwards the raw query to the
/// winning runner's DNSPort.
///
/// Every change is captured first (<see cref="AdapterState"/>) and undone by
/// <see cref="Restore"/>, so a disconnect leaves the machine exactly as it was
/// found: DHCP adapters go back to <c>source=dhcp</c>, static ones get their
/// original servers back.
/// </summary>
public static class SystemDns
{
    private const string Tag = "TorDns";
    private static readonly object Gate = new();

    /// <summary>What an adapter looked like before it was pointed at Tor.</summary>
    private sealed record AdapterState(bool DhcpV4, bool DhcpV6, string[] V4, string[] V6);

    /// <summary>Adapters we changed, and what to put back. Only these are restored.</summary>
    private static readonly Dictionary<string, AdapterState> Saved = new(StringComparer.Ordinal);

    /// <summary>
    /// Adapters we want pointed at the relay. Superset of <see cref="Saved"/>:
    /// the TUN adapter is destroyed with its tunnel, so it has nothing to
    /// restore but still has to be kept pointed while it lives.
    /// </summary>
    private static readonly HashSet<string> Desired = new(StringComparer.Ordinal);

    private static string? _v4;
    private static string? _v6;

    /// <summary>
    /// Bumped by every <see cref="Restore"/>. A slow re-apply that started
    /// before a teardown sees the change and gives up instead of pointing a
    /// freshly restored adapter back at a relay that is already gone.
    /// </summary>
    private static int _epoch;

    /// <summary>True once at least one adapter has been pointed at Tor.</summary>
    public static bool Active
    {
        get { lock (Gate) return _v4 != null; }
    }

    /// <summary>
    /// The adapters that carry the machine's internet: up, not loopback, not a
    /// tunnel this app owns, and holding a default gateway. Those are the ones
    /// Windows would otherwise resolve names with, i.e. the leak.
    /// </summary>
    public static IReadOnlyList<string> PhysicalAdapters()
    {
        var names = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (ChainNetwork.IsTunnelLike(nic)) continue;

                var props = nic.GetIPProperties();
                var hasV4 = props.UnicastAddresses.Any(
                    u => u.Address.AddressFamily == AddressFamily.InterNetwork);
                if (!hasV4) continue;

                var hasGateway = props.GatewayAddresses.Any(
                    g => g.Address.AddressFamily == AddressFamily.InterNetwork &&
                         !g.Address.Equals(IPAddress.Any));
                if (!hasGateway) continue;

                names.Add(nic.Name);
            }
        }
        catch (Exception e)
        {
            AppLog.W(Tag, $"physical adapter scan failed: {e.Message}");
        }

        return names;
    }

    /// <summary>Point every physical adapter at the relay (captured for restore).</summary>
    public static void ApplyPhysical(string v4, string? v6)
    {
        lock (Gate)
        {
            _v4 = v4;
            _v6 = v6;
        }

        foreach (var name in PhysicalAdapters()) Apply(name, v4, v6, capture: true);
    }

    /// <summary>
    /// Point a mode's own adapter at the relay (the TUN or chain interface).
    /// Not captured: it comes down with the session, so there is nothing to
    /// restore.
    /// </summary>
    public static void ApplyTunnel(string adapterName, string v4, string? v6)
    {
        if (string.IsNullOrWhiteSpace(adapterName)) return;
        Apply(adapterName, v4, v6, capture: false);
    }

    /// <summary>
    /// Re-assert the resolver on every adapter we touched when something put it
    /// back. The TUN data plane in particular owns its adapter's DNS and can
    /// overwrite ours during a reconnect, so this is checked on a slow tick.
    /// Cheap when nothing changed: it reads the live DNS list, no process.
    /// </summary>
    public static void VerifyAndReapply()
    {
        string v4;
        string? v6;
        List<string> desired;
        int epoch;
        lock (Gate)
        {
            if (_v4 is null) return;
            v4 = _v4;
            v6 = _v6;
            desired = Desired.ToList();
            epoch = _epoch;
        }

        // A physical adapter that appeared since the last check joins the set.
        foreach (var name in PhysicalAdapters())
        {
            lock (Gate)
            {
                if (epoch != _epoch) return;
                Desired.Add(name);
            }
            Apply(name, v4, v6, capture: true, epoch);
        }

        foreach (var name in desired)
        {
            if (!Apply(name, v4, v6, capture: false, epoch)) return;
        }
    }

    private static bool Apply(string name, string v4, string? v6, bool capture, int epoch = -1)
    {
        lock (Gate)
        {
            if (epoch >= 0 && epoch != _epoch) return false;

            Desired.Add(name);

            if (AlreadyOnly(name, v4, v6)) return true;

            // Not present right now (a tunnel that already came down, or an
            // adapter between states): stop tracking it instead of firing a
            // doomed netsh at it on every tick.
            if (Find(name) is null)
            {
                Desired.Remove(name);
                return true;
            }

            if (capture && !Saved.ContainsKey(name))
            {
                if (!TryCapture(name, out var state)) return false;
                Saved[name] = state;
            }

            SetV4(name, v4);
            if (v6 != null) SetV6(name, v6);
            AppLog.I(Tag, $"resolver for \"{name}\" -> {v4}{(v6 != null ? " / " + v6 : "")}");
            return true;
        }
    }

    /// <summary>True when the adapter's live resolver list is exactly ours.</summary>
    private static bool AlreadyOnly(string name, string v4, string? v6)
    {
        var nic = Find(name);
        if (nic is null) return false;
        try
        {
            var dns = nic.GetIPProperties().DnsAddresses;
            var cur4 = dns.Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString()).ToHashSet(StringComparer.Ordinal);
            var cur6 = dns.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6)
                .Select(a => a.ToString()).ToHashSet(StringComparer.Ordinal);

            if (cur4.Count != 1 || !cur4.Contains(v4)) return false;
            if (v6 != null && (cur6.Count != 1 || !cur6.Contains(v6))) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryCapture(string name, out AdapterState state)
    {
        state = new AdapterState(false, false, Array.Empty<string>(), Array.Empty<string>());
        var nic = Find(name);
        if (nic is null) return false;
        try
        {
            var props = nic.GetIPProperties();
            var v4 = props.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString()).Distinct(StringComparer.Ordinal).ToArray();
            var v6 = props.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6)
                .Select(a => a.ToString()).Distinct(StringComparer.Ordinal).ToArray();
            var dhcp4 = false;
            var dhcp6 = false;
            try { dhcp4 = props.GetIPv4Properties().IsDhcpEnabled; } catch { /* no IPv4 config */ }
            try { dhcp6 = props.GetIPv6Properties().IsDhcpEnabled; } catch { /* no IPv6 config */ }
            state = new AdapterState(dhcp4, dhcp6, v4, v6);
            return true;
        }
        catch (Exception e)
        {
            AppLog.W(Tag, $"could not read DNS of \"{name}\": {e.Message}");
            return false;
        }
    }

    private static void SetV4(string name, string v4)
    {
        var ok = ChainNetwork.RunNetsh(
            $"interface ipv4 set dnsservers name=\"{name}\" source=static address={v4} " +
            "register=primary validate=no");
        if (!ok)
        {
            // Some adapters reject the suffix registration; the address itself
            // is what matters, so retry without it.
            ChainNetwork.RunNetsh(
                $"interface ipv4 set dnsservers name=\"{name}\" source=static address={v4} " +
                "register=none validate=no");
        }
    }

    private static void SetV6(string name, string v6)
    {
        var ok = ChainNetwork.RunNetsh(
            $"interface ipv6 set dnsservers name=\"{name}\" source=static address={v6} " +
            "register=primary validate=no");
        if (!ok)
        {
            ChainNetwork.RunNetsh(
                $"interface ipv6 set dnsservers name=\"{name}\" source=static address={v6} " +
                "register=none validate=no");
        }
    }

    /// <summary>Put every captured adapter back the way it was found.</summary>
    public static void Restore()
    {
        Dictionary<string, AdapterState> saved;
        lock (Gate)
        {
            saved = new Dictionary<string, AdapterState>(Saved, StringComparer.Ordinal);
            Saved.Clear();
            Desired.Clear();
            _v4 = null;
            _v6 = null;
            _epoch++;
        }

        foreach (var (name, state) in saved)
        {
            RestoreV4(name, state);
            RestoreV6(name, state);
        }

        if (saved.Count > 0) AppLog.I(Tag, $"restored resolvers on {saved.Count} adapter(s)");
    }

    private static void RestoreV4(string name, AdapterState state)
    {
        if (state.DhcpV4 || state.V4.Length == 0)
        {
            ChainNetwork.RunNetsh($"interface ipv4 set dnsservers name=\"{name}\" source=dhcp");
            return;
        }

        ChainNetwork.RunNetsh(
            $"interface ipv4 set dnsservers name=\"{name}\" source=static address={state.V4[0]} " +
            "register=primary validate=no");
        for (var i = 1; i < state.V4.Length; i++)
        {
            ChainNetwork.RunNetsh(
                $"interface ipv4 add dnsservers name=\"{name}\" address={state.V4[i]} " +
                $"index={i + 1} validate=no");
        }
    }

    private static void RestoreV6(string name, AdapterState state)
    {
        if (state.DhcpV6 || state.V6.Length == 0)
        {
            ChainNetwork.RunNetsh($"interface ipv6 set dnsservers name=\"{name}\" source=dhcp");
            return;
        }

        ChainNetwork.RunNetsh(
            $"interface ipv6 set dnsservers name=\"{name}\" source=static address={state.V6[0]} " +
            "register=primary validate=no");
        for (var i = 1; i < state.V6.Length; i++)
        {
            ChainNetwork.RunNetsh(
                $"interface ipv6 add dnsservers name=\"{name}\" address={state.V6[i]} " +
                $"index={i + 1} validate=no");
        }
    }

    /// <summary>Drop resolver answers that were learned on the pre-Tor path.</summary>
    public static void Flush() => ChainNetwork.Run("ipconfig", "/flushdns");

    private static NetworkInterface? Find(string name)
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }
}
