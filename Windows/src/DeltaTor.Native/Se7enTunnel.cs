using System.Net.NetworkInformation;
using DeltaTor.Core;
using DeltaTor.Core.Util;
using Se7enPro.Models;
using Se7enPro.Services;
using Se7enTun;

namespace DeltaTor.Native;

/// <summary>
/// <see cref="ISocksTunnel"/> over Se7enTun's <see cref="TunHost"/> — the
/// WinTUN + tun2socks engine from Se7en-Pro.
///
/// Loop prevention: Android keeps this app's own sockets out of the tunnel
/// with <c>addDisallowedApplication(packageName)</c> because Tor runs in the
/// same process. On Windows Tor is a separate process, so the equivalent is
/// Se7enTun's per-process split rules in exclude mode — <c>tor</c>,
/// <c>lyrebird</c> and <c>DeltaTor</c> keep reaching relays over the physical
/// default route instead of re-entering the tunnel and deadlocking against
/// their own outbound connections.
/// </summary>
public sealed class Se7enTunnel : ISocksTunnel
{
    private const string Tag = "Se7enTunnel";

    /// <summary>Wintun adapter alias Se7enTun creates ("se7en_tun").</summary>
    private const string AdapterAlias = "se7en_tun";

    /// <summary>Processes kept outside the tunnel (Windows' addDisallowedApplication).</summary>
    private static readonly string[] ExcludedProcesses = { "tor", "lyrebird", "DeltaTor" };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private TunHost? _host;

    public Exception? Start(string socksAddress, int socksPort)
    {
        _gate.Wait();
        try
        {
            StopCore();

            var host = new TunHost(new TunOptions
            {
                SocksHost = string.IsNullOrWhiteSpace(socksAddress) ? "127.0.0.1" : socksAddress,
                SocksPort = socksPort,
                Log = line => AppLog.D(Tag, line),
            });

            if (!host.IsElevated)
            {
                DisposeQuietly(host);
                return new InvalidOperationException(
                    "Se7enTun needs Administrator privileges; relaunch DeltaTor elevated.");
            }

            var settings = host.Settings;
            settings.SplitTunnelEnabled = true;
            settings.SplitTunnelMode = "exclude";
            settings.SplitTunnelEntries.Clear();
            foreach (var name in ExcludedProcesses)
                settings.SplitTunnelEntries.Add(new SplitTunnelEntry { Kind = "app", Value = name });
            host.Save();

            host.SetConnected(true);

            var started = Task.Run(() => host.StartAsync(TimeSpan.FromSeconds(60)))
                .GetAwaiter()
                .GetResult();

            if (!started)
            {
                var reason = host.LastError ?? "Se7enTun failed to start (no error reported)";
                DisposeQuietly(host);
                return new InvalidOperationException(reason);
            }

            _host = host;
            AppLog.I(Tag, $"tunnel up: socks5 {socksAddress}:{socksPort}, adapter {AdapterAlias}");
            return null;
        }
        catch (Exception e)
        {
            AppLog.E(Tag, "tunnel start failed", e);
            StopCore();
            return e;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Stop()
    {
        _gate.Wait();
        try { StopCore(); }
        finally { _gate.Release(); }
    }

    public TunnelStats? GetStats()
    {
        var host = _host;
        if (host is null || host.State != TunState.Running) return null;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!string.Equals(nic.Name, AdapterAlias, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(nic.Description, AdapterAlias, StringComparison.OrdinalIgnoreCase))
                    continue;
                var st = nic.GetIPStatistics();
                return new TunnelStats((long)st.BytesSent, (long)st.BytesReceived);
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private void StopCore()
    {
        var host = _host;
        _host = null;
        if (host is null) return;
        try
        {
            Task.Run(() => host.StopAsync(TimeSpan.FromSeconds(30)))
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception e)
        {
            AppLog.W(Tag, $"tunnel stop failed: {e.Message}");
        }
        DisposeQuietly(host);
        AppLog.I(Tag, "tunnel stopped");
    }

    private static void DisposeQuietly(TunHost host)
    {
        try
        {
            Task.Run(async () => await host.DisposeAsync()).GetAwaiter().GetResult();
        }
        catch
        {
            // Ignore
        }
    }
}