using DeltaTor.App.Controls;
using DeltaTor.Core.Util;

namespace DeltaTor.App;

/// <summary>
/// The connection log screen (MainActivity LogScreen, line 3866): a borderless
/// push-over form with the ScreenTopBar and COPY action, the recording-off
/// notice, the transport and severity filter chips, and the grouped, filtered
/// list painted newest-last with auto-follow at the bottom.
///
/// Engine notes (this is a from-scratch rewrite of the first port):
/// every node is measured exactly once when the log snapshot, a filter or
/// the width changes, into a flat array of content-coordinate rows; a paint
/// walks only the rows that intersect the viewport and draws them, so a
/// 4500-line log scrolls and repaints at the cost of the visible slice.
/// A per-unique-line measure cache absorbs the repetition of bootstrap
/// output. The timer flushes the producer and repaints only when the
/// snapshot reference actually moved — an idle screen paints nothing. Chip
/// rows wrap instead of scrolling sideways, so every chip is reachable under
/// the wheel and the wheel always scrolls the list.
/// </summary>
public sealed class LogForm : Form
{
    private const string SeverityOrder = "EWIDV";

    private abstract record LogNode;
    private sealed record SessionNode(int Id, string Title, int Count) : LogNode;
    private sealed record LevelHeader(char Level, int Count) : LogNode;
    private sealed record EntryNode(LogEntry Entry) : LogNode;

    /// <summary>A node pinned at content Y with its measured height.</summary>
    private readonly record struct Placed(LogNode Node, float Y, float H);

    private readonly Font _fMono = new("Consolas", 7.5f);
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 200 };

    private readonly List<(RectangleF Rect, Action Act)> _hits = new();
    private readonly Dictionary<string, float> _measure = new(StringComparer.Ordinal);

    private string? _transport;
    private char? _filter;
    private bool _copied;
    private long _copiedAt;

    private Placed[] _placed = Array.Empty<Placed>();
    private IReadOnlyList<LogEntry> _lastLines = Array.Empty<LogEntry>();
    private IReadOnlyList<LogSession> _lastSessions = Array.Empty<LogSession>();
    private Dictionary<string, int> _tCounts = new(StringComparer.Ordinal);
    private bool _lastEnabled;
    private int _lastWidth;
    private float _contentH;
    private float _viewTop;
    private float _viewBottom;
    private float _scroll;
    private bool _follow = true;

    public LogForm()
    {
        Text = "DeltaTor — Connection Log";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(450, 700);
        BackColor = DeltaTorTheme.Bg;
        KeyPreview = true;
        DoubleBuffered = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        AppLog.AddObserver();
        _tick.Tick += (_, _) => Pump();
        _tick.Start();
        Rebuild();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _tick.Stop();
        AppLog.RemoveObserver();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tick.Dispose();
            _fMono.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!IsHandleCreated || Width == _lastWidth) return;
        // Wrapped heights depend on the content width: one resize re-measures.
        _measure.Clear();
        Rebuild();
        Invalidate();
    }

    // ---- data plumbing ------------------------------------------------------

    /// <summary>Flush the producer, then repaint only when the snapshot,
    /// the enabled flag or the width actually moved.</summary>
    private void Pump()
    {
        AppLog.FlushIfDirty();
        var lines = AppLog.Lines;
        var sessions = AppLog.Sessions;
        var widthChanged = Width != _lastWidth;
        if (widthChanged || AppLog.Enabled != _lastEnabled ||
            !ReferenceEquals(lines, _lastLines) ||
            !ReferenceEquals(sessions, _lastSessions))
        {
            if (widthChanged) _measure.Clear();
            Rebuild();
            Invalidate();
            return;
        }
        if (_copied && Environment.TickCount64 - _copiedAt > 1500)
        {
            _copied = false;
            Invalidate();
        }
    }

    /// <summary>Group the snapshot and measure every node once into content
    /// coordinates; called only when something structural changed, never on
    /// a scroll or hover frame.</summary>
    private void Rebuild()
    {
        var lines = AppLog.Lines;
        var sessions = AppLog.Sessions;
        var nodes = GroupLog(lines, sessions, _filter, _transport);
        var contentW = Math.Max(1f, Width - 40f);

        var placed = new List<Placed>(nodes.Count);
        var y = 0f;
        foreach (var node in nodes)
        {
            var h = node switch
            {
                SessionNode => 12f + (6f + DeltaTorTheme.Caption.Height + 6f) + 4f,
                LevelHeader => 10f + (5f + DeltaTorTheme.Caption.Height + 5f) + 4f,
                EntryNode e => Measure(e.Entry.Raw, contentW) + 3f,
                _ => 0f
            };
            placed.Add(new Placed(node, y, h));
            y += h;
        }
        _placed = placed.ToArray();
        _contentH = y + 18f; // trailing spacer
        _lastLines = lines;
        _lastSessions = sessions;
        _lastEnabled = AppLog.Enabled;
        _lastWidth = Width;
        _tCounts = TransportCounts(lines);
        _scroll = _follow ? MaxScroll : Math.Clamp(_scroll, 0f, MaxScroll);
    }

    /// <summary>Wrapped height of one raw line, cached: bootstrap output
    /// repeats the same shapes thousands of times.</summary>
    private float Measure(string raw, float w)
    {
        if (_measure.TryGetValue(raw, out var h)) return h;
        h = TextRenderer.MeasureText(raw, _fMono,
            new Size(Math.Max(1, (int)w), int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
        // Bound the cache so pathological churn cannot grow it forever.
        if (_measure.Count > 8000) _measure.Clear();
        _measure[raw] = h;
        return h;
    }

    // ---- input --------------------------------------------------------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var viewH = _viewBottom - _viewTop;
        switch (e.KeyCode)
        {
            case Keys.Escape:
                Close();
                e.Handled = true;
                break;
            case Keys.Down:
                ScrollBy(36f);
                e.Handled = true;
                break;
            case Keys.Up:
                ScrollBy(-36f);
                e.Handled = true;
                break;
            case Keys.PageDown:
                ScrollBy(viewH * 0.9f);
                e.Handled = true;
                break;
            case Keys.PageUp:
                ScrollBy(-viewH * 0.9f);
                e.Handled = true;
                break;
            case Keys.Home:
                _follow = false;
                ScrollTo(0f);
                e.Handled = true;
                break;
            case Keys.End:
                _follow = true;
                ScrollTo(MaxScroll);
                e.Handled = true;
                break;
        }
        if (e.Handled) e.SuppressKeyPress = true;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        var next = Math.Clamp(_scroll - e.Delta / 120f * 54f, 0f, MaxScroll);
        // At the bottom again the tail is followed once more.
        _follow = next >= MaxScroll - 2f;
        if (next == _scroll) return;
        _scroll = next;
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

    private float MaxScroll => Math.Max(0f, _contentH - (_viewBottom - _viewTop));

    private void ScrollBy(float delta)
    {
        if (delta < 0f) _follow = false;
        ScrollTo(_scroll + delta);
    }

    private void ScrollTo(float target)
    {
        var next = Math.Clamp(target, 0f, MaxScroll);
        if (next == _scroll) return;
        _scroll = next;
        Invalidate();
    }

    // ---- painting -----------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        _hits.Clear();

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

        var copyW = 16f + DrawUtil.SpacedWidth(_copied ? "COPIED" : "COPY",
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
            var notice = _lastLines.Count == 0
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

        // ---- Transport chips (wrapped rows; no sideways scroll to hunt).
        y += 6f;
        var tChips = new List<(string Label, string Key)> { ("ALL", "") };
        foreach (var t in AppLog.Transports)
        {
            var n = _tCounts.TryGetValue(t, out var c) ? c : 0;
            tChips.Add((n > 0 ? t.ToUpperInvariant() + $" ({n})" : t.ToUpperInvariant(), t));
        }
        y = DrawChipRow(g, y, tChips, key =>
        {
            _transport = key.Length == 0 || _transport == key ? null : key;
            _follow = true;
            Rebuild();
            Invalidate();
        }, _transport ?? "", 14f, 7f);

        // ---- Severity chips (wrapped rows).
        y += 6f;
        var fChips = new List<(string Label, string Key)> { ("ALL", "") };
        foreach (var level in SeverityOrder) fChips.Add((LevelLabel(level), level.ToString()));
        y = DrawChipRow(g, y, fChips, key =>
        {
            _filter = key.Length == 0 || _filter?.ToString() == key ? null : key[0];
            _follow = true;
            Rebuild();
            Invalidate();
        }, _filter?.ToString() ?? "", 13f, 6f);

        // ---- The list.
        y += 6f;
        _viewTop = y;
        _viewBottom = Height - 20f;

        if (_placed.Length == 0)
        {
            var msg = !AppLog.Enabled
                ? "Logging is off. Turn it on under ADVANCED · CONNECTION LOG."
                : _lastLines.Count == 0
                    ? "No log lines captured yet. Start a connection."
                    : _transport != null
                        ? $"No {_transport.ToUpperInvariant()} lines captured yet."
                        : "No entries for this level.";
            using (var br = new SolidBrush(DeltaTorTheme.Muted))
                DrawUtil.DrawSpacedCentered(g, msg, DeltaTorTheme.Body, br,
                    new RectangleF(20f, _viewTop, w - 40f, _viewBottom - _viewTop), 0.4f);
            return;
        }

        if (_follow) _scroll = MaxScroll;
        else _scroll = Math.Clamp(_scroll, 0f, MaxScroll);

        var clip = g.Save();
        g.SetClip(new RectangleF(0f, _viewTop, w, _viewBottom - _viewTop));
        var contentW = w - 40f;
        foreach (var p in _placed)
        {
            var sy = p.Y - _scroll + _viewTop;
            if (sy + p.H < _viewTop) continue;
            if (sy > _viewBottom) break; // the array is ordered by Y
            PaintPlaced(g, p, sy, contentW);
        }
        g.Restore(clip);

        PaintScrollbar(g);
    }

    private void CopyLog()
    {
        var text = string.Join("\n",
            _placed.Select(p => p.Node).OfType<EntryNode>().Select(r => r.Entry.Raw));
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

    /// <summary>TransportChip / SeverityChip (MainActivity 3720): pills laid
    /// out left to right, wrapping onto the next line when the row fills, so
    /// every chip is reachable under the wheel with no sideways scroll.</summary>
    private float DrawChipRow(
        Graphics g, float y, IReadOnlyList<(string Label, string Key)> chips,
        Action<string> pick, string selectedKey, float padH, float padV)
    {
        var w = Width;
        var h = padV * 2f + DeltaTorTheme.Caption.Height;
        var x = 20f;
        var rowY = y + 6f;
        foreach (var (label, key) in chips)
        {
            var cw = padH * 2f + DrawUtil.SpacedWidth(label, DeltaTorTheme.Caption, 1.1f);
            if (x > 20f && x + cw > w - 20f)
            {
                x = 20f;
                rowY += h + 6f;
            }
            var rect = new RectangleF(x, rowY, cw, h);
            var selected = key == selectedKey;
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
            var captured = key;
            _hits.Add((rect, () => pick(captured)));
            x += cw + 8f;
        }
        return rowY + h + 6f;
    }

    /// <summary>Thin rounded thumb on the right edge while the list overflows.</summary>
    private void PaintScrollbar(Graphics g)
    {
        var max = MaxScroll;
        if (max <= 1f) return;
        var viewH = _viewBottom - _viewTop;
        if (viewH <= 0f || _contentH <= 0f) return;
        var thumbH = Math.Max(28f, viewH * viewH / _contentH);
        var t = Math.Clamp(_scroll / max, 0f, 1f);
        var y = _viewTop + 3f + t * (viewH - 6f - thumbH);
        var rect = new RectangleF(Width - 7f, y, 3f, thumbH);
        using var path = DrawUtil.RoundedRect(rect, 1.5f);
        using var fill = new SolidBrush(Color.FromArgb(120, DeltaTorTheme.BorderLight));
        g.FillPath(fill, path);
    }

    /// <summary>One already-measured node at screen Y; nothing here measures.</summary>
    private void PaintPlaced(Graphics g, Placed p, float sy, float w)
    {
        var x = 20f;
        switch (p.Node)
        {
            case SessionNode s:
            {
                // Outer pad top12 bottom4 + inner row pad h10 v6.
                var y = sy + 12f;
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
                var idW = DrawUtil.SpacedWidth(idText, DeltaTorTheme.Caption, 1.6f);
                using (var br = new SolidBrush(DeltaTorTheme.AccentLight))
                    DrawUtil.DrawSpaced(g, idText, DeltaTorTheme.Caption, br,
                        tx, y + 6f, idW + 2f, 1.6f);
                tx += idW + 8f;

                var countText = $"· {s.Count} lines";
                var countW = DrawUtil.SpacedWidth(countText, DeltaTorTheme.Caption, 0.4f);
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
                break;
            }
            case LevelHeader lh:
            {
                // Outer pad top10 bottom4 + inner row pad h10 v5.
                var y = sy + 10f;
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
                var levelW = DrawUtil.SpacedWidth(levelText, DeltaTorTheme.Caption, 1.6f);
                using (var br = new SolidBrush(DeltaTorTheme.Text))
                    DrawUtil.DrawSpaced(g, levelText, DeltaTorTheme.Caption, br,
                        tx, y + 5f, levelW + 2f, 1.6f);
                tx += levelW + 8f;
                using (var br = new SolidBrush(DeltaTorTheme.Muted))
                    DrawUtil.DrawSpaced(g, $"· {lh.Count}", DeltaTorTheme.Caption, br,
                        tx, y + 5f, 60f, 0.4f);
                break;
            }
            case EntryNode en:
                TextRenderer.DrawText(g, en.Entry.Raw, _fMono,
                    new Rectangle((int)x, (int)sy, (int)w, (int)p.H + 6),
                    DeltaTorTheme.Muted,
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                break;
        }
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
