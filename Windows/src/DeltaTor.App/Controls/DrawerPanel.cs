using DeltaTor.Core;

namespace DeltaTor.App.Controls;

/// <summary>
/// The full-screen control drawer (MainActivity ControlDrawer, line 656):
/// slides in from the left over the main screen, holds the CONTROLS header,
/// the LOCATION picker (country rows, capacity group headers, warn card),
/// the ADVANCED section and the GITHUB pill. Rows are painted and hit-tested
/// against the same rects; only the pill is a child control.
/// </summary>
public sealed partial class DrawerPanel : Panel
{
    private const string RepoUrl = "https://github.com/Delta-Kronecker/Delta-Tor";
    private const int PickerTop = 25; // EXIT_PICKER_TOP (MainActivity 499)

    private readonly List<(RectangleF Rect, Action Act, bool InView)> _hits = new();
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private float _viewTop;
    private float _viewBottom;
    private readonly GradientPill _github = new() { Label = "GITHUB", Filled = true };
    private readonly System.Windows.Forms.Timer _slide = new() { Interval = 15 };
    private readonly System.Windows.Forms.Timer _sync = new() { Interval = 1000 };

    // Fonts for the painted cards (owned here, created once and shared across
    // paints — a per-paint `new Font` churned GDI handles and made every
    // scroll frame pay for it). Sizes are retuned for the 420px-wide drawer:
    // titles down, descriptions up, so a card reads title → body instead of
    // shouting over it.
    private readonly Font _fCaption = new(DeltaTorTheme.FontFamilyName,
        8f, FontStyle.Bold);
    private readonly Font _fCaptionReg = new(DeltaTorTheme.FontFamilyName,
        8f, FontStyle.Regular);
    private readonly Font _fBody = DeltaTorTheme.Body;
    private readonly Font _fBodyBold = new(DeltaTorTheme.FontFamilyName,
        DeltaTorTheme.BodyPt, FontStyle.Bold);
    private readonly Font _fSmall = new(DeltaTorTheme.FontFamilyName, 9f);
    private readonly Font _fLarge = new(DeltaTorTheme.FontFamilyName, 10f, FontStyle.Bold);
    private readonly Font _fMono = new("Consolas", 9f);
    private readonly Font _fMonoBig = new("Consolas", 10f, FontStyle.Bold);
    private readonly Font _fTiny = new(DeltaTorTheme.FontFamilyName, 7.5f, FontStyle.Bold);

    // Header, section and row type, cached like the rest.
    private readonly Font _fHeader = new(DeltaTorTheme.FontFamilyName, 11f, FontStyle.Bold);
    private readonly Font _fHeaderSub = new(DeltaTorTheme.FontFamilyName, 8.5f);
    private readonly Font _fSection = new(DeltaTorTheme.FontFamilyName, 9f, FontStyle.Bold);
    private readonly Font _fSummary = new(DeltaTorTheme.FontFamilyName, 10f);
    private readonly Font _fNameReg = new(DeltaTorTheme.FontFamilyName, 10f);
    private readonly Font _fNameBold = new(DeltaTorTheme.FontFamilyName, 10f, FontStyle.Bold);
    private Font? _fEmoji; // EmojiFont(): the family lookup runs once

    private float _captionLineH;
    private float _bodyLineH;
    private float _largeLineH;
    private float _smallLineH;
    private float _tinyLineH;
    private float _monoBigLineH;
    private float _headerLineH;
    private float _headerSubLineH;
    private float _sectionLineH;
    private float _summaryLineH;
    private float _nameLineH;
    private float _emojiLineH;

    private bool _open;
    private bool _closing;
    private long _slideStart;
    private float _slideFrom;
    private float _contentH;
    private int _scroll;

    private bool _showCountries;
    private bool _showAdvanced;
    private bool _showAllCountries;

    private IReadOnlyList<ExitCountry> _directory = Array.Empty<ExitCountry>();
    private int _dirCapCount = -1;

    public DrawerPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
        BackColor = Color.FromArgb(0xFF, 0x14, 0x17, 0x1F); // ModalDrawerSheet
        Visible = false;

        Controls.Add(_github);
        _github.Clicked += (_, _) => ReleaseChecker.OpenInBrowser(RepoUrl);

        using (var bmp = new Bitmap(1, 1))
        using (var mg = Graphics.FromImage(bmp))
        {
            _captionLineH = mg.MeasureString("A", _fCaption).Height;
            _bodyLineH = mg.MeasureString("A", _fBody).Height;
            _largeLineH = mg.MeasureString("A", _fLarge).Height;
            _smallLineH = mg.MeasureString("A", _fSmall).Height;
            _tinyLineH = mg.MeasureString("A", _fTiny).Height;
            _monoBigLineH = mg.MeasureString("A", _fMonoBig).Height;
            _headerLineH = mg.MeasureString("A", _fHeader).Height;
            _headerSubLineH = mg.MeasureString("A", _fHeaderSub).Height;
            _sectionLineH = mg.MeasureString("A", _fSection).Height;
            _summaryLineH = mg.MeasureString("A", _fSummary).Height;
            _nameLineH = mg.MeasureString("A", _fNameReg).Height;
            _fEmoji = EmojiFont();
            _emojiLineH = mg.MeasureString("A", _fEmoji).Height;
        }

        _slide.Tick += (_, _) => SlideFrame();
        // The capacity table and the country list arrive in the background and
        // raise no event of their own, so a visible drawer re-checks them while
        // it is open instead of sitting stale.
        _sync.Tick += (_, _) =>
        {
            if (Visible) Invalidate();
        };
    }

    public bool IsOpen => _open;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _slide.Dispose();
            _sync.Dispose();
            _fCaption.Dispose();
            _fCaptionReg.Dispose();
            _fBodyBold.Dispose();
            _fSmall.Dispose();
            _fLarge.Dispose();
            _fMono.Dispose();
            _fMonoBig.Dispose();
            _fTiny.Dispose();
            _fHeader.Dispose();
            _fHeaderSub.Dispose();
            _fSection.Dispose();
            _fSummary.Dispose();
            _fNameReg.Dispose();
            _fNameBold.Dispose();
            _fEmoji?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Slide in from the left (ModalNavigationDrawer's open).</summary>
    public void Open()
    {
        _sync.Start();
        if (_open && !_closing) { Focus(); return; }
        if (Parent != null)
            SetBounds(0, 0, Parent.ClientSize.Width, Parent.ClientSize.Height);
        _open = true;
        _closing = false;
        Visible = true;
        Left = -Width;
        Focus();
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

    /// <summary>Called by MainForm whenever the shared state ticks, so the
    /// selection summary and rows stay live without the drawer collecting the
    /// whole VpnState itself.</summary>
    public void NotifyStateChanged() => Invalidate();

    // ---- input --------------------------------------------------------------

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_drop != null)
        {
            _drop = null;
            Invalidate();
        }
        _scroll = Math.Max(0, _scroll - e.Delta / 120 * 60);
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;

        // An open dropdown consumes the click: pick a row, or dismiss.
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

        var inView = e.Y >= _viewTop && e.Y < _viewBottom;
        foreach (var (rect, act, row) in _hits)
        {
            if (row != inView) continue;
            if (!rect.Contains(e.Location)) continue;
            act();
            return;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
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
        }
    }

    // ---- painting -----------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        _hits.Clear();

        var w = Width;

        // Header row: CONTROLS + subtitle, close box on the right.
        var titleH = _headerLineH;
        var subH = _headerSubLineH;
        var colH = titleH + 3f + subH;
        var rowH = Math.Max(36f, colH);
        var rowY = 18f;

        using (var brush = new SolidBrush(DeltaTorTheme.Text))
            DrawUtil.DrawSpaced(g, "CONTROLS", _fHeader, brush,
                20f, rowY + (rowH - colH) / 2f, w - 70f, 2.4f);
        using (var brush = new SolidBrush(DeltaTorTheme.Muted))
            g.DrawString("Everything here applies on the next connect",
                _fHeaderSub, brush, 20f, rowY + (rowH - colH) / 2f + titleH + 3f);

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
        _hits.Add((close, Close, false));

        var dividerY = rowY + rowH + 16f;
        PaintGradientLine(g, dividerY);
        var viewTop = dividerY + 1f;
        var viewBottom = Height - 60f; // GITHUB pill: 16 bottom + 40 + 4 top
        var viewH = Math.Max(0f, viewBottom - viewTop);
        _viewTop = viewTop;
        _viewBottom = viewBottom;

        // Scroll clamp from the previous pass's content height.
        var maxScroll = Math.Max(0f, _contentH - viewH);
        if (_scroll > maxScroll) _scroll = (int)maxScroll;

        var state = g.Save();
        g.SetClip(new RectangleF(0, viewTop, w, viewH));
        var y = viewTop - _scroll;
        y = PaintLocation(g, y, w);
        PaintGradientLine(g, y);
        y += 1f;
        y = PaintAdvanced(g, y, w);
        g.Restore(state);

        _contentH = y + _scroll - viewTop;

        // The dropdown popup floats above the clipped content region.
        PaintDropdown(g);

        _github.SetBounds(20, Height - 56, w - 40, 40);
    }

    private static void PaintGradientLine(Graphics g, float y)
    {
        using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
            new RectangleF(0, y, 10, 1), Color.Transparent, Color.Transparent, 0f);
        brush.InterpolationColors = new System.Drawing.Drawing2D.ColorBlend(3)
        {
            Positions = new[] { 0f, 0.5f, 1f },
            Colors = new[] { Color.Transparent, DeltaTorTheme.Border, Color.Transparent }
        };
        g.FillRectangle(brush, 0, y, 10000f, 1f);
    }

    // ---- LOCATION -----------------------------------------------------------

    private float PaintLocation(Graphics g, float y, float w)
    {
        _selected.Clear();
        foreach (var c in ExitNodes.Codes) _selected.Add(c);
        var selection = SelectionSummary();
        y = PaintSection(g, y, w, "LOCATION", selection, _showCountries,
            () => { _showCountries = !_showCountries; Invalidate(); });
        if (!_showCountries) return y;

        var codes = ExitNodes.Codes;
        y = PaintWarnCard(g, y, w, codes.Count > 0, () =>
        {
            ExitNodes.Clear();
            Invalidate();
        });

        // "Any location · default" row.
        y = PaintCountryRow(g, y, w, "🌐", "Any location · default", "--",
            _selected.Count == 0, 0, 0, () =>
            {
                ExitNodes.Clear();
                Invalidate();
            }, last: false);

        var directory = Directory();
        if (directory.Count == 0)
        {
            y = PaintLoading(g, y, w);
            return y;
        }

        var capacity = ExitCapacityIndex.ByCountry;
        var known = capacity.Count > 0;
        var withExits = known ? directory.Take(PickerTop).ToList() : new List<ExitCountry>();
        var without = known ? directory.Skip(withExits.Count).ToList() : directory.ToList();

        if (known && withExits.Count > 0)
        {
            y = PaintGroupHeader(g, y, w,
                "COUNTRIES WITH THE MOST EXIT BANDWIDTH",
                $"TOP {withExits.Count} OF {directory.Count}", false, null);
        }
        else if (!known)
        {
            y = PaintGroupHeader(g, y, w, "COUNTRIES", "EXIT DATA NOT LOADED", true, null);
        }

        for (var i = 0; i < withExits.Count; i++)
        {
            var c = withExits[i];
            capacity.TryGetValue(c.Code, out var cap);
            var last = without.Count == 0 && i == withExits.Count - 1;
            y = PaintCountryRow(g, y, w, UiHelpers.FlagEmoji(c.Code), c.Name, c.Code,
                _selected.Contains(c.Code), cap?.Exits ?? 0, cap?.Weight ?? 0,
                () =>
                {
                    ExitNodes.Toggle(c.Code, c.Name);
                    Invalidate();
                }, last);
        }

        if (known && without.Count > 0)
        {
            // A country picked before the relay data arrived is in this group
            // and nowhere else, so the group opens itself rather than hiding
            // the selection.
            var holdsSelection = without.Any(c => _selected.Contains(c.Code));
            y = PaintGroupHeader(g, y, w,
                "REST OF WORLD · MOST HAVE NO EXIT",
                _showAllCountries ? "HIDE" : "SHOW ALL", true, () =>
                {
                    // The header clears a selection that lives in this group
                    // before it collapses, so nothing gets stuck hidden.
                    if (holdsSelection) ExitNodes.Clear();
                    _showAllCountries = !_showAllCountries;
                    Invalidate();
                });

            if (_showAllCountries || without.Any(c => _selected.Contains(c.Code)))
            {
                for (var i = 0; i < without.Count; i++)
                {
                    var c = without[i];
                    var last = i == without.Count - 1;
                    y = PaintCountryRow(g, y, w, UiHelpers.FlagEmoji(c.Code), c.Name, c.Code,
                        _selected.Contains(c.Code), 0, 0, () =>
                        {
                            ExitNodes.Toggle(c.Code, c.Name);
                            Invalidate();
                        }, last);
                }
            }
        }

        return y;
    }

    private string SelectionSummary()
    {
        var codes = ExitNodes.Codes;
        if (codes.Count == 0) return "Any location · default";
        if (codes.Count == 1)
        {
            var only = codes[0];
            var names = ExitNodes.Names;
            return $"{UiHelpers.FlagEmoji(only)}  {(names.TryGetValue(only, out var n) ? n : "")}".Trim();
        }
        return $"{codes.Count} countries selected";
    }

    /// <summary>The picker order (ExitNodes.order, MainActivity 90): capacity
    /// head first, the rest alphabetical. Cached until the capacity table grows.</summary>
    private IReadOnlyList<ExitCountry> Directory()
    {
        var cap = ExitCapacityIndex.ByCountry;
        if (_dirCapCount == cap.Count && _directory.Count > 0) return _directory;
        var all = BridgeCountries.TopSync();
        if (cap.Count == 0)
        {
            _directory = all;
            _dirCapCount = 0;
            return _directory;
        }
        var byCode = all.ToDictionary(c => c.Code, StringComparer.Ordinal);
        var head = cap
            .OrderByDescending(kv => kv.Value.Weight)
            .Select(kv => byCode.TryGetValue(kv.Key, out var c) ? c : null)
            .Where(c => c != null)
            .Cast<ExitCountry>()
            .ToList();
        var rest = all
            .Where(c => !cap.ContainsKey(c.Code))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _directory = head.Concat(rest).ToList();
        _dirCapCount = cap.Count;
        return _directory;
    }

    /// <summary>DrawerSection (MainActivity 1038): title + summary with a
    /// SHOW/HIDE toggle and rotating chevron.</summary>
    private float PaintSection(
        Graphics g, float y, float w, string title, string summary,
        bool expanded, Action onClick)
    {
        var titleH = _sectionLineH;
        var summaryH = _summaryLineH;
        var h = 18f + titleH + 4f + summaryH + 12f;

        using (var brush = new SolidBrush(DeltaTorTheme.Text))
            DrawUtil.DrawSpaced(g, title, _fSection, brush, 20f, y + 18f, w * 0.5f, 2f);
        using (var brush = new SolidBrush(DeltaTorTheme.Muted))
            DrawUtil.DrawSpaced(g, summary, _fSummary, brush,
                20f, y + 18f + titleH + 4f, w - 40f - 90f, 0.2f);

        var chev = new RectangleF(w - 20f - 12f, y + 18f + (titleH + 4f + summaryH - 12f) / 2f, 12f, 12f);
        var actionText = expanded ? "HIDE" : "SHOW";
        var actionW = DrawUtil.SpacedWidth(g, actionText, _fCaption, 1.2f);
        using (var brush = new SolidBrush(expanded ? DeltaTorTheme.AccentLight : DeltaTorTheme.Muted))
            DrawUtil.DrawSpaced(g, actionText, _fCaption, brush,
                chev.X - 8f - actionW, chev.Y - 2f, actionW + 2f, 1.2f);
        Icons.Chevron(g, chev, DeltaTorTheme.AccentLight, expanded ? 180f : 0f);

        _hits.Add((new RectangleF(0, y, w, h), onClick, true));
        return y + h;
    }

    /// <summary>The amber caution card over the country list (MainActivity 706).</summary>
    private float PaintWarnCard(
        Graphics g, float y, float w, bool canClear, Action onClear)
    {
        const float marginX = 20f;
        var cardW = w - marginX * 2f;
        var labelFont = _fCaption;
        var bodyFont = _fSmall;
        var clearFont = _fCaption;

        var innerX = marginX + 16f;
        var innerW = cardW - 32f;
        var boxX = innerX + 12f;
        var boxW = innerW - 24f;
        var labelH = _captionLineH;
        var paragraph =
            "Picking a country sends your traffic through a relay there. " +
            "It can lower your speed and make the connection less stable.";
        var paraSize = TextRenderer.MeasureText(paragraph, bodyFont,
            new Size((int)boxW, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

        var headH = Math.Max(16f, labelH);
        var boxH = 11f + headH + 6f + paraSize.Height + 11f;
        var cardH = 8f + boxH + 8f;

        // Off-screen: the height was all this pass needed (the y-flow still
        // has to advance), so the gradient card, the icon and the paragraph
        // are skipped — this is what keeps scrolling the country list smooth.
        if (y + cardH < _viewTop || y > _viewBottom)
            return y + cardH;

        var card = new RectangleF(marginX, y, cardW, cardH);
        using (var path = DrawUtil.RoundedRect(card, 18f))
        {
            using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                card, Color.FromArgb(0xFF, 0x20, 0x24, 0x2F), DeltaTorTheme.Surface, 90f);
            g.FillPath(brush, path);
            using var pen = new Pen(DeltaTorTheme.Border);
            g.DrawPath(pen, path);
        }

        var box = new RectangleF(innerX, y + 8f, innerW, boxH);
        using (var path = DrawUtil.RoundedRect(box, 12f))
        {
            using var fill = new SolidBrush(Color.FromArgb(26, DeltaTorTheme.Amber)); // 0.10
            g.FillPath(fill, path);
        }

        Icons.Warn(g, new RectangleF(boxX, box.Y + 11f + (headH - 16f) / 2f, 16f, 16f),
            DeltaTorTheme.Amber);
        using (var brush = new SolidBrush(DeltaTorTheme.Amber))
            DrawUtil.DrawSpaced(g, "USE ONLY WHEN NEEDED", labelFont, brush,
                boxX + 16f + 8f, box.Y + 11f + (headH - labelH) / 2f,
                boxW - 24f - 16f - 8f, 1.2f);

        if (canClear)
        {
            var clearW = DrawUtil.SpacedWidth(g, "CLEAR", clearFont, 1.2f);
            var clearRect = new RectangleF(
                box.Right - 12f - clearW - 10f,
                box.Y + 11f + (headH - labelH) / 2f - 3f,
                clearW + 20f, labelH + 6f);
            using (var brush = new SolidBrush(DeltaTorTheme.Red))
                DrawUtil.DrawSpaced(g, "CLEAR", clearFont, brush,
                    clearRect.X + 10f, clearRect.Y + 3f, clearW + 2f, 1.2f);
            _hits.Add((clearRect, onClear, true));
        }

        TextRenderer.DrawText(g, paragraph, bodyFont,
            new Rectangle((int)boxX, (int)(box.Y + 11f + headH + 6f),
                (int)boxW, (int)paraSize.Height),
            DeltaTorTheme.Muted,
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

        return y + cardH;
    }

    /// <summary>CountryRow (MainActivity 2798): emoji, name, capacity numbers,
    /// code and the check circle, over a DrawerRow divider.</summary>
    private float PaintCountryRow(
        Graphics g, float y, float w,
        string emoji, string name, string code, bool selected,
        int exits, double share, Action onClick, bool last)
    {
        const float inset = 20f;

        // Heights are font properties, not text properties: one measure per
        // font at startup replaces two MeasureString calls per row per paint.
        var blockH = Math.Max(17f, Math.Max(_emojiLineH, _nameLineH));
        var h = 10f + blockH + 10f;

        // Off-screen rows only advance the y-flow. Nothing is measured,
        // drawn or hit-tested — the country list is ~60 rows deep and this
        // is what makes scrolling it cheap.
        if (y + h < _viewTop || y > _viewBottom)
            return y + h + (last ? 0f : 1f);

        var emojiFont = EmojiFont();
        var nameFont = selected ? _fNameBold : _fNameReg;
        var capFont = _fCaption;
        var capRegFont = _fCaptionReg;
        var nameH = _nameLineH;
        var emojiH = _emojiLineH;

        var row = new RectangleF(inset, y, w - inset * 2f, h);
        if (selected)
        {
            using var path = DrawUtil.RoundedRect(row, 10f);
            using var fill = new SolidBrush(Color.FromArgb(31, DeltaTorTheme.Accent)); // 0.12
            g.FillPath(fill, path);
        }

        var contentY = y + (h - blockH) / 2f;
        var x = row.X + 8f;
        using (var brush = new SolidBrush(DeltaTorTheme.Text))
            g.DrawString(emoji, emojiFont, brush, x, contentY + (17f - emojiH) / 2f);
        x += 19f + 12f;

        // Capacity numbers sit between the name and the code.
        var tailW = 10f + DrawUtil.SpacedWidth(g, code, capRegFont, 1f) + 10f + 16f + 8f;
        if (exits > 0)
            tailW += g.MeasureString("00.0%", capFont).Width + 8f
                + g.MeasureString("000", capFont).Width + 10f;
        var nameMaxW = row.Right - 8f - x - tailW;
        using (var brush = new SolidBrush(selected ? DeltaTorTheme.Text : DeltaTorTheme.Muted))
            DrawUtil.DrawSpaced(g, name, nameFont, brush,
                x, contentY + (blockH - nameH) / 2f, nameMaxW, 0f);

        var right = row.Right - 8f;
        // Check circle, 16px, at the far right.
        var circle = new RectangleF(right - 16f, y + (h - 16f) / 2f, 16f, 16f);
        using (var path = DrawUtil.RoundedRect(circle, 8f))
        {
            if (selected)
            {
                using var fill = new SolidBrush(DeltaTorTheme.AccentLight);
                g.FillPath(fill, path);
            }
            else
            {
                using var pen = new Pen(DeltaTorTheme.Border);
                g.DrawPath(pen, path);
            }
        }
        if (selected)
            Icons.Check(g,
                new RectangleF(circle.X + 3f, circle.Y + 3f, 10f, 10f),
                Color.FromArgb(0xFF, 0x14, 0x17, 0x1F));
        right -= 16f + 10f;

        // Code.
        var codeW = DrawUtil.SpacedWidth(g, code, capRegFont, 1f);
        var codeH = g.MeasureString(code, capRegFont).Height;
        using (var brush = new SolidBrush(Color.FromArgb(204, DeltaTorTheme.Muted)))
            DrawUtil.DrawSpaced(g, code, capRegFont, brush,
                right - codeW, contentY + (blockH - codeH) / 2f, codeW + 2f, 1f);
        right -= codeW + 10f;

        if (exits > 0)
        {
            var shareText = $"{UiHelpers.Format1((float)(share * 100))}%";
            var shareW = g.MeasureString(shareText, capFont).Width;
            var exitsW = g.MeasureString($"{exits}", capFont).Width;
            var capLineH = g.MeasureString($"{exits}", capFont).Height;
            right -= exitsW + 8f;
            using (var brush = new SolidBrush(Color.FromArgb(140, DeltaTorTheme.Muted)))
                g.DrawString($"{exits}", capFont, brush, right,
                    contentY + (blockH - capLineH) / 2f);
            right -= shareW + 8f;
            using (var brush = new SolidBrush(selected
                ? DeltaTorTheme.AccentLight
                : Color.FromArgb(217, DeltaTorTheme.Muted)))
                g.DrawString(shareText, capFont, brush, right,
                    contentY + (blockH - capLineH) / 2f);
        }

        _hits.Add((row, onClick, true));
        if (!last)
        {
            using var div = new SolidBrush(Color.FromArgb(153, DeltaTorTheme.BorderLight)); // 0.6
            g.FillRectangle(div, inset, y + h, w - inset * 2f, 1f);
        }
        return y + h + (last ? 0f : 1f);
    }

    /// <summary>DrawerGroupHeader (MainActivity 2756).</summary>
    private float PaintGroupHeader(
        Graphics g, float y, float w, string title, string trailing,
        bool muted, Action? onClick)
    {
        var font = _fCaption;
        var regFont = _fCaptionReg;
        var lineH = _captionLineH;
        var h = 14f + lineH + 6f;

        // Off-screen group headers still cost height, nothing else.
        if (y + h < _viewTop || y > _viewBottom)
        {
            if (onClick != null)
                _hits.Add((new RectangleF(0, y, w, h), onClick, true));
            return y + h;
        }

        var color = muted
            ? Color.FromArgb(153, DeltaTorTheme.Muted) // Muted 0.6
            : DeltaTorTheme.Muted;
        using (var brush = new SolidBrush(color))
            g.DrawString(title, font, brush, 26f, y + 14f);

        var trailingW = g.MeasureString(trailing, onClick != null ? font : regFont).Width;
        using (var brush = new SolidBrush(
            onClick != null ? DeltaTorTheme.AccentLight : Color.FromArgb(179, color))) // 0.7
            g.DrawString(trailing, onClick != null ? font : regFont, brush,
                w - 20f - trailingW, y + 14f);

        if (onClick != null)
            _hits.Add((new RectangleF(0, y, w, h), onClick, true));
        return y + h;
    }

    private float PaintLoading(Graphics g, float y, float w)
    {
        var h = 12f + _smallLineH + 12f;
        if (y + h >= _viewTop && y <= _viewBottom)
        {
            using var brush = new SolidBrush(Color.FromArgb(189, DeltaTorTheme.Muted)); // 0.75
            g.DrawString("Reading country list …", _fSmall, brush, 26f, y + 12f);
        }
        return y + h;
    }

    // ---- ADVANCED -----------------------------------------------------------

    private float PaintAdvanced(Graphics g, float y, float w)
    {
        y = PaintSection(g, y, w, "ADVANCED",
            "Transport, bridges, torrc and the log", _showAdvanced,
            () => { _showAdvanced = !_showAdvanced; Invalidate(); });
        if (!_showAdvanced)
        {
            HideCardControls();
            return y;
        }

        // Android syncs the form from the live mode (LaunchedEffect(liveMode));
        // once the service has a mode, it wins over the stored one.
        var live = AppState.Mode;
        if (live.Length > 0 && live != _transportMode) _transportMode = live;

        y = PaintTransportCard(g, y, w);
        if (_transportMode == ParallelTorManager.TransportAuto)
            y = PaintAutoRacersCard(g, y, w);
        if (_transportMode == ParallelTorManager.TransportCustom)
            y = PaintCustomBridgesCard(g, y, w);
        else if (_customBox != null)
            _customBox.Visible = false;
        y = PaintProxyCard(g, y, w);
        y = PaintTorrcCard(g, y, w);
        y = PaintBridgeStoreCard(g, y, w);
        y = PaintLogCard(g, y, w);
        return y;
    }

    /// <summary>The emoji face, found once: the original per-row helper
    /// walked FontFamily.Families — every installed family — per row per
    /// paint, which was the single most expensive thing in a scroll frame.</summary>
    private Font EmojiFont()
    {
        if (_fEmoji != null) return _fEmoji;
        foreach (var f in FontFamily.Families)
        {
            if (f.Name is not ("Segoe UI Emoji" or "Segoe UI Symbol")) continue;
            _fEmoji = new Font(f, 13f);
            return _fEmoji;
        }
        _fEmoji = new Font(DeltaTorTheme.FontFamilyName, 13f);
        return _fEmoji;
    }
}
