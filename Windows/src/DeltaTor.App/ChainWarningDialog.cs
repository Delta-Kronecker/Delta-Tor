using DeltaTor.App.Controls;

namespace DeltaTor.App;

/// <summary>
/// ChainProxyModeWarning (MainActivity 2352): proxy mode gives the chain
/// nothing to chain onto, and switching is the user's call, never the app's.
/// A rounded card with the title, one body paragraph, and the two pill
/// choices — "Turn proxy mode off" (<see cref="DialogResult.OK"/>) or "Keep
/// proxy mode" (<see cref="DialogResult.Cancel"/>). Owner-drawn like
/// <see cref="NoticeDialog"/>: no Labels inside the card, because a
/// transparent Label in an owner-painted container is a known WinForms
/// combination that silently fails to paint.
/// </summary>
public sealed class ChainWarningDialog : Form
{
    private const int DialogWidth = 460;
    private const int Pad = 20;
    private const int TitleGap = 10;
    private const int ButtonHeight = 44;
    private const int ButtonGap = 10;

    private const string TitleText = "The chain needs the VPN";

    private const string BodyText =
        "No-VPN proxy mode captures no traffic and opens no tunnel, so there is " +
        "nothing a chain can chain onto. Choosing a server turns the VPN back " +
        "on: the chain will run on top of Tor, exactly like it would in the " +
        "normal mode.";

    private readonly CardPanel _card;
    private readonly GradientPill _confirm;
    private readonly GradientPill _dismiss;

    private int _dialogW = DialogWidth;
    private readonly int _titleH;

    public ChainWarningDialog()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = DeltaTorTheme.Bg;

        _titleH = TextRenderer.MeasureText(
            TitleText, DeltaTorTheme.Body, Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Height;
        _card = new CardPanel(this);
        _confirm = new GradientPill
        {
            Filled = true,
            Radius = 13,
            Label = "Turn proxy mode off"
        };
        _dismiss = new GradientPill
        {
            Filled = false,
            ShowBorder = true,
            Radius = 13,
            Label = "Keep proxy mode"
        };

        Controls.Add(_card);
        Controls.Add(_confirm);
        Controls.Add(_dismiss);
        _confirm.BringToFront();
        _dismiss.BringToFront();

        _confirm.Clicked += (_, _) =>
        {
            DialogResult = DialogResult.OK;
            Close();
        };
        _dismiss.Clicked += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };
        KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Escape) return;
            DialogResult = DialogResult.Cancel;
            Close();
        };

        Width = _dialogW;
        LayoutDialog();
    }

    private static Size MeasureBody(int w) =>
        TextRenderer.MeasureText(
            BodyText, DeltaTorTheme.Body, new Size(w, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding |
            TextFormatFlags.GlyphOverhangPadding | TextFormatFlags.Left);

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var owner = Owner;
        if (owner is null) return;

        var w = Math.Clamp(owner.ClientSize.Width - Pad * 2 - 8, 320, DialogWidth);
        if (w != _dialogW)
        {
            _dialogW = w;
            Width = w;
            // _bodyH cannot be reassigned: recompute the layout from scratch.
            LayoutDialog();
        }

        // CenterParent can misplace on a borderless owner whose visible frame
        // does not match its client area — center explicitly on the owner.
        Location = new Point(
            owner.Location.X + (owner.Width - Width) / 2,
            owner.Location.Y + (owner.Height - Height) / 2);
    }

    private void LayoutDialog()
    {
        var bodyH = MeasureBody(_dialogW - Pad * 2).Height;
        var bodyTop = Pad + _titleH + TitleGap;
        var buttonsTop = bodyTop + bodyH + 18;

        var innerW = _dialogW - Pad * 2;
        var halfW = (innerW - ButtonGap) / 2;
        _dismiss.SetBounds(Pad, buttonsTop, halfW, ButtonHeight);
        _confirm.SetBounds(Pad + halfW + ButtonGap, buttonsTop, halfW, ButtonHeight);

        var height = buttonsTop + ButtonHeight + Pad;
        _card.SetBounds(0, 0, _dialogW, height);
        Height = height;
        using (var path = DrawUtil.RoundedRect(
            new RectangleF(0f, 0f, _dialogW, height), 22f))
            Region = new Region(path);
    }

    private sealed class CardPanel : Panel
    {
        private readonly ChainWarningDialog _owner;

        public CardPanel(ChainWarningDialog owner)
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

            // Title: Android's AlertDialog title, in DeltaTor.Text.
            using (var brush = new SolidBrush(DeltaTorTheme.Text))
                TextRenderer.DrawText(g, TitleText, DeltaTorTheme.Body,
                    new Rectangle(Pad, Pad, _owner._dialogW - Pad * 2, _owner._titleH),
                    DeltaTorTheme.Text,
                    TextFormatFlags.NoPadding | TextFormatFlags.Left |
                    TextFormatFlags.SingleLine);

            // Body: one wrapped paragraph.
            var bodyTop = Pad + _owner._titleH + TitleGap;
            TextRenderer.DrawText(g, BodyText, DeltaTorTheme.Body,
                new Rectangle(Pad, bodyTop, _owner._dialogW - Pad * 2, Height - bodyTop),
                DeltaTorTheme.Text,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding |
                TextFormatFlags.GlyphOverhangPadding | TextFormatFlags.Left);
        }
    }
}
