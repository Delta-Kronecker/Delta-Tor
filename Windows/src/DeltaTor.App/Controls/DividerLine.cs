namespace DeltaTor.App.Controls;

/// <summary>
/// The 1px divider (Compose DividerLine 1:1): horizontal gradient
/// transparent → Border → transparent. The Solid kind is the plain row
/// separator (BorderLight 60%) the drawer uses between rows.
/// </summary>
public sealed class DividerLine : Control
{
    public enum Kind
    {
        Gradient,
        Solid
    }

    private Kind _kind = Kind.Gradient;

    public DividerLine()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        Height = 1;
        BackColor = Color.Transparent;
    }

    public Kind Style
    {
        get => _kind;
        set { _kind = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var rect = new RectangleF(0f, 0f, Width, Height);

        if (_kind == Kind.Solid)
        {
            using var brush = new SolidBrush(Color.FromArgb(153, DeltaTorTheme.BorderLight));
            g.FillRectangle(brush, rect);
            return;
        }

        using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
            rect, Color.Transparent, Color.Transparent, 0f))
        {
            var blend = new System.Drawing.Drawing2D.ColorBlend(3)
            {
                Positions = new[] { 0f, 0.5f, 1f },
                Colors = new[] { Color.Transparent, DeltaTorTheme.Border, Color.Transparent }
            };
            brush.InterpolationColors = blend;
            g.FillRectangle(brush, rect);
        }
    }
}
