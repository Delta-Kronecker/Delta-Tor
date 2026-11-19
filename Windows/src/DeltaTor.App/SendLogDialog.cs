using DeltaTor.App.Controls;
using DeltaTor.Core;

namespace DeltaTor.App;

/// <summary>
/// Shown when a connect fails or a crash is reported (the counterpart of
/// MainActivity's SendLogDialog): asks the user to send the connection log and
/// offers a single SEND action. A hint, not a gate -- LATER and Esc dismiss it,
/// because an error the user cannot postpone is exactly the coercion Play
/// Protect reads as hostile.
///
/// SEND builds the archive and hands it to Telegram: the zip is revealed in
/// Explorer and put on the clipboard, and the bot chat is opened so the file can
/// be attached and sent. Nothing is uploaded by the app itself, so no bot token
/// lives in the binary. Owner-drawn like <see cref="ChainWarningDialog"/>.
/// </summary>
public sealed class SendLogDialog : Form
{
    private const int DialogWidth = 460;
    private const int Pad = 20;
    private const int TitleGap = 10;
    private const int BlockGap = 8;
    private const int ButtonHeight = 44;
    private const int ButtonGap = 10;

    private readonly ErrorReport _report;
    private readonly string _title;
    private readonly string _body;
    private readonly string _hint;
    private readonly bool _rtl;

    private readonly CardPanel _card;
    private readonly GradientPill _send;
    private readonly GradientPill _later;

    // The body is drawn a touch heavier than the hint, like Android's
    // bodyMedium-bold paragraph above the muted hint line.
    private readonly Font _bodyFont =
        new(DeltaTorTheme.FontFamilyName, DeltaTorTheme.BodyPt, FontStyle.Bold);

    private int _dialogW = DialogWidth;
    private Rectangle _titleRect, _bodyRect, _errorRect, _hintRect;
    private bool _hasError;

    public SendLogDialog(ErrorReport report)
    {
        _report = report;
        var copy = CopyFor(Config.Language);
        _title = copy.Title;
        _body = copy.Body;
        _hint = copy.Hint;
        _rtl = Config.Language == Config.LanguagePersian;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = DeltaTorTheme.Bg;

        _card = new CardPanel(this);
        _send = new GradientPill { Filled = true, Radius = 13, Label = copy.Send };
        _later = new GradientPill
        {
            Filled = false,
            ShowBorder = true,
            Radius = 13,
            Label = copy.Later
        };

        Controls.Add(_card);
        // Children of the card, so the transparent corners of the pills ask the
        // card to paint its gradient behind them (see GradientPill).
        _card.Controls.Add(_send);
        _card.Controls.Add(_later);
        _send.BringToFront();
        _later.BringToFront();

        _send.Clicked += (_, _) => SendAndClose();
        _later.Clicked += (_, _) => Close();
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) Close();
        };

        Width = _dialogW;
        LayoutDialog();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Dark system frame, like the other dialogs: the default border reads
        // as a white edge around the dark card on Win11.
        DarkTitleBar.Apply(Handle);
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
            LayoutDialog();
        }
        // CenterParent can misplace on a borderless owner whose visible frame
        // does not match its client area -- center explicitly on the owner.
        Location = new Point(
            owner.Location.X + (owner.Width - Width) / 2,
            owner.Location.Y + (owner.Height - Height) / 2);
    }

    private TextFormatFlags Flags(bool singleLine = false)
    {
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.GlyphOverhangPadding |
                    (singleLine ? TextFormatFlags.SingleLine : TextFormatFlags.WordBreak);
        flags |= _rtl
            ? TextFormatFlags.RightToLeft | TextFormatFlags.Right
            : TextFormatFlags.Left;
        return flags;
    }

    private void LayoutDialog()
    {
        var innerW = _dialogW - Pad * 2;
        var titleH = TextRenderer.MeasureText(
            _title, DeltaTorTheme.Caption, Size.Empty, Flags(singleLine: true)).Height;

        var y = Pad;
        _titleRect = new Rectangle(Pad, y, innerW, titleH);
        y += titleH + TitleGap;

        var bodyH = TextRenderer.MeasureText(
            _body, _bodyFont, new Size(innerW, int.MaxValue), Flags()).Height;
        _bodyRect = new Rectangle(Pad, y, innerW, bodyH);
        y += bodyH;

        _hasError = !string.IsNullOrWhiteSpace(_report.Message);
        if (_hasError)
        {
            y += BlockGap;
            var errorH = TextRenderer.MeasureText(
                _report.Message!, DeltaTorTheme.Body,
                new Size(innerW, int.MaxValue), Flags()).Height;
            _errorRect = new Rectangle(Pad, y, innerW, errorH);
            y += errorH;
        }

        y += BlockGap;
        var hintH = TextRenderer.MeasureText(
            _hint, DeltaTorTheme.Small, new Size(innerW, int.MaxValue), Flags()).Height;
        _hintRect = new Rectangle(Pad, y, innerW, hintH);
        y += hintH;

        var buttonsTop = y + 18;
        _send.SetBounds(Pad, buttonsTop, innerW, ButtonHeight);
        _later.SetBounds(Pad, buttonsTop + ButtonHeight + ButtonGap, innerW, ButtonHeight);

        var height = buttonsTop + ButtonHeight * 2 + ButtonGap + Pad;
        _card.SetBounds(0, 0, _dialogW, height);
        Height = height;
        using var path = DrawUtil.RoundedRect(
            new RectangleF(0f, 0f, _dialogW, height), 22f);
        Region = new Region(path);
    }

    private void SendAndClose()
    {
        var zip = LogReporter.BuildZip(_report);
        if (zip is not null) OpenTelegram(zip);
        Close();
    }

    /// <summary>
    /// Hand the archive to Telegram for a manual send: copy the file so it can
    /// be pasted into the chat, reveal it in Explorer so it can be dragged in,
    /// and open the bot chat.
    /// </summary>
    private static void OpenTelegram(string zip)
    {
        try
        {
            var files = new System.Collections.Specialized.StringCollection { zip };
            Clipboard.SetFileDropList(files);
        }
        catch
        {
            // The clipboard can be held by another app; the reveal below is the
            // fallback.
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{zip}\"",
                UseShellExecute = false
            });
        }
        catch
        {
            // Explorer failing must not stop the chat from opening.
        }
        try
        {
            // The app first (tg://), the web page second: t.me in a browser
            // still opens Telegram when the desktop client is installed.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "tg://resolve?domain=DeltaKronecker_Bot",
                UseShellExecute = true
            });
        }
        catch
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = LogReporter.BotUrl,
                    UseShellExecute = true
                });
            }
            catch
            {
                // Nothing left to try; the file is still copied and revealed.
            }
        }
    }

    private sealed record CopyText(
        string Title, string Body, string Hint, string Send, string Later);

    private static CopyText CopyFor(string? language) => language switch
    {
        Config.LanguageRussian => new CopyText(
            "Отправить журнал подключения",
            "Пожалуйста, отправьте журнал подключения в Delta. Мы изучим его и устраним " +
            "ошибку, из-за которой она возникла.",
            $"Откроется Telegram со сжатым журналом. Отправьте файл в {LogReporter.BotName}.",
            "ОТПРАВИТЬ",
            "ПОЗЖЕ"),
        Config.LanguagePersian => new CopyText(
            "ارسال لاگ اتصال",
            "لطفاً لاگ اتصال خود را برای دلتا ارسال کنید تا بررسی شود و مشکل این خطا حل شود.",
            $"تلگرام با فایل فشردهٔ لاگ باز می‌شود. آن را برای {LogReporter.BotName} بفرستید.",
            "ارسال",
            "بعداً"),
        _ => new CopyText(
            "Send connection log",
            "Please send your connection log to Delta. We will review it and fix the " +
            "problem that caused this error.",
            $"Telegram opens with the compressed log. Send the file to {LogReporter.BotName}.",
            "SEND",
            "LATER")
    };

    private sealed class CardPanel : Panel
    {
        private readonly SendLogDialog _owner;

        public CardPanel(SendLogDialog owner)
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

            TextRenderer.DrawText(g, _owner._title, DeltaTorTheme.Caption,
                _owner._titleRect, DeltaTorTheme.Red, _owner.Flags(singleLine: true));

            TextRenderer.DrawText(g, _owner._body, _owner._bodyFont,
                _owner._bodyRect, DeltaTorTheme.Text, _owner.Flags());

            if (_owner._hasError)
            {
                TextRenderer.DrawText(g, _owner._report.Message ?? "", DeltaTorTheme.Body,
                    _owner._errorRect, DeltaTorTheme.Muted, _owner.Flags());
            }

            TextRenderer.DrawText(g, _owner._hint, DeltaTorTheme.Small,
                _owner._hintRect, DeltaTorTheme.Muted, _owner.Flags());
        }
    }
}
