using System.Diagnostics;
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
        try
        {
            // exceptionInfo is PEXCEPTION_POINTERS: ExceptionRecord (ptr) at
            // +0, ContextRecord (ptr) at +8. EXCEPTION_RECORD has
            // ExceptionCode (uint) at +0 and ExceptionAddress (void*) at +8.
            var record = Marshal.ReadIntPtr(exceptionInfo, 0);
            var code = Marshal.ReadInt32(record, 0);
            var addr = Marshal.ReadIntPtr(record, 8);
            Console.Error.WriteLine($"NATIVE FAULT: code=0x{code:X8} address=0x{addr.ToInt64():X}");

            // For EXCEPTION_ACCESS_VIOLATION the authoritative bad address is
            // ExceptionInformation[1] (record+32); [0] (record+24) is 0=read
            // 1=write, NumberParameters (record+16) is 2 for an AV. This is
            // what actually faulted, independent of the CONTEXT registers.
            var numParams = Marshal.ReadInt32(record, 16);
            if (numParams >= 2)
            {
                var access = Marshal.ReadInt64(record, 24);
                var badAddr = Marshal.ReadInt64(record, 32);
                Console.Error.WriteLine($"NATIVE AV: access={(access == 1 ? "write" : "read")} bad=0x{badAddr:X} nparams={numParams}");
            }

            // x64 CONTEXT field offsets (Windows SDK winnt.h):
            //   Rax 0x78 Rcx 0x80 Rdx 0x88 Rbx 0x90 Rsp 0x98 Rbp 0xA0
            //   Rsi 0xA8 Rdi 0xB0 R8  0xB8 R9  0xC0 R10 0xC8 R11 0xD0
            //   R12 0xD8 R13 0xE0 R14 0xE8 R15 0xF0 Rip 0xF8
            var ctx = Marshal.ReadIntPtr(exceptionInfo, 8);
            string[] names = { "rax", "rcx", "rdx", "rbx", "rsp", "rbp", "rsi", "rdi",
                               "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15", "rip" };
            int[] offs = { 0x78, 0x80, 0x88, 0x90, 0x98, 0xA0, 0xA8, 0xB0,
                           0xB8, 0xC0, 0xC8, 0xD0, 0xD8, 0xE0, 0xE8, 0xF0, 0xF8 };
            var regs = new long[names.Length];
            var sb = new System.Text.StringBuilder("NATIVE CTX:");
            for (int i = 0; i < names.Length; i++)
            {
                regs[i] = Marshal.ReadInt64(ctx, offs[i]);
                sb.Append(' ').Append(names[i]).Append("=0x").Append(regs[i].ToString("X"));
            }
            Console.Error.WriteLine(sb.ToString());
            long rip = regs[16];

            // Raw instruction bytes at RIP so the faulting instruction can be
            // disassembled locally (objdump -b binary -m i386:x86-64) even
            // though the DLL only exists in CI.
            var bytes = new byte[24];
            Marshal.Copy((IntPtr)rip, bytes, 0, bytes.Length);
            Console.Error.WriteLine("NATIVE BYTES: " + string.Concat(bytes.Select(b => b.ToString("x2"))));

            // Which module owns RIP. GetModuleHandleExW(FROM_ADDRESS) proved
            // unreliable here (err=87), so resolve against the module list
            // captured at startup via System.Diagnostics.
            long ripRva = -1;
            string ripMod = "?";
            foreach (var m in s_modules)
            {
                if (rip >= m.Base && rip < m.Base + m.Size)
                {
                    ripMod = m.Name;
                    ripRva = rip - m.Base;
                    break;
                }
            }
            Console.Error.WriteLine($"NATIVE MOD: {ripMod} rip_rva=0x{ripRva:X}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"NATIVE VEX-ERROR: {ex.GetType().Name}: {ex.Message}");
        }
        return 0; // EXCEPTION_CONTINUE_SEARCH
    }

    private sealed record LoadedModule(long Base, long Size, string Name);

    private static LoadedModule[] s_modules = Array.Empty<LoadedModule>();

    /// <summary>
    /// Snapshot every loaded module once, at startup. The crash handler runs
    /// on a faulting thread inside a dying process, so it must not call into
    /// the loader or allocate; comparing against this plain array is safe.
    /// </summary>
    private static void CaptureModules()
    {
        var list = new List<LoadedModule>();
        try
        {
            foreach (ProcessModule pm in Process.GetCurrentProcess().Modules)
                list.Add(new LoadedModule(pm.BaseAddress.ToInt64(), pm.ModuleMemorySize, pm.ModuleName ?? "?"));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"CaptureModules failed: {ex.Message}");
        }
        s_modules = list.ToArray();
    }

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

        // After the first P/Invoke, hev-socks5-tunnel.dll (and wintun.dll,
        // loaded later by the tunnel) are mapped; snapshot the module list
        // now so the crash handler can resolve faulting addresses without
        // touching the loader.
        CaptureModules();

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
