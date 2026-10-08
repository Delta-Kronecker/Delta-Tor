using System.Drawing.Drawing2D;
using System.Globalization;

namespace DeltaTor.App.Controls;

/// <summary>
/// Shared GDI+ drawing helpers: rounded rectangles and letter-spaced text
/// (Compose sets letterSpacing between glyphs; GDI+ has no such concept, so
/// labels are drawn glyph by glyph at the requested pitch).
///
/// Widths come from TextRenderer with TextFormatFlags.NoPadding — the GDI
/// advance of the text, with no trailing cell padding. MeasureString must
/// not be used to position glyphs: it returns ink-plus-bearings with a
/// padded right edge, so a one-character measure is WIDER than that
/// character's advance. Taking prefix differences of padded measures cancels
/// the padding everywhere except the first prefix, which inflated the first
/// advance and opened a visible gap after the first letter ("A dvance").
/// GDI advances telescope exactly: prefix i lands at the sum of the
/// advances before it, which is where DrawString's own layout would put it.
///
/// Stepping happens by text element, not UTF-16 unit: a flag emoji is one
/// element of four units, and slicing a surrogate pair mid-way makes the
/// measure return nonsense, so every glyph after it lands at a wrong x and
/// the line draws on top of itself.
/// </summary>
internal static class DrawUtil
{
    /// <summary>Rectangle with rounded corners (radius clamped to half the short side).</summary>
    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = Math.Min(radius, Math.Min(r.Width, r.Height) / 2f);
        var path = new GraphicsPath();
        if (d <= 0f)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d * 2f, d * 2f, 180, 90);
        path.AddArc(r.Right - d * 2f, r.Y, d * 2f, d * 2f, 270, 90);
        path.AddArc(r.Right - d * 2f, r.Bottom - d * 2f, d * 2f, d * 2f, 0, 90);
        path.AddArc(r.X, r.Bottom - d * 2f, d * 2f, d * 2f, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Single-line pixel width of <paramref name="text"/>: the exact
    /// GDI advance, no cell padding — the measure glyph positioning needs.</summary>
    public static float AdvanceWidth(string text, Font font) =>
        string.IsNullOrEmpty(text)
            ? 0f
            : TextRenderer.MeasureText(text, font, Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;

    /// <summary>Text elements in <paramref name="text"/> (a flag emoji counts as one).</summary>
    private static int ElementCount(string text)
    {
        var count = 0;
        for (var i = 0; i < text.Length; i += StringInfo.GetNextTextElementLength(text.AsSpan(i)))
            count++;
        return count;
    }

    /// <summary>Start index of the last text element in <paramref name="line"/>.</summary>
    private static int StartOfLastElement(string line)
    {
        var last = 0;
        for (var i = 0; i < line.Length;)
        {
            last = i;
            i += StringInfo.GetNextTextElementLength(line.AsSpan(i));
        }
        return last;
    }

    /// <summary>Total width of <paramref name="text"/> when drawn with pitch spacing.</summary>
    public static float SpacedWidth(string text, Font font, float spacing)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        return AdvanceWidth(text, font) + spacing * Math.Max(0, ElementCount(text) - 1);
    }

    /// <summary>Ellipsize <paramref name="text"/> to <paramref name="maxWidth"/> px when drawn with pitch spacing.</summary>
    public static string Ellipsize(string text, Font font, float spacing, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || SpacedWidth(text, font, spacing) <= maxWidth)
            return text ?? "";
        var line = text;
        while (line.Length > 1)
        {
            if (SpacedWidth(line + "…", font, spacing) <= maxWidth) break;
            var last = StartOfLastElement(line);
            if (last == 0) break; // one element only: nothing whole left to drop
            line = line[..last];
        }
        return line + "…";
    }

    /// <summary>
    /// Draw <paramref name="text"/> left-aligned from (x, y), ellipsized to
    /// <paramref name="maxWidth"/>, with <paramref name="spacing"/> px
    /// between glyphs. At spacing 0 the string goes down in one DrawString,
    /// which is already exactly the zero-pitch layout.
    /// </summary>
    public static void DrawSpaced(
        Graphics g, string text, Font font, Brush brush,
        float x, float y, float maxWidth, float spacing)
    {
        var line = Ellipsize(text, font, spacing, maxWidth);
        if (line.Length == 0) return;
        if (spacing <= 0f)
        {
            g.DrawString(line, font, brush, x, y);
            return;
        }
        var prev = 0f;
        for (var i = 0; i < line.Length;)
        {
            var len = StringInfo.GetNextTextElementLength(line.AsSpan(i));
            var upTo = AdvanceWidth(line[..(i + len)], font);
            g.DrawString(line.AsSpan(i, len).ToString(), font, brush, x, y);
            x += upTo - prev + spacing;
            prev = upTo;
            i += len;
        }
    }

    /// <summary>
    /// Draw <paramref name="text"/> centered horizontally in
    /// <paramref name="bounds"/>, vertically centered, single line,
    /// ellipsized to the bounds width (Compose maxLines=1 ellipsis).
    /// </summary>
    public static void DrawSpacedCentered(
        Graphics g, string text, Font font, Brush brush, RectangleF bounds, float spacing)
    {
        var line = Ellipsize(text, font, spacing, bounds.Width);
        var w = SpacedWidth(line, font, spacing);
        var lineSize = g.MeasureString(line, font);
        var x = bounds.X + (bounds.Width - w) / 2f;
        var y = bounds.Y + (bounds.Height - lineSize.Height) / 2f;
        DrawSpaced(g, line, font, brush, x, y, float.MaxValue, spacing);
    }
}
