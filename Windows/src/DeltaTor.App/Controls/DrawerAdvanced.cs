using DeltaTor.Core;
using DeltaTor.Core.Util;

namespace DeltaTor.App.Controls;

/// <summary>
/// The ADVANCED section of the drawer (MainActivity AdvancedItems, line
/// 2960): one painted SettingsCard per card, laid out through deferred draw
/// ops so each card can size its background box from the content it just
/// measured. This file carries the form state, the shared card primitives and
/// the first three cards; PROXY ONLY / TORRC / BRIDGES / LOG follow in the
/// next pass.
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

    private sealed class DropState
    {
        public required List<(string Value, string Label)> Options { get; init; }
        public required RectangleF Trigger { get; init; }
        public required string Value { get; init; }
        public required Action<string> OnSelect { get; init; }
        public readonly List<(RectangleF Rect, string Value)> Rows = new();
        public RectangleF Panel;
    }

    private DropState? _drop;
    private TextBox? _customBox;
    private TextBox? _torrcBox;
    private ToggleSwitch? _proxySwitch;
    private ToggleSwitch? _logSwitch;
    private Spinner? _storeSpinner;

    /// <summary>MainForm hooks this to the log screen once it exists.</summary>
    public event Action? OpenLog;

    // ---- shared card primitives --------------------------------------------

    private const float CardMargin = 20f;
    private const float CardPadX = 16f;
    private const float CardPadY = 8f;

    /// <summary>SettingsCard (MainActivity 2901): #20242F→Surface tile,
    /// Border hairline, radius 18.</summary>
    private void CardBg(Graphics g, float x, float y, float w, float h)
    {
        var rect = new RectangleF(x, y, w, h);
        using var path = DrawUtil.RoundedRect(rect, 18f);
        using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
            rect, Color.FromArgb(0xFF, 0x20, 0x24, 0x2F), DeltaTorTheme.Surface, 90f))
            g.FillPath(brush, path);
        using var pen = new Pen(DeltaTorTheme.Border);
        g.DrawPath(pen, path);
    }

    /// <summary>Runs the deferred draw ops after the card box is behind them.
    /// An off-screen card still lays out (the y-flow needs its height) but
    /// draws nothing: no gradient tile, no text, no hits. Its child control
    /// keeps the visibility its own op last set, so nothing floats over the
    /// cards that are on screen.</summary>
    private float RunCard(Graphics g, float y, float w,
        Func<float, float, List<Action<Graphics>>, float> layout, float padY = CardPadY)
    {
        var ops = new List<Action<Graphics>>();
        var innerX = CardMargin + CardPadX;
        var innerW = w - CardMargin * 2f - CardPadX * 2f;
        var contentH = layout(innerX, innerW, ops);
        var h = contentH + padY * 2f;
        if (y + h > _viewTop && y < _viewBottom)
        {
            CardBg(g, CardMargin, y, w - CardMargin * 2f, h);
            foreach (var op in ops) op(g);
        }
        return y + h + 20f;
    }

    /// <summary>Approximate letter-spaced width without a Graphics handle
    /// (layout runs before the ops draw, where no handle is in scope).</summary>
    private static float MeasureSpaced(string text, Font font, float spacing) =>
        TextRenderer.MeasureText(text, font).Width + spacing * Math.Max(0, text.Length - 1);

    /// <summary>CardTitle (MainActivity 2734): labelSmall, letter-spaced,
    /// Muted, with an optional trailing action.</summary>
    private void LayoutCardTitle(
        float x, float w, float y, string title, Action<Graphics>? trailing, List<Action<Graphics>> ops)
    {
        var top = y + 2f;
        ops.Add(g =>
        {
            using var brush = new SolidBrush(DeltaTorTheme.Muted);
            DrawUtil.DrawSpaced(g, title, _fCaption, brush, x, top, w, 1.6f);
            trailing?.Invoke(g);
        });
        // Height: caption line + the 2dp paddings around it.
        _lastTitleH = _captionLineH + 4f;
    }

    private float _lastTitleH;

    /// <summary>Wrapped bodySmall text; returns its height.</summary>
    private float LayoutWrapped(
        float x, float w, float y, string text, Color color,
        List<Action<Graphics>> ops, float topPad = 0f, float bottomPad = 0f)
    {
        var ty = y + topPad;
        var size = WrapSize(text, w);
        ops.Add(g => DrawWrapped(g, text, color, x, ty, size));
        return topPad + size.Height + bottomPad;
    }

    private Size WrapSize(string text, float w) =>
        TextRenderer.MeasureText(text, _fSmall, new Size((int)w, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

    private void DrawWrapped(Graphics g, string text, Color color, float x, float y, Size size) =>
        TextRenderer.DrawText(g, text, _fSmall,
            new Rectangle((int)x, (int)y, size.Width + 2, size.Height),
            color, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

    /// <summary>SettingsDropdown (MainActivity 3320): the closed box; the
    /// option list opens as a dark popup handled in the shared click path.</summary>
    private float LayoutDropdown(
        float x, float w, float y, string label,
        List<(string Value, string Label)> options, string value,
        Action<string> onSelect, List<Action<Graphics>> ops)
    {
        var lineH = _bodyLineH;
        var h = 12f + lineH + 12f;
        var box = new RectangleF(x, y, w, h);
        var selectedLabel = options.FirstOrDefault(o => o.Value == value).Label ?? value;
        ops.Add(g =>
        {
            using var path = DrawUtil.RoundedRect(box, 12f);
            using (var fill = new SolidBrush(DeltaTorTheme.Surface))
                g.FillPath(fill, path);
            using var pen = new Pen(
                _drop != null && _drop.Trigger == box
                    ? DeltaTorTheme.AccentLight
                    : DeltaTorTheme.BorderLight);
            g.DrawPath(pen, path);

            using (var brush = new SolidBrush(DeltaTorTheme.Muted))
                DrawUtil.DrawSpaced(g, label, _fCaption, brush,
                    x + 14f, y + (h - lineH) / 2f, w * 0.5f, 1.1f);
            var chevronW = 8f;
            var labelW = DrawUtil.SpacedWidth(g, label, _fCaption, 1.1f);
            var value = DrawUtil.Ellipsize(g, selectedLabel, _fBodyBold, 0.2f,
                w - 28f - labelW - 16f - chevronW);
            var valueW = DrawUtil.SpacedWidth(g, value, _fBodyBold, 0.2f);
            using (var brush = new SolidBrush(DeltaTorTheme.Text))
                DrawUtil.DrawSpaced(g, value, _fBodyBold, brush,
                    x + w - 14f - chevronW - 8f - valueW,
                    y + (h - lineH) / 2f, valueW + 2f, 0.2f);
            using (var brush = new SolidBrush(DeltaTorTheme.AccentLight))
                g.DrawString(_drop != null && _drop.Trigger == box ? "▲" : "▼",
                    _fSmall, brush, x + w - 14f - chevronW, y + (h - _fSmall.GetHeight(g)) / 2f);
        });
        ops.Add(g =>
        {
            _hits.Add((box, () =>
            {
                _drop = new DropState
                {
                    Options = options,
                    Trigger = box,
                    Value = value,
                    OnSelect = onSelect
                };
                Invalidate();
            }, true));
        });
        return h;
    }

    /// <summary>The ON/OFF pill used by AUTO RECOVERY and RUN MEMORY (MainActivity 2041).</summary>
    private float LayoutToggleRow(
        float x, float w, float y, string title, string description,
        bool on, Action onToggle, List<Action<Graphics>> ops)
    {
        // Divider first (AutoRecoveryButton draws DividerLine above the row).
        ops.Add(g =>
        {
            using var div = new SolidBrush(Color.FromArgb(153, DeltaTorTheme.BorderLight));
            g.FillRectangle(div, x, y, w, 1f);
        });
        var rowY = y + 1f + 12f;
        var titleH = _largeLineH;
        var descSize = WrapSize(description, w - 70f - 12f);
        var colH = titleH + 3f + descSize.Height;
        var rowH = Math.Max(colH, 8f + _captionLineH + 8f) + 4f;

        var btnW = 14f + _captionLineH * 1.6f + 14f;
        var btn = new RectangleF(x + w - btnW, rowY + Math.Max(0f, (colH - (8f + _captionLineH + 8f)) / 2f),
            btnW, 8f + _captionLineH + 8f);
        var row = new RectangleF(x, y, w, rowH + 13f);

        ops.Add(g =>
        {
            using (var brush = new SolidBrush(DeltaTorTheme.Text))
                DrawUtil.DrawSpaced(g, title, _fLarge, brush, x, rowY, w, 0.3f);
            DrawWrapped(g, description, DeltaTorTheme.Muted, x, rowY + titleH + 3f, descSize);

            using (var path = DrawUtil.RoundedRect(btn, 12f))
            {
                if (on)
                {
                    using var fill = new SolidBrush(Color.FromArgb(46, DeltaTorTheme.Green)); // 0.18
                    g.FillPath(fill, path);
                }
                else
                {
                    using var fill = new SolidBrush(DeltaTorTheme.SurfaceAlt);
                    g.FillPath(fill, path);
                }
                using var pen = new Pen(on ? DeltaTorTheme.Green : DeltaTorTheme.BorderLight);
                g.DrawPath(pen, path);
            }
            using (var brush = new SolidBrush(on ? DeltaTorTheme.GreenLight : DeltaTorTheme.Muted))
                DrawUtil.DrawSpacedCentered(g, on ? "ON" : "OFF", _fCaption, brush, btn, 1.2f);
        });
        ops.Add(g => _hits.Add((btn, onToggle, true)));
        return row.Height;
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

    private float PaintTransportCard(Graphics g, float y, float w) =>
        RunCard(g, y, w, (x, iw, ops) =>
        {
            var cy = y + CardPadY;

            LayoutCardTitle(x, iw, cy, "TRANSPORT", null, ops);
            cy += _lastTitleH;

            cy += 2f;
            var mode = _transportMode;
            cy += LayoutDropdown(x, iw, cy, "CONNECT VIA", TransportModes.ToList(), mode,
                v =>
                {
                    _transportMode = v;
                    Config.TransportMode = v;
                    // Publish it too, so a drawer that is opened before the next
                    // connect starts shows the same thing.
                    AppState.SetMode(v);
                    Invalidate();
                }, ops);

            cy += LayoutWrapped(x, iw, cy, TransportDescription(mode),
                DeltaTorTheme.Muted, ops, 8f, 2f);

            if (ParallelTorManager.TwinnedModes.Contains(mode))
            {
                // Single-transport modes are not single-runner modes: the memory
                // twin races next to them, so say that up front.
                cy += LayoutWrapped(x, iw, cy,
                    "Every bridge list also gets a memory twin: the bridges " +
                    "that worked in this transport are pulled from the log and " +
                    "race beside it, e.g. " + mode + "-memory. " +
                    "RUN MEMORY below turns that twin off for a connect.",
                    Color.FromArgb(217, DeltaTorTheme.Muted), ops, 6f, 2f);
            }

            cy += LayoutToggleRow(x, iw, cy,
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
                    Invalidate();
                }, ops);

            cy += LayoutToggleRow(x, iw, cy,
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
                    Invalidate();
                }, ops);

            return cy + 4f - (y + CardPadY);
        });

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

    private float PaintAutoRacersCard(Graphics g, float y, float w) =>
        RunCard(g, y, w, (x, iw, ops) =>
        {
            var cy = y + CardPadY;
            LayoutCardTitle(x, iw, cy, "AUTO RACERS", null, ops);
            cy += _lastTitleH;

            foreach (var choice in Config.AutoTransportChoices)
            {
                var captured = choice;
                var on = _autoTransports.Contains(choice);
                var only = on && _autoTransports.Count == 1;
                var rowH = 10f + _captionLineH + 10f;
                var row = new RectangleF(x, cy, iw, rowH);
                var check = new RectangleF(x + 6f, cy + (rowH - 18f) / 2f, 18f, 18f);

                ops.Add(gg =>
                {
                    using (var path = DrawUtil.RoundedRect(check, 5f))
                    {
                        if (on)
                        {
                            using var fill = new SolidBrush(DeltaTorTheme.Accent);
                            gg.FillPath(fill, path);
                        }
                        using var pen = new Pen(on ? DeltaTorTheme.Accent : DeltaTorTheme.BorderLight);
                        gg.DrawPath(pen, path);
                    }
                    if (on)
                    {
                        using var brush = new SolidBrush(Color.White);
                        gg.DrawString("✓", _fCaption, brush,
                            check.X + 3f, check.Y + (18f - _captionLineH) / 2f);
                    }
                    using (var brush = new SolidBrush(on ? DeltaTorTheme.Text : DeltaTorTheme.Muted))
                        DrawUtil.DrawSpaced(gg, choice.ToUpperInvariant(), _fCaption, brush,
                            check.Right + 12f, cy + (rowH - _captionLineH) / 2f,
                            iw - 36f - 80f, 1.2f);
                    if (only)
                    {
                        using var brush = new SolidBrush(DeltaTorTheme.Amber);
                        var onlyW = DrawUtil.SpacedWidth(gg, "only one left", _fCaption, 0.6f);
                        DrawUtil.DrawSpaced(gg, "only one left", _fCaption, brush,
                            x + iw - 6f - onlyW, cy + (rowH - _captionLineH) / 2f,
                            onlyW + 2f, 0.6f);
                    }
                });
                ops.Add(_ => _hits.Add((row, () =>
                {
                    // The last remaining tick cannot be unticked: auto with an
                    // empty racer set has nothing to race.
                    if (only) return;
                    if (!_autoTransports.Remove(captured)) _autoTransports.Add(captured);
                    Config.AutoTransports = _autoTransports;
                    Invalidate();
                }, true)));
                cy += rowH;
            }

            cy += LayoutWrapped(x, iw, cy,
                "Auto starts every ticked transport at once and keeps the first that reaches 100%.",
                DeltaTorTheme.Muted, ops);
            return cy - (y + CardPadY);
        });

    // ---- CUSTOM BRIDGES -----------------------------------------------------

    private float PaintCustomBridgesCard(Graphics g, float y, float w) =>
        RunCard(g, y, w, (x, iw, ops) =>
        {
            var cy = y + CardPadY;
            LayoutCardTitle(x, iw, cy, "CUSTOM BRIDGES", null, ops);
            cy += _lastTitleH + 4f;

            EnsureCustomBox();
            var boxH = _fMono.Height * 5f + 8f;
            var boxRect = new RectangleF(x, cy + 4f, iw, boxH);
            ops.Add(_ =>
            {
                _customBox!.SetBounds(
                    (int)boxRect.X, (int)boxRect.Y,
                    (int)boxRect.Width, (int)boxRect.Height);
                _customBox.Visible = boxRect.Bottom > _viewTop && boxRect.Y < _viewBottom;
            });
            cy += 4f + boxH + 4f;

            var count = _customBridges.Split('\n').Count(
                l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#'));
            var hint = count == 0
                ? "No bridges yet — paste at least one line."
                : $"{count} bridge line(s)";
            ops.Add(gg =>
            {
                using var brush = new SolidBrush(
                    count == 0 ? DeltaTorTheme.Amber : DeltaTorTheme.Green);
                DrawUtil.DrawSpaced(gg, hint, _fCaption, brush, x, cy, iw, 1.1f);
            });
            cy += _captionLineH;
            return cy - (y + CardPadY);
        });

    private void EnsureCustomBox()
    {
        if (_customBox != null) return;
        _customBox = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Font = _fMono,
            BackColor = DeltaTorTheme.Surface,
            ForeColor = DeltaTorTheme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            // Hidden until its card's ops position it on screen: the card may
            // be laid out far below the viewport before ever being drawn.
            Visible = false,
            Text = _customBridges,
            PlaceholderText =
                "snowflake 192.0.2.3:80 FINGERPRINT url=...\nobfs4 1.2.3.4:443 FINGERPRINT cert=..."
        };
        _customBox.TextChanged += (_, _) =>
        {
            _customBridges = _customBox.Text;
            Config.CustomBridges = _customBridges;
        };
        Controls.Add(_customBox);
    }

    // ---- PROXY ONLY ---------------------------------------------------------

    private const string ProxyDescription =
        "Tor will bootstrap normally and listen on a local address. " +
        "No tunnel is created and nothing on this device is routed " +
        "automatically — you point each app at the address yourself.";

    private float PaintProxyCard(Graphics g, float y, float w) =>
        RunCard(g, y, w, (x, iw, ops) =>
        {
            var cy = y + CardPadY;
            LayoutCardTitle(x, iw, cy, "PROXY ONLY", null, ops);
            cy += _lastTitleH + 4f;

            EnsureProxySwitch();
            var rowTop = cy + 4f;
            var descSize = WrapSize(ProxyDescription, iw - 46f - 12f);
            var colH = _largeLineH + 3f + descSize.Height;
            var swRect = new RectangleF(x + iw - 46f,
                rowTop + Math.Max(0f, (colH - 26f) / 2f), 46f, 26f);
            ops.Add(go =>
            {
                using (var br = new SolidBrush(DeltaTorTheme.Text))
                    DrawUtil.DrawSpaced(go, "No VPN Only Proxy", _fLarge, br,
                        x, rowTop, iw - 58f, 0.3f);
                DrawWrapped(go, ProxyDescription, DeltaTorTheme.Muted,
                    x, rowTop + _largeLineH + 3f, descSize);
            });
            ops.Add(go =>
            {
                _proxySwitch!.SetBounds((int)swRect.X, (int)swRect.Y, 46, 26);
                _proxySwitch.Visible =
                    swRect.Bottom > _viewTop && swRect.Y < _viewBottom;
            });
            cy = rowTop + Math.Max(colH, 26f) + 2f;

            if (!_proxyOnly) return cy - (y + CardPadY);

            // The block below is drawn whether or not Tor is up yet: the card
            // must not grow mid-bootstrap and shove the log card down.
            ops.Add(go =>
            {
                using var div = new SolidBrush(Color.FromArgb(153, DeltaTorTheme.BorderLight));
                go.FillRectangle(div, x, cy, iw, 1f);
            });
            cy += 1f + 12f;

            var endpoint = AppState.State.SocksEndpoint;
            var shown = endpoint.Length > 0
                ? StripScheme(endpoint)
                : $"127.0.0.1:{Config.ProxyPort}";
            var copyTarget = endpoint.Length > 0
                ? StripScheme(endpoint)
                : $"127.0.0.1:{Config.ProxyPort}";
            var epColor = endpoint.Length > 0 ? DeltaTorTheme.Green : DeltaTorTheme.Muted;
            var epY = cy;
            ops.Add(go =>
            {
                using var br = new SolidBrush(epColor);
                go.DrawString(shown, _fMonoBig, br, x, epY);
            });
            cy = epY + _monoBigLineH + 4f;

            cy += LayoutWrapped(x, iw, cy,
                endpoint.Length > 0
                    ? "Live. Set this as SOCKS5 in a browser or another app."
                    : "Not running yet — connect and this address becomes live.",
                DeltaTorTheme.Muted, ops);
            cy += 6f;
            cy += LayoutWrapped(x, iw, cy,
                "Your own apps can also reach Tor at this address, which " +
                "is what an app with its own proxy setting needs.",
                Color.FromArgb(204, DeltaTorTheme.Muted), ops, 6f, 0f);
            cy += 8f;

            var copyRect = new RectangleF(x, cy, iw, _smallLineH + 8f);
            ops.Add(go =>
            {
                using var br = new SolidBrush(DeltaTorTheme.AccentLight);
                DrawUtil.DrawSpaced(go, "Tap to copy this address", _fSmall, br,
                    x, cy + 4f, iw, 0.2f);
                _hits.Add((copyRect, () =>
                {
                    try
                    {
                        Clipboard.SetText(copyTarget);
                    }
                    catch
                    {
                        // Another app held the clipboard; the copy is best-effort.
                    }
                    AppLog.I("UI", $"Copied SOCKS endpoint {copyTarget}");
                }, true));
            });
            cy = copyRect.Bottom;
            cy += LayoutWrapped(x, iw, cy,
                "Paste it into the app's network settings as the SOCKS5 " +
                "host and port. Turning proxy mode off needs a reconnect.",
                DeltaTorTheme.Muted, ops, 2f, 0f);
            return cy - (y + CardPadY);
        });

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

    private float PaintTorrcCard(Graphics g, float y, float w) =>
        RunCard(g, y, w, (x, iw, ops) =>
        {
            if (_savedFlash && Environment.TickCount64 - _savedAt > 1500)
                _savedFlash = false;
            var cy = y + CardPadY;

            LayoutCardTitle(x, iw, cy, "TORRC TEMPLATE", go =>
            {
                var lw = MeasureSpaced("RESET", _fCaption, 1.1f);
                var rx = x + iw - lw;
                using (var br = new SolidBrush(DeltaTorTheme.AccentLight))
                    DrawUtil.DrawSpaced(go, "RESET", _fCaption, br, rx, cy + 2f, lw + 2f, 1.1f);
                var rect = new RectangleF(rx - 8f, cy, lw + 16f, _captionLineH + 4f);
                _hits.Add((rect, () =>
                {
                    TorrcSettings.ResetTemplate();
                    _templateText = TorrcSettings.Template();
                    if (_torrcBox != null) _torrcBox.Text = _templateText;
                    Invalidate();
                }, true));
            }, ops);
            cy += _lastTitleH;

            cy += LayoutWrapped(x, iw, cy,
                "A ready-made torrc template. Bridges and pluggable transports " +
                "are appended automatically · applied on next connect.",
                DeltaTorTheme.Muted, ops, 4f, 4f);

            EnsureTorrcBox();
            var lineCount = Math.Clamp(_templateText.Split('\n').Length, 10, 18);
            var boxH = _fMono.Height * lineCount + 8f;
            var boxRect = new RectangleF(x, cy + 4f, iw, boxH);
            ops.Add(go =>
            {
                _torrcBox!.SetBounds(
                    (int)boxRect.X, (int)boxRect.Y,
                    (int)boxRect.Width, (int)boxRect.Height);
                _torrcBox.Visible = boxRect.Bottom > _viewTop && boxRect.Y < _viewBottom;
            });
            cy = boxRect.Bottom + 6f;

            var statusText = "One directive per line · lines starting with # are ignored";
            var statusW = iw - (18f + MeasureSpaced("SAVE", _fCaption, 1.2f) + 18f) - 12f;
            var statusSize = _savedFlash ? Size.Empty : WrapSize(statusText, statusW);
            var saveW = 18f + MeasureSpaced("SAVE", _fCaption, 1.2f) + 18f;
            var saveH = 8f + _captionLineH + 8f;
            var rowTop = cy;
            var blockH = Math.Max(Math.Max(statusSize.Height, _smallLineH), saveH);
            var saveRect = new RectangleF(x + iw - saveW, rowTop + (blockH - saveH) / 2f, saveW, saveH);
            var saved = _savedFlash;
            var statusY = rowTop + (blockH - (saved ? _smallLineH : statusSize.Height)) / 2f;
            ops.Add(go =>
            {
                if (saved)
                {
                    using var br = new SolidBrush(DeltaTorTheme.GreenLight);
                    DrawUtil.DrawSpaced(go, "SAVED ✓", _fSmall, br, x, statusY, statusW, 0.2f);
                }
                else
                {
                    DrawWrapped(go, statusText, DeltaTorTheme.Muted, x, statusY, statusSize);
                }
                using (var path = DrawUtil.RoundedRect(saveRect, 12f))
                using (var br = new System.Drawing.Drawing2D.LinearGradientBrush(
                    saveRect, DeltaTorTheme.AccentDark, DeltaTorTheme.Accent, 0f))
                    go.FillPath(br, path);
                using (var br = new SolidBrush(Color.White))
                    DrawUtil.DrawSpacedCentered(go, "SAVE", _fCaption, br, saveRect, 1.2f);
                _hits.Add((saveRect, () =>
                {
                    TorrcSettings.SetTemplate(_templateText);
                    _savedFlash = true;
                    _savedAt = Environment.TickCount64;
                    Invalidate();
                }, true));
            });
            cy = rowTop + blockH;
            return cy - (y + CardPadY);
        });

    private void EnsureTorrcBox()
    {
        if (_torrcBox != null) return;
        _torrcBox = new TextBox
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
        _torrcBox.TextChanged += (_, _) =>
        {
            _templateText = _torrcBox.Text;
            Invalidate();
        };
        Controls.Add(_torrcBox);
    }

    // ---- BRIDGE STORE -------------------------------------------------------

    private static int MemOf(BridgeState bs, string transport) =>
        bs.Memory.TryGetValue(transport, out var n) ? n : 0;

    private float PaintBridgeStoreCard(Graphics g, float y, float w)
    {
        const float pad = 14f;
        return RunCard(g, y, w, (x, iw, ops) =>
        {
            var cy = y + pad;
            var bs = AppState.BridgeState;
            var rowH = _captionLineH + 12f;
            var titleY = cy + (rowH - _captionLineH) / 2f;

            // Title row with the trailing spinner or the two action boxes.
            var spinnerMode = bs.Updating;
            RectangleF exportRect = default;
            RectangleF updateRect = default;
            RectangleF spinRect = default;
            var trailY = cy;
            if (spinnerMode)
            {
                var tw = MeasureSpaced("UPDATING …", _fCaption, 1.1f);
                spinRect = new RectangleF(x + iw - tw - 8f - 14f,
                    cy + (rowH - 14f) / 2f, 14f, 14f);
            }
            else
            {
                var updateW = 14f + MeasureSpaced("UPDATE", _fCaption, 1.2f) + 14f;
                var boxH = 6f + _captionLineH + 6f;
                updateRect = new RectangleF(x + iw - updateW, trailY, updateW, boxH);
                var exportW = 14f + MeasureSpaced("EXPORT", _fCaption, 1.2f) + 14f;
                exportRect = new RectangleF(updateRect.X - 8f - exportW,
                    updateRect.Y, exportW, boxH);
            }

            ops.Add(go =>
            {
                using (var br = new SolidBrush(DeltaTorTheme.Muted))
                    DrawUtil.DrawSpaced(go, "BRIDGE STORE", _fCaption, br,
                        x, titleY, iw, 1.6f);
                if (spinnerMode)
                {
                    using var br = new SolidBrush(DeltaTorTheme.Amber);
                    DrawUtil.DrawSpaced(go, "UPDATING …", _fCaption, br,
                        spinRect.X + 14f + 8f, titleY, iw, 1.1f);
                }
                else
                {
                    DrawBox(go, exportRect, false, "EXPORT", DeltaTorTheme.Text);
                    DrawBox(go, updateRect, true, "UPDATE", DeltaTorTheme.AccentLight);
                }
            });
            EnsureStoreSpinner();
            ops.Add(go =>
            {
                if (spinnerMode)
                {
                    _storeSpinner!.SetBounds(
                        (int)spinRect.X, (int)spinRect.Y, 14, 14);
                    _storeSpinner.Visible =
                        spinRect.Bottom > _viewTop && spinRect.Y < _viewBottom;
                }
                else if (_storeSpinner != null)
                {
                    _storeSpinner.Visible = false;
                }
            });
            if (!spinnerMode)
            {
                var e = exportRect;
                var u = updateRect;
                ops.Add(_ =>
                {
                    _hits.Add((e, DoExport, true));
                    _hits.Add((u, BridgeStore.Update, true));
                });
            }
            cy += rowH + 12f;

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
            cy += PaintStatRow(x, iw, cy, row1, ops);
            cy += 12f;
            cy += PaintStatRow(x, iw, cy, row2, ops);
            cy += 12f;

            var failed = bs.Error != null;
            var line = failed
                ? $"Update failed · {bs.Error}"
                : $"Updated {RelativeTime(bs.LastUpdateMillis)} · auto every 24h";
            var lineSize = WrapSize(line, iw - 15f);
            ops.Add(go =>
            {
                using (var path = DrawUtil.RoundedRect(
                    new RectangleF(x, cy + (lineSize.Height - 7f) / 2f, 7f, 7f), 3.5f))
                using (var br = new SolidBrush(failed ? DeltaTorTheme.Red : DeltaTorTheme.Accent))
                    go.FillPath(br, path);
                DrawWrapped(go, line, failed ? DeltaTorTheme.Red : DeltaTorTheme.Muted,
                    x + 15f, cy, lineSize);
            });
            cy += lineSize.Height;
            return cy - (y + pad);
        }, pad);
    }

    /// <summary>One third of a stat row: dot + count, label, memory line,
    /// with 1×30 hairlines between the cells (StatCell, MainActivity 2147).</summary>
    private float PaintStatRow(float x, float w, float y,
        (string Label, int Count, int Mem, Color Dot)[] cells, List<Action<Graphics>> ops)
    {
        var cellW = (w - 2f) / 3f;
        var blockH = _largeLineH + 3f + _captionLineH + 2f + _tinyLineH;
        for (var i = 0; i < cells.Length; i++)
        {
            var (label, count, mem, dot) = cells[i];
            var cx = x + i * (cellW + 1f);
            var countText = count.ToString();
            var labelRect = new RectangleF(cx, y + _largeLineH + 3f, cellW, _captionLineH);
            var memRect = new RectangleF(cx, labelRect.Bottom + 2f, cellW, _tinyLineH);
            ops.Add(go =>
            {
                var countW = DrawUtil.SpacedWidth(go, countText, _fLarge, 0f);
                var rowX = cx + (cellW - (14f + countW)) / 2f;
                using (var path = DrawUtil.RoundedRect(
                    new RectangleF(rowX, y + (_largeLineH - 7f) / 2f, 7f, 7f), 3.5f))
                using (var br = new SolidBrush(dot))
                    go.FillPath(br, path);
                using (var br = new SolidBrush(DeltaTorTheme.Text))
                    DrawUtil.DrawSpaced(go, countText, _fLarge, br,
                        rowX + 14f, y, countW + 2f, 0f);
                using (var br = new SolidBrush(DeltaTorTheme.Muted))
                    DrawUtil.DrawSpacedCentered(go, label, _fCaption, br, labelRect, 1f);
                using (var br = new SolidBrush(
                    mem > 0 ? DeltaTorTheme.GreenLight : DeltaTorTheme.Muted))
                    DrawUtil.DrawSpacedCentered(go, $"{mem} mem", _fTiny, br, memRect, 0.5f);
            });
            if (i > 0)
            {
                var divX = cx - 1f;
                ops.Add(go =>
                {
                    using var br = new SolidBrush(DeltaTorTheme.Border);
                    go.FillRectangle(br, divX, y + (blockH - 30f) / 2f, 1f, 30f);
                });
            }
        }
        return blockH;
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

    /// <summary>Small action box: outlined (EXPORT) or gradient-filled
    /// (UPDATE), as the Android card draws them.</summary>
    private static void DrawBox(Graphics g, RectangleF rect, bool gradient,
        string text, Color textColor)
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
        using var br = new SolidBrush(textColor);
        DrawUtil.DrawSpacedCentered(g, text, DeltaTorTheme.Caption, br, rect, 1.2f);
    }

    // ---- CONNECTION LOG -----------------------------------------------------

    private float PaintLogCard(Graphics g, float y, float w) =>
        RunCard(g, y, w, (x, iw, ops) =>
        {
            var cy = y + CardPadY;
            LayoutCardTitle(x, iw, cy, "CONNECTION LOG", null, ops);
            cy += _lastTitleH;

            EnsureLogSwitch();
            var rowTop = cy;
            const float rowH = 64f;
            var desc = "Shows the live bootstrap; press COPY to share it.";
            var descSize = WrapSize(desc, iw - 16f - MeasureSpaced("VIEW LOG", _fCaption, 1.2f) - 16f - 12f);
            var colH = _largeLineH + 3f + descSize.Height;
            var pillW = 16f + MeasureSpaced("VIEW LOG", _fCaption, 1.2f) + 16f;
            var pillH = 10f + _captionLineH + 10f;
            var pillRect = new RectangleF(x + iw - pillW, rowTop + (rowH - pillH) / 2f, pillW, pillH);
            ops.Add(go =>
            {
                using (var br = new SolidBrush(DeltaTorTheme.Text))
                    DrawUtil.DrawSpaced(go, "View connection log", _fLarge, br,
                        x, rowTop + (rowH - colH) / 2f, iw - pillW - 12f, 0.3f);
                DrawWrapped(go, desc, DeltaTorTheme.Muted, x,
                    rowTop + (rowH - colH) / 2f + _largeLineH + 3f, descSize);
                using (var path = DrawUtil.RoundedRect(pillRect, 12f))
                using (var br = new System.Drawing.Drawing2D.LinearGradientBrush(
                    pillRect, DeltaTorTheme.AccentDark, DeltaTorTheme.Accent, 0f))
                    go.FillPath(br, path);
                using (var br = new SolidBrush(Color.White))
                    DrawUtil.DrawSpacedCentered(go, "VIEW LOG", _fCaption, br, pillRect, 1.2f);
                _hits.Add((pillRect, () => OpenLog?.Invoke(), true));
            });
            cy = rowTop + rowH;

            ops.Add(go =>
            {
                using var div = new SolidBrush(Color.FromArgb(153, DeltaTorTheme.BorderLight));
                go.FillRectangle(div, x, cy, iw, 1f);
            });
            cy += 1f + 10f;

            var recDesc = _loggingOn
                ? "Keeps every Tor line of every connect. Turn it off to stop " +
                  "recording; the reason for a failure still shows on the screen."
                : "Nothing is being recorded. The bootstrap still runs — it just " +
                  "is not kept, so a later log has nothing in it.";
            var recSize = WrapSize(recDesc, iw - 46f - 12f);
            var recColH = _largeLineH + 3f + recSize.Height;
            var swRect = new RectangleF(x + iw - 46f,
                cy + Math.Max(0f, (recColH - 26f) / 2f), 46f, 26f);
            ops.Add(go =>
            {
                using (var br = new SolidBrush(DeltaTorTheme.Text))
                    DrawUtil.DrawSpaced(go, "Record the log", _fLarge, br, x, cy, iw - 58f, 0.3f);
                DrawWrapped(go, recDesc, DeltaTorTheme.Muted, x, cy + _largeLineH + 3f, recSize);
            });
            ops.Add(go =>
            {
                _logSwitch!.SetBounds((int)swRect.X, (int)swRect.Y, 46, 26);
                _logSwitch.Visible = swRect.Bottom > _viewTop && swRect.Y < _viewBottom;
            });
            cy = cy + Math.Max(recColH, 26f) + 2f;
            return cy - (y + CardPadY);
        });

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

    /// <summary>Hide every child control when the section it belongs to is
    /// collapsed (the cards are not painted, so their ops never run).</summary>
    private void HideCardControls()
    {
        if (_customBox != null) _customBox.Visible = false;
        if (_torrcBox != null) _torrcBox.Visible = false;
        if (_proxySwitch != null) _proxySwitch.Visible = false;
        if (_logSwitch != null) _logSwitch.Visible = false;
        if (_storeSpinner != null) _storeSpinner.Visible = false;
    }

    // ---- dropdown popup -----------------------------------------------------

    private void PaintDropdown(Graphics g)
    {
        if (_drop is null) return;
        var d = _drop;
        var rowH = 26f;
        var panelH = rowH * d.Options.Count + 8f;
        var panel = new RectangleF(
            d.Trigger.X, Math.Min(d.Trigger.Bottom + 2f, Height - panelH - 60f),
            d.Trigger.Width, panelH);
        d.Panel = panel;
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
            using (var brush = new SolidBrush(
                selected ? DeltaTorTheme.AccentLight : DeltaTorTheme.Text))
                DrawUtil.DrawSpaced(g, label, selected ? _fBodyBold : _fBody, brush,
                    rect.X + 10f, rect.Y + (rowH - _bodyLineH) / 2f, rect.Width - 20f, 0.2f);
            d.Rows.Add((rect, value));
            ry += rowH;
        }
    }
}
