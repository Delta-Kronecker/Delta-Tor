using System.Runtime.InteropServices;

namespace DeltaTor.Native;

/// <summary>
/// P/Invoke surface of hev-socks5-tunnel.dll — the tun2socks library built
/// from the vendored source by <c>Windows/native/build-hev.sh</c>, with its
/// Windows backend (hev-tunnel-windows.c) creating the Wintun adapter itself.
///
/// The exports mirror the Android JNI wrapper (hev_jni.c / HevSocks5Tunnel.kt)
/// one for one: main_from_str runs the tunnel for the lifetime of the calling
/// thread, quit stops it, stats reports the byte counters the UI shows, and
/// the two set_reject_* toggles carry the Android defaults (QUIC rejected,
/// non-DNS UDP kept).
/// </summary>
internal static class HevTunnelApi
{
    private const string Lib = "hev-socks5-tunnel.dll";

    /// <summary>hev_jni.c: runs hev_socks5_tunnel_main_from_str on this thread; blocks until quit.</summary>
    /// <param name="config">UTF-8 YAML config (the Android buildConfig output, plus the Windows-only adapter name).</param>
    /// <param name="configLen">Byte length of <paramref name="config"/>.</param>
    /// <param name="tunFd">-1 on Windows: the library opens the Wintun adapter itself.</param>
    /// <returns>0 on a clean run, negative on config/init failure.</returns>
    [DllImport(Lib, EntryPoint = "hev_socks5_tunnel_main_from_str", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int MainFromStr(byte[] config, uint configLen, int tunFd);

    /// <summary>hev_jni.c nativeStop: asks the tunnel thread to exit (join it yourself).</summary>
    [DllImport(Lib, EntryPoint = "hev_socks5_tunnel_quit", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Quit();

    /// <summary>hev_jni.c nativeGetStats: tx/rx packet and byte counters (the UI speed/total source).</summary>
    [DllImport(Lib, EntryPoint = "hev_socks5_tunnel_stats", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void GetStats(out nuint txPackets, out nuint txBytes, out nuint rxPackets, out nuint rxBytes);

    /// <summary>HevSocks5Tunnel.kt nativeSetRejectQuic(disableQuic=true).</summary>
    [DllImport(Lib, EntryPoint = "hev_socks5_tunnel_set_reject_quic", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SetRejectQuic(int enabled);

    /// <summary>HevSocks5Tunnel.kt nativeSetRejectNonDnsUdp(rejectNonDnsUdp=false).</summary>
    [DllImport(Lib, EntryPoint = "hev_socks5_tunnel_set_reject_non_dns_udp", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SetRejectNonDnsUdp(int enabled);
}
