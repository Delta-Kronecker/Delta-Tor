// DeltaTorUi.cs - the WinForms front end (compiled together with start-tor.cs).
// DeltaTor Core License v1.0 (see LICENSE). Using this Core in another program
// requires the mandatory attribution of https://github.com/Delta-Kronecker/DeltaTor.
// One borderless, fully owner-drawn surface: no native controls are placed on
// the window, so the dark theme renders exactly the same everywhere. The
// console machinery of Program is reused behind the scenes; its output goes
// into an internal ring buffer that nothing displays yet.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace StartTor
{
    internal static partial class Program
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetConsoleWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ReleaseCapture();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 0x2;

        private static void HideOwnConsoleWindow()
        {
            try
            {
                IntPtr h = GetConsoleWindow();
                if (h != IntPtr.Zero) ShowWindow(h, 0);
            }
            catch { }
        }

        // Console output produced by the reused machinery lands in a capped
        // in-memory buffer. Nothing renders it today; future builds can.
        private static readonly object uiLogLock = new object();
        private static readonly List<string> uiLogBuffer = new List<string>();
        private const int UiLogCap = 600;
        // set while an auto race is running (drives the RACE n% status)
        internal static volatile bool uiRaceActive;
        // mode that won the last race — reconnects skip re-racing
        internal static int lastWinnerMode = -1;

        private static void AppendUiLogLine(string line)
        {
            if (line == null) return;
            lock (uiLogLock)
            {
                uiLogBuffer.Add(line);
                if (uiLogBuffer.Count > UiLogCap)
                    uiLogBuffer.RemoveRange(0, uiLogBuffer.Count - UiLogCap);
            }
        }

        // The exact DeltaTor.ico travels INSIDE the exe (embedded resource), so
        // the tray, taskbar and Alt-Tab all show the real icon without any
        // external file. Falls back to the exe icon, then a drawn placeholder.
        internal static Icon LoadAppIcon()
        {
            try
            {
                System.Reflection.Assembly a = System.Reflection.Assembly.GetExecutingAssembly();
                using (Stream s = a.GetManifestResourceStream("DeltaTor.ico"))
                {
                    if (s != null) return new Icon(s);
                }
            }
            catch { }
            try
            {
                System.Reflection.Assembly a = System.Reflection.Assembly.GetExecutingAssembly();
                return Icon.ExtractAssociatedIcon(a.Location);
            }
            catch { }
            return null;
        }

        private static void RunGui()
        {
            try
            {
                UiTextWriter ui = new UiTextWriter(AppendUiLogLine);
                Console.SetOut(ui);
                Console.SetError(ui);
            }
            catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                try
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "deltator-ui-crash.txt"),
                        DateTime.Now + "\r\n" + e.Exception);
                }
                catch { }
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                try
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "deltator-ui-crash.txt"),
                        DateTime.Now + "\r\n" + e.ExceptionObject);
                }
                catch { }
            };
            Application.Run(new MainForm());
            try { Cleanup(); } catch { }
            Environment.Exit(0);
        }

        internal sealed class UiTextWriter : TextWriter
        {
            private readonly Action<string> sink;
            private readonly StringBuilder pending = new StringBuilder();
            public UiTextWriter(Action<string> sink) { this.sink = sink; }
            public override Encoding Encoding { get { return Encoding.UTF8; } }
            public override void Write(char value)
            {
                lock (pending)
                {
                    if (value == '\n')
                    {
                        string s = pending.ToString().TrimEnd();
                        pending.Length = 0;
                        if (s.Length > 0) sink(s);
                    }
                    else pending.Append(value);
                }
            }
            public override void Write(string value)
            {
                if (value == null) return;
                lock (pending)
                {
                    pending.Append(value);
                    DrainLines();
                }
            }
            public override void WriteLine(string value)
            {
                lock (pending)
                {
                    pending.Append(value == null ? "" : value);
                    string s = pending.ToString().TrimEnd();
                    pending.Length = 0;
                    if (s.Length > 0) sink(s);
                }
            }
            private void DrainLines()
            {
                int nl = FindNl();
                while (nl >= 0)
                {
                    string s = pending.ToString(0, nl).TrimEnd();
                    pending.Remove(0, nl + 1);
                    if (s.Length > 0) sink(s);
                    nl = FindNl();
                }
            }
            private int FindNl()
            {
                for (int i = 0; i < pending.Length; i++)
                    if (pending[i] == '\n') return i;
                return -1;
            }
        }

        // ---- palette / typography -------------------------------------------
        internal static class Theme
        {
            internal static readonly Color Bg = Color.FromArgb(18, 20, 28);
            internal static readonly Color Surface = Color.FromArgb(26, 29, 38);
            internal static readonly Color SurfaceAlt = Color.FromArgb(35, 39, 50);
            internal static readonly Color SurfaceLight = Color.FromArgb(44, 49, 62);
            internal static readonly Color Border = Color.FromArgb(45, 50, 65);
            internal static readonly Color BorderLight = Color.FromArgb(60, 66, 82);
            internal static readonly Color Text = Color.FromArgb(245, 247, 252);
            internal static readonly Color Muted = Color.FromArgb(120, 130, 155);
            internal static readonly Color Accent = Color.FromArgb(138, 92, 246);
            internal static readonly Color AccentSoft = Color.FromArgb(96, 64, 190);
            internal static readonly Color AccentDark = Color.FromArgb(72, 48, 160);
            internal static readonly Color Green = Color.FromArgb(52, 211, 153);
            internal static readonly Color GreenDark = Color.FromArgb(38, 160, 118);
            internal static readonly Color Red = Color.FromArgb(239, 92, 112);
            internal static readonly Color Amber = Color.FromArgb(245, 178, 60);
            internal const string FontName = "Segoe UI";
            internal static Font Title() { return new Font(FontName, 11f, FontStyle.Bold); }
            internal static Font Big() { return new Font(FontName, 18f, FontStyle.Bold); }
            internal static Font H2() { return new Font(FontName, 9.5f, FontStyle.Bold); }
            internal static Font Body() { return new Font(FontName, 9.25f, FontStyle.Regular); }
            internal static Font Small() { return new Font(FontName, 8f, FontStyle.Regular); }
            internal static Font Caption() { return new Font(FontName, 7.25f, FontStyle.Bold); }

            internal static GraphicsPath RoundRect(Rectangle r, int radius)
            {
                if (r.Width <= 0 || r.Height <= 0) return new GraphicsPath();
                int maxR = Math.Max(1, Math.Min(r.Width, r.Height) / 2);
                if (radius < 1) radius = 1;
                if (radius > maxR) radius = maxR;
                int d = radius * 2;
                GraphicsPath p = new GraphicsPath();
                p.AddArc(r.X, r.Y, d, d, 180, 90);
                p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                p.CloseFigure();
                return p;
            }

            internal static void FillGradient(Graphics g, Rectangle r, Color top, Color bottom)
            {
                if (r.Width <= 0 || r.Height <= 0) return;
                using (LinearGradientBrush br = new LinearGradientBrush(
                    new Point(r.X, r.Y), new Point(r.X, r.Bottom), top, bottom))
                    g.FillRectangle(br, r);
            }

            internal static void FillGradientPath(Graphics g, GraphicsPath path, Color top, Color bottom)
            {
                RectangleF boundsF = path.GetBounds();
                if (boundsF.Width < 1 || boundsF.Height < 1) return;
                Rectangle bounds = Rectangle.Round(boundsF);
                using (LinearGradientBrush br = new LinearGradientBrush(
                    new Point(bounds.X, bounds.Y), new Point(bounds.X, bounds.Bottom), top, bottom))
                    g.FillPath(br, path);
            }

            internal static void DrawGlow(Graphics g, Rectangle center, Color color, int spread, int alpha)
            {
                for (int i = spread; i >= 1; i--)
                {
                    int a = (int)(alpha * (1.0 - (double)i / (spread + 1)));
                    if (a < 1) continue;
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(a, color)))
                    {
                        Rectangle r = new Rectangle(center.X - i, center.Y - i,
                            center.Width + i * 2, center.Height + i * 2);
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.FillEllipse(b, r);
                    }
                }
            }

            internal static void DrawShadow(Graphics g, GraphicsPath path, int offset, int alpha)
            {
                using (GraphicsPath shadow = (GraphicsPath)path.Clone())
                {
                    Matrix m = new Matrix(1, 0, 0, 1, offset, offset);
                    shadow.Transform(m);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0)))
                        g.FillPath(b, shadow);
                }
            }

            internal static void Pill(Graphics g, Rectangle r, Color fill, Color border)
            {
                using (GraphicsPath path = RoundRect(r, r.Height / 2))
                {
                    using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, path);
                    using (Pen pen = new Pen(border, 1f)) g.DrawPath(pen, path);
                }
            }

            internal static void PillGradient(Graphics g, Rectangle r, Color top, Color bottom, Color border)
            {
                using (GraphicsPath path = RoundRect(r, r.Height / 2))
                {
                    FillGradientPath(g, path, top, bottom);
                    using (Pen pen = new Pen(border, 1f)) g.DrawPath(pen, path);
                }
            }

            internal static void DrawTextShadow(Graphics g, string text, Font font, Rectangle r,
                Color textColor, Color shadowColor, TextFormatFlags flags, int ox, int oy)
            {
                TextRenderer.DrawText(g, text, font,
                    new Rectangle(r.X + ox, r.Y + oy, r.Width, r.Height), shadowColor, flags);
                TextRenderer.DrawText(g, text, font, r, textColor, flags);
            }
        }

        // ---- main form -------------------------------------------------------
        private sealed class MainForm : Form
        {
            private enum RunState { Idle, Connecting, Connected, Restarting, Stopping }
            private enum Page { Main, Settings }

            private RunState state = RunState.Idle;
            private Page page = Page.Main;
            private readonly System.Windows.Forms.Timer uiTimer = new System.Windows.Forms.Timer();

            private int bootPct;
            private string bootTag = "";
            private DateTime bootPctSince = DateTime.MinValue;
            private bool fallbackPending;    // set when the UI learned a fallback restart is coming
            private bool uiCanFallback;      // whether this session has a healthy/fallback split to widen to
            private string errorMsg = "";
            private bool errorMsgIsError = true;
            private DateTime errorMsgUntil = DateTime.MinValue;
            private int restartAttempts;
            private volatile bool sessionBusy;
            private bool showUpdateBanner;
            private string updateVersion = "";
            private DateTime nextUpdateCheck = DateTime.UtcNow.AddMinutes(2);

            private int hoverId = -1;
            private bool anyHover;

            private bool autoProxyEnabled;
            private int settingsScrollY;

            private int editRow = -1;
            private string editBuf = "";
            private bool caretOn;
            private DateTime lastCaretFlip = DateTime.MinValue;

            private System.Windows.Forms.NotifyIcon trayIcon;
            private System.Windows.Forms.ContextMenuStrip trayMenu;

            private Rectangle rcClose, rcMin,
                              rcProxy, rcSettings, rcPower, rcBack, rcUpdateBtn;
            private readonly Rectangle[] rcRowVal = new Rectangle[12];
            private readonly Rectangle[] rcRowPrev = new Rectangle[12];
            private readonly Rectangle[] rcRowNext = new Rectangle[12];
            private readonly Rectangle[] rcRowBody = new Rectangle[12];

            private static readonly string[] SettingLabels =
            {
                "Mode", "Auto proxy",
                "Strategy level", "Conflux sets", "Conflux legs", "Linked-set cap",
                "Keep-alive", "Set select", "Skip slow sets (RTT)", "Best % of sets",
                "Weak legs (top %)", "Isolate SOCKS"
            };

            public MainForm()
            {
                Text = "DeltaTor";
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.CenterScreen;
                ClientSize = new Size(400, 470);
                BackColor = Theme.Bg;
                ForeColor = Theme.Text;
                Font = Theme.Body();
                DoubleBuffered = true;
                KeyPreview = true;
                try
                {
                    Icon = System.Drawing.Icon.ExtractAssociatedIcon(
                        System.Reflection.Assembly.GetExecutingAssembly().Location);
                }
                catch { }

                string forced = Environment.GetEnvironmentVariable("DELTATOR_UI_PAGE");
                if (forced == "settings") page = Page.Settings;

                Resize += delegate { LayoutPass(); };
                Paint += OnPaintAll;
                MouseMove += OnMouseMoveAll;
                MouseDown += OnMouseDownAll;
                MouseWheel += OnMouseWheelAll;
                MouseLeave += delegate { anyHover = false; hoverId = -1; Invalidate(); };
                KeyDown += OnKeyDownAll;
                FormClosing += OnFormClosing;

                trayMenu = new System.Windows.Forms.ContextMenuStrip();
                trayMenu.Items.Add("Show", null, delegate { ShowFromTray(); });
                trayMenu.Items.Add("-");
                trayMenu.Items.Add("Exit", null, delegate { ExitFromTray(); });

                trayIcon = new System.Windows.Forms.NotifyIcon();
                trayIcon.Text = "DeltaTor";
                trayIcon.Icon = LoadAppIcon() ?? CreateTrayIcon();
                trayIcon.ContextMenuStrip = trayMenu;
                trayIcon.MouseClick += delegate(object s, MouseEventArgs e)
                {
                    if (e.Button == MouseButtons.Left) ShowFromTray();
                };

                Icon appIcon = LoadAppIcon() ?? CreateTrayIcon();
                Icon = appIcon;

                uiTimer.Interval = 250;
                uiTimer.Tick += UiTick;
                uiTimer.Start();
                LayoutPass();
                // Auto race is the shipped default: it runs unless the user
                // explicitly turned it off (auto.txt = "off") or picked a
                // concrete mode (mode.txt written by the cycler).
                string autoPref = null;
                try
                {
                    if (File.Exists(AutoPrefFile))
                        autoPref = File.ReadAllText(AutoPrefFile).Trim();
                }
                catch { }
                if (autoPref == "off")
                {
                    int lastMode = ReadLastMode();
                    uiModePos = lastMode >= 0 ? lastMode : 1;
                }
                else
                {
                    uiModePos = ModeNames.Length;   // Auto race
                }
                autoProxyEnabled = ReadAutoProxySetting();
                LogLine("DeltaTor " + DeltaTorVersion.App);
            }

            // ---- geometry --------------------------------------------------
            private void LayoutPass()
            {
                int w = ClientSize.Width;
                rcClose = new Rectangle(w - 40, 0, 40, 36);
                rcMin = new Rectangle(w - 78, 0, 38, 36);

                int cx = w / 2;
                bool showProxy = !autoProxyEnabled;
                bool showUpd = showUpdateBanner && updateVersion.Length > 0;

                // vertical flow: ring -> connect -> proxy? -> settings -> update?
                // connect text is drawn at rcPower.Bottom + 8 with height 22.
                int total = 104 + 30 + 24;              // ring + connect region + gap
                if (showProxy) total += 42 + 14;        // proxy row + gap
                total += 38;                            // settings row
                if (showUpd) total += 14 + 38;          // gap + update banner row

                int y = 78 + Math.Max(0, (ClientSize.Height - 24 - 78 - total) / 2);
                rcPower = new Rectangle(cx - 52, y, 104, 104);
                y += 104 + 30 + 24;
                int pw = Math.Max(32, w - 48);
                if (showProxy)
                {
                    rcProxy = new Rectangle(24, y, pw, 42);
                    y += 42 + 14;
                }
                else
                    rcProxy = new Rectangle(0, 0, 0, 0);
                rcSettings = new Rectangle(24, y, pw, 38);
                y += 38;
                if (showUpd)
                {
                    y += 14;
                    rcUpdateBtn = new Rectangle(24, y, pw, 38);
                }
                else
                    rcUpdateBtn = new Rectangle(0, 0, 0, 0);

                int ry = 78 - settingsScrollY;
                int rowCount = SettingLabels.Length;
                for (int i = 0; i < rowCount; i++)
                {
                    rcRowBody[i] = new Rectangle(18, ry, w - 36, 36);
                    int valW = 152;
                    rcRowVal[i] = new Rectangle(w - 18 - valW, ry + 3, valW, 30);
                    rcRowPrev[i] = new Rectangle(rcRowVal[i].Left, ry + 3, 26, 30);
                    rcRowNext[i] = new Rectangle(rcRowVal[i].Right - 26, ry + 3, 26, 30);
                    ry += 37;
                }
                rcBack = new Rectangle(14, 42, 96, 26);

                try
                {
                    using (GraphicsPath path = Theme.RoundRect(
                        new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), 12))
                    {
                        Region = new Region(path);
                    }
                }
                catch { }

                Invalidate();
            }

            // ---- painting ---------------------------------------------------
            private Color StateColor()
            {
                if (uiRaceActive) return Theme.Amber;
                switch (state)
                {
                    case RunState.Connected: return Theme.Green;
                    case RunState.Connecting: return Theme.Amber;
                    case RunState.Restarting: return Theme.Red;
                    case RunState.Stopping: return Theme.Amber;
                    default: return Theme.Muted;
                }
            }

            private string StateText()
            {
                if (uiRaceActive)
                    return bootPct > 0 ? "RACE " + bootPct + "%" : "RACING";
                switch (state)
                {
                    case RunState.Connected: return "CONNECTED";
                    case RunState.Connecting:
                        return bootPct > 0 ? "BOOTSTRAP " + bootPct + "%" : "BOOTSTRAP";
                    case RunState.Restarting:
                        return "RESTART " + restartAttempts + "/3";
                    case RunState.Stopping: return "STOPPING";
                    default: return "OFFLINE";
                }
            }

            private void OnPaintAll(object s, PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Theme.Bg);
                try
                {
                    PaintTitlebar(g);
                    if (page == Page.Main) PaintMain(g);
                    else PaintSettings(g);
                }
                catch (Exception ex)
                {
                    try
                    {
                        File.WriteAllText(Path.Combine(Path.GetTempPath(), "deltator-ui-paint.txt"),
                            DateTime.Now + "\r\n" + ex);
                    }
                    catch { }
                    lastPaintError = ex.Message;
                }
                if (lastPaintError.Length > 0)
                    TextRenderer.DrawText(g, "ui error: " + lastPaintError, Theme.Small(),
                        new Rectangle(8, ClientSize.Height - 26, ClientSize.Width - 16, 22),
                        Theme.Red, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.EndEllipsis);
            }

            private string lastPaintError = "";

            private void PaintTitlebar(Graphics g)
            {
                Theme.FillGradient(g, new Rectangle(0, 0, ClientSize.Width, 36),
                    Theme.SurfaceAlt, Theme.Surface);

                Color sc = StateColor();
                Theme.DrawGlow(g, new Rectangle(11, 10, 14, 14), sc, 6, 40);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush b = new SolidBrush(sc))
                    g.FillEllipse(b, 14, 13, 8, 8);

                TextRenderer.DrawText(g, "DeltaTor", Theme.Title(),
                    new Rectangle(30, 0, 120, 36), Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

                string ver = DeltaTorVersion.App;
                TextRenderer.DrawText(g, "v" + ver, Theme.Small(),
                    new Rectangle(ClientSize.Width - 180, 0, 90, 36), Theme.Muted,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

                bool hovClose = hoverId == 1;
                bool hovMin = hoverId == 2;
                if (hovClose)
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(50, Theme.Red)))
                        g.FillRectangle(b, rcClose);
                if (hovMin)
                    Theme.FillGradient(g, rcMin, Theme.SurfaceAlt, Theme.SurfaceLight);

                using (Pen pen = new Pen(Theme.Border, 1f))
                    g.DrawLine(pen, 0, 35, ClientSize.Width, 35);

                int midY = 18;
                using (Pen p = new Pen(hovMin ? Theme.Text : Theme.Muted, 1.8f))
                {
                    p.StartCap = LineCap.Round;
                    p.EndCap = LineCap.Round;
                    g.DrawLine(p, rcMin.Left + 14, midY, rcMin.Right - 14, midY);
                }
                using (Pen p = new Pen(hovClose ? Theme.Text : Theme.Muted, 1.8f))
                {
                    p.StartCap = LineCap.Round;
                    p.EndCap = LineCap.Round;
                    g.DrawLine(p, rcClose.Left + 14, midY - 4, rcClose.Right - 14, midY + 4);
                    g.DrawLine(p, rcClose.Left + 14, midY + 4, rcClose.Right - 14, midY - 4);
                }
            }

            private void PaintMain(Graphics g)
            {
                string stateTxt = StateText();
                Color stateCol = StateColor();
                Theme.DrawTextShadow(g, stateTxt, Theme.Big(),
                    new Rectangle(0, 58, ClientSize.Width, 36), stateCol,
                    Color.FromArgb(60, 0, 0, 0),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter,
                    1, 2);

                if (state == RunState.Connecting && !uiRaceActive)
                    PaintBootstrapLine(g);

                Rectangle ring = rcPower;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                Color ringColor, glowColor;
                if (state == RunState.Connecting && bootPct > 0)
                {
                    ringColor = Theme.Accent;
                    glowColor = Theme.Accent;
                }
                else
                {
                    switch (state)
                    {
                        case RunState.Connected: ringColor = Theme.Green; glowColor = Theme.Green; break;
                        case RunState.Restarting: ringColor = Theme.Red; glowColor = Theme.Red; break;
                        case RunState.Stopping: ringColor = Theme.Amber; glowColor = Theme.Amber; break;
                        default: ringColor = Theme.BorderLight; glowColor = Color.Transparent; break;
                    }
                }

                if (glowColor != Color.Transparent)
                    Theme.DrawGlow(g,
                        new Rectangle(ring.X - 4, ring.Y - 4, ring.Width + 8, ring.Height + 8),
                        glowColor, 10, 25);

                using (GraphicsPath ringPath = Theme.RoundRect(
                    new Rectangle(ring.X - 1, ring.Y - 1, ring.Width + 2, ring.Height + 2),
                    ring.Width / 2))
                {
                    Theme.DrawShadow(g, ringPath, 3, 40);
                }

                if (state == RunState.Connecting && bootPct > 0)
                {
                    using (Pen bgPen = new Pen(Theme.Border, 5f))
                    {
                        bgPen.StartCap = LineCap.Round;
                        bgPen.EndCap = LineCap.Round;
                        g.DrawEllipse(bgPen, ring);
                    }
                    float sweep = 360f * Math.Min(100, bootPct) / 100f;
                    if (sweep >= 1f)
                    {
                        using (Pen p = new Pen(Theme.Accent, 5f))
                        {
                            p.StartCap = LineCap.Round;
                            p.EndCap = LineCap.Round;
                            g.DrawArc(p, ring, -90, sweep);
                        }
                    }
                }
                else
                {
                    using (LinearGradientBrush ringBrush = new LinearGradientBrush(
                        new Point(ring.X, ring.Y), new Point(ring.X, ring.Bottom),
                        Color.FromArgb(255, ringColor), Color.FromArgb(180, ringColor)))
                    using (Pen p = new Pen(ringBrush, 5f))
                    {
                        p.StartCap = LineCap.Round;
                        p.EndCap = LineCap.Round;
                        g.DrawEllipse(p, ring);
                    }
                }

                Color glyph = state == RunState.Idle ? Theme.Text : StateColor();
                int gd = rcPower.Width - 44;
                Rectangle arc = new Rectangle(rcPower.Left + 22, rcPower.Top + 22, gd, gd);
                using (Pen p = new Pen(glyph, 5f))
                {
                    p.StartCap = LineCap.Round;
                    p.EndCap = LineCap.Round;
                    g.DrawArc(p, arc, -60, 300);
                    g.DrawLine(p, cx(), rcPower.Top + 16, cx(), rcPower.Top + 46);
                }

                string cap = ErrorOr(state == RunState.Idle ? "CONNECT" :
                                     state == RunState.Connected ? "DISCONNECT" :
                                     state == RunState.Stopping ? "STOPPING" : "CANCEL");
                TextRenderer.DrawText(g, cap, Theme.Body(),
                    new Rectangle(0, rcPower.Bottom + 8, ClientSize.Width, 22),
                    HasError() ? (errorMsgIsError ? Theme.Red : Theme.Text) : Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis);

                if (state == RunState.Connected)
                {
                    TextRenderer.DrawText(g, "SOCKS 127.0.0.1:" + liveSocksPort +
                        "   HTTP 127.0.0.1:" + liveHttpPort +
                        "   DNS 127.0.0.1:" + liveDnsPort,
                        Theme.Small(), new Rectangle(0, rcPower.Bottom + 30, ClientSize.Width, 18),
                        Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }

                if (!autoProxyEnabled)
                    PaintTogglePill(g, rcProxy, "PROXY", ProxyIsOurs(), hoverId == 20, false);

                bool hovSet = hoverId == 30;
                Theme.PillGradient(g, rcSettings,
                    hovSet ? Theme.SurfaceLight : Theme.SurfaceAlt,
                    hovSet ? Theme.SurfaceAlt : Theme.Surface, Theme.Border);
                TextRenderer.DrawText(g, "SETTINGS", Theme.H2(),
                    new Rectangle(rcSettings.Left + 18, rcSettings.Y, rcSettings.Width - 36, rcSettings.Height),
                    Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                DrawChevron(g, new Rectangle(rcSettings.Right - 34,
                    rcSettings.Y + (rcSettings.Height - 24) / 2, 24, 24),
                    true, hovSet);

                if (showUpdateBanner && updateVersion.Length > 0)
                {
                    bool hovUpd = hoverId == 50;
                    Theme.PillGradient(g, rcUpdateBtn,
                        hovUpd ? Theme.Accent : Theme.AccentSoft,
                        hovUpd ? Theme.AccentSoft : Theme.AccentDark, Theme.Accent);
                    TextRenderer.DrawText(g, "New version v" + updateVersion,
                        Theme.H2(),
                        new Rectangle(rcUpdateBtn.Left + 14, rcUpdateBtn.Y,
                            rcUpdateBtn.Width - 40, rcUpdateBtn.Height),
                        Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                    using (SolidBrush b = new SolidBrush(hovUpd ? Color.White : Theme.Text))
                    {
                        int bx = rcUpdateBtn.Right - 28, by = rcUpdateBtn.Y + rcUpdateBtn.Height / 2;
                        using (Pen p = new Pen(b, 2.2f))
                        {
                            p.StartCap = LineCap.Round;
                            p.EndCap = LineCap.Round;
                            g.DrawLine(p, bx - 4, by - 5, bx + 2, by);
                            g.DrawLine(p, bx + 2, by, bx - 4, by + 5);
                        }
                    }
                }
            }

            private int cx() { return rcPower.Left + rcPower.Width / 2; }

            // Must match start-tor.cs StuckFallbackMinutes (2 minutes): how long a
            // frozen bootstrap percentage is tolerated before the fallback restart.
            private static readonly TimeSpan FallbackSpan = TimeSpan.FromMinutes(2.0);

            private void PaintBootstrapLine(Graphics g)
            {
                int pct = bootPct;
                string line;
                // Timer text is drawn white so it stands out on the main screen.
                Color col = Theme.Text;

                if (fallbackPending)
                {
                    line = "FALLBACK - trying all bridges";
                }
                else if (pct <= 0)
                {
                    line = "starting tor...";
                }
                else if (pct >= 100)
                {
                    line = "connected";
                }
                else if (uiCanFallback)
                {
                    TimeSpan remaining = FallbackSpan - (DateTime.UtcNow - bootPctSince);
                    int secs = (int)Math.Max(0, Math.Ceiling(remaining.TotalSeconds));
                    line = "fallback in " + secs + "s" +
                           (string.IsNullOrEmpty(bootTag) ? "" : "  " + bootTag);
                }
                else
                {
                    line = "bootstrap " + pct + "%" +
                           (string.IsNullOrEmpty(bootTag) ? "" : "  " + bootTag);
                }

                TextRenderer.DrawText(g, line, Theme.Small(),
                    new Rectangle(0, 96, ClientSize.Width, 18),
                    col, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                         TextFormatFlags.EndEllipsis);
            }

            private void PaintTogglePill(Graphics g, Rectangle r, string name, bool on,
                                         bool hovered, bool pending)
            {
                Theme.PillGradient(g, r,
                    hovered ? Theme.SurfaceLight : Theme.SurfaceAlt,
                    Theme.Surface, pending ? Theme.Amber : Theme.Border);
                TextRenderer.DrawText(g, name, Theme.H2(),
                    new Rectangle(r.Left + 16, r.Y, r.Width - 40, r.Height),
                    pending ? Theme.Amber : (on ? Theme.Text : Theme.Muted),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

                int swW = 44, swH = 22, swX = r.Right - 56, swY = r.Y + (r.Height - swH) / 2;
                Rectangle sw = new Rectangle(swX, swY, swW, swH);

                Color bg = pending ? Theme.Amber : (on ? Theme.Green : Theme.SurfaceLight);
                using (GraphicsPath bgPath = Theme.RoundRect(sw, swH / 2))
                    Theme.FillGradientPath(g, bgPath, bg, Color.FromArgb(
                        Math.Max(0, bg.A - 30), bg.R, bg.G, bg.B));

                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (on && !pending)
                    Theme.DrawGlow(g, new Rectangle(swX - 2, swY - 2, swW + 4, swH + 4),
                        Theme.Green, 4, 20);

                int knobD = 16;
                int knobX = (on && !pending) ? swX + swW - knobD - 3 : swX + 3;
                int knobY = swY + (swH - knobD) / 2;
                Rectangle knob = new Rectangle(knobX, knobY, knobD, knobD);

                using (SolidBrush b = new SolidBrush(on || pending ? Color.White : Theme.BorderLight))
                    g.FillEllipse(b, knob);

                if (!on && !pending)
                {
                    using (Pen pen = new Pen(Theme.Border, 1f))
                        g.DrawEllipse(pen, knob);
                }
            }

            private void DrawChevron(Graphics g, Rectangle r, bool pointRight, bool hovered)
            {
                Color c = hovered ? Theme.Text : Theme.Muted;
                using (Pen p = new Pen(c, 1.8f))
                {
                    p.StartCap = LineCap.Round;
                    p.EndCap = LineCap.Round;
                    int mx = r.Left + r.Width / 2;
                    int my = r.Top + r.Height / 2;
                    if (!pointRight)
                    {
                        g.DrawLine(p, mx + 3, my - 5, mx - 3, my);
                        g.DrawLine(p, mx - 3, my, mx + 3, my + 5);
                    }
                    else
                    {
                        g.DrawLine(p, mx - 3, my - 5, mx + 3, my);
                        g.DrawLine(p, mx + 3, my, mx - 3, my + 5);
                    }
                }
            }

            private void PaintSettings(Graphics g)
            {
                bool hovBack = hoverId == 40;
                TextRenderer.DrawText(g, hovBack ? "\u2039 BACK" : "\u2039 Back", Theme.Body(),
                    rcBack, hovBack ? Theme.Text : Theme.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g, "SETTINGS", Theme.H2(),
                    new Rectangle(0, 4, ClientSize.Width, 28), Theme.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

                int rowCount = SettingLabels.Length;
                int settingsTop = 78;
                int settingsBottom = ClientSize.Height - 8;
                Region prevClip = g.Clip;
                g.SetClip(new Rectangle(0, settingsTop, ClientSize.Width, settingsBottom - settingsTop));

                for (int i = 0; i < rowCount; i++)
                {
                    Rectangle body = rcRowBody[i];
                    if (body.Bottom < settingsTop || body.Y > settingsBottom) continue;
                    bool selected = editRow == i;

                    Color bgTop = i % 2 == 0 ? Theme.Surface : Theme.SurfaceAlt;
                    Color bgBot = i % 2 == 0 ? Theme.SurfaceAlt : Theme.Surface;
                    Theme.FillGradient(g, new Rectangle(body.X - 12, body.Y, body.Width + 24, body.Height), bgTop, bgBot);

                    if (selected)
                    {
                        using (SolidBrush b = new SolidBrush(Theme.Accent))
                            g.FillRectangle(b, body.X - 12, body.Y + 4, 4, body.Height - 8);
                    }

                    using (Pen pen = new Pen(Theme.Border, 1f))
                        g.DrawLine(pen, body.X - 12, body.Bottom, body.Right + 12, body.Bottom);

                    TextRenderer.DrawText(g, SettingLabels[i], Theme.Body(),
                        new Rectangle(body.Left + 8, body.Y, body.Width - 174, body.Height),
                        Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.EndEllipsis);
                    PaintSettingValue(g, i);
                }
                g.Clip = prevClip;

                int lastRow = rowCount - 1;
                if (rcRowBody[lastRow].Bottom < settingsBottom)
                {
                    TextRenderer.DrawText(g, "applies on next connect", Theme.Small(),
                        new Rectangle(0, rcRowBody[lastRow].Bottom + 8, ClientSize.Width, 18),
                        Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
                }
            }

            private void PaintSettingValue(Graphics g, int i)
            {
                Rectangle v = rcRowVal[i];
                if (i == 1 || i == 6 || i == 11)
                {
                    bool on = i == 1 ? autoProxyEnabled : (i == 6 ? keepAliveEnabled : isolateSocksAuth);
                    int swW = 44, swH = 22, swX = v.Right - 52, swY = v.Y + (v.Height - swH) / 2;
                    Rectangle sw = new Rectangle(swX, swY, swW, swH);
                    Color bg = on ? Theme.Green : Theme.SurfaceLight;
                    using (GraphicsPath bgPath = Theme.RoundRect(sw, swH / 2))
                        Theme.FillGradientPath(g, bgPath, bg, Color.FromArgb(
                            Math.Max(0, bg.A - 30), bg.R, bg.G, bg.B));
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    if (on)
                        Theme.DrawGlow(g, new Rectangle(swX - 2, swY - 2, swW + 4, swH + 4),
                            Theme.Green, 3, 15);
                    int knobD = 16;
                    int knobX = on ? swX + swW - knobD - 3 : swX + 3;
                    int knobY = swY + (swH - knobD) / 2;
                    Rectangle knob = new Rectangle(knobX, knobY, knobD, knobD);
                    using (SolidBrush b = new SolidBrush(on ? Color.White : Theme.BorderLight))
                        g.FillEllipse(b, knob);
                    TextRenderer.DrawText(g, on ? "ON" : "OFF", Theme.Caption(),
                        new Rectangle(v.Left, v.Y, v.Width - 60, v.Height), Theme.Muted,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                    return;
                }
                string text = i == 0
                    ? (uiModePos < ModeNames.Length ? PrettyMode(ModeNames[uiModePos]) : "Auto race")
                    : SettingDisplay(i);
                bool editing = editRow == i;

                Theme.PillGradient(g, v,
                    editing ? Theme.SurfaceLight : Theme.SurfaceAlt,
                    editing ? Theme.Surface : Theme.Surface,
                    editing ? Theme.Accent : Theme.Border);

                Rectangle inner = Rectangle.Inflate(v, -6, 0);
                if (editing)
                {
                    string shown = editBuf.Length == 0 ? "_" : editBuf + (caretOn ? "|" : "");
                    TextRenderer.DrawText(g, shown, Theme.Body(), inner, Theme.Text,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                else
                {
                    DrawChevron(g, rcRowPrev[i], false, hoverId == 101 + i * 3);
                    DrawChevron(g, rcRowNext[i], true, hoverId == 102 + i * 3);
                    Rectangle mid = Rectangle.FromLTRB(rcRowPrev[i].Right, v.Y, rcRowNext[i].Left, v.Bottom);
                    TextRenderer.DrawText(g, text, Theme.Body(), mid, Theme.Text,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.EndEllipsis);
                }
            }

            // ---- settings model ---------------------------------------------
            private static readonly int[] RttSteps =
            {
                0, 50, 100, 150, 200, 250, 300, 400, 500, 650, 800, 1000, 1250,
                1500, 2000, 3000, 5000, 8000, 10000
            };

            private string SettingDisplay(int i)
            {
                switch (i)
                {
                    case 2: return StrategyNames[Math.Max(0, comboStrategyIndexSafe())];
                    case 3: return confluxSets == 0 ? "consensus" : confluxSets.ToString();
                    case 4: return confluxLegs == 0 ? "consensus" : confluxLegs.ToString();
                    case 5: return confluxLinkedSets == 0 ? "consensus" : confluxLinkedSets.ToString();
                    case 7: return SetSelectionNames[Math.Max(0, Math.Min(3, confluxSelection))];
                    case 8: return confluxRttMax == 0 ? "off" : confluxRttMax + " ms";
                    case 9: return confluxRttPct == 0 ? "off" : confluxRttPct + "%";
                    case 10: return watchRttPct == 0 ? "off" : watchRttPct + "%";
                    default: return "";
                }
            }

            private void CycleSetting(int i, int dir)
            {
                switch (i)
                {
                    case 2:
                    {
                        int idx = Math.Max(0, comboStrategyIndexSafe());
                        idx = Wrap(idx + dir, StrategyNames.Length);
                        WriteStrategyFile(idx);
                        ApplyConfluxPresetForStrategy(idx);
                        break;
                    }
                    case 3: confluxSets = ClampCycle(confluxSets + dir, 0, 32); WriteConfluxSetting(ConfluxSetsFile, confluxSets); break;
                    case 4: confluxLegs = ClampCycle(confluxLegs + dir, 0, 16); WriteConfluxSetting(ConfluxLegsFile, confluxLegs); break;
                    case 5: confluxLinkedSets = ClampCycle(confluxLinkedSets + dir, 0, 32); WriteConfluxSetting(ConfluxLinkedSetsFile, confluxLinkedSets); break;
                    case 7:
                        confluxSelection = Wrap(confluxSelection + dir, SetSelectionNames.Length);
                        WriteConfluxSetting(ConfluxSelectionFile, confluxSelection);
                        break;
                    case 8: confluxRttMax = StepRtt(confluxRttMax, dir); WriteConfluxSetting(ConfluxRttMaxFile, confluxRttMax); break;
                    case 9: confluxRttPct = StepClamp(confluxRttPct, dir * 5, 0, 100); WriteConfluxSetting(ConfluxRttPctFile, confluxRttPct); break;
                    case 10: watchRttPct = StepClamp(watchRttPct, dir * 5, 0, 100); WriteConfluxSetting(WatchRttPctFile, watchRttPct); break;
                }
                Invalidate();
            }

            private static int Wrap(int v, int n) { return ((v % n) + n) % n; }
            private static int ClampCycle(int v, int lo, int hi)
            {
                if (v < lo) return hi;
                if (v > hi) return lo;
                return v;
            }
            private static int StepClamp(int cur, int delta, int lo, int hi)
            {
                int v = cur + delta;
                if (v < lo) v = lo;
                if (v > hi) v = hi;
                return v;
            }
            private static int StepRtt(int cur, int dir)
            {
                int idx = 0;
                for (int i = 0; i < RttSteps.Length; i++) if (RttSteps[i] == cur) { idx = i; break; }
                idx = idx + dir;
                if (idx < 0) idx = 0;
                if (idx >= RttSteps.Length) idx = RttSteps.Length - 1;
                return RttSteps[idx];
            }

            private bool RowIsNumeric(int i)
            {
                return i == 3 || i == 4 || i == 5 || i == 8 || i == 9 || i == 10;
            }

            private void CommitEdit()
            {
                if (editRow < 0) return;
                int i = editRow;
                int v;
                if (int.TryParse(editBuf, out v))
                {
                    switch (i)
                    {
                        case 3: confluxSets = Math.Max(0, Math.Min(32, v)); WriteConfluxSetting(ConfluxSetsFile, confluxSets); break;
                        case 4: confluxLegs = Math.Max(0, Math.Min(16, v)); WriteConfluxSetting(ConfluxLegsFile, confluxLegs); break;
                        case 5: confluxLinkedSets = Math.Max(0, Math.Min(32, v)); WriteConfluxSetting(ConfluxLinkedSetsFile, confluxLinkedSets); break;
                        case 8: confluxRttMax = Math.Max(0, Math.Min(10000, v)); WriteConfluxSetting(ConfluxRttMaxFile, confluxRttMax); break;
                        case 9: confluxRttPct = Math.Max(0, Math.Min(100, v)); WriteConfluxSetting(ConfluxRttPctFile, confluxRttPct); break;
                        case 10: watchRttPct = Math.Max(0, Math.Min(100, v)); WriteConfluxSetting(WatchRttPctFile, watchRttPct); break;
                    }
                }
                editRow = -1;
                editBuf = "";
                Invalidate();
            }

            // ---- input ------------------------------------------------------
            private void OnMouseMoveAll(object s, MouseEventArgs e)
            {
                int h = HitTest(e.Location);
                if (h != hoverId || !anyHover)
                {
                    hoverId = h;
                    anyHover = true;
                    Invalidate();
                }
                Cursor = h != -1 ? Cursors.Hand : Cursors.Default;
            }

            private void OnMouseWheelAll(object s, MouseEventArgs e)
            {
                if (page != Page.Settings) return;
                int rowCount = SettingLabels.Length;
                int contentHeight = rowCount * 37 + 40;
                int visibleHeight = ClientSize.Height - 78;
                int maxScroll = Math.Max(0, contentHeight - visibleHeight);
                int delta = e.Delta > 0 ? -37 : 37;
                int newScroll = Math.Max(0, Math.Min(maxScroll, settingsScrollY + delta));
                if (newScroll != settingsScrollY)
                {
                    settingsScrollY = newScroll;
                    LayoutPass();
                }
            }

            private void OnMouseDownAll(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                Point p = e.Location;
                int h = HitTest(p);
                if (h == -1)
                {
                    if (p.Y <= 36)
                    {
                        ReleaseCapture();
                        SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                    }
                    else if (editRow >= 0) CommitEdit();
                    return;
                }
                switch (h)
                {
                    case 1: HandleCloseRequest(); break;
                    case 2: WindowState = FormWindowState.Minimized; break;
                    case 5: OnConnectButton(); break;
                    case 20: ApplyProxyToggle(!ProxyIsOurs()); break;
                    case 30: page = Page.Settings; settingsScrollY = 0; CancelEdit(); LayoutPass(); Invalidate(); break;
                    case 50: OpenReleases(); break;
                    case 40: page = Page.Main; CancelEdit(); LayoutPass(); Invalidate(); break;
                    default:
                        if (page != Page.Settings || h < 100) break;
                        if (h >= 200 && h < 300)
                        {
                            int row = h - 200;
                            if (row == 1)
                            {
                                autoProxyEnabled = !autoProxyEnabled;
                                WriteAutoProxyFile(autoProxyEnabled);
                                if (autoProxyEnabled && state == RunState.Connected)
                                    ApplyProxyToggle(true);
                                else if (!autoProxyEnabled && ProxyIsOurs())
                                    ApplyProxyToggle(false);
                                LayoutPass();
                                Invalidate();
                            }
                            else if (row == 6)
                            {
                                keepAliveEnabled = !keepAliveEnabled;
                                WriteKeepAliveFile(keepAliveEnabled);
                                if (!keepAliveEnabled) StopKeepAlive();
                                else if (state == RunState.Connected) StartKeepAlive();
                                Invalidate();
                            }
                            else if (row == 11)
                            {
                                isolateSocksAuth = !isolateSocksAuth;
                                WriteIsolateSocksFile(isolateSocksAuth);
                                Invalidate();
                            }
                        }
                        else
                        {
                            int baseIdx = (h - 100) / 3;
                            int part = (h - 100) % 3;
                            if (baseIdx == 0) CycleMode(part == 1 ? -1 : 1);
                            else if (part == 1) CycleSetting(baseIdx, -1);
                            else if (part == 2) CycleSetting(baseIdx, 1);
                            else if (RowIsNumeric(baseIdx))
                            {
                                CommitEdit();
                                editRow = baseIdx;
                                editBuf = RawNumeric(baseIdx);
                                Invalidate();
                            }
                        }
                        break;
                }
            }

            private string RawNumeric(int i)
            {
                switch (i)
                {
                    case 3: return confluxSets.ToString();
                    case 4: return confluxLegs.ToString();
                    case 5: return confluxLinkedSets.ToString();
                    case 8: return confluxRttMax.ToString();
                    case 9: return confluxRttPct.ToString();
                    case 10: return watchRttPct.ToString();
                    default: return "";
                }
            }

            private void OnKeyDownAll(object s, KeyEventArgs e)
            {
                if (editRow >= 0)
                {
                    if (e.KeyCode == Keys.Escape) { CancelEdit(); e.Handled = true; return; }
                    if (e.KeyCode == Keys.Enter) { CommitEdit(); e.Handled = true; return; }
                    if (e.KeyCode == Keys.Back)
                    {
                        if (editBuf.Length > 0) editBuf = editBuf.Substring(0, editBuf.Length - 1);
                        Invalidate(); e.Handled = true; return;
                    }
                    if (e.KeyCode >= Keys.D0 && e.KeyCode <= Keys.D9)
                    {
                        if (editBuf.Length < 5) editBuf += (char)('0' + (e.KeyCode - Keys.D0));
                        Invalidate(); e.Handled = true; return;
                    }
                    if (e.KeyCode >= Keys.NumPad0 && e.KeyCode <= Keys.NumPad9)
                    {
                        if (editBuf.Length < 5) editBuf += (char)('0' + (e.KeyCode - Keys.NumPad0));
                        Invalidate(); e.Handled = true; return;
                    }
                    return;
                }
                if (e.KeyCode == Keys.Escape && page == Page.Settings)
                {
                    page = Page.Main; LayoutPass(); Invalidate();
                }
            }

            private void CancelEdit()
            {
                editRow = -1;
                editBuf = "";
            }

            private int HitTest(Point p)
            {
                if (rcClose.Contains(p)) return 1;
                if (rcMin.Contains(p)) return 2;
                if (page == Page.Main && rcPower.Contains(p)) return 5;
                if (page == Page.Main)
                {
                    if (!autoProxyEnabled && rcProxy.Contains(p)) return 20;
                    if (rcSettings.Contains(p)) return 30;
                    if (showUpdateBanner && updateVersion.Length > 0 &&
                        rcUpdateBtn.Contains(p)) return 50;
                }
                else
                {
                    if (rcBack.Contains(p)) return 40;
                    int rowCount = SettingLabels.Length;
                    for (int i = 0; i < rowCount; i++)
                    {
                        if (i == 1 || i == 6 || i == 11)
                        {
                            if (rcRowVal[i].Contains(p)) return 200 + i;
                            continue;
                        }
                        if (rcRowPrev[i].Contains(p)) return 100 + i * 3 + 1;
                        if (rcRowNext[i].Contains(p)) return 100 + i * 3 + 2;
                        if (RowIsNumeric(i) && rcRowVal[i].Contains(p)) return 100 + i * 3;
                    }
                }
                return -1;
            }

            // ---- mode helpers -----------------------------------------------
            private int uiModePos = 1;
            private static readonly string AutoPrefFile = Path.Combine(DataDir, "auto.txt");

            private int comboModeIndexSafe()
            {
                int m = ReadLastMode();
                return m >= 0 ? m : ParseMode("obfs4");
            }

            private int comboStrategyIndexSafe()
            {
                int st = ReadLastStrategy();
                return st >= 0 ? st : DefaultStrategy();
            }

            private void CycleMode(int dir)
            {
                uiModePos = Wrap(uiModePos + dir, ModeNames.Length + 1);
                if (uiModePos < ModeNames.Length)
                {
                    WriteModeFile(uiModePos);
                    try { File.WriteAllText(AutoPrefFile, "off", new UTF8Encoding(false)); } catch { }
                }
                else
                {
                    try { File.WriteAllText(AutoPrefFile, "on", new UTF8Encoding(false)); } catch { }
                }
                Invalidate();
            }

            private string PrettyMode(string m)
            {
                switch (m)
                {
                    case "vanilla": return "Vanilla";
                    case "obfs4": return "Obfs4";
                    case "webtunnel": return "WebTunnel";
                    case "snowflake": return "Snowflake";
                    case "memory": return "Memory";
                    default: return "Direct";
                }
            }

            // ---- session wiring ----------------------------------------------
            private delegate void SimpleAction();

            private void UiInvokeDelegate(SimpleAction d)
            {
                try { BeginInvoke(d); } catch { }
            }

            private void ApplyProxyToggle(bool want)
            {
                bool ours = ProxyIsOurs();
                if (want && !ours)
                {
                    SetSystemProxy(true);
                    FlashMessage("system proxy on", false);
                }
                else if (!want && ours)
                {
                    SetSystemProxy(false);
                    FlashMessage("system proxy off", false);
                }
                Invalidate();
            }

            private void Run(string file, string args)
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = file,
                        Arguments = args,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    Process.Start(psi);
                }
                catch { }
            }

            private void OpenReleases()
            {
                try
                {
                    Process.Start(new ProcessStartInfo(
                        "https://github.com/Delta-Kronecker/DeltaTor/releases")
                    { UseShellExecute = true });
                }
                catch { }
            }

            private void FlashMessage(string msg, bool asError = true)
            {
                errorMsg = msg;
                errorMsgIsError = asError;
                errorMsgUntil = DateTime.UtcNow.AddSeconds(6);
                Invalidate();
            }

            private bool HasError()
            {
                return errorMsg.Length > 0 && DateTime.UtcNow < errorMsgUntil;
            }

            private string ErrorOr(string fallback)
            {
                return HasError() ? errorMsg : fallback;
            }

            private void LogLine(string line)
            {
                AppendUiLogLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + line);
            }

            private void RunBg(ThreadStart work)
            {
                Thread t = new Thread(work) { IsBackground = true };
                t.Start();
            }

            // ---- connect / disconnect ------------------------------------------
            private volatile bool stoppingBusy;

            private void OnConnectButton()
            {
                if (state == RunState.Idle) Connect();
                else if (state == RunState.Stopping) return;
                else Disconnect("stopped by user");
            }

            private void Connect()
            {
                if (sessionBusy) return;
                sessionBusy = true;
                restartAttempts = 0;
                bootPct = 0;
                bootTag = "";
                bootPctSince = DateTime.UtcNow;
                fallbackPending = false;
                SetState(RunState.Connecting);
                bool race = uiModePos >= ModeNames.Length;
                int mode = race ? -1 : uiModePos;
                int strategy = comboStrategyIndexSafe();
                WriteStrategyFile(strategy);
                RunBg(delegate { SessionWorker(mode, strategy, race); });
            }
            private void SessionWorker(int mode, int strategy, bool raceStart)
            {
                if (mode == 5) // memory
                {
                    int cachedMode, cachedStrategy;
                    if (RestoreLastSuccessFull(out cachedMode, out cachedStrategy))
                    {
                        mode = cachedMode;
                        if (strategy < 0) strategy = cachedStrategy;
                        LogLine("[memory] " + ModeNames[mode] + " / " + StrategyNames[strategy]);
                        if (Directory.Exists(MemoryBackupDir))
                            LogLine("[memory] restored warm state");
                    }
                    else
                    {
                        if (!stoppingBusy)
                        {
                            sessionBusy = false;
                            UiInvokeDelegate(delegate
                            {
                                bootPct = 0;
                                SetState(RunState.Idle);
                                FlashMessage("No cached connection");
                            });
                        }
                        return;
                    }
                }
                if (raceStart)
                {
                    torProc = null;
                    uiRaceActive = true;
                    uiCanFallback = false;   // races have no single healthy/fallback timer
                    int winnerMode;
                    string raceErr;
                    bool ok = AutoRace(strategy, out winnerMode, out raceErr,
                        delegate(string line) { LogLine(line); },
                        delegate(int p, string info)
                        {
                            // Keep the single highest percentage seen so far. The
                            // race reports the best racer each tick, but which
                            // racer leads can fluctuate, so we never lower the
                            // shown value — it only ratchets upward to the peak.
                            if (p >= 0 && p > bootPct) { bootPct = p; bootTag = info; }
                            bootPctSince = DateTime.UtcNow;
                            fallbackPending = false;
                            UiInvokeDelegate(delegate { Invalidate(); });
                        });
                    uiRaceActive = false;
                    if (!ok)
                    {
                        if (!stoppingBusy && !autoAbort)
                        {
                            sessionBusy = false;
                            UiInvokeDelegate(delegate
                            {
                                bootPct = 0;
                                SetState(RunState.Idle);
                                FlashMessage(raceErr);
                            });
                        }
                        return;
                    }
                    mode = winnerMode;
                    lastWinnerMode = winnerMode;
                    if (autoLiveProc != null)
                    {
                        torProc = autoLiveProc;   // winner kept alive — skip the restart
                        LogLine("connected - " + ModeNames[mode] +
                                " | SOCKS 127.0.0.1:" + liveSocksPort +
                                " | HTTP 127.0.0.1:" + liveHttpPort +
                                " | DNS 127.0.0.1:" + liveDnsPort);
                    }
                    else
                    {
                        LogLine("[auto] " + ModeNames[mode] + " won — restarting on primary ports");
                    }
                    if (stoppingBusy)
                    {
                        // user asked to stop while the race was claiming the
                        // winner — don't bring the session up, let Disconnect
                        // drive the state back to Idle
                        if (autoLiveProc != null)
                        {
                            try { autoLiveProc.Kill(); } catch { }
                            autoLiveProc = null;
                        }
                        torProc = null;
                        return;
                    }
                }
                if (torProc == null)
                {
                    StopPreviousRun();
                    for (int i = 0; i < 30 && PreviousRunActive(); i++) Thread.Sleep(500);
                }
                string err;
                bool aborted;
                Process proc;
                if (torProc != null)
                {
                    proc = torProc;   // live race winner — already at 100%
                }
                else
                {
                    uiCanFallback = HasFallbackSection(mode);
                    proc = StartTorAndWait(mode, strategy, false, delegate(int pct, string tag)

                    {
                        if (pct >= 0)
                        {
                            bootPct = pct;
                            bootTag = tag ?? "";
                            bootPctSince = DateTime.UtcNow;
                        }
                        else if (pct == -2)
                        {
                            bootPct = 0;
                            bootTag = "fallback";
                            bootPctSince = DateTime.UtcNow;
                            fallbackPending = true;
                        }
                        else
                        {
                            bootPct = 0;
                            bootTag = tag ?? "";
                            bootPctSince = DateTime.UtcNow;
                            fallbackPending = false;
                        }
                        UiInvokeDelegate(delegate { Invalidate(); });
                    }, out err, out aborted);
                    if (proc == null)
                    {
                        if (!stoppingBusy)
                        {
                            sessionBusy = false;
                            UiInvokeDelegate(delegate
                            {
                                bootPct = 0;
                                SetState(RunState.Idle);
                            });
                        }
                        return;
                    }
                }
                circuitWatchStop = false;
                circuitWatchWarmup = true;
                if (circuitWatchEnabled)
                {
                    Thread watcher = new Thread(CircuitWatchLoop) { IsBackground = true };
                    watcher.Start();
                }
                if (keepAliveEnabled) StartKeepAlive();
                StartWatchdog();
                Thread warmupEnd = new Thread(delegate()
                {
                    Thread.Sleep(TimeSpan.FromSeconds(warmupRelaxSeconds));
                    circuitWatchWarmup = false;
                }) { IsBackground = true };
                warmupEnd.Start();

                BackupLastSuccessFull(mode, strategy);
                if (autoProxyEnabled)
                    UiInvokeDelegate(delegate { ApplyProxyToggle(true); });
                sessionBusy = false;
                UiInvokeDelegate(delegate
                {
                    bootPct = 100;
                    fallbackPending = false;
                    SetState(RunState.Connected);
                });
            }

            private void Disconnect(string why)
            {
                if (stoppingBusy) return;
                stoppingBusy = true;
                autoAbort = true;   // cancels a running race immediately
                SetState(RunState.Stopping);
                watchdogStop = true;
                circuitWatchStop = true;
                StopKeepAlive();
                if (autoProxyEnabled && ProxyIsOurs()) SetSystemProxy(false);
                try { if (torProc != null) torProc.Kill(); } catch { }
                torProc = null;
                LogLine("tor stopped (" + why + ")");
                RunBg(delegate
                {
                    Cleanup();
                    cleaned = false;
                    Thread.Sleep(5000);
                    UiInvokeDelegate(delegate
                    {
                        if (state == RunState.Stopping)
                        {
                            bootPct = 0;
                            stoppingBusy = false;
                            sessionBusy = false;
                            SetState(RunState.Idle);
                        }
                        else
                        {
                            // a newer session already took over (reconnect
                            // during the 5 s window) — release the stop flags
                            // without stomping its state or progress
                            stoppingBusy = false;
                            sessionBusy = false;
                        }
                    });
                });
            }

            private void SetState(RunState s)
            {
                state = s;
                Invalidate();
            }

            // ---- tick: death / watchdog / caret ---------------------------------
            private void UiTick(object s, EventArgs e)
            {
                // Keep the bootstrap countdown ("fallback in Xs") live while
                // connecting (single-mode only — races have no fallback) but
                // not yet at 100%.
                if (!uiRaceActive && state == RunState.Connecting &&
                    bootPct > 0 && bootPct < 100)
                    Invalidate();

                if (editRow >= 0 && (DateTime.UtcNow - lastCaretFlip).TotalMilliseconds >= 450)
                {
                    caretOn = !caretOn;
                    lastCaretFlip = DateTime.UtcNow;
                    Invalidate();
                }

                if (HasError() && DateTime.UtcNow >= errorMsgUntil)
                {
                    errorMsg = "";
                    Invalidate();
                }

                if (!showUpdateBanner && DateTime.UtcNow >= nextUpdateCheck)
                {
                    nextUpdateCheck = DateTime.UtcNow.AddMinutes(5);
                    RunBg(delegate { CheckForUpdateFromUi(); });
                }

                if (state == RunState.Connected || state == RunState.Restarting)
                {
                    bool alive = false;
                    try { torProc.Refresh(); alive = !torProc.HasExited; } catch { }
                    if (!alive)
                    {
                        if (watchdogTriggered && restartAttempts < 3)
                        {
                            restartAttempts++;
                            SetState(RunState.Restarting);
                            watchdogStop = true;
                            circuitWatchStop = true;
                            StopKeepAlive();
                            LogLine("watchdog: restarting tor (" + restartAttempts + "/3)");
                            // reconnect with the winning mode — never re-race
                            int mode = lastWinnerMode >= 0 ? lastWinnerMode :
                                       (uiModePos < ModeNames.Length ? uiModePos : 1);
                            int strat = comboStrategyIndexSafe();
                            bootPct = 0;
                            RunBg(delegate
                            {
                                try { torProc.WaitForExit(5000); } catch { }
                                for (int i = 0; i < 30 && PreviousRunActive(); i++) Thread.Sleep(500);
                                try { if (File.Exists(LockFile)) File.Delete(LockFile); } catch { }
                                cleaned = false;
                                SessionWorker(mode, strat, false);
                                UiInvokeDelegate(delegate { SetState(RunState.Connected); });
                            });
                        }
                        else if (!watchdogTriggered)
                        {
                            int code = -1;
                            try { code = torProc.ExitCode; } catch { }
                            Disconnect("tor exited (" + code + ")");
                        }
                    }
                }
            }

            private void CheckForUpdateFromUi()
            {
                try
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(
                        "https://api.github.com/repos/Delta-Kronecker/DeltaTor/releases/latest");
                    req.UserAgent = "deltator-ui/" + DeltaTorVersion.App;
                    req.Timeout = 8000;
                    req.ReadWriteTimeout = 8000;
                    using (WebResponse resp = req.GetResponse())
                    using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
                    {
                        Match m = Regex.Match(sr.ReadToEnd(), "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                        if (m.Success)
                        {
                            string latest = m.Groups[1].Value.TrimStart('v', 'V');
                            if (CompareVersionsLocal(latest, DeltaTorVersion.App) > 0)
                            {
                                UiInvokeDelegate(delegate
                                {
                                    showUpdateBanner = true;
                                    updateVersion = latest;
                                    Invalidate();
                                });
                            }
                        }
                    }
                }
                catch { }
            }

            private static int CompareVersionsLocal(string a, string b)
            {
                string[] pa = a.Split('.');
                string[] pb = b.Split('.');
                int n = Math.Max(pa.Length, pb.Length);
                for (int i = 0; i < n; i++)
                {
                    int x = 0, y = 0;
                    if (i < pa.Length) int.TryParse(pa[i], out x);
                    if (i < pb.Length) int.TryParse(pb[i], out y);
                    if (x != y) return x.CompareTo(y);
                }
                return 0;
            }

            private void CloseApp()
            {
                forceClosing = true;   // FormClosing must not re-prompt
                autoAbort = true;
                if (state != RunState.Idle && state != RunState.Stopping)
                {
                    watchdogStop = true;
                    circuitWatchStop = true;
                    StopKeepAlive();
                    try { if (torProc != null) torProc.Kill(); } catch { }
                    Cleanup();
                }
                if (trayIcon != null) trayIcon.Visible = false;
                Close();
            }

            private bool forceClosing;

            // The ✕ button and Alt+F4 both land here. While tor is running the
            // user gets the two-choice dialog; only an explicit "stop and exit"
            // (or idle state) actually tears down and closes.
            private void HandleCloseRequest()
            {
                if (state == RunState.Idle || state == RunState.Stopping)
                {
                    CloseApp();
                    return;
                }
                bool tray = false, stopExit = false;
                using (ExitDialog d = new ExitDialog())
                {
                    d.StartPosition = FormStartPosition.CenterParent;
                    d.ShowDialog(this);
                    tray = d.ChoiceTray;
                    stopExit = d.ChoiceStop;
                }
                if (stopExit) CloseApp();
                else if (tray) MinimizeToTray();
            }

            private void OnFormClosing(object s, FormClosingEventArgs e)
            {
                if (forceClosing || e.CloseReason != CloseReason.UserClosing)
                {
                    if (trayIcon != null) trayIcon.Visible = false;
                    return;
                }
                e.Cancel = true;
                HandleCloseRequest();
            }

            // Small owner-drawn exit prompt: exactly two choices, matching the
            // main window's style. Esc / the ✕ dismiss it (keeps running).
            internal sealed class ExitDialog : Form
            {
                public bool ChoiceTray;
                public bool ChoiceStop;
                private Rectangle rcClose, rcTray, rcStop;
                private int hover = -1;

                public ExitDialog()
                {
                    FormBorderStyle = FormBorderStyle.None;
                    StartPosition = FormStartPosition.CenterParent;
                    ClientSize = new Size(320, 176);
                    BackColor = Theme.Bg;
                    ForeColor = Theme.Text;
                    Font = Theme.Body();
                    DoubleBuffered = true;
                    MaximizeBox = false;
                    MinimizeBox = false;
                    ShowInTaskbar = false;
                    KeyPreview = true;

                    Resize += delegate { Layout(); };
                    Paint += OnPaint;
                    MouseMove += delegate(object s, MouseEventArgs e)
                    {
                        int h = Hit(e.Location);
                        if (h != hover) { hover = h; Invalidate(); }
                        Cursor = h >= 1 ? Cursors.Hand : Cursors.Default;
                    };
                    MouseDown += delegate(object s, MouseEventArgs e)
                    {
                        int h = Hit(e.Location);
                        if (h == 1) { ChoiceTray = true; Close(); }
                        else if (h == 2) { ChoiceStop = true; Close(); }
                        else if (h == 3) Close();
                    };
                    KeyDown += delegate(object s, KeyEventArgs e)
                    {
                        if (e.KeyCode == Keys.Escape) Close();
                        if (e.KeyCode == Keys.Enter) { ChoiceTray = true; Close(); }
                    };
                    Layout();
                }

                private void Layout()
                {
                    int w = ClientSize.Width;
                    rcClose = new Rectangle(w - 36, 0, 36, 32);
                    rcTray = new Rectangle(24, 66, w - 48, 40);
                    rcStop = new Rectangle(24, 116, w - 48, 40);
                    Invalidate();
                }

                private int Hit(Point p)
                {
                    if (rcTray.Contains(p)) return 1;
                    if (rcStop.Contains(p)) return 2;
                    if (rcClose.Contains(p)) return 3;
                    return -1;
                }

                private void OnPaint(object s, PaintEventArgs e)
                {
                    Graphics g = e.Graphics;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Theme.Bg);
                    using (SolidBrush b = new SolidBrush(Theme.Surface))
                        g.FillRectangle(b, 0, 0, ClientSize.Width, 32);
                    using (Pen pen = new Pen(Theme.Border))
                        g.DrawLine(pen, 0, 32, ClientSize.Width, 32);
                    TextRenderer.DrawText(g, "DeltaTor", Theme.H2(),
                        new Rectangle(16, 0, 160, 32), Theme.Text,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                    bool hovX = hover == 3;
                    using (Pen p = new Pen(hovX ? Theme.Text : Theme.Muted, 1.6f))
                    {
                        g.DrawLine(p, rcClose.Left + 13, 12, rcClose.Right - 13, 20);
                        g.DrawLine(p, rcClose.Left + 13, 20, rcClose.Right - 13, 12);
                    }

                    TextRenderer.DrawText(g, "Tor is running", Theme.Body(),
                        new Rectangle(0, 38, ClientSize.Width, 22), Theme.Muted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

                    PaintPill(g, rcTray, "MINIMIZE TO TRAY", hover == 1, false);
                    PaintPill(g, rcStop, "STOP AND EXIT", hover == 2, true);
                }

                private void PaintPill(Graphics g, Rectangle r, string text, bool hovered, bool danger)
                {
                    Color border = danger ? Theme.Red : Theme.Border;
                    Color col = danger ? Theme.Red : Theme.Text;
                    Theme.Pill(g, r, hovered ? Theme.SurfaceAlt : Theme.Surface, border);
                    TextRenderer.DrawText(g, text, Theme.H2(), r, col,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }

            private void MinimizeToTray()
            {
                ShowInTaskbar = false;
                Visible = false;
                if (trayIcon != null) trayIcon.Visible = true;
            }

            private void ShowFromTray()
            {
                ShowInTaskbar = true;
                Visible = true;
                WindowState = FormWindowState.Normal;
                if (trayIcon != null) trayIcon.Visible = false;
                Activate();
            }

            private void ExitFromTray()
            {
                if (state != RunState.Idle && state != RunState.Stopping)
                {
                    watchdogStop = true;
                    circuitWatchStop = true;
                    StopKeepAlive();
                    try { if (torProc != null) torProc.Kill(); } catch { }
                    Cleanup();
                }
                if (trayIcon != null) trayIcon.Visible = false;
                Environment.Exit(0);
            }

            private Icon CreateTrayIcon()
            {
                string icoPath = Path.Combine(
                    Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "",
                    "DeltaTor.ico");
                if (File.Exists(icoPath))
                {
                    try { return new Icon(icoPath, 16, 16); } catch { }
                }

                Bitmap bmp = new Bitmap(16, 16);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (SolidBrush b = new SolidBrush(Theme.Accent))
                        g.FillEllipse(b, 1, 1, 14, 14);
                    using (Pen p = new Pen(Color.White, 1.8f))
                    {
                        p.StartCap = LineCap.Round;
                        p.EndCap = LineCap.Round;
                        g.DrawArc(p, 4, 2, 8, 10, -60, 300);
                        g.DrawLine(p, 8, 3, 8, 8);
                    }
                }
                IntPtr h = bmp.GetHicon();
                return Icon.FromHandle(h);
            }
        }
    }
}
