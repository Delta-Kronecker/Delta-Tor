using DeltaTor.App.Controls;
using DeltaTor.Core;

namespace DeltaTor.App;

/// <summary>
/// The main window, laid out 1:1 with MainActivity's MainScreen: layered
/// ambient background (AirBackground), header with drawer button, wordmark
/// and status chip, the update banner, the big state word, the floating
/// power ring and its action label. The bottom panel and drawer land in the
/// following passes.
/// </summary>
public sealed class MainForm : Form
{
    private VpnState _state = AppState.State;

    // The state color and the subline color animate over 450ms
    // (animateColorAsState(sc, tween(450))).
    private Color _scCurrent;
    private Color _scFrom;
    private Color _scTo;
    private Color _subCurrent;
    private Color _subFrom;
    private Color _subTo;
    private long _colorStart;
    private bool _tweening;

    // Background halo pulse: 0.5→1→0.5 over 2×2600ms while connecting,
    // resting at 0.5 otherwise (rememberActiveLoop, resting = from).
    private long _pulseStart;
    private const int PulseDuration = 2600;

    // Ring loops: rotation 0→360 over 9000ms (restart) and breathe
    // 1→1.035 over 2400ms while connecting; halo pulse 0.55→1 over 2200ms
    // while the halo is lit, resting at 0.78.
    private long _rotStart;
    private long _breathStart;
    private long _ringPulseStart;
    private const int RotDuration = 9000;
    private const int BreathDuration = 2400;
    private const int RingPulseDuration = 2200;

    // Bootstrap peak: max transport percent seen this connect, cleared as
    // soon as the connect ends (peakPct, MainActivity 1181).
    private int _peak;
    private bool _wasConnecting;
    private bool _wasGlow;

    private RectangleF _menuRect;
    private RectangleF _ringRect;
    private RectangleF _bannerRect;
    private bool _noticeOpen;

    // Bottom-panel action pills: live controls laid over the painted rows.
    private readonly GradientPill _pillAction = new() { Label = "START VPN", Filled = false };
    private readonly GradientPill _pillDisconnect = new() { Label = "DISCONNECT", Filled = false };
    private bool _proxyLive;

    // The control drawer sits over everything, pills included.
    private readonly DrawerPanel _drawer = new();

    // Tray/toast surface (Android foreground notification equivalent).
    private readonly TrayIcon _tray;
    private ReleaseChecker.ReleaseNotice? _pendingRelease;

    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 16 };

    public MainForm()
    {
        Text = "DeltaTor";
        BackColor = DeltaTorTheme.Bg;
        StartPosition = FormStartPosition.CenterScreen;
        // 1080x2300, the Android phone aspect ratio: 420x894 at launch,
        // resizable down to 360x767 (the 360dp-wide floor keeps the ratio).
        ClientSize = new Size(420, 894);
        MinimumSize = new Size(360, 767);
        KeyPreview = true;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.Dpi;

        _scCurrent = _scFrom = _scTo = UiHelpers.StateColor(_state);
        _subCurrent = _subFrom = _subTo = SubColor(_state);

        Controls.Add(_pillAction);
        Controls.Add(_pillDisconnect);
        Controls.Add(_drawer);
        _pillAction.Clicked += (_, _) =>
        {
            // In proxy mode the left pill shows the endpoint and does nothing;
            // pressing it would offer a start the engine declines.
            if (_proxyLive) return;
            StopOrStartVpn();
        };
        _pillDisconnect.Clicked += (_, _) => Task.Run(BridgeRace.Disconnect);
        _drawer.OpenLog += () =>
        {
            using var log = new LogForm();
            log.ShowDialog(this);
        };

        if (TrayIcon.LoadAppIcon() is { } appIcon) Icon = appIcon;
        _tray = new TrayIcon(this);
        _tray.DisconnectRequested += () => Task.Run(BridgeRace.Disconnect);
        _tray.StopVpnRequested += () => Task.Run(BridgeRace.StopVpn);
        _tray.StartVpnRequested += () => Task.Run(BridgeRace.StartVpn);
        BridgeRace.NotificationChanged += OnEngineNotification;
        ReleaseChecker.NotificationRaised += OnReleaseNotice;
        _tray.UpdateMenu(_state);

        _tick.Tick += (_, _) => AnimationFrame();

        AppState.Changed += OnStateChanged;
        FormClosed += (_, _) =>
        {
            AppState.Changed -= OnStateChanged;
            BridgeRace.NotificationChanged -= OnEngineNotification;
            ReleaseChecker.NotificationRaised -= OnReleaseNotice;
            _tray.Dispose();
            _tick.Stop();
            _tick.Dispose();
        };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_pendingRelease is { } pending)
        {
            _pendingRelease = null;
            _tray.ShowRelease(pending);
        }
        RefreshFromState();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _drawer.SetBounds(0, 0, ClientSize.Width, ClientSize.Height);
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

    // Engine notifications arrive on engine threads; the tray is UI.
    private void OnEngineNotification(EngineNotification? n)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            if (InvokeRequired) BeginInvoke(() => _tray.SetNotification(n));
            else _tray.SetNotification(n);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
            // The handle went away between the check and the post.
        }
    }

    // The release check runs before the window is up: hold its toast for OnShown.
    private void OnReleaseNotice(ReleaseChecker.ReleaseNotice notice)
    {
        if (IsDisposed) return;
        if (!IsHandleCreated)
        {
            _pendingRelease = notice;
            return;
        }
        try
        {
            if (InvokeRequired) BeginInvoke(() => _tray.ShowRelease(notice));
            else _tray.ShowRelease(notice);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void RefreshFromState()
    {
        var prev = _state;
        _state = AppState.State;
        var now = Environment.TickCount64;

        var sc = UiHelpers.StateColor(_state);
        var sub = SubColor(_state);
        if (sc != _scTo || sub != _subTo)
        {
            _scFrom = _scCurrent;
            _scTo = sc;
            _subFrom = _subCurrent;
            _subTo = sub;
            _colorStart = now;
            _tweening = true;
        }

        // peakPct: reset on both edges of connecting, keep the high water
        // mark while a connect is running.
        var connecting = _state.Connecting;
        if (connecting && !_wasConnecting) _peak = 0;
        if (!connecting) _peak = 0;
        if (connecting)
        {
            var max = 0;
            foreach (var v in _state.Transports.Values)
                if (v >= 0 && v > max) max = v;
            _peak = Math.Max(_peak, max);
        }
        _wasConnecting = connecting;

        if (connecting && !prev.Connecting)
        {
            _pulseStart = now;
            _rotStart = now;
            _breathStart = now;
        }

        var glow = connecting || _state.Connected;
        if (glow && !_wasGlow) _ringPulseStart = now;
        _wasGlow = glow;

        _tick.Start();
        _drawer.NotifyStateChanged();
        _tray.UpdateMenu(_state);
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

    private static Color SubColor(VpnState state)
    {
        if (state.Stopping || state.Reconnecting) return DeltaTorTheme.AmberLight;
        if (state.Connected) return DeltaTorTheme.GreenLight;
        return DeltaTorTheme.Muted;
    }

    private void AnimationFrame()
    {
        var invalidate = false;

        if (_tweening)
        {
            var p = (Environment.TickCount64 - _colorStart) / 450.0;
            if (p >= 1.0)
            {
                p = 1.0;
                _tweening = false;
            }
            var t = Easing.FastOutSlowIn((float)p);
            _scCurrent = LerpColor(_scFrom, _scTo, t);
            _subCurrent = LerpColor(_subFrom, _subTo, t);
            invalidate = true;
        }

        if (_state.Connecting)
            invalidate = true; // bg pulse, ring rotation and breathe
        else if (GlowLit)
            invalidate = true; // ring halo pulse stays lit while connected

        if (invalidate) Invalidate();
        else _tick.Stop();
    }

    private bool GlowLit => _state.Connecting || _state.Connected;

    private static Color LerpColor(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t),
        (int)(a.G + (b.G - a.G) * t),
        (int)(a.B + (b.B - a.B) * t));

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

    private float RingRotation =>
        _state.Connecting
            ? (Environment.TickCount64 - _rotStart) % RotDuration * 360f / RotDuration
            : 0f;

    private float RingBreathe
    {
        get
        {
            if (!_state.Connecting) return 1f;
            var phase = (Environment.TickCount64 - _breathStart) % (BreathDuration * 2L);
            var p = phase < BreathDuration
                ? (float)phase / BreathDuration
                : 1f - (float)(phase - BreathDuration) / BreathDuration;
            return 1f + 0.035f * Easing.FastOutSlowIn(p);
        }
    }

    private float RingPulse
    {
        get
        {
            if (!GlowLit) return 0.78f;
            var phase = (Environment.TickCount64 - _ringPulseStart) % (RingPulseDuration * 2L);
            var p = phase < RingPulseDuration
                ? (float)phase / RingPulseDuration
                : 1f - (float)(phase - RingPulseDuration) / RingPulseDuration;
            return 0.55f + 0.45f * Easing.FastOutSlowIn(p);
        }
    }

    // ---- input --------------------------------------------------------------

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;

        if (_menuRect.Contains(e.Location))
        {
            _drawer.Open();
            return;
        }

        if (_bannerRect.Width > 0 && _bannerRect.Contains(e.Location))
        {
            var rel = AppState.ReleaseState;
            if (rel.Newer && rel.LatestUrl.Length > 0)
                ReleaseChecker.OpenInBrowser(rel.LatestUrl);
            return;
        }

        if (_ringRect.Contains(e.Location))
        {
            PrimaryAction();
        }
    }

    /// <summary>MainScreen's onPrimary (MainActivity 162): each state gets
    /// the only action that actually does something.</summary>
    private void PrimaryAction()
    {
        var s = AppState.State;
        if (s.Stopping) return;
        // A live SOCKS endpoint means proxy mode, where there is no VPN to
        // start. Must be checked before torRunning, which is also true in
        // this state and would otherwise send a start the engine declines.
        if (s.SocksEndpoint.Length > 0 || s.Connecting || s.Connected)
        {
            Task.Run(BridgeRace.Disconnect);
            return;
        }
        if (s.TorRunning)
        {
            Task.Run(BridgeRace.StartVpn);
            return;
        }
        Task.Run(BridgeRace.Connect);
    }

    /// <summary>The action pill: STOP VPN while a tunnel runs, otherwise the
    /// primary start path.</summary>
    private void StopOrStartVpn()
    {
        var s = AppState.State;
        if (s.Connected && !s.Stopping) Task.Run(BridgeRace.StopVpn);
        else PrimaryAction();
    }

    // ---- painting -----------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        PaintAirBackground(g);
        PaintHeader(g);
        PaintUpdateBanner(g);
        PaintStateBlock(g);
        PaintRing(g);
        PaintPrimaryLabel(g);
        PaintBottomPanel(g);
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

    /// <summary>UpdateBanner (MainActivity 2647): sits 98px from the top while
    /// a newer release exists, and opens the release page on click.</summary>
    private void PaintUpdateBanner(Graphics g)
    {
        _bannerRect = RectangleF.Empty;
        var rel = AppState.ReleaseState;
        if (!rel.Newer || rel.LatestVersion.Length == 0) return;

        const float x = 20f;
        const float y = 98f;
        var w = ClientSize.Width - x * 2f;

        using var titleFont = new Font(
            DeltaTorTheme.FontFamilyName, DeltaTorTheme.CaptionPt, FontStyle.Bold);
        using var bodyFont = new Font(
            DeltaTorTheme.FontFamilyName, DeltaTorTheme.CaptionPt, FontStyle.Regular);
        var rowH = Math.Max(9f, g.MeasureString("A", titleFont).Height);
        var subH = g.MeasureString("A", bodyFont).Height;
        var h = 12f + rowH + 5f + subH + 12f;

        var rect = new RectangleF(x, y, w, h);
        _bannerRect = rect;
        using (var path = DrawUtil.RoundedRect(rect, 16f))
        {
            using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                rect, Color.FromArgb(0xFF, 0x2A, 0x21, 0x40), DeltaTorTheme.Surface, 0f))
                g.FillPath(brush, path);
            using var pen = new Pen(Color.FromArgb(115, DeltaTorTheme.Accent)); // accent 0.45
            g.DrawPath(pen, path);
        }

        using (var dot = new SolidBrush(DeltaTorTheme.Green))
            g.FillEllipse(dot, x + 14f, y + 12f + (rowH - 9f) / 2f, 9f, 9f);
        using (var brush = new SolidBrush(DeltaTorTheme.GreenLight))
            DrawUtil.DrawSpaced(g, $"NEW RELEASE v{rel.LatestVersion}", titleFont, brush,
                x + 14f + 9f + 8f, y + 12f, w - 14f * 2f - 17f, 1.3f);
        using (var brush = new SolidBrush(DeltaTorTheme.Muted))
            g.DrawString("An update is available · tap anywhere to open on GitHub",
                bodyFont, brush, x + 14f, y + 12f + rowH + 5f);
    }

    /// <summary>StateBlock (MainActivity 1355): the big state word with its
    /// soft shadow and the animated subline under it, centered 112 above the
    /// window center.</summary>
    private void PaintStateBlock(Graphics g)
    {
        var s = _state;
        var hasError = s.Error != null;
        var word = UiHelpers.WordFor(
            s.Connecting, s.TorRunning, s.Connected, s.Reconnecting, s.Stopping, hasError);
        var sub = UiHelpers.SublineFor(
            s.Connecting, s.TorRunning, s.Connected, s.Reconnecting, s.Stopping,
            s.Transport, _peak, hasError);

        using var wordFont = new Font(DeltaTorTheme.FontFamilyName, 20f, FontStyle.Bold);
        using var subFont = new Font(
            DeltaTorTheme.FontFamilyName, DeltaTorTheme.CaptionPt, FontStyle.Bold);

        var wordH = g.MeasureString(word, wordFont).Height;
        var subH = sub.Length > 0 ? g.MeasureString(sub, subFont).Height : 0f;
        var blockH = wordH + 8f + subH;
        var cy = ClientSize.Height / 2f;
        var top = cy - 112f - blockH / 2f;

        DrawSpacedShadow(g, word, wordFont, _scCurrent, ClientSize.Width, top,
            Color.FromArgb(115, Color.Black)); // black 0.45, offset (0, 4)
        if (sub.Length > 0)
        {
            using var brush = new SolidBrush(_subCurrent);
            DrawUtil.DrawSpacedCentered(g, sub, subFont, brush,
                new RectangleF(32f, top + wordH + 8f, ClientSize.Width - 64f, subH), 1.4f);
        }
    }

    /// <summary>Centered letter-spaced text with the big word's soft shadow
    /// (Compose Shadow(black 0.45, offset (0,4), blur 10) approximated by a
    /// jittered low-alpha pass under the glyphs). Centering uses the client
    /// width, never g.ClipBounds: a partial repaint (the strips a closing
    /// drawer or a dialog uncovers) has a narrow clip, and centering on that
    /// clip would draw the word shifted in each strip.</summary>
    private static void DrawSpacedShadow(
        Graphics g, string text, Font font, Color color, float clientW, float top,
        Color shadow)
    {
        var w = DrawUtil.SpacedWidth(g, text, font, 2.5f);
        var x0 = (clientW - w) / 2f;
        var y = top;

        using var shadowBrush = new SolidBrush(shadow);
        using var mainBrush = new SolidBrush(color);
        // Shadow pass: a small jittered ring under and slightly right of the
        // glyphs, standing in for the blur radius.
        for (var i = 0; i < 8; i++)
        {
            var a = i * Math.PI / 4.0;
            var dx = (float)Math.Cos(a) * 4f;
            var dy = 4f + (float)Math.Sin(a) * 4f;
            DrawGlyphs(g, text, font, shadowBrush, x0 + dx, y + dy, 2.5f);
        }
        DrawGlyphs(g, text, font, mainBrush, x0, y, 2.5f);
    }

    private static void DrawGlyphs(
        Graphics g, string text, Font font, Brush brush, float x, float y, float spacing)
    {
        if (text.Length == 0) return;
        // Prefix-width differences, not a per-character MeasureString: a
        // one-character measure carries GDI+'s right-side bearing and opened
        // a visible gap between the glyphs of the big state word.
        var prev = 0f;
        for (var i = 0; i < text.Length; i++)
        {
            var upTo = g.MeasureString(text[..(i + 1)], font).Width;
            g.DrawString(text[i].ToString(), font, brush, x, y);
            x += upTo - prev + spacing;
            prev = upTo;
        }
    }

    /// <summary>RingButton (MainActivity 1415): halo, rotating beam, track,
    /// progress arc, matte disc and the power glyph — always dead-center,
    /// scaled by the breathing loop while connecting.</summary>
    private void PaintRing(Graphics g)
    {
        var cx = ClientSize.Width / 2f;
        var cy = ClientSize.Height / 2f;
        _ringRect = new RectangleF(cx - 76f, cy - 76f, 152f, 152f);

        var connecting = _state.Connecting;
        var glow = GlowLit ? _scCurrent : (Color?)null;
        var scale = connecting ? RingBreathe : 1f;

        // Ambient halo (pulse while lit), radius 130.
        var haloR = 130f * scale;
        if (glow.HasValue)
        {
            var alpha = (int)(255 * 0.30f * RingPulse);
            FillRadial(g, new PointF(cx, cy), haloR, Color.FromArgb(alpha, glow.Value));
        }
        else
        {
            FillRadial(g, new PointF(cx, cy), haloR,
                Color.FromArgb(26, DeltaTorTheme.BorderLight)); // borderLight 0.10
        }

        var r = 76f * scale;

        // Rotating beam trail behind the track while connecting: a sweep
        // gradient [0, 0.30, 0], drawn as fading arc slices.
        if (connecting)
        {
            var rot = RingRotation;
            const int slices = 60;
            for (var i = 0; i < slices; i++)
            {
                var pos = (i + 0.5f) / slices;
                var a = pos < 0.5f
                    ? 0.30f * pos / 0.5f
                    : 0.30f * (1f - (pos - 0.5f) / 0.5f);
                using var pen = new Pen(Color.FromArgb((int)(255 * a), _scCurrent), 10f)
                {
                    StartCap = System.Drawing.Drawing2D.LineCap.Round,
                    EndCap = System.Drawing.Drawing2D.LineCap.Round
                };
                g.DrawArc(pen, cx - r, cy - r, r * 2f, r * 2f,
                    rot + i * (360f / slices), 360f / slices + 1f);
            }
        }

        // Thin flat track ring.
        using (var pen = new Pen(Color.FromArgb(89, DeltaTorTheme.BorderLight), 2f)) // 0.35
        {
            pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
            pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
            g.DrawArc(pen, cx - r, cy - r, r * 2f, r * 2f, -90f, 360f);
        }

        // Progress arc: one solid state color, always from the top.
        var prog = Math.Clamp(RingProgress(), 0f, 1f);
        if (prog > 0f)
        {
            using var pen = new Pen(_scCurrent, 6f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round
            };
            g.DrawArc(pen, cx - r, cy - r, r * 2f, r * 2f, -90f, 360f * prog);
        }

        // Flat matte disc: Surface → #0F1119, hairlined in Border.
        var inner = r - 9f * scale;
        using (var disc = new System.Drawing.Drawing2D.GraphicsPath())
        {
            disc.AddEllipse(cx - inner, cy - inner, inner * 2f, inner * 2f);
            using var brush = new System.Drawing.Drawing2D.PathGradientBrush(disc)
            {
                CenterPoint = new PointF(cx, cy),
                CenterColor = DeltaTorTheme.Surface,
                SurroundColors = new[] { Color.FromArgb(0xFF, 0x0F, 0x11, 0x19) }
            };
            g.FillEllipse(brush, cx - inner, cy - inner, inner * 2f, inner * 2f);
        }
        using (var pen = new Pen(Color.FromArgb(230, DeltaTorTheme.Border))) // Border 0.9
            g.DrawEllipse(pen, cx - inner, cy - inner, inner * 2f, inner * 2f);

        // The power glyph — crisp single color: 280° arc from 310° plus the stem.
        var rg = inner * 0.46f;
        var gp = 9f;
        var glyphColor = UiHelpers.GlyphColor(connecting, _state.Connected, _state.Stopping);
        using (var pen = new Pen(glyphColor, gp)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round
        })
        {
            g.DrawArc(pen, cx - rg, cy - rg, rg * 2f, rg * 2f, 310f, 280f);
            g.DrawLine(pen, cx, cy - rg, cx, cy + rg * 0.5f);
        }
    }

    /// <summary>ringProgressOf (MainActivity 1196): bootstrap progress only
    /// while connecting and Tor is not up yet.</summary>
    private float RingProgress()
    {
        var s = _state;
        return s.Connecting && !s.TorRunning && _peak > 0 ? _peak / 100f : 0f;
    }

    /// <summary>The action word under the ring (MainActivity 420): CANCEL /
    /// DISCONNECT / START VPN / CONNECT, or the error itself in red.</summary>
    private void PaintPrimaryLabel(Graphics g)
    {
        var text = UiHelpers.LabelText(_state);
        if (text.Length == 0) return;

        using var font = new Font(DeltaTorTheme.FontFamilyName, 10.5f, FontStyle.Regular);
        var lineH = g.MeasureString(text, font).Height;
        var cy = ClientSize.Height / 2f + 106f;
        var color = _state.Error != null ? DeltaTorTheme.Red : DeltaTorTheme.Muted;
        using var brush = new SolidBrush(color);
        DrawUtil.DrawSpacedCentered(g, text, font, brush,
            new RectangleF(32f, cy - lineH / 2f, ClientSize.Width - 64f, lineH), 1.4f);
    }

    /// <summary>BottomPanel (MainActivity 1579): action pills (kept in place
    /// during teardown, just relabeling and dimming), the two speed cards,
    /// the two byte cards and the uptime/exit info pills, bottom-centered.</summary>
    private void PaintBottomPanel(Graphics g)
    {
        var s = _state;
        const float padX = 20f;
        var rowW = ClientSize.Width - padX * 2f;
        var halfW = (rowW - 10f) / 2f;
        var statW = (rowW - 8f) / 2f;

        using var capBold = new Font(
            DeltaTorTheme.FontFamilyName, DeltaTorTheme.CaptionPt, FontStyle.Bold);
        using var titleBold = new Font(
            DeltaTorTheme.FontFamilyName, DeltaTorTheme.TitlePt, FontStyle.Bold);
        using var bodyBold = new Font(
            DeltaTorTheme.FontFamilyName, DeltaTorTheme.BodyPt, FontStyle.Bold);
        var labelLine = g.MeasureString("A", capBold).Height;
        var valueLine = g.MeasureString("A", bodyBold).Height;
        var titleLine = g.MeasureString("A", titleBold).Height;

        var infoH = 7f + labelLine + 3f + valueLine + 7f;
        var statH = 8f + Math.Max(15f, labelLine) + 4f + titleLine + 8f;
        var busy = s.Connecting || s.Stopping;

        // The action row is never removed. During teardown the left pill keeps
        // its slot and just relabels to START VPN, dimmed until the cores are
        // gone: a start click there would race the teardown for the ports, but
        // hiding the row made both buttons blink out and back in on every stop.
        // In proxy mode it becomes the endpoint pill instead of a lying
        // START VPN the engine would decline.
        var pillsVisible = s.Connected || s.TorRunning || s.Stopping;
        var pillsH = pillsVisible ? 40f + 8f : 0f;
        var total = 4f + pillsH + statH + 8f + statH + 8f + infoH + 9f + 12f;
        var y = ClientSize.Height - total;

        if (pillsVisible)
        {
            var py = y + 4f;
            _proxyLive = s.SocksEndpoint.Length > 0;
            _pillAction.Label = _proxyLive
                ? "SOCKS5 " + StripScheme(s.SocksEndpoint)
                : s.Connected && !s.Stopping
                    ? "STOP VPN"
                    : "START VPN";
            _pillAction.Visible = true;
            _pillAction.Enabled = !busy;
            _pillAction.SetBounds(
                (int)padX, (int)py, (int)halfW, 40);
            _pillDisconnect.Visible = true;
            _pillDisconnect.Enabled = !busy;
            _pillDisconnect.SetBounds(
                (int)(padX + halfW + 10f), (int)py, (int)halfW, 40);
            y = py + 40f + 8f;
        }
        else
        {
            _pillAction.Visible = false;
            _pillDisconnect.Visible = false;
        }

        // Speed: live on the home screen itself, not only in a notification.
        DrawStatCard(g, padX, y, statW, statH, "SPEED DOWN",
            s.Connected ? UiHelpers.FormatBytes((long)s.RxSpeed) + "/s" : "--",
            DeltaTorTheme.Green, false, capBold, titleBold);
        DrawStatCard(g, padX + statW + 8f, y, statW, statH, "SPEED UP",
            s.Connected ? UiHelpers.FormatBytes((long)s.TxSpeed) + "/s" : "--",
            DeltaTorTheme.Accent, true, capBold, titleBold);
        y += statH + 8f;

        DrawStatCard(g, padX, y, statW, statH, "DOWNLOADED",
            UiHelpers.FormatBytes(s.RxBytes), DeltaTorTheme.Green, false, capBold, titleBold);
        DrawStatCard(g, padX + statW + 8f, y, statW, statH, "UPLOADED",
            UiHelpers.FormatBytes(s.TxBytes), DeltaTorTheme.Accent, true, capBold, titleBold);
        y += statH + 8f;

        var uptime = s.Connected
            ? UiHelpers.FormatDuration(
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - s.ConnectedAtMillis)
            : "--";
        var exit = s.ExitCode.Length > 0
            ? $"{UiHelpers.FlagEmoji(s.ExitCode)} {s.ExitName}".Trim()
            : s.Connected
                ? "Locating …"
                : "--";
        DrawInfoPill(g, padX, y, statW, infoH, "UP TIME", uptime, capBold, bodyBold);
        DrawInfoPill(g, padX + statW + 8f, y, statW, infoH, "EXIT", exit, capBold, bodyBold);
    }

    /// <summary>Kotlin substringAfter("://"): the endpoint without its scheme.</summary>
    private static string StripScheme(string endpoint)
    {
        var idx = endpoint.IndexOf("://", StringComparison.Ordinal);
        return idx >= 0 ? endpoint[(idx + 2)..] : endpoint;
    }

    /// <summary>StatCard (MainActivity 1751): arrow glyph + label over a big
    /// value, SurfaceAlt→Surface tile with a Border hairline.</summary>
    private void DrawStatCard(
        Graphics g, float x, float y, float w, float h,
        string label, string value, Color accent, bool up, Font capFont, Font titleFont)
    {
        var rect = new RectangleF(x, y, w, h);
        using (var path = DrawUtil.RoundedRect(rect, 14f))
        {
            using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                rect, DeltaTorTheme.SurfaceAlt, DeltaTorTheme.Surface, 90f))
                g.FillPath(brush, path);
            using var pen = new Pen(DeltaTorTheme.Border);
            g.DrawPath(pen, path);
        }

        var labelLine = g.MeasureString(label, capFont).Height;
        var rowH = Math.Max(15f, labelLine);
        Icons.Arrow(g, new RectangleF(x + 12f, y + 8f + (rowH - 15f) / 2f, 15f, 15f),
            accent, up);
        using (var brush = new SolidBrush(DeltaTorTheme.Muted))
            DrawUtil.DrawSpaced(g, label, capFont, brush,
                x + 12f + 15f + 7f, y + 8f + (rowH - labelLine) / 2f,
                w - 12f * 2f - 22f, 1.2f);
        using (var brush = new SolidBrush(DeltaTorTheme.Text))
            DrawUtil.DrawSpaced(g, value, titleFont, brush,
                x + 12f, y + 8f + rowH + 4f, w - 24f, 0.4f);
    }

    /// <summary>InfoPill (MainActivity 1797): label over value on a Surface
    /// tile with a BorderLight hairline.</summary>
    private void DrawInfoPill(
        Graphics g, float x, float y, float w, float h,
        string label, string value, Font capFont, Font valueFont)
    {
        var rect = new RectangleF(x, y, w, h);
        using (var path = DrawUtil.RoundedRect(rect, 12f))
        {
            using var fill = new SolidBrush(DeltaTorTheme.Surface);
            g.FillPath(fill, path);
            using var pen = new Pen(DeltaTorTheme.BorderLight);
            g.DrawPath(pen, path);
        }

        var labelLine = g.MeasureString(label, capFont).Height;
        using (var brush = new SolidBrush(DeltaTorTheme.Muted))
            DrawUtil.DrawSpaced(g, label, capFont, brush, x + 12f, y + 7f, w - 24f, 1.2f);
        using (var brush = new SolidBrush(DeltaTorTheme.Text))
            DrawUtil.DrawSpaced(g, value, valueFont, brush,
                x + 12f, y + 7f + labelLine + 3f, w - 24f, 0f);
    }
}
