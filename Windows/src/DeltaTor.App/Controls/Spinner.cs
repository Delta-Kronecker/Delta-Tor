namespace DeltaTor.App.Controls;

/// <summary>
/// The small activity spinner (Compose Spinner 1:1): 14×14 canvas, a 260°
/// round-capped 2px arc rotating 360° every 900 ms (linear), restarted
/// every loop. Ticks only while visible.
/// </summary>
public sealed class Spinner : Control
{
    private readonly System.Windows.Forms.Timer _timer;
    private long _start = Environment.TickCount64;
    private Color _spinColor = DeltaTorTheme.Accent;

    public Spinner()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(14, 14);
        _timer = new System.Windows.Forms.Timer { Interval = 16 };
        _timer.Tick += (_, _) => Invalidate();
    }

    public Color SpinColor
    {
        get => _spinColor;
        set { _spinColor = value; Invalidate(); }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible && !DesignMode) _timer.Start();
        else _timer.Stop();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var rotation = (float)((Environment.TickCount64 - _start) % 900) * 360f / 900f;
        var inset = Height * (2f / 14f);
        var rect = new RectangleF(inset, inset, Width - inset * 2f, Height - inset * 2f);
        using var pen = new Pen(_spinColor, Height * (2f / 14f))
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round
        };
        g.DrawArc(pen, rect, -rotation, 260f);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
