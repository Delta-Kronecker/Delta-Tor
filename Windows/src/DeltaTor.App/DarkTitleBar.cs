using System.Runtime.InteropServices;

namespace DeltaTor.App;

/// <summary>
/// The DWM "immersive dark mode" caption, so the system title bar and its
/// borders match the app's dark sheet instead of shipping the default white
/// Win32 chrome. Build 19041 (the TFM floor) answers attribute 20; older
/// 20H1 previews only knew 19, so a failed 20 falls back to 19. The border
/// tint is Win11-only (attribute 34) and fails harmlessly on Win10.
/// </summary>
internal static class DarkTitleBar
{
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attr, ref int value, int size);

    public static void Apply(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        var on = 1;
        if (DwmSetWindowAttribute(
                hwnd, DwmwaUseImmersiveDarkMode, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(
                hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref on, sizeof(int));

        var border = ColorTranslator.ToWin32(DeltaTorTheme.BorderLight);
        _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(int));
    }
}
