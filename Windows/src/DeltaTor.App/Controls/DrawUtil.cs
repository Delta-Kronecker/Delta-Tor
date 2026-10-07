using System.Drawing.Drawing2D;

namespace DeltaTor.App.Controls;

/// <summary>
/// Shared GDI+ drawing helpers for the owner-drawn controls: rounded
/// rectangles and letter-spaced text (Compose adds letterSpacing between
/// glyphs; GDI+ has no such concept, so short labels are drawn glyph by
/// glyph at the requested pitch).
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

    /// <summary>Total width of <paramref name="text"/> when drawn with pitch spacing.</summary>
    public static float SpacedWidth(Graphics g, string text, Font font, float spacing)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        var width = 0f;
        foreach (var c in text)
            width += g.MeasureString(c.ToString(), font).Width;
        return width + spacing * (text.Length - 1);
    }

    /// <summary>Ellipsize <paramref name="text"/> to <paramref name="maxWidth"/> px when drawn with pitch spacing.</summary>
    public static string Ellipsize(Graphics g, string text, Font font, float spacing, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || SpacedWidth(g, text, font, spacing) <= maxWidth)
            return text ?? "";
        var line = text;
        while (line.Length > 1 && SpacedWidth(g, line + "…", font, spacing) > maxWidth)
            line = line[..^1];
        return line + "…";
    }

    /// <summary>
    /// Draw <paramref name="text"/> glyph-by-glyph left-aligned from
    /// (x, y), ellipsized to <paramref name="maxWidth"/>.
    /// </summary>
    public static void DrawSpaced(
        Graphics g, string text, Font font, Brush brush,
        float x, float y, float maxWidth, float spacing)
    {
        foreach (var c in Ellipsize(g, text, font, spacing, maxWidth))
        {
            var cs = c.ToString();
            g.DrawString(cs, font, brush, x, y);
            x += g.MeasureString(cs, font).Width + spacing;
        }
    }

    /// <summary>
    /// Draw <paramref name="text"/> glyph-by-glyph centered horizontally in
    /// <paramref name="bounds"/>, vertically centered, with
    /// <paramref name="spacing"/> px between glyphs. Single line, ellipsized
    /// to the bounds width (like Compose maxLines=1 ellipsis).
    /// </summary>
    public static void DrawSpacedCentered(
        Graphics g, string text, Font font, Brush brush, RectangleF bounds, float spacing)
    {
        var line = Ellipsize(g, text, font, spacing, bounds.Width);
        var w = SpacedWidth(g, line, font, spacing);
        var lineSize = g.MeasureString(line, font);
        var x = bounds.X + (bounds.Width - w) / 2f;
        var y = bounds.Y + (bounds.Height - lineSize.Height) / 2f;
        DrawSpaced(g, line, font, brush, x, y, bounds.Width, spacing);
    }
}
