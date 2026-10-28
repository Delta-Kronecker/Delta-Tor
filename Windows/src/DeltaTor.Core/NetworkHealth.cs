using System.Net;
using System.Net.NetworkInformation;

namespace DeltaTor.Core;

/// <summary>
/// Link-health helpers that ignore the tunnel adapter itself.
///
/// Windows raises availability events and flips the aggregate
/// "is any network available" flag when the Wintun adapter comes up or when
/// its routes/DNS are reconciled. Treating those as a real outage is what
/// wedges a tun connect in a false "network down" reconnect loop, so every
/// decision here is made from the physical adapters only.
/// </summary>
public static class NetworkHealth
{
    /// <summary>
    /// True when at least one non-tunnel physical adapter is up and has an IPv4
    /// address. The tunnel adapter name (default "DeltaTor") is excluded by name
    /// along with loopback and obvious virtual/tunnel adapters.
    /// </summary>
    public static bool IsPhysicalLinkUp(string tunnelAdapterName = "DeltaTor")
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.Name.Equals(tunnelAdapterName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                if (nic.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
                    nic.Description.Contains("TAP", StringComparison.OrdinalIgnoreCase) ||
                    nic.Description.Contains("TUN", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;

                var props = nic.GetIPProperties();
                var hasIpv4 = false;
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        hasIpv4 = true;
                        break;
                    }
                }
                if (!hasIpv4) continue;

                return true;
            }
            return false;
        }
        catch
        {
            // Fail open: a broken enumeration must never look like "no network".
            return true;
        }
    }
}