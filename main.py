import os
import sys
import urllib.request
import tarfile
import subprocess
import threading
import winreg
import tkinter as tk
from tkinter import ttk
import re
import json
import ctypes
import shutil
import time
import socket
import ssl


def resource_path(filename):
    """Return absolute path to a bundled resource file."""
    if hasattr(sys, '_MEIPASS'):
        return os.path.join(sys._MEIPASS, filename)
    return os.path.join(os.path.dirname(os.path.abspath(__file__)), filename)


def apply_dark_titlebar(hwnd):
    try:
        dwmapi = ctypes.windll.dwmapi
        v = ctypes.c_int(1)
        dwmapi.DwmSetWindowAttribute(hwnd, 20, ctypes.byref(v), ctypes.sizeof(v))
        color = ctypes.c_int(0x2E1E1E)
        dwmapi.DwmSetWindowAttribute(hwnd, 35, ctypes.byref(color), ctypes.sizeof(color))
        text_color = ctypes.c_int(0xF4D6CD)
        dwmapi.DwmSetWindowAttribute(hwnd, 36, ctypes.byref(text_color), ctypes.sizeof(text_color))
    except Exception:
        pass


def socks5_request(host, port, path,
                   proxy_host="127.0.0.1", proxy_port=19050,
                   use_ssl=True, timeout=20):
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    s.settimeout(timeout)
    s.connect((proxy_host, proxy_port))

    s.sendall(b'\x05\x01\x00')
    if s.recv(2)[1] != 0x00:
        raise ConnectionError("SOCKS5 handshake failed")

    host_b = host.encode()
    s.sendall(b'\x05\x01\x00\x03' + bytes([len(host_b)]) + host_b +
              port.to_bytes(2, 'big'))
    resp = s.recv(10)
    if resp[1] != 0x00:
        raise ConnectionError(f"SOCKS5 connect failed (code {resp[1]})")

    if use_ssl:
        ctx = ssl.create_default_context()
        s = ctx.wrap_socket(s, server_hostname=host)

    s.sendall((f"GET {path} HTTP/1.1\r\n"
               f"Host: {host}\r\n"
               f"Connection: close\r\n"
               f"User-Agent: Mozilla/5.0\r\n\r\n").encode())

    data = b""
    while True:
        chunk = s.recv(4096)
        if not chunk:
            break
        data += chunk
    s.close()

    sep = data.find(b"\r\n\r\n")
    return data[sep + 4:].decode(errors="replace") if sep != -1 else data.decode(errors="replace")


class TorClientGUI:
    def __init__(self, root):
        self.root = root
        self.root.title("Tor Client")
        self.root.geometry("750x930")
        self.root.configure(bg="#1E1E2E")
        self.root.after(120, self._apply_titlebar)

        self._set_window_icon()

        self.tor_url = (
            "https://github.com/Delta-Kronecker/Tor-Expert-Bundle/raw/refs/heads/main/"
            "tor-expert-bundle-windows-x86_64-15.0.6.tar.gz"
        )
        self.tor_fallback_url = (
            "https://archive.torproject.org/tor-package-archive/torbrowser/15.0.6/"
            "tor-expert-bundle-windows-x86_64-15.0.6.tar.gz"
        )
        self.archive_name = "tor-expert-bundle.tar.gz"
        self.extract_dir  = "tor_custom_client"
        self.bridges_dir  = os.path.join(self.extract_dir, "bridges")

        self.bridge_data = [
            ("Tested & Active", "obfs4",     "IPv4",
             "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/obfs4_tested.txt"),
            ("Tested & Active", "webtunnel", "IPv4",
             "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/webtunnel_tested.txt"),
            ("Tested & Active", "vanilla",   "IPv4",
             "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/vanilla_tested.txt"),
            ("Fresh (72h)",     "obfs4",     "IPv4",
             "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/obfs4_72h.txt"),
            ("Fresh (72h)",     "obfs4",     "IPv6",
             "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/obfs4_ipv6_72h.txt"),
            ("Fresh (72h)",     "webtunnel", "IPv4",
             "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/webtunnel_72h.txt"),
            ("Fresh (72h)",     "webtunnel", "IPv6",
             "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/webtunnel_ipv6_72h.txt"),
            ("Fresh (72h)",     "vanilla",   "IPv4",
             "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/vanilla_72h.txt"),
            ("Fresh (72h)",     "vanilla",   "IPv6",
             "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/vanilla_ipv6_72h.txt"),
            ("Full Archive",    "obfs4",     "IPv4",
             "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/obfs4.txt"),
            ("Full Archive",    "obfs4",     "IPv6",
             "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/obfs4_ipv6.txt"),
            ("Full Archive",    "webtunnel", "IPv4",
             "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/webtunnel.txt"),
            ("Full Archive",    "webtunnel", "IPv6",
             "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/webtunnel_ipv6.txt"),
            ("Full Archive",    "vanilla",   "IPv4",
             "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/vanilla.txt"),
            ("Full Archive",    "vanilla",   "IPv6",
             "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/vanilla_ipv6.txt"),
        ]

        self.tor_process      = None
        self.tor_connected    = False
        self.connect_time     = None
        self._uptime_after_id = None

        self.status_var        = tk.StringVar(value="Status: Initializing...")
        self.proxy_var         = tk.BooleanVar()
        self.source_var        = tk.StringVar(value="Delta-Kronecker Tor-Bridges-Collector")
        self.cat_var           = tk.StringVar(value="Tested & Active")
        self.trans_var         = tk.StringVar()
        self.ip_var            = tk.StringVar(value="IPv4")
        self.progress_var      = tk.IntVar(value=0)
        self.progress_text_var = tk.StringVar(value="0%")
        self.conn_progress_var = tk.IntVar(value=0)
        self.conn_pct_var      = tk.StringVar(value="0%")
        self.stat_ip_var      = tk.StringVar(value="—")
        self.stat_country_var = tk.StringVar(value="—")
        self.stat_uptime_var  = tk.StringVar(value="—")
        self.stat_tor_var     = tk.StringVar(value="—")

        self.setup_theme()
        self.setup_ui()
        threading.Thread(target=self.auto_initialize, daemon=True).start()

    def _apply_titlebar(self):
        try:
            hwnd = ctypes.windll.user32.GetParent(self.root.winfo_id())
            if not hwnd:
                hwnd = self.root.winfo_id()
            apply_dark_titlebar(hwnd)
        except Exception:
            pass

    def _set_window_icon(self):

        ico_path = resource_path("icon.ico")
        if os.path.exists(ico_path):
            try:
                self.root.iconbitmap(default=ico_path)
                return
            except Exception:
                pass

        try:
            from tkinter import PhotoImage
            import base64, zlib

            _icon_b64 = (
                "iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAB"
                "mklEQVRYhe2Xv0oDQRDGf5dcjFFTiIVYiI2FWHgBKz6AhZWV"
                "lZWCIFiIhZWFhZWFhZWFkIsgFkIshFgIsRBiIcRCiIUQCyEW"
                "QiyEWAixEGIhxEKIhRALIRZCLIRYCLEQYiHEQoiFEAshFkIs"
                "hFgIsRBiIcRCiIUQCyEWQiyEWAixEGIhxEKIhRALIRZCLIRY"
                "CLEQYiHEQoiFEAshFkIshFgIsRBiIcRCiIUQCyEWQiyEWAix"
                "EGIhxEKIhRALIRZCLIRYCLEQYiHEQoiFEAshFkIshFgIsRBi"
                "IcRCiIUQCyEWQiyEWAixEGIhxEKIhRALIRZCLIRYCLEQYiHE"
                "QoiFEAshFkIshFgIsRBiIcRCiIUQCyEWQiyEWAixEGIhxEKI"
                "hRALIRZCLIRYCLEQYiHEQoiFEAshFkIshFgIsRBiIcRCiIUQ"
                "CyEWQiyEWAixEGIhxEKIhRALIRZCLIRYCLEQYiHEQoiFEAsh"
                "FkIshFgIsRBiIcRCiIUQCyEWQiyEWAixEGIhxEKIhRALIRZC"
                "LIRYCLEQYiHEQoiFEAshFkIshFgIsRBiIcRCiIUQCyEWQiyE"
                "WAixEGIhxEKIhRALIRZCLO8fBn8B3QAAAABJRU5ErkJggg=="
            )
            xbm_data = """
#define icon_width 16
#define icon_height 16
static char icon_bits[] = {
   0xf0,0x0f,0xfe,0x7f,0xff,0xff,0xff,0xff,
   0xff,0xff,0xfe,0x7f,0xfe,0x7f,0xfe,0x7f,
   0xfe,0x7f,0xfe,0x7f,0xfe,0x7f,0xfe,0x7f,
   0xff,0xff,0xff,0xff,0xfe,0x7f,0xf0,0x0f};
"""
            icon_img = PhotoImage(data=xbm_data, format="xbm")
            self.root.iconphoto(True, icon_img)
            self._icon_img_ref = icon_img   
        except Exception:
            pass   

    def setup_theme(self):
        s   = ttk.Style()
        s.theme_use('clam')
        BG  = "#1E1E2E";  FG = "#CDD6F4"
        ACC = "#89B4FA";  BTN = "#313244"
        DRK = "#181825"

        s.configure('.',            background=BG, foreground=FG, font=('Segoe UI', 10))
        s.configure('TLabel',       background=BG, foreground=FG)
        s.configure('TLabelframe',  background=BG, foreground=ACC, bordercolor=BTN)
        s.configure('TLabelframe.Label', background=BG, foreground=ACC,
                    font=('Segoe UI', 10, 'bold'))
        s.configure('TCombobox',    fieldbackground=BTN, background=BTN,
                    foreground=FG, borderwidth=0, arrowcolor=FG,
                    selectbackground=BTN, selectforeground=FG)
        s.map('TCombobox',
              fieldbackground=[('readonly', BTN)],
              foreground=[('readonly', FG)],
              background=[('readonly', BTN)])
        s.configure('TCheckbutton', background=BG, foreground=FG, font=('Segoe UI', 10))
        s.map('TCheckbutton', background=[('active', BG)])
        s.configure('Horizontal.TProgressbar',
                    background='#A6E3A1', troughcolor=BTN,
                    bordercolor=BG, lightcolor='#A6E3A1', darkcolor='#A6E3A1')
        s.configure('Stat.TLabel',    background=DRK, foreground="#A6ADC8",
                    font=('Segoe UI', 9))
        s.configure('StatVal.TLabel', background=DRK, foreground="#A6E3A1",
                    font=('Segoe UI', 9, 'bold'))

        self.root.option_add('*TCombobox*Listbox.background', BTN)
        self.root.option_add('*TCombobox*Listbox.foreground', FG)
        self.root.option_add('*TCombobox*Listbox.selectBackground', ACC)
        self.root.option_add('*TCombobox*Listbox.selectForeground', "#11111B")

    def setup_ui(self):
        BG  = "#1E1E2E";  FG = "#CDD6F4";  BTN = "#313244"

        tk.Button(self.root, text="📖  How to Use",
                  command=self.show_help_window,
                  bg="#45475A", fg=FG, font=('Segoe UI', 10, 'bold'),
                  relief="flat", cursor="hand2",
                  activebackground="#585B70", activeforeground=FG
                  ).pack(pady=(10, 0), fill='x', padx=40)

        tk.Label(self.root, text="Tor Client",
                 font=('Segoe UI', 18, 'bold'), bg=BG, fg="#89B4FA").pack(pady=(8, 2))

        tk.Label(self.root, textvariable=self.status_var, wraplength=650,
                 font=('Segoe UI', 10, 'italic'), bg=BG, fg="#F38BA8").pack(pady=2)

        dl = tk.Frame(self.root, bg=BG)
        dl.pack(fill='x', padx=40, pady=(5, 0))
        ttk.Label(dl, text="Download Progress:").pack(side='left')
        ttk.Label(dl, textvariable=self.progress_text_var,
                  font=('Segoe UI', 10, 'bold'), foreground="#A6E3A1").pack(side='right')
        ttk.Progressbar(self.root, variable=self.progress_var, maximum=100,
                        mode='determinate').pack(fill='x', padx=40, pady=(2, 4))

        self.update_btn = tk.Button(
            self.root, text="Update Bridges from Delta-Kronecker",
            command=self.start_download_bridges,
            bg=BTN, fg=FG, font=('Segoe UI', 10), relief="flat", cursor="hand2",
            activebackground="#45475A", activeforeground=FG)
        self.update_btn.pack(pady=4, fill='x', padx=40)

        frame = ttk.LabelFrame(self.root, text=" Bridge Configuration ")
        frame.pack(pady=6, padx=40, fill='x')

        ttk.Label(frame, text="Bridge Source:").grid(row=0, column=0, padx=15, pady=7, sticky="w")
        self.source_combo = ttk.Combobox(
            frame, textvariable=self.source_var,
            values=["Default (Built-in)", "Delta-Kronecker Tor-Bridges-Collector"],
            state="readonly")
        self.source_combo.grid(row=0, column=1, padx=15, pady=7, sticky="ew")
        self.source_combo.bind("<<ComboboxSelected>>", self.on_source_changed)

        self.cat_label = ttk.Label(frame, text="Category:")
        self.cat_label.grid(row=1, column=0, padx=15, pady=7, sticky="w")
        self.cat_combo = ttk.Combobox(
            frame, textvariable=self.cat_var,
            values=["Tested & Active", "Fresh (72h)", "Full Archive"],
            state="readonly")
        self.cat_combo.grid(row=1, column=1, padx=15, pady=7, sticky="ew")
        self.cat_combo.bind("<<ComboboxSelected>>", self.update_transports)

        ttk.Label(frame, text="Transport:").grid(row=2, column=0, padx=15, pady=7, sticky="w")
        self.trans_combo = ttk.Combobox(frame, textvariable=self.trans_var, state="readonly")
        self.trans_combo.grid(row=2, column=1, padx=15, pady=7, sticky="ew")

        ttk.Label(frame, text="IP Version:").grid(row=3, column=0, padx=15, pady=7, sticky="w")
        ttk.Combobox(frame, textvariable=self.ip_var,
                     values=["Both", "IPv4", "IPv6"], state="readonly"
                     ).grid(row=3, column=1, padx=15, pady=7, sticky="ew")

        frame.columnconfigure(1, weight=1)
        self.update_transports()
        self.on_source_changed()

        ttk.Checkbutton(self.root, text="Set System Proxy  (SOCKS5  127.0.0.1:19050)",
                        variable=self.proxy_var, command=self.toggle_proxy).pack(pady=6)

        bf = tk.Frame(self.root, bg=BG)
        bf.pack(fill='x', padx=40, pady=4)
        bf.columnconfigure(0, weight=1)
        bf.columnconfigure(1, weight=1)

        self.start_btn = tk.Button(bf, text="Start Tor", command=self.start_tor_thread,
                                   bg="#89B4FA", fg="#11111B", font=('Segoe UI', 11, 'bold'),
                                   relief="flat", cursor="hand2",
                                   activebackground="#B4BEFE", activeforeground="#11111B")
        self.start_btn.grid(row=0, column=0, padx=(0, 5), sticky="ew")

        self.stop_btn = tk.Button(bf, text="Stop Tor", command=self.stop_tor,
                                  bg=BTN, fg=FG, font=('Segoe UI', 11, 'bold'),
                                  relief="flat", cursor="hand2",
                                  activebackground="#45475A", activeforeground=FG)
        self.stop_btn.grid(row=0, column=1, padx=(5, 0), sticky="ew")

        cp = tk.Frame(self.root, bg=BG)
        cp.pack(fill='x', padx=40, pady=(10, 0))
        ttk.Label(cp, text="Connection Progress:").pack(side='left')
        ttk.Label(cp, textvariable=self.conn_pct_var,
                  font=('Segoe UI', 10, 'bold'), foreground="#A6E3A1").pack(side='right')
        ttk.Progressbar(self.root, variable=self.conn_progress_var, maximum=100,
                        mode='determinate').pack(fill='x', padx=40, pady=(2, 4))

        stats_lf = ttk.LabelFrame(self.root, text=" Connection Stats ")
        stats_lf.pack(pady=4, padx=40, fill='x')

        grid = tk.Frame(stats_lf, bg="#181825")
        grid.pack(fill='x', padx=6, pady=(6, 4))
        grid.columnconfigure(1, weight=1)
        grid.columnconfigure(3, weight=1)

        def _lbl(text, row, col):
            ttk.Label(grid, text=text, style='Stat.TLabel').grid(
                row=row, column=col, padx=(12, 3), pady=4, sticky="w")

        def _val(var, row, col):
            ttk.Label(grid, textvariable=var, style='StatVal.TLabel').grid(
                row=row, column=col, padx=(0, 12), pady=4, sticky="w")

        _lbl("Exit IP:",     0, 0);  _val(self.stat_ip_var,      0, 1)
        _lbl("Country:",     0, 2);  _val(self.stat_country_var,  0, 3)
        _lbl("Uptime:",      1, 0);  _val(self.stat_uptime_var,   1, 1)
        _lbl("Tor Status:",  1, 2);  _val(self.stat_tor_var,      1, 3)

        self.test_btn = tk.Button(
            stats_lf, text="🔍  Test Connection via Tor",
            command=self.start_test_connection,
            bg="#313244", fg=FG, font=('Segoe UI', 9, 'bold'),
            relief="flat", cursor="hand2",
            activebackground="#45475A", activeforeground=FG)
        self.test_btn.pack(padx=6, pady=(2, 8), fill='x')

        ttk.Label(self.root, text="Tor Logs:").pack(anchor='w', padx=40, pady=(8, 3))
        log_frame = tk.Frame(self.root, bg="#11111B")
        log_frame.pack(fill='both', expand=True, padx=40, pady=(0, 12))

        self.log_text = tk.Text(log_frame, font=('Consolas', 9), wrap='word',
                                state='disabled', bg="#11111B", fg="#A6E3A1",
                                bd=0, padx=10, pady=10)
        sb = ttk.Scrollbar(log_frame, command=self.log_text.yview)
        self.log_text.configure(yscrollcommand=sb.set)
        self.log_text.pack(side='left', fill='both', expand=True)
        sb.pack(side='right', fill='y')

    def show_help_window(self):
        win = tk.Toplevel(self.root)
        win.title("How to Use — Tor Client")
        win.geometry("700x590")
        win.configure(bg="#1E1E2E")
        win.resizable(False, False)
        win.after(120, lambda: apply_dark_titlebar(
            ctypes.windll.user32.GetParent(win.winfo_id()) or win.winfo_id()))
        ico_path = resource_path("icon.ico")
        if os.path.exists(ico_path):
            try:
                win.iconbitmap(default=ico_path)
            except Exception:
                pass
        elif hasattr(self, '_icon_img_ref'):
            try:
                win.iconphoto(True, self._icon_img_ref)
            except Exception:
                pass

        tk.Label(win, text="📖  How to Use — Tor Client",
                 font=('Segoe UI', 14, 'bold'), bg="#1E1E2E", fg="#89B4FA").pack(pady=(15, 5))

        tf = tk.Frame(win, bg="#11111B")
        tf.pack(fill='both', expand=True, padx=20, pady=8)
        txt = tk.Text(tf, font=('Segoe UI', 10), wrap='word', bg="#11111B",
                      fg="#CDD6F4", bd=0, padx=15, pady=12, spacing2=4)
        sb = ttk.Scrollbar(tf, command=txt.yview)
        txt.configure(yscrollcommand=sb.set)
        sb.pack(side='right', fill='y')
        txt.pack(fill='both', expand=True)

        txt.insert('1.0', """\
🔰  QUICK START — RECOMMENDED SETUP
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

  Bridge Source  →  Delta-Kronecker Tor-Bridges-Collector
  Category       →  Tested & Active
  Transport      →  obfs4
  IP Version     →  IPv4

  Why? "Tested & Active" bridges from the Delta-Kronecker
  repository are automatically collected and verified,
  IPv4 is the most stable option.

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
📋  STEP-BY-STEP INSTRUCTIONS
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

  Step 1  On first launch, the app automatically downloads the
          Tor Expert Bundle. Wait for the
          "Download Progress" bar to reach 100%.

  Step 2  After download completes, bridges are fetched
          automatically.  Use "Update Bridges" at any time
          to refresh them.

  Step 3  Set "Bridge Source" to
          Delta-Kronecker Tor-Bridges-Collector.

  Step 4  Choose:
            Category   →  Tested & Active
            Transport  →  obfs4
            IP Version →  IPv4

  Step 5  Click "Start Tor".

  Step 6  Wait for "Connection Progress" to reach 100%.
          Status will read "Tor is fully connected."

  Step 7  Enable "Set System Proxy" to route all
          Windows traffic through Tor automatically.

  Step 8  Click "Test Connection via Tor" in the Stats panel
          to verify your exit IP and confirm you are routed
          through the Tor network.

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
🔎  BRIDGE CATEGORIES EXPLAINED
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

  Tested & Active    Verified working bridges (Recommended)
  Fresh (72h)        Collected in the last 72 hours
  Full Archive       Complete historical bridge list
  Default (Built-in) Bridges bundled inside Tor itself

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
📊  CONNECTION STATS PANEL
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

  After connecting, press "Test Connection via Tor" to fill in:
    Exit IP      The public IP address your traffic exits from
    Country      The country of the Tor exit node
    Uptime       How long the current Tor session has been active
    Tor Status   Whether the exit is confirmed as a Tor node

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
⚠️  TROUBLESHOOTING
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

  Connection stuck below 100%?
    Click "Update Bridges" and restart Tor.

  obfs4 not working?
    Switch Transport to "webtunnel" and try again.

  Download failed?
    Check your internet connection. The app falls back to
    torproject.org automatically if GitHub is unreachable.

  Test Connection shows "Not Tor"?
    Make sure Tor has fully connected (100%) before testing.
""")
        txt.configure(state='disabled')

        tk.Button(win, text="Close", command=win.destroy,
                  bg="#89B4FA", fg="#11111B", font=('Segoe UI', 10, 'bold'),
                  relief="flat", cursor="hand2",
                  activebackground="#B4BEFE", activeforeground="#11111B"
                  ).pack(pady=(0, 12), padx=120, fill='x')

    def on_source_changed(self, event=None):
        if self.source_var.get() == "Delta-Kronecker Tor-Bridges-Collector":
            self.cat_label.grid()
            self.cat_combo.grid()
            self.update_transports()
        else:
            self.cat_label.grid_remove()
            self.cat_combo.grid_remove()
            opts = ["obfs4", "snowflake", "meek"]
            self.trans_combo['values'] = opts
            if self.trans_var.get() not in opts:
                self.trans_var.set("obfs4")

    def update_transports(self, event=None):
        opts = (["obfs4", "snowflake", "meek"]
                if self.source_var.get() == "Default (Built-in)"
                else ["obfs4", "webtunnel", "vanilla"])
        self.trans_combo['values'] = opts
        if self.trans_var.get() not in opts:
            self.trans_var.set(opts[0])

    def append_log(self, message):
        self.log_text.configure(state='normal')
        self.log_text.insert(tk.END, message)
        self.log_text.see(tk.END)
        self.log_text.configure(state='disabled')

    def update_progress(self, value):
        self.progress_var.set(value)
        self.progress_text_var.set(f"{value}%")

    def update_conn_progress(self, value):
        self.conn_progress_var.set(value)
        self.conn_pct_var.set(f"{value}%")

    def update_status(self, msg):
        self.status_var.set(f"Status: {msg}")
        self.root.update_idletasks()

    def _tick_uptime(self):
        if self.connect_time is None:
            return
        e = int(time.time() - self.connect_time)
        h, r = divmod(e, 3600);  m, s = divmod(r, 60)
        self.stat_uptime_var.set(f"{h:02d}:{m:02d}:{s:02d}")
        self._uptime_after_id = self.root.after(1000, self._tick_uptime)

    def _start_uptime(self):
        self.connect_time = time.time()
        self._tick_uptime()

    def _stop_uptime(self):
        if self._uptime_after_id:
            self.root.after_cancel(self._uptime_after_id)
            self._uptime_after_id = None
        self.connect_time = None
        self.stat_uptime_var.set("—")

    def start_test_connection(self):
        if not self.tor_connected:
            self.append_log("[Test] Tor is not connected yet.\n")
            return
        self.test_btn.configure(text="Testing…", state='disabled')
        threading.Thread(target=self._run_test_connection, daemon=True).start()

    def _run_test_connection(self):
        self.root.after(0, self.append_log,
                        "[Test] Connecting to check.torproject.org via SOCKS5…\n")
        try:
            raw      = socks5_request("check.torproject.org", 443, "/api/ip", timeout=20)
            tor_data = json.loads(raw.strip())
            exit_ip  = tor_data.get("IP", "unknown")
            is_tor   = tor_data.get("IsTor", False)

            self.root.after(0, self.stat_ip_var.set,  exit_ip)
            self.root.after(0, self.stat_tor_var.set,
                            "✅ Confirmed Tor" if is_tor else "⚠️  Not Tor")
            self.root.after(0, self.append_log,
                            f"[Test] Exit IP : {exit_ip}\n"
                            f"[Test] Is Tor  : {is_tor}\n")

            try:
                geo  = json.loads(
                    socks5_request("ipinfo.io", 443, f"/{exit_ip}/json", timeout=20).strip())
                country = geo.get("country", "?")
                org     = geo.get("org", "")
                self.root.after(0, self.stat_country_var.set, country)
                self.root.after(0, self.append_log,
                                f"[Test] Country : {country}\n"
                                f"[Test] Org     : {org}\n")
            except Exception as geo_e:
                self.root.after(0, self.stat_country_var.set, "?")
                self.root.after(0, self.append_log,
                                f"[Test] Country lookup failed: {geo_e}\n")

            self.root.after(0, self.update_status,
                            "Test complete — " +
                            ("Connected via Tor ✅" if is_tor else "Warning: Not on Tor ⚠️"))

        except Exception as e:
            self.root.after(0, self.append_log, f"[Test] Failed: {e}\n")
            self.root.after(0, self.update_status, "Test failed — is Tor running?")
        finally:
            self.root.after(0, self.test_btn.configure,
                            {"text": "🔍  Test Connection via Tor", "state": "normal"})

    def download_with_progress(self, url, dest, retries=3, timeout=60):
        for attempt in range(retries):
            try:
                req = urllib.request.Request(url, headers={'User-Agent': 'Mozilla/5.0'})
                with urllib.request.urlopen(req, timeout=timeout) as resp:
                    total      = resp.getheader('Content-Length')
                    total      = int(total) if total else None
                    downloaded = 0
                    self.root.after(0, self.update_progress, 0)
                    with open(dest, 'wb') as f:
                        while True:
                            chunk = resp.read(65536)
                            if not chunk:
                                break
                            f.write(chunk)
                            downloaded += len(chunk)
                            if total:
                                self.root.after(0, self.update_progress,
                                                int(downloaded * 100 / total))
                self.root.after(0, self.update_progress, 100)
                return True
            except Exception:
                if attempt == retries - 1:
                    raise
                time.sleep(2)

    def download_with_retry(self, url, dest, retries=3, timeout=30):
        for attempt in range(retries):
            try:
                req = urllib.request.Request(url, headers={'User-Agent': 'Mozilla/5.0'})
                with urllib.request.urlopen(req, timeout=timeout) as resp, \
                     open(dest, 'wb') as f:
                    shutil.copyfileobj(resp, f)
                return True
            except Exception:
                if attempt == retries - 1:
                    raise
                time.sleep(2)

    def auto_initialize(self):
        self.setup_tor()
        if not os.path.exists(self.bridges_dir) or not os.listdir(self.bridges_dir):
            self.download_bridges()
        else:
            self.root.after(0, self.update_status, "Ready.")

    def setup_tor(self):
        if not os.path.exists(self.archive_name) and not os.path.exists(self.extract_dir):
            self.root.after(0, self.update_status,
                            "Downloading Tor Expert Bundle from GitHub… Please wait.")
            try:
                self.download_with_progress(self.tor_url, self.archive_name,
                                            retries=3, timeout=120)
            except Exception:
                self.root.after(0, self.update_status,
                                "GitHub download failed. Trying torproject.org…")
                self.root.after(0, self.update_progress, 0)
                try:
                    self.download_with_progress(self.tor_fallback_url, self.archive_name,
                                                retries=3, timeout=120)
                except Exception as e:
                    self.root.after(0, self.update_status, f"Download failed: {e}")
                    return

        if os.path.exists(self.archive_name):
            self.root.after(0, self.update_status, "Extracting Tor… Please wait.")
            with tarfile.open(self.archive_name, "r:gz") as tar:
                tar.extractall(path=self.extract_dir)
            os.remove(self.archive_name)

        if not os.path.exists(self.bridges_dir):
            os.makedirs(self.bridges_dir)

    def start_download_bridges(self):
        threading.Thread(target=self.download_bridges, daemon=True).start()

    def get_safe_filename(self, cat, trans, ip):
        safe = cat.replace(" ", "_").replace("&", "and").replace("(", "").replace(")", "")
        return f"{safe}_{trans}_{ip}.txt"

    def download_bridges(self):
        if not os.path.exists(self.bridges_dir):
            os.makedirs(self.bridges_dir)
        self.root.after(0, self.update_status, "Downloading bridges… Please wait.")
        for cat, trans, ip, url in self.bridge_data:
            fpath = os.path.join(self.bridges_dir, self.get_safe_filename(cat, trans, ip))
            try:
                self.download_with_retry(url, fpath, retries=2, timeout=15)
            except Exception:
                pass
        self.root.after(0, self.update_status, "Ready. All bridges updated.")

    def toggle_proxy(self):
        self.set_system_proxy(self.proxy_var.get())

    def set_system_proxy(self, enable):
        try:
            key = winreg.OpenKey(
                winreg.HKEY_CURRENT_USER,
                r'Software\Microsoft\Windows\CurrentVersion\Internet Settings',
                0, winreg.KEY_ALL_ACCESS)
            if enable:
                winreg.SetValueEx(key, 'ProxyEnable', 0, winreg.REG_DWORD, 1)
                winreg.SetValueEx(key, 'ProxyServer', 0, winreg.REG_SZ,
                                  'socks=127.0.0.1:19050')
            else:
                winreg.SetValueEx(key, 'ProxyEnable', 0, winreg.REG_DWORD, 0)
            winreg.CloseKey(key)
            ctypes.windll.wininet.InternetSetOptionW(0, 39, 0, 0)
            ctypes.windll.wininet.InternetSetOptionW(0, 37, 0, 0)
        except Exception:
            pass

    def generate_torrc(self):
        base    = os.path.abspath(self.extract_dir)
        data    = os.path.join(base, "data")
        tor_d   = os.path.join(base, "tor")
        pt_d    = os.path.join(tor_d, "pluggable_transports")
        lyrebird = os.path.join(pt_d, "lyrebird.exe")
        conjure  = os.path.join(pt_d, "conjure-client.exe")
        torrc    = os.path.join(tor_d, "torrc")

        src   = self.source_var.get()
        cat   = self.cat_var.get()
        trans = self.trans_var.get()
        ip    = self.ip_var.get()

        bridges = []
        if src == "Default (Built-in)":
            cfg = os.path.join(pt_d, "pt_config.json")
            if os.path.exists(cfg):
                try:
                    with open(cfg, encoding="utf-8") as f:
                        for b in json.load(f).get("bridges", {}).get(trans, []):
                            bridges.append(f"Bridge {b}\n")
                except Exception as e:
                    self.root.after(0, self.append_log, f"pt_config.json error: {e}\n")
        else:
            for c, t, v, _ in self.bridge_data:
                if c == cat and t == trans and (ip == "Both" or ip == v):
                    fn = os.path.join(self.bridges_dir, self.get_safe_filename(c, t, v))
                    if os.path.exists(fn):
                        with open(fn, encoding="utf-8") as f:
                            for line in f:
                                line = line.strip()
                                if line:
                                    bridges.append(f"Bridge {line}\n")

        use = "1" if bridges else "0"
        content = (
            f"Log notice stdout\n"
            f"DataDirectory {data}\n"
            f"GeoIPFile {os.path.join(data, 'geoip')}\n"
            f"GeoIPv6File {os.path.join(data, 'geoip6')}\n"
            f"SOCKSPort 127.0.0.1:19050\n"
            f"ControlPort 19051\n"
            f"CookieAuthentication 1\n"
            f"DormantCanceledByStartup 1\n"
            f"UseBridges {use}\n\n"
            f"ClientTransportPlugin meek_lite,obfs2,obfs3,obfs4,scramblesuit,webtunnel"
            f" exec {lyrebird}\n"
            f"ClientTransportPlugin snowflake exec {lyrebird}\n"
            f"ClientTransportPlugin conjure exec {conjure}"
            f" -registerURL \"https://registration.refraction.network/api\"\n\n"
            f"AllowNonRFC953Hostnames 1\n"
            f"EnforceDistinctSubnets 0\n\n"
        )
        if use == "1":
            content += "".join(bridges[:50])

        with open(torrc, "w", encoding="utf-8") as f:
            f.write(content)
        return torrc, os.path.join(tor_d, "tor.exe"), use

    def start_tor_thread(self):
        if self.tor_process is not None:
            self.update_status("Tor is already running.")
            return
        self.log_text.configure(state='normal')
        self.log_text.delete(1.0, tk.END)
        self.log_text.configure(state='disabled')
        self.update_conn_progress(0)
        self.stat_ip_var.set("—");  self.stat_country_var.set("—")
        self.stat_tor_var.set("—"); self._stop_uptime()
        self.tor_connected = False
        threading.Thread(target=self.run_tor, daemon=True).start()

    def run_tor(self):
        try:
            torrc, tor_exe, use_bridges = self.generate_torrc()

            if self.source_var.get() != "Default (Built-in)" and use_bridges == "0":
                self.root.after(0, self.append_log,
                                "Warning: No bridges found. Starting without bridges.\n")

            self.root.after(0, self.update_status, "Starting Tor…")
            self.tor_process = subprocess.Popen(
                [tor_exe, "-f", torrc],
                stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                text=True, creationflags=subprocess.CREATE_NO_WINDOW)

            self.root.after(0, self.update_status, "Tor is running.")
            self.root.after(0, lambda: self.stop_btn.config(
                bg="#F38BA8", fg="#11111B",
                activebackground="#E64553", activeforeground="white"))

            for line in iter(self.tor_process.stdout.readline, ''):
                self.root.after(0, self.append_log, line)
                m = re.search(r'Bootstrapped (\d+)%', line)
                if m:
                    pct = int(m.group(1))
                    self.root.after(0, self.update_conn_progress, pct)
                    if pct == 100 and not self.tor_connected:
                        self.tor_connected = True
                        self.root.after(0, self.update_status, "Tor is fully connected.")
                        self.root.after(0, self._start_uptime)
                        self.root.after(0, self.stat_tor_var.set, "✅ Connected")

            self.tor_process.stdout.close()
            self.tor_process.wait()

        except Exception as e:
            self.root.after(0, self.update_status, "Failed to start Tor.")
            self.root.after(0, self.append_log, f"Error: {e}\n")
        finally:
            self.tor_process   = None
            self.tor_connected = False
            self.set_system_proxy(False)
            self.root.after(0, lambda: self.proxy_var.set(False))
            self.root.after(0, lambda: self.stop_btn.config(
                bg="#313244", fg="#CDD6F4",
                activebackground="#45475A", activeforeground="#CDD6F4"))
            self.root.after(0, self.update_status, "Tor stopped.")
            self.root.after(0, self.update_conn_progress, 0)
            self.root.after(0, self._stop_uptime)
            self.root.after(0, self.stat_tor_var.set,     "—")
            self.root.after(0, self.stat_ip_var.set,      "—")
            self.root.after(0, self.stat_country_var.set, "—")

    def stop_tor(self):
        if self.tor_process:
            self.tor_process.terminate()
            self.tor_process = None
        self.tor_connected = False
        self.set_system_proxy(False)
        self.proxy_var.set(False)
        self.stop_btn.config(bg="#313244", fg="#CDD6F4",
                             activebackground="#45475A", activeforeground="#CDD6F4")
        self.update_status("Tor stopped.")
        self.update_conn_progress(0)
        self._stop_uptime()
        self.stat_tor_var.set("—");     self.stat_ip_var.set("—")
        self.stat_country_var.set("—")


if __name__ == "__main__":
    root = tk.Tk()
    app  = TorClientGUI(root)
    root.protocol("WM_DELETE_WINDOW", lambda: (app.stop_tor(), root.destroy()))
    root.mainloop()
