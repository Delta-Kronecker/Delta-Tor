using System.Runtime.InteropServices;
using DeltaTor.Core.Util;
using Microsoft.Win32;

namespace DeltaTor.Core;

/// <summary>
/// Points WinINET (the per-user Windows proxy Edge/Chrome/IE read) at the
/// local HTTP→SOCKS bridge (<see cref="HttpSocksBridge"/>), which forwards to
/// Tor. Applied only in System proxy mode after bootstrap reaches 100% with
/// the "No VPN and System Proxy" toggle off; the values that were there
/// before are saved once and restored when the proxy is cleared (disconnect,
/// teardown, mode change to Tun, app exit).
/// </summary>
public static class SystemProxy
{
    private const string KeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    private static readonly object Lock = new();
    private static bool _applied;
    private static bool _prevEnabled;
    private static string _prevServer = "";

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(
        IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    /// <summary>True while this app owns the machine proxy (must be cleared later).</summary>
    public static bool IsApplied
    {
        get { lock (Lock) return _applied; }
    }

    /// <summary>Enable the machine proxy at host:port (HTTP-proxy form).
    /// No-op-safe on failure.</summary>
    public static void Apply(string host, int port)
    {
        lock (Lock)
        {
            try
            {
                // WinINET only speaks the HTTP-proxy protocol to this address
                // (CONNECT / absolute-form), while Tor speaks SOCKS5 — so the
                // machine points at the local bridge, not at Tor's SOCKS port.
                if (!HttpSocksBridge.Start())
                {
                    AppLog.W("SystemProxy", "HTTP bridge is down; system proxy not set");
                    return;
                }
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
                if (key == null)
                {
                    AppLog.W("SystemProxy", "Internet Settings key missing; system proxy not set");
                    HttpSocksBridge.Stop();
                    return;
                }
                if (!_applied)
                {
                    _prevEnabled = key.GetValue("ProxyEnable") is int pe && pe != 0;
                    _prevServer = key.GetValue("ProxyServer") as string ?? "";
                }
                // Bare host:port — the HTTP-proxy form the Windows settings
                // page expects: Address 127.0.0.1, Port 9070. No scheme, no
                // "socks=" prefix (which Windows shows as garbage and many
                // apps turn into "http://socks=…").
                var target = $"{host}:{HttpSocksBridge.Port}";
                key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                key.SetValue("ProxyServer", target, RegistryValueKind.String);
                NotifyWinInet();
                _applied = true;
                AppLog.I("SystemProxy",
                    $"System proxy set to {target} (HTTP bridge → socks {host}:{port})");
            }
            catch (Exception e)
            {
                AppLog.W("SystemProxy", $"Failed to set system proxy: {e.Message}");
            }
        }
    }

    /// <summary>Put the pre-app values back and stop the bridge. No-op when
    /// nothing was applied.</summary>
    public static void Clear()
    {
        lock (Lock)
        {
            if (!_applied) return;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
                if (key != null)
                {
                    key.SetValue("ProxyEnable", _prevEnabled ? 1 : 0, RegistryValueKind.DWord);
                    key.SetValue("ProxyServer", _prevServer, RegistryValueKind.String);
                }
                NotifyWinInet();
                AppLog.I("SystemProxy", "System proxy restored to the previous value");
            }
            catch (Exception e)
            {
                AppLog.W("SystemProxy", $"Failed to restore system proxy: {e.Message}");
            }
            finally
            {
                _applied = false;
                HttpSocksBridge.Stop();
            }
        }
    }

    /// <summary>Broadcast the registry change to running apps (WinINET).</summary>
    private static void NotifyWinInet()
    {
        InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }
}
