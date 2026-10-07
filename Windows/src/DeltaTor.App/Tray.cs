using DeltaTor.App.Controls;
using DeltaTor.Core;

namespace DeltaTor.App;

/// <summary>
/// The system tray surface — the Windows counterpart of Android's foreground
/// notification (PLAN 2.4): tray icon with the live notification tooltip, the
/// three context-sensitive engine actions from TorVpnService.withStateActions
/// (line 1124: Disconnect always; Stop VPN while connected; Start VPN when the
/// core is up and proxy mode is off), and release toasts that open the release
/// page.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _open;
    private readonly ToolStripMenuItem _disconnect;
    private readonly ToolStripMenuItem _stop;
    private readonly ToolStripMenuItem _start;
    private readonly Form _owner;
    private string _releaseUrl = "";

    public event Action? DisconnectRequested;
    public event Action? StopVpnRequested;
    public event Action? StartVpnRequested;

    public TrayIcon(Form owner)
    {
        _owner = owner;

        _open = Item("Open DeltaTor", null, ShowOwner);
        _disconnect = Item("Disconnect", GlyphPower, () => DisconnectRequested?.Invoke());
        _stop = Item("Stop VPN", GlyphSquare, () => StopVpnRequested?.Invoke());
        _start = Item("Start VPN", GlyphTriangle, () => StartVpnRequested?.Invoke());
        var exit = Item("Exit", null, () => _owner.Close());

        _menu.Items.Add(_open);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_disconnect);
        _menu.Items.Add(_stop);
        _menu.Items.Add(_start);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(exit);

        _icon = new NotifyIcon
        {
            Text = "DeltaTor",
            Visible = true,
            ContextMenuStrip = _menu,
            Icon = LoadAppIcon() ?? SystemIcons.Application
        };
        _icon.Click += (_, e) =>
        {
            if (e is MouseEventArgs m && m.Button == MouseButtons.Left) ShowOwner();
        };
        _icon.BalloonTipClicked += (_, _) =>
        {
            if (_releaseUrl.Length > 0) ReleaseChecker.OpenInBrowser(_releaseUrl);
        };
    }

    /// <summary>Multi-size DeltaTor.ico next to the exe (assets/app, shipped by
    /// the csproj); null when the asset was not copied.</summary>
    public static Icon? LoadAppIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "DeltaTor.ico");
            return File.Exists(path) ? new Icon(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Menu visibility from the state (withStateActions): the two VPN
    /// actions are exclusive halves of one engine.</summary>
    public void UpdateMenu(VpnState s)
    {
        _stop.Visible = s.Connected;
        _start.Visible = !s.Connected && s.TorRunning && !Config.ProxyOnlyMode;
    }

    /// <summary>
    /// Engine notification → tooltip. Android's notification is ongoing, low
    /// priority and onlyAlertOnce: it updates in place and never pops, so
    /// neither does this.
    /// </summary>
    public void SetNotification(EngineNotification? n)
    {
        _icon.Text = Truncate(n == null || n.Body.Length == 0
            ? n?.Title ?? "DeltaTor"
            : $"{n.Title} — {n.Body}");
    }

    /// <summary>The release notice (notification id 42) as a toast; the click
    /// opens the release page, same as the in-app banner.</summary>
    public void ShowRelease(ReleaseChecker.ReleaseNotice notice)
    {
        _releaseUrl = notice.Url;
        _icon.BalloonTipTitle = notice.Title;
        _icon.BalloonTipText = notice.Body;
        _icon.BalloonTipIcon = BalloonTipIcon.Info;
        _icon.ShowBalloonTip(5_000);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }

    private void ShowOwner()
    {
        if (_owner.IsDisposed) return;
        _owner.Show();
        _owner.WindowState = FormWindowState.Normal;
        _owner.BringToFront();
        _owner.Activate();
    }

    private ToolStripMenuItem Item(string label, Bitmap? glyph, Action onClick)
    {
        var item = new ToolStripMenuItem(label);
        if (glyph != null) item.Image = glyph;
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>NotifyIcon.Text is capped (63 chars on the classic shell) and
    /// tooltip lines are single-line.</summary>
    private static string Truncate(string text)
    {
        var flat = text.Replace('\n', ' ').Replace('\r', ' ');
        return flat.Length <= 63 ? flat : flat[..62] + "…";
    }

    // ---- action glyphs (PLAN: hollow power / square / triangle, monochrome
    // so the shell style decides the color; menu images take MenuText).

    private static Bitmap GlyphPower()
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var pen = new Pen(SystemColors.MenuText, 1.8f)
        { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
        // Circle with the gap at the top (GDI+: 0° is 3 o'clock, +cw).
        g.DrawArc(pen, new RectangleF(3f, 3.5f, 10f, 10f), 300f, 300f);
        g.DrawLine(pen, 8f, 2.2f, 8f, 7.5f);
        return bmp;
    }

    private static Bitmap GlyphSquare()
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        using var br = new SolidBrush(SystemColors.MenuText);
        using var path = DrawUtil.RoundedRect(new RectangleF(4f, 4f, 8f, 8f), 2f);
        g.FillPath(br, path);
        return bmp;
    }

    private static Bitmap GlyphTriangle()
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        using var br = new SolidBrush(SystemColors.MenuText);
        g.FillPolygon(br, new[] { new PointF(5f, 3.5f), new PointF(13f, 8f), new PointF(5f, 12.5f) });
        return bmp;
    }
}
