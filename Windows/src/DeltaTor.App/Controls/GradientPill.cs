namespace DeltaTor.App.Controls;

/// <summary>
/// The drawer/pill button (Compose GradientPill 1:1): 40px tall, fully
/// rounded, filled = AccentSoft→Accent horizontal gradient, outline =
/// Surface→SurfaceAlt; bold letter-spaced label (1.2), ellipsized single
/// line; a dimmed label (Text 40%) is the only cue while disabled.
/// </summary>
public sealed class GradientPill : Control
{
    private string _label = "";
    private bool _filled;
    private float _labelPt = DeltaTorTheme.BodyPt;

    public GradientPill()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 40;
        Cursor = Cursors.Hand;
    }

    public event EventHandler? Clicked;

    public string Label
    {
        get => _label;
        set { _label = value ?? ""; Invalidate(); }
    }

    public bool Filled
    {
        get => _filled;
        set { _filled = value; Invalidate(); }
    }

    public float LabelPt
    {
        get => _labelPt;
        set { _labelPt = value; Invalidate(); }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left && Enabled) Clicked?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Enabled && (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter))
        {
            Clicked?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        var radius = Math.Min(20f, Height / 2f);

        using var path = DrawUtil.RoundedRect(rect, radius);
        Color from, to;
        if (_filled)
        {
            from = DeltaTorTheme.AccentSoft;
            to = DeltaTorTheme.Accent;
        }
        else
        {
            from = DeltaTorTheme.Surface;
            to = DeltaTorTheme.SurfaceAlt;
        }
        using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(rect, from, to, 0f))
            g.FillPath(brush, path);

        using (var pen = new Pen(
            _filled ? Color.FromArgb(102, DeltaTorTheme.AccentLight) : DeltaTorTheme.BorderLight))
            g.DrawPath(pen, path);

        // Label color: dimmed when inert is the only cue for a temporarily
        // disabled pill, so the button keeps its size instead of blinking
        // out of the row while a teardown runs.
        Color labelColor = !Enabled
            ? Color.FromArgb(102, DeltaTorTheme.Text)
            : _filled
                ? Color.White
                : DeltaTorTheme.Text;

        using var font = new Font(DeltaTorTheme.FontFamilyName, _labelPt, FontStyle.Bold);
        var spacing = Height * (1.2f / 40f);
        var inner = new RectangleF(rect.X + 6f, rect.Y, rect.Width - 12f, rect.Height);
        using (var brush = new SolidBrush(labelColor))
            DrawUtil.DrawSpacedCentered(g, _label, font, brush, inner, spacing);
    }
}
