namespace DeltaTor.App.Controls;

/// <summary>
/// The drawer's on/off switch (Compose ToggleSwitch 1:1): 46×26 track,
/// 18px knob at y=3, Accent when on / SurfaceAlt when off, knob slides
/// 4→22 over 160 ms with FastOutSlowIn easing.
/// </summary>
public sealed class ToggleSwitch : Control
{
    private bool _checked;
    private float _progress;
    private bool _animating;
    private long _animStart;
    private float _animFrom;
    private float _animTo = 1f;

    private readonly System.Windows.Forms.Timer _timer;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(46, 26);
        _timer = new System.Windows.Forms.Timer { Interval = 15 };
        _timer.Tick += (_, _) =>
        {
            var p = (Environment.TickCount64 - _animStart) / 160.0;
            if (p >= 1.0)
            {
                p = 1.0;
                _animating = false;
                _timer.Stop();
            }
            _progress = _animFrom + (_animTo - _animFrom) * FastOutSlowIn((float)p);
            Invalidate();
        };
    }

    public event EventHandler? CheckedChanged;

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            _animFrom = _progress;
            _animTo = value ? 1f : 0f;
            _animStart = Environment.TickCount64;
            _animating = true;
            _timer.Start();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left) Checked = !_checked;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var sx = Width / 46f;
        var sy = Height / 26f;

        // Track
        var track = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using (var path = DrawUtil.RoundedRect(track, Height / 2f))
        using (var fill = new SolidBrush(_checked ? DeltaTorTheme.Accent : DeltaTorTheme.SurfaceAlt))
        using (var pen = new Pen(
            _checked ? Color.FromArgb(102, DeltaTorTheme.AccentLight) : DeltaTorTheme.BorderLight))
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        // Knob: 18×18 at y=3, slides 4→22 (exactly its own width)
        var d = 18f * sy;
        var kx = sx * (4f + 18f * _progress);
        var ky = 3f * sy;
        using (var knob = new SolidBrush(_checked ? Color.White : DeltaTorTheme.Muted))
            g.FillEllipse(knob, kx, ky, d, d);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>Compose FastOutSlowInEasing: cubic-bezier(0.4, 0, 0.2, 1).</summary>
    private static float FastOutSlowIn(float x)
    {
        // Bisection on x(t) = x, then evaluate y(t).
        float lo = 0f, hi = 1f, t = x;
        for (var i = 0; i < 24; i++)
        {
            t = (lo + hi) / 2f;
            var v = 1f - t;
            var cx = 3f * v * v * t * 0.4f + 3f * v * t * t * 0.2f + t * t * t;
            if (cx < x) lo = t; else hi = t;
        }
        var u = 1f - t;
        return 3f * u * u * t * 0f + 3f * u * t * t * 1f + t * t * t;
    }
}
