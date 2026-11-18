using DeltaTor.App.Controls;
using DeltaTor.Core;

namespace DeltaTor.App;

/// <summary>
/// The startup language picker (Android LanguagePickerDialog): English,
/// Russian and Persian, each offered in its own script.
///
/// Shown when no language is stored, which covers a fresh install and an
/// update of an install that predates the preference — the same condition on
/// both platforms. It cannot be dismissed without a choice: the first-run and
/// chain-intro notices are worded from the answer, and a default nobody picked
/// would be the wrong one for the user who needs the picker at all.
/// </summary>
public sealed class LanguageDialog : Form
{
    private const int DialogWidth = 380;
    private const int Pad = 20;
    private const int ButtonHeight = 44;
    private const int ButtonGap = 10;
    private const int ButtonsTop = 98;

    private readonly GradientPill _english;
    private readonly GradientPill _russian;
    private readonly GradientPill _persian;

    /// <summary>The code picked, empty until one of the buttons is pressed.</summary>
    public string Selected { get; private set; } = "";

    public LanguageDialog()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = DeltaTorTheme.Bg;
        DoubleBuffered = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        _english = MakeButton("English", 0);
        _russian = MakeButton("Русский", 1);
        _persian = MakeButton("فارسی", 2);
        _english.Clicked += (_, _) => Pick(Config.LanguageEnglish);
        _russian.Clicked += (_, _) => Pick(Config.LanguageRussian);
        _persian.Clicked += (_, _) => Pick(Config.LanguagePersian);

        Controls.Add(_english);
        Controls.Add(_russian);
        Controls.Add(_persian);

        // No close action, and Alt+F4 must not get around that either: while
        // nothing is picked there is no answer to store and nothing to word.
        FormClosing += (_, e) =>
        {
            if (Selected.Length == 0) e.Cancel = true;
        };

        ClientSize = new Size(DialogWidth, ButtonsTop + ButtonHeight * 3 + ButtonGap * 2 + Pad);
        LayoutButtons();
    }

    private static GradientPill MakeButton(string label, int tabIndex) => new()
    {
        Label = label,
        Filled = false,
        Radius = 13,
        TabIndex = tabIndex,
        AccessibleName = label,
        Height = ButtonHeight
    };

    private void Pick(string code)
    {
        Selected = code;
        Config.Language = code;
        Close();
    }

    private void LayoutButtons()
    {
        // Base Form construction can raise OnResize before the buttons exist.
        if (_english is null) return;
        var width = Math.Max(0, ClientSize.Width - Pad * 2);
        var y = ButtonsTop;
        foreach (var pill in new[] { _english, _russian, _persian })
        {
            pill.SetBounds(Pad, y, width, ButtonHeight);
            y += ButtonHeight + ButtonGap;
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutButtons();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Clip the borderless window to the card's rounded outline, and put
        // the keyboard on the first choice so Space/Enter alone are enough.
        using (var path = DrawUtil.RoundedRect(
            new RectangleF(0f, 0f, ClientSize.Width, ClientSize.Height), 22f))
            Region = new Region(path);
        _english.Focus();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var rect = new RectangleF(0.5f, 0.5f, ClientSize.Width - 1f, ClientSize.Height - 1f);
        using (var path = DrawUtil.RoundedRect(rect, 22f))
        {
            var top = Color.FromArgb(0xFF, 0x20, 0x24, 0x2F);
            using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                rect, top, DeltaTorTheme.Surface, 90f))
                g.FillPath(brush, path);
            using var pen = new Pen(DeltaTorTheme.BorderLight);
            g.DrawPath(pen, path);
        }

        var innerW = ClientSize.Width - Pad * 2;
        TextRenderer.DrawText(g, "Choose your language", DeltaTorTheme.Title,
            new Rectangle(Pad, Pad, innerW, 26), DeltaTorTheme.Text,
            TextFormatFlags.Left | TextFormatFlags.NoPadding);

        // Each hint line in its own direction: the Persian one reads RTL and
        // the Russian one stays LTR, and the English heading above lets anyone
        // who reads none of them still recognise the buttons by their scripts.
        TextRenderer.DrawText(g, "زبان خود را انتخاب کنید", DeltaTorTheme.Small,
            new Rectangle(Pad, Pad + 30, innerW, 18), DeltaTorTheme.Muted,
            TextFormatFlags.RightToLeft | TextFormatFlags.Right | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, "Выберите язык", DeltaTorTheme.Small,
            new Rectangle(Pad, Pad + 49, innerW, 18), DeltaTorTheme.Muted,
            TextFormatFlags.Left | TextFormatFlags.NoPadding);
    }
}