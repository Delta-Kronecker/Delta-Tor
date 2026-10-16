using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace DeltaTor.Native;

/// <summary>
/// <c>DeltaTor.exe --native-probe</c>: reproduces the exact native-load path
/// the app fails on (real DllImport in a real WinExe process) and writes
/// <c>native-probe.txt</c> next to the exe, which diagnose.ps1 then prints.
///
/// A plain PowerShell LoadLibraryExW test succeeding while the app fails
/// means the difference is process context, so the probe reports everything
/// that differs: BaseDirectory, PATH, elevation, both load variants
/// (raw LoadLibraryExW with LOAD_WITH_ALTERED_SEARCH_PATH, then the real
/// .NET P/Invoke), and the file paths of the msys/hev/wintun modules the
/// process actually mapped - a foreign msys-2.0.dll (e.g. from Git for
/// Windows) shadowing ours would show up there.
/// </summary>
public static class NativeProbe
{
    private const string Lib = "hev-socks5-tunnel.dll";

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryExW(string path, IntPtr reserved, uint flags);

    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(IntPtr handle);

    /// <summary>Runs the probe; returns the process exit code (0 = every step loaded).</summary>
    public static int Run()
    {
        var sb = new System.Text.StringBuilder();
        void Line(string s) => sb.AppendLine(s);

        Line("DeltaTor native probe");
        Line($"Date:      {DateTime.Now:s}");
        Line($"OS:        {Environment.OSVersion}");
        Line($"Arch:      {RuntimeInformation.OSArchitecture}");
        Line($"Process:   {(Environment.Is64BitProcess ? "x64" : "x86")}");
        Line($"Elevated:  {IsElevated()}");
        Line($"BaseDir:   {AppContext.BaseDirectory}");
        Line($"CWD:       {Directory.GetCurrentDirectory()}");
        Line($"PATH:      {Environment.GetEnvironmentVariable("PATH")}");
        Line("");

        var full = Path.Combine(AppContext.BaseDirectory, Lib);
        Line($"--- 1. raw LoadLibraryExW(full path, LOAD_WITH_ALTERED_SEARCH_PATH=8) on {Lib}");
        var h = LoadLibraryExW(full, IntPtr.Zero, 0x8);
        if (h == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            Line($"FAIL: Win32 error {err} ({DescribeWin32(err)})");
        }
        else
        {
            Line($"OK: handle 0x{h.ToInt64():x}");
            FreeLibrary(h);
        }
        Line("");

        Line($"--- 2. NativeLibrary.TryLoad(full path) on {Lib}");
        if (NativeLibrary.TryLoad(full, out var nh))
        {
            Line($"OK: handle 0x{nh.ToInt64():x}");
        }
        else
        {
            Line("FAIL: TryLoad returned false (no Win32 code surfaced)");
        }
        Line("");

        Line("--- 3. real P/Invoke: HevTunnelApi.SetRejectQuic(1)");
        try
        {
            HevTunnelApi.SetRejectQuic(1);
            HevTunnelApi.SetRejectNonDnsUdp(0);
            Line("OK: SetRejectQuic + SetRejectNonDnsUdp returned");
        }
        catch (Exception e)
        {
            Line($"FAIL: {e.GetType().FullName}: {e.Message}");
            Line(ExitCodeOf(e));
            rc = 4;
        }
        Line("");

        Line("--- 4. native modules mapped into this process");
        try
        {
            foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
            {
                var n = Path.GetFileName(m.FileName ?? "");
                if (n.Contains("msys", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("hev-socks5", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("wintun", StringComparison.OrdinalIgnoreCase))
                    Line($"  {n} = {m.FileName}");
            }
        }
        catch (Exception e)
        {
            Line($"  (module enumeration failed: {e.Message})");
        }
        Line("");

        Line(rc == 0 ? "RESULT: all loads OK" : "RESULT: FAILED (see above)");

        var outPath = Path.Combine(AppContext.BaseDirectory, "native-probe.txt");
        File.WriteAllText(outPath, sb.ToString());
        return rc;
    }

    private static int rc;

    private static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static string DescribeWin32(int err) => err switch
    {
        126 => "ERROR_MOD_NOT_FOUND - a file is missing (dependency or the dll itself)",
        1114 => "ERROR_DLL_INIT_FAILED - a dll was found but its DllMain returned FALSE (0x8007045A)",
        193 => "ERROR_BAD_EXE_FORMAT - wrong bitness (32/64 mismatch)",
        5 => "ERROR_ACCESS_DENIED - blocked by policy or antivirus",
        _ => "see winerror.h",
    };

    private static string ExitCodeOf(Exception e)
    {
        for (var x = e; x != null; x = x.InnerException!)
            if (x is DllNotFoundException && x.Message.Contains("0x8007045A", StringComparison.OrdinalIgnoreCase))
                return "  (0x8007045A = ERROR_DLL_INIT_FAILED, code 1114)";
        return "";
    }
}
