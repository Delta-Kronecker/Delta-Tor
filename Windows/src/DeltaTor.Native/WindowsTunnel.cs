using System.Text;
using DeltaTor.Core;
using DeltaTor.Core.Util;

namespace DeltaTor.Native;

/// <summary>
/// The Windows counterpart of Android's <c>HevSocks5Tunnel</c> object, wired
/// into <see cref="TunnelEngine.Impl"/> at startup.
///
/// Call sequence mirrors hev_jni.c exactly: start runs
/// <c>hev_socks5_tunnel_main_from_str</c> on a dedicated thread (the call
/// blocks for the tunnel's lifetime), stop calls <c>hev_socks5_tunnel_quit</c>
/// and joins the thread with Android's 3000 ms budget, and stats feeds the
/// same tx/rx byte counters the UI speed and totals read. The YAML config is
/// the Android buildConfig output, unchanged — plus a Windows-only
/// <c>name:</c> so the Wintun adapter gets a stable name (Android lets the
/// kernel pick tun0; the Windows backend passes the name straight to
/// WintunCreateAdapter and cannot take null).
///
/// Routing is this class's extra job: Android's VpnService installs the
/// routes and keeps this app's own sockets out of the tunnel automatically;
/// on Windows <see cref="RouteManager"/> does both.
/// </summary>
public sealed class WindowsTunnel : ISocksTunnel
{
    private const string Tag = "WindowsTunnel";

    // Android HevSocks5Tunnel.kt defaults: disableQuic = true,
    // rejectNonDnsUdp = false. The service exposes no knobs for either.
    private const int DisableQuic = 1;
    private const int RejectNonDnsUdp = 0;

    private Thread? _thread;
    private byte[]? _config;

    public Exception? Start(
        string socksAddress,
        int socksPort,
        bool enableUdpTunneling,
        int mtu,
        string ipv4Address)
    {
        try
        {
            if (_thread is { IsAlive: true })
            {
                // HevSocks5Tunnel.kt: "Tunnel already running, stopping first..."
                Stop();
                Thread.Sleep(500);
            }

            _config = Encoding.UTF8.GetBytes(BuildConfig(socksAddress, socksPort, enableUdpTunneling, mtu, ipv4Address));

            HevTunnelApi.SetRejectQuic(DisableQuic);
            HevTunnelApi.SetRejectNonDnsUdp(RejectNonDnsUdp);

            var config = _config;
            _thread = new Thread(() =>
            {
                var rc = HevTunnelApi.MainFromStr(config, (uint)config.Length, tunFd: -1);
                if (rc != 0)
                    AppLog.E(Tag, $"hev-socks5-tunnel exited with code {rc}");
                else
                    AppLog.D(Tag, "hev-socks5-tunnel thread exited");
            })
            {
                IsBackground = true,
                Name = "hev-socks5-tunnel",
            };
            _thread.Start();

            // Android's nativeStart only reports that the thread came up. Give
            // the config parse a beat anyway, so a failure that shows up
            // immediately fails the connect instead of leaving a dead tunnel
            // behind a connected state.
            Thread.Sleep(300);
            if (!_thread.IsAlive)
                return new InvalidOperationException("hev-socks5-tunnel failed to start (see log)");

            var routeEx = RouteManager.InstallTunnel();
            if (routeEx != null)
            {
                Stop();
                return routeEx;
            }

            AppLog.I(Tag, $"tunnel up: socks5 {socksAddress}:{socksPort}, mtu {mtu}, ipv4 {ipv4Address}");
            return null;
        }
        catch (Exception e)
        {
            // DllNotFoundException (native pieces missing from the build) and
            // friends must fail the connect, not crash the service flow.
            AppLog.E(Tag, "tunnel start failed", e);
            try { Stop(); }
            catch { /* already down */ }
            return e;
        }
    }

    public void Stop()
    {
        RouteManager.StopWatching();
        var thread = _thread;
        _thread = null;
        if (thread is { IsAlive: true })
        {
            try { HevTunnelApi.Quit(); }
            catch (Exception e) { AppLog.W(Tag, $"quit failed: {e.Message}"); }
            if (!thread.Join(3000))
                AppLog.W(Tag, "tunnel thread did not stop within 3000 ms");
        }
        try { RouteManager.RemoveTunnel(); }
        catch (Exception e) { AppLog.W(Tag, $"route removal failed: {e.Message}"); }
        _config = null;
    }

    public TunnelStats? GetStats()
    {
        if (_thread is not { IsAlive: true }) return null;
        try
        {
            HevTunnelApi.GetStats(out _, out var txBytes, out _, out var rxBytes);
            return new TunnelStats((long)txBytes, (long)rxBytes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>HevSocks5Tunnel.kt buildConfig — same fields, same values, same order.</summary>
    private static string BuildConfig(
        string socksAddress,
        int socksPort,
        bool enableUdpTunneling,
        int mtu,
        string ipv4Address)
    {
        var sb = new StringBuilder();

        sb.AppendLine("tunnel:");
        // Windows-only: a stable Wintun adapter name (see the type docs).
        sb.AppendLine($"  name: {RouteManager.AdapterName}");
        sb.AppendLine($"  mtu: {mtu}");
        sb.AppendLine($"  ipv4: {ipv4Address}");
        sb.AppendLine("  ipv6: fd00::1");
        sb.AppendLine();

        sb.AppendLine("socks5:");
        sb.AppendLine($"  address: {socksAddress}");
        sb.AppendLine($"  port: {socksPort}");
        // UDP tunneling via 'tcp' mode sends FWD_UDP (cmd 0x05) to the SOCKS5
        // proxy — the same comment and value as on Android.
        if (enableUdpTunneling)
            sb.AppendLine("  udp: 'tcp'");
        sb.AppendLine();

        sb.AppendLine("misc:");
        sb.AppendLine("  task-stack-size: 32768");
        sb.AppendLine("  connect-timeout: 8000");
        sb.AppendLine("  tcp-read-write-timeout: 120000");
        sb.AppendLine("  udp-read-write-timeout: 60000");
        sb.AppendLine("  log-level: warning");

        return sb.ToString();
    }
}
