using DeltaTor.App.Controls;
using DeltaTor.Core;

namespace DeltaTor.App;

/// <summary>
/// The modal notice (MainActivity NoticeDialog, line 516): a rounded
/// gradient card with the amber warn icon and optional amber title, one
/// paragraph row per direction so an RTL block lays out right-to-left, a
/// body capped at 420px (scrollable beyond that) and the fixed full-width
/// OK pill below it. Every dismissal path acknowledges the notice.
/// </summary>
public sealed class NoticeDialog : Form
{
    private readonly Notice _notice;
    private readonly CardPanel _card;
    private readonly Panel _blocks;
    private readonly GradientPill _ok;
    private readonly List<Label> _leadLabels = new();
    private readonly List<Label> _textLabels = new();
    private readonly List<Size> _leadSizes = new();
    private readonly List<Size> _textSizes = new();
    private readonly bool[] _hasLead;
    private readonly Font _leadFont;

    private const int DialogWidth = 460;
    private const int Pad = 20;
    private const int BlockGap = 14;
    private const int LeadGap = 5;
    private const int MaxBlocksHeight = 420;
    private const int ButtonHeight = 44;
    private const int IconBox = 34;
    private const int IconBoxRadius = 11;

    public NoticeDialog(Notice notice)
    {
        _notice = notice;
        _hasLead = new bool[notice.Blocks.Count];
        // Not disposed: the labels share this instance for their lifetime.
        _leadFont = new Font(DeltaTorTheme.FontFamilyName, DeltaTorTheme.BodyPt, FontStyle.Bold);

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = DeltaTorTheme.Bg;

        _card = new CardPanel(this);
        _blocks = new Panel
        {
            BackColor = DeltaTorTheme.Surface,
            AutoScroll = true
        };
        _ok = new GradientPill
        {
            Filled = true,
            Radius = 13,
            Label = "OK"
        };

        var w = DialogWidth - Pad * 2;
        for (var i = 0; i < notice.Blocks.Count; i++)
        {
            var block = notice.Blocks[i];
            var rtl = block.Rtl ? RightToLeft.Yes : RightToLeft.No;

            if (!string.IsNullOrWhiteSpace(block.Lead))
            {
                _hasLead[i] = true;
                var lead = MakeLabel(block.Lead, _leadFont, rtl);
                _leadLabels.Add(lead);
                _leadSizes.Add(Measure(block.Lead, _leadFont, w));
                _blocks.Controls.Add(lead);
            }

            var text = MakeLabel(block.Text, DeltaTorTheme.Body, rtl);
            _textLabels.Add(text);
            _textSizes.Add(Measure(block.Text, DeltaTorTheme.Body, w));
            _blocks.Controls.Add(text);
        }

        Controls.Add(_card);
        Controls.Add(_blocks);
        Controls.Add(_ok);

        _ok.Clicked += (_, _) => Close();
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) Close();
        };

        Width = DialogWidth;
        LayoutDialog();
    }

    private static Label MakeLabel(string text, Font font, RightToLeft rtl) => new()
    {
        Text = text,
        Font = font,
        ForeColor = DeltaTorTheme.Text,
        BackColor = Color.Transparent,
        RightToLeft = rtl,
        AutoSize = false,
        TextAlign = ContentAlignment.TopLeft
    };

    private static Size Measure(string text, Font font, int width) =>
        TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

    private void LayoutDialog()
    {
        var w = DialogWidth - Pad * 2;

        // Header row: 34px icon box; the title is drawn by the card next to
        // it (skipped entirely when blank — a blank heading leaves a
        // conspicuous gap where a heading should be).
        var headerH = IconBox;

        // Rows are placed in the body's content space (x=0), so AutoScroll
        // offsets them with the scroll position.
        var y = 0;
        var li = 0;
        var ti = 0;
        for (var i = 0; i < _notice.Blocks.Count; i++)
        {
            if (i > 0) y += BlockGap;
            if (_hasLead[i])
            {
                _leadLabels[li].SetBounds(0, y, w, _leadSizes[li].Height);
                y += _leadSizes[li].Height + LeadGap;
                li++;
            }
            _textLabels[ti].SetBounds(0, y, w, _textSizes[ti].Height);
            y += _textSizes[ti].Height;
            ti++;
        }
        var blocksTotal = y;
        var blocksH = Math.Min(blocksTotal, MaxBlocksHeight);

        _blocks.SetBounds(Pad, Pad + headerH + 14, w, blocksH);
        _blocks.AutoScrollMinSize = new Size(0, blocksTotal);

        // The button sits below the scroll area and never scrolls with it.
        _ok.SetBounds(Pad, Pad + headerH + 14 + blocksH + 20, w, ButtonHeight);

        var height = Pad + headerH + 14 + blocksH + 20 + ButtonHeight + Pad;
        _card.SetBounds(0, 0, DialogWidth, height);
        Height = height;
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
    /// radius, BorderLight hairline, and the warn-icon header.</summary>
    private sealed class CardPanel : Panel
    {
        private readonly NoticeDialog _owner;

        public CardPanel(NoticeDialog owner)
        {
            _owner = owner;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
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
        }
    }
}
