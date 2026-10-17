using System.Drawing.Drawing2D;
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

    /// <summary>Two-letter country code → flag emoji; anything else → 🌐.
    /// Non-letters (a "--" placeholder) must not reach the arithmetic below:
    /// it would build a non-flag codepoint instead of falling back.</summary>
    public static string FlagEmoji(string code)
    {
        if (code.Length != 2 || !char.IsAsciiLetter(code[0]) || !char.IsAsciiLetter(code[1]))
            return "\U0001F310";
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

    // ---- flag images --------------------------------------------------------
    // Regional-indicator emoji show as the plain letters "DE" on machines
    // without Segoe UI Emoji (GDI never applies the pair substitution at all),
    // so country flags ship as PNGs under assets/flags/ and are drawn as
    // images. The emoji string stays the last-resort fallback when a file is
    // missing for a code.

    private static readonly Dictionary<string, Bitmap?> FlagCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cached flag bitmap for an ISO code, or null when not shipped.</summary>
    public static Bitmap? FlagImage(string code)
    {
        if (code.Length != 2 || !char.IsAsciiLetter(code[0]) || !char.IsAsciiLetter(code[1]))
            return null;
        var key = code.ToLowerInvariant();
        if (FlagCache.TryGetValue(key, out var cached)) return cached;
        Bitmap? bmp = null;
        var path = AppAssets.Existing($"flags/{key}.png");
        if (path != null)
        {
            try { bmp = new Bitmap(path); }
            catch { bmp = null; }
        }
        FlagCache[key] = bmp;
        return bmp;
    }

    /// <summary>Draw the flag at (x, y), w wide and its natural 4:3 tall.
    /// Returns false when no image ships for the code, so the caller can fall
    /// back to text instead of silently dropping the flag.</summary>
    public static bool DrawFlag(Graphics g, string code, float x, float y, float w)
    {
        var bmp = FlagImage(code);
        if (bmp == null) return false;
        var h = w * bmp.Height / bmp.Width;
        var state = g.Save();
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.DrawImage(bmp, x, y, w, h);
        g.Restore(state);
        return true;
    }

    /// <summary>Strips a leading flag emoji (regional pair, or the globe for
    /// "any location" → code "--") from a display string. Each emoji is a
    /// surrogate pair: both forms start with U+D83C, the globe's low word is
    /// U+DF10 and regional indicators carry U+DDE6..U+DDFF letters.</summary>
    public static bool TrySplitFlag(ref string text, out string code)
    {
        code = "";
        if (text.Length < 2 || text[0] != '\uD83C') return false;
        var lo1 = text[1];
        if (lo1 == '\uDF10')
        {
            code = "--";
            text = text.Substring(2).TrimStart();
            return true;
        }
        if (lo1 is >= '\uDDE6' and <= '\xDDFF' &&
            text.Length >= 4 && text[2] == '\uD83C' &&
            text[3] is >= '\uDDE6' and <= '\xDDFF')
        {
            code = $"{(char)('A' + lo1 - '\uDDE6')}{(char)('A' + text[3] - '\uDDE6')}";
            text = text.Substring(4).TrimStart();
            return true;
        }
        return false;
    }
}
