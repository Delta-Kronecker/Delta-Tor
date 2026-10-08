using DeltaTor.App.Controls;
using DeltaTor.Core.Util;

namespace DeltaTor.App;

/// <summary>
/// The connection log screen (MainActivity LogScreen, line 3866): a borderless
/// push-over form with the ScreenTopBar and COPY action, the recording-off
/// notice, the transport and severity filter chips, and the grouped, filtered
/// list painted newest-last with auto-follow at the bottom. The window is a
/// fixed 420x894 (1080x2300, the Android phone ratio) and the chip rows scroll
/// sideways under the wheel, matching Android's horizontalScroll.
/// </summary>
public sealed class LogForm : Form
{
    private const string SeverityOrder = "EWIDV";

    private abstract record LogNode;
    private sealed record SessionNode(int Id, string Title, int Count) : LogNode;
    private sealed record LevelHeader(char Level, int Count) : LogNode;
    private sealed record EntryNode(LogEntry Entry) : LogNode;

    private readonly Font _fMono = new("Consolas", 7.5f);
    private readonly System.Windows.Forms.Timer _flush = new() { Interval = 250 };

    private readonly List<(RectangleF Rect, Action Act)> _hits = new();

    private string? _transport;
    private char? _filter;
    private bool _copied;
    private long _copiedAt;
    private int _scroll;
    private float _contentH;
    private float _viewH;
    private bool _follow = true;

    // Chip-row horizontal scroll (horizontalScroll on each Row) and the
    // bands the wheel hijack applies to.
    private float _tScroll;
    private float _fScroll;
    private RectangleF _tBand;
    private RectangleF _fBand;

    public LogForm()
    {
        Text = "DeltaTor — Connection Log";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(420, 894);
        BackColor = DeltaTorTheme.Bg;
        KeyPreview = true;
        DoubleBuffered = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        AppLog.AddObserver();
        _flush.Tick += (_, _) =>
        {
            AppLog.FlushIfDirty();
            Invalidate();
        };
        _flush.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _flush.Stop();
        AppLog.RemoveObserver();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _flush.Dispose();
            _fMono.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        // Over a chip row the wheel scrolls that row sideways, the way Android
        // scrolls it under a horizontal drag; anywhere else it is list scroll.
        var side = e.Delta / 120 * 40;
        if (_tBand.Contains(e.Location))
        {
            _tScroll = Math.Max(0f, _tScroll - side);
            Invalidate();
            return;
        }
        if (_fBand.Contains(e.Location))
        {
            _fScroll = Math.Max(0f, _fScroll - side);
            Invalidate();
            return;
        }
        _scroll = Math.Max(0, _scroll - e.Delta / 120 * 60);
        // Follow the tail again once the user scrolls back to the bottom.
        _follow = _scroll >= Math.Max(0f, _contentH - _viewH) - 2f;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        foreach (var (rect, act) in _hits)
        {
            if (!rect.Contains(e.Location)) continue;
            act();
            return;
        }
    }

    // ---- painting -----------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        _hits.Clear();
        if (_copied && Environment.TickCount64 - _copiedAt > 1500) _copied = false;

        // Screen background: vertical #1B2030 → Bg → #0C0E15.
        using (var bg = new System.Drawing.Drawing2D.LinearGradientBrush(
            ClientRectangle, Color.White, Color.White, 90f))
        {
            bg.InterpolationColors = new System.Drawing.Drawing2D.ColorBlend(3)
            {
                Positions = new[] { 0f, 0.5f, 1f },
                Colors = new[]
                {
                    Color.FromArgb(0xFF, 0x1B, 0x20, 0x30),
                    DeltaTorTheme.Bg,
                    Color.FromArgb(0xFF, 0x0C, 0x0E, 0x15)
                }
            };
            g.FillRectangle(bg, ClientRectangle);
        }

        var w = Width;
        var lines = AppLog.Lines;
        var sessions = AppLog.Sessions;

        // ---- ScreenTopBar (MainActivity 2607): back box, title, COPY pill.
        var back = new RectangleF(20f, 16f, 38f, 38f);
        using (var path = DrawUtil.RoundedRect(back, 12f))
        {
            using var fill = new SolidBrush(DeltaTorTheme.Surface);
            g.FillPath(fill, path);
            using var pen = new Pen(DeltaTorTheme.BorderLight);
            g.DrawPath(pen, path);
        }
        Icons.Back(g, new RectangleF(
            back.X + (38f - 18f) / 2f, back.Y + (38f - 18f) / 2f, 18f, 18f),
            DeltaTorTheme.Text);
        _hits.Add((back, Close));

        using (var brush = new SolidBrush(DeltaTorTheme.Text))
            DrawUtil.DrawSpaced(g, "CONNECTION LOG", DeltaTorTheme.Title, brush,
                back.Right + 14f, 16f + (38f - DeltaTorTheme.Title.Height) / 2f, 400f, 1.6f);

        var copyW = 16f + DrawUtil.SpacedWidth(g, _copied ? "COPIED" : "COPY",
            DeltaTorTheme.Caption, 1.2f) + 16f;
        var copyH = 8f + DeltaTorTheme.Caption.Height + 8f;
        var copyRect = new RectangleF(w - 20f - copyW, 16f + (38f - copyH) / 2f, copyW, copyH);
        using (var path = DrawUtil.RoundedRect(copyRect, 12f))
        using (var fill = new System.Drawing.Drawing2D.LinearGradientBrush(
            copyRect,
            _copied ? DeltaTorTheme.GreenDark : DeltaTorTheme.AccentDark,
            _copied ? DeltaTorTheme.Green : DeltaTorTheme.Accent, 0f))
            g.FillPath(fill, path);
        using (var brush = new SolidBrush(Color.White))
            DrawUtil.DrawSpacedCentered(g, _copied ? "COPIED" : "COPY",
                DeltaTorTheme.Caption, brush, copyRect, 1.2f);
        _hits.Add((copyRect, CopyLog));

        var y = 16f + 38f + 16f;
        using (var div = new SolidBrush(DeltaTorTheme.Border))
            g.FillRectangle(div, 0f, y, w, 1f);
        y += 1f;

        // ---- Recording-off notice (Spacer4 + Row pad h20 v6).
        y += 4f;
        if (!AppLog.Enabled)
        {
            var notice = lines.Count == 0
                ? "Recording is off · nothing here yet"
                : "Recording is off · this log stops here";
            var rowY = y + 6f;
            using (var path = DrawUtil.RoundedRect(
                new RectangleF(20f, rowY + (DeltaTorTheme.Small.Height - 6f) / 2f, 6f, 6f), 3f))
            using (var br = new SolidBrush(DeltaTorTheme.Muted))
                g.FillPath(br, path);
            using (var br = new SolidBrush(DeltaTorTheme.Muted))
                DrawUtil.DrawSpaced(g, notice, DeltaTorTheme.Small, br,
                    20f + 6f + 8f, rowY, w - 40f, 0.3f);
            y += 6f + DeltaTorTheme.Small.Height + 6f;
        }

        // ---- Transport chips (Spacer6 + Row pad h20, gap 8; horizontalScroll).
        y += 6f;
        var counts = TransportCounts(lines);
        var chipY = y + 6f;
        var tLabels = new List<string> { "ALL" };
        foreach (var t in AppLog.Transports)
            tLabels.Add(counts.TryGetValue(t, out var n) && n > 0
                ? t.ToUpperInvariant() + $" ({n})"
                : t.ToUpperInvariant());
        _tScroll = Math.Clamp(_tScroll, 0f,
            Math.Max(0f, RowContentW(g, tLabels, 14f) - (w - 20f)));
        _tBand = new RectangleF(0f, chipY - 4f, w, 14f + DeltaTorTheme.Caption.Height + 8f);
        var cx = 20f - _tScroll;
        DrawChip(g, cx, chipY, tLabels[0], _transport == null,
            () => { _transport = null; Invalidate(); }, 14f, 7f, ref cx);
        for (var i = 0; i < AppLog.Transports.Count; i++)
        {
            var captured = AppLog.Transports[i];
            DrawChip(g, cx, chipY, tLabels[i + 1], _transport == captured,
                () => { _transport = _transport == captured ? null : captured; Invalidate(); },
                14f, 7f, ref cx);
        }
        y = chipY + 7f + DeltaTorTheme.Caption.Height + 7f;

        // ---- Severity chips (Spacer6 + Row pad h20, gap 8; horizontalScroll).
        y += 6f;
        chipY = y + 6f;
        var fLabels = new List<string> { "ALL" };
        foreach (var level in SeverityOrder) fLabels.Add(LevelLabel(level));
        _fScroll = Math.Clamp(_fScroll, 0f,
            Math.Max(0f, RowContentW(g, fLabels, 13f) - (w - 20f)));
        _fBand = new RectangleF(0f, chipY - 4f, w, 12f + DeltaTorTheme.Caption.Height + 8f);
        cx = 20f - _fScroll;
        DrawChip(g, cx, chipY, fLabels[0], _filter == null,
            () => { _filter = null; Invalidate(); }, 13f, 6f, ref cx);
        for (var i = 0; i < SeverityOrder.Length; i++)
        {
            var captured = SeverityOrder[i];
            DrawChip(g, cx, chipY, fLabels[i + 1], _filter == captured,
                () => { _filter = captured; Invalidate(); },
                13f, 6f, ref cx);
        }
        y = chipY + 6f + DeltaTorTheme.Caption.Height + 6f;

        // ---- The list (weight(1), pad h20, trailing spacer 18).
        y += 6f;
        var viewTop = y;
        var viewBottom = Height - 20f;
        _viewH = Math.Max(0f, viewBottom - viewTop);

        var nodes = GroupLog(lines, sessions, _filter, _transport);

        if (nodes.Count == 0)
        {
            var msg = !AppLog.Enabled
                ? "Logging is off. Turn it on under ADVANCED · CONNECTION LOG."
                : lines.Count == 0
                    ? "No log lines captured yet. Start a connection."
                    : _transport != null
                        ? $"No {_transport.ToUpperInvariant()} lines captured yet."
                        : "No entries for this level.";
            using (var br = new SolidBrush(DeltaTorTheme.Muted))
                DrawUtil.DrawSpacedCentered(g, msg, DeltaTorTheme.Body, br,
                    new RectangleF(20f, viewTop, w - 40f, _viewH), 0.4f);
            _contentH = 0f;
            return;
        }

        // Follow the tail: content grows downward, so pin to the bottom while
        // the user has not scrolled away from it.
        var maxScroll = Math.Max(0f, ExpectedContentH(g, nodes, w - 40f) - _viewH);
        if (_follow) _scroll = (int)maxScroll;
        else if (_scroll > maxScroll) _scroll = (int)maxScroll;

        var state = g.Save();
        g.SetClip(new RectangleF(0f, viewTop, w, _viewH));
        var ly = viewTop - _scroll;
        foreach (var node in nodes)
            ly = PaintNode(g, node, ly, w - 40f, viewTop);
        ly += 18f; // trailing spacer
        _contentH = ly - (viewTop - _scroll);
        g.Restore(state);
    }

    private void CopyLog()
    {
        var text = string.Join("\n",
            GroupLog(AppLog.Lines, AppLog.Sessions, _filter, _transport)
                .OfType<EntryNode>()
                .Select(r => r.Entry.Raw));
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            // Another app held the clipboard; the copy is best-effort.
        }
        _copied = true;
        _copiedAt = Environment.TickCount64;
        Invalidate();
    }

    /// <summary>TransportChip / SeverityChip (MainActivity 3720): pill,
    /// selected = SurfaceLight + Text.</summary>
    private void DrawChip(Graphics g, float x, float y, string label, bool selected,
        Action onClick, float padH, float padV, ref float cursorX)
    {
        var textW = DrawUtil.SpacedWidth(g, label, DeltaTorTheme.Caption, 1.1f);
        var h = padV * 2f + DeltaTorTheme.Caption.Height;
        var rect = new RectangleF(x, y, padH * 2f + textW, h);
        using (var path = DrawUtil.RoundedRect(rect, h / 2f))
        {
            using var fill = new SolidBrush(
                selected ? DeltaTorTheme.SurfaceLight : DeltaTorTheme.Surface);
            g.FillPath(fill, path);
            using var pen = new Pen(
                selected ? DeltaTorTheme.BorderLight : DeltaTorTheme.Border);
            g.DrawPath(pen, path);
        }
        using (var br = new SolidBrush(selected ? DeltaTorTheme.Text : DeltaTorTheme.Muted))
            DrawUtil.DrawSpacedCentered(g, label, DeltaTorTheme.Caption, br, rect, 1.1f);
        _hits.Add((rect, onClick));
        cursorX = rect.Right + 8f;
    }

    /// <summary>Right edge of a chip row laid out at scroll 0, for clamping
    /// the horizontalScroll offset when the row is wider than the window.</summary>
    private static float RowContentW(Graphics g, IReadOnlyList<string> labels, float padH)
    {
        var x = 20f;
        foreach (var label in labels)
            x += padH * 2f + DrawUtil.SpacedWidth(g, label, DeltaTorTheme.Caption, 1.1f) + 8f;
        return x - 8f;
    }

    // ---- list nodes ---------------------------------------------------------

    private float PaintNode(Graphics g, LogNode node, float y, float w, float viewTop)
    {
        var x = 20f;
        switch (node)
        {
            case SessionNode s:
            {
                // Outer pad top12 bottom4 + inner row pad h10 v6.
                y += 12f;
                var h = 6f + DeltaTorTheme.Caption.Height + 6f;
                var rect = new RectangleF(x, y, w, h);
                using (var path = DrawUtil.RoundedRect(rect, 10f))
                using (var fill = new SolidBrush(Color.FromArgb(41, DeltaTorTheme.Accent))) // 0.16
                    g.FillPath(fill, path);

                using (var path = DrawUtil.RoundedRect(
                    new RectangleF(x + 10f, y + (h - 8f) / 2f, 8f, 8f), 4f))
                using (var br = new SolidBrush(DeltaTorTheme.AccentLight))
                    g.FillPath(br, path);

                var tx = x + 10f + 8f + 8f;
                var idText = $"CONNECTION #{s.Id}";
                var idW = DrawUtil.SpacedWidth(g, idText, DeltaTorTheme.Caption, 1.6f);
                using (var br = new SolidBrush(DeltaTorTheme.AccentLight))
                    DrawUtil.DrawSpaced(g, idText, DeltaTorTheme.Caption, br, tx, y + 6f, idW + 2f, 1.6f);
                tx += idW + 8f;

                var countText = $"· {s.Count} lines";
                var countW = DrawUtil.SpacedWidth(g, countText, DeltaTorTheme.Caption, 0.4f);
                if (s.Title.Length > 0)
                {
                    var titleMax = Math.Max(20f, w - 20f - (tx - x) - countW - 16f);
                    using (var br = new SolidBrush(DeltaTorTheme.Text))
                        DrawUtil.DrawSpaced(g, s.Title, DeltaTorTheme.Caption, br,
                            tx, y + 6f, titleMax, 0.4f);
                }
                using (var br = new SolidBrush(DeltaTorTheme.Muted))
                    DrawUtil.DrawSpaced(g, countText, DeltaTorTheme.Caption, br,
                        x + w - 10f - countW, y + 6f, countW + 2f, 0.4f);
                return y + h + 4f;
            }
            case LevelHeader lh:
            {
                // Outer pad top10 bottom4 + inner row pad h10 v5.
                y += 10f;
                var h = 5f + DeltaTorTheme.Caption.Height + 5f;
                var rect = new RectangleF(x, y, w, h);
                using (var path = DrawUtil.RoundedRect(rect, 10f))
                using (var fill = new SolidBrush(DeltaTorTheme.Surface))
                    g.FillPath(fill, path);

                using (var path = DrawUtil.RoundedRect(
                    new RectangleF(x + 10f, y + (h - 8f) / 2f, 8f, 8f), 4f))
                using (var br = new SolidBrush(DeltaTorTheme.Muted))
                    g.FillPath(br, path);

                var tx = x + 10f + 8f + 8f;
                var levelText = LevelLabel(lh.Level);
                var levelW = DrawUtil.SpacedWidth(g, levelText, DeltaTorTheme.Caption, 1.6f);
                using (var br = new SolidBrush(DeltaTorTheme.Text))
                    DrawUtil.DrawSpaced(g, levelText, DeltaTorTheme.Caption, br,
                        tx, y + 5f, levelW + 2f, 1.6f);
                tx += levelW + 8f;
                using (var br = new SolidBrush(DeltaTorTheme.Muted))
                    DrawUtil.DrawSpaced(g, $"· {lh.Count}", DeltaTorTheme.Caption, br,
                        tx, y + 5f, 60f, 0.4f);
                return y + h + 4f;
            }
            case EntryNode en:
            {
                var size = g.MeasureString(en.Entry.Raw, _fMono, (int)w);
                using (var br = new SolidBrush(DeltaTorTheme.Muted))
                    g.DrawString(en.Entry.Raw, _fMono, br,
                        new RectangleF(x, y, w, size.Height + 4f));
                return y + size.Height + 3f;
            }
            default:
                return y;
        }
    }

    /// <summary>Layout height of every node without drawing (auto-follow needs
    /// the content height before the paint pass runs).</summary>
    private float ExpectedContentH(Graphics g, IReadOnlyList<LogNode> nodes, float contentW)
    {
        var h = 0f;
        foreach (var node in nodes)
        {
            h += node switch
            {
                SessionNode => 12f + (6f + DeltaTorTheme.Caption.Height + 6f) + 4f,
                LevelHeader => 10f + (5f + DeltaTorTheme.Caption.Height + 5f) + 4f,
                EntryNode e => g.MeasureString(e.Entry.Raw, _fMono, (int)contentW).Height + 3f,
                _ => 0f
            };
        }
        return h + 18f;
    }

    // ---- grouping (groupLog, MainActivity 3597) -----------------------------

    private static List<LogNode> GroupLog(
        IReadOnlyList<LogEntry> lines, IReadOnlyList<LogSession> sessions,
        char? filter, string? transport)
    {
        var order = new List<int>();
        var bySession = new Dictionary<int, Dictionary<char, List<LogEntry>>>();
        foreach (var entry in lines)
        {
            if (transport != null && entry.Transport != transport) continue;
            if (!bySession.TryGetValue(entry.Session, out var levels))
            {
                levels = new Dictionary<char, List<LogEntry>>();
                bySession[entry.Session] = levels;
                order.Add(entry.Session);
            }
            if (!levels.TryGetValue(entry.Level, out var list))
            {
                list = new List<LogEntry>();
                levels[entry.Level] = list;
            }
            list.Add(entry);
        }

        var outp = new List<LogNode>();
        if (order.Count == 0) return outp;

        void AddSections(Dictionary<char, List<LogEntry>> levels, int sid)
        {
            _ = sid;
            foreach (var level in SeverityOrder)
            {
                if (filter is { } f && f != level) continue;
                if (!levels.TryGetValue(level, out var sel)) continue;
                outp.Add(new LevelHeader(level, sel.Count));
                foreach (var entry in sel) outp.Add(new EntryNode(entry));
            }
        }

        if (order.All(id => id == 0))
        {
            AddSections(bySession[0], 0);
            return outp;
        }

        foreach (var sid in order)
        {
            var levels = bySession[sid];
            if (filter is { } f && !levels.ContainsKey(f)) continue;
            var meta = sessions.FirstOrDefault(s => s.Id == sid);
            var count = levels.Values.Sum(l => l.Count);
            var title = string.Join(" · ",
                new[] { meta?.Label, meta?.Outcome }
                    .Where(v => !string.IsNullOrEmpty(v)));
            if (title.Length == 0) title = sid == 0 ? "startup" : $"session {sid}";
            outp.Add(new SessionNode(sid, title, count));
            AddSections(levels, sid);
        }
        return outp;
    }

    private static Dictionary<string, int> TransportCounts(IReadOnlyList<LogEntry> lines)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in lines)
        {
            if (entry.Transport is not { } t) continue;
            counts.TryGetValue(t, out var n);
            counts[t] = n + 1;
        }
        return counts;
    }

    private static string LevelLabel(char level) => level switch
    {
        'E' => "ERRORS",
        'W' => "WARNINGS",
        'I' => "INFO",
        'D' => "DEBUG",
        _ => "VERBOSE"
    };
}
