using System.Globalization;
using DeltaTor.Core;

namespace DeltaTor.App;

/// <summary>
/// Pure UI helpers ported 1:1 from MainActivity.kt (state labels/colors, the
/// byte/duration/relative-time formatters, flag emoji). No controls here —
/// these are the strings and colors the painted controls render.
/// </summary>
public static class UiHelpers
{
    public static Color StateColor(VpnState state)
    {
        if (state.Stopping) return DeltaTorTheme.Amber;
        if (state.Connecting) return DeltaTorTheme.Amber;
        if (state.Reconnecting) return DeltaTorTheme.AmberLight;
        if (state.Connected) return DeltaTorTheme.Green;
        if (state.TorRunning) return DeltaTorTheme.Amber;
        return DeltaTorTheme.Muted;
    }

    public static string StatusLabel(VpnState state)
    {
        if (state.Stopping) return "";
        if (state.Connecting) return "CONNECTING";
        if (state.Connected) return "CONNECTED";
        if (state.TorRunning) return "READY";
        return "OFFLINE";
    }

    public static Color GlyphColor(bool connecting, bool connected, bool stopping)
    {
        if (stopping) return DeltaTorTheme.Amber;
        if (connecting) return DeltaTorTheme.Amber;
        if (connected) return DeltaTorTheme.Green;
        return DeltaTorTheme.Text;
    }

    public static string LabelText(VpnState state)
    {
        // Nothing here. The only STOPPING the user sees is the big word at the top.
        if (state.Stopping) return "";
        if (state.Error != null) return state.Error;
        if (state.Connecting) return "CANCEL";
        if (state.Connected) return "DISCONNECT";
        // Proxy mode is a live state of its own, not a half-finished VPN.
        // Without this the ring reads START VPN, because Tor is up but no
        // tunnel exists, and pressing it would do nothing: the service
        // refuses a start when proxy mode is on. Offering DISCONNECT is the
        // only action that actually stops anything.
        if (state.SocksEndpoint.Length > 0) return "DISCONNECT";
        if (state.TorRunning) return "START VPN";
        return "CONNECT";
    }

    public static string WordFor(
        bool connecting,
        bool torRunning,
        bool connected,
        bool reconnecting,
        bool stopping,
        bool hasError)
    {
        if (stopping) return "STOPPING";
        if (hasError) return "ERROR";
        if (connecting) return "CONNECTING";
        if (reconnecting) return "LINK LOST";
        if (connected) return "CONNECTED";
        if (torRunning) return "READY";
        return "OFFLINE";
    }

    public static string SublineFor(
        bool connecting,
        bool torRunning,
        bool connected,
        bool reconnecting,
        bool stopping,
        string transport,
        int peak,
        bool hasError)
    {
        if (stopping) return "";
        if (hasError) return "BOOTSTRAP FAILED";
        if (connecting) return $"TUNNEL BOOTSTRAPPING · {peak}%";
        if (reconnecting) return "RESTORING THE TUNNEL";
        if (connected) return $"{transport.ToUpperInvariant()} · GATEWAY ACTIVE";
        if (torRunning) return "TOR RUNNING · VPN PAUSED";
        return "YOUR PRIVATE GATEWAY";
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        var units = new[] { "KB", "MB", "GB", "TB" };
        var v = (float)bytes;
        var idx = -1;
        while (v >= 1024 && idx < units.Length - 1)
        {
            v /= 1024f;
            idx++;
        }
        return string.Format(CultureInfo.InvariantCulture, "{0:F1} {1}", v, units[idx]);
    }

    public static string FormatDuration(long ms)
    {
        var total = Math.Max(ms / 1000, 0);
        var h = total / 3600;
        var m = (total % 3600) / 60;
        var s = total % 60;
        static string Pad(long n) => n.ToString().PadLeft(2, '0');
        return h > 0 ? $"{h}:{Pad(m)}:{Pad(s)}" : $"{Pad(m)}:{Pad(s)}";
    }

    public static string RelativeTime(long ms)
    {
        if (ms <= 0) return "never";
        var secs = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ms) / 1000;
        if (secs < 60) return "just now";
        if (secs < 3600) return $"{secs / 60}m ago";
        if (secs < 86_400) return $"{secs / 3600}h ago";
        return $"{secs / 86_400}d ago";
    }

    /// <summary>Two-letter country code → flag emoji; anything else → 🌐.</summary>
    public static string FlagEmoji(string code)
    {
        if (code.Length != 2) return "\U0001F310";
        var sb = new System.Text.StringBuilder();
        foreach (var c in code.ToUpperInvariant())
            sb.Append(char.ConvertFromUtf32(0x1F1E6 + (c - 'A')));
        return sb.ToString();
    }

    /// <summary>"9.0", "0.3" · one decimal, never "9", so columns line up.</summary>
    public static string Format1(float value)
    {
        var v = (int)(value * 10f);
        return $"{v / 10}.{v % 10}";
    }
}
