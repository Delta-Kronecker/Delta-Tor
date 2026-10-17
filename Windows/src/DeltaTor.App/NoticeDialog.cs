using DeltaTor.App.Controls;
using DeltaTor.Core;

namespace DeltaTor.App;

/// <summary>
/// The modal notice (MainActivity NoticeDialog, line 516): a rounded
/// gradient card with the amber warn icon and optional amber title, one
/// drawn text row per block so an RTL block lays out right-to-left, a body
/// capped at 420px (scrollable beyond that) and the fixed full-width OK
/// pill below it. The body is owner-drawn directly on the card — no Label
/// controls, because a transparent Label inside an AutoScroll panel is a
/// known WinForms combination that silently fails to paint its text.
/// Every dismissal path acknowledges the notice.
/// </summary>
public sealed class NoticeDialog : Form
{
    private readonly Notice _notice;
    private readonly CardPanel _card;
    private readonly GradientPill _ok;
    private readonly Font _leadFont;

    // One drawn row per lead/text line-group; spacer rows carry an empty
    // string and exist only for their height (block gap, lead gap).
    private readonly List<Row> _rows = new();
    private float _contentHeight;
    private float _scroll;

    private const int DialogWidth = 460;
    private const int Pad = 20;
    private const int BlockGap = 14;
    private const int LeadGap = 5;
    private const int MaxBlocksHeight = 420;
    private const int ButtonHeight = 44;
    private const int IconBox = 34;
    private const int IconBoxRadius = 11;

    // Actual width: the parent minus 24px margins per side (Android's
    // fillMaxWidth inside the dialog window), capped at the material max.
    private int _dialogW = DialogWidth;
    private int _blocksTop; // viewport top inside the card
    private int _blocksH;   // viewport height (content may overflow it)

    private sealed record Row(string Text, Font Font, bool Rtl, float Height);

    public NoticeDialog(Notice notice)
    {
        _notice = notice;
        // Not disposed: the rows share this instance for their lifetime.
        _leadFont = new Font(DeltaTorTheme.FontFamilyName, DeltaTorTheme.BodyPt, FontStyle.Bold);

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = DeltaTorTheme.Bg;

        _card = new CardPanel(this);
        _ok = new GradientPill
        {
            Filled = true,
            Radius = 13,
            Label = "OK"
        };

        MeasureBlocks(_dialogW - Pad * 2);

        Controls.Add(_card);
        Controls.Add(_ok);
        _ok.BringToFront();

        _ok.Clicked += (_, _) => Close();
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) Close();
        };

        Width = _dialogW;
        LayoutDialog();
    }

    /// <summary>Re-measure the rows at the current content width. Uses
    /// TextRenderer (GDI) so the measured height matches what
    /// TextRenderer.DrawText renders — Graphics.MeasureString and GDI can
    /// disagree by a pixel or two and clip the last line.</summary>
    private void MeasureBlocks(int w)
    {
        _rows.Clear();
        for (var i = 0; i < _notice.Blocks.Count; i++)
        {
            var block = _notice.Blocks[i];
            if (i > 0) _rows.Add(new Row("", DeltaTorTheme.Body, false, BlockGap));

            if (!string.IsNullOrWhiteSpace(block.Lead))
            {
                var leadSize = Measure(block.Lead, _leadFont, w, block.Rtl);
                _rows.Add(new Row(block.Lead, _leadFont, block.Rtl, leadSize.Height));
                _rows.Add(new Row("", DeltaTorTheme.Body, false, LeadGap));
            }

            var textSize = Measure(block.Text, DeltaTorTheme.Body, w, block.Rtl);
            _rows.Add(new Row(block.Text, DeltaTorTheme.Body, block.Rtl, textSize.Height));
        }

        _contentHeight = 0f;
        foreach (var row in _rows) _contentHeight += row.Height;
        _blocksH = (int)Math.Min(_contentHeight, MaxBlocksHeight);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (Owner is not { } owner) return;
        var w = Math.Clamp(owner.ClientSize.Width - Pad * 2 - 8, 320, DialogWidth);
        if (w != _dialogW)
        {
            _dialogW = w;
            Width = w;
            MeasureBlocks(w - Pad * 2);
            LayoutDialog();
        }
        // CenterParent can misplace on a borderless owner whose visible frame
        // does not match the client area — center explicitly on the owner.
        Location = new Point(
            owner.Location.X + (owner.Width - Width) / 2,
            owner.Location.Y + (owner.Height - Height) / 2);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        var max = Math.Max(0f, _contentHeight - _blocksH);
        if (max <= 0f) return;
        _scroll = Math.Clamp(_scroll - e.Delta / 120f * 40f, 0f, max);
        _card.Invalidate();
    }

    private static Size Measure(string text, Font font, int width, bool rtl)
    {
        var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding |
                    TextFormatFlags.GlyphOverhangPadding |
                    (rtl ? TextFormatFlags.RightToLeft : TextFormatFlags.Left);
        return TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue), flags);
    }

    private void LayoutDialog()
    {
        var headerH = IconBox;
        _blocksTop = Pad + headerH + 14;

        // The button sits below the scroll viewport and never scrolls with it.
        _ok.SetBounds(Pad, _blocksTop + _blocksH + 20, _dialogW - Pad * 2, ButtonHeight);

        var height = _blocksTop + _blocksH + 20 + ButtonHeight + Pad;
        _card.SetBounds(0, 0, _dialogW, height);
        Height = height;
        // Clip the borderless form to the card's rounded outline so the
        // square corners do not show the form BackColor as a black frame.
        using (var path = DrawUtil.RoundedRect(
            new RectangleF(0f, 0f, _dialogW, height), 22f))
            Region = new Region(path);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        // Dismissing is acknowledging, whatever the path (OK, Esc): the id
        // check means a dismissal that arrives after a newer notice replaced
        // this one cannot take the newer one down.
        AppState.ClearNotice(_notice.Id);
    }

    /// <summary>The dialog card: vertical #20242F→Surface gradient, 22px
    /// radius, BorderLight hairline, the warn-icon header, and the body
    /// rows drawn directly on the gradient (clipped to the scroll
    /// viewport, offset by the scroll position).</summary>
    private sealed class CardPanel : Panel
    {
        private readonly NoticeDialog _owner;

        public CardPanel(NoticeDialog owner)
        {
            _owner = owner;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = DeltaTorTheme.Bg;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            using (var path = DrawUtil.RoundedRect(rect, 22f))
            {
                var top = Color.FromArgb(0xFF, 0x20, 0x24, 0x2F);
                using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                    rect, top, DeltaTorTheme.Surface, 90f))
                    g.FillPath(brush, path);
                using var pen = new Pen(DeltaTorTheme.BorderLight);
                g.DrawPath(pen, path);
            }

            var notice = _owner._notice;

            // Icon box: 34px, radius 11, Amber 16% fill, WarnIcon 16px centered.
            var box = new RectangleF(Pad, Pad, IconBox, IconBox);
            using (var path = DrawUtil.RoundedRect(box, IconBoxRadius))
            {
                using var fill = new SolidBrush(Color.FromArgb(41, DeltaTorTheme.Amber)); // 0.16f
                g.FillPath(fill, path);
            }
            var warn = new RectangleF(
                box.X + (IconBox - 16) / 2f, box.Y + (IconBox - 16) / 2f, 16f, 16f);
            Icons.Warn(g, warn, DeltaTorTheme.Amber);

            // Title: labelSmall, letterSpacing 1.5, bold, AmberLight.
            if (!string.IsNullOrWhiteSpace(notice.Title))
            {
                using var font = new Font(
                    DeltaTorTheme.FontFamilyName, DeltaTorTheme.CaptionPt, FontStyle.Bold);
                using var brush = new SolidBrush(DeltaTorTheme.AmberLight);
                var titleH = g.MeasureString(notice.Title, font).Height;
                DrawUtil.DrawSpaced(g, notice.Title, font, brush,
                    box.Right + 12f, box.Y + (IconBox - titleH) / 2f,
                    Width - box.Right - 12f - Pad, 1.5f);
            }

            // Body rows: clipped to the viewport, translated by the scroll
            // offset so only the visible slice is drawn.
            var w = _owner._dialogW - Pad * 2;
            var viewport = new Rectangle(0, _owner._blocksTop, Width, _owner._blocksH);
            var saved = g.Save();
            g.SetClip(viewport);
            g.TranslateTransform(0f, -_owner._scroll);
            var y = (float)_owner._blocksTop;
            foreach (var row in _owner._rows)
            {
                if (row.Text.Length > 0)
                {
                    var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding |
                        TextFormatFlags.GlyphOverhangPadding |
                        (row.Rtl
                            ? TextFormatFlags.RightToLeft | TextFormatFlags.Right
                            : TextFormatFlags.Left);
                    TextRenderer.DrawText(g, row.Text, row.Font,
                        new Rectangle(Pad, (int)y, w, (int)Math.Ceiling(row.Height)),
                        DeltaTorTheme.Text, flags);
                }
                y += row.Height;
            }
            g.Restore(saved);
        }
    }
}
