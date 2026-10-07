namespace DeltaTor.App.Controls;

/// <summary>
/// The app's canvas-drawn glyphs, ported 1:1 from MainActivity's icon
/// composables: geometry in fractions of the bounds (like the Compose
/// canvases), round caps/joins, absolute stroke widths in px — at 100%
/// scale 1px == 1dp for these.
/// </summary>
public static class Icons
{
    /// <summary>MenuIcon (MainActivity 447): three round-capped lines.</summary>
    public static void Menu(Graphics g, RectangleF b, Color color)
    {
        using var pen = new Pen(color, 1.6f)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round
        };
        for (var i = 0; i < 3; i++)
        {
            var y = b.Y + b.Height * (0.22f + i * 0.28f);
            g.DrawLine(pen, b.X + b.Width * 0.12f, y, b.X + b.Width * 0.88f, y);
        }
    }

    /// <summary>CloseIcon (MainActivity 460): an X.</summary>
    public static void Close(Graphics g, RectangleF b, Color color)
    {
        using var pen = new Pen(color, 1.6f)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round
        };
        var p = b.Width * 0.24f;
        var q = b.Width - p;
        g.DrawLine(pen, b.X + p, b.Y + p, b.X + q, b.Y + q);
        g.DrawLine(pen, b.X + q, b.Y + p, b.X + p, b.Y + q);
    }

    /// <summary>WarnIcon (MainActivity 471): filled-outline triangle with ! mark.</summary>
    public static void Warn(Graphics g, RectangleF b, Color color)
    {
        var pts = new[]
        {
            new PointF(b.X + b.Width * 0.50f, b.Y + b.Height * 0.06f),
            new PointF(b.X + b.Width * 0.96f, b.Y + b.Height * 0.92f),
            new PointF(b.X + b.Width * 0.04f, b.Y + b.Height * 0.92f)
        };
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddPolygon(pts);
        using (var fill = new SolidBrush(Color.FromArgb(51, color))) // copy(alpha = 0.20f)
            g.FillPath(fill, path);
        using (var stroke = new Pen(color, 1.3f)
        {
            LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round
        })
            g.DrawPath(stroke, path);

        var cx = b.X + b.Width * 0.5f;
        using (var line = new Pen(color, 1.5f)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round
        })
            g.DrawLine(line, cx, b.Y + b.Height * 0.38f, cx, b.Y + b.Height * 0.64f);
        using (var dot = new SolidBrush(color))
            g.FillEllipse(dot, cx - 1.2f, b.Y + b.Height * 0.78f - 1.2f, 2.4f, 2.4f);
    }

    /// <summary>ChevronIcon (MainActivity 1092): down-caret, optionally rotated.</summary>
    public static void Chevron(Graphics g, RectangleF b, Color color, float turn = 0f)
    {
        var state = g.Save();
        if (turn != 0f)
        {
            g.TranslateTransform(b.X + b.Width / 2f, b.Y + b.Height / 2f);
            g.RotateTransform(turn);
            g.TranslateTransform(-(b.X + b.Width / 2f), -(b.Y + b.Height / 2f));
        }
        using var pen = new Pen(color, 1.6f)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round,
            LineJoin = System.Drawing.Drawing2D.LineJoin.Round
        };
        var p = new PointF[]
        {
            new(b.X + b.Width * 0.16f, b.Y + b.Height * 0.36f),
            new(b.X + b.Width * 0.50f, b.Y + b.Height * 0.70f),
            new(b.X + b.Width * 0.84f, b.Y + b.Height * 0.36f)
        };
        g.DrawLines(pen, p);
        g.Restore(state);
    }

    /// <summary>ArrowIcon (MainActivity 1817): up/down chevron plus stem.</summary>
    public static void Arrow(Graphics g, RectangleF b, Color color, bool up)
    {
        using var pen = new Pen(color, 2f)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round,
            LineJoin = System.Drawing.Drawing2D.LineJoin.Round
        };
        var w = b.Width;
        var h = b.Height;
        var tipY = b.Y + h * (up ? 0.30f : 0.70f);
        var baseY = b.Y + h * (up ? 0.68f : 0.30f);
        var elbowY = b.Y + h * (up ? 0.62f : 0.38f);
        g.DrawLines(pen, new[]
        {
            new PointF(b.X + w * 0.15f, elbowY),
            new PointF(b.X + w * 0.50f, tipY),
            new PointF(b.X + w * 0.85f, elbowY)
        });
        g.DrawLine(pen, b.X + w * 0.5f, baseY, b.X + w * 0.5f,
            b.Y + h * (up ? 0.78f : 0.22f));
    }

    /// <summary>BackArrowIcon (MainActivity 2593): left-pointing chevron.</summary>
    public static void Back(Graphics g, RectangleF b, Color color)
    {
        using var pen = new Pen(color, 2f)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round,
            LineJoin = System.Drawing.Drawing2D.LineJoin.Round
        };
        g.DrawLines(pen, new[]
        {
            new PointF(b.X + b.Width * 0.64f, b.Y + b.Height * 0.16f),
            new PointF(b.X + b.Width * 0.30f, b.Y + b.Height * 0.50f),
            new PointF(b.X + b.Width * 0.64f, b.Y + b.Height * 0.84f)
        });
    }

    /// <summary>CheckIcon (MainActivity 2874): tick.</summary>
    public static void Check(Graphics g, RectangleF b, Color color)
    {
        using var pen = new Pen(color, 1.7f)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round,
            LineJoin = System.Drawing.Drawing2D.LineJoin.Round
        };
        g.DrawLines(pen, new[]
        {
            new PointF(b.X + b.Width * 0.14f, b.Y + b.Height * 0.52f),
            new PointF(b.X + b.Width * 0.40f, b.Y + b.Height * 0.78f),
            new PointF(b.X + b.Width * 0.86f, b.Y + b.Height * 0.22f)
        });
    }
}
