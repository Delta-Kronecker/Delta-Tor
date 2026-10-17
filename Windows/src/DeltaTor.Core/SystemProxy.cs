using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DeltaTor.Core;

/// <summary>
/// Points WinINET (the per-user Windows proxy Edge/Chrome/IE read) at Tor's
/// local SOCKS5 listener. Applied only in System proxy mode after bootstrap
/// reaches 100% with the "No VPN &amp; Auto Proxy" toggle off; the values that
/// were there before are saved once and restored when the proxy is cleared
/// (disconnect, teardown, mode change to Tun, app exit).
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

    /// <summary>Enable the machine proxy at socks=host:port. No-op-safe on failure.</summary>
    public static void Apply(string host, int port)
    {
        lock (Lock)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
                if (key == null)
                {
                    AppLog.W("SystemProxy", "Internet Settings key missing; system proxy not set");
                    return;
                }
                if (!_applied)
                {
                    _prevEnabled = key.GetValue("ProxyEnable") is int pe && pe != 0;
                    _prevServer = key.GetValue("ProxyServer") as string ?? "";
                }
                // WinINET reads "socks=host:port" as a SOCKS proxy; a bare
                // host:port would be taken for an HTTP proxy and Tor's SOCKS
                // would never see the traffic.
                key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                key.SetValue("ProxyServer", $"socks={host}:{port}", RegistryValueKind.String);
                NotifyWinInet();
                _applied = true;
                AppLog.I("SystemProxy", $"System proxy set to socks={host}:{port}");
            }
            catch (Exception e)
            {
                AppLog.W("SystemProxy", $"Failed to set system proxy: {e.Message}");
            }
        }
    }

    /// <summary>Put the pre-app values back. No-op when nothing was applied.</summary>
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
