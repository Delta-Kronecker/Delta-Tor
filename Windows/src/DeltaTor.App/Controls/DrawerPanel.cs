using System.Drawing.Drawing2D;
using DeltaTor.Core;

namespace DeltaTor.App.Controls;

/// <summary>
/// The control drawer: a fixed CONTROLS header with a close box, a scrolling
/// flow of section nodes, and the GITHUB pill docked at the bottom.
///
/// The flow is built into a flat node list whenever something structural
/// changes (a toggle, a selection, live data arriving, a resize) and painted
/// by intersecting each node's rectangle with the viewport. A scroll frame
/// draws only the nodes that are on screen and re-measures nothing; hits are
/// stored in content coordinates and converted once per click, so a scrolled
/// hit cannot drift. Child controls (text boxes, switches, spinner) are
/// hidden at the start of every frame and re-placed only by the node that
/// owns them, so nothing can float over the header, the footer pill or the
/// dropdown. This file is a from-scratch rewrite of the original per-paint
/// painter: the same features, none of its font churn, frame-lagged content
/// height or viewport bugs.
/// </summary>
public sealed partial class DrawerPanel : Panel
{
    private const string RepoUrl = "https://github.com/Delta-Kronecker/Delta-Tor";

    // Card geometry shared by the ADVANCED builders.
    private const float CardMargin = 20f;
    private const float CardPadX = 16f;
    private const float CardPadY = 10f;
    private const float CardGap = 16f;

    // ---- type ---------------------------------------------------------------
    // One face per role, created once and disposed with the control. Sizes
    // are tuned for the 450px sheet: compact letter-spaced titles over
    // generous body and mono text, so nothing shouts and nothing is tiny.
    private readonly Font _fTitle = new(DeltaTorTheme.FontFamilyName, 12f, FontStyle.Bold);
    private readonly Font _fTitleSub = new(DeltaTorTheme.FontFamilyName, 8.5f);
    private readonly Font _fSection = new(DeltaTorTheme.FontFamilyName, 10f, FontStyle.Bold);
    private readonly Font _fSummary = new(DeltaTorTheme.FontFamilyName, 9.5f);
    private readonly Font _fCardTitle = new(DeltaTorTheme.FontFamilyName, 8.5f, FontStyle.Bold);
    private readonly Font _fLabel = new(DeltaTorTheme.FontFamilyName, 8.5f, FontStyle.Bold);
    private readonly Font _fLabelReg = new(DeltaTorTheme.FontFamilyName, 8.5f);
    private readonly Font _fBody = new(DeltaTorTheme.FontFamilyName, 9.5f);
    private readonly Font _fBodyBold = new(DeltaTorTheme.FontFamilyName, 9.5f, FontStyle.Bold);
    private readonly Font _fRow = new(DeltaTorTheme.FontFamilyName, 10.5f, FontStyle.Bold);
    private readonly Font _fName = new(DeltaTorTheme.FontFamilyName, 10.5f);
    private readonly Font _fNameBold = new(DeltaTorTheme.FontFamilyName, 10.5f, FontStyle.Bold);
    private readonly Font _fStat = new(DeltaTorTheme.FontFamilyName, 11f, FontStyle.Bold);
    private readonly Font _fTiny = new(DeltaTorTheme.FontFamilyName, 8f, FontStyle.Bold);
    private readonly Font _fMono = new("Consolas", 10f);
    private readonly Font _fMonoBig = new("Consolas", 11f, FontStyle.Bold);
    private Font? _fEmoji;

    private readonly float _titleLineH;
    private readonly float _titleSubLineH;
    private readonly float _sectionLineH;
    private readonly float _summaryLineH;
    private readonly float _cardTitleLineH;
    private readonly float _labelLineH;
    private readonly float _bodyLineH;
    private readonly float _rowLineH;
    private readonly float _nameLineH;
    private readonly float _statLineH;
    private readonly float _tinyLineH;
    private readonly float _monoLineH;
    private readonly float _monoBigLineH;
    private readonly float _emojiLineH;

    // ---- flow model ---------------------------------------------------------
    /// <summary>One laid-out row of the scrollable flow.</summary>
    private sealed class Node
    {
        public float Y; // top, content coordinates
        public float H;
        public Action<Graphics, float>? Draw; // g, node top in screen coordinates
        public Action? Click;
    }

    /// <summary>Builder the section code appends nodes to; Y advances as it goes.</summary>
    private sealed class Flow
    {
        public readonly List<Node> Nodes = new();
        public float Y { get; private set; }
        public float W { get; }

        public Flow(float w) => W = w;

        public void Add(float h, Action<Graphics, float>? draw, Action? click = null)
        {
            Nodes.Add(new Node { Y = Y, H = h, Draw = draw, Click = click });
            Y += h;
        }
    }

    private sealed class Drop
    {
        public required List<(string Value, string Label)> Options { get; init; }
        public required RectangleF Trigger { get; init; } // content coordinates
        public required string Value { get; init; }
        public required Action<string> OnSelect { get; init; }
        public readonly List<(RectangleF Rect, string Value)> Rows = new(); // screen
    }

    private readonly List<Node> _nodes = new();
    private readonly List<(RectangleF Rect, Action Act)> _fixedHits = new();  // screen
    private readonly List<(RectangleF Rect, Action Act)> _scrollHits = new(); // content
    private Drop? _drop;

    private bool _dirty = true;
    private float _contentH;
    private float _scroll;
    private float _viewTop;
    private float _viewBottom;

    // ---- chrome and animation ----------------------------------------------
    private readonly GradientPill _github = new() { Label = "GITHUB", Filled = true };
    private readonly System.Windows.Forms.Timer _slide = new() { Interval = 15 };
    private readonly System.Windows.Forms.Timer _sync = new() { Interval = 1000 };
    private bool _open;
    private bool _closing;
    private long _slideStart;
    private float _slideFrom;

    public DrawerPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.Selectable, true);
        DoubleBuffered = true;
        BackColor = Color.FromArgb(0xFF, 0x14, 0x17, 0x1F); // ModalDrawerSheet
        Visible = false;

        Controls.Add(_github);
        _github.Clicked += (_, _) => ReleaseChecker.OpenInBrowser(RepoUrl);

        using (var bmp = new Bitmap(1, 1))
        using (var mg = Graphics.FromImage(bmp))
        {
            _titleLineH = mg.MeasureString("A", _fTitle).Height;
            _titleSubLineH = mg.MeasureString("A", _fTitleSub).Height;
            _sectionLineH = mg.MeasureString("A", _fSection).Height;
            _summaryLineH = mg.MeasureString("A", _fSummary).Height;
            _cardTitleLineH = mg.MeasureString("A", _fCardTitle).Height;
            _labelLineH = mg.MeasureString("A", _fLabel).Height;
            _bodyLineH = mg.MeasureString("A", _fBody).Height;
            _rowLineH = mg.MeasureString("A", _fRow).Height;
            _nameLineH = mg.MeasureString("A", _fName).Height;
            _statLineH = mg.MeasureString("A", _fStat).Height;
            _tinyLineH = mg.MeasureString("A", _fTiny).Height;
            _monoLineH = mg.MeasureString("A", _fMono).Height;
            _monoBigLineH = mg.MeasureString("A", _fMonoBig).Height;
            _fEmoji = EmojiFont();
            _emojiLineH = mg.MeasureString("A", _fEmoji).Height;
        }

        _slide.Tick += (_, _) => SlideFrame();
        // The capacity table and the country list arrive in the background
        // and raise no event of their own, so an open drawer rebuilds from
        // live data once a second instead of sitting stale.
        _sync.Tick += (_, _) =>
        {
            if (!Visible) return;
            MarkDirty();
            Invalidate();
        };
    }

    public bool IsOpen => _open;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _slide.Dispose();
            _sync.Dispose();
            _fTitle.Dispose();
            _fTitleSub.Dispose();
            _fSection.Dispose();
            _fSummary.Dispose();
            _fCardTitle.Dispose();
            _fLabel.Dispose();
            _fLabelReg.Dispose();
            _fBody.Dispose();
            _fBodyBold.Dispose();
            _fRow.Dispose();
            _fName.Dispose();
            _fNameBold.Dispose();
            _fStat.Dispose();
            _fTiny.Dispose();
            _fMono.Dispose();
            _fMonoBig.Dispose();
            _fEmoji?.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---- lifecycle ----------------------------------------------------------

    /// <summary>The layout is stale: the next paint rebuilds it from live state.</summary>
    private void MarkDirty() => _dirty = true;

    /// <summary>Called by MainForm whenever the shared state ticks, so the
    /// selection summary, endpoints and store rows stay live.</summary>
    public void NotifyStateChanged()
    {
        if (IsDisposed || !Visible) return;
        MarkDirty();
        Invalidate();
    }

    /// <summary>Slide in from the left (ModalNavigationDrawer's open).</summary>
    public void Open()
    {
        _sync.Start();
        if (_open && !_closing)
        {
            Focus();
            return;
        }
        if (Parent != null)
            SetBounds(0, 0, Parent.ClientSize.Width, Parent.ClientSize.Height);
        _open = true;
        _closing = false;
        Visible = true;
        Left = -Width;
        Focus();
        MarkDirty();
        _slideFrom = Left;
        _slideStart = Environment.TickCount64;
        _slide.Start();
        Invalidate();
    }

    /// <summary>Slide back out (close, Esc, or the close box).</summary>
    public void Close()
    {
        if (!_open) return;
        _open = false;
        _closing = true;
        _drop = null;
        _slideFrom = Left;
        _slideStart = Environment.TickCount64;
        _slide.Start();
    }

    private void SlideFrame()
    {
        var p = (Environment.TickCount64 - _slideStart) / 250.0;
        if (p >= 1.0)
        {
            p = 1.0;
            _slide.Stop();
        }
        var t = Easing.FastOutSlowIn((float)p);
        var target = _closing ? -Width : 0f;
        Left = (int)(_slideFrom + (target - _slideFrom) * t);
        if (_closing && p >= 1.0)
        {
            Visible = false;
            _sync.Stop();
        }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        MarkDirty();
        if (Visible) Invalidate();
    }

    // ---- input --------------------------------------------------------------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus(); // keys (Esc, arrows) go to the drawer, not the form behind it
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_drop != null)
        {
            _drop = null;
            Invalidate();
        }
        ScrollBy(-e.Delta / 120f * 60f);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;

        // The dropdown overlay is modal: pick a row, or dismiss.
        if (_drop != null)
        {
            foreach (var (rect, value) in _drop.Rows)
            {
                if (!rect.Contains(e.Location)) continue;
                var select = _drop.OnSelect;
                var picked = value;
                _drop = null;
                select(picked);
                Invalidate();
                return;
            }
            _drop = null;
            Invalidate();
            return;
        }

        foreach (var (rect, act) in _fixedHits)
        {
            if (!rect.Contains(e.Location)) continue;
            act();
            return;
        }

        // Scroll-region hits are content coordinates; convert once here.
        var cy = e.Y - _viewTop + _scroll;
        foreach (var (rect, act) in _scrollHits)
        {
            if (!rect.Contains(e.X, cy)) continue;
            act();
            return;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Escape:
                if (_drop != null)
                {
                    _drop = null;
                    Invalidate();
                }
                else
                {
                    Close();
                }
                e.Handled = true;
                break;
            case Keys.Down:
                ScrollBy(60f);
                e.Handled = true;
                break;
            case Keys.Up:
                ScrollBy(-60f);
                e.Handled = true;
                break;
            case Keys.PageDown:
                ScrollBy((_viewBottom - _viewTop) * 0.9f);
                e.Handled = true;
                break;
            case Keys.PageUp:
                ScrollBy(-(_viewBottom - _viewTop) * 0.9f);
                e.Handled = true;
                break;
            case Keys.Home:
                ScrollTo(0f);
                e.Handled = true;
                break;
            case Keys.End:
                ScrollTo(MaxScroll);
                e.Handled = true;
                break;
        }
        if (e.Handled) e.SuppressKeyPress = true;
    }

    private float MaxScroll => Math.Max(0f, _contentH - (_viewBottom - _viewTop));

    private void ScrollBy(float delta) => ScrollTo(_scroll + delta);

    private void ScrollTo(float target)
    {
        var next = Math.Clamp(target, 0f, MaxScroll);
        if (next == _scroll) return;
        _scroll = next;
        Invalidate();
    }

    // ---- painting -----------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        _fixedHits.Clear();
        _scrollHits.Clear();

        // Header first: it defines the viewport the layout clamps against.
        PaintHeader(g);
        if (_dirty) Rebuild();

        // Children are placed only by the node that owns them. Anything not
        // placed this frame stays hidden — including while the dropdown is
        // open, so a TextBox can never sit on top of it.
        foreach (Control c in Controls)
            if (!ReferenceEquals(c, _github))
                c.Visible = false;

        var w = Width;
        var clip = g.Save();
        g.SetClip(new RectangleF(0f, _viewTop, w, _viewBottom - _viewTop));
        foreach (var node in _nodes)
        {
            var sy = node.Y - _scroll + _viewTop;
            if (sy + node.H < _viewTop || sy > _viewBottom) continue;
            if (node.Click != null)
                _scrollHits.Add((new RectangleF(0f, node.Y, w, node.H), node.Click));
            node.Draw?.Invoke(g, sy);
        }
        g.Restore(clip);

        PaintScrollbar(g);
        PaintDropdown(g);
        _github.SetBounds(20, Height - 56, w - 40, 40);
    }

    /// <summary>Build the node list from live state; called only when dirty,
    /// never on a scroll frame.</summary>
    private void Rebuild()
    {
        _dirty = false;
        var flow = new Flow(Width);
        BuildLocation(flow);
        flow.Add(1f, (g, sy) => PaintGradientLine(g, sy, Width));
        BuildAdvanced(flow);

        _nodes.Clear();
        _nodes.AddRange(flow.Nodes);
        _contentH = flow.Y;

        // The clamp runs against the real height right here: no rubber band
        // from last frame's measurement.
        _scroll = Math.Clamp(_scroll, 0f, MaxScroll);
    }

    /// <summary>Fixed header: title, subtitle, close box, gradient rule.
    /// Sets the viewport bounds everything else uses.</summary>
    private void PaintHeader(Graphics g)
    {
        var w = Width;
        var colH = _titleLineH + 3f + _titleSubLineH;
        var rowH = Math.Max(36f, colH);
        const float rowY = 18f;
        var colY = rowY + (rowH - colH) / 2f;

        using (var b = new SolidBrush(DeltaTorTheme.Text))
            DrawUtil.DrawSpaced(g, "CONTROLS", _fTitle, b, 20f, colY, w - 70f, 2.4f);
        using (var b = new SolidBrush(DeltaTorTheme.Muted))
            g.DrawString("Everything here applies on the next connect",
                _fTitleSub, b, 20f, colY + _titleLineH + 3f);

        var close = new RectangleF(w - 16f - 36f, rowY + (rowH - 36f) / 2f, 36f, 36f);
        using (var path = DrawUtil.RoundedRect(close, 12f))
        {
            using var fill = new SolidBrush(DeltaTorTheme.Surface);
            g.FillPath(fill, path);
            using var pen = new Pen(DeltaTorTheme.BorderLight);
            g.DrawPath(pen, path);
        }
        Icons.Close(g,
            new RectangleF(close.X + (36f - 13f) / 2f, close.Y + (36f - 13f) / 2f, 13f, 13f),
            DeltaTorTheme.Text);
        _fixedHits.Add((close, Close));

        var dividerY = rowY + rowH + 16f;
        PaintGradientLine(g, dividerY, w);
        _viewTop = dividerY + 1f;
        _viewBottom = Height - 60f; // footer: 16 bottom + 40 pill + 4 above
    }

    /// <summary>Thin rounded thumb on the right edge while content overflows.</summary>
    private void PaintScrollbar(Graphics g)
    {
        var max = MaxScroll;
        if (max <= 1f) return;
        var viewH = _viewBottom - _viewTop;
        if (viewH <= 0f || _contentH <= 0f) return;
        var thumbH = Math.Max(28f, viewH * viewH / _contentH);
        var t = Math.Clamp(_scroll / max, 0f, 1f);
        var y = _viewTop + 3f + t * (viewH - 6f - thumbH);
        var rect = new RectangleF(Width - 7f, y, 3f, thumbH);
        using var path = DrawUtil.RoundedRect(rect, 1.5f);
        using var fill = new SolidBrush(Color.FromArgb(120, DeltaTorTheme.BorderLight));
        g.FillPath(fill, path);
    }

    private static void PaintGradientLine(Graphics g, float y, float w)
    {
        using var brush = new LinearGradientBrush(
            new RectangleF(0f, y, 10f, 1f), Color.Transparent, Color.Transparent, 0f);
        brush.InterpolationColors = new ColorBlend(3)
        {
            Positions = new[] { 0f, 0.5f, 1f },
            Colors = new[] { Color.Transparent, DeltaTorTheme.Border, Color.Transparent }
        };
        g.FillRectangle(brush, 0f, y, w, 1f);
    }

    // ---- shared layout helpers ---------------------------------------------

    /// <summary>Word-wrapped size of a block; layout-time, no Graphics needed.</summary>
    private static Size Wrap(string text, Font font, float w) =>
        TextRenderer.MeasureText(text, font,
            new Size(Math.Max(1, (int)w), int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

    /// <summary>Draw a block previously sized by Wrap, at screen coordinates.</summary>
    private static void DrawWrap(Graphics g, string text, Font font, Color color,
        float x, float y, Size size) =>
        TextRenderer.DrawText(g, text, font,
            new Rectangle((int)x, (int)y, size.Width + 2, size.Height), color,
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

    /// <summary>Position a child control inside the viewport. Children draw
    /// above the painted sheet, so one is shown only when it lies fully
    /// inside the viewport — never over the header, the footer pill or the
    /// dropdown.</summary>
    private void PlaceChild(Control c, RectangleF screenRect)
    {
        c.SetBounds((int)screenRect.X, (int)screenRect.Y,
            (int)screenRect.Width, (int)screenRect.Height);
        c.Visible = _drop == null &&
            screenRect.Top >= _viewTop && screenRect.Bottom <= _viewBottom;
    }

    /// <summary>DrawerSection (MainActivity 1038): letter-spaced title,
    /// summary under it, SHOW/HIDE and a rotating chevron; the whole row
    /// toggles the section.</summary>
    private void AddSectionHeader(
        Flow flow, string title, string summary, bool expanded, Action onClick)
    {
        var w = flow.W;
        var h = 16f + _sectionLineH + 4f + _summaryLineH + 10f;
        flow.Add(h, (g, sy) =>
        {
            using (var b = new SolidBrush(DeltaTorTheme.Text))
                DrawUtil.DrawSpaced(g, title, _fSection, b, 20f, sy + 16f, w * 0.5f, 1.8f);
            using (var b = new SolidBrush(DeltaTorTheme.Muted))
                DrawUtil.DrawSpaced(g, summary, _fSummary, b, 20f,
                    sy + 16f + _sectionLineH + 4f, w - 150f, 0.2f);

            var chev = new RectangleF(w - 20f - 12f,
                sy + 16f + (_sectionLineH + 4f + _summaryLineH - 12f) / 2f, 12f, 12f);
            var action = expanded ? "HIDE" : "SHOW";
            var aw = DrawUtil.SpacedWidth(action, _fLabel, 1.2f);
            using (var b = new SolidBrush(
                expanded ? DeltaTorTheme.AccentLight : DeltaTorTheme.Muted))
                DrawUtil.DrawSpaced(g, action, _fLabel, b,
                    chev.X - 8f - aw, chev.Y - 2f, aw + 2f, 1.2f);
            Icons.Chevron(g, chev, DeltaTorTheme.AccentLight, expanded ? 180f : 0f);
        }, onClick);
    }

    // ---- dropdown overlay ---------------------------------------------------

    private void PaintDropdown(Graphics g)
    {
        if (_drop == null) return;
        var d = _drop;
        const float rowH = 30f;
        var panelH = rowH * d.Options.Count + 8f;
        var triggerBottom = d.Trigger.Bottom - _scroll + _viewTop;
        var panelY = Math.Max(_viewTop + 4f,
            Math.Min(triggerBottom + 2f, _viewBottom - panelH - 4f));
        var panel = new RectangleF(d.Trigger.X, panelY, d.Trigger.Width, panelH);
        d.Rows.Clear();

        using (var path = DrawUtil.RoundedRect(panel, 8f))
        {
            using var fill = new SolidBrush(DeltaTorTheme.SurfaceAlt);
            g.FillPath(fill, path);
            using var pen = new Pen(DeltaTorTheme.BorderLight);
            g.DrawPath(pen, path);
        }

        var ry = panel.Y + 4f;
        foreach (var (value, label) in d.Options)
        {
            var rect = new RectangleF(panel.X + 4f, ry, panel.Width - 8f, rowH);
            var selected = value == d.Value;
            if (selected)
            {
                using var path = DrawUtil.RoundedRect(rect, 6f);
                using var fill = new SolidBrush(Color.FromArgb(31, DeltaTorTheme.Accent));
                g.FillPath(fill, path);
            }
            using (var b = new SolidBrush(
                selected ? DeltaTorTheme.AccentLight : DeltaTorTheme.Text))
                DrawUtil.DrawSpaced(g, label, selected ? _fBodyBold : _fBody, b,
                    rect.X + 10f, rect.Y + (rowH - _bodyLineH) / 2f,
                    rect.Width - 20f, 0.2f);
            d.Rows.Add((rect, value));
            ry += rowH;
        }
    }

    private void OpenDrop(
        RectangleF trigger, List<(string Value, string Label)> options,
        string value, Action<string> onSelect)
    {
        _drop = new Drop
        {
            Options = options,
            Trigger = trigger,
            Value = value,
            OnSelect = onSelect
        };
        Invalidate();
    }
}
