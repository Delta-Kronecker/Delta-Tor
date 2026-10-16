using System.Runtime.InteropServices;
using System.Text;

namespace NativeSmoke;

/// <summary>
/// CI gate for the MinGW port of hev-socks5-tunnel.dll: starts the real
/// tunnel in this console process (config parse, task system, IOCP reactor,
/// lwip, Wintun adapter open), checks stats, quits, and returns the tunnel's
/// own exit code. No routes are installed, so the runner's connectivity is
/// untouched; traffic never flows (no SOCKS5 server is contacted at startup).
/// </summary>
internal static class Program
{
    // Print raw native fault info before the process dies, so a crash inside
    // the DLL (coroutine switch, IOCP reactor, ...) is attributable to an
    // offset rather than just "exit code -1073741819".
    private delegate int VexHandler(IntPtr exceptionInfo);

    [DllImport("kernel32.dll")]
    private static extern IntPtr AddVectoredExceptionHandler(uint firstHandler, VexHandler handler);

    private static readonly VexHandler Vex = VexHandlerImpl;

    private static int VexHandlerImpl(IntPtr exceptionInfo)
    {
        // exceptionInfo is PEXCEPTION_POINTERS: ExceptionRecord (ptr) at +0,
        // ContextRecord (ptr) at +8. The EXCEPTION_RECORD itself has
        // ExceptionCode (uint) at +0 and ExceptionAddress (void*) at +8.
        var record = Marshal.ReadIntPtr(exceptionInfo, 0);
        var code = Marshal.ReadInt32(record, 0);
        var addr = Marshal.ReadIntPtr(record, 8);
        Console.Error.WriteLine($"NATIVE FAULT: code=0x{code:X8} address=0x{addr.ToInt64():X}");
        if (GetModuleHandleExW(0x3 /* GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS */, addr, out var mod))
        {
            var sb = new StringBuilder(260);
            GetModuleFileNameW(mod, sb, sb.Capacity);
            Console.Error.WriteLine($"NATIVE FAULT module: {sb}");
        }
        return 0; // EXCEPTION_CONTINUE_SEARCH
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetModuleHandleExW(uint flags, IntPtr lpModuleName, out IntPtr phModule);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetModuleFileNameW(IntPtr hModule, StringBuilder lpFilename, int nSize);

    [DllImport("hev-socks5-tunnel.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void hev_socks5_tunnel_set_reject_quic(int enabled);

    [DllImport("hev-socks5-tunnel.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void hev_socks5_tunnel_set_reject_non_dns_udp(int enabled);

    [DllImport("hev-socks5-tunnel.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int hev_socks5_tunnel_main_from_str(byte[] config, uint configLen, int tunFd);

    [DllImport("hev-socks5-tunnel.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void hev_socks5_tunnel_quit();

    [DllImport("hev-socks5-tunnel.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void hev_socks5_tunnel_stats(out nuint txPackets, out nuint txBytes, out nuint rxPackets, out nuint rxBytes);

    private static int Main()
    {
        AddVectoredExceptionHandler(1, Vex);
        Console.WriteLine($"BaseDirectory: {AppContext.BaseDirectory}");

        try
        {
            hev_socks5_tunnel_set_reject_quic(1);
            hev_socks5_tunnel_set_reject_non_dns_udp(0);
            Console.WriteLine("set_reject_quic / set_reject_non_dns_udp OK");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"initial P/Invoke failed: {e}");
            return 1;
        }

        var bytes = Encoding.UTF8.GetBytes(BuildConfig());
        var rc = -1;
        var thread = new Thread(() =>
        {
            rc = hev_socks5_tunnel_main_from_str(bytes, (uint)bytes.Length, tunFd: -1);
        })
        {
            IsBackground = true,
            Name = "hev-socks5-tunnel",
        };

        thread.Start();
        Thread.Sleep(5000);

        if (!thread.IsAlive)
        {
            Console.Error.WriteLine($"tunnel thread exited during startup, rc={rc}");
            return 2;
        }

        hev_socks5_tunnel_stats(out _, out var txBytes, out _, out var rxBytes);
        Console.WriteLine($"tunnel up: stats tx={txBytes} rx={rxBytes}");

        hev_socks5_tunnel_quit();
        if (!thread.Join(8000))
        {
            Console.Error.WriteLine("tunnel thread did not stop after quit");
            return 3;
        }

        Console.WriteLine($"stopped cleanly, rc={rc}");
        return rc == 0 ? 0 : 4;
    }

    private static string BuildConfig()
    {
        // Mirrors WindowsTunnel.BuildConfig (name/mtu/ipv4 match what the app
        // asks for; the socks5 endpoint is never dialed during this test).
        var sb = new StringBuilder();
        sb.AppendLine("tunnel:");
        sb.AppendLine("  name: DeltaTor");
        sb.AppendLine("  mtu: 1500");
        sb.AppendLine("  ipv4: 10.88.0.2");
        sb.AppendLine("  ipv6: fd00::1");
        sb.AppendLine();
        sb.AppendLine("socks5:");
        sb.AppendLine("  address: 127.0.0.1");
        sb.AppendLine("  port: 19050");
        sb.AppendLine("  udp: 'tcp'");
        sb.AppendLine();
        sb.AppendLine("misc:");
        sb.AppendLine("  task-stack-size: 32768");
        sb.AppendLine("  connect-timeout: 8000");
        sb.AppendLine("  tcp-read-write-timeout: 120000");
        sb.AppendLine("  udp-read-write-timeout: 60000");
        sb.AppendLine("  log-level: debug");
        return sb.ToString();
    }
}
