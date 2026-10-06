// WindowsNetStats.cs - live byte counters for the DeltaTor TUN adapter.
// DeltaTor Core License v1.0 (see LICENSE). Using this Core in another program
// requires the mandatory attribution of https://github.com/Delta-Kronecker/DeltaTor.
//
// Android counts traffic inside its own TUN sniffer (HevSocks5Tunnel + the
// protocol sniffer). Windows has no process of ours in the data path: zeptun
// owns the adapter and feeds tor over SOCKS, and the zeptun state file carries
// only status/pid/relays/socks - no counters. So the adapter's own interface
// counters are the only honest source of "how much has moved".
//
// Everything here is best effort. When the adapter is absent (proxy-only mode,
// or TUN never started) the probe reports false and the UI shows "--", exactly
// the way Android shows "--" while the tunnel is not up.
using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;

namespace StartTor
{
    internal static class WindowsNetStats
    {
        private static readonly object gate = new object();
        private static List<NetworkInterface> cached = null;
        private static DateTime cachedAt = DateTime.MinValue;

        private static List<NetworkInterface> Adapters()
        {
            // The adapter set barely changes; re-enumerating it on every 250 ms
            // UI tick would be wasteful.
            lock (gate)
            {
                if (cached != null && (DateTime.Now - cachedAt).TotalSeconds < 5.0)
                    return cached;
                try
                {
                    cached = new List<NetworkInterface>(NetworkInterface.GetAllNetworkInterfaces());
                    cachedAt = DateTime.Now;
                }
                catch
                {
                    if (cached == null) cached = new List<NetworkInterface>();
                }
                return cached;
            }
        }

        private static bool NameMatches(NetworkInterface ni, string wanted)
        {
            if (ni == null) return false;
            try
            {
                if (string.Equals(ni.Name, wanted, StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(ni.Description, wanted, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            return false;
        }

        // Reports the adapter's lifetime counters. These are cumulative since
        // the adapter came up, so the UI keeps its own session base to show
        // per-session totals and derives speed from successive deltas.
        internal static bool TryGetCounters(string adapterName,
            out long rxBytes, out long txBytes)
        {
            rxBytes = 0;
            txBytes = 0;
            try
            {
                foreach (NetworkInterface ni in Adapters())
                {
                    if (!NameMatches(ni, adapterName)) continue;
                    IPv4InterfaceStatistics st = ni.GetIPv4Statistics();
                    if (st == null) return false;
                    rxBytes = st.BytesReceived;
                    txBytes = st.BytesSent;
                    return true;
                }
            }
            catch { }
            return false;
        }

        internal static bool IsUp(string adapterName)
        {
            try
            {
                foreach (NetworkInterface ni in Adapters())
                {
                    if (!NameMatches(ni, adapterName)) continue;
                    return ni.OperationalStatus == OperationalStatus.Up;
                }
            }
            catch { }
            return false;
        }
    }
}