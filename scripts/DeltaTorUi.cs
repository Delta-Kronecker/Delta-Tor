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
using System.Globalization;
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
            internal static readonly Color AccentLight = Color.FromArgb(183, 156, 255);
            internal static readonly Color Green = Color.FromArgb(52, 211, 153);
            internal static readonly Color GreenDark = Color.FromArgb(38, 160, 118);
            internal static readonly Color GreenLight = Color.FromArgb(125, 243, 192);
            internal static readonly Color Red = Color.FromArgb(239, 92, 112);
            internal static readonly Color Amber = Color.FromArgb(245, 178, 60);
            internal static readonly Color AmberLight = Color.FromArgb(255, 213, 138);
            internal const string FontName = "Segoe UI";
            internal static Font Title() { return new Font(FontName, 11f, FontStyle.Bold); }
            internal static Font Big() { return new Font(FontName, 18f, FontStyle.Bold); }
            internal static Font H2() { return new Font(FontName, 9.5f, FontStyle.Bold); }
            internal static Font Body() { return new Font(FontName, 9.25f, FontStyle.Regular); }
            internal static Font Small() { return new Font(FontName, 8f, FontStyle.Regular); }
            internal static Font Caption() { return new Font(FontName, 7.25f, FontStyle.Bold); }
            internal static Font Mono() { return new Font("Consolas", 8.25f, FontStyle.Regular); }
            internal static Font MonoBig() { return new Font("Consolas", 9.25f, FontStyle.Regular); }

            // Mirrors MainActivity.formatBytes so both surfaces render the same
            // numbers: "0 B", "1.5 KB", "12.4 MB".
            internal static string FormatBytes(long b)
            {
                if (b < 0) b = 0;
                if (b < 1024) return b + " B";
                double v = b / 1024.0;
                if (v < 1024) return v.ToString("0.0") + " KB";
                v /= 1024.0;
                if (v < 1024) return v.ToString("0.0") + " MB";
                v /= 1024.0;
                if (v < 1024) return v.ToString("0.0") + " GB";
                v /= 1024.0;
                return v.ToString("0.0") + " TB";
            }

            // Mirrors MainActivity.formatDuration: "mm:ss" under an hour, else "h:mm:ss".
            internal static string FormatDuration(long ms)
            {
                if (ms < 0) ms = 0;
                long s = ms / 1000;
                long h = s / 3600;
                int m = (int)((s % 3600) / 60);
                int ss = (int)(s % 60);
                if (h > 0) return h + ":" + m.ToString("00") + ":" + ss.ToString("00");
                return m.ToString("00") + ":" + ss.ToString("00");
            }

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

            // ---- bottom-panel primitives (mirror of the Android StatCard /
            // InfoPill / ArrowIcon composables) --------------------------
            internal static void StatArrow(Graphics g, Rectangle r, Color color, bool up)
            {
                using (Pen p = new Pen(color, 1.8f))
                {
                    p.StartCap = LineCap.Round;
                    p.EndCap = LineCap.Round;
                    int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                    if (up)
                    {
                        g.DrawLine(p, cx, cy + 4, cx, cy - 4);
                        g.DrawLine(p, cx - 4, cy - 1, cx, cy - 5);
                        g.DrawLine(p, cx + 4, cy - 1, cx, cy - 5);
                    }
                    else
                    {
                        g.DrawLine(p, cx, cy - 4, cx, cy + 4);
                        g.DrawLine(p, cx - 4, cy + 1, cx, cy + 5);
                        g.DrawLine(p, cx + 4, cy + 1, cx, cy + 5);
                    }
                }
            }

            internal static void StatCard(Graphics g, Rectangle r, string label,
                string value, Color accent, bool up)
            {
                using (GraphicsPath p = RoundRect(r, 10))
                {
                    FillGradientPath(g, p, SurfaceAlt, Surface);
                    using (Pen pen = new Pen(Border, 1f)) g.DrawPath(pen, p);
                }
                g.SmoothingMode = SmoothingMode.AntiAlias;
                StatArrow(g, new Rectangle(r.Right - 22, r.Y + 8, 14, 14), accent, up);
                TextRenderer.DrawText(g, label, Small(),
                    new Rectangle(r.X + 12, r.Y + 7, r.Width - 34, 14), Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, value, H2(),
                    new Rectangle(r.X + 12, r.Y + 22, r.Width - 24, r.Height - 28), Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis);
            }

            internal static void InfoPill(Graphics g, Rectangle r, string label, string value)
            {
                using (GraphicsPath p = RoundRect(r, 10))
                {
                    FillGradientPath(g, p, SurfaceAlt, Surface);
                    using (Pen pen = new Pen(Border, 1f)) g.DrawPath(pen, p);
                }
                TextRenderer.DrawText(g, label, Small(),
                    new Rectangle(r.X + 12, r.Y, r.Width - 24, 16), Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g, value, Body(),
                    new Rectangle(r.X + 12, r.Y + 16, r.Width - 24, r.Height - 20), Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis);
            }
        }

        // ---- main form -------------------------------------------------------
        private sealed class MainForm : Form
        {
            private enum RunState { Idle, Connecting, Connected, Restarting, Stopping }
            private enum Page { Main, Drawer, Log, Torrc }

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
            private bool tunShownOn, tunShownPending, tunLocalPending;
            private string lastTunErrorShown = "";
            private bool anyHover;

            private bool autoProxyEnabled;

            // ---- drawer (Android ControlDrawer parity) -------------------
            // Both sections start collapsed, like Android: the drawer opens on
            // its two headings, not on 248 countries.
            private bool drawerShowLocation;
            private bool drawerShowAdvanced;
            private bool drawerShowAllCountries;
            private int drawerScrollY;
            // code -> name for every country in countries.tsv, and the
            // capacity table for the ones that have exits. Loaded off the UI
            // thread on open; empty until then, which the section heading says.
            private readonly List<Country> drawerCountries = new List<Country>();
            private readonly Dictionary<string, ExitCap> drawerCapacity =
                new Dictionary<string, ExitCap>(StringComparer.OrdinalIgnoreCase);
            // Codes the user picked, in tap order. Mirrors Android ExitNodes.
            private readonly List<string> exitCodes = new List<string>();
            private readonly Dictionary<string, string> exitNames =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            private bool geoipLoaded;
            private string geoipError = "";

            private int editRow = -1;
            private string editBuf = "";
            private bool caretOn;
            private DateTime lastCaretFlip = DateTime.MinValue;

            private System.Windows.Forms.NotifyIcon trayIcon;
            private System.Windows.Forms.ContextMenuStrip trayMenu;

private Rectangle rcClose, rcMin,
                             rcProxy, rcTun, rcPower, rcBack, rcUpdateBtn;
            // state block + ring label
            private Rectangle rcStateWord, rcStateSub, rcRingLabel, rcPortsLine;
            // bottom panel (Android BottomPanel parity)
            private Rectangle rcProxyBtn, rcTunBtn;
            private Rectangle rcStatDown, rcStatUp, rcStatDl, rcStatUl, rcPillUpTime, rcPillExit;
            // log page
            private Rectangle rcLogCopy;
            private int logScrollY;
            // torrc editor page
            private Rectangle rcTorrcBox, rcTorrcSave, rcTorrcReset;
            private string torrcBuf = "";
            private bool torrcDirty;
            private int torrcScrollY;
            private int torrcLine = -1;
            private int torrcCaret;
            private int torrcAnchor = -1;
            // bridge store card
            private int bridgeVanilla, bridgeObfs4, bridgeWebtunnel;
            private DateTime bridgeChecked = DateTime.MinValue;
            private string bridgeError = "";

            // Live session stats, read off the Wintun adapter. rx/tx are the
            // adapter totals, so a session total is the delta taken at connect.
            private long rxBytes, txBytes, rxSessionBase, txSessionBase;
            private double rxSpeed, txSpeed;
            private long lastRxSample, lastTxSample;
            private DateTime lastNetSample = DateTime.MinValue;
            private DateTime connectedAt = DateTime.MinValue;
            private string exitCode = "", exitName = "";
            private bool exitLocating;
            private DateTime lastExitLookup = DateTime.MinValue;
            private readonly Rectangle[] rcRowVal = new Rectangle[15];
            private readonly Rectangle[] rcRowPrev = new Rectangle[15];
            private readonly Rectangle[] rcRowNext = new Rectangle[15];
            private readonly Rectangle[] rcRowBody = new Rectangle[15];

            private static readonly string[] SettingLabels =
            {
                "Mode", "Auto proxy",
                "Strategy level", "Conflux sets", "Conflux legs", "Linked-set cap",
                "Keep-alive", "Set select", "Skip slow sets (RTT)", "Best % of sets",
                "Weak legs (top %)", "Isolate SOCKS",
                "Torrc template", "Bridge store", "Connection log"
            };

            // Same grouping idea as Android's CONTROL DRAWER sections: the
            // Windows rows stay native (conflux knobs have no Android peer) but
            // they now read as sections instead of one flat list.
            private static readonly string[] SectionNames =
            {
                "CONNECTION", "CONFLUX", "SYSTEM", "ADVANCED"
            };

            private const int RowTorrc = 12, RowBridges = 13, RowLog = 14;

            private static int SectionOf(int i)
            {
                switch (i)
                {
                    case 0: case 2: case 7: return 0;                        // CONNECTION
                    case 3: case 4: case 5: case 8: case 9: case 10: return 1;  // CONFLUX
                    case 1: case 6: case 11: return 2;                       // SYSTEM
                    default: return 3;                                        // ADVANCED
                }
            }

            private static bool RowIsAction(int i)
            {
                return i == RowTorrc || i == RowBridges || i == RowLog;
            }

            public MainForm()
            {
                Text = "DeltaTor";
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.CenterScreen;
                ClientSize = new Size(400, 620);
                MinimumSize = new Size(400, 620);
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
                if (forced == "drawer" || forced == "settings") page = Page.Drawer;
                else if (forced == "log") page = Page.Log;
                else if (forced == "torrc") { page = Page.Torrc; torrcBuf = ReadTorrcTemplate(); }

                Resize += delegate { LayoutPass(); };
                HandleCreated += delegate { FlushUiQueue(); };
                Paint += OnPaintAll;
                MouseMove += OnMouseMoveAll;
                MouseDown += OnMouseDownAll;
                MouseWheel += OnMouseWheelAll;
                MouseLeave += delegate { anyHover = false; hoverId = -1; Invalidate(); };
                KeyDown += OnKeyDownAll;
                KeyPress += OnKeyPressAll;
                MouseDoubleClick += OnMouseDoubleClickAll;
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
                LoadExitSelection();
                // Read the country tables at startup, like Android's
                // loadDirectory() in onCreate, so the picker is already
                // ordered the first time the drawer is opened.
                LoadGeoTables();
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
                int hgt = ClientSize.Height;
                int pw = Math.Max(32, w - 48);

                // ---- bottom panel, pinned to the bottom edge ----------------
                // Android lays this out as a fixed stack of rows under the ring;
                // anchoring it to the bottom keeps the numbers still while the
                // state block above re-centres on window resize.
                int side = 20, gap = 8;
                int pillH = 40, statH = 52, infoH = 40;
                int panelH = pillH + gap + statH + gap + statH + gap + infoH;
                int py = hgt - 14 - panelH;
                int halfW = (w - side * 2 - gap) / 2;

                if (showProxy)
                {
                    rcProxyBtn = new Rectangle(side, py, halfW, pillH);
                    rcTunBtn = new Rectangle(side + halfW + gap, py, halfW, pillH);
                }
                else
                {
                    rcProxyBtn = new Rectangle(0, 0, 0, 0);
                    rcTunBtn = new Rectangle(side, py, w - side * 2, pillH);
                }
                int by2 = py + pillH + gap;
                rcStatDown = new Rectangle(side, by2, halfW, statH);
                rcStatUp = new Rectangle(side + halfW + gap, by2, halfW, statH);
                by2 += statH + gap;
                rcStatDl = new Rectangle(side, by2, halfW, statH);
                rcStatUl = new Rectangle(side + halfW + gap, by2, halfW, statH);
                by2 += statH + gap;
                rcPillUpTime = new Rectangle(side, by2, halfW, infoH);
                rcPillExit = new Rectangle(side + halfW + gap, by2, halfW, infoH);

                // the old in-flow proxy/tun rows are gone from the drawing code,
                // so leave them zeroed and unreachable by HitTest
                rcProxy = new Rectangle(0, 0, 0, 0);
                rcTun = new Rectangle(0, 0, 0, 0);

                // The state block centres in the space between the titlebar and
                // the bottom panel. The panel's top edge is that bound; it used
                // to be measured through the SETTINGS pill, which is gone.
                int panelTop = py - 14;

                // ---- update banner under the titlebar ----------------------
                rcUpdateBtn = showUpd
                    ? new Rectangle(24, 44, pw, 38)
                    : new Rectangle(0, 0, 0, 0);

                // ---- state block + ring centred in what is left -----------
                int topLimit = showUpd ? 92 : 46;
                int avail = panelTop - 14 - topLimit;
                int blockH = 34 + 16 + 10 + 104 + 22 + 18;
                int by3 = topLimit + Math.Max(0, (avail - blockH) / 2);
                rcStateWord = new Rectangle(0, by3, w, 34);
                rcStateSub = new Rectangle(0, by3 + 36, w, 16);
                rcPower = new Rectangle(cx - 52, by3 + 60, 104, 104);
                rcRingLabel = new Rectangle(0, rcPower.Bottom + 8, w, 22);
                rcPortsLine = new Rectangle(0, rcPower.Bottom + 30, w, 18);

// ---- settings rows -----------------------------------------
                // Grouped into sections, so each new section pushes its rows
                // down by one header band. The rows live inside the drawer's
                // ADVANCED section, so they start below it when it is open and
                // at the top of the scroll area when it is not; the whole block
                // is clipped to the drawer viewport in PaintDrawer.
                int ry;
                if (drawerShowAdvanced) ry = rcDrawerAdvanced.Bottom + 26;
                else ry = rcDrawerAdvanced.Y + 54;
                ry -= drawerScrollY;
                int rowCount = SettingLabels.Length;
                int lastSec = -1;
                for (int i = 0; i < rowCount; i++)
                {
                    int sec = SectionOf(i);
                    if (sec != lastSec)
                    {
                        ry += 26;
                        lastSec = sec;
                    }
                    rcRowBody[i] = new Rectangle(18, ry, w - 36, 36);
                    int valW = 152;
                    rcRowVal[i] = new Rectangle(w - 18 - valW, ry + 3, valW, 30);
                    if (RowIsAction(i))
                    {
                        rcRowVal[i] = new Rectangle(18, ry, w - 36, 36);
                        rcRowPrev[i] = new Rectangle(0, 0, 0, 0);
                        rcRowNext[i] = new Rectangle(0, 0, 0, 0);
                    }
                    else
                    {
                        rcRowPrev[i] = new Rectangle(rcRowVal[i].Left, ry + 3, 26, 30);
                        rcRowNext[i] = new Rectangle(rcRowVal[i].Right - 26, ry + 3, 26, 30);
                    }
                    ry += 37;
                }
                rcBack = new Rectangle(14, 42, 96, 26);

                // ---- drawer geometry ----------------------------------------
                LayoutDrawerPass();
                // ---- log page ------------------------------------------------
                rcLogCopy = new Rectangle(w - 84, 42, 70, 26);

                // ---- torrc editor page ---------------------------------------
                int tTop = 78;
                rcTorrcReset = new Rectangle(24, tTop, 90, 26);
                rcTorrcSave = new Rectangle(w - 114, tTop, 90, 26);
                rcTorrcBox = new Rectangle(20, tTop + 36, w - 40, hgt - tTop - 36 - 62);

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
            // These four mirror MainActivity's stateColor / statusLabel / wordFor /
            // sublineFor / labelText so both clients speak the same language.
            // Windows has no VPN pause state, so Android's READY /
            // "TOR RUNNING · VPN PAUSED" pair has no counterpart here: on
            // Windows the proxy and TUN pills already carry that meaning.
            private bool HasUiError()
            {
                return errorMsg.Length > 0 && DateTime.UtcNow < errorMsgUntil;
            }

            private Color StateColor()
            {
                if (uiRaceActive) return Theme.Amber;
                switch (state)
                {
                    case RunState.Connected: return Theme.Green;
                    case RunState.Connecting: return Theme.Amber;
                    case RunState.Restarting: return Theme.AmberLight;
                    case RunState.Stopping: return Theme.Amber;
                    default: return HasUiError() ? Theme.Red : Theme.Muted;
                }
            }

            // Android falls through to OFFLINE while reconnecting, which reads
            // wrong on a live link; LINK LOST is the honest label here.
            private string StatusLabel()
            {
                switch (state)
                {
                    case RunState.Stopping: return "";
                    case RunState.Connecting: return "CONNECTING";
                    case RunState.Connected: return "CONNECTED";
                    case RunState.Restarting: return "LINK LOST";
                    default: return "OFFLINE";
                }
            }

            private string StateText()
            {
                if (uiRaceActive)
                    return bootPct > 0 ? "RACE " + bootPct + "%" : "RACING";
                switch (state)
                {
                    case RunState.Stopping: return "STOPPING";
                    case RunState.Connected: return "CONNECTED";
                    case RunState.Connecting: return "CONNECTING";
                    case RunState.Restarting: return "LINK LOST";
                    default: return HasUiError() ? "ERROR" : "OFFLINE";
                }
            }

            private string ActiveModeLabel()
            {
                int m = lastWinnerMode >= 0
                        ? lastWinnerMode
                        : (uiModePos < ModeNames.Length ? uiModePos : -1);
                if (m < 0 || m >= ModeNames.Length) return "AUTO RACE";
                return PrettyMode(ModeNames[m]).ToUpperInvariant();
            }

            private string SublineText()
            {
                if (state == RunState.Stopping) return "";
                if (HasUiError()) return "BOOTSTRAP FAILED";
                switch (state)
                {
                    case RunState.Connecting:
                    {
                        if (uiRaceActive)
                        {
                            string r = bootPct > 0 ? "RACING · " + bootPct + "%" : "RACING";
                            return r + (bootTag.Length > 0 ? " · " + bootTag : "");
                        }
                        string pct = "TUNNEL BOOTSTRAPPING · " +
                                     (bootPct > 0 ? bootPct + "%" : "0%");
                        if (fallbackPending) return pct + " · FALLBACK";
                        if (uiCanFallback && bootPct > 0 && bootPct < 100)
                        {
                            int secs = (int)Math.Max(0, Math.Ceiling(
                                (FallbackSpan - (DateTime.UtcNow - bootPctSince)).TotalSeconds));
                            return pct + " · FALLBACK IN " + secs + "s";
                        }
                        return pct + (bootTag.Length > 0 ? " · " + bootTag : "");
                    }
                    case RunState.Restarting:
                        return "RESTORING THE TUNNEL";
                    case RunState.Connected:
                        return ActiveModeLabel() + " · GATEWAY ACTIVE";
                    default:
                        return "YOUR PRIVATE GATEWAY";
                }
            }

            // Label under the ring. Empty while stopping so STOPPING appears in
            // exactly one place, matching the Android fix in e681511.
            private string RingLabelText()
            {
                if (state == RunState.Stopping) return "";
                if (HasUiError()) return errorMsg;
                switch (state)
                {
                    case RunState.Connecting: return "CANCEL";
                    case RunState.Connected: return "DISCONNECT";
                    case RunState.Restarting: return "CANCEL";
                    default: return "CONNECT";
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
                    else if (page == Page.Drawer) PaintDrawer(g);
                    else if (page == Page.Log) PaintLog(g);
                    else PaintTorrc(g);
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

                // Android's top bar opens the controls drawer from a hamburger
                // on the left. Windows had a SETTINGS pill in the body instead;
                // the hamburger is where the same door is now.
                if (page == Page.Main)
                    DrawHamburger(g, rcHamburger, hoverId == 30);

                // The dot and the word move right of the hamburger on Main and
                // keep their old place on the sub-pages, which have no drawer.
                int barLeft = page == Page.Main ? 52 : 30;
                Color sc = StateColor();
                Theme.DrawGlow(g, new Rectangle(barLeft - 19, 10, 14, 14), sc, 6, 40);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush b = new SolidBrush(sc))
                    g.FillEllipse(b, barLeft - 16, 13, 8, 8);

                TextRenderer.DrawText(g, "DeltaTor", Theme.Title(),
                    new Rectangle(barLeft, 0, 120, 36), Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

                // Android's StatusChip lives in the titlebar here, because the
                // borderless window already owns the top strip. It is hidden on
                // the sub-pages, matching Android's main-screen-only chip.
                string chipTxt = page == Page.Main ? StatusLabel() : "";
                if (chipTxt.Length > 0)
                {
                    Font cf = Theme.Caption();
                    int cw = TextRenderer.MeasureText(g, chipTxt, cf).Width + 30;
                    int cxr = ClientSize.Width - 192 - cw;
                    Rectangle chipR = new Rectangle(Math.Max(152, cxr), 9, cw, 18);
                    using (GraphicsPath p = Theme.RoundRect(chipR, chipR.Height / 2))
                    {
                        Theme.FillGradientPath(g, p, Theme.SurfaceLight, Theme.Surface);
                        using (Pen pen = new Pen(Theme.Border, 1f)) g.DrawPath(pen, p);
                    }
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (SolidBrush b = new SolidBrush(sc))
                        g.FillEllipse(b, chipR.X + 9, chipR.Y + 6, 6, 6);
                    TextRenderer.DrawText(g, chipTxt, cf,
                        new Rectangle(chipR.X + 20, chipR.Y,
                            chipR.Width - 24, chipR.Height), Theme.Text,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                }

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
                    rcStateWord, stateCol,
                    Color.FromArgb(60, 0, 0, 0),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter,
                    1, 2);

                // StateBlock subline: Android draws it right under the big word.
                string sub = SublineText();
                if (sub.Length > 0)
                    TextRenderer.DrawText(g, sub, Theme.Caption(),
                        rcStateSub, stateCol,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.EndEllipsis);

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
                        case RunState.Restarting: ringColor = Theme.AmberLight; glowColor = Theme.AmberLight; break;
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

                // Ring label. Deliberately not ErrorOr(): while stopping this slot must stay
                // empty so STOPPING is not repeated, and a flash message must
                // not be able to hijack it.
                string cap = RingLabelText();
                if (cap.Length > 0)
                    TextRenderer.DrawText(g, cap, Theme.Body(),
                        rcRingLabel,
                        HasError() ? (errorMsgIsError ? Theme.Red : Theme.Text) : Theme.Muted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.EndEllipsis);

                if (state == RunState.Connected)
                {
                    TextRenderer.DrawText(g, "SOCKS 127.0.0.1:" + liveSocksPort +
                        "   HTTP 127.0.0.1:" + liveHttpPort +
                        "   DNS 127.0.0.1:" + liveDnsPort,
                        Theme.Small(), rcPortsLine,
                        Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }

                // Android renders nothing at all while stopping, and shows "--"
                // for every stat that is not live. Same rules here.
                if (state != RunState.Stopping)
                    PaintBottomPanel(g);

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

            // ---- bottom panel -----------------------------------------------
            // Row order matches Android's BottomPanel exactly: the two toggles,
            // then the speed cards, then the totals, then the info pills.
            private void PaintBottomPanel(Graphics g)
            {
                bool tunPend = TunPending() || tunLocalPending;
                string tunName = tunPend ? "TUN …" : "TUN";

                if (!autoProxyEnabled)
                    PaintTogglePill(g, rcProxyBtn, "PROXY", ProxyIsOurs(),
                        hoverId == 20, false, true);
                PaintTogglePill(g, rcTunBtn, tunName, TunOnNow(), hoverId == 21, tunPend, true);

                bool live = state == RunState.Connected || state == RunState.Restarting;
                string dash = "--";
                string down = dash, up = dash, dl = dash, ul = dash;
                if (live)
                {
                    down = Theme.FormatBytes((long)rxSpeed) + "/s";
                    up = Theme.FormatBytes((long)txSpeed) + "/s";
                    dl = Theme.FormatBytes(rxBytes);
                    ul = Theme.FormatBytes(txBytes);
                }
                Theme.StatCard(g, rcStatDown, "SPEED DOWN", down, Theme.GreenLight, false);
                Theme.StatCard(g, rcStatUp, "SPEED UP", up, Theme.AccentLight, true);
                Theme.StatCard(g, rcStatDl, "DOWNLOADED", dl, Theme.Green, false);
                Theme.StatCard(g, rcStatUl, "UPLOADED", ul, Theme.Accent, true);

                string upTime = dash;
                if (connectedAt != DateTime.MinValue)
                    upTime = Theme.FormatDuration((long)(DateTime.UtcNow - connectedAt).TotalMilliseconds);

                string ex = dash;
                if (state == RunState.Connected || state == RunState.Restarting)
                {
                    if (exitName.Length > 0) ex = exitCode + " " + exitName;
                    else if (exitLocating) ex = "Locating …";
                }
                Theme.InfoPill(g, rcPillUpTime, "UP TIME", upTime);
                Theme.InfoPill(g, rcPillExit, "EXIT", ex);
            }

            // Must match start-tor.cs StuckFallbackMinutes (2 minutes): how long a
            // frozen bootstrap percentage is tolerated before the fallback restart.
            private static readonly TimeSpan FallbackSpan = TimeSpan.FromMinutes(2.0);


            private void PaintTogglePill(Graphics g, Rectangle r, string name, bool on,
                                         bool hovered, bool pending, bool compact)
            {
                Theme.PillGradient(g, r,
                    hovered ? Theme.SurfaceLight : Theme.SurfaceAlt,
                    Theme.Surface, pending ? Theme.Amber : Theme.Border);
                int pad = compact ? 12 : 16;
                int swW = compact ? 40 : 44, swH = compact ? 20 : 22;
                TextRenderer.DrawText(g, name, Theme.H2(),
                    new Rectangle(r.Left + pad, r.Y, r.Width - pad - swW - 16, r.Height),
                    pending ? Theme.Amber : (on ? Theme.Text : Theme.Muted),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis);

                int swX = r.Right - swW - 12, swY = r.Y + (r.Height - swH) / 2;
                Rectangle sw = new Rectangle(swX, swY, swW, swH);

                Color bg = pending ? Theme.Amber : (on ? Theme.Green : Theme.SurfaceLight);
                using (GraphicsPath bgPath = Theme.RoundRect(sw, swH / 2))
                    Theme.FillGradientPath(g, bgPath, bg, Color.FromArgb(
                        Math.Max(0, bg.A - 30), bg.R, bg.G, bg.B));

                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (on && !pending)
                    Theme.DrawGlow(g, new Rectangle(swX - 2, swY - 2, swW + 4, swH + 4),
                        Theme.Green, 4, 20);

                int knobD = compact ? 14 : 16;
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

// ---- drawer layout ---------------------------------------------
            // The drawer is a single scrollable column: the LOCATION heading,
            // its body when it is open, a divider, the ADVANCED heading and the
            // settings rows when that one is open. Android collapses both
            // sections by default, so the drawer opens on two headings.
            //
            // Every rectangle is computed here rather than at paint time, and
            // hit-testing reads the same fields, so a row cannot be drawn in
            // one place and tapped in another.
            private const int CountryRowHeight = 42;
            // Country rows report CountryHitBase + index. It sits above the
            // heading ids (3000/3001) and clear of 40, which is GoBack.
            private const int CountryHitBase = 4000;
            private const int ExitPickerTop = 25;
            private int drawerContentHeight;
            private readonly List<Rectangle> rcCountryRow = new List<Rectangle>();
            private readonly List<string> countryRowCode = new List<string>();
            private Rectangle rcHamburger, rcDrawerClose, rcDrawerLocation,
                              rcDrawerAdvanced, rcDrawerGithub, rcDrawerScrollTrack,
                              rcDrawerWarn;
            private Rectangle rcRowAny, rcDrawerRestHeader, rcHeaderExits;

            private void LayoutDrawerPass()
            {
                int w = ClientSize.Width;
                int hgt = ClientSize.Height;
                rcHamburger = new Rectangle(8, 3, 34, 30);
                rcDrawerClose = new Rectangle(w - 54, 46, 34, 34);
                rcDrawerGithub = new Rectangle(20, hgt - 58, w - 40, 40);
                rcRowAny = new Rectangle(0, 0, 0, 0);
                rcDrawerRestHeader = new Rectangle(0, 0, 0, 0);
                rcDrawerWarn = new Rectangle(0, 0, 0, 0);

                // The scroll area runs from under the drawer header to above the
                // GITHUB pill, which is pinned to the bottom like Android's.
                int top = 96;
                int bottom = rcDrawerGithub.Top - 10;
                int viewport = Math.Max(1, bottom - top);

                rcCountryRow.Clear();
                countryRowCode.Clear();

                int y = top - drawerScrollY;
                rcDrawerLocation = new Rectangle(0, y, w, 54);
                y += 54;
                if (drawerShowLocation)
                {
                    // The amber "use only when needed" card sits directly under
                    // the heading, before any country, because it is a warning
                    // about the whole section rather than about one row.
                    rcDrawerWarn = new Rectangle(20, y, w - 40, 74);
                    y += 74;
                    rcRowAny = new Rectangle(20, y, w - 40, CountryRowHeight);
                    y += CountryRowHeight;
                    bool known = drawerCapacity.Count > 0;
                    List<Country> withExits = WithExitCountries();
                    List<Country> without = WithoutExitCountries();
                    int shown = known ? Math.Min(ExitPickerTop, withExits.Count) : withExits.Count;
                    // The group heading needs its own band, otherwise the
                    // first row sits under the caption text.
                    bool reading = !known && withExits.Count == 0 && without.Count == 0;
                    rcHeaderExits = new Rectangle(20, y, w - 40, reading ? 30 : 22);
                    y += rcHeaderExits.Height;
                    for (int i = 0; i < shown; i++)
                    {
                        AddCountryRow(new Rectangle(20, y, w - 40, CountryRowHeight), withExits[i]);
                        y += CountryRowHeight;
                    }
                    if (without.Count > 0)
                    {
                        // The header is tappable: it toggles the remainder and,
                        // when the selection lives down there, it opens itself
                        // instead of hiding the pick the user just made.
                        rcDrawerRestHeader = new Rectangle(20, y, w - 40, 30);
                        y += 30;
                        bool openRest = drawerShowAllCountries || RestHoldsSelection();
                        if (openRest)
                            for (int i = 0; i < without.Count; i++)
                            {
                                AddCountryRow(new Rectangle(20, y, w - 40, CountryRowHeight), without[i]);
                                y += CountryRowHeight;
                            }
                    }
                    y += 8;
                }
                rcDrawerAdvanced = new Rectangle(0, y, w, 54);
                y += 54;
                if (drawerShowAdvanced)
                {
                    int lastSec = -1;
                    for (int i = 0; i < SettingLabels.Length; i++)
                    {
                        int sec = SectionOf(i);
                        if (sec != lastSec) { y += 26; lastSec = sec; }
                        y += 37;
                    }
                    y += 10;
                }

                int contentH = Math.Max(0, y + drawerScrollY - top) + 14;
                drawerContentHeight = contentH;
                int maxScroll = Math.Max(0, contentH - viewport);
                if (drawerScrollY > maxScroll) drawerScrollY = maxScroll;

                if (contentH > viewport)
                {
                    int trackH = Math.Max(40, viewport * viewport / contentH);
                    int span = viewport - trackH;
                    int off = maxScroll > 0 ? span * drawerScrollY / maxScroll : 0;
                    rcDrawerScrollTrack = new Rectangle(w - 6, top + off, 3, trackH);
                }
                else rcDrawerScrollTrack = new Rectangle(0, 0, 0, 0);

            }

            private void AddCountryRow(Rectangle r, Country c)
            {
                rcCountryRow.Add(r);
                countryRowCode.Add(c.Code);
            }

            private int DrawerMaxScroll()
            {
                int viewport = Math.Max(1, rcDrawerGithub.Top - 10 - 96);
                return Math.Max(0, drawerContentHeight - viewport);
            }
// ---- drawer painting -------------------------------------------
            // Mirrors Android's ControlDrawer: a CONTROLS header with the
            // "applies on the next connect" subline and a close box, then the
            // two expandable sections, then a GITHUB pill pinned to the
            // bottom. Both sections start collapsed.
            private void PaintDrawer(Graphics g)
            {
                TextRenderer.DrawText(g, "CONTROLS", Theme.Big(), new Rectangle(20, 40, 260, 30),
                    Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g, "Everything here applies on the next connect",
                    Theme.Small(), new Rectangle(20, 68, rcDrawerClose.Left - 26, 18),
                    Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis);
                DrawCloseIcon(g, rcDrawerClose, hoverId == 31);

                int top = 96;
                int bottom = rcDrawerGithub.Top - 10;
                Region prevClip = g.Clip;
                g.SetClip(new Rectangle(0, top, ClientSize.Width, Math.Max(0, bottom - top)));

                PaintSectionHeading(g, rcDrawerLocation, "LOCATION", LocationSummary(),
                    drawerShowLocation, hoverId == 3000);
                PaintSectionHeading(g, rcDrawerAdvanced, "ADVANCED",
                    drawerShowAdvanced ? "TUNING AND TEMPLATES" : "HIDDEN",
                    drawerShowAdvanced, hoverId == 3001);

                if (drawerShowLocation)
                {
                    PaintLocationWarning(g);
                    PaintCountryRow(g, new Rectangle(20, rcRowAny.Y, ClientSize.Width - 40,
                        CountryRowHeight), "\U0001F310", "Any location \u00b7 default", "--",
                        exitCodes.Count == 0, 0, 0f);
                    bool known = drawerCapacity.Count > 0;
                    List<Country> withExits = WithExitCountries();
                    List<Country> without = WithoutExitCountries();
                    int shown = known ? Math.Min(ExitPickerTop, withExits.Count) : withExits.Count;
                    bool reading = !known && withExits.Count == 0 && without.Count == 0;
                    if (reading)
                        TextRenderer.DrawText(g, "Reading country list \u2026", Theme.Small(),
                            rcHeaderExits, Theme.Muted,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                    else if (known)
                        PaintGroupHeader(g, rcHeaderExits,
                            "COUNTRIES WITH THE MOST EXIT BANDWIDTH",
                            "TOP " + shown + " OF " + withExits.Count);
                    else
                        PaintGroupHeader(g, rcHeaderExits, "COUNTRIES",
                            "EXIT DATA NOT LOADED", true);
                    for (int i = 0; i < shown && i < withExits.Count; i++)
                        PaintLoadedCountry(g, withExits[i]);
                    if (without.Count > 0)
                    {
                        // The warning names the majority rather than claiming
                        // there are no exits at all: the countries past the cut
                        // that still have one are in this group too.
                        bool hov = hoverId == 34;
                        PaintGroupHeader(g, rcDrawerRestHeader,
                            "REST OF WORLD \u00b7 MOST HAVE NO EXIT",
                            drawerShowAllCountries ? "HIDE" : "SHOW ALL", true, hov);
                        if (drawerShowAllCountries || RestHoldsSelection())
                            for (int i = 0; i < without.Count; i++)
                                PaintLoadedCountry(g, without[i]);
                    }
                }
                DrawDivider(g, rcDrawerAdvanced.Y - 8);

                if (drawerShowAdvanced)
                    PaintSettingRows(g, rcDrawerAdvanced.Bottom, top, bottom);

                g.Clip = prevClip;

                if (rcDrawerScrollTrack.Height > 0)
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(90, Theme.BorderLight)))
                        g.FillRectangle(b, rcDrawerScrollTrack);

                bool hovGh = hoverId == 35;
                Theme.PillGradient(g, rcDrawerGithub,
                    hovGh ? Theme.Accent : Theme.AccentSoft,
                    hovGh ? Theme.AccentSoft : Theme.AccentDark, Theme.Accent);
                TextRenderer.DrawText(g, "GITHUB", Theme.H2(), rcDrawerGithub,
                    hovGh ? Color.White : Theme.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            private void PaintLocationWarning(Graphics g)
            {
                Rectangle r = rcDrawerWarn;
                if (r.Height <= 0) return;
                using (GraphicsPath p = Theme.RoundRect(r, 12))
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(26, Theme.Amber.R, Theme.Amber.G, Theme.Amber.B)))
                        g.FillPath(b, p);
                TextRenderer.DrawText(g, "USE ONLY WHEN NEEDED", Theme.Caption(),
                    new Rectangle(r.Left + 12, r.Y + 8, r.Width - 100, 16),
                    Theme.Amber, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                bool hovClear = hoverId == 32;
                TextRenderer.DrawText(g, "CLEAR", Theme.Caption(),
                    new Rectangle(r.Right - 92, r.Y + 8, 80, 16),
                    exitCodes.Count > 0 ? (hovClear ? Color.White : Theme.Red) : Theme.Muted,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g,
                    "Picking a country sends your traffic through a relay there. " +
                    "It can lower your speed and make the connection less stable.",
                    Theme.Small(),
                    new Rectangle(r.Left + 12, r.Y + 28, r.Width - 24, r.Height - 36),
                    Theme.Muted, TextFormatFlags.Left | TextFormatFlags.Top |
                    TextFormatFlags.WordBreak);
            }

            /// <summary>The two drawer sections: name, one-line state, chevron.</summary>
            private void PaintSectionHeading(Graphics g, Rectangle r, string title, string summary,
                bool open, bool hovered)
            {
                if (r.Height <= 0) return;
                TextRenderer.DrawText(g, title, Theme.H2(),
                    new Rectangle(r.Left + 20, r.Y + 6, r.Width - 90, 20), Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g, summary, Theme.Small(),
                    new Rectangle(r.Left + 20, r.Y + 26, r.Width - 90, 18), Theme.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis);
                DrawChevron(g, new Rectangle(r.Right - 44, r.Y + (r.Height - 22) / 2, 22, 22),
                    !open, hovered);
            }

            private void PaintGroupHeader(Graphics g, Rectangle r, string title, string right,
                bool muted = false, bool hovered = false)
            {
                TextRenderer.DrawText(g, title, Theme.Caption(), r,
                    muted ? Theme.Muted : Theme.AccentLight,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g, right, Theme.Caption(), r,
                    hovered ? Theme.Text : Theme.Muted,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }

            // One country row: flag, name, the share and count that predict
            // speed, then the code and the selection dot.
            private void PaintCountryRow(Graphics g, Rectangle r, string flag, string name,
                string code, bool selected, int exits, float share)
            {
                bool hov = hoverId >= CountryHitBase &&
                           hoverId - CountryHitBase < countryRowCode.Count &&
                           rcCountryRow[hoverId - CountryHitBase] == r;
                if (selected)
                {
                    using (GraphicsPath p = Theme.RoundRect(r, 10))
                        using (SolidBrush b = new SolidBrush(
                            Color.FromArgb(30, Theme.Accent.R, Theme.Accent.G, Theme.Accent.B)))
                            g.FillPath(b, p);
                }
                else if (hov)
                {
                    using (GraphicsPath p = Theme.RoundRect(r, 10))
                        using (SolidBrush b = new SolidBrush(Theme.SurfaceAlt))
                            g.FillPath(b, p);
                }

                int midY = r.Y + r.Height / 2;
                Rectangle dot = new Rectangle(r.Right - 28, midY - 8, 16, 16);
                TextRenderer.DrawText(g, flag, Theme.Body(),
                    new Rectangle(r.Left + 8, r.Y, 30, r.Height),
                    Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, code, Theme.Caption(),
                    new Rectangle(dot.Left - 42, r.Y, 34, r.Height),
                    Theme.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

                if (exits > 0)
                {
                    TextRenderer.DrawText(g, (share * 100f).ToString("0.0") + "%", Theme.Caption(),
                        new Rectangle(r.Right - 150, r.Y, 46, r.Height),
                        selected ? Theme.AccentLight : Theme.Muted,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                    TextRenderer.DrawText(g, exits.ToString(), Theme.Caption(),
                        new Rectangle(r.Right - 100, r.Y, 30, r.Height),
                        Theme.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                }

                TextRenderer.DrawText(g, name, Theme.Body(),
                    new Rectangle(r.Left + 40, r.Y, r.Width - 190, r.Height),
                    selected ? Theme.Text : Theme.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis);

                Color ring = selected ? Theme.AccentLight : Theme.Border;
                using (Pen pen = new Pen(ring, 1f))
                    g.DrawEllipse(pen, dot);
                if (selected)
                    using (Pen pen = new Pen(Color.White, 1.8f))
                    {
                        g.DrawLine(pen, dot.Left + 4, dot.Top + 8, dot.Left + 6, dot.Top + 11);
                        g.DrawLine(pen, dot.Left + 6, dot.Top + 11, dot.Left + 12, dot.Top + 5);
                    }
            }

            private void PaintLoadedCountry(Graphics g, Country c)
            {
                int idx = countryRowCode.IndexOf(c.Code);
                if (idx < 0) return;
                Rectangle r = rcCountryRow[idx];
                PaintCountryRow(g, r, FlagEmoji(c.Code), c.Name, c.Code,
                    exitCodes.Contains(c.Code), c.Cap.Exits, c.Cap.Weight);
            }

            // The settings rows, drawn into the drawer's ADVANCED section with
            // the section headers that already group them.
            private void PaintSettingRows(Graphics g, int fromY, int clipTop, int clipBottom)
            {
                int rowCount = SettingLabels.Length;
                int lastSec = -1;
                for (int i = 0; i < rowCount; i++)
                {
                    Rectangle body = rcRowBody[i];
                    if (body.Bottom < clipTop || body.Y > clipBottom) continue;
                    bool selected = editRow == i;
                    int sec = SectionOf(i);
                    if (sec != lastSec)
                    {
                        lastSec = sec;
                        int hy = body.Y - 24;
                        if (hy >= clipTop)
                            TextRenderer.DrawText(g, SectionNames[sec], Theme.Caption(),
                                new Rectangle(20, hy, body.Width, 18), Theme.AccentLight,
                                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                    }
                    Color bgTop = i % 2 == 0 ? Theme.Surface : Theme.SurfaceAlt;
                    Color bgBot = i % 2 == 0 ? Theme.SurfaceAlt : Theme.Surface;
                    Theme.FillGradient(g, new Rectangle(20, body.Y, body.Width, body.Height), bgTop, bgBot);
                    if (selected)
                    {
                        using (SolidBrush b = new SolidBrush(Theme.Accent))
                            g.FillRectangle(b, 20, body.Y + 4, 4, body.Height - 8);
                    }
                    using (Pen pen = new Pen(Theme.Border, 1f))
                        g.DrawLine(pen, 20, body.Bottom, 20 + body.Width, body.Bottom);
                    TextRenderer.DrawText(g, SettingLabels[i], Theme.Body(),
                        new Rectangle(body.Left + 10, body.Y, body.Width - 174, body.Height),
                        Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.EndEllipsis);
                    PaintSettingValue(g, i);
                }
                int lastRow = rowCount - 1;
                if (rcRowBody[lastRow].Bottom < clipBottom && rcRowBody[lastRow].Bottom > clipTop)
                    TextRenderer.DrawText(g, "applies on next connect", Theme.Small(),
                        new Rectangle(0, rcRowBody[lastRow].Bottom + 8, ClientSize.Width, 18),
                        Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
            }

            private void DrawDivider(Graphics g, int y)
            {
                using (Pen pen = new Pen(Theme.Border, 1f))
                    g.DrawLine(pen, 20, y, ClientSize.Width - 20, y);
            }

            private void DrawCloseIcon(Graphics g, Rectangle r, bool hovered)
            {
                if (r.Width <= 0) return;
                using (GraphicsPath p = Theme.RoundRect(r, 12))
                using (SolidBrush b = new SolidBrush(hovered ? Theme.SurfaceLight : Theme.Surface))
                using (Pen pen = new Pen(Theme.Border, 1f))
                {
                    g.FillPath(b, p);
                    g.DrawPath(pen, p);
                }
                int midX = r.Left + r.Width / 2, midY = r.Top + r.Height / 2;
                using (Pen pen = new Pen(Theme.Text, 1.8f))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawLine(pen, midX - 5, midY - 5, midX + 5, midY + 5);
                    g.DrawLine(pen, midX + 5, midY - 5, midX - 5, midY + 5);
                }
            }

            // The three-line hamburger Android draws in its top bar.
            private void DrawHamburger(Graphics g, Rectangle r, bool hovered)
            {
                using (GraphicsPath p = Theme.RoundRect(r, 11))
                using (SolidBrush b = new SolidBrush(hovered ? Theme.SurfaceLight : Theme.Surface))
                using (Pen pen = new Pen(Theme.Border, 1f))
                {
                    g.FillPath(b, p);
                    g.DrawPath(pen, p);
                }
                using (Pen pen = new Pen(Theme.AccentLight, 1.7f))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    int x1 = r.Left + 10, x2 = r.Right - 10;
                    int midY = r.Top + r.Height / 2;
                    g.DrawLine(pen, x1, midY - 5, x2, midY - 5);
                    g.DrawLine(pen, x1, midY, x2, midY);
                    g.DrawLine(pen, x1, midY + 5, x2, midY + 5);
                }
            }
            private void PaintSettingValue(Graphics g, int i)
            {
                Rectangle v = rcRowVal[i];
                if (RowIsAction(i))
                {
                    bool hov = hoverId == 300 + i;
                    Theme.PillGradient(g, v,
                        hov ? Theme.SurfaceLight : Theme.SurfaceAlt, Theme.Surface, Theme.Border);
                    string val =
                        i == RowTorrc ? (torrcDirty ? "Edit torrc •" : "Edit torrc") :
                        i == RowLog ? "View log" : BridgeCardSummary();
                    TextRenderer.DrawText(g, SettingLabels[i], Theme.Body(),
                        new Rectangle(v.Left + 14, v.Y, v.Width - 120, v.Height), Theme.Text,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                    TextRenderer.DrawText(g, val, Theme.Small(),
                        new Rectangle(v.Left + v.Width - 200, v.Y, 160, v.Height),
                        bridgeError.Length > 0 && i == RowBridges ? Theme.Red : Theme.Muted,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.EndEllipsis);
                    DrawChevron(g, new Rectangle(v.Right - 30, v.Y + (v.Height - 22) / 2, 22, 22),
                        true, hov);
                    return;
                }
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
            // ---- torrc line editor -------------------------------------------
            // The template is edited line by line: a double-click drops the
            // caret on that line, then the normal WinForms KeyDown path drives
            // it. torrcBuf is the single source of truth, so PASTE, SELECT ALL
            // and COPY behave the way a text box would.
            private List<string> TorrcLines()
            {
                return new List<string>(
                    (torrcBuf ?? "").Replace("\r\n", "\n").Split('\n'));
            }

            private void SetTorrcLines(List<string> l)
            {
                torrcBuf = string.Join("\r\n", l.ToArray());
                torrcDirty = true;
            }

            private void TorrcBeginLine(int idx)
            {
                List<string> l = TorrcLines();
                if (idx < 0 || idx >= l.Count) return;
                torrcLine = idx;
                torrcAnchor = -1;
                torrcCaret = l[idx].Length;
                ScrollTorrcToLine(idx);
            }

            private void ScrollTorrcToLine(int idx)
            {
                int lh = 13;
                int top = idx * lh - torrcScrollY;
                int view = rcTorrcBox.Height - 12;
                if (top < 0) torrcScrollY = idx * lh;
                else if (top + lh > view) torrcScrollY = idx * lh + lh - view;
                int maxScrollY = Math.Max(0, TorrcLines().Count * lh - rcTorrcBox.Height);
                torrcScrollY = Math.Max(0, Math.Min(maxScrollY, torrcScrollY));
            }

            private void TorrcSelRange(string line, out int a, out int b)
            {
                a = Math.Max(0, Math.Min(torrcCaret, line.Length));
                b = a;
                if (torrcAnchor >= 0)
                {
                    int x = Math.Max(0, Math.Min(torrcAnchor, line.Length));
                    a = Math.Min(a, x);
                    b = Math.Max(b, x);
                }
            }

            private void TorrcReplaceSel(string s)
            {
                List<string> l = TorrcLines();
                if (torrcLine < 0 || torrcLine >= l.Count) return;
                string line = l[torrcLine];
                int a, b;
                TorrcSelRange(line, out a, out b);
                l[torrcLine] = line.Substring(0, a) + s + line.Substring(b);
                torrcCaret = a + s.Length;
                torrcAnchor = -1;
                SetTorrcLines(l);
                Invalidate();
            }

            private void TorrcDeleteSel()
            {
                List<string> l = TorrcLines();
                if (torrcLine < 0 || torrcLine >= l.Count) return;
                string line = l[torrcLine];
                int a, b;
                TorrcSelRange(line, out a, out b);
                if (a == b && torrcCaret == 0)
                {
                    // Backspace at column 0 joins the line with the previous one.
                    if (torrcLine == 0) return;
                    string prev = l[torrcLine - 1];
                    l[torrcLine - 1] = prev + line;
                    l.RemoveAt(torrcLine);
                    torrcLine--;
                    torrcCaret = prev.Length;
                    torrcAnchor = -1;
                    SetTorrcLines(l);
                    return;
                }
                l[torrcLine] = line.Substring(0, a) + line.Substring(b);
                torrcCaret = a;
                torrcAnchor = -1;
                SetTorrcLines(l);
            }

            private void TorrcMoveLine(int delta)
            {
                List<string> l = TorrcLines();
                int dst = torrcLine + delta;
                if (torrcLine < 0 || dst < 0 || dst >= l.Count) return;
                string cur = l[torrcLine];
                l[torrcLine] = l[dst];
                l[dst] = cur;
                torrcLine = dst;
                torrcAnchor = -1;
                SetTorrcLines(l);
                ScrollTorrcToLine(dst);
            }

            private string TorrcSelectedText()
            {
                List<string> l = TorrcLines();
                if (torrcLine < 0 || torrcLine >= l.Count) return "";
                int a, b;
                TorrcSelRange(l[torrcLine], out a, out b);
                return l[torrcLine].Substring(a, b - a);
            }

            private void OnMouseDoubleClickAll(object s, MouseEventArgs e)
            {
                if (page != Page.Torrc) return;
                if (!rcTorrcBox.Contains(e.Location)) return;
                int lh = 13;
                int idx = (e.Location.Y - (rcTorrcBox.Y + 6) + torrcScrollY) / lh;
                TorrcBeginLine(idx);
                Invalidate();
            }

            private bool TorrcKeyDown(KeyEventArgs e)
            {
                // Ctrl+S always saves, whether or not a line is focused.
                if (e.Control && e.KeyCode == Keys.S)
                {
                    SaveTorrc();
                    return true;
                }
                if (torrcLine < 0) return false;
                List<string> l = TorrcLines();
                if (torrcLine >= l.Count) { torrcLine = -1; return false; }
                string line = l[torrcLine];

                if (e.Control && e.KeyCode == Keys.A)
                {
                    torrcAnchor = 0;
                    torrcCaret = line.Length;
                    Invalidate();
                    return true;
                }
                if (e.Control && e.KeyCode == Keys.C)
                {
                    string t = TorrcSelectedText();
                    if (t.Length > 0) try { Clipboard.SetText(t); } catch { }
                    return true;
                }
                if (e.Control && e.KeyCode == Keys.X)
                {
                    string t = TorrcSelectedText();
                    if (t.Length > 0)
                    {
                        try { Clipboard.SetText(t); } catch { }
                        TorrcReplaceSel("");
                    }
                    return true;
                }
                if (e.Control && e.KeyCode == Keys.V)
                {
                    string t = null;
                    try { if (Clipboard.ContainsText()) t = Clipboard.GetText(); }
                    catch { }
                    if (t != null)
                        TorrcReplaceSel(t.Replace("\r\n", "\n").Replace("\n", " ").Replace("\r", " "));
                    return true;
                }

                switch (e.KeyCode)
                {
                    case Keys.Escape:
                        torrcLine = -1;
                        torrcAnchor = -1;
                        Invalidate();
                        return true;
                    case Keys.Enter:
                    {
                        string ind = "";
                        foreach (char ch in line)
                        {
                            if (ch != ' ' && ch != '\t') break;
                            ind += ch;
                        }
                        l.Insert(torrcLine + 1, ind);
                        torrcLine++;
                        torrcCaret = ind.Length;
                        torrcAnchor = -1;
                        SetTorrcLines(l);
                        ScrollTorrcToLine(torrcLine);
                        return true;
                    }
                    case Keys.Back:
                        TorrcDeleteSel();
                        return true;
                    case Keys.Delete:
                    {
                        int a, b;
                        TorrcSelRange(line, out a, out b);
                        if (b < line.Length) TorrcReplaceSel("");
                        return true;
                    }
                    case Keys.Left:
                        torrcCaret = Math.Max(0, torrcCaret - 1);
                        if (!e.Shift) torrcAnchor = -1;
                        Invalidate();
                        return true;
                    case Keys.Right:
                        torrcCaret = Math.Min(line.Length, torrcCaret + 1);
                        if (!e.Shift) torrcAnchor = -1;
                        Invalidate();
                        return true;
                    case Keys.Home:
                        torrcCaret = 0;
                        if (!e.Shift) torrcAnchor = -1;
                        Invalidate();
                        return true;
                    case Keys.End:
                        torrcCaret = line.Length;
                        if (!e.Shift) torrcAnchor = -1;
                        Invalidate();
                        return true;
                    case Keys.Up:
                        if (torrcLine > 0)
                        {
                            torrcLine--;
                            torrcCaret = Math.Min(torrcCaret, TorrcLines()[torrcLine].Length);
                            torrcAnchor = -1;
                            ScrollTorrcToLine(torrcLine);
                        }
                        return true;
                    case Keys.Down:
                        if (torrcLine < l.Count - 1)
                        {
                            torrcLine++;
                            torrcCaret = Math.Min(torrcCaret, TorrcLines()[torrcLine].Length);
                            torrcAnchor = -1;
                            ScrollTorrcToLine(torrcLine);
                        }
                        return true;
                    default:
                        break;
                }

                // Alt+Up / Alt+Down reorder the focused line, which is how a
                // torrc flag usually gets moved.
                if (e.Alt && e.KeyCode == Keys.Up) { TorrcMoveLine(-1); return true; }
                if (e.Alt && e.KeyCode == Keys.Down) { TorrcMoveLine(1); return true; }
                return false;
            }

            private void OnKeyPressAll(object s, KeyPressEventArgs e)
            {
                if (page != Page.Torrc || torrcLine < 0) return;
                if ((e.KeyChar < 32) || e.KeyChar == 127) return;   // handled by KeyDown
                if ((ModifierKeys & (Keys.Control | Keys.Alt)) != 0) return;
                TorrcReplaceSel(e.KeyChar.ToString());
                e.Handled = true;
            }

            // ---- torrc template I/O -----------------------------------------
            // RESET restores data\torrc.template.default, which the build ships
            // alongside the working copy so a user edit never destroys the
            // original.
            private static string TorrcDefaultFile()
            {
                return Path.Combine(DataDir, "torrc.template.default");
            }

            private string ReadTorrcTemplate()
            {
                try
                {
                    if (File.Exists(TorrcTemplate))
                        return File.ReadAllText(TorrcTemplate);
                }
                catch { }
                return "";
            }

            private void SaveTorrc()
            {
                try
                {
                    File.WriteAllText(TorrcTemplate, torrcBuf, new UTF8Encoding(false));
                    torrcDirty = false;
                    FlashMessage("torrc saved", false);
                    LogLine("[ui] torrc template saved");
                }
                catch (Exception ex)
                {
                    FlashMessage("torrc save failed: " + ex.Message);
                }
                Invalidate();
            }

            private void ResetTorrc()
            {
                RunBg(delegate
                {
                    string txt = "";
                    try
                    {
                        if (File.Exists(TorrcDefaultFile()))
                            txt = File.ReadAllText(TorrcDefaultFile());
                        else if (File.Exists(TorrcTemplate))
                            txt = File.ReadAllText(TorrcTemplate);
                    }
                    catch { }
                    bool same = txt.Length > 0 && txt == ReadTorrcTemplate();
                    UiInvokeDelegate(delegate
                    {
                        torrcBuf = txt;
                        torrcDirty = false;
                        Invalidate();
                        FlashMessage(same ? "already the shipped default" : "torrc reset", false);
                    });
                });
            }

            // ---- bridge store ------------------------------------------------
            private string BridgeCardSummary()
            {
                if (bridgeError.Length > 0) return "update failed";
                if (bridgeChecked == DateTime.MinValue) return "tap to check";
                string ago = RelativeTime(bridgeChecked);
                return bridgeVanilla + "/" + bridgeObfs4 + "/" + bridgeWebtunnel + " · " + ago;
            }

            private static string RelativeTime(DateTime t)
            {
                if (t == DateTime.MinValue) return "never";
                double s = (DateTime.Now - t).TotalSeconds;
                if (s < 90) return "just now";
                if (s < 5400) return ((int)(s / 60)) + "m ago";
                if (s < 172800) return ((int)(s / 3600)) + "h ago";
                return ((int)(s / 86400)) + "d ago";
            }

            private void UpdateBridgeCard(bool refresh)
            {
                int v = 0, o = 0, w = 0;
                string err = "";
                try
                {
                    // Only an explicit tap downloads; opening Settings just
                    // counts whatever is already on disk.
                    if (refresh) UpdateBridges();
                    foreach (string mf in new string[] { "vanilla", "obfs4", "webtunnel" })
                    {
                        int n = 0;
                        try
                        {
                            string[] files = Directory.Exists(BridgesDir)
                                ? Directory.GetFiles(BridgesDir, "*" + mf + "*") : new string[0];
                            foreach (string bf in files)
                            {
                                foreach (string ln in File.ReadAllLines(bf))
                                {
                                    if (ln.Trim().Length == 0) continue;
                                    if (ln.TrimStart().StartsWith("#")) continue;
                                    n++;
                                }
                            }
                        }
                        catch { }
                        if (mf == "vanilla") v = n;
                        else if (mf == "obfs4") o = n;
                        else w = n;
                    }
                }
                catch (Exception ex) { err = ex.Message; }

                string ferr = err;
                UiInvokeDelegate(delegate
                {
                    bridgeVanilla = v; bridgeObfs4 = o; bridgeWebtunnel = w;
                    bridgeError = ferr;
                    bridgeChecked = DateTime.UtcNow;
                    Invalidate();
                });
            }

            // ---- navigation between pages ----------------------------------
// ---- drawer actions --------------------------------------------
            private void OpenDrawer()
            {
                CancelEdit();
                page = Page.Drawer;
                drawerScrollY = 0;
                // The tables ship with the build, so this is a file read and it
                // refines the picker in place rather than gating the drawer.
                LoadGeoTables();
                LayoutPass();
                Invalidate();
            }

            private void CloseDrawer()
            {
                CancelEdit();
                page = Page.Main;
                drawerScrollY = 0;
                LayoutPass();
                Invalidate();
            }

            /// <summary>
            /// Opening a section closes the other one. Android lets both be open
            /// at once, but its drawer is a full screen of cards; here the two
            /// bodies are hundreds of rows in a 400px window, so keeping both
            /// open would push the second heading off the bottom.
            /// </summary>
            private void ToggleDrawerSection(bool location)
            {
                CancelEdit();
                if (location)
                {
                    drawerShowLocation = !drawerShowLocation;
                }
                else
                {
                    drawerShowAdvanced = !drawerShowAdvanced;
                    if (drawerShowAdvanced) RunBg(delegate { UpdateBridgeCard(false); });
                }
                drawerScrollY = 0;
                LayoutPass();
                Invalidate();
            }

            private void ToggleRestCountries()
            {
                // Opening the group on a pick keeps the chosen country in
                // view; hiding it again must not throw that pick away, so the
                // toggle only collapses the list.
                drawerShowAllCountries = !drawerShowAllCountries;
                LayoutPass();
                Invalidate();
            }

            private string CountryName(string code)
            {
                string nm;
                if (exitNames.TryGetValue(code, out nm)) return nm;
                foreach (Country c in drawerCountries)
                    if (c.Code == code) { exitNames[code] = c.Name; return c.Name; }
                return code;
            }

            private void OpenRepo()
            {
                try
                {
                    Process.Start(new ProcessStartInfo(
                        "https://github.com/Delta-Kronecker/Delta-Tor"));
                }
                catch { }
            }
            private void GoBack()
            {
                CancelEdit();
                // The drawer replaced the Settings page, so Log and Torrc go
                // back to it rather than to a page that is gone.
                if (page == Page.Log || page == Page.Torrc) page = Page.Drawer;
                else if (page == Page.Drawer) { page = Page.Main; drawerScrollY = 0; }
                LayoutPass();
                Invalidate();
            }

            private void OpenLog()
            {
                CancelEdit();
                page = Page.Log;
                logScrollY = 0;
                LayoutPass();
                Invalidate();
            }

            private void OpenTorrc()
            {
                CancelEdit();
                page = Page.Torrc;
                torrcDirty = false;
                RunBg(delegate
                {
                    string txt = ReadTorrcTemplate();
                    UiInvokeDelegate(delegate
                    {
                        torrcBuf = txt;
                        torrcDirty = false;
                        Invalidate();
                    });
                });
                LayoutPass();
                Invalidate();
            }

            // ---- log page ---------------------------------------------------
            private List<string> UiLogSnapshot()
            {
                lock (uiLogLock)
                    return new List<string>(uiLogBuffer);
            }

            private void CopyLogToClipboard()
            {
                List<string> lines = UiLogSnapshot();
                try
                {
                    string all = string.Join(Environment.NewLine, lines.ToArray());
                    if (all.Length == 0) all = "(no log lines yet)";
                    Clipboard.SetText(all);
                    FlashMessage("log copied", false);
                }
                catch
                {
                    try { FlashMessage("clipboard unavailable"); } catch { }
                }
            }

            private void PaintLog(Graphics g)
            {
                bool hovBack = hoverId == 40;
                TextRenderer.DrawText(g, hovBack ? "‹ BACK" : "‹ Back", Theme.Body(),
                    rcBack, hovBack ? Theme.Text : Theme.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g, "CONNECTION LOG", Theme.H2(),
                    new Rectangle(0, 4, ClientSize.Width, 28), Theme.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

                bool hovCopy = hoverId == 60;
                Theme.PillGradient(g, rcLogCopy,
                    hovCopy ? Theme.SurfaceLight : Theme.SurfaceAlt,
                    Theme.Surface, Theme.Border);
                TextRenderer.DrawText(g, "COPY", Theme.Caption(), rcLogCopy,
                    hovCopy ? Theme.Text : Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

                int top = 78;
                int bottom = ClientSize.Height - 8;
                List<string> lines = UiLogSnapshot();
                if (lines.Count == 0)
                {
                    TextRenderer.DrawText(g, "No log lines captured yet. Start a connection.", Theme.Small(),
                        new Rectangle(20, top + 12, ClientSize.Width - 40, 40), Theme.Muted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
                    return;
                }

                Font f = Theme.Mono();
                int lh = 13;
                Region prev = g.Clip;
                g.SetClip(new Rectangle(0, top, ClientSize.Width, bottom - top));
                int y = top - logScrollY;
                using (SolidBrush b = new SolidBrush(Theme.Bg))
                    g.FillRectangle(b, 12, top - logScrollY,
                        ClientSize.Width - 24, lines.Count * lh + 12);
                for (int i = 0; i < lines.Count; i++)
                {
                    int ly = y + i * lh;
                    if (ly + lh < top || ly > bottom) continue;
                    TextRenderer.DrawText(g, lines[i], f,
                        new Rectangle(18, ly, ClientSize.Width - 36, lh),
                        LogLineColor(lines[i]),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }
                g.Clip = prev;
            }

            private static Color LogLineColor(string s)
            {
                if (s == null) return Theme.Muted;
                if (s.Contains("[x]") || s.Contains("ERROR") || s.Contains("failed"))
                    return Theme.Red;
                if (s.Contains("[!]") || s.Contains("WARN")) return Theme.Amber;
                return Theme.Muted;
            }

            // ---- torrc editor page -------------------------------------------
            private void PaintTorrc(Graphics g)
            {
                bool hovBack = hoverId == 40;
                TextRenderer.DrawText(g, hovBack ? "‹ BACK" : "‹ Back", Theme.Body(),
                    rcBack, hovBack ? Theme.Text : Theme.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g, "TORRC TEMPLATE", Theme.H2(),
                    new Rectangle(0, 4, ClientSize.Width, 28), Theme.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

                DrawActionButton(g, rcTorrcReset, "RESET", hoverId == 62, false);
                DrawActionButton(g, rcTorrcSave,
                    torrcDirty ? "SAVE •" : "SAVED ✓", hoverId == 61, true);

                TextRenderer.DrawText(g,
                    "The full torrc, editable here · applied on next connect",
                    Theme.Small(), new Rectangle(24, rcTorrcBox.Y - 20, ClientSize.Width - 48, 18),
                    Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

                using (GraphicsPath p = Theme.RoundRect(rcTorrcBox, 10))
                {
                    Theme.FillGradientPath(g, p, Theme.Surface, Theme.Bg);
                    using (Pen pen = new Pen(Theme.Border, 1f)) g.DrawPath(pen, p);
                }
                Region prev = g.Clip;
                Rectangle inner = Rectangle.Inflate(rcTorrcBox, -8, -6);
                g.SetClip(inner);
                string[] ls = (torrcBuf ?? "").Replace("\r\n", "\n").Split('\n');
                int lh = 13;
                using (SolidBrush b = new SolidBrush(Theme.SurfaceAlt))
                    g.FillRectangle(b, inner.X, inner.Y, 92, inner.Height);
                int first = Math.Max(0, torrcScrollY / lh);
                Font mf = Theme.Mono();
                for (int i = first; i < ls.Length; i++)
                {
                    int ly = inner.Y + i * lh - torrcScrollY;
                    if (ly > inner.Bottom) break;
                    if (i == torrcLine)
                    {
                        int ca, cb;
                        TorrcSelRange(ls[i], out ca, out cb);
                        int selX = TextRenderer.MeasureText(ls[i].Substring(0, ca), mf).Width;
                        int selW = TextRenderer.MeasureText(
                            ls[i].Substring(ca, cb - ca), mf).Width;
                        using (SolidBrush sb = new SolidBrush(Theme.Accent))
                            g.FillRectangle(sb, inner.X + 98 + selX, ly, selW, lh);
                        using (SolidBrush sb = new SolidBrush(Theme.SurfaceAlt))
                            g.FillRectangle(sb, inner.X + 4, ly, 92, lh);
                        if (caretOn)
                        {
                            using (SolidBrush cb2 = new SolidBrush(Theme.Text))
                                g.FillRectangle(cb2, inner.X + 98 + selX, ly, 1, lh);
                        }
                    }
                    TextRenderer.DrawText(g, (i + 1).ToString(), Theme.Small(),
                        new Rectangle(inner.X + 4, ly, 40, lh),
                        i == torrcLine ? Theme.Text : Theme.Muted,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.NoPrefix);
                    TextRenderer.DrawText(g, ls[i], mf,
                        new Rectangle(inner.X + 98, ly, inner.Width - 104, lh),
                        Theme.Text,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }
                g.Clip = prev;
            }

            private void DrawActionButton(Graphics g, Rectangle r, string text,
                bool hovered, bool accent)
            {
                Theme.PillGradient(g, r,
                    accent ? (hovered ? Theme.Accent : Theme.AccentSoft) :
                             (hovered ? Theme.SurfaceLight : Theme.SurfaceAlt),
                    accent ? (hovered ? Theme.AccentSoft : Theme.AccentDark) : Theme.Surface,
                    accent ? Theme.Accent : Theme.Border);
                TextRenderer.DrawText(g, text, Theme.Caption(), r,
                    accent ? Theme.Text : (hovered ? Theme.Text : Theme.Muted),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

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
                int notch = e.Delta > 0 ? -1 : 1;
                if (page == Page.Log)
                {
                    int logMax = Math.Max(0,
                        UiLogSnapshot().Count * 13 - (ClientSize.Height - 96));
                    int logNext = Math.Max(0, Math.Min(logMax, logScrollY - notch * 78));
                    if (logNext != logScrollY) { logScrollY = logNext; Invalidate(); }
                    return;
                }
                if (page == Page.Torrc)
                {
                    string[] ls = (torrcBuf ?? "").Replace("\r\n", "\n").Split('\n');
                    int tMax = Math.Max(0, ls.Length * 13 - rcTorrcBox.Height);
                    int tNext = Math.Max(0, Math.Min(tMax, torrcScrollY - notch * 39));
                    if (tNext != torrcScrollY) { torrcScrollY = tNext; Invalidate(); }
                    return;
                }
                if (page != Page.Drawer) return;
                int newScrollY = Math.Max(0, Math.Min(DrawerMaxScroll(),
                    drawerScrollY - notch * 56));
                if (newScrollY != drawerScrollY)
                {
                    drawerScrollY = newScrollY;
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
                    case 21: ApplyTunToggle(); break;
                    case 40: GoBack(); break;                   // Log / torrc back
                    case 30: OpenDrawer(); break;
                    case 31: CloseDrawer(); break;
                    case 32: ClearExitCountries(); break;
                    case 34: ToggleRestCountries(); break;
                    case 35: OpenRepo(); break;
                    case 36: ClearExitCountries(); break;
                    case 50: OpenReleases(); break;
                    case 60: CopyLogToClipboard(); break;
                    case 61: SaveTorrc(); break;
                    case 62: ResetTorrc(); break;
                    case 3000: ToggleDrawerSection(true); break;
                    case 3001: ToggleDrawerSection(false); break;
                    default:
                        if (page != Page.Drawer) break;
                        if (h >= CountryHitBase && h < CountryHitBase + 3000)
                        {
                            int cidx = h - CountryHitBase;
                            if (cidx >= 0 && cidx < countryRowCode.Count)
                                ToggleExitCountry(countryRowCode[cidx], CountryName(countryRowCode[cidx]));
                            break;
                        }
                        if (h < 100) break;
                        if (h >= 300)
                        {
                            int arow = h - 300;
                            if (arow == RowTorrc) OpenTorrc();
                            else if (arow == RowLog) OpenLog();
                            else if (arow == RowBridges) RunBg(delegate { UpdateBridgeCard(true); });
                        }
                        else if (h >= 200 && h < 300)
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
                if (page == Page.Torrc && TorrcKeyDown(e)) { e.Handled = true; return; }
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
                if (e.KeyCode == Keys.Escape &&
                    (page == Page.Drawer || page == Page.Log || page == Page.Torrc))
                {
                    GoBack();
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
                bool inert = state == RunState.Stopping;
                if (page == Page.Main)
                {
                    // While stopping the ring and the two toggles are dead: no
                    // hover, no hand cursor, no click. Mirrors Android's
                    // `enabled = !state.stopping` on the ring button.
                    if (!inert)
                    {
                        if (rcPower.Contains(p)) return 5;
                        if (rcProxyBtn.Width > 0 && rcProxyBtn.Contains(p)) return 20;
                        if (rcTunBtn.Contains(p)) return 21;
                    }
                    if (rcHamburger.Contains(p)) return 30;
                    if (showUpdateBanner && updateVersion.Length > 0 &&
                        rcUpdateBtn.Contains(p)) return 50;
                }
                else if (page == Page.Drawer)
                {
                    // The headings and the GITHUB pill are the only things that
                    // answer taps above and below the scrolling body, so they
                    // are tested before the rows.
                    if (rcDrawerClose.Contains(p)) return 31;
                    if (rcDrawerGithub.Contains(p)) return 35;
                    if (rcDrawerLocation.Contains(p)) return 3000;
                    if (rcDrawerWarn.Contains(p) && p.X >= rcDrawerWarn.Right - 96) return 32;
                    if (rcDrawerRestHeader.Height > 0 &&
                        rcDrawerRestHeader.Contains(p)) return 34;
                    if (rcDrawerAdvanced.Contains(p)) return 3001;
                    // A country row reports its index, so the click handler can
                    // name the country without searching the list again.
                    if (rcRowAny.Contains(p)) return 36;
                    for (int i = 0; i < rcCountryRow.Count; i++)
                        if (rcCountryRow[i].Contains(p)) return CountryHitBase + i;
                    if (drawerShowAdvanced)
                    {
                        int rowCount = SettingLabels.Length;
                        for (int i = 0; i < rowCount; i++)
                        {
                            if (RowIsAction(i))
                            {
                                if (rcRowBody[i].Contains(p)) return 300 + i;
                                continue;
                            }
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
                }
                else if (page == Page.Log)
                {
                    if (rcBack.Contains(p)) return 40;
                    if (rcLogCopy.Contains(p)) return 60;
                }
                else
                {
                    if (rcBack.Contains(p)) return 40;
                    if (rcTorrcSave.Contains(p)) return 61;
                    if (rcTorrcReset.Contains(p)) return 62;
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

            private readonly List<SimpleAction> uiQueue = new List<SimpleAction>();

            private void UiInvokeDelegate(SimpleAction d)
            {
                if (!IsHandleCreated) { lock (uiQueue) uiQueue.Add(d); return; }
                try { BeginInvoke(d); } catch { lock (uiQueue) uiQueue.Add(d); }
            }

            private void FlushUiQueue()
            {
                List<SimpleAction> pending;
                lock (uiQueue) { pending = new List<SimpleAction>(uiQueue); uiQueue.Clear(); }
                foreach (SimpleAction a in pending) BeginInvoke(a);
            }

            private void ApplyProxyToggle(bool want)
            {
                // Mirrors Android's guard chain: nothing may turn the system
                // proxy on while tearing down or with no session up.
                if (state == RunState.Stopping) return;
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

            // Toggle whole-system TUN (zeptun). Starting it spawns the elevated
            // supervisor (UAC prompt); stopping it is just the stop file, which
            // the elevated keeper picks up within a few seconds.
            private void ApplyTunToggle()
            {
                if (state != RunState.Connected && state != RunState.Restarting)
                {
                    FlashMessage("connect first");
                    Invalidate();
                    return;
                }
                bool want = !TunOnNow();
                if (want && (TunOnNow() || TunPending())) return;
                TunToggle(want);
                if (want)
                    FlashMessage("TUN on - allow the admin prompt", false);
                else
                {
                    tunLocalPending = true;
                    FlashMessage("TUN off", false);
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
                // While stopping, the ring label slot must stay empty and the
                // state word must stay STOPPING, so transient messages are
                // dropped instead of hijacking the surface.
                if (state == RunState.Stopping) return;
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

// ---- exit countries + geo tables -------------------------------
            // Android keeps these two files as assets and this client keeps
            // them next to tor.exe; the format is the same TSV, so the tables
            // are shared rather than re-derived.
            private struct Country
            {
                public string Code;
                public string Name;
                public ExitCap Cap;
                public bool HasCap;
            }

            private struct ExitCap
            {
                public int Exits;
                public float Weight;
            }

            // Picks are read once at construction so the drawer's summary line
            // is right on first open, and written whenever the user taps.
            private void LoadExitSelection()
            {
                try
                {
                    if (!File.Exists(ExitNodesFile)) return;
                    foreach (string rawCc in File.ReadAllText(ExitNodesFile).Split(','))
                    {
                        string cc = rawCc.Trim().ToUpperInvariant();
                        if (cc.Length != 2) continue;
                        bool alpha = true;
                        foreach (char ch in cc)
                            if (ch < 'A' || ch > 'Z') { alpha = false; break; }
                        if (!alpha || exitCodes.Contains(cc)) continue;
                        exitCodes.Add(cc);
                    }
                }
                catch { }
            }

            private void SaveExitSelection()
            {
                try
                {
                    File.WriteAllText(ExitNodesFile,
                        string.Join(",", exitCodes.ToArray()), new UTF8Encoding(false));
                }
                catch
                {
                    try { FlashMessage("could not save location"); } catch { }
                }
            }

            /// <summary>Add or remove one country. Order of selection is kept.</summary>
            private void ToggleExitCountry(string code, string name)
            {
                string cc = code.ToUpperInvariant();
                if (exitCodes.Remove(cc)) exitNames.Remove(cc);
                else { exitCodes.Add(cc); exitNames[cc] = name; }
                SaveExitSelection();
                Invalidate();
            }

            private void ClearExitCountries()
            {
                exitCodes.Clear();
                exitNames.Clear();
                SaveExitSelection();
                Invalidate();
            }

            /// <summary>
            /// Reads countries.tsv and exit-capacity.tsv once. Both are static
            /// tables shipped with the build, so this never hits the network:
            /// a country list is not worth a round trip and a cache expiry on
            /// every launch, and the numbers move on the scale of months.
            /// </summary>
            private void LoadGeoTables()
            {
                if (geoipLoaded) return;
                geoipLoaded = true;
                RunBg(delegate
                {
                    var countries = new List<Country>();
                    var caps = new Dictionary<string, ExitCap>(StringComparer.OrdinalIgnoreCase);
                    string err = "";
                    try
                    {
                        foreach (string line in ReadTsvLines(Path.Combine(DataDir, "countries.tsv")))
                        {
                            string[] p = line.Split('\t');
                            if (p.Length < 2) continue;
                            var c = new Country();
                            c.Code = p[0].Trim().ToUpperInvariant();
                            c.Name = p[1].Trim();
                            if (c.Code.Length != 2 || c.Name.Length == 0) continue;
                            countries.Add(c);
                        }
                        foreach (string line in ReadTsvLines(Path.Combine(DataDir, "exit-capacity.tsv")))
                        {
                            string[] p = line.Split('\t');
                            if (p.Length < 4) continue;
                            int exits;
                            float weight;
                            if (!int.TryParse(p[2].Trim(), out exits)) continue;
                            if (!float.TryParse(p[3].Trim(),
                                    NumberStyles.Float, CultureInfo.InvariantCulture, out weight))
                                continue;
                            var cap = new ExitCap();
                            cap.Exits = exits;
                            cap.Weight = weight;
                            caps[p[0].Trim().ToUpperInvariant()] = cap;
                        }
                    }
                    catch (Exception ex) { err = ex.Message; }
                    UiInvokeDelegate(delegate
                    {
                        // The picker order is Tor's own answer rather than a
                        // curated list: countries with exits first, most exit
                        // bandwidth first, everything else alphabetical. Past
                        // the cut the numbers stop arguing with each other,
                        // so the remainder is one tap away instead of padding
                        // the list with places that could never be picked.
                        for (int i = 0; i < countries.Count; i++)
                        {
                            Country c = countries[i];
                            ExitCap cap;
                            if (caps.TryGetValue(c.Code, out cap)) { c.Cap = cap; c.HasCap = true; }
                            countries[i] = c;
                        }
                        countries.Sort(delegate(Country a, Country b)
                        {
                            if (a.HasCap != b.HasCap) return a.HasCap ? -1 : 1;
                            if (a.HasCap && b.HasCap && a.Cap.Weight != b.Cap.Weight)
                                return b.Cap.Weight.CompareTo(a.Cap.Weight);
                            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                        });
                        drawerCountries.Clear();
                        drawerCountries.AddRange(countries);
                        drawerCapacity.Clear();
                        foreach (var kv in caps) drawerCapacity[kv.Key] = kv.Value;
                        // Names for the picks: from the table when it has them,
                        // so the summary line can never show a bare code.
                        foreach (string cc in exitCodes)
                            if (!exitNames.ContainsKey(cc))
                                foreach (Country c in drawerCountries)
                                    if (c.Code == cc) { exitNames[cc] = c.Name; break; }
                        geoipError = err;
                        LayoutPass();
                    });
                });
            }

            private static IEnumerable<string> ReadTsvLines(string path)
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    string t = raw.TrimEnd('\r');
                    if (t.Length == 0 || t.StartsWith("#")) continue;
                    yield return t;
                }
            }

            /// <summary>
            /// The countries with exits, and the remainder. The cut is at 25,
            /// not at every country that has an exit: the top 25 already hold
            /// about 99% of the exit bandwidth, so ranks past that are
            /// competing over the last fraction of a percent.
            /// </summary>
            private List<Country> WithExitCountries()
            {
                var list = new List<Country>();
                foreach (Country c in drawerCountries) if (c.HasCap) list.Add(c);
                return list;
            }

            private List<Country> WithoutExitCountries()
            {
                var list = new List<Country>();
                foreach (Country c in drawerCountries) if (!c.HasCap) list.Add(c);
                return list;
            }

            private bool RestHoldsSelection()
            {
                foreach (string cc in exitCodes)
                    foreach (Country c in drawerCountries)
                        if (!c.HasCap && c.Code == cc) return true;
                return false;
            }

            /// <summary>The one-line summary the LOCATION heading carries.</summary>
            private string LocationSummary()
            {
                if (exitCodes.Count == 0) return "Any location \u00b7 default";
                if (exitCodes.Count == 1)
                {
                    string cc = exitCodes[0];
                    string nm;
                    return FlagEmoji(cc) + "  " + (exitNames.TryGetValue(cc, out nm) ? nm : cc);
                }
                return exitCodes.Count + " countries selected";
            }

            // Regional indicator symbols, same as Android's flagEmoji().
            private static string FlagEmoji(string code)
            {
                if (code == null || code.Length != 2) return "\U0001F310";
                StringBuilder sb = new StringBuilder();
                foreach (char c in code.ToUpperInvariant())
                    sb.Append(char.ConvertFromUtf32(0x1F1E6 + (c - 'A')));
                return sb.ToString();
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
                // Disconnect() raises autoAbort to cancel an in-flight race, and
                // AutoRace() only clears it for the auto mode. Clear it here as
                // well, or every single-mode connect after the first stop would
                // kill itself on the new stop guards.
                autoAbort = false;
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
                        // `aborted` is StartTorAndWait's own "user stopped
                        // during bootstrap" signal. The console callers all read
                        // it; the UI used to write it and drop it on the floor.
                        if (!stoppingBusy && !autoAbort)
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
                // Everything below brings the session UP. If the user asked to
                // stop while this bootstrap was still running, tear the fresh
                // process down again instead of stomp-ing STOPPING.
                if (stoppingBusy || autoAbort)
                {
                    try { proc.Kill(); } catch { }
                    proc = null;
                    torProc = null;
                    return;
                }
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
                // A stop that landed inside the bring-up window still wins. Undo
                // exactly what was armed above, otherwise the keeper, watchdog
                // and circuit watcher outlive a tor that is already gone.
                if (stoppingBusy || autoAbort)
                {
                    watchdogStop = true;
                    circuitWatchStop = true;
                    circuitWatchWarmup = false;
                    StopKeepAlive();
                    try { if (proc != null) proc.Kill(); } catch { }
                    torProc = null;
                    sessionBusy = false;
                    return;
                }
                if (autoProxyEnabled)
                    UiInvokeDelegate(delegate { ApplyProxyToggle(true); });
                sessionBusy = false;
                UiInvokeDelegate(delegate
                {
                    bootPct = 100;
                    fallbackPending = false;
                    ResetStats();
                    connectedAt = DateTime.UtcNow;
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
                // Kill tor FIRST. It used to happen after keep-alive teardown,
                // TUN release and the proxy reset, which left a window where a
                // still-starting tor survived the stop and could still reach
                // 100% and stomp STOPPING with CONNECTED.
                try { if (torProc != null) torProc.Kill(); } catch { }
                torProc = null;
                StopKeepAlive();
                TunRequestOff();    // leaves the elevated keeper to tear down
                tunLocalPending = false;
                if (autoProxyEnabled && ProxyIsOurs()) SetSystemProxy(false);
                LogLine("tor stopped (" + why + ")");
                ResetStats();
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
                            connectedAt = DateTime.MinValue;
                            exitCode = ""; exitName = ""; exitLocating = false;
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

                // Reflect TUN (zeptun) changes and helper errors on the
                // main-page pill without blocking.
                if (state != RunState.Stopping)
                {
                    bool ton = TunOnNow();
                    bool tpen = TunPending();
                    string tmsg = TunErrorNow();
                    if (tmsg.Length > 0 && tmsg != lastTunErrorShown)
                    {
                        lastTunErrorShown = tmsg;
                        tunLocalPending = false;
                        FlashMessage("TUN: " + tmsg);
                    }
                    else if (tmsg.Length == 0) lastTunErrorShown = "";
                    if (ton != tunShownOn || tpen != tunShownPending)
                    {
                        tunShownOn = ton;
                        tunShownPending = tpen;
                        if (ton) tunLocalPending = false;
                        Invalidate();
                    }
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
                                // SessionWorker already flips to CONNECTED on
                                // its own; only clear the busy flags, and never
                                // if a stop is in flight.
                                UiInvokeDelegate(delegate
                                {
                                    if (!stoppingBusy && !autoAbort)
                                        SetState(RunState.Connected);
                                });
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

                SampleStats();
                UpdateExitLookup();
            }

            // ---- live stats --------------------------------------------------
            // The TUN adapter carries the counters. Proxy-only sessions have no
            // adapter, so the panel shows "--" exactly like Android does when
            // the tunnel itself is not up.
            private const string TunAdapterName = "DeltaTor";

            private void ResetStats()
            {
                rxSpeed = 0; txSpeed = 0;
                rxBytes = 0; txBytes = 0;
                exitCode = ""; exitName = ""; exitLocating = false;
                lastNetSample = DateTime.UtcNow;
                long rx, tx;
                if (WindowsNetStats.TryGetCounters(TunAdapterName, out rx, out tx))
                {
                    // Adapter totals are cumulative since the adapter came up;
                    // the session total is the delta from this baseline.
                    rxSessionBase = rx;
                    txSessionBase = tx;
                    lastRxSample = rx;
                    lastTxSample = tx;
                }
                else
                {
                    rxSessionBase = 0;
                    txSessionBase = 0;
                    lastRxSample = 0;
                    lastTxSample = 0;
                }
            }

            private void SampleStats()
            {
                if (state != RunState.Connected && state != RunState.Restarting)
                    return;
                DateTime now = DateTime.UtcNow;
                if ((now - lastNetSample).TotalMilliseconds < 1000) return;
                double dt = (now - lastNetSample).TotalSeconds;
                lastNetSample = now;

                if (!TunOnNow())
                {
                    rxSpeed = 0; txSpeed = 0;
                    return;
                }

                long rx, tx;
                if (!WindowsNetStats.TryGetCounters(TunAdapterName, out rx, out tx))
                {
                    rxSpeed = 0; txSpeed = 0;
                    return;
                }

                rxBytes = Math.Max(0, rx - rxSessionBase);
                txBytes = Math.Max(0, tx - txSessionBase);
                if (dt > 0.05)
                {
                    rxSpeed = Math.Max(0, (rx - lastRxSample) / dt);
                    txSpeed = Math.Max(0, (tx - lastTxSample) / dt);
                }
                lastRxSample = rx;
                lastTxSample = tx;
                Invalidate();
            }

            // ---- exit node ---------------------------------------------------
            private void UpdateExitLookup()
            {
                if (state != RunState.Connected) return;
                if (exitCode.Length > 0 || exitLocating) return;
                if ((DateTime.UtcNow - lastExitLookup).TotalSeconds < 10) return;
                lastExitLookup = DateTime.UtcNow;
                exitLocating = true;
                Invalidate();
                RunBg(delegate
                {
                    string code = "";
                    try
                    {
                        List<string> cs = ControlCommand("GETINFO circuit-status");
                        if (cs != null)
                        {
                            foreach (string ln in cs)
                            {
                                if (ln.IndexOf("BUILT ", StringComparison.OrdinalIgnoreCase) < 0) continue;
                                string fp = FingerprintFrom(ln);
                                if (fp.Length == 0) continue;
                                List<string> idl = ControlCommand("GETINFO ns/id/" + fp);
                                if (idl != null)
                                    foreach (string cl in idl)
                                    {
                                        string t = cl.Trim();
                                        if (t.Length == 0 || t.StartsWith("+") || t.StartsWith("5")) continue;
                                        code = t.ToLowerInvariant();
                                        break;
                                    }
                                if (code.Length > 0) break;
                            }
                        }
                    }
                    catch { }
                    string cc = code;
                    UiInvokeDelegate(delegate
                    {
                        exitLocating = false;
                        if (cc.Length > 0)
                        {
                            exitCode = cc.ToUpperInvariant();
                            exitName = CountryNameFor(cc);
                        }
                        Invalidate();
                    });
                });
            }

            // Pulls the 40 hex relay fingerprint out of a circuit-status line.
            private static string FingerprintFrom(string line)
            {
                if (line == null) return "";
                var parts = line.Split(' ');
                foreach (string p in parts)
                {
                    string t = p.Trim();
                    if (t.Length != 40) continue;
                    bool hex = true;
                    foreach (char ch in t)
                        if (!Uri.IsHexDigit(ch)) { hex = false; break; }
                    if (hex) return t;
                }
                return "";
            }

            private static string CountryNameFor(string cc)
            {
                if (cc == null || cc.Length == 0) return "";
                switch (cc.ToLowerInvariant())
                {
                    case "ar": return "Argentina";
                    case "at": return "Austria";
                    case "au": return "Australia";
                    case "be": return "Belgium";
                    case "bg": return "Bulgaria";
                    case "br": return "Brazil";
                    case "ca": return "Canada";
                    case "ch": return "Switzerland";
                    case "cl": return "Chile";
                    case "cn": return "China";
                    case "co": return "Colombia";
                    case "cy": return "Cyprus";
                    case "cz": return "Czechia";
                    case "de": return "Germany";
                    case "dk": return "Denmark";
                    case "ee": return "Estonia";
                    case "eg": return "Egypt";
                    case "es": return "Spain";
                    case "fi": return "Finland";
                    case "fr": return "France";
                    case "gb": return "United Kingdom";
                    case "gr": return "Greece";
                    case "hk": return "Hong Kong";
                    case "hr": return "Croatia";
                    case "hu": return "Hungary";
                    case "id": return "Indonesia";
                    case "ie": return "Ireland";
                    case "il": return "Israel";
                    case "in": return "India";
                    case "ir": return "Iran";
                    case "is": return "Iceland";
                    case "it": return "Italy";
                    case "jp": return "Japan";
                    case "kg": return "Kyrgyzstan";
                    case "kp": return "North Korea";
                    case "kr": return "South Korea";
                    case "lt": return "Lithuania";
                    case "lu": return "Luxembourg";
                    case "lv": return "Latvia";
                    case "md": return "Moldova";
                    case "mx": return "Mexico";
                    case "my": return "Malaysia";
                    case "nl": return "Netherlands";
                    case "no": return "Norway";
                    case "nz": return "New Zealand";
                    case "ph": return "Philippines";
                    case "pl": return "Poland";
                    case "pt": return "Portugal";
                    case "ro": return "Romania";
                    case "rs": return "Serbia";
                    case "ru": return "Russia";
                    case "se": return "Sweden";
                    case "sg": return "Singapore";
                    case "si": return "Slovenia";
                    case "sk": return "Slovakia";
                    case "tr": return "Turkey";
                    case "tw": return "Taiwan";
                    case "ua": return "Ukraine";
                    case "us": return "United States";
                    case "uz": return "Uzbekistan";
                    case "ve": return "Venezuela";
                    case "vn": return "Vietnam";
                    case "za": return "South Africa";
                }
                return cc.ToUpperInvariant();
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
