using DeltaTor.Core;

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

    /// <summary>Runs the deferred draw ops after the card box is behind them.</summary>
    private float RunCard(Graphics g, float y, float w,
        Func<float, float, List<Action<Graphics>>, float> layout)
    {
        var ops = new List<Action<Graphics>>();
        var innerX = CardMargin + CardPadX;
        var innerW = w - CardMargin * 2f - CardPadX * 2f;
        var contentH = layout(innerX, innerW, ops);
        CardBg(g, CardMargin, y, w - CardMargin * 2f, contentH + CardPadY * 2f);
        foreach (var op in ops) op(g);
        return y + contentH + CardPadY * 2f + 20f;
    }

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
