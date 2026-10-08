using DeltaTor.Core;
using DeltaTor.Core.Util;

namespace DeltaTor.App.Controls;

/// <summary>
/// The ADVANCED half of the drawer's flow (MainActivity AdvancedItems, line
/// 2960): one gradient SettingsCard per feature. Each card measures its
/// content at layout time into node-relative rectangles and hands the core a
/// single draw closure — no deferred op lists, no per-paint measuring. Child
/// controls are created hidden and placed by PlaceChild only while their
/// card is fully inside the viewport, so nothing can float over the header,
/// the footer pill or the dropdown.
/// </summary>
public sealed partial class DrawerPanel
{
    // AdvancedForm (MainActivity 2924): hoisted editable state.
    private string _templateText = TorrcSettings.Template();
    private bool _savedFlash;
    private long _savedAt;
    private string _transportMode = Config.TransportMode;
    private HashSet<string> _autoTransports = new(Config.AutoTransports, StringComparer.Ordinal);
    private bool _autoRecovery = Config.AutoRecovery;
    private bool _runMemory = Config.RunMemory;
    private string _customBridges = Config.CustomBridges;
    private bool _proxyOnly = Config.ProxyOnlyMode;
    private bool _loggingOn = Config.LoggingEnabled;
    private bool _showAdvanced;

    private DrawerEdit? _customBox;
    private DrawerEdit? _torrcBox;
    private ToggleSwitch? _proxySwitch;
    private ToggleSwitch? _logSwitch;
    private Spinner? _storeSpinner;

    /// <summary>MainForm hooks this to the log screen once it exists.</summary>
    public event Action? OpenLog;

    /// <summary>Multi-line box inside the drawer: the wheel scrolls the
    /// drawer itself unless the box has focus, then it scrolls its own text.</summary>
    private sealed class DrawerEdit : TextBox
    {
        public Action<MouseEventArgs>? WheelToDrawer;

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (Focused)
            {
                base.OnMouseWheel(e);
                return;
            }
            WheelToDrawer?.Invoke(e);
        }
    }

    // ---- flow entry ---------------------------------------------------------

    private void BuildAdvanced(Flow flow)
    {
        AddSectionHeader(flow, "ADVANCED",
            "Transport, bridges, torrc and the log", _showAdvanced,
            () =>
            {
                _showAdvanced = !_showAdvanced;
                MarkDirty();
                Invalidate();
            });
        if (!_showAdvanced) return;

        // Android syncs the form from the live mode (LaunchedEffect(liveMode));
        // once the service has a mode, it wins over the stored one.
        var live = AppState.Mode;
        if (live.Length > 0 && live != _transportMode) _transportMode = live;

        AddTransportCard(flow);
        if (_transportMode == ParallelTorManager.TransportAuto)
            AddRacersCard(flow);
        if (_transportMode == ParallelTorManager.TransportCustom)
            AddCustomBridgesCard(flow);
        AddProxyCard(flow);
        AddTorrcCard(flow);
        AddBridgeStoreCard(flow);
        AddLogCard(flow);
    }

    // ---- shared card primitives --------------------------------------------

    /// <summary>SettingsCard (MainActivity 2901): #20242F→Surface tile,
    /// Border hairline, radius 18.</summary>
    private static void CardBg(Graphics g, float x, float y, float w, float h)
    {
        var rect = new RectangleF(x, y, w, h);
        using var path = DrawUtil.RoundedRect(rect, 18f);
        using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
            rect, Color.FromArgb(0xFF, 0x20, 0x24, 0x2F), DeltaTorTheme.Surface, 90f);
        g.FillPath(brush, path);
        using var pen = new Pen(DeltaTorTheme.Border);
        g.DrawPath(pen, path);
    }

    /// <summary>CardTitle (MainActivity 2734): labelSmall, letter-spaced,
    /// Muted, with an optional trailing action.</summary>
    private void DrawCardTitle(
        Graphics g, float sy, float x, float iw, string title,
        string? trailing, Color trailingColor, Action? onTrailing, float top)
    {
        var titleY = sy + 2f;
        using (var b = new SolidBrush(DeltaTorTheme.Muted))
            DrawUtil.DrawSpaced(g, title, _fLabel, b, x, titleY, iw, 1.6f);
        if (trailing == null) return;

        var tw = DrawUtil.SpacedWidth(trailing, _fLabel, 1.1f);
        using (var b = new SolidBrush(trailingColor))
            DrawUtil.DrawSpaced(g, trailing, _fLabel, b,
                x + iw - tw, titleY, tw + 2f, 1.1f);
        if (onTrailing != null)
            _scrollHits.Add((
                new RectangleF(x + iw - 16f - tw, top + 2f, tw + 16f, _cardTitleLineH + 4f),
                onTrailing));
    }

    /// <summary>ON/OFF toggle row (AutoRecoveryButton, MainActivity 2041):
    /// divider, title, wrapped description, pill on the right.</summary>
    private sealed class ToggleView
    {
        public float DivY;
        public float TitleY;
        public float DescY;
        public float BtnX;
        public float BtnY;
        public float H;
        public Size DescSize;
        public required string Title;
        public required string Desc;
        public bool On;
        public required Action Toggle;
        public const float BtnW = 50f;
        public const float BtnH = 26f;
    }

    private ToggleView MakeToggle(
        float x, float w, float cy, string title, string desc, bool on, Action toggle)
    {
        var v = new ToggleView
        {
            Title = title,
            Desc = desc,
            On = on,
            Toggle = toggle,
            DivY = cy
        };
        var rowY = cy + 13f;
        v.DescSize = Wrap(desc, _fBody, w - 74f);
        var colH = _rowLineH + 3f + v.DescSize.Height;
        v.BtnX = x + w - ToggleView.BtnW;
        v.BtnY = rowY + Math.Max(0f, (colH - ToggleView.BtnH) / 2f);
        v.TitleY = rowY;
        v.DescY = rowY + _rowLineH + 3f;
        v.H = 13f + Math.Max(colH, ToggleView.BtnH) + 6f;
        return v;
    }

    private void DrawToggle(Graphics g, ToggleView v, float x, float w, float sy, float top)
    {
        using (var b = new SolidBrush(Color.FromArgb(153, DeltaTorTheme.BorderLight)))
            g.FillRectangle(b, x, sy + v.DivY, w, 1f);
        using (var b = new SolidBrush(DeltaTorTheme.Text))
            DrawUtil.DrawSpaced(g, v.Title, _fRow, b, x, sy + v.TitleY, w, 0.3f);
        DrawWrap(g, v.Desc, _fBody, DeltaTorTheme.Muted, x, sy + v.DescY, v.DescSize);

        var btn = new RectangleF(v.BtnX, sy + v.BtnY, ToggleView.BtnW, ToggleView.BtnH);
        using (var path = DrawUtil.RoundedRect(btn, 12f))
        {
            if (v.On)
            {
                using var fill = new SolidBrush(Color.FromArgb(46, DeltaTorTheme.Green));
                g.FillPath(fill, path);
            }
            else
            {
                using var fill = new SolidBrush(DeltaTorTheme.SurfaceAlt);
                g.FillPath(fill, path);
            }
            using var pen = new Pen(v.On ? DeltaTorTheme.Green : DeltaTorTheme.BorderLight);
            g.DrawPath(pen, path);
        }
        using (var b = new SolidBrush(v.On ? DeltaTorTheme.GreenLight : DeltaTorTheme.Muted))
            DrawUtil.DrawSpacedCentered(g, v.On ? "ON" : "OFF", _fLabel, b, btn, 1.2f);

        _scrollHits.Add((
            new RectangleF(v.BtnX, top + v.BtnY, ToggleView.BtnW, ToggleView.BtnH),
            v.Toggle));
    }

    /// <summary>Title + wrapped description with a child switch on the right
    /// (NoVpnOnlySwitch / RecordLogSwitch rows).</summary>
    private sealed class SwitchView
    {
        public float TitleY;
        public float DescY;
        public float SwX;
        public float SwY;
        public float H;
        public Size DescSize;
        public required string Title;
        public required string Desc;
    }

    private SwitchView MakeSwitch(float x, float w, float cy, string title, string desc)
    {
        var v = new SwitchView { Title = title, Desc = desc };
        v.DescSize = Wrap(desc, _fBody, w - 58f);
        var colH = _rowLineH + 3f + v.DescSize.Height;
        v.SwX = x + w - 46f;
        v.SwY = cy + Math.Max(0f, (colH - 26f) / 2f);
        v.TitleY = cy;
        v.DescY = cy + _rowLineH + 3f;
        v.H = Math.Max(colH, 26f) + 2f;
        return v;
    }

    private void DrawSwitch(
        Graphics g, SwitchView v, float x, float w, float sy, ToggleSwitch sw)
    {
        using (var b = new SolidBrush(DeltaTorTheme.Text))
            DrawUtil.DrawSpaced(g, v.Title, _fRow, b, x, sy + v.TitleY, w - 58f, 0.3f);
        DrawWrap(g, v.Desc, _fBody, DeltaTorTheme.Muted, x, sy + v.DescY, v.DescSize);
        PlaceChild(sw, new RectangleF(v.SwX, sy + v.SwY, 46f, 26f));
    }

    /// <summary>Small action box: outlined (EXPORT) or gradient-filled
    /// (UPDATE), as the Android card draws them.</summary>
    private void DrawBox(Graphics g, RectangleF rect, bool gradient, string text, Color textColor)
    {
        using var path = DrawUtil.RoundedRect(rect, 12f);
        if (gradient)
        {
            using var fill = new System.Drawing.Drawing2D.LinearGradientBrush(
                rect, DeltaTorTheme.AccentDark, DeltaTorTheme.Accent, 0f);
            g.FillPath(fill, path);
        }
        else
        {
            using var pen = new Pen(DeltaTorTheme.BorderLight);
            g.DrawPath(pen, path);
        }
        using var b = new SolidBrush(textColor);
        DrawUtil.DrawSpacedCentered(g, text, _fLabel, b, rect, 1.2f);
    }

    // ---- TRANSPORT ----------------------------------------------------------

    private static readonly (string Value, string Label)[] TransportModes =
    {
        (ParallelTorManager.TransportAuto, "Auto"),
        (ParallelTorManager.TransportFresh, "Fresh"),
        (ParallelTorManager.TransportCombined, "Combined-Bridge"),
        (ParallelTorManager.TransportVanilla, "Vanilla"),
        (ParallelTorManager.TransportObfs4, "obfs4"),
        (ParallelTorManager.TransportWebtunnel, "WebTunnel"),
        (ParallelTorManager.TransportSnowflake, "Snowflake"),
        (ParallelTorManager.TransportDirect, "Direct"),
        (ParallelTorManager.TransportCustom, "Custom")
    };

    private void AddTransportCard(Flow flow)
    {
        var w = flow.W;
        var x = CardMargin + CardPadX;
        var iw = w - CardMargin * 2f - CardPadX * 2f;
        var top = flow.Y;
        var mode = _transportMode;

        var cy = CardPadY;
        var titleTop = cy;
        cy += 2f + _cardTitleLineH + 4f;

        var ddH = 12f + _bodyLineH + 12f;
        var ddRel = cy;
        var ddContent = new RectangleF(x, top + cy, iw, ddH);
        cy += ddH;

        var desc = TransportDescription(mode);
        const float descPad = 8f;
        var descSize = Wrap(desc, _fBody, iw);
        cy += descPad + descSize.Height + 2f;

        var hasTwin = ParallelTorManager.TwinnedModes.Contains(mode);
        var twinText = "";
        var twinSize = Size.Empty;
        if (hasTwin)
        {
            twinText =
                "Every bridge list also gets a memory twin: the bridges " +
                "that worked in this transport are pulled from the log and " +
                "race beside it, e.g. " + mode + "-memory. " +
                "RUN MEMORY below turns that twin off for a connect.";
            twinSize = Wrap(twinText, _fBody, iw);
            cy += 6f + twinSize.Height + 2f;
        }

        var toggle1 = MakeToggle(x, iw, cy,
            "AUTO RECOVERY",
            _autoRecovery
                ? "On. If a connect is not in auto and its progress has not moved for " +
                  "a minute, the app stops it, switches the mode to auto and starts " +
                  "again -- once per connect, since the mode is auto by then."
                : "Off. If a connect is not in auto and its progress has not " +
                  "moved for a minute, the app does nothing: the runners stay " +
                  "on it until one connects or all of them fail, and the mode " +
                  "is never switched for you.",
            _autoRecovery,
            () =>
            {
                _autoRecovery = !_autoRecovery;
                Config.AutoRecovery = _autoRecovery;
                MarkDirty();
                Invalidate();
            });
        cy += toggle1.H;

        var toggle2 = MakeToggle(x, iw, cy,
            "RUN MEMORY",
            _runMemory
                ? "On. The bridges that already worked here race beside the mode " +
                  "you picked, e.g. webtunnel-memory."
                : "Off. Only the mode you picked is connected, with nothing beside " +
                  "it. Healthy bridges are still read from the log and still " +
                  "added to that mode's memory list.",
            _runMemory,
            () =>
            {
                _runMemory = !_runMemory;
                Config.RunMemory = _runMemory;
                MarkDirty();
                Invalidate();
            });
        cy += toggle2.H;

        var cardH = cy + CardPadY;

        flow.Add(cardH + CardGap, (g, sy) =>
        {
            CardBg(g, CardMargin, sy, w - CardMargin * 2f, cardH);
            DrawCardTitle(g, sy + titleTop, x, iw, "TRANSPORT",
                null, default, null, top + titleTop);

            // CONNECT VIA dropdown (SettingsDropdown, MainActivity 3320).
            var box = new RectangleF(x, sy + ddRel, iw, ddH);
            using (var path = DrawUtil.RoundedRect(box, 12f))
            {
                using var fill = new SolidBrush(DeltaTorTheme.Surface);
                g.FillPath(fill, path);
                using var pen = new Pen(DeltaTorTheme.BorderLight);
                g.DrawPath(pen, path);
            }
            const string label = "CONNECT VIA";
            var ddTextY = sy + ddRel + (ddH - _bodyLineH) / 2f;
            using (var b = new SolidBrush(DeltaTorTheme.Muted))
                DrawUtil.DrawSpaced(g, label, _fLabel, b, x + 14f, ddTextY, iw * 0.5f, 1.1f);
            var labelW = DrawUtil.SpacedWidth(label, _fLabel, 1.1f);
            var picked = TransportModes.FirstOrDefault(o => o.Value == mode).Label ?? mode;
            var value = DrawUtil.Ellipsize(picked, _fBodyBold, 0.2f,
                iw - 28f - labelW - 16f - 8f);
            var valueW = DrawUtil.SpacedWidth(value, _fBodyBold, 0.2f);
            using (var b = new SolidBrush(DeltaTorTheme.Text))
                DrawUtil.DrawSpaced(g, value, _fBodyBold, b,
                    x + iw - 14f - 8f - 8f - valueW, ddTextY, valueW + 2f, 0.2f);
            using (var b = new SolidBrush(DeltaTorTheme.AccentLight))
                g.DrawString("▼", _fBody, b,
                    x + iw - 14f - 8f, ddTextY, StringFormat.GenericTypographic);

            DrawWrap(g, desc, _fBody, DeltaTorTheme.Muted,
                x, sy + ddRel + ddH + descPad, descSize);
            if (hasTwin)
                DrawWrap(g, twinText, _fBody, Color.FromArgb(217, DeltaTorTheme.Muted),
                    x, sy + ddRel + ddH + descPad + descSize.Height + 2f + 6f, twinSize);

            DrawToggle(g, toggle1, x, iw, sy, top);
            DrawToggle(g, toggle2, x, iw, sy, top);

            _scrollHits.Add((ddContent, () => OpenDrop(ddContent,
                TransportModes.ToList(), mode,
                v =>
                {
                    _transportMode = v;
                    Config.TransportMode = v;
                    // Publish it too, so a drawer opened before the next
                    // connect starts shows the same thing.
                    AppState.SetMode(v);
                    MarkDirty();
                    Invalidate();
                })));
        });
    }

    private static string TransportDescription(string mode) => mode switch
    {
        ParallelTorManager.TransportAuto =>
            "Races vanilla, obfs4, webtunnel and the previously working bridges at the same time.",
        ParallelTorManager.TransportFresh =>
            "Every 72-hour collector list in one go: vanilla, obfs4 and webtunnel, IPv4 and IPv6, merged into a single list.",
        ParallelTorManager.TransportCombined =>
            "Every bridge except Fresh: the vanilla, obfs4 and webtunnel lists merged into a single list.",
        ParallelTorManager.TransportVanilla => "Plain bridges, no pluggable transport.",
        ParallelTorManager.TransportObfs4 => "obfs4 only, via lyrebird.",
        ParallelTorManager.TransportWebtunnel => "webtunnel only, via lyrebird. Needs IPv6.",
        ParallelTorManager.TransportSnowflake =>
            "Snowflake only, via lyrebird. Uses the bundled two bridges.",
        ParallelTorManager.TransportDirect =>
            "No bridges at all — connects straight to a guard.",
        _ => "Uses only the bridge lines you paste below."
    };

    // ---- AUTO RACERS --------------------------------------------------------

    private void AddRacersCard(Flow flow)
    {
        var w = flow.W;
        var x = CardMargin + CardPadX;
        var iw = w - CardMargin * 2f - CardPadX * 2f;
        var top = flow.Y;

        var cy = CardPadY;
        var titleTop = cy;
        cy += 2f + _cardTitleLineH + 4f;

        var rows = new List<(float RelY, string Choice, bool On, bool Only)>();
        foreach (var choice in Config.AutoTransportChoices)
        {
            var on = _autoTransports.Contains(choice);
            rows.Add((cy, choice, on, on && _autoTransports.Count == 1));
            cy += 10f + _labelLineH + 10f;
        }

        const string footText =
            "Auto starts every ticked transport at once and keeps the first that reaches 100%.";
        var footSize = Wrap(footText, _fBody, iw);
        cy += footSize.Height;

        var cardH = cy + CardPadY;

        flow.Add(cardH + CardGap, (g, sy) =>
        {
            CardBg(g, CardMargin, sy, w - CardMargin * 2f, cardH);
            DrawCardTitle(g, sy + titleTop, x, iw, "AUTO RACERS",
                null, default, null, top + titleTop);

            foreach (var (relY, choice, on, only) in rows)
            {
                var rowH = 10f + _labelLineH + 10f;
                var rowY = sy + relY;
                var check = new RectangleF(x + 6f, rowY + (rowH - 18f) / 2f, 18f, 18f);
                using (var path = DrawUtil.RoundedRect(check, 5f))
                {
                    if (on)
                    {
                        using var fill = new SolidBrush(DeltaTorTheme.Accent);
                        g.FillPath(fill, path);
                    }
                    using var pen = new Pen(on ? DeltaTorTheme.Accent : DeltaTorTheme.BorderLight);
                    g.DrawPath(pen, path);
                }
                if (on)
                {
                    using var b = new SolidBrush(Color.White);
                    g.DrawString("✓", _fLabel, b,
                        check.X + 3f, check.Y + (18f - _labelLineH) / 2f);
                }
                using (var b = new SolidBrush(on ? DeltaTorTheme.Text : DeltaTorTheme.Muted))
                    DrawUtil.DrawSpaced(g, choice.ToUpperInvariant(), _fLabel, b,
                        check.Right + 12f, rowY + (rowH - _labelLineH) / 2f,
                        iw - 36f - 80f, 1.2f);
                if (only)
                {
                    using var b = new SolidBrush(DeltaTorTheme.Amber);
                    var onlyW = DrawUtil.SpacedWidth("only one left", _fLabel, 0.6f);
                    DrawUtil.DrawSpaced(g, "only one left", _fLabel, b,
                        x + iw - 6f - onlyW, rowY + (rowH - _labelLineH) / 2f,
                        onlyW + 2f, 0.6f);
                }

                var captured = choice;
                var capturedOnly = only;
                _scrollHits.Add((
                    new RectangleF(x, top + relY, iw, rowH),
                    () =>
                    {
                        // The last remaining tick cannot be unticked: auto
                        // with an empty racer set has nothing to race.
                        if (capturedOnly) return;
                        if (!_autoTransports.Remove(captured)) _autoTransports.Add(captured);
                        Config.AutoTransports = _autoTransports;
                        MarkDirty();
                        Invalidate();
                    }));
            }

            DrawWrap(g, footText, _fBody, DeltaTorTheme.Muted,
                x, sy + cy - footSize.Height, footSize);
        });
    }

    // ---- CUSTOM BRIDGES -----------------------------------------------------

    private void AddCustomBridgesCard(Flow flow)
    {
        var w = flow.W;
        var x = CardMargin + CardPadX;
        var iw = w - CardMargin * 2f - CardPadX * 2f;
        var top = flow.Y;

        var cy = CardPadY;
        var titleTop = cy;
        cy += 2f + _cardTitleLineH + 4f + 4f;

        var boxRel = cy;
        var boxH = _monoLineH * 5f + 8f;
        cy += boxH + 4f;

        var count = _customBridges.Split('\n').Count(
            l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#'));
        var hint = count == 0
            ? "No bridges yet — paste at least one line."
            : $"{count} bridge line(s)";
        var hintColor = count == 0 ? DeltaTorTheme.Amber : DeltaTorTheme.Green;
        var hintRel = cy;
        cy += _labelLineH;

        var cardH = cy + CardPadY;

        flow.Add(cardH + CardGap, (g, sy) =>
        {
            CardBg(g, CardMargin, sy, w - CardMargin * 2f, cardH);
            DrawCardTitle(g, sy + titleTop, x, iw, "CUSTOM BRIDGES",
                null, default, null, top + titleTop);

            EnsureCustomBox();
            PlaceChild(_customBox!,
                new RectangleF(x, sy + boxRel, iw, boxH));

            using var b = new SolidBrush(hintColor);
            DrawUtil.DrawSpaced(g, hint, _fLabel, b, x, sy + hintRel, iw, 1.1f);
        });
    }

    private void EnsureCustomBox()
    {
        if (_customBox != null) return;
        _customBox = new DrawerEdit
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Font = _fMono,
            BackColor = DeltaTorTheme.Surface,
            ForeColor = DeltaTorTheme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Visible = false,
            Text = _customBridges,
            PlaceholderText =
                "snowflake 192.0.2.3:80 FINGERPRINT url=...\nobfs4 1.2.3.4:443 FINGERPRINT cert=..."
        };
        _customBox.WheelToDrawer = OnMouseWheel;
        _customBox.TextChanged += (_, _) =>
        {
            _customBridges = _customBox.Text;
            Config.CustomBridges = _customBridges;
            // The line-count hint under the box reads live data.
            MarkDirty();
            Invalidate();
        };
        Controls.Add(_customBox);
    }

    // ---- PROXY ONLY ---------------------------------------------------------

    private const string ProxyDescription =
        "Tor will bootstrap normally and listen on a local address. " +
        "No tunnel is created and nothing on this device is routed " +
        "automatically — you point each app at the address yourself.";

    private void AddProxyCard(Flow flow)
    {
        var w = flow.W;
        var x = CardMargin + CardPadX;
        var iw = w - CardMargin * 2f - CardPadX * 2f;
        var top = flow.Y;

        var cy = CardPadY;
        var titleTop = cy;
        cy += 2f + _cardTitleLineH + 4f + 4f;

        var swView = MakeSwitch(x, iw, cy, "No VPN Only Proxy", ProxyDescription);
        cy += swView.H;
        EnsureProxySwitch();

        // The endpoint block below is drawn whether or not Tor is up yet:
        // the card must not grow mid-bootstrap and shove the log card down.
        var showEndpoint = _proxyOnly;
        float divRel = 0f, epRel = 0f, liveRel = 0f, appsRel = 0f, copyRel = 0f, pasteRel = 0f;
        Size liveSize = default, appsSize = default, pasteSize = default;
        var liveText = "";
        const string appsText =
            "Your own apps can also reach Tor at this address, which " +
            "is what an app with its own proxy setting needs.";
        const string pasteText =
            "Paste it into the app's network settings as the SOCKS5 " +
            "host and port. Turning proxy mode off needs a reconnect.";
        var endpoint = "";
        var shown = "";
        var copyTarget = "";

        if (showEndpoint)
        {
            divRel = cy;
            cy += 1f + 12f;

            endpoint = AppState.State.SocksEndpoint;
            shown = endpoint.Length > 0 ? StripScheme(endpoint) : $"127.0.0.1:{Config.ProxyPort}";
            copyTarget = shown;

            epRel = cy;
            cy += _monoBigLineH + 4f;

            liveText = endpoint.Length > 0
                ? "Live. Set this as SOCKS5 in a browser or another app."
                : "Not running yet — connect and this address becomes live.";
            liveSize = Wrap(liveText, _fBody, iw);
            liveRel = cy;
            cy += liveSize.Height;
            cy += 6f;

            appsSize = Wrap(appsText, _fBody, iw);
            appsRel = cy;
            cy += appsSize.Height;
            cy += 8f;

            copyRel = cy;
            cy += _bodyLineH + 8f;
            cy += 2f;

            pasteSize = Wrap(pasteText, _fBody, iw);
            pasteRel = cy;
            cy += pasteSize.Height;
        }

        var cardH = cy + CardPadY;

        flow.Add(cardH + CardGap, (g, sy) =>
        {
            CardBg(g, CardMargin, sy, w - CardMargin * 2f, cardH);
            DrawCardTitle(g, sy + titleTop, x, iw, "PROXY ONLY",
                null, default, null, top + titleTop);
            DrawSwitch(g, swView, x, iw, sy, _proxySwitch!);
            if (!showEndpoint) return;

            using (var b = new SolidBrush(Color.FromArgb(153, DeltaTorTheme.BorderLight)))
                g.FillRectangle(b, x, sy + divRel, iw, 1f);

            using var epBrush = new SolidBrush(
                endpoint.Length > 0 ? DeltaTorTheme.Green : DeltaTorTheme.Muted);
            g.DrawString(shown, _fMonoBig, epBrush, x, sy + epRel);

            DrawWrap(g, liveText, _fBody, DeltaTorTheme.Muted, x, sy + liveRel, liveSize);
            DrawWrap(g, appsText, _fBody, Color.FromArgb(204, DeltaTorTheme.Muted),
                x, sy + appsRel, appsSize);

            using (var b = new SolidBrush(DeltaTorTheme.AccentLight))
                DrawUtil.DrawSpaced(g, "Tap to copy this address", _fBody, b,
                    x, sy + copyRel + 4f, iw, 0.2f);
            _scrollHits.Add((
                new RectangleF(x, top + copyRel, iw, _bodyLineH + 8f),
                () =>
                {
                    try
                    {
                        Clipboard.SetText(copyTarget);
                    }
                    catch
                    {
                        // Another app held the clipboard; best-effort.
                    }
                    AppLog.I("UI", $"Copied SOCKS endpoint {copyTarget}");
                }));

            DrawWrap(g, pasteText, _fBody, DeltaTorTheme.Muted, x, sy + pasteRel, pasteSize);
        });
    }

    private static string StripScheme(string endpoint)
    {
        var idx = endpoint.IndexOf("://", StringComparison.Ordinal);
        return idx >= 0 ? endpoint[(idx + 2)..] : endpoint;
    }

    private void EnsureProxySwitch()
    {
        if (_proxySwitch != null) return;
        _proxySwitch = new ToggleSwitch { Checked = _proxyOnly, Visible = false };
        _proxySwitch.CheckedChanged += (_, _) =>
        {
            _proxyOnly = _proxySwitch.Checked;
            Config.ProxyOnlyMode = _proxyOnly;
        };
        Controls.Add(_proxySwitch);
    }

    // ---- TORRC TEMPLATE -----------------------------------------------------

    private void AddTorrcCard(Flow flow)
    {
        if (_savedFlash && Environment.TickCount64 - _savedAt > 1500)
            _savedFlash = false;

        var w = flow.W;
        var x = CardMargin + CardPadX;
        var iw = w - CardMargin * 2f - CardPadX * 2f;
        var top = flow.Y;

        var cy = CardPadY;
        var titleTop = cy;
        cy += 2f + _cardTitleLineH + 4f;

        const string descText =
            "A ready-made torrc template. Bridges and pluggable transports " +
            "are appended automatically · applied on next connect.";
        var descSize = Wrap(descText, _fBody, iw);
        cy += 4f + descSize.Height + 4f;

        var boxRel = cy;
        EnsureTorrcBox();
        var lineCount = Math.Clamp(_templateText.Split('\n').Length, 10, 18);
        var boxH = _monoLineH * lineCount + 8f;
        cy += boxH + 6f;

        const string statusText = "One directive per line · lines starting with # are ignored";
        var saveW = 18f + DrawUtil.SpacedWidth("SAVE", _fLabel, 1.2f) + 18f;
        var saveH = 8f + _labelLineH + 8f;
        var statusW = iw - saveW - 12f;
        var saved = _savedFlash;
        var statusSize = saved ? Size.Empty : Wrap(statusText, _fBody, statusW);
        var rowRel = cy;
        var blockH = Math.Max(Math.Max(statusSize.Height, _bodyLineH), saveH);
        var saveRel = rowRel + (blockH - saveH) / 2f;
        var statusRel = rowRel + (blockH - (saved ? _bodyLineH : statusSize.Height)) / 2f;
        cy += blockH;

        var cardH = cy + CardPadY;

        flow.Add(cardH + CardGap, (g, sy) =>
        {
            CardBg(g, CardMargin, sy, w - CardMargin * 2f, cardH);
            DrawCardTitle(g, sy + titleTop, x, iw, "TORRC TEMPLATE",
                "RESET", DeltaTorTheme.AccentLight,
                () =>
                {
                    TorrcSettings.ResetTemplate();
                    _templateText = TorrcSettings.Template();
                    if (_torrcBox != null) _torrcBox.Text = _templateText;
                    MarkDirty();
                    Invalidate();
                }, top + titleTop);

            DrawWrap(g, descText, _fBody, DeltaTorTheme.Muted,
                x, sy + titleTop + 2f + _cardTitleLineH + 4f + 4f, descSize);

            EnsureTorrcBox();
            PlaceChild(_torrcBox!,
                new RectangleF(x, sy + boxRel, iw, boxH));

            if (saved)
            {
                using var b = new SolidBrush(DeltaTorTheme.GreenLight);
                DrawUtil.DrawSpaced(g, "SAVED ✓", _fBody, b, x, sy + statusRel, statusW, 0.2f);
            }
            else
            {
                DrawWrap(g, statusText, _fBody, DeltaTorTheme.Muted,
                    x, sy + statusRel, statusSize);
            }

            var saveRect = new RectangleF(x + iw - saveW, sy + saveRel, saveW, saveH);
            using (var path = DrawUtil.RoundedRect(saveRect, 12f))
            using (var b = new System.Drawing.Drawing2D.LinearGradientBrush(
                saveRect, DeltaTorTheme.AccentDark, DeltaTorTheme.Accent, 0f))
                g.FillPath(b, path);
            using (var b = new SolidBrush(Color.White))
                DrawUtil.DrawSpacedCentered(g, "SAVE", _fLabel, b, saveRect, 1.2f);

            _scrollHits.Add((
                new RectangleF(x + iw - saveW, top + saveRel, saveW, saveH),
                () =>
                {
                    TorrcSettings.SetTemplate(_templateText);
                    _savedFlash = true;
                    _savedAt = Environment.TickCount64;
                    MarkDirty();
                    Invalidate();
                }));
        });
    }

    private void EnsureTorrcBox()
    {
        if (_torrcBox != null) return;
        _torrcBox = new DrawerEdit
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Font = _fMono,
            BackColor = Color.FromArgb(0xFF, 0x1A, 0x1E, 0x28),
            ForeColor = DeltaTorTheme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Visible = false,
            Text = _templateText
        };
        _torrcBox.WheelToDrawer = OnMouseWheel;
        _torrcBox.TextChanged += (_, _) =>
        {
            _templateText = _torrcBox.Text;
            // Height follows the line count (clamped), so a typed newline
            // or a long paste must reflow the card.
            MarkDirty();
            Invalidate();
        };
        Controls.Add(_torrcBox);
    }

    // ---- BRIDGE STORE -------------------------------------------------------

    private static int MemOf(BridgeState bs, string transport) =>
        bs.Memory.TryGetValue(transport, out var n) ? n : 0;

    private void AddBridgeStoreCard(Flow flow)
    {
        var w = flow.W;
        var x = CardMargin + CardPadX;
        var iw = w - CardMargin * 2f - CardPadX * 2f;
        var top = flow.Y;
        var bs = AppState.BridgeState;

        var cy = CardPadY;
        var titleRowH = _labelLineH + 12f;
        var titleY = cy + (titleRowH - _labelLineH) / 2f;

        var spinnerMode = bs.Updating;
        RectangleF spinRel = default, exportRel = default, updateRel = default;
        if (spinnerMode)
        {
            var tw = DrawUtil.SpacedWidth("UPDATING …", _fLabel, 1.1f);
            spinRel = new RectangleF(x + iw - tw - 8f - 14f,
                cy + (titleRowH - 14f) / 2f, 14f, 14f);
        }
        else
        {
            var boxH = 6f + _labelLineH + 6f;
            var updateW = 14f + DrawUtil.SpacedWidth("UPDATE", _fLabel, 1.2f) + 14f;
            updateRel = new RectangleF(x + iw - updateW, cy, updateW, boxH);
            var exportW = 14f + DrawUtil.SpacedWidth("EXPORT", _fLabel, 1.2f) + 14f;
            exportRel = new RectangleF(updateRel.X - 8f - exportW, cy, exportW, boxH);
        }
        cy += titleRowH + 12f;

        var stat1Rel = cy;
        var stat2Rel = cy + (_statLineH + 3f + _labelLineH + 2f + _tinyLineH) + 12f;
        cy = stat2Rel + (_statLineH + 3f + _labelLineH + 2f + _tinyLineH) + 12f;

        var failed = bs.Error != null;
        var statusLine = failed
            ? $"Update failed · {bs.Error}"
            : $"Updated {RelativeTime(bs.LastUpdateMillis)} · auto every 24h";
        var statusSize = Wrap(statusLine, _fBody, iw - 15f);
        var statusRel = cy;
        cy += statusSize.Height;

        var cardH = cy + CardPadY;
        var cellW = (iw - 2f) / 3f;
        var blockH = _statLineH + 3f + _labelLineH + 2f + _tinyLineH;

        var row1 = new (string Label, int Count, int Mem, Color Dot)[]
        {
            ("VANILLA", bs.Vanilla, MemOf(bs, ParallelTorManager.TransportVanilla), DeltaTorTheme.Accent),
            ("OBFS4", bs.Obfs4, MemOf(bs, ParallelTorManager.TransportObfs4), DeltaTorTheme.Green),
            ("WEBTUNNEL", bs.Webtunnel, MemOf(bs, ParallelTorManager.TransportWebtunnel), DeltaTorTheme.Amber)
        };
        var row2 = new (string Label, int Count, int Mem, Color Dot)[]
        {
            ("SNOWFLAKE", bs.Snowflake, MemOf(bs, ParallelTorManager.TransportSnowflake), DeltaTorTheme.AccentSoft),
            ("FRESH", bs.Fresh, MemOf(bs, ParallelTorManager.TransportFresh), DeltaTorTheme.GreenLight),
            ("COMBINED-BRIDGE", bs.Combined, MemOf(bs, ParallelTorManager.TransportCombined), DeltaTorTheme.AmberLight)
        };

        flow.Add(cardH + CardGap, (g, sy) =>
        {
            CardBg(g, CardMargin, sy, w - CardMargin * 2f, cardH);

            using (var b = new SolidBrush(DeltaTorTheme.Muted))
                DrawUtil.DrawSpaced(g, "BRIDGE STORE", _fLabel, b,
                    x, sy + titleY, iw, 1.6f);

            if (spinnerMode)
            {
                using var b = new SolidBrush(DeltaTorTheme.Amber);
                DrawUtil.DrawSpaced(g, "UPDATING …", _fLabel, b,
                    spinRel.X + 14f + 8f, sy + titleY, iw, 1.1f);
                EnsureStoreSpinner();
                PlaceChild(_storeSpinner!,
                    new RectangleF(spinRel.X, sy + spinRel.Y, 14f, 14f));
            }
            else
            {
                var exportRect = new RectangleF(
                    exportRel.X, sy + exportRel.Y, exportRel.Width, exportRel.Height);
                var updateRect = new RectangleF(
                    updateRel.X, sy + updateRel.Y, updateRel.Width, updateRel.Height);
                DrawBox(g, exportRect, false, "EXPORT", DeltaTorTheme.Text);
                DrawBox(g, updateRect, true, "UPDATE", DeltaTorTheme.AccentLight);
                _scrollHits.Add((
                    new RectangleF(exportRel.X, top + exportRel.Y,
                        exportRel.Width, exportRel.Height),
                    DoExport));
                _scrollHits.Add((
                    new RectangleF(updateRel.X, top + updateRel.Y,
                        updateRel.Width, updateRel.Height),
                    BridgeStore.Update));
            }

            DrawStatRow(g, x, cellW, sy + stat1Rel, blockH, row1);
            DrawStatRow(g, x, cellW, sy + stat2Rel, blockH, row2);

            using (var path = DrawUtil.RoundedRect(
                new RectangleF(x, sy + statusRel + (statusSize.Height - 7f) / 2f, 7f, 7f), 3.5f))
            using (var b = new SolidBrush(failed ? DeltaTorTheme.Red : DeltaTorTheme.Accent))
                g.FillPath(b, path);
            DrawWrap(g, statusLine, _fBody, failed ? DeltaTorTheme.Red : DeltaTorTheme.Muted,
                x + 15f, sy + statusRel, statusSize);
        });
    }

    /// <summary>One row of three stat cells: dot + count, label, memory line,
    /// with hairlines between the cells (StatCell, MainActivity 2147).</summary>
    private void DrawStatRow(
        Graphics g, float x, float cellW, float rowY, float blockH,
        (string Label, int Count, int Mem, Color Dot)[] cells)
    {
        for (var i = 0; i < cells.Length; i++)
        {
            var (label, count, mem, dot) = cells[i];
            var cx = x + i * (cellW + 1f);
            var countText = count.ToString();
            var countW = DrawUtil.SpacedWidth(countText, _fStat, 0f);
            var rowX = cx + (cellW - (14f + countW)) / 2f;
            using (var path = DrawUtil.RoundedRect(
                new RectangleF(rowX, rowY + (_statLineH - 7f) / 2f, 7f, 7f), 3.5f))
            using (var b = new SolidBrush(dot))
                g.FillPath(b, path);
            using (var b = new SolidBrush(DeltaTorTheme.Text))
                DrawUtil.DrawSpaced(g, countText, _fStat, b,
                    rowX + 14f, rowY, countW + 2f, 0f);
            using (var b = new SolidBrush(DeltaTorTheme.Muted))
                DrawUtil.DrawSpacedCentered(g, label, _fLabel, b,
                    new RectangleF(cx, rowY + _statLineH + 3f, cellW, _labelLineH), 1f);
            using (var b = new SolidBrush(
                mem > 0 ? DeltaTorTheme.GreenLight : DeltaTorTheme.Muted))
                DrawUtil.DrawSpacedCentered(g, $"{mem} mem", _fTiny, b,
                    new RectangleF(cx, rowY + _statLineH + 3f + _labelLineH + 2f,
                        cellW, _tinyLineH), 0.5f);
            if (i > 0)
            {
                using var b = new SolidBrush(DeltaTorTheme.Border);
                g.FillRectangle(b, cx - 1f, rowY + (blockH - 30f) / 2f, 1f, 30f);
            }
        }
    }

    /// <summary>EXPORT: zip the store and reveal it in Explorer
    /// (Android shares the zip; Windows has no share sheet).</summary>
    private static void DoExport()
    {
        var zip = BridgeExport.Build();
        if (zip == null) return;
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
            // Explorer failing must not take the drawer with it.
        }
    }

    private void EnsureStoreSpinner()
    {
        if (_storeSpinner != null) return;
        _storeSpinner = new Spinner { SpinColor = DeltaTorTheme.Amber, Visible = false };
        Controls.Add(_storeSpinner);
    }

    private static string RelativeTime(long ms)
    {
        if (ms <= 0) return "never";
        var secs = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ms) / 1000;
        return secs < 60 ? "just now"
            : secs < 3600 ? $"{secs / 60}m ago"
            : secs < 86_400 ? $"{secs / 3600}h ago"
            : $"{secs / 86_400}d ago";
    }

    // ---- CONNECTION LOG -----------------------------------------------------

    private void AddLogCard(Flow flow)
    {
        var w = flow.W;
        var x = CardMargin + CardPadX;
        var iw = w - CardMargin * 2f - CardPadX * 2f;
        var top = flow.Y;

        var cy = CardPadY;
        var titleTop = cy;
        cy += 2f + _cardTitleLineH + 4f;

        const string desc = "Shows the live bootstrap; press COPY to share it.";
        const float rowH = 64f;
        var pillW = 16f + DrawUtil.SpacedWidth("VIEW LOG", _fLabel, 1.2f) + 16f;
        var pillH = 10f + _labelLineH + 10f;
        var rowRel = cy;
        var descSize = Wrap(desc, _fBody, iw - pillW - 12f);
        var colH = _rowLineH + 3f + descSize.Height;
        var pillRel = rowRel + (rowH - pillH) / 2f;
        var headRel = rowRel + (rowH - colH) / 2f;
        cy += rowH;

        var divRel = cy;
        cy += 1f + 10f;

        var recDesc = _loggingOn
            ? "Keeps every Tor line of every connect. Turn it off to stop " +
              "recording; the reason for a failure still shows on the screen."
            : "Nothing is being recorded. The bootstrap still runs — it just " +
              "is not kept, so a later log has nothing in it.";
        var recView = MakeSwitch(x, iw, cy, "Record the log", recDesc);
        cy += recView.H;

        var cardH = cy + CardPadY;

        flow.Add(cardH + CardGap, (g, sy) =>
        {
            CardBg(g, CardMargin, sy, w - CardMargin * 2f, cardH);
            DrawCardTitle(g, sy + titleTop, x, iw, "CONNECTION LOG",
                null, default, null, top + titleTop);

            using (var b = new SolidBrush(DeltaTorTheme.Text))
                DrawUtil.DrawSpaced(g, "View connection log", _fRow, b,
                    x, sy + headRel, iw - pillW - 12f, 0.3f);
            DrawWrap(g, desc, _fBody, DeltaTorTheme.Muted, x,
                sy + headRel + _rowLineH + 3f, descSize);

            var pillRect = new RectangleF(x + iw - pillW, sy + pillRel, pillW, pillH);
            using (var path = DrawUtil.RoundedRect(pillRect, 12f))
            using (var b = new System.Drawing.Drawing2D.LinearGradientBrush(
                pillRect, DeltaTorTheme.AccentDark, DeltaTorTheme.Accent, 0f))
                g.FillPath(b, path);
            using (var b = new SolidBrush(Color.White))
                DrawUtil.DrawSpacedCentered(g, "VIEW LOG", _fLabel, b, pillRect, 1.2f);
            _scrollHits.Add((
                new RectangleF(x + iw - pillW, top + pillRel, pillW, pillH),
                () => OpenLog?.Invoke()));

            using (var b = new SolidBrush(Color.FromArgb(153, DeltaTorTheme.BorderLight)))
                g.FillRectangle(b, x, sy + divRel, iw, 1f);

            EnsureLogSwitch();
            DrawSwitch(g, recView, x, iw, sy, _logSwitch!);
        });
    }

    private void EnsureLogSwitch()
    {
        if (_logSwitch != null) return;
        _logSwitch = new ToggleSwitch { Checked = _loggingOn, Visible = false };
        _logSwitch.CheckedChanged += (_, _) =>
        {
            _loggingOn = _logSwitch.Checked;
            Config.LoggingEnabled = _loggingOn;
            AppLog.Enabled = _loggingOn;
        };
        Controls.Add(_logSwitch);
    }
}
