// zeptun-helper.cs - builds to data\zeptun-helper.exe (elevated, windowed).
// DeltaTor Core License v1.0 (see LICENSE). Using this Core in another program
// requires the mandatory attribution of https://github.com/Delta-Kronecker/DeltaTor.
// Elevated TUN supervisor for DeltaTor. It runs data\zeptun.exe (the zeptun
// engine, official pinned release) on the "DeltaTor" Wintun adapter and keeps
// it alive: the keeper watches data\zeptun-stop.txt (written by the launcher
// when the user toggles TUN off or exits) and the tor SOCKS socket, and tears
// everything down cleanly (zeptun's own route/DNS/filter teardown; adapter
// deletion + DNS flush as a belt-and-braces fallback).
//
// The tunnel proxies the WHOLE system to 127.0.0.1:<socks> (tor). tor itself
// runs outside the tunnel: its relay/bridge IPs (parsed from the cached
// consensus + data\bridges) are written to an --exclude-file so zeptun gives
// them bypass routes over the physical gateway. Without that, tor's outbound
// connections would be captured by the tunnel and loop forever.
//
// Commands:
//   on <socksPort>    start the keeper (long running; this is the elevated
//                     session that owns zeptun until torn down)
//   off               write the stop file; if the owner is gone, stop zeptun
//                     directly and clean up the adapter
//   status            print the current state from data\zeptun-state.txt
//
// The exe embeds zeptun-helper.manifest (requireAdministrator) because Wintun,
// route and firewall operations need elevation; the launcher spawns it with
// the `runas` verb (no UAC re-prompt when the caller is already elevated).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace StartTor
{
    internal static class ZeptunHelper
    {
        private static readonly string DataDir = ResolveDataDir();
        private static string ResolveDataDir()
        {
            string env = Environment.GetEnvironmentVariable("DELTATOR_DATA_DIR");
            if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "zeptun.exe"))) return env;
            string d = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (File.Exists(Path.Combine(d, "zeptun.exe"))) return d;
            if (!string.IsNullOrEmpty(env)) return env;
            return d;
        }

        private const string TunName = "DeltaTor";
        private const string TunAddr = "172.19.0.1/30";
        private const int TunMtu = 1500;
        private const int CheckPeriodMs = 4000;   // keeper loop period
        private const int TorDownLimit = 4;       // misses before teardown
        private const uint CtrlBreakEvent = 1;    // CTRL_BREAK_EVENT

        private static readonly string ZeptunExe = Path.Combine(DataDir, "zeptun.exe");
        private static readonly string WintunDll = Path.Combine(DataDir, "wintun.dll");
        private static readonly string StateFile = Path.Combine(DataDir, "zeptun-state.txt");
        private static readonly string StopFile = Path.Combine(DataDir, "zeptun-stop.txt");
        private static readonly string ResultFile = Path.Combine(DataDir, "zeptun-result.txt");
        private static readonly string ExcludeFile = Path.Combine(DataDir, "zeptun-exclude.txt");
        private static readonly string ZeptunLog = Path.Combine(DataDir, "zeptun.log");
        private static readonly object StateLock = new object();

        // ---- state files ---------------------------------------------------
        private static string ReadState()
        {
            try { if (File.Exists(StateFile)) return File.ReadAllText(StateFile); }
            catch { }
            return "";
        }
        private static string StateValue(string key)
        {
            foreach (string ln in ReadState().Split('\n'))
            {
                int eq = ln.IndexOf('=');
                if (eq < 0) continue;
                if (ln.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    return ln.Substring(eq + 1).Trim();
            }
            return "";
        }
        private static void WriteState(string content)
        {
            lock (StateLock)
            {
                try { File.WriteAllText(StateFile, content, new UTF8Encoding(false)); }
                catch { }
            }
        }
        private static void WriteResult(string msg)
        {
            lock (StateLock)
            {
                try { File.WriteAllText(ResultFile, msg, new UTF8Encoding(false)); }
                catch { }
            }
        }

        // ---- relay exclusion list -------------------------------------------
        // Relay IPs from the cached consensus (microdesc + full) and every
        // bridge list in data\bridges. The first bootstrap is always done
        // WITHOUT the tunnel (DeltaTor connects first, TUN is optional), so the
        // cached consensus already covers the relays tor may pick; the same
        // limitation existed in the xray-based TUN design.
        private static HashSet<string> ParseRelayIps()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            string bridgesDir = Path.Combine(DataDir, "bridges");
            foreach (string f in new[]
            {
                Path.Combine(DataDir, "data", "cached-microdesc-consensus"),
                Path.Combine(DataDir, "data", "cached-consensus")
            })
            {
                if (!File.Exists(f)) continue;
                try
                {
                    foreach (string raw in File.ReadAllLines(f))
                    {
                        if (!raw.StartsWith("r ")) continue;
                        string[] parts = raw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        for (int i = 5; i <= 6 && i < parts.Length; i++)
                            if (IsPublicIpv4(parts[i])) set.Add(parts[i]);
                    }
                }
                catch { }
            }
            if (Directory.Exists(bridgesDir))
            {
                try
                {
                    foreach (string f in Directory.GetFiles(bridgesDir, "*.txt"))
                    {
                        foreach (string raw in File.ReadAllLines(f))
                        {
                            foreach (Match m in Regex.Matches(raw, @"\b(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})\b"))
                                if (IsPublicIpv4(m.Groups[1].Value)) set.Add(m.Groups[1].Value);
                        }
                    }
                }
                catch { }
            }
            return set;
        }

        private static bool IsPublicIpv4(string s)
        {
            string[] p = s.Split('.');
            if (p.Length != 4) return false;
            byte[] b = new byte[4];
            for (int i = 0; i < 4; i++)
                if (!byte.TryParse(p[i], out b[i])) return false;
            if (b[0] == 10 || b[0] == 127) return false;                                  // private / loopback
            if (b[0] == 172 && (b[1] & 0xf0) == 16) return false;                          // 172.16-31
            if (b[0] == 192 && b[1] == 168) return false;                                  // 192.168
            if (b[0] == 169 && b[1] == 254) return false;                                  // link-local
            if (b[0] == 100 && (b[1] & 0xc0) == 64) return false;                          // CGNAT 100.64/10
            if (b[0] == 0 || b[0] == 255) return false;
            return true;
        }

        private static void WriteExcludeFile(HashSet<string> relays)
        {
            var lines = new List<string>();
            foreach (string ip in relays) lines.Add(ip);     // bare IPs parse as /32
            lines.Add("10.0.0.0/8");
            lines.Add("172.16.0.0/12");
            lines.Add("192.168.0.0/16");
            lines.Add("169.254.0.0/16");
            try { File.WriteAllLines(ExcludeFile, lines, new UTF8Encoding(false)); }
            catch { }
        }

        // ---- tor / adapter checks -------------------------------------------
        private static bool TorUp(int socksPort, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    using (TcpClient c = new TcpClient())
                    {
                        IAsyncResult ar = c.BeginConnect(IPAddress.Loopback, socksPort, null, null);
                        if (ar.AsyncWaitHandle.WaitOne(800) && c.Connected) return true;
                    }
                }
                catch { }
                Thread.Sleep(200);
            }
            return false;
        }

        private static bool AdapterUp()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh.exe",
                    Arguments = "interface show interface",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                using (Process p = Process.Start(psi))
                {
                    string outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    return outp.IndexOf(TunName, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        // ---- wintun adapter deletion (hard-kill fallback only) ----------------
        private delegate int WintunDeleteAdapterFn(string name);
        private static readonly WintunDeleteAdapterFn WintunDelete =
            LoadWintunDelete();

        private static WintunDeleteAdapterFn LoadWintunDelete()
        {
            try
            {
                IntPtr h = LoadLibrary(WintunDll);
                if (h == IntPtr.Zero) return null;
                IntPtr fn = GetProcAddress(h, "WintunDeleteAdapter");
                if (fn == IntPtr.Zero) return null;
                return (WintunDeleteAdapterFn)Marshal.GetDelegateForFunctionPointer(
                    fn, typeof(WintunDeleteAdapterFn));
            }
            catch { return null; }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr h, string name);

        private static void DeleteAdapterBestEffort()
        {
            try
            {
                if (WintunDelete != null) WintunDelete(TunName);
            }
            catch { }
        }

        // ---- zeptun process (its own console process group so we can signal
        // it with CTRL_BREAK for a graceful, route/filter-correct teardown) --
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcessW(
            string lpApplicationName, StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
            bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment,
            string lpCurrentDirectory, ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr h, int ms);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr h, uint exitCode);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr h);

        private const uint CREATE_NEW_PROCESS_GROUP = 0x200;
        private const int STARTF_USESHOWWINDOW = 0x1;
        private const int SW_HIDE = 0;
        private const long INFINITE = 0xffffffff;

        private static PROCESS_INFORMATION StartZeptun(string socksPort)
        {
            string args =
                "run --tun " + TunName +
                " --no-address --address " + TunAddr +
                " --mtu " + TunMtu +
                " --socks5 127.0.0.1:" + socksPort +
                " --socks5-no-udp" +
                " --auto-route --strict-route" +
                " --exclude-file \"" + ExcludeFile + "\"" +
                " --fake-ip --dns-hijack" +
                " --log-level warn" +
                " --log-file \"" + ZeptunLog + "\"" +
                " --pid-file \"" + Path.Combine(DataDir, "zeptun.pid") + "\"";
            var si = new STARTUPINFO
            {
                cb = Marshal.SizeOf(typeof(STARTUPINFO)),
                dwFlags = STARTF_USESHOWWINDOW,
                wShowWindow = SW_HIDE
            };
            PROCESS_INFORMATION pi;
            bool ok = CreateProcessW(null, new StringBuilder("\"" + ZeptunExe + "\" " + args),
                IntPtr.Zero, IntPtr.Zero, false, CREATE_NEW_PROCESS_GROUP,
                IntPtr.Zero, DataDir, ref si, out pi);
            if (!ok) throw new Exception("CreateProcessW failed (Win32 error " +
                Marshal.GetLastWin32Error() + ")");
            return pi;
        }

        // Graceful stop: CTRL_BREAK to zeptun's process group makes zeptun's
        // SetConsoleCtrlHandler run its own teardown (route removal, Wintun
        // adapter deletion, DNS restore). Hard-kill + adapter delete remain as
        // the fallback if it does not exit promptly.
        private static void StopZeptun(ref PROCESS_INFORMATION pi)
        {
            try { GenerateConsoleCtrlEvent(CtrlBreakEvent, pi.dwProcessId); }
            catch { }
            uint rc = WaitForSingleObject(pi.hProcess, 8000);
            if (rc != 0)
            {
                try { TerminateProcess(pi.hProcess, 1); } catch { }
                WaitForSingleObject(pi.hProcess, 4000);
                DeleteAdapterBestEffort();
            }
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
        }

        private static void FlushDns()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "ipconfig.exe",
                    Arguments = "/flushdns",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch { }
        }

        // ---- commands --------------------------------------------------------
        private static int CmdFallbackOff()
        {
            // The keeper is gone (or was never up): take down zeptun directly.
            int n;
            if (int.TryParse(StateValue("pid"), out n) && n > 0)
            {
                try
                {
                    Process z = Process.GetProcessById(n);
                    if (z != null && !z.HasExited)
                    {
                        var pi = new PROCESS_INFORMATION
                        {
                            hProcess = z.Handle,
                            hThread = IntPtr.Zero,
                            dwProcessId = (uint)n
                        };
                        StopZeptun(ref pi);
                    }
                }
                catch { }
            }
            DeleteAdapterBestEffort();
            FlushDns();
            WriteState("status=off");
            try { if (File.Exists(StopFile)) File.Delete(StopFile); } catch { }
            return 0;
        }

        private static int CmdOff()
        {
            try { File.WriteAllText(StopFile, "1", new UTF8Encoding(false)); } catch { }
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 12000)
            {
                if (StateValue("status") != "on") { WriteResult("off"); return 0; }
                Thread.Sleep(500);
            }
            return CmdFallbackOff();
        }

        private static int CmdStatus()
        {
            string st = StateValue("status");
            if (st != "on") st = "off";
            Console.WriteLine("status=" + st);
            return 0;
        }

        private static int CmdOn(string socksPort)
        {
            WriteResult("enabling...");
            if (!File.Exists(ZeptunExe)) { Fail("zeptun.exe not found in " + DataDir); return 1; }
            if (StateValue("status") == "on") { WriteResult("error: TUN is already on"); return 1; }
            if (!TorUpInt(socksPort, 2000))
            {
                Fail("tor SOCKS (127.0.0.1:" + socksPort + ") is not up - connect first");
                return 1;
            }

            // clean leftovers from a previous (crashed) elevated run
            foreach (Process p in Process.GetProcessesByName("zeptun"))
            {
                try
                {
                    if (p.MainModule.FileName.Equals(ZeptunExe, StringComparison.OrdinalIgnoreCase))
                        p.Kill();
                }
                catch { }
            }
            Thread.Sleep(300);
            DeleteAdapterBestEffort();

            var relays = ParseRelayIps();
            WriteExcludeFile(relays);

            // zeptun needs a console to receive CTRL_BREAK. Allocate our own
            // and hide the window; zeptun (spawned without CREATE_NEW_CONSOLE)
            // inherits it and, thanks to CREATE_NEW_PROCESS_GROUP, lives in its
            // own process group so the signal never touches us.
            try { AllocConsole(); } catch { }
            try { IntPtr cw = GetConsoleWindow(); if (cw != IntPtr.Zero) ShowWindow(cw, SW_HIDE); } catch { }

            PROCESS_INFORMATION pi;
            try
            {
                pi = StartZeptun(socksPort);
            }
            catch (Exception ex)
            {
                WriteState("status=off");
                Fail("cannot start zeptun: " + ex.Message);
                return 1;
            }

            // wait for the Wintun adapter (zeptun installs addresses/routes itself)
            bool up = false;
            for (int i = 0; i < 60; i++)
            {
                Thread.Sleep(500);
                uint rc = WaitForSingleObject(pi.hProcess, 0);
                if (rc == 0) { up = false; break; }        // 0 = WAIT_OBJECT_0 (exited)
                if (AdapterUp()) { up = true; break; }
            }
            if (!up)
            {
                StopZeptun(ref pi);
                Fail("TUN adapter did not come up; check data\\zeptun.log");
                return 1;
            }

            WriteState("status=on\r\npid=" + pi.dwProcessId +
                       "\r\nrelays=" + relays.Count +
                       "\r\nsocks=" + socksPort);
            WriteResult("on: TUN active (zeptun, " + relays.Count +
                        " relay IPs excluded, DNS fake-ip, UDP blocked)");
            FlushDns();

            int torDown = 0;
            try
            {
                while (true)
                {
                    Thread.Sleep(CheckPeriodMs);
                    if (File.Exists(StopFile)) break;
                    uint rc = WaitForSingleObject(pi.hProcess, 0);
                    if (rc == 0) break;                    // zeptun exited
                    if (TorUpInt(socksPort, 1500)) torDown = 0; else torDown++;
                    if (torDown >= TorDownLimit) break;    // tor died underneath us
                }
            }
            finally
            {
                StopZeptun(ref pi);
                DeleteAdapterBestEffort();
                FlushDns();
                WriteState("status=off");
                try { if (File.Exists(StopFile)) File.Delete(StopFile); } catch { }
                WriteResult("off");
            }
            return 0;
        }

        private static bool TorUpInt(string socksPort, int timeoutMs)
        {
            int p;
            if (!int.TryParse(socksPort, out p) || p <= 0) return false;
            return TorUp(p, timeoutMs);
        }

        private static void Fail(string msg)
        {
            WriteState("status=error\r\nmsg=" + msg);
            WriteResult("error: " + msg);
        }

        private static int Main(string[] args)
        {
            string arg = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
            if (arg == "on")
            {
                bool acquired;
                Mutex m = null;
                try
                {
                    m = new Mutex(false, "DeltatorZeptunTunHelper");
                    acquired = m.WaitOne(0);
                }
                catch (AbandonedMutexException) { acquired = true; }
                catch { acquired = true; }
                if (!acquired)
                {
                    WriteResult("error: TUN is already on (another instance is running)");
                    return 1;
                }
                try
                {
                    string socksPort = args.Length > 1 ? args[1] : "9050";
                    return CmdOn(socksPort);
                }
                catch (Exception ex)
                {
                    try { WriteResult("error: " + ex.GetType().Name + ": " + ex.Message); }
                    catch { }
                    return 1;
                }
                finally
                {
                    try
                    {
                        if (m != null && acquired)
                        {
                            m.ReleaseMutex();
                            m.Dispose();
                        }
                    }
                    catch { }
                }
            }
            if (arg == "off") return CmdOff();
            return CmdStatus();
        }
    }
}