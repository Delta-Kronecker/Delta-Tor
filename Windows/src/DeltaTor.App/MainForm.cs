using DeltaTor.App.Controls;
using DeltaTor.Core;

namespace DeltaTor.App;

/// <summary>
/// The main window, laid out 1:1 with MainActivity's MainScreen: layered
/// ambient background (AirBackground), header with drawer button, wordmark
/// and status chip, and — added in the following passes — the state block,
/// the floating power ring and the bottom panel.
/// </summary>
public sealed class MainForm : Form
{
    private VpnState _state = AppState.State;

    // The state color animates over 450ms (animateColorAsState(sc, tween(450))).
    private Color _scCurrent;
    private Color _scFrom;
    private Color _scTo;
    private long _scStart;
    private bool _tweening;

    // The background halo pulses 0.5→1→0.5 over 2×2600ms while connecting and
    // rests at 0.5 otherwise (rememberActiveLoop, resting = from).
    private long _pulseStart;
    private const int PulseDuration = 2600;

    private RectangleF _menuRect;
    private bool _noticeOpen;

    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 16 };

    public MainForm()
    {
        Text = "DeltaTor";
        BackColor = DeltaTorTheme.Bg;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1024, 680);
        MinimumSize = new Size(900, 600);
        KeyPreview = true;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.Dpi;

        _scCurrent = _scFrom = _scTo = UiHelpers.StateColor(_state);

        _tick.Tick += (_, _) => AnimationFrame();

        AppState.Changed += OnStateChanged;
        FormClosed += (_, _) =>
        {
            AppState.Changed -= OnStateChanged;
            _tick.Stop();
            _tick.Dispose();
        };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        RefreshFromState();
    }

    // ---- state plumbing -----------------------------------------------------

    private void OnStateChanged()
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(RefreshFromState);
            }
            catch (ObjectDisposedException)
            {
            }
            return;
        }
        RefreshFromState();
    }

    private void RefreshFromState()
    {
        var prev = _state;
        _state = AppState.State;

        var sc = UiHelpers.StateColor(_state);
        if (sc != _scTo)
        {
            _scFrom = _scCurrent;
            _scTo = sc;
            _scStart = Environment.TickCount64;
            _tweening = true;
        }
        if (_state.Connecting && !prev.Connecting)
            _pulseStart = Environment.TickCount64;
        _tick.Start();
        Invalidate();
        ShowNoticeIfAny();
    }

    /// <summary>
    /// A notice is latched on its id and cleared by the UI itself, not by
    /// whatever the service does next: these describe an action the app took
    /// on the user's behalf, and the restart that action caused is exactly
    /// what would otherwise wipe them.
    /// </summary>
    private void ShowNoticeIfAny()
    {
        while (!_noticeOpen)
        {
            var notice = _state.Notice;
            if (notice is null) return;
            _noticeOpen = true;
            try
            {
                new NoticeDialog(notice).ShowDialog(this);
            }
            finally
            {
                _noticeOpen = false;
            }
            // The modal loop kept pumping events, so a notice may have been
            // cleared or replaced while the dialog was up.
            _state = AppState.State;
        }
    }

    private void AnimationFrame()
    {
        var invalidate = false;

        if (_tweening)
        {
            var p = (Environment.TickCount64 - _scStart) / 450.0;
            if (p >= 1.0)
            {
                p = 1.0;
                _tweening = false;
            }
            _scCurrent = LerpColor(_scFrom, _scTo, Easing.FastOutSlowIn((float)p));
            invalidate = true;
        }

        if (_state.Connecting)
            invalidate = true; // the halo pulse runs every frame while connecting

        if (invalidate) Invalidate();
        else _tick.Stop();
    }

    /// <summary>Background pulse: 0.5→1 over <see cref="PulseDuration"/>, reversing.</summary>
    private float Pulse
    {
        get
        {
            if (!_state.Connecting) return 0.5f;
            var phase = (Environment.TickCount64 - _pulseStart) % (PulseDuration * 2L);
            var p = phase < PulseDuration
                ? (float)phase / PulseDuration
                : 1f - (float)(phase - PulseDuration) / PulseDuration;
            return 0.5f + 0.5f * Easing.FastOutSlowIn(p);
        }
    }

    private static Color LerpColor(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t),
        (int)(a.G + (b.G - a.G) * t),
        (int)(a.B + (b.B - a.B) * t));

    // ---- input --------------------------------------------------------------

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        if (_menuRect.Contains(e.Location))
        {
            // The control drawer opens here once ControlDrawer lands.
        }
    }

    // ---- painting -----------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        PaintAirBackground(g);
        PaintHeader(g);
    }

    /// <summary>AirBackground (MainActivity 1204): base gradient, corner key
    /// light, state halo behind the ring, accent bloom at the bottom edge.</summary>
    private void PaintAirBackground(Graphics g)
    {
        var w = ClientSize.Width;
        var h = ClientSize.Height;
        var rect = new RectangleF(0, 0, w, h);

        // Base: #1B2030 → Bg → #0C0E15
        using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
            rect, Color.Transparent, Color.Transparent, 90f))
        {
            brush.InterpolationColors = new System.Drawing.Drawing2D.ColorBlend(3)
            {
                Positions = new[] { 0f, 0.5f, 1f },
                Colors = new[]
                {
                    Color.FromArgb(0xFF, 0x1B, 0x20, 0x30),
                    DeltaTorTheme.Bg,
                    Color.FromArgb(0xFF, 0x0C, 0x0E, 0x15)
                }
            };
            g.FillRectangle(brush, rect);
        }

        // Soft key light in the top-left corner.
        FillRadial(g, new PointF(w * 0.16f, h * 0.10f), w * 0.55f,
            Color.FromArgb(13, Color.White)); // white.copy(alpha = 0.05f)

        // State-colored halo exactly behind the ring; its alpha carries the
        // connect pulse (0.16 * pulse).
        var halo = (int)(255 * 0.16f * Pulse);
        FillRadial(g, new PointF(w / 2f, h / 2f), w * 0.58f,
            Color.FromArgb(halo, _scCurrent));

        // Faint accent bloom at the bottom edge.
        FillRadial(g, new PointF(w / 2f, h * 0.98f), w * 0.7f,
            Color.FromArgb(18, DeltaTorTheme.Accent)); // accent.copy(alpha = 0.07f)
    }

    private static void FillRadial(Graphics g, PointF center, float radius, Color inner)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddEllipse(center.X - radius, center.Y - radius, radius * 2f, radius * 2f);
        using var brush = new System.Drawing.Drawing2D.PathGradientBrush(path)
        {
            CenterPoint = center,
            CenterColor = inner,
            SurroundColors = new[] { Color.Transparent }
        };
        g.FillEllipse(brush, center.X - radius, center.Y - radius, radius * 2f, radius * 2f);
    }

    /// <summary>Header (MainActivity 1264): drawer button, DELTA·TOR wordmark,
    /// status chip, and the transparent→Border→transparent rule under them.</summary>
    private void PaintHeader(Graphics g)
    {
        const float padX = 16f;
        const float padY = 14f;
        const float box = 34f;

        // Drawer button: Surface box, radius 11, BorderLight hairline, menu glyph.
        var menu = new RectangleF(padX, padY, box, box);
        _menuRect = menu;
        using (var path = DrawUtil.RoundedRect(menu, 11f))
        {
            using var fill = new SolidBrush(DeltaTorTheme.Surface);
            g.FillPath(fill, path);
            using var pen = new Pen(DeltaTorTheme.BorderLight);
            g.DrawPath(pen, path);
        }
        var glyph = new RectangleF(
            menu.X + (box - 15f) / 2f, menu.Y + (box - 15f) / 2f, 15f, 15f);
        Icons.Menu(g, glyph, DeltaTorTheme.AccentLight);

        // Wordmark: "DELTA" in Text + "TOR" in AccentLight, letterSpacing 2.6.
        using var wmFont = new Font(DeltaTorTheme.FontFamilyName, 10.5f, FontStyle.Bold);
        var lineH = g.MeasureString("A", wmFont).Height;
        var wmY = menu.Y + (box - lineH) / 2f;
        var x = menu.Right + 11f;
        using (var brush = new SolidBrush(DeltaTorTheme.Text))
        {
            DrawUtil.DrawSpaced(g, "DELTA", wmFont, brush, x, wmY, 200f, 2.6f);
            x += DrawUtil.SpacedWidth(g, "DELTA", wmFont, 2.6f) + 2.6f;
        }
        using (var brush = new SolidBrush(DeltaTorTheme.AccentLight))
            DrawUtil.DrawSpaced(g, "TOR", wmFont, brush, x, wmY, 200f, 2.6f);

        // Status chip, right-aligned on the menu row: Surface pill, BorderLight
        // hairline, 9px state dot, labelSmall bold in the state color.
        var label = UiHelpers.StatusLabel(_state);
        using var capFont = new Font(
            DeltaTorTheme.FontFamilyName, DeltaTorTheme.CaptionPt, FontStyle.Bold);
        var textH = g.MeasureString(label, capFont).Height;
        var textW = DrawUtil.SpacedWidth(g, label, capFont, 1.1f);
        var chipH = Math.Max(9f, textH) + 12f;
        var chipW = 12f + 9f + 8f + textW + 12f;
        var chip = new RectangleF(
            ClientSize.Width - padX - chipW, menu.Y + (box - chipH) / 2f, chipW, chipH);
        using (var path = DrawUtil.RoundedRect(chip, chipH / 2f))
        {
            using var fill = new SolidBrush(DeltaTorTheme.Surface);
            g.FillPath(fill, path);
            using var pen = new Pen(DeltaTorTheme.BorderLight);
            g.DrawPath(pen, path);
        }
        using (var dot = new SolidBrush(_scCurrent))
            g.FillEllipse(dot, chip.X + 12f, chip.Y + (chipH - 9f) / 2f, 9f, 9f);
        using (var brush = new SolidBrush(_scCurrent))
            DrawUtil.DrawSpaced(g, label, capFont, brush,
                chip.X + 12f + 9f + 8f, chip.Y + (chipH - textH) / 2f, textW + 2f, 1.1f);

        // Rule: transparent → Border → transparent, 14 below the row.
        var ruleY = menu.Bottom + 14f;
        using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
            new RectangleF(padX, ruleY, ClientSize.Width - padX * 2f, 1f),
            Color.Transparent, Color.Transparent, 0f))
        {
            brush.InterpolationColors = new System.Drawing.Drawing2D.ColorBlend(3)
            {
                Positions = new[] { 0f, 0.5f, 1f },
                Colors = new[] { Color.Transparent, DeltaTorTheme.Border, Color.Transparent }
            };
            g.FillRectangle(brush, padX, ruleY, ClientSize.Width - padX * 2f, 1f);
        }
    }
}
