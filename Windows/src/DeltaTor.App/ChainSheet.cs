using System.Drawing.Drawing2D;
using DeltaTor.App.Controls;
using DeltaTor.Core;

namespace DeltaTor.App;

/// <summary>
/// The VPN Gate server sheet (MainActivity ProxyChainSheet, line 2139): the
/// app's modal bottom sheet, anchored flush to the bottom of the main window.
/// It carries the header (title, list summary, the bare TEST trigger), a
/// scrollable list grouped by the country sections the host file declared
/// (each group keeps the file's order, fastest first), the fetch states in the
/// space the list will occupy, and per-row millisecond results that fill in as
/// the reachability probes answer.
///
/// Owner-drawn end to end: no child labels, because a transparent Label inside
/// a scrolled container is a known WinForms combination that silently fails to
/// paint. Only the list region scrolls; the header is fixed above it.
/// </summary>
public sealed class ChainSheet : Form
{
    // Android's ModalBottomSheet + the sheet's own Column.
    private const int Pad = 20;
    private const int HandleY = 8;
    private const int HandleW = 36;
    private const int HandleH = 4;
    private const int HeaderY = 22;
    private const int ListHeight = 380;
    private const int BottomPad = 24;
    private const int MaxWidth = 480;
    private const int MinWidth = 360;

    /// <summary>Set when a row is picked; the caller dials this config.</summary>
    public string PickedHost { get; private set; } = "";
    public int PickedPort { get; private set; }

    private readonly Font _titleFont =
        new(DeltaTorTheme.FontFamilyName, 10f, FontStyle.Bold);
    private readonly Font _subFont =
        new(DeltaTorTheme.FontFamilyName, DeltaTorTheme.SmallPt, FontStyle.Regular);
    private readonly Font _testFont =
        new(DeltaTorTheme.FontFamilyName, DeltaTorTheme.SmallPt, FontStyle.Bold);
    private readonly Font _nameFont =
        new(DeltaTorTheme.FontFamilyName, DeltaTorTheme.BodyPt, FontStyle.Regular);
    private readonly Font _capFont =
        new(DeltaTorTheme.FontFamilyName, DeltaTorTheme.SmallPt, FontStyle.Regular);
    private readonly Font _msFont =
        new(DeltaTorTheme.FontFamilyName, DeltaTorTheme.BodyPt, FontStyle.Bold);
    private readonly Font _groupFont =
        new(DeltaTorTheme.FontFamilyName, DeltaTorTheme.H2Pt, FontStyle.Bold);

    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 250 };

    private readonly int _titleH;
    private readonly int _subH;
    private readonly int _headerH;
    private int _w = 440;
    private int _listTop;

    private float _scroll;
    private float _contentH;

    // Layout, rebuilt on every tick: the sheet is opened for a moment and the
    // probe results land while it is up.
    private readonly List<Node> _nodes = new();
    private RectangleF _testRect;
    private RectangleF _retryRect;
    private RectangleF _closeRect;

    private sealed record Node(float Y, float H, string Kind, ProxyChainManager.Server? Server, int Count);

    public ChainSheet()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(0xFF, 0x14, 0x17, 0x1F); // ModalBottomSheet containerColor
        DoubleBuffered = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        _titleH = TextRenderer.MeasureText("A", _titleFont).Height;
        _subH = TextRenderer.MeasureText("A", _subFont).Height;
        _headerH = _titleH + 4 + _subH;

        _tick.Tick += (_, _) =>
        {
            Rebuild();
            Invalidate();
        };
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var owner = Owner;
        _w = owner is null
            ? 440
            : Math.Clamp(owner.ClientSize.Width - 24, MinWidth, MaxWidth);
        Width = _w;
        _listTop = HeaderY + _headerH + 10;
        Height = _listTop + ListHeight + BottomPad;

        if (owner is not null)
        {
            var client = owner.RectangleToScreen(owner.ClientRectangle);
            Location = new Point(
                client.Left + (client.Width - Width) / 2,
                client.Bottom - Height);
        }

        using (var path = DrawUtil.RoundedRect(
            new RectangleF(0f, 0f, Width, Height), 22f))
            Region = new Region(path);

        // An empty list means no fetch has landed yet (first run, or the
        // background one failed): kick one off here instead of leaving the
        // user with an empty sheet.
        if (ProxyChainManager.Servers.Count == 0)
            _ = Task.Run(ProxyChainManager.UpdateNowAsync);

        Rebuild();
        _tick.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        _tick.Stop();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tick.Dispose();
            _titleFont.Dispose();
            _subFont.Dispose();
            _testFont.Dispose();
            _nameFont.Dispose();
            _capFont.Dispose();
            _msFont.Dispose();
            _groupFont.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---- layout -------------------------------------------------------------

    private float ListBottom => _listTop + ListHeight;

    private void Rebuild()
    {
        _nodes.Clear();
        var servers = ProxyChainManager.Servers;
        if (servers.Count == 0)
        {
            _contentH = 0f;
            _scroll = 0f;
            return;
        }

        var y = 0f;
        foreach (var group in servers.GroupBy(s => s.CountryCode))
        {
            var count = group.Count();
            _nodes.Add(new Node(y, GroupHeaderHeight(), "country", group.First(), count));
            y += GroupHeaderHeight();
            foreach (var server in group)
            {
                _nodes.Add(new Node(y, RowHeight(), "server", server, 0));
                y += RowHeight();
            }
        }
        _contentH = y;
        _scroll = Math.Clamp(_scroll, 0f, Math.Max(0f, _contentH - ListHeight));
    }

    private static float GroupHeaderHeight() =>
        12f + TextRenderer.MeasureText("A", DeltaTorTheme.H2).Height + 4f;

    private static float RowHeight() =>
        10f + TextRenderer.MeasureText("A", DeltaTorTheme.Body).Height + 2f +
        TextRenderer.MeasureText("A", DeltaTorTheme.Small).Height + 10f;

    // ---- input --------------------------------------------------------------

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        var max = Math.Max(0f, _contentH - ListHeight);
        if (max <= 0f) return;
        _scroll = Math.Clamp(_scroll - e.Delta / 120f * 60f, 0f, max);
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;

        if (_closeRect.Contains(e.Location))
        {
            DialogResult = DialogResult.Cancel;
            Close();
            return;
        }

        if (_testRect.Contains(e.Location))
        {
            if (!ProxyChainManager.Testing && ProxyChainManager.Servers.Count > 0)
            {
                _ = Task.Run(ProxyChainManager.TestAllAsync);
                Invalidate();
            }
            return;
        }

        if (_retryRect.Width > 0 && _retryRect.Contains(e.Location))
        {
            _ = Task.Run(ProxyChainManager.UpdateNowAsync);
            Invalidate();
            return;
        }

        // Scroll-region hits are content coordinates; convert once here.
        if (e.Y < _listTop || e.Y >= ListBottom) return;
        var cy = e.Y - _listTop + _scroll;
        foreach (var node in _nodes)
        {
            if (node.Kind != "server" || node.Server is null) continue;
            if (cy < node.Y || cy >= node.Y + node.H) continue;
            PickedHost = node.Server.Host;
            PickedPort = node.Server.Port;
            DialogResult = DialogResult.OK;
            Close();
            return;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode != Keys.Escape) return;
        DialogResult = DialogResult.Cancel;
        Close();
    }

    // ---- painting -----------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using (var path = DrawUtil.RoundedRect(rect, 22f))
        {
            using var fill = new SolidBrush(BackColor);
            g.FillPath(fill, path);
            using var pen = new Pen(DeltaTorTheme.BorderLight);
            g.DrawPath(pen, path);
        }

        // Drag handle (ModalBottomSheet's handle): a short rounded bar.
        using (var path = DrawUtil.RoundedRect(
            new RectangleF((Width - HandleW) / 2f, HandleY, HandleW, HandleH), HandleH / 2f))
        {
            using var fill = new SolidBrush(Color.FromArgb(0x44, DeltaTorTheme.BorderLight));
            g.FillPath(fill, path);
        }

        DrawHeader(g);
        DrawBody(g);
    }

    private void DrawHeader(Graphics g)
    {
        var servers = ProxyChainManager.Servers;

        // Close affordance (the Windows stand-in for a swipe-down): a small ✕
        // at the top-right, on the handle row.
        _closeRect = new RectangleF(Width - Pad - 16f, 2f, 16f, 16f);
        Icons.Close(g, _closeRect, DeltaTorTheme.Muted);

        using (var brush = new SolidBrush(DeltaTorTheme.Text))
            DrawUtil.DrawSpaced(g, "PROXY CHAIN", _titleFont, brush,
                Pad, HeaderY, Width - Pad * 2f - 60f, 1.6f);

        var subtitle = servers.Count == 0
            ? "list updates automatically, once a day"
            : ProxyChainManager.Countries.Count == 0
                ? $"{servers.Count} servers \u00b7 fastest on top"
                : $"{servers.Count} servers \u00b7 {ProxyChainManager.Countries.Count} countries" +
                  " \u00b7 fastest first in each";
        using (var brush = new SolidBrush(DeltaTorTheme.Muted))
            DrawUtil.DrawSpaced(g, subtitle, _subFont, brush,
                Pad, HeaderY + _titleH + 4f, Width - Pad * 2f, 0f);

        // The bare TEST trigger: its word alone, in the accent, dimmed while a
        // probe is already running.
        var testing = ProxyChainManager.Testing;
        var test = testing ? "TESTING\u2026" : "TEST";
        var testColor = testing
            ? DeltaTorTheme.Amber
            : servers.Count > 0 ? DeltaTorTheme.Accent : DeltaTorTheme.Muted;
        var testW = DrawUtil.SpacedWidth(test, _testFont, 1.2f);
        var testY = HeaderY + (_headerH - _subH) / 2f;
        using (var brush = new SolidBrush(testColor))
            DrawUtil.DrawSpaced(g, test, _testFont, brush,
                Width - Pad - testW, testY, testW + 2f, 1.2f);
        _testRect = new RectangleF(Width - Pad - testW - 8f, testY - 6f, testW + 16f, 24f);
    }

    private void DrawBody(Graphics g)
    {
        var saved = g.Save();
        g.SetClip(new Rectangle(0, _listTop, Width, ListHeight));

        if (ProxyChainManager.Servers.Count == 0)
        {
            DrawEmpty(g);
            g.Restore(saved);
            return;
        }

        g.TranslateTransform(0f, _listTop - _scroll);
        foreach (var node in _nodes)
        {
            if (node.Server is null) continue;
            if (node.Kind == "country")
                DrawGroup(g, node.Y, node.Server.CountryCode, node.Server.CountryName, node.Count);
            else
                DrawRow(g, node.Y, node.Server);
        }
        g.Restore(saved);
    }

    private void DrawEmpty(Graphics g)
    {
        var cx = Width / 2f;
        var cy = _listTop + ListHeight / 2f;
        _retryRect = RectangleF.Empty;
        if (ProxyChainManager.Fetching)
        {
            DrawSpinner(g, new PointF(cx, cy - 20f), 14f, DeltaTorTheme.Accent);
            TextRenderer.DrawText(g, "در حال دریافت سرورها\u2026",
                _nameFont, new Rectangle(0, (int)cy + 4, Width, 24),
                DeltaTorTheme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            return;
        }

        TextRenderer.DrawText(g, "دریافت لیست سرورها ناموفق بود",
            _nameFont, new Rectangle(0, (int)cy - 18, Width, 24),
            DeltaTorTheme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);

        var retry = "تلاش دوباره";
        var retryW = DrawUtil.SpacedWidth(retry, _testFont, 1.2f);
        var retryY = cy + 8f;
        using (var brush = new SolidBrush(DeltaTorTheme.Accent))
            DrawUtil.DrawSpaced(g, retry, _testFont, brush,
                cx - retryW / 2f, retryY, retryW + 2f, 1.2f);
        _retryRect = new RectangleF(cx - retryW / 2f - 6f, retryY - 4f, retryW + 12f, 24f);
    }

    private static void DrawSpinner(Graphics g, PointF c, float r, Color color)
    {
        var rot = (Environment.TickCount64 % 1000) / 1000f * 360f;
        using var pen = new Pen(color, 2f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        g.DrawArc(pen, c.X - r, c.Y - r, r * 2f, r * 2f, rot, 300f);
    }

    private void DrawGroup(Graphics g, float y, string code, string name, int count)
    {
        // The section names come straight from the host file, so any country
        // the generator adds tomorrow renders here with no code change.
        var title = name.Length > 0 ? name : code.Length > 0 ? code : "unknown";
        var line = TextRenderer.MeasureText("A", _groupFont).Height;
        var x = (float)Pad;
        var text = $"{title} \u00b7 {count}";
        if (code.Length == 2)
        {
            var flagY = y + 12f + (line - 15f) / 2f;
            if (UiHelpers.DrawFlag(g, code, x, flagY, 16f))
            {
                x += 16f + 6f;
            }
            else
            {
                // No image for the code: lead with the emoji instead.
                text = $"{UiHelpers.FlagEmoji(code)} {text}";
            }
        }
        using var brush = new SolidBrush(DeltaTorTheme.Text);
        DrawUtil.DrawSpaced(g, text, _groupFont, brush, x, y + 12f, Width - x - Pad, 0f);
    }

    private void DrawRow(Graphics g, float y, ProxyChainManager.Server server)
    {
        var nameLine = TextRenderer.MeasureText("A", _nameFont).Height;
        var x = (float)Pad;

        using (var brush = new SolidBrush(DeltaTorTheme.Text))
            DrawUtil.DrawSpaced(g, server.Host, _nameFont, brush,
                x, y + 10f, Width - Pad * 2f - 70f, 0f);
        using (var brush = new SolidBrush(DeltaTorTheme.Muted))
            DrawUtil.DrawSpaced(g, $"port {server.Port}", _capFont, brush,
                x, y + 10f + nameLine + 2f, Width - Pad * 2f - 70f, 0f);

        var running = AppState.Chain.TestTotal >= 0;
        var text = server.Tested
            ? $"{server.DelayMs} ms"
            : running ? "\u2026" : "\u2014";
        var color = server.Tested ? DeltaTorTheme.Green : DeltaTorTheme.Muted;
        var w = DrawUtil.SpacedWidth(text, _msFont, 0f);
        var msLine = TextRenderer.MeasureText("A", _msFont).Height;
        using (var brush = new SolidBrush(color))
            DrawUtil.DrawSpaced(g, text, _msFont, brush,
                Width - Pad - w, y + 10f + (nameLine - msLine) / 2f, w + 2f, 0f);
    }
}
