
import os
import sys
import json
import re
import ssl
import shutil
import socket
import random
import tarfile
import threading
import time
import subprocess
import urllib.request
import winreg
import ctypes
import ctypes.wintypes
import select
from concurrent.futures import ThreadPoolExecutor
import tkinter as tk
from tkinter import ttk, filedialog, messagebox

TOR_SOCKS_PORT  = 9050
TOR_CTRL_PORT   = 9051
HTTP_PROXY_PORT = 19052

AUTO_SEQUENCE = [
    ("Fresh (72h)",     "obfs4",     "IPv4"),
    ("Fresh (72h)",     "vanilla",   "IPv4"),
    ("Fresh (72h)",     "webtunnel", "IPv4"),
    ("Tested & Active", "obfs4",     "IPv4"),
    ("Tested & Active", "vanilla",   "IPv4"),
    ("Tested & Active", "webtunnel", "IPv4"),
    ("Full Archive",    "obfs4",     "IPv4"),
    ("Full Archive",    "vanilla",   "IPv4"),
    ("Full Archive",    "webtunnel", "IPv4"),
]

DEFAULT_CFG = {
    "auto_connect_timeout":   180,
    "bridges_in_torrc":       100,
    "shuffle_bridges":        True,
    "dns_over_tor":          False,
    "max_circuit_dirtiness": 1800,
    "new_circuit_period":      10,
    "num_entry_guards":        15,
    "keep_alive_enabled":    True,
    "keep_alive_interval":    120,
    "watchdog_enabled":      True,
    "watchdog_interval":       30,
    "exit_nodes_enabled":   False,
    "exit_nodes_countries": "{nl},{de},{fr},{ch},{at},{se},{no},{fi},{is}",
    "strict_exit_nodes":    False,
    "auto_proxy_on_connect": True,
    "exp_connection_padding":             False,
    "exp_reduced_connection_padding":     False,
    "exp_circuit_stream_timeout":         0,
    "exp_socks_timeout":                  0,
    "exp_safe_logging":                   False,
    "exp_avoid_disk_writes":              False,
    "exp_hardware_accel":                 False,
    "exp_client_dns_reject_internal":     False,
    "exp_fascist_firewall":               False,
    "exp_firewall_ports":                 "80,443",
    "exp_reachable_addresses":            "",
    "exp_num_cpus":                       0,
    "exp_exclude_nodes":                  "",
    "exp_exclude_exit_nodes":             "",
    "exp_use_entry_guards_as_dir_guards": False,
    "exp_path_bias_circ_threshold":       0,
    "exp_isolate_dest_addr":              False,
    "exp_isolate_dest_port":              False,
    "exp_no_exit_stream_ports":           "",
}

C = {
    "BG":   "#1E1E2E",
    "DRK":  "#181825",
    "FG":   "#CDD6F4",
    "ACC":  "#89B4FA",
    "BTN":  "#313244",
    "BTN2": "#45475A",
    "GRN":  "#A6E3A1",
    "RED":  "#F38BA8",
    "YLW":  "#F9E2AF",
    "BLK":  "#11111B",
    "ORG":  "#FAB387",
    "PRP":  "#CBA6F7",
}


def resource_path(filename):
    if hasattr(sys, '_MEIPASS'):
        return os.path.join(sys._MEIPASS, filename)
    return os.path.join(os.path.dirname(os.path.abspath(__file__)), filename)


def _bootstrap_config_path():
    base = os.path.join(
        os.environ.get("LOCALAPPDATA", os.path.expanduser("~")),
        "TorClient")
    os.makedirs(base, exist_ok=True)
    return os.path.join(base, "tor_client_config.json")


def load_config() -> dict:
    bootstrap = _bootstrap_config_path()
    data = {}
    if os.path.exists(bootstrap):
        try:
            with open(bootstrap, encoding="utf-8") as f:
                data = json.load(f)
        except Exception:
            data = {}
    extract_dir = data.get("extract_dir", "")
    if extract_dir and os.path.isdir(extract_dir):
        real_cfg = os.path.join(extract_dir, "tor_client_config.json")
        if os.path.exists(real_cfg):
            try:
                with open(real_cfg, encoding="utf-8") as f:
                    data = json.load(f)
            except Exception:
                pass
    for k, v in DEFAULT_CFG.items():
        data.setdefault(k, v)
    return data


def save_config(cfg: dict, extract_dir: str = ""):
    ed = extract_dir or cfg.get("extract_dir", "")
    if ed:
        try:
            os.makedirs(ed, exist_ok=True)
            with open(os.path.join(ed, "tor_client_config.json"), "w",
                      encoding="utf-8") as f:
                json.dump(cfg, f, indent=2)
        except Exception:
            pass
    try:
        with open(_bootstrap_config_path(), "w", encoding="utf-8") as f:
            json.dump({"extract_dir": ed}, f, indent=2)
    except Exception:
        pass


def apply_dark_titlebar(widget):
    try:
        GA_ROOT = 2
        hwnd = ctypes.windll.user32.GetAncestor(widget.winfo_id(), GA_ROOT)
        if not hwnd:
            hwnd = widget.winfo_id()
        dwm = ctypes.windll.dwmapi
        one = ctypes.c_int(1)
        dwm.DwmSetWindowAttribute(hwnd, 20, ctypes.byref(one), ctypes.sizeof(one))
        cap = ctypes.c_int(0x2E1E1E)
        dwm.DwmSetWindowAttribute(hwnd, 35, ctypes.byref(cap), ctypes.sizeof(cap))
        txt = ctypes.c_int(0xF4D6CD)
        dwm.DwmSetWindowAttribute(hwnd, 36, ctypes.byref(txt), ctypes.sizeof(txt))
    except Exception:
        pass


def _load_tray_icon():
    try:
        user32   = ctypes.windll.user32
        ico_path = resource_path("icon.ico")
        if os.path.exists(ico_path):
            hIcon = user32.LoadImageW(
                None, ico_path, 1, 0, 0,
                0x00000010 | 0x00008000)
            if hIcon:
                return hIcon
    except Exception:
        pass
    try:
        return ctypes.windll.user32.LoadIconW(0, 32512)
    except Exception:
        return 0


def socks5_request(host, port, path,
                   proxy_host="127.0.0.1", proxy_port=TOR_SOCKS_PORT,
                   use_ssl=True, timeout=20):
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    s.settimeout(timeout)
    s.connect((proxy_host, proxy_port))
    s.sendall(b'\x05\x01\x00')
    if s.recv(2)[1] != 0x00:
        raise ConnectionError("SOCKS5 handshake failed")
    hb = host.encode()
    s.sendall(b'\x05\x01\x00\x03' + bytes([len(hb)]) + hb + port.to_bytes(2, 'big'))
    r = s.recv(10)
    if r[1] != 0x00:
        raise ConnectionError(f"SOCKS5 connect error {r[1]}")
    if use_ssl:
        ctx = ssl.create_default_context()
        s = ctx.wrap_socket(s, server_hostname=host)
    s.sendall((f"GET {path} HTTP/1.1\r\nHost: {host}\r\n"
               f"Connection: close\r\nUser-Agent: Mozilla/5.0\r\n\r\n").encode())
    data = b""
    while True:
        chunk = s.recv(4096)
        if not chunk:
            break
        data += chunk
    s.close()
    sep = data.find(b"\r\n\r\n")
    return (data[sep + 4:] if sep != -1 else data).decode(errors="replace")


def _http_proxy_relay(client_sock, socks_host, socks_port, host, port, initial_data=b""):
    try:
        tor = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        tor.settimeout(30)
        tor.connect((socks_host, socks_port))
        tor.sendall(b'\x05\x01\x00')
        if tor.recv(2)[1] != 0x00:
            client_sock.close(); tor.close(); return
        host_b = host.encode()
        tor.sendall(b'\x05\x01\x00\x03' + bytes([len(host_b)]) + host_b + port.to_bytes(2, 'big'))
        resp = tor.recv(10)
        if resp[1] != 0x00:
            client_sock.close(); tor.close(); return
        if initial_data:
            tor.sendall(initial_data)
        tor.settimeout(None)
        client_sock.settimeout(None)
        while True:
            try:
                r, _, _ = select.select([client_sock, tor], [], [], 120)
            except Exception:
                break
            if not r:
                break
            for s in r:
                try:
                    d = s.recv(65536)
                except Exception:
                    d = b""
                if not d:
                    client_sock.close(); tor.close(); return
                other = tor if s is client_sock else client_sock
                try:
                    other.sendall(d)
                except Exception:
                    client_sock.close(); tor.close(); return
    except Exception:
        pass
    finally:
        try: client_sock.close()
        except: pass
        try: tor.close()
        except: pass


def _http_proxy_handle(client_sock, socks_host, socks_port):
    try:
        client_sock.settimeout(15)
        buf = b""
        while b"\r\n\r\n" not in buf:
            chunk = client_sock.recv(4096)
            if not chunk:
                client_sock.close(); return
            buf += chunk
        header_end = buf.index(b"\r\n\r\n")
        headers_raw = buf[:header_end].decode(errors="replace")
        body = buf[header_end + 4:]
        first_line = headers_raw.split("\r\n")[0]
        parts = first_line.split(" ", 2)
        if len(parts) < 2:
            client_sock.close(); return
        method = parts[0]
        target = parts[1]
        if method == "CONNECT":
            if ":" in target:
                host, port_s = target.rsplit(":", 1)
                port = int(port_s)
            else:
                host = target; port = 443
            try:
                client_sock.sendall(b"HTTP/1.1 200 Connection established\r\n\r\n")
            except Exception:
                client_sock.close(); return
            _http_proxy_relay(client_sock, socks_host, socks_port, host, port)
        else:
            from urllib.parse import urlparse
            parsed = urlparse(target)
            host = parsed.hostname or ""
            port = parsed.port or 80
            path = parsed.path or "/"
            if parsed.query:
                path += "?" + parsed.query
            lines = headers_raw.split("\r\n")
            lines[0] = f"{method} {path} HTTP/1.1"
            new_headers = "\r\n".join(lines) + "\r\n\r\n"
            _http_proxy_relay(client_sock, socks_host, socks_port, host, port,
                              new_headers.encode() + body)
    except Exception:
        try: client_sock.close()
        except: pass


def run_http_proxy_server(stop_event, socks_host="127.0.0.1", socks_port=TOR_SOCKS_PORT,
                          listen_port=HTTP_PROXY_PORT):
    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    try:
        srv.bind(("127.0.0.1", listen_port))
    except Exception:
        return
    srv.listen(64)
    srv.settimeout(1)
    while not stop_event.is_set():
        try:
            client, _ = srv.accept()
        except socket.timeout:
            continue
        except Exception:
            break
        threading.Thread(target=_http_proxy_handle,
                         args=(client, socks_host, socks_port),
                         daemon=True).start()
    srv.close()


def _win_notify(title: str, msg: str, hwnd: int = 0):
    try:
        NIM_ADD    = 0x00000000
        NIM_DELETE = 0x00000002
        NIF_ICON   = 0x00000002
        NIF_TIP    = 0x00000004
        NIF_INFO   = 0x00000010
        NIIF_INFO  = 0x00000001

        class NOTIFYICONDATA(ctypes.Structure):
            _fields_ = [
                ("cbSize",           ctypes.wintypes.DWORD),
                ("hWnd",             ctypes.wintypes.HWND),
                ("uID",              ctypes.wintypes.UINT),
                ("uFlags",           ctypes.wintypes.UINT),
                ("uCallbackMessage", ctypes.wintypes.UINT),
                ("hIcon",            ctypes.wintypes.HICON),
                ("szTip",            ctypes.c_wchar * 128),
                ("dwState",          ctypes.wintypes.DWORD),
                ("dwStateMask",      ctypes.wintypes.DWORD),
                ("szInfo",           ctypes.c_wchar * 256),
                ("uTimeout",         ctypes.wintypes.UINT),
                ("szInfoTitle",      ctypes.c_wchar * 64),
                ("dwInfoFlags",      ctypes.wintypes.DWORD),
            ]

        shell32 = ctypes.windll.shell32
        nid = NOTIFYICONDATA()
        nid.cbSize      = ctypes.sizeof(NOTIFYICONDATA)
        nid.hWnd        = hwnd
        nid.uID         = 1
        nid.uFlags      = NIF_ICON | NIF_TIP | NIF_INFO
        nid.hIcon       = _load_tray_icon()
        nid.szTip       = "Tor Client"
        nid.szInfo      = msg[:255]
        nid.szInfoTitle = title[:63]
        nid.dwInfoFlags = NIIF_INFO
        shell32.Shell_NotifyIconW(NIM_ADD, ctypes.byref(nid))
        time.sleep(4)
        shell32.Shell_NotifyIconW(NIM_DELETE, ctypes.byref(nid))
    except Exception:
        pass


class FolderSetupDialog:
    DEFAULT = os.path.join(
        os.environ.get("LOCALAPPDATA", os.path.expanduser("~")),
        "TorClient", "tor_custom_client")

    def __init__(self, parent):
        self.result = None
        w = tk.Toplevel(parent)
        w.title("First-Time Setup")
        w.geometry("620x290")
        w.configure(bg=C["BG"])
        w.resizable(False, False)
        w.grab_set()
        w.update()
        apply_dark_titlebar(w)

        tk.Label(w, text="📁  Choose Tor Installation Folder",
                 font=('Segoe UI', 13, 'bold'), bg=C["BG"], fg=C["ACC"]).pack(pady=(18, 4))
        tk.Label(w,
                 text=("Tor Expert Bundle and bridge files will be stored here.\n"
                       "The recommended default (AppData\\Local) is always writable.\n"
                       "Avoid C:\\Program Files or paths with spaces."),
                 font=('Segoe UI', 9), bg=C["BG"], fg=C["FG"],
                 justify='left', wraplength=560).pack(padx=24, pady=4)

        row = tk.Frame(w, bg=C["BG"])
        row.pack(fill='x', padx=24, pady=(10, 4))
        self._pv = tk.StringVar(value=self.DEFAULT)
        tk.Entry(row, textvariable=self._pv, font=('Segoe UI', 9),
                 bg=C["BTN"], fg=C["FG"], insertbackground=C["FG"],
                 relief="flat", bd=6).pack(side='left', fill='x', expand=True)
        tk.Button(row, text="Browse…", command=self._browse,
                  bg=C["BTN2"], fg=C["FG"], font=('Segoe UI', 9),
                  relief="flat", cursor="hand2").pack(side='left', padx=(6, 0))

        tk.Button(w, text="Continue →", command=lambda: self._ok(w),
                  bg=C["ACC"], fg=C["BLK"], font=('Segoe UI', 11, 'bold'),
                  relief="flat", cursor="hand2",
                  activebackground="#B4BEFE").pack(pady=(14, 0), padx=80, fill='x')
        parent.wait_window(w)

    def _browse(self):
        d = filedialog.askdirectory(initialdir=os.path.dirname(self._pv.get()))
        if d:
            self._pv.set(os.path.join(d, "tor_custom_client"))

    def _ok(self, w):
        p = self._pv.get().strip()
        if not p:
            messagebox.showerror("Error", "Please choose a folder.", parent=w)
            return
        self.result = p
        w.destroy()


class SettingsWindow:
    def __init__(self, parent, cfg: dict, on_save, on_clear_data=None):
        self.on_save = on_save
        w = tk.Toplevel(parent)
        w.title("Settings")
        w.geometry("560x720")
        w.configure(bg=C["BG"])
        w.resizable(False, True)
        w.grab_set()
        w.update()
        apply_dark_titlebar(w)

        tk.Label(w, text="⚙️  Settings",
                 font=('Segoe UI', 14, 'bold'), bg=C["BG"], fg=C["ACC"]).pack(pady=(14, 6))

        canvas = tk.Canvas(w, bg=C["BG"], highlightthickness=0)
        sb = ttk.Scrollbar(w, orient='vertical', command=canvas.yview)
        canvas.configure(yscrollcommand=sb.set)
        sb.pack(side='right', fill='y', padx=(0, 4))
        canvas.pack(fill='both', expand=True, padx=10)

        inner = tk.Frame(canvas, bg=C["BG"])
        inner_id = canvas.create_window((0, 0), window=inner, anchor='nw')

        def _resize(e):
            canvas.configure(scrollregion=canvas.bbox("all"))
            canvas.itemconfig(inner_id, width=e.width)
        canvas.bind("<Configure>", _resize)
        inner.bind("<Configure>", lambda e: canvas.configure(scrollregion=canvas.bbox("all")))

        def _bind_scroll(widget):
            widget.bind("<MouseWheel>",
                        lambda e: canvas.yview_scroll(-1 * (e.delta // 120), "units"))

        canvas.bind("<MouseWheel>",
                    lambda e: canvas.yview_scroll(-1 * (e.delta // 120), "units"))
        inner.bind("<MouseWheel>",
                   lambda e: canvas.yview_scroll(-1 * (e.delta // 120), "units"))

        def _section(t, color=C["BTN"], fg=C["ACC"]):
            lbl = tk.Label(inner, text=t, font=('Segoe UI', 10, 'bold'),
                           bg=color, fg=fg, anchor='w', padx=10)
            lbl.pack(fill='x', pady=(10, 2))
            _bind_scroll(lbl)

        def _hint(t):
            lbl = tk.Label(inner, text=t, bg=C["BG"], fg="#6C7086",
                           font=('Segoe UI', 8), anchor='w', justify='left')
            lbl.pack(fill='x', padx=14)
            _bind_scroll(lbl)

        def _row(label, widget_factory, lw=28):
            f = tk.Frame(inner, bg=C["BG"])
            f.pack(fill='x', padx=14, pady=3)
            lbl = tk.Label(f, text=label, width=lw, anchor='w',
                           bg=C["BG"], fg=C["FG"], font=('Segoe UI', 9))
            lbl.pack(side='left')
            _bind_scroll(lbl)
            _bind_scroll(f)
            w2 = widget_factory(f)
            w2.pack(side='left', fill='x', expand=True)
            return w2

        def _spin(parent, var, lo, hi, width=7):
            sb2 = tk.Spinbox(parent, textvariable=var, from_=lo, to=hi,
                             width=width, bg=C["BTN"], fg=C["FG"],
                             buttonbackground=C["BTN2"], relief="flat",
                             insertbackground=C["FG"], font=('Segoe UI', 9))
            _bind_scroll(sb2)
            return sb2

        def _chk(parent, var):
            cb = ttk.Checkbutton(parent, variable=var)
            _bind_scroll(cb)
            return cb

        def _entry(parent, var, width=30):
            e = tk.Entry(parent, textvariable=var, width=width,
                         bg=C["BTN"], fg=C["FG"], insertbackground=C["FG"],
                         relief="flat", bd=4, font=('Segoe UI', 9))
            _bind_scroll(e)
            return e

        _section("🔄  Auto-Connect")
        v_act = tk.IntVar(value=cfg.get("auto_connect_timeout", 180))
        _row("Timeout per config (sec):", lambda p: _spin(p, v_act, 30, 600))
        _hint("  How long to wait at a stuck bootstrap % before trying the next bridge group.")

        v_apc = tk.BooleanVar(value=cfg.get("auto_proxy_on_connect", True))
        _row("Auto-enable proxy on connect:", lambda p: _chk(p, v_apc))
        _hint("  Automatically turns on System Proxy when Tor reaches 100%.")

        _section("🌉  Bridges")
        v_bnum = tk.IntVar(value=cfg.get("bridges_in_torrc", 100))
        _row("Bridges written to torrc:", lambda p: _spin(p, v_bnum, 5, 300))
        v_shuf = tk.BooleanVar(value=cfg.get("shuffle_bridges", True))
        _row("Shuffle bridge order:", lambda p: _chk(p, v_shuf))
        _hint("  Randomising ensures different bridges are tried each session.")

        _section("🔒  Privacy / DNS")
        v_dns = tk.BooleanVar(value=cfg.get("dns_over_tor", False))
        _row("DNS over Tor (DNSPort 9053):", lambda p: _chk(p, v_dns))
        _hint("  Routes DNS queries through Tor. Requires apps to use 127.0.0.1:9053.\n"
              "  Default: OFF — the HTTP proxy already handles DNS for most apps.")

        _section("⚡  Circuit Building")
        v_mcd = tk.IntVar(value=cfg.get("max_circuit_dirtiness", 1800))
        _row("MaxCircuitDirtiness (sec):", lambda p: _spin(p, v_mcd, 60, 7200))
        v_ncp = tk.IntVar(value=cfg.get("new_circuit_period", 10))
        _row("NewCircuitPeriod (sec):", lambda p: _spin(p, v_ncp, 5, 300))
        v_neg = tk.IntVar(value=cfg.get("num_entry_guards", 15))
        _row("NumEntryGuards:", lambda p: _spin(p, v_neg, 1, 30))
        _hint("  Optimized for maximum connection stability.\n"
              "  Higher MaxCircuitDirtiness = circuits live longer, fewer rebuilds.\n"
              "  Lower NewCircuitPeriod = fresh circuits always ready faster.")

        _section("💓  Keep-Alive")
        v_kae = tk.BooleanVar(value=cfg.get("keep_alive_enabled", True))
        _row("Keep-Alive enabled:", lambda p: _chk(p, v_kae))
        v_kai = tk.IntVar(value=cfg.get("keep_alive_interval", 120))
        _row("Keep-Alive interval (sec):", lambda p: _spin(p, v_kai, 30, 600))
        _hint("  Sends a tiny request through Tor to prevent ISP from closing idle connections.")

        _section("🐕  Watchdog")
        v_wde = tk.BooleanVar(value=cfg.get("watchdog_enabled", True))
        _row("Watchdog enabled:", lambda p: _chk(p, v_wde))
        v_wdi = tk.IntVar(value=cfg.get("watchdog_interval", 30))
        _row("Check interval (sec):", lambda p: _spin(p, v_wdi, 10, 300))
        _hint("  Automatically restarts Tor if the process crashes or goes dormant.")

        _section("🌍  Exit Nodes")
        v_ene = tk.BooleanVar(value=cfg.get("exit_nodes_enabled", False))
        _row("Enable Exit Nodes filter:", lambda p: _chk(p, v_ene))
        ef = tk.Frame(inner, bg=C["BG"])
        ef.pack(fill='x', padx=14, pady=3)
        _bind_scroll(ef)
        lbl_enc = tk.Label(ef, text="Countries (torrc format):", width=28, anchor='w',
                           bg=C["BG"], fg=C["FG"], font=('Segoe UI', 9))
        lbl_enc.pack(side='left')
        _bind_scroll(lbl_enc)
        v_enc = tk.StringVar(value=cfg.get("exit_nodes_countries",
                                           "{nl},{de},{fr},{ch},{at},{se},{no},{fi},{is}"))
        tk.Entry(ef, textvariable=v_enc, bg=C["BTN"], fg=C["FG"],
                 insertbackground=C["FG"], relief="flat", bd=4,
                 font=('Segoe UI', 9)).pack(side='left', fill='x', expand=True)
        v_sne = tk.BooleanVar(value=cfg.get("strict_exit_nodes", False))
        _row("StrictNodes (only these countries):", lambda p: _chk(p, v_sne))
        _hint("  Recommended: {nl} {de} {fr} {ch} {at} {se} {no} {fi} {is}\n"
              "  Default OFF. Enable if YouTube/Instagram do not load via Tor.\n"
              "  StrictNodes OFF = fall back to any country if chosen ones fail.")

        _section("🗑️  Maintenance")
        _hint("  Clear cached Tor circuits and state (data directory).\n"
              "  Tor will rebuild circuits from scratch on next start.")

        def _do_clear():
            if on_clear_data:
                on_clear_data()
            messagebox.showinfo("Done", "Data directory cleared.", parent=w)

        btn_clr = tk.Button(inner, text="🗑️  Clear Data Directory",
                            command=_do_clear,
                            bg=C["BTN2"], fg=C["RED"],
                            font=('Segoe UI', 9, 'bold'),
                            relief="flat", cursor="hand2",
                            activebackground=C["BTN"])
        btn_clr.pack(fill='x', padx=14, pady=(4, 6))

        _section("🧪  Experimental (Advanced torrc)",
                 color="#2A1F3D", fg=C["PRP"])

        warn_lbl = tk.Label(inner,
                            text="  ⚠️  All options below are OFF by default.\n"
                                 "  Wrong settings can break connectivity. Use with caution.",
                            bg="#2A1F3D", fg=C["YLW"],
                            font=('Segoe UI', 8, 'bold'), anchor='w', justify='left')
        warn_lbl.pack(fill='x', padx=0, pady=(0, 4))
        _bind_scroll(warn_lbl)

        def _exp_section(t):
            lbl = tk.Label(inner, text=t, font=('Segoe UI', 9, 'bold'),
                           bg="#241B2F", fg=C["PRP"], anchor='w', padx=14)
            lbl.pack(fill='x', pady=(6, 1))
            _bind_scroll(lbl)

        _exp_section("― Connection & Padding")
        v_cp = tk.BooleanVar(value=cfg.get("exp_connection_padding", False))
        _row("ConnectionPadding:", lambda p: _chk(p, v_cp))
        _hint("  Send dummy traffic to resist traffic-analysis by ISP. Increases bandwidth usage.")
        v_rcp = tk.BooleanVar(value=cfg.get("exp_reduced_connection_padding", False))
        _row("ReducedConnectionPadding:", lambda p: _chk(p, v_rcp))
        _hint("  Lighter version of ConnectionPadding. Less bandwidth overhead.")

        _exp_section("― Streams & Timeouts")
        v_cst = tk.IntVar(value=cfg.get("exp_circuit_stream_timeout", 0))
        _row("CircuitStreamTimeout (sec):", lambda p: _spin(p, v_cst, 0, 3600))
        _hint("  Idle stream timeout before circuit is closed. 0 = Tor default.")
        v_st = tk.IntVar(value=cfg.get("exp_socks_timeout", 0))
        _row("SocksTimeout (sec):", lambda p: _spin(p, v_st, 0, 600))
        _hint("  Timeout for unanswered SOCKS connections. 0 = Tor default (120s).")

        _exp_section("― Stream Isolation")
        v_ida = tk.BooleanVar(value=cfg.get("exp_isolate_dest_addr", False))
        _row("IsolateDestAddr:", lambda p: _chk(p, v_ida))
        _hint("  Each destination address gets its own circuit. More privacy,\n"
              "  but uses more circuits and memory.")
        v_idp = tk.BooleanVar(value=cfg.get("exp_isolate_dest_port", False))
        _row("IsolateDestPort:", lambda p: _chk(p, v_idp))
        _hint("  Each destination port gets its own circuit.")

        _exp_section("― Security & Disk")
        v_sl = tk.BooleanVar(value=cfg.get("exp_safe_logging", False))
        _row("SafeLogging:", lambda p: _chk(p, v_sl))
        _hint("  Scrub sensitive data (IPs, addresses) from logs.")
        v_adw = tk.BooleanVar(value=cfg.get("exp_avoid_disk_writes", False))
        _row("AvoidDiskWrites:", lambda p: _chk(p, v_adw))
        _hint("  Minimize writing to disk. Good for SSDs and privacy.")
        v_ha = tk.BooleanVar(value=cfg.get("exp_hardware_accel", False))
        _row("HardwareAccel:", lambda p: _chk(p, v_ha))
        _hint("  Use CPU hardware AES for faster encryption.")
        v_cdri = tk.BooleanVar(value=cfg.get("exp_client_dns_reject_internal", False))
        _row("ClientDNSRejectInternalAddresses:", lambda p: _chk(p, v_cdri))
        _hint("  Reject DNS responses pointing to private/internal IPs (DNS rebinding protection).")

        _exp_section("― Firewall & Network")
        v_ff = tk.BooleanVar(value=cfg.get("exp_fascist_firewall", False))
        _row("FascistFirewall:", lambda p: _chk(p, v_ff))
        _hint("  Only connect via ports 80 and 443. Useful behind strict firewalls.")
        v_fp = tk.StringVar(value=cfg.get("exp_firewall_ports", "80,443"))
        _row("FirewallPorts:", lambda p: _entry(p, v_fp, 20))
        _hint("  Comma-separated ports allowed when FascistFirewall is ON.")
        v_ra = tk.StringVar(value=cfg.get("exp_reachable_addresses", ""))
        _row("ReachableAddresses:", lambda p: _entry(p, v_ra, 30))
        _hint("  Only connect to these IP ranges (CIDR). Empty = all.")
        v_nc = tk.IntVar(value=cfg.get("exp_num_cpus", 0))
        _row("NumCPUs:", lambda p: _spin(p, v_nc, 0, 32))
        _hint("  CPU threads for Tor. 0 = auto-detect.")

        _exp_section("― Node Selection")
        v_en = tk.StringVar(value=cfg.get("exp_exclude_nodes", ""))
        _row("ExcludeNodes:", lambda p: _entry(p, v_en, 30))
        _hint("  Nodes/countries to NEVER use. Example: {ru},{cn},{ir}")
        v_een = tk.StringVar(value=cfg.get("exp_exclude_exit_nodes", ""))
        _row("ExcludeExitNodes:", lambda p: _entry(p, v_een, 30))
        _hint("  Countries/nodes excluded only from exit position.")
        v_nesp = tk.StringVar(value=cfg.get("exp_no_exit_stream_ports", ""))
        _row("Reject exit ports:", lambda p: _entry(p, v_nesp, 20))
        _hint("  Ports Tor must NOT exit to. Example: 25,587")
        v_ueag = tk.BooleanVar(value=cfg.get("exp_use_entry_guards_as_dir_guards", False))
        _row("UseEntryGuardsAsDirGuards:", lambda p: _chk(p, v_ueag))
        _hint("  Reuse entry guards for directory fetches (fewer distinct nodes contacted).")
        v_pbct = tk.IntVar(value=cfg.get("exp_path_bias_circ_threshold", 0))
        _row("PathBiasCircThreshold:", lambda p: _spin(p, v_pbct, 0, 200))
        _hint("  Circuits to build before path bias detection kicks in. 0 = Tor default.")

        bf = tk.Frame(w, bg=C["BG"])
        bf.pack(fill='x', padx=20, pady=10)

        def _apply():
            cfg.update({
                "auto_connect_timeout":              v_act.get(),
                "bridges_in_torrc":                  v_bnum.get(),
                "shuffle_bridges":                   v_shuf.get(),
                "dns_over_tor":                      v_dns.get(),
                "max_circuit_dirtiness":             v_mcd.get(),
                "new_circuit_period":                v_ncp.get(),
                "num_entry_guards":                  v_neg.get(),
                "keep_alive_enabled":                v_kae.get(),
                "keep_alive_interval":               v_kai.get(),
                "watchdog_enabled":                  v_wde.get(),
                "watchdog_interval":                 v_wdi.get(),
                "exit_nodes_enabled":                v_ene.get(),
                "exit_nodes_countries":              v_enc.get().strip(),
                "strict_exit_nodes":                 v_sne.get(),
                "auto_proxy_on_connect":             v_apc.get(),
                "exp_connection_padding":            v_cp.get(),
                "exp_reduced_connection_padding":    v_rcp.get(),
                "exp_circuit_stream_timeout":        v_cst.get(),
                "exp_socks_timeout":                 v_st.get(),
                "exp_isolate_dest_addr":             v_ida.get(),
                "exp_isolate_dest_port":             v_idp.get(),
                "exp_safe_logging":                  v_sl.get(),
                "exp_avoid_disk_writes":             v_adw.get(),
                "exp_hardware_accel":                v_ha.get(),
                "exp_client_dns_reject_internal":    v_cdri.get(),
                "exp_fascist_firewall":              v_ff.get(),
                "exp_firewall_ports":                v_fp.get().strip(),
                "exp_reachable_addresses":           v_ra.get().strip(),
                "exp_num_cpus":                      v_nc.get(),
                "exp_exclude_nodes":                 v_en.get().strip(),
                "exp_exclude_exit_nodes":            v_een.get().strip(),
                "exp_no_exit_stream_ports":          v_nesp.get().strip(),
                "exp_use_entry_guards_as_dir_guards": v_ueag.get(),
                "exp_path_bias_circ_threshold":      v_pbct.get(),
            })
            save_config(cfg, cfg.get('extract_dir', ''))
            on_save(cfg)
            w.destroy()

        tk.Button(bf, text="✔  Apply & Save", command=_apply,
                  bg=C["ACC"], fg=C["BLK"], font=('Segoe UI', 10, 'bold'),
                  relief="flat", cursor="hand2",
                  activebackground="#B4BEFE").pack(side='left', fill='x',
                                                   expand=True, padx=(0, 5), ipady=4)
        tk.Button(bf, text="Cancel", command=w.destroy,
                  bg=C["BTN"], fg=C["FG"], font=('Segoe UI', 10),
                  relief="flat", cursor="hand2").pack(side='left', ipady=4, padx=40)


BRIDGE_DATA = [
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

FRESH_DATA = [(c, t, v, u) for c, t, v, u in BRIDGE_DATA if c == "Fresh (72h)"]


class TorClientGUI:

    TOR_URL      = ("https://github.com/Delta-Kronecker/Tor-Expert-Bundle/raw/refs/heads/main/"
                    "tor-expert-bundle-windows-x86_64-15.0.6.tar.gz")
    TOR_FALLBACK = ("https://archive.torproject.org/tor-package-archive/torbrowser/15.0.6/"
                    "tor-expert-bundle-windows-x86_64-15.0.6.tar.gz")

    def __init__(self, root):
        self.root = root
        self.root.title("Tor Client")
        self.root.configure(bg=C["BG"])

        self.cfg = load_config()
        if "extract_dir" not in self.cfg:
            dlg = FolderSetupDialog(root)
            self.cfg["extract_dir"] = dlg.result or FolderSetupDialog.DEFAULT
            save_config(self.cfg, self.cfg["extract_dir"])

        self.extract_dir   = self.cfg["extract_dir"]
        self.archive_name  = os.path.join(
            os.path.dirname(self.extract_dir), "tor-expert-bundle.tar.gz")
        self.bridges_dir   = os.path.join(self.extract_dir, "bridges")
        self.logs_dir      = os.path.join(self.extract_dir, "logs")

        self.tor_process           = None
        self.tor_connected         = False
        self.connect_time          = None
        self._uptime_id            = None
        self._auto_test_id         = None
        self._watchdog_id          = None
        self._keepalive_id         = None
        self._auto_connect_active  = False
        self._http_proxy_stop      = None
        self._tray_hwnd            = 0

        self.status_var         = tk.StringVar(value="Status: Initializing…")
        self.proxy_var          = tk.BooleanVar()
        self.source_var         = tk.StringVar(value="Delta-Kronecker Tor-Bridges-Collector")
        self.cat_var            = tk.StringVar(value="Tested & Active")
        self.trans_var          = tk.StringVar()
        self.ip_var             = tk.StringVar(value="IPv4")
        self.conn_progress_var  = tk.IntVar(value=0)
        self.conn_pct_var       = tk.StringVar(value="0%")
        self.stat_ip_var        = tk.StringVar(value="—")
        self.stat_country_var   = tk.StringVar(value="—")
        self.stat_uptime_var    = tk.StringVar(value="—")
        self.stat_tor_var       = tk.StringVar(value="—")
        self._dl_bar_var        = tk.IntVar(value=0)
        self.bridge_count_var   = tk.StringVar(value="")
        self.bridge_updated_var = tk.StringVar(value="")

        self.setup_theme()
        self.setup_ui()

        self.root.update()
        self._set_window_icon()
        apply_dark_titlebar(self.root)
        self.root.geometry("750x980")
        self.root.protocol("WM_DELETE_WINDOW", self._on_close_btn)

        threading.Thread(target=self.auto_initialize, daemon=True).start()

    def _on_close_btn(self):
        dlg = tk.Toplevel(self.root)
        dlg.title("Close")
        dlg.geometry("320x130")
        dlg.configure(bg=C["BG"])
        dlg.resizable(False, False)
        dlg.grab_set()
        dlg.transient(self.root)
        dlg.update()
        apply_dark_titlebar(dlg)
        tk.Label(dlg, text="What would you like to do?",
                 font=('Segoe UI', 10), bg=C["BG"], fg=C["FG"]).pack(pady=(18, 10))
        bf = tk.Frame(dlg, bg=C["BG"])
        bf.pack(padx=20, fill='x')

        def _tray():
            dlg.destroy()
            self.root.withdraw()
            if not getattr(self, '_tray_running', False):
                self._tray_running = True
                threading.Thread(target=self._tray_icon_loop, daemon=True).start()

        def _quit():
            dlg.destroy()
            self.stop_tor()
            self.root.destroy()

        tk.Button(bf, text="🗕  Minimize to Tray", command=_tray,
                  bg=C["BTN2"], fg=C["FG"], font=('Segoe UI', 9, 'bold'),
                  relief="flat", cursor="hand2"
                  ).pack(side='left', fill='x', expand=True, padx=(0, 4), ipady=4)
        tk.Button(bf, text="✕  Quit", command=_quit,
                  bg="#4A1E1E", fg=C["RED"], font=('Segoe UI', 9, 'bold'),
                  relief="flat", cursor="hand2"
                  ).pack(side='left', fill='x', expand=True, padx=(4, 0), ipady=4)

    def _tray_icon_loop(self):
        try:
            NIM_ADD     = 0x00000000
            NIM_DELETE  = 0x00000002
            NIF_ICON    = 0x00000002
            NIF_TIP     = 0x00000004
            NIF_MESSAGE = 0x00000001
            TRAY_MSG    = 0x0400 + 20
            ID_SHOW     = 1001
            ID_QUIT     = 1002

            class NOTIFYICONDATA(ctypes.Structure):
                _fields_ = [
                    ("cbSize",           ctypes.wintypes.DWORD),
                    ("hWnd",             ctypes.wintypes.HWND),
                    ("uID",              ctypes.wintypes.UINT),
                    ("uFlags",           ctypes.wintypes.UINT),
                    ("uCallbackMessage", ctypes.wintypes.UINT),
                    ("hIcon",            ctypes.wintypes.HICON),
                    ("szTip",            ctypes.c_wchar * 128),
                ]

            user32  = ctypes.windll.user32
            shell32 = ctypes.windll.shell32

            WNDPROCTYPE = ctypes.WINFUNCTYPE(
                ctypes.c_long, ctypes.wintypes.HWND,
                ctypes.wintypes.UINT, ctypes.wintypes.WPARAM, ctypes.wintypes.LPARAM)

            def wnd_proc(hwnd, msg, wparam, lparam):
                if msg == TRAY_MSG:
                    if lparam == 0x0203:
                        self.root.after(0, self.root.deiconify)
                    elif lparam == 0x0205:
                        hmenu = user32.CreatePopupMenu()
                        user32.AppendMenuW(hmenu, 0, ID_SHOW, "Show Window")
                        user32.AppendMenuW(hmenu, 0, ID_QUIT, "Quit")
                        pt = ctypes.wintypes.POINT()
                        user32.GetCursorPos(ctypes.byref(pt))
                        user32.SetForegroundWindow(hwnd)
                        cmd = user32.TrackPopupMenu(
                            hmenu, 0x0100, pt.x, pt.y, 0, hwnd, None)
                        user32.DestroyMenu(hmenu)
                        if cmd == ID_SHOW:
                            self.root.after(0, self.root.deiconify)
                        elif cmd == ID_QUIT:
                            self.root.after(0, lambda: (self.stop_tor(), self.root.destroy()))
                elif msg == 0x0002:
                    user32.PostQuitMessage(0)
                return user32.DefWindowProcW(hwnd, msg, wparam, lparam)

            wnd_proc_ptr = WNDPROCTYPE(wnd_proc)

            class WNDCLASSEX(ctypes.Structure):
                _fields_ = [
                    ("cbSize",        ctypes.wintypes.UINT),
                    ("style",         ctypes.wintypes.UINT),
                    ("lpfnWndProc",   WNDPROCTYPE),
                    ("cbClsExtra",    ctypes.c_int),
                    ("cbWndExtra",    ctypes.c_int),
                    ("hInstance",     ctypes.wintypes.HANDLE),
                    ("hIcon",         ctypes.wintypes.HANDLE),
                    ("hCursor",       ctypes.wintypes.HANDLE),
                    ("hbrBackground", ctypes.wintypes.HANDLE),
                    ("lpszMenuName",  ctypes.c_wchar_p),
                    ("lpszClassName", ctypes.c_wchar_p),
                    ("hIconSm",       ctypes.wintypes.HANDLE),
                ]

            wc = WNDCLASSEX()
            wc.cbSize        = ctypes.sizeof(WNDCLASSEX)
            wc.lpfnWndProc   = wnd_proc_ptr
            wc.lpszClassName = "TorClientTray"
            wc.hInstance     = ctypes.windll.kernel32.GetModuleHandleW(None)
            user32.RegisterClassExW(ctypes.byref(wc))

            hwnd = user32.CreateWindowExW(
                0, "TorClientTray", "TorClientTray",
                0, 0, 0, 0, 0, None, None, wc.hInstance, None)
            self._tray_hwnd = hwnd

            hIcon = _load_tray_icon()

            nid = NOTIFYICONDATA()
            nid.cbSize           = ctypes.sizeof(NOTIFYICONDATA)
            nid.hWnd             = hwnd
            nid.uID              = 1
            nid.uFlags           = NIF_ICON | NIF_TIP | NIF_MESSAGE
            nid.uCallbackMessage = TRAY_MSG
            nid.hIcon            = hIcon
            nid.szTip            = "Tor Client"
            shell32.Shell_NotifyIconW(NIM_ADD, ctypes.byref(nid))

            msg = ctypes.wintypes.MSG()
            while user32.GetMessageW(ctypes.byref(msg), None, 0, 0) != 0:
                user32.TranslateMessage(ctypes.byref(msg))
                user32.DispatchMessageW(ctypes.byref(msg))

            shell32.Shell_NotifyIconW(NIM_DELETE, ctypes.byref(nid))
        except Exception:
            self.root.after(0, self.root.deiconify)
        finally:
            self._tray_running = False

    def _notify(self, title: str, msg: str):
        threading.Thread(
            target=_win_notify, args=(title, msg, self._tray_hwnd),
            daemon=True).start()

    def setup_theme(self):
        s = ttk.Style()
        s.theme_use('clam')
        s.configure('.', background=C["BG"], foreground=C["FG"], font=('Segoe UI', 10))
        s.configure('TLabel',       background=C["BG"], foreground=C["FG"])
        s.configure('TLabelframe',  background=C["BG"], foreground=C["ACC"],
                    bordercolor=C["BTN"])
        s.configure('TLabelframe.Label', background=C["BG"], foreground=C["ACC"],
                    font=('Segoe UI', 10, 'bold'))
        s.configure('TCombobox',    fieldbackground=C["BTN"], background=C["BTN"],
                    foreground=C["FG"], borderwidth=0, arrowcolor=C["FG"],
                    selectbackground=C["BTN"], selectforeground=C["FG"])
        s.map('TCombobox',
              fieldbackground=[('readonly', C["BTN"])],
              foreground=[('readonly', C["FG"])],
              background=[('readonly', C["BTN"])])
        s.configure('TCheckbutton', background=C["BG"], foreground=C["FG"],
                    font=('Segoe UI', 10))
        s.map('TCheckbutton', background=[('active', C["BG"])])
        s.configure('Horizontal.TProgressbar',
                    background=C["GRN"], troughcolor=C["BTN"],
                    bordercolor=C["BG"], lightcolor=C["GRN"], darkcolor=C["GRN"])
        s.configure('Stat.TLabel',    background=C["DRK"], foreground="#A6ADC8",
                    font=('Segoe UI', 9))
        s.configure('StatVal.TLabel', background=C["DRK"], foreground=C["GRN"],
                    font=('Segoe UI', 9, 'bold'))
        self.root.option_add('*TCombobox*Listbox.background', C["BTN"])
        self.root.option_add('*TCombobox*Listbox.foreground', C["FG"])
        self.root.option_add('*TCombobox*Listbox.selectBackground', C["ACC"])
        self.root.option_add('*TCombobox*Listbox.selectForeground', C["BLK"])

    def setup_ui(self):
        BG = C["BG"]; FG = C["FG"]; BTN = C["BTN"]

        top = tk.Frame(self.root, bg=BG)
        top.pack(fill='x', padx=40, pady=(10, 0))
        top.columnconfigure(0, weight=1)
        top.columnconfigure(1, weight=1)
        tk.Button(top, text="📖  How to Use",
                  command=self.show_help_window,
                  bg=C["BTN2"], fg=FG, font=('Segoe UI', 9, 'bold'),
                  relief="flat", cursor="hand2",
                  activebackground="#585B70"
                  ).grid(row=0, column=0, sticky="ew", padx=(0, 3), ipady=3)
        tk.Button(top, text="⚙️  Settings",
                  command=self.show_settings_window,
                  bg=C["BTN2"], fg=FG, font=('Segoe UI', 9, 'bold'),
                  relief="flat", cursor="hand2",
                  activebackground="#585B70"
                  ).grid(row=0, column=1, sticky="ew", padx=(3, 0), ipady=3)

        tk.Label(self.root, text="Tor Client",
                 font=('Segoe UI', 17, 'bold'), bg=BG, fg=C["ACC"]).pack(pady=(6, 1))

        tk.Label(self.root, textvariable=self.status_var, wraplength=680,
                 font=('Segoe UI', 9, 'italic'), bg=BG, fg=C["RED"]).pack(pady=2)

        self._dl_outer = tk.Frame(self.root, bg=BG)
        dl_hdr = tk.Frame(self._dl_outer, bg=BG)
        dl_hdr.pack(fill='x', padx=40)
        self._dl_title_lbl = tk.Label(dl_hdr, text="", bg=BG, fg=FG, font=('Segoe UI', 9))
        self._dl_title_lbl.pack(side='left')
        self._dl_pct_lbl = tk.Label(dl_hdr, text="", bg=BG, fg=C["GRN"],
                                    font=('Segoe UI', 9, 'bold'))
        self._dl_pct_lbl.pack(side='right')
        ttk.Progressbar(self._dl_outer, variable=self._dl_bar_var,
                        maximum=100, mode='determinate'
                        ).pack(fill='x', padx=40, pady=(2, 3))

        self.update_btn = tk.Button(
            self.root, text="🔄  Update All Bridges",
            command=self.start_download_bridges,
            bg=BTN, fg=FG, font=('Segoe UI', 9), relief="flat", cursor="hand2",
            activebackground=C["BTN2"])
        self.update_btn.pack(pady=3, fill='x', padx=40)

        frame = ttk.LabelFrame(self.root, text=" Bridge Configuration ")
        frame.pack(pady=5, padx=40, fill='x')

        ttk.Label(frame, text="Bridge Source:").grid(
            row=0, column=0, padx=12, pady=5, sticky="w")
        self.source_combo = ttk.Combobox(
            frame, textvariable=self.source_var,
            values=["Default (Built-in)", "Delta-Kronecker Tor-Bridges-Collector"],
            state="readonly")
        self.source_combo.grid(row=0, column=1, padx=12, pady=5, sticky="ew")
        self.source_combo.bind("<<ComboboxSelected>>", self.on_source_changed)

        self.cat_label = ttk.Label(frame, text="Category:")
        self.cat_label.grid(row=1, column=0, padx=12, pady=5, sticky="w")
        self.cat_combo = ttk.Combobox(
            frame, textvariable=self.cat_var,
            values=["Tested & Active", "Fresh (72h)", "Full Archive"],
            state="readonly")
        self.cat_combo.grid(row=1, column=1, padx=12, pady=5, sticky="ew")
        self.cat_combo.bind("<<ComboboxSelected>>", self._on_bridge_selection_change)

        ttk.Label(frame, text="Transport:").grid(
            row=2, column=0, padx=12, pady=5, sticky="w")
        self.trans_combo = ttk.Combobox(frame, textvariable=self.trans_var, state="readonly")
        self.trans_combo.grid(row=2, column=1, padx=12, pady=5, sticky="ew")
        self.trans_combo.bind("<<ComboboxSelected>>", self._on_bridge_selection_change)

        ttk.Label(frame, text="IP Version:").grid(
            row=3, column=0, padx=12, pady=5, sticky="w")
        ip_combo = ttk.Combobox(frame, textvariable=self.ip_var,
                                values=["Both", "IPv4", "IPv6"], state="readonly")
        ip_combo.grid(row=3, column=1, padx=12, pady=5, sticky="ew")
        ip_combo.bind("<<ComboboxSelected>>", self._on_bridge_selection_change)

        info_row = tk.Frame(frame, bg=BG)
        info_row.grid(row=4, column=0, columnspan=2, sticky="ew", padx=12, pady=(0, 6))
        ttk.Label(info_row, text="Bridges available:", style='Stat.TLabel').pack(side='left')
        ttk.Label(info_row, textvariable=self.bridge_count_var,
                  style='StatVal.TLabel').pack(side='left', padx=(4, 20))
        ttk.Label(info_row, text="Last updated:", style='Stat.TLabel').pack(side='left')
        ttk.Label(info_row, textvariable=self.bridge_updated_var,
                  style='StatVal.TLabel').pack(side='left', padx=(4, 0))

        frame.columnconfigure(1, weight=1)
        self.update_transports()
        self.on_source_changed()

        bf = tk.Frame(self.root, bg=BG)
        bf.pack(fill='x', padx=40, pady=6)
        for i in range(3):
            bf.columnconfigure(i, weight=1)

        self.auto_btn = tk.Button(bf, text="🤖  Auto Connect",
                                  command=self.start_auto_connect,
                                  bg="#1E3A4A", fg="#89DCEB",
                                  font=('Segoe UI', 11, 'bold'),
                                  relief="flat", cursor="hand2",
                                  activebackground="#2A4F64",
                                  activeforeground="#89DCEB")
        self.auto_btn.grid(row=0, column=0, padx=(0, 3), sticky="ew", ipady=5)

        self.start_btn = tk.Button(bf, text="▶  Start Tor",
                                   command=self.start_tor_thread,
                                   bg=C["ACC"], fg=C["BLK"],
                                   font=('Segoe UI', 11, 'bold'),
                                   relief="flat", cursor="hand2",
                                   activebackground="#B4BEFE",
                                   activeforeground=C["BLK"])
        self.start_btn.grid(row=0, column=1, padx=3, sticky="ew", ipady=5)

        self.stop_btn = tk.Button(bf, text="■  Stop",
                                  command=self.stop_tor,
                                  bg=BTN, fg=FG,
                                  font=('Segoe UI', 11, 'bold'),
                                  relief="flat", cursor="hand2",
                                  activebackground=C["BTN2"],
                                  activeforeground=FG)
        self.stop_btn.grid(row=0, column=2, padx=(3, 0), sticky="ew", ipady=5)

        pf = tk.Frame(self.root, bg=BG)
        pf.pack(fill='x', padx=40, pady=(0, 3))
        pf.columnconfigure(0, weight=3)
        pf.columnconfigure(1, weight=1)

        self.proxy_btn = tk.Button(
            pf,
            text="🌐  System Proxy  ●  OFF",
            command=self.toggle_proxy_button,
            bg="#2A2A3E", fg="#6C7086",
            font=('Segoe UI', 10, 'bold'),
            relief="flat", cursor="hand2",
            activebackground=BTN)
        self.proxy_btn.grid(row=0, column=0, sticky="ew", padx=(0, 3), ipady=4)

        self.newnym_btn = tk.Button(
            pf,
            text="🔄  New Circuit",
            command=self.request_new_circuit,
            bg=BTN, fg=C["YLW"],
            font=('Segoe UI', 10, 'bold'),
            relief="flat", cursor="hand2",
            activebackground=C["BTN2"])
        self.newnym_btn.grid(row=0, column=1, sticky="ew", padx=(3, 0), ipady=4)

        cp = tk.Frame(self.root, bg=BG)
        cp.pack(fill='x', padx=40, pady=(4, 0))
        ttk.Label(cp, text="Connection Progress:").pack(side='left')
        ttk.Label(cp, textvariable=self.conn_pct_var,
                  font=('Segoe UI', 10, 'bold'),
                  foreground=C["GRN"]).pack(side='right')
        ttk.Progressbar(self.root, variable=self.conn_progress_var,
                        maximum=100, mode='determinate'
                        ).pack(fill='x', padx=40, pady=(2, 4))

        stats_lf = ttk.LabelFrame(self.root, text=" Connection Stats ")
        stats_lf.pack(pady=4, padx=40, fill='x')

        sg = tk.Frame(stats_lf, bg=C["DRK"])
        sg.pack(fill='x', padx=6, pady=(6, 4))
        sg.columnconfigure(1, weight=1)
        sg.columnconfigure(3, weight=1)

        def _sl(t, r, c):
            ttk.Label(sg, text=t, style='Stat.TLabel').grid(
                row=r, column=c, padx=(12, 3), pady=4, sticky="w")

        def _sv(var, r, c):
            ttk.Label(sg, textvariable=var, style='StatVal.TLabel').grid(
                row=r, column=c, padx=(0, 12), pady=4, sticky="w")

        _sl("Exit IP:",    0, 0); _sv(self.stat_ip_var,      0, 1)
        _sl("Country:",    0, 2); _sv(self.stat_country_var,  0, 3)
        _sl("Uptime:",     1, 0); _sv(self.stat_uptime_var,   1, 1)
        _sl("Tor Status:", 1, 2); _sv(self.stat_tor_var,      1, 3)

        btn_row = tk.Frame(stats_lf, bg=C["BG"])
        btn_row.pack(padx=6, pady=(2, 8), fill='x')
        btn_row.columnconfigure(0, weight=1)
        btn_row.columnconfigure(1, weight=1)

        self.test_btn = tk.Button(
            btn_row, text="🔍  Test Connection via Tor",
            command=self.start_test_connection,
            bg=BTN, fg=FG, font=('Segoe UI', 9, 'bold'),
            relief="flat", cursor="hand2",
            activebackground=C["BTN2"])
        self.test_btn.grid(row=0, column=0, sticky="ew", padx=(0, 3))

        self.save_log_btn = tk.Button(
            btn_row, text="💾  Save Log",
            command=self.save_log_to_file,
            bg=BTN, fg=C["ACC"], font=('Segoe UI', 9, 'bold'),
            relief="flat", cursor="hand2",
            activebackground=C["BTN2"])
        self.save_log_btn.grid(row=0, column=1, sticky="ew", padx=(3, 0))

        tk.Label(self.root, text="Tor Logs:", bg=BG, fg=FG,
                 font=('Segoe UI', 9)).pack(anchor='w', padx=40, pady=(4, 2))
        log_frame = tk.Frame(self.root, bg=C["BLK"])
        log_frame.pack(fill='both', expand=True, padx=40, pady=(0, 8))

        self.log_text = tk.Text(log_frame, font=('Consolas', 9), wrap='word',
                                state='disabled', bg=C["BLK"], fg=C["GRN"],
                                bd=0, padx=10, pady=8)
        self.log_text.tag_configure("warn",   foreground=C["YLW"])
        self.log_text.tag_configure("err",    foreground=C["RED"])
        self.log_text.tag_configure("notice", foreground=C["GRN"])
        self.log_text.tag_configure("info",   foreground="#A6ADC8")
        self.log_text.tag_configure("auto",   foreground=C["ACC"])
        self.log_text.tag_configure("test",   foreground=C["ORG"])

        sb_log = ttk.Scrollbar(log_frame, command=self.log_text.yview)
        self.log_text.configure(yscrollcommand=sb_log.set)
        self.log_text.pack(side='left', fill='both', expand=True)
        sb_log.pack(side='right', fill='y')

    def _on_bridge_selection_change(self, event=None):
        self.update_transports(event)
        self._refresh_bridge_info()

    def _refresh_bridge_info(self):
        cat   = self.cat_var.get()
        trans = self.trans_var.get()
        ip    = self.ip_var.get()
        src   = self.source_var.get()
        count = 0
        mtime_str = "—"
        if src != "Default (Built-in)":
            for c, t, v, _ in BRIDGE_DATA:
                if c == cat and t == trans and (ip == "Both" or ip == v):
                    fn = os.path.join(self.bridges_dir, self.get_safe_filename(c, t, v))
                    if os.path.exists(fn):
                        try:
                            with open(fn, encoding="utf-8") as f:
                                count += sum(1 for l in f if l.strip())
                            mt = os.path.getmtime(fn)
                            mtime_str = time.strftime("%Y-%m-%d %H:%M", time.localtime(mt))
                        except Exception:
                            pass
        self.bridge_count_var.set(str(count) if count else "—")
        self.bridge_updated_var.set(mtime_str)

    def show_settings_window(self):
        def _on_save(new_cfg):
            self.cfg.update(new_cfg)
            if self.tor_connected:
                self._restart_keepalive()
                self._restart_watchdog()
        SettingsWindow(self.root, self.cfg, _on_save, on_clear_data=self._clear_data_dir)

    def _clear_data_dir(self):
        if self.tor_connected:
            messagebox.showwarning("Warning",
                "Stop Tor first before clearing data.", parent=self.root)
            return
        data_dir = os.path.join(self.extract_dir, "data")
        if os.path.isdir(data_dir):
            try:
                shutil.rmtree(data_dir)
                self.append_log("[Maintenance] Data directory cleared.\n", "info")
            except Exception as e:
                self.append_log(f"[Maintenance] Clear failed: {e}\n", "err")

    def _set_window_icon(self):
        ico = resource_path("icon.ico")
        if os.path.exists(ico):
            try:
                self.root.iconbitmap(default=ico)
                self._ico_path = ico
                return
            except Exception:
                pass
        try:
            xbm = ("#define i_w 16\n#define i_h 16\n"
                   "static char i_b[] = {"
                   "0xf0,0x0f,0xfe,0x7f,0xff,0xff,0xff,0xff,"
                   "0xff,0xff,0xfe,0x7f,0xfe,0x7f,0xfe,0x7f,"
                   "0xfe,0x7f,0xfe,0x7f,0xfe,0x7f,0xfe,0x7f,"
                   "0xff,0xff,0xff,0xff,0xfe,0x7f,0xf0,0x0f};")
            img = tk.PhotoImage(data=xbm, format="xbm")
            self.root.iconphoto(True, img)
            self._icon_ref = img
        except Exception:
            pass
        self._ico_path = None

    def _apply_icon_to(self, win):
        if getattr(self, '_ico_path', None):
            try:
                win.iconbitmap(default=self._ico_path)
                return
            except Exception:
                pass
        if hasattr(self, '_icon_ref'):
            try:
                win.iconphoto(True, self._icon_ref)
            except Exception:
                pass

    def _show_dl(self, title="Downloading…"):
        self._dl_title_lbl.configure(text=title)
        self._dl_pct_lbl.configure(text="0%")
        self._dl_bar_var.set(0)
        if not self._dl_outer.winfo_ismapped():
            self._dl_outer.pack(fill='x', before=self.update_btn)

    def _set_dl(self, pct, title=None):
        self._dl_bar_var.set(pct)
        self._dl_pct_lbl.configure(text=f"{pct}%")
        if title:
            self._dl_title_lbl.configure(text=title)

    def _hide_dl(self, delay=900):
        self.root.after(delay, self._dl_outer.pack_forget)

    def append_log(self, msg, tag=None):
        if tag is None:
            low = msg.lower()
            if "[warn]" in low or " warn " in low:
                tag = "warn"
            elif "[err]" in low or " err " in low or "[error]" in low:
                tag = "err"
            elif "[auto]" in low:
                tag = "auto"
            elif "[test]" in low:
                tag = "test"
            elif "[notice]" in low:
                tag = "notice"
            else:
                tag = "info"
        self.log_text.configure(state='normal')
        self.log_text.insert(tk.END, msg, tag)
        self.log_text.see(tk.END)
        self.log_text.configure(state='disabled')

    def save_log_to_file(self):
        try:
            os.makedirs(self.logs_dir, exist_ok=True)
            stamp = time.strftime("%Y%m%d_%H%M%S")
            fpath = os.path.join(self.logs_dir, f"tor_log_{stamp}.txt")
            with open(fpath, "w", encoding="utf-8") as f:
                f.write(self.log_text.get("1.0", tk.END))
            self.append_log(f"[Log] Saved to {fpath}\n", "info")
        except Exception as e:
            self.append_log(f"[Log] Save failed: {e}\n", "err")

    def update_status(self, msg):
        self.status_var.set(f"Status: {msg}")
        self.root.update_idletasks()

    def update_conn_progress(self, v):
        self.conn_progress_var.set(v)
        self.conn_pct_var.set(f"{v}%")

    def _tick_uptime(self):
        if self.connect_time is None:
            return
        e = int(time.time() - self.connect_time)
        h, r = divmod(e, 3600); m, s = divmod(r, 60)
        self.stat_uptime_var.set(f"{h:02d}:{m:02d}:{s:02d}")
        self._uptime_id = self.root.after(1000, self._tick_uptime)

    def _start_uptime(self):
        self.connect_time = time.time()
        self._tick_uptime()

    def _stop_uptime(self):
        if self._uptime_id:
            self.root.after_cancel(self._uptime_id)
            self._uptime_id = None
        self.connect_time = None
        self.stat_uptime_var.set("—")

    def _schedule_auto_test(self):
        if not self.tor_connected:
            return
        threading.Thread(target=self._run_test_connection, daemon=True).start()
        self._auto_test_id = self.root.after(60_000, self._schedule_auto_test)

    def _cancel_auto_test(self):
        if self._auto_test_id:
            self.root.after_cancel(self._auto_test_id)
            self._auto_test_id = None

    def start_test_connection(self):
        if not self.tor_connected:
            self.append_log("[Test] Not connected yet.\n")
            return
        self.test_btn.configure(text="Testing…", state='disabled')
        threading.Thread(target=self._run_test_connection, daemon=True).start()

    def _run_test_connection(self):
        self.root.after(0, self.append_log, "[Test] Checking connection…\n")
        try:
            raw     = socks5_request("check.torproject.org", 443, "/api/ip", timeout=15)
            data    = json.loads(raw.strip())
            exit_ip = data.get("IP", "")
            is_tor  = data.get("IsTor", False)
            self.root.after(0, self.stat_ip_var.set, exit_ip or "—")
            self.root.after(0, self.stat_tor_var.set,
                            "✅ Confirmed Tor" if is_tor else "⚠️ Not Tor")
            self.root.after(0, self.append_log,
                            f"[Test] Exit IP: {exit_ip}  Tor: {is_tor}\n")
            country = self._lookup_country(exit_ip)
            self.root.after(0, self.stat_country_var.set, country)
            self.root.after(0, self.append_log, f"[Test] Country: {country}\n")
            self.root.after(0, self.update_status,
                            "Auto-test: " + ("Tor ✅" if is_tor else "⚠️ Not Tor"))
        except Exception as e:
            self.root.after(0, self.append_log, f"[Test] Failed: {e}\n")
        finally:
            self.root.after(0, self.test_btn.configure,
                            {"text": "🔍  Test Connection via Tor", "state": "normal"})

    def _lookup_country(self, ip: str) -> str:
        services = [
            ("ipapi.co",      f"/{ip}/json/",   "country_code", True),
            ("ip-api.com",    f"/json/{ip}",     "countryCode",  True),
            ("ipinfo.io",     f"/{ip}/json",     "country",      True),
            ("ipwho.is",      f"/{ip}",          "country_code", True),
            ("freeipapi.com", f"/api/json/{ip}", "countryCode",  True),
        ]
        for host, path, key, use_ssl in services:
            try:
                raw = socks5_request(host, 443, path, use_ssl=use_ssl, timeout=12)
                if raw.strip().startswith("{"):
                    val = json.loads(raw.strip()).get(key, "")
                    if val and len(val) >= 2:
                        return val.upper()
            except Exception:
                continue
        return "?"

    def request_new_circuit(self):
        if not self.tor_connected:
            self.append_log("[Circuit] Not connected.\n", "warn")
            return
        threading.Thread(target=self._send_newnym, daemon=True).start()

    def _send_newnym(self):
        try:
            cookie_file = os.path.join(self.extract_dir, "data", "control_auth_cookie")
            with open(cookie_file, "rb") as f:
                cookie_hex = f.read().hex()
            s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            s.settimeout(5)
            s.connect(("127.0.0.1", TOR_CTRL_PORT))
            s.sendall(f"AUTHENTICATE {cookie_hex}\r\n".encode())
            s.recv(256)
            s.sendall(b"SIGNAL NEWNYM\r\n")
            resp = s.recv(256).decode(errors="replace")
            s.close()
            if "250" in resp:
                self.root.after(0, self.append_log,
                                "[Circuit] New circuit requested ✅\n", "notice")
                self.root.after(0, self.update_status, "New circuit obtained.")
                self._notify("Tor Client", "New circuit obtained.")
            else:
                self.root.after(0, self.append_log,
                                f"[Circuit] Response: {resp.strip()}\n", "warn")
        except Exception as e:
            self.root.after(0, self.append_log, f"[Circuit] Failed: {e}\n", "err")

    def _start_watchdog(self):
        self._cancel_watchdog()
        interval = self.cfg.get("watchdog_interval", 30) * 1000
        self._watchdog_id = self.root.after(interval, self._watchdog_tick)

    def _cancel_watchdog(self):
        if self._watchdog_id:
            self.root.after_cancel(self._watchdog_id)
            self._watchdog_id = None

    def _restart_watchdog(self):
        self._cancel_watchdog()
        if self.tor_connected:
            self._start_watchdog()

    def _watchdog_tick(self):
        if not self.cfg.get("watchdog_enabled", True):
            return
        if self.tor_process is not None and self.tor_process.poll() is not None:
            self.root.after(0, self.append_log,
                            "[Watchdog] Tor process died — restarting…\n", "warn")
            self.root.after(0, self.update_status, "Watchdog: restarting Tor…")
            self._notify("Tor Client", "Tor process died — restarting…")
            self.tor_process   = None
            self.tor_connected = False
            threading.Thread(target=self._watchdog_restart, daemon=True).start()
            return
        interval = self.cfg.get("watchdog_interval", 30) * 1000
        self._watchdog_id = self.root.after(interval, self._watchdog_tick)

    def _watchdog_restart(self):
        time.sleep(2)
        self.run_tor()

    def _start_keepalive(self):
        self._cancel_keepalive()
        if not self.cfg.get("keep_alive_enabled", True):
            return
        interval = self.cfg.get("keep_alive_interval", 120) * 1000
        self._keepalive_id = self.root.after(interval, self._keepalive_tick)

    def _cancel_keepalive(self):
        if self._keepalive_id:
            self.root.after_cancel(self._keepalive_id)
            self._keepalive_id = None

    def _restart_keepalive(self):
        self._cancel_keepalive()
        if self.tor_connected:
            self._start_keepalive()

    def _keepalive_tick(self):
        if not self.tor_connected or not self.cfg.get("keep_alive_enabled", True):
            return
        threading.Thread(target=self._do_keepalive, daemon=True).start()
        interval = self.cfg.get("keep_alive_interval", 120) * 1000
        self._keepalive_id = self.root.after(interval, self._keepalive_tick)

    def _do_keepalive(self):
        try:
            socks5_request("check.torproject.org", 443, "/api/ip", timeout=10)
        except Exception:
            pass

    def _start_http_proxy(self):
        if self._http_proxy_stop is not None:
            return
        ev = threading.Event()
        self._http_proxy_stop = ev
        threading.Thread(target=run_http_proxy_server, args=(ev,), daemon=True).start()

    def _stop_http_proxy(self):
        if self._http_proxy_stop is not None:
            self._http_proxy_stop.set()
            self._http_proxy_stop = None

    def toggle_proxy_button(self):
        new = not self.proxy_var.get()
        self.proxy_var.set(new)
        self.set_system_proxy(new)
        self._refresh_proxy_btn()

    def _refresh_proxy_btn(self):
        if self.proxy_var.get():
            self.proxy_btn.configure(
                text="🌐  System Proxy  ●  ON",
                bg="#1E4620", fg=C["GRN"],
                activebackground="#2A5C2E", activeforeground=C["GRN"])
        else:
            self.proxy_btn.configure(
                text="🌐  System Proxy  ●  OFF",
                bg="#2A2A3E", fg="#6C7086",
                activebackground=C["BTN"], activeforeground=C["FG"])

    def set_system_proxy(self, enable):
        try:
            key = winreg.OpenKey(
                winreg.HKEY_CURRENT_USER,
                r'Software\Microsoft\Windows\CurrentVersion\Internet Settings',
                0, winreg.KEY_ALL_ACCESS)
            if enable:
                self._start_http_proxy()
                proxy_str = f'http=127.0.0.1:{HTTP_PROXY_PORT};https=127.0.0.1:{HTTP_PROXY_PORT}'
                winreg.SetValueEx(key, 'ProxyEnable',   0, winreg.REG_DWORD, 1)
                winreg.SetValueEx(key, 'ProxyServer',   0, winreg.REG_SZ, proxy_str)
                winreg.SetValueEx(key, 'ProxyOverride', 0, winreg.REG_SZ,
                                  '127.0.0.1;localhost;<local>')
            else:
                self._stop_http_proxy()
                winreg.SetValueEx(key, 'ProxyEnable', 0, winreg.REG_DWORD, 0)
                winreg.SetValueEx(key, 'ProxyServer',  0, winreg.REG_SZ, '')
            winreg.CloseKey(key)
            ctypes.windll.wininet.InternetSetOptionW(0, 39, 0, 0)
            ctypes.windll.wininet.InternetSetOptionW(0, 37, 0, 0)
        except Exception:
            pass

    def _auto_enable_proxy(self):
        if self.cfg.get("auto_proxy_on_connect", True) and not self.proxy_var.get():
            self.proxy_var.set(True)
            self.set_system_proxy(True)
            self._refresh_proxy_btn()
            self.append_log("[Proxy] System proxy enabled automatically.\n", "notice")

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
        self._refresh_bridge_info()

    def update_transports(self, event=None):
        opts = (["obfs4", "snowflake", "meek"]
                if self.source_var.get() == "Default (Built-in)"
                else ["obfs4", "webtunnel", "vanilla"])
        self.trans_combo['values'] = opts
        if self.trans_var.get() not in opts:
            self.trans_var.set(opts[0])

    def _check_port_free(self, port: int) -> bool:
        try:
            s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            s.bind(("127.0.0.1", port))
            s.close()
            return True
        except OSError:
            return False

    def _get_bridge_lines(self, cat, trans, ip, src="Delta-Kronecker Tor-Bridges-Collector"):
        bridge_lines = []
        limit      = self.cfg.get("bridges_in_torrc", 100)
        do_shuffle = self.cfg.get("shuffle_bridges", True)

        if src == "Default (Built-in)":
            cfg_file = os.path.join(self.extract_dir, "tor",
                                    "pluggable_transports", "pt_config.json")
            if os.path.exists(cfg_file):
                try:
                    with open(cfg_file, encoding="utf-8") as f:
                        entries = json.load(f).get("bridges", {}).get(trans, [])
                    if do_shuffle:
                        entries = list(entries)
                        random.shuffle(entries)
                    for b in entries[:limit]:
                        bridge_lines.append(f"Bridge {b}\n")
                except Exception:
                    pass
        else:
            for c, t, v, _ in BRIDGE_DATA:
                if c == cat and t == trans and (ip == "Both" or ip == v):
                    fn = os.path.join(self.bridges_dir, self.get_safe_filename(c, t, v))
                    if os.path.exists(fn):
                        with open(fn, encoding="utf-8") as f:
                            lines = [l.strip() for l in f if l.strip()]
                        if do_shuffle:
                            random.shuffle(lines)
                        for line in lines[:limit]:
                            bridge_lines.append(f"Bridge {line}\n")
        return bridge_lines

    def generate_torrc(self, cat_ov=None, trans_ov=None, ip_ov=None, src_ov=None):
        base     = os.path.abspath(self.extract_dir)
        data_dir = os.path.join(base, "data")
        tor_dir  = os.path.join(base, "tor")
        pt_dir   = os.path.join(tor_dir, "pluggable_transports")
        lyrebird = os.path.join(pt_dir, "lyrebird.exe")
        conjure  = os.path.join(pt_dir, "conjure-client.exe")
        torrc    = os.path.join(tor_dir, "torrc")
        os.makedirs(data_dir, exist_ok=True)

        src   = src_ov   or self.source_var.get()
        cat   = cat_ov   or self.cat_var.get()
        trans = trans_ov or self.trans_var.get()
        ip    = ip_ov    or self.ip_var.get()

        bridge_lines = self._get_bridge_lines(cat, trans, ip, src)
        use = "1" if bridge_lines else "0"
        cfg = self.cfg

        socks_opts = f"127.0.0.1:{TOR_SOCKS_PORT}"
        isolate_parts = []
        if cfg.get("exp_isolate_dest_addr", False):
            isolate_parts.append("IsolateDestAddr")
        if cfg.get("exp_isolate_dest_port", False):
            isolate_parts.append("IsolateDestPort")
        if isolate_parts:
            socks_opts += " " + " ".join(isolate_parts)

        content  = "Log notice stdout\n"
        content += f"DataDirectory {data_dir}\n"
        content += f"GeoIPFile {os.path.join(data_dir, 'geoip')}\n"
        content += f"GeoIPv6File {os.path.join(data_dir, 'geoip6')}\n"
        content += f"SOCKSPort {socks_opts}\n"
        content += f"ControlPort 127.0.0.1:{TOR_CTRL_PORT}\n"
        content += "CookieAuthentication 1\n"
        content += "DormantClientTimeout 24 hours\n"
        content += "DormantOnFirstStartup 0\n"
        content += "DormantCanceledByStartup 1\n"
        content += f"UseBridges {use}\n"
        content += f"MaxCircuitDirtiness {cfg.get('max_circuit_dirtiness', 1800)}\n"
        content += f"NewCircuitPeriod {cfg.get('new_circuit_period', 10)}\n"
        content += f"NumEntryGuards {cfg.get('num_entry_guards', 15)}\n"
        content += "AllowNonRFC953Hostnames 1\n"
        content += "EnforceDistinctSubnets 0\n"
        content += "MaxClientCircuitsPending 64\n"
        content += "CircuitBuildTimeout 60\n"
        content += "LearnCircuitBuildTimeout 0\n"
        content += "GuardLifetime 90 days\n"
        content += "NumDirectoryGuards 6\n"
        content += "TokenBucketRefillInterval 10 msec\n"

        if cfg.get("dns_over_tor", False):
            content += "DNSPort 127.0.0.1:9053\n"

        if cfg.get("exit_nodes_enabled", False):
            countries = cfg.get("exit_nodes_countries",
                                "{nl},{de},{fr},{ch},{at},{se},{no},{fi},{is}").strip()
            if countries:
                content += f"ExitNodes {countries}\n"
                content += f"StrictNodes {'1' if cfg.get('strict_exit_nodes', False) else '0'}\n"

        if cfg.get("exp_connection_padding", False):
            content += "ConnectionPadding 1\n"
        if cfg.get("exp_reduced_connection_padding", False):
            content += "ReducedConnectionPadding 1\n"

        v_cst = cfg.get("exp_circuit_stream_timeout", 0)
        if v_cst > 0:
            content += f"CircuitStreamTimeout {v_cst}\n"

        v_st = cfg.get("exp_socks_timeout", 0)
        if v_st > 0:
            content += f"SocksTimeout {v_st}\n"

        if cfg.get("exp_safe_logging", False):
            content += "SafeLogging 1\n"
        if cfg.get("exp_avoid_disk_writes", False):
            content += "AvoidDiskWrites 1\n"
        if cfg.get("exp_hardware_accel", False):
            content += "HardwareAccel 1\n"
        if cfg.get("exp_client_dns_reject_internal", False):
            content += "ClientDNSRejectInternalAddresses 1\n"

        if cfg.get("exp_fascist_firewall", False):
            content += "FascistFirewall 1\n"
            fp = cfg.get("exp_firewall_ports", "80,443").strip()
            if fp:
                content += f"FirewallPorts {fp}\n"

        ra = cfg.get("exp_reachable_addresses", "").strip()
        if ra:
            content += f"ReachableAddresses {ra}\n"

        v_nc = cfg.get("exp_num_cpus", 0)
        if v_nc > 0:
            content += f"NumCPUs {v_nc}\n"

        en = cfg.get("exp_exclude_nodes", "").strip()
        if en:
            content += f"ExcludeNodes {en}\n"

        een = cfg.get("exp_exclude_exit_nodes", "").strip()
        if een:
            content += f"ExcludeExitNodes {een}\n"

        nesp = cfg.get("exp_no_exit_stream_ports", "").strip()
        if nesp:
            for port in nesp.split(","):
                port = port.strip()
                if port:
                    content += f"ExitPolicy reject *:{port}\n"

        if cfg.get("exp_use_entry_guards_as_dir_guards", False):
            content += "UseEntryGuardsAsDirGuards 1\n"

        v_pbct = cfg.get("exp_path_bias_circ_threshold", 0)
        if v_pbct > 0:
            content += f"PathBiasCircThreshold {v_pbct}\n"

        content += "\n"
        content += (f"ClientTransportPlugin meek_lite,obfs2,obfs3,obfs4,"
                    f"scramblesuit,webtunnel exec {lyrebird}\n")
        content += f"ClientTransportPlugin snowflake exec {lyrebird}\n"
        content += (f"ClientTransportPlugin conjure exec {conjure}"
                    f" -registerURL \"https://registration.refraction.network/api\"\n\n")

        if use == "1":
            content += "".join(bridge_lines)

        with open(torrc, "w", encoding="utf-8") as f:
            f.write(content)
        return torrc, os.path.join(tor_dir, "tor.exe"), use, bridge_lines

    def _save_last_success(self, cat, trans, ip):
        self.cfg["last_success_cat"]   = cat
        self.cfg["last_success_trans"] = trans
        self.cfg["last_success_ip"]    = ip
        save_config(self.cfg, self.extract_dir)

    def start_auto_connect(self):
        if self.tor_process is not None:
            self.update_status("Already running — stop first.")
            return
        self.auto_btn.configure(text="⏹  Stop Auto", command=self.stop_auto_connect,
                                bg="#4A1E1E", fg="#F38BA8")
        self._auto_connect_active = True
        threading.Thread(target=self._run_auto_connect, daemon=True).start()

    def stop_auto_connect(self):
        self._auto_connect_active = False
        self.stop_tor()
        self.auto_btn.configure(text="🤖  Auto Connect", command=self.start_auto_connect,
                                bg="#1E3A4A", fg="#89DCEB")

    def _run_auto_connect(self):
        last_cat   = self.cfg.get("last_success_cat", "")
        last_trans = self.cfg.get("last_success_trans", "")
        last_ip    = self.cfg.get("last_success_ip", "")
        timeout_s  = self.cfg.get("auto_connect_timeout", 180)

        if last_cat and last_trans and last_ip:
            mem_label = f"[Memory] {last_cat} / {last_trans} / {last_ip}"
            self.root.after(0, self.update_status, f"Auto-connect {mem_label}")
            self.root.after(0, self.append_log,
                            f"\n[Auto] Trying last successful config: {mem_label}\n")
            self.root.after(0, self.source_var.set, "Delta-Kronecker Tor-Bridges-Collector")
            self.root.after(0, self.cat_var.set, last_cat)
            self.root.after(0, self.trans_var.set, last_trans)
            self.root.after(0, self.ip_var.set, last_ip)
            if self._try_bridge_config(last_cat, last_trans, last_ip,
                                       timeout_override=timeout_s):
                self.root.after(0, self.append_log,
                                f"[Auto] ✅ Connected with {mem_label}\n")
                self.root.after(0, self.auto_btn.configure,
                                {"text": "🤖  Auto Connect",
                                 "command": self.start_auto_connect,
                                 "bg": "#1E3A4A", "fg": "#89DCEB"})
                return
            if not self._auto_connect_active:
                self.root.after(0, self.auto_btn.configure,
                                {"text": "🤖  Auto Connect",
                                 "command": self.start_auto_connect,
                                 "bg": "#1E3A4A", "fg": "#89DCEB"})
                return
            self.root.after(0, self.append_log,
                            "[Auto] Memory config timed out — continuing sequence.\n")

        in_sequence = [(cat, trans, ip) for cat, trans, ip in AUTO_SEQUENCE
                       if not (cat == last_cat and trans == last_trans and ip == last_ip)]

        total = len(in_sequence)
        for step, (cat, trans, ip) in enumerate(in_sequence):
            if not self._auto_connect_active:
                break
            label = f"[{step+1}/{total}] {cat} / {trans} / {ip}"
            self.root.after(0, self.update_status, f"Auto-connect {label}")
            self.root.after(0, self.append_log, f"\n[Auto] Trying {label}\n")
            self.root.after(0, self.source_var.set, "Delta-Kronecker Tor-Bridges-Collector")
            self.root.after(0, self.cat_var.set, cat)
            self.root.after(0, self.trans_var.set, trans)
            self.root.after(0, self.ip_var.set, ip)
            if self._try_bridge_config(cat, trans, ip):
                self.root.after(0, self.append_log,
                                f"[Auto] ✅ Connected with {label}\n")
                self.root.after(0, self.auto_btn.configure,
                                {"text": "🤖  Auto Connect",
                                 "command": self.start_auto_connect,
                                 "bg": "#1E3A4A", "fg": "#89DCEB"})
                return

        if self._auto_connect_active:
            self.root.after(0, self.update_status,
                            "Auto-connect failed. Try updating bridges or manual settings.")
            self.root.after(0, self.append_log, "[Auto] ❌ All bridge groups exhausted.\n")
        self._auto_connect_active = False
        self.root.after(0, self.auto_btn.configure,
                        {"text": "🤖  Auto Connect",
                         "command": self.start_auto_connect,
                         "bg": "#1E3A4A", "fg": "#89DCEB"})

    def _try_bridge_config(self, cat, trans, ip, timeout_override=None) -> bool:
        timeout_s = (timeout_override if timeout_override is not None
                     else self.cfg.get("auto_connect_timeout", 180))

        if not self._check_port_free(TOR_SOCKS_PORT):
            self.root.after(0, self.append_log,
                            f"[Auto] Port {TOR_SOCKS_PORT} is already in use — "
                            f"stop other Tor instances.\n", "err")
            return False

        try:
            torrc, tor_exe, _, bridge_lines = self.generate_torrc(
                cat_ov=cat, trans_ov=trans, ip_ov=ip,
                src_ov="Delta-Kronecker Tor-Bridges-Collector")
        except Exception as e:
            self.root.after(0, self.append_log, f"[Auto] torrc error: {e}\n")
            return False

        if not os.path.exists(tor_exe):
            self.root.after(0, self.append_log, "[Auto] tor.exe not found\n")
            return False

        try:
            proc = subprocess.Popen(
                [tor_exe, "-f", torrc],
                stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                text=True, creationflags=subprocess.CREATE_NO_WINDOW)
        except Exception as e:
            self.root.after(0, self.append_log, f"[Auto] Launch error: {e}\n")
            return False

        self.tor_process = proc
        self.root.after(0, lambda: self.stop_btn.config(
            bg=C["RED"], fg=C["BLK"],
            activebackground="#E64553", activeforeground="white"))

        last_pct  = -1
        last_move = time.time()

        for line in iter(proc.stdout.readline, ''):
            if not self._auto_connect_active:
                proc.terminate()
                try: proc.wait(timeout=3)
                except: proc.kill()
                self.tor_process = None
                self.root.after(0, self.update_conn_progress, 0)
                return False

            self.root.after(0, self.append_log, line)

            if "Reading config failed" in line or "Failed to parse/validate config" in line:
                self.root.after(0, self.update_status, "Tor config error — check logs.")
                proc.terminate()
                try: proc.wait(timeout=3)
                except: proc.kill()
                self.tor_process = None
                self.root.after(0, self.update_conn_progress, 0)
                return False

            m = re.search(r'Bootstrapped (\d+)%', line)
            if m:
                pct = int(m.group(1))
                self.root.after(0, self.update_conn_progress, pct)
                if pct != last_pct:
                    last_pct  = pct
                    last_move = time.time()
                if pct == 100:
                    self.tor_connected = True
                    self._save_last_success(cat, trans, ip)
                    self.root.after(0, self.update_status, "Tor is fully connected.")
                    self.root.after(0, self._start_uptime)
                    self.root.after(0, self.stat_tor_var.set, "✅ Connected")
                    self.root.after(0, self._auto_enable_proxy)
                    self.root.after(500, self._schedule_auto_test)
                    self.root.after(0, self._start_watchdog)
                    self.root.after(0, self._start_keepalive)
                    self._notify("Tor Client", "✅ Tor is fully connected!")
                    proc.stdout.close()
                    return True

            if last_pct >= 0 and time.time() - last_move > timeout_s:
                self.root.after(0, self.append_log,
                                f"[Auto] Stuck at {last_pct}% for {timeout_s}s → next\n")
                proc.terminate()
                try: proc.wait(timeout=3)
                except: proc.kill()
                self.tor_process = None
                self.root.after(0, self.update_conn_progress, 0)
                return False

        proc.wait()
        self.tor_process = None
        return False

    def start_tor_thread(self):
        if self.tor_process is not None:
            self.update_status("Already running.")
            return
        self._reset_stats()
        threading.Thread(target=self.run_tor, daemon=True).start()

    def _reset_stats(self):
        self.log_text.configure(state='normal')
        self.log_text.delete(1.0, tk.END)
        self.log_text.configure(state='disabled')
        self.update_conn_progress(0)
        self.stat_ip_var.set("—")
        self.stat_country_var.set("—")
        self.stat_tor_var.set("—")
        self._stop_uptime()
        self._cancel_auto_test()
        self._cancel_watchdog()
        self._cancel_keepalive()
        self.tor_connected = False

    def run_tor(self):
        try:
            if not self._check_port_free(TOR_SOCKS_PORT):
                self.root.after(0, self.update_status,
                                f"Port {TOR_SOCKS_PORT} busy — stop other Tor instances first.")
                self.root.after(0, self.append_log,
                                f"[Error] Port {TOR_SOCKS_PORT} already in use.\n", "err")
                return

            torrc, tor_exe, use_bridges, _ = self.generate_torrc()
            if self.source_var.get() != "Default (Built-in)" and use_bridges == "0":
                self.root.after(0, self.append_log,
                                "Warning: No bridges found. Starting without bridges.\n", "warn")
            self.root.after(0, self.update_status, "Starting Tor…")
            self.tor_process = subprocess.Popen(
                [tor_exe, "-f", torrc],
                stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                text=True, creationflags=subprocess.CREATE_NO_WINDOW)
            self.root.after(0, self.update_status, "Tor is running.")
            self.root.after(0, lambda: self.stop_btn.config(
                bg=C["RED"], fg=C["BLK"],
                activebackground="#E64553", activeforeground="white"))

            for line in iter(self.tor_process.stdout.readline, ''):
                self.root.after(0, self.append_log, line)
                if ("Reading config failed" in line or
                        "Failed to parse/validate config" in line):
                    self.root.after(0, self.update_status, "Tor config error — check logs.")
                    break
                m = re.search(r'Bootstrapped (\d+)%', line)
                if m:
                    pct = int(m.group(1))
                    self.root.after(0, self.update_conn_progress, pct)
                    if pct == 100 and not self.tor_connected:
                        self.tor_connected = True
                        self.root.after(0, self.update_status, "Tor is fully connected.")
                        self.root.after(0, self._start_uptime)
                        self.root.after(0, self.stat_tor_var.set, "✅ Connected")
                        self.root.after(0, self._auto_enable_proxy)
                        self.root.after(500, self._schedule_auto_test)
                        self.root.after(0, self._start_watchdog)
                        self.root.after(0, self._start_keepalive)
                        self._notify("Tor Client", "✅ Tor is fully connected!")

            self.tor_process.stdout.close()
            self.tor_process.wait()

        except Exception as e:
            self.root.after(0, self.update_status, "Failed to start Tor.")
            self.root.after(0, self.append_log, f"Error: {e}\n", "err")
        finally:
            self._on_tor_stopped()

    def _on_tor_stopped(self):
        self.tor_process   = None
        self.tor_connected = False
        self._cancel_auto_test()
        self._cancel_watchdog()
        self._cancel_keepalive()
        self._stop_http_proxy()
        self.set_system_proxy(False)
        self.proxy_var.set(False)
        self.root.after(0, self._refresh_proxy_btn)
        self.root.after(0, lambda: self.stop_btn.config(
            bg=C["BTN"], fg=C["FG"],
            activebackground=C["BTN2"], activeforeground=C["FG"]))
        self.root.after(0, self.update_status,    "Tor stopped.")
        self.root.after(0, self.update_conn_progress, 0)
        self.root.after(0, self._stop_uptime)
        self.root.after(0, self.stat_tor_var.set,     "—")
        self.root.after(0, self.stat_ip_var.set,      "—")
        self.root.after(0, self.stat_country_var.set, "—")
        self._notify("Tor Client", "Tor has stopped.")

    def stop_tor(self):
        self._auto_connect_active = False
        if self.tor_process:
            self.tor_process.terminate()
            try: self.tor_process.wait(timeout=4)
            except: self.tor_process.kill()
            self.tor_process = None
        self._on_tor_stopped()

    def _dl_with_progress(self, url, dest, retries=3, timeout=90):
        for attempt in range(retries):
            try:
                req = urllib.request.Request(url, headers={'User-Agent': 'Mozilla/5.0'})
                with urllib.request.urlopen(req, timeout=timeout) as resp:
                    total = resp.getheader('Content-Length')
                    total = int(total) if total else None
                    done  = 0
                    self.root.after(0, self._set_dl, 0)
                    with open(dest, 'wb') as f:
                        while True:
                            chunk = resp.read(65536)
                            if not chunk:
                                break
                            f.write(chunk)
                            done += len(chunk)
                            if total:
                                self.root.after(0, self._set_dl, int(done * 100 / total))
                self.root.after(0, self._set_dl, 100)
                return True
            except Exception:
                if attempt == retries - 1:
                    raise
                time.sleep(2)

    def _dl_simple(self, url, dest, retries=2, timeout=20):
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
        is_first_launch = (
            not os.path.exists(self.bridges_dir) or
            not os.listdir(self.bridges_dir)
        )
        if is_first_launch:
            self.root.after(0, self.append_log,
                            "[Init] First launch — downloading all bridge files…\n", "info")
            self._download_all_bridges_parallel()
        else:
            self._update_fresh_bridges_parallel()
            self.root.after(0, self.update_status, "Ready.")
        self.root.after(0, self._refresh_bridge_info)

    def setup_tor(self):
        tor_exe = os.path.join(self.extract_dir, "tor", "tor.exe")
        if os.path.exists(tor_exe):
            return
        os.makedirs(os.path.dirname(self.archive_name), exist_ok=True)
        self.root.after(0, self.update_status, "Downloading Tor Expert Bundle from GitHub…")
        self.root.after(0, self._show_dl, "Downloading Tor Bundle…")
        try:
            self._dl_with_progress(self.TOR_URL, self.archive_name, retries=3, timeout=120)
        except Exception:
            self.root.after(0, self.update_status, "GitHub failed. Trying torproject.org…")
            self.root.after(0, self._set_dl, 0, "Retrying (torproject.org)…")
            try:
                self._dl_with_progress(self.TOR_FALLBACK, self.archive_name,
                                       retries=3, timeout=120)
            except Exception as e:
                self.root.after(0, self.update_status, f"Download failed: {e}")
                self.root.after(0, self._hide_dl)
                return
        self.root.after(0, self.update_status, "Extracting Tor…")
        os.makedirs(self.extract_dir, exist_ok=True)
        with tarfile.open(self.archive_name, "r:gz") as tar:
            tar.extractall(path=self.extract_dir)
        try:
            os.remove(self.archive_name)
        except Exception:
            pass
        self.root.after(0, self._hide_dl)
        os.makedirs(self.bridges_dir, exist_ok=True)

    def get_safe_filename(self, cat, trans, ip):
        safe = cat.replace(" ", "_").replace("&", "and").replace("(", "").replace(")", "")
        return f"{safe}_{trans}_{ip}.txt"

    def _update_fresh_bridges_parallel(self):
        os.makedirs(self.bridges_dir, exist_ok=True)
        self.root.after(0, self._show_dl, "Auto-updating Fresh (72h) bridges…")
        self.root.after(0, self.update_status, "Updating Fresh (72h) bridges…")
        total      = len(FRESH_DATA)
        done_lock  = threading.Lock()
        done_count = [0]

        def _fetch(entry):
            cat, trans, ip, url = entry
            fpath = os.path.join(self.bridges_dir, self.get_safe_filename(cat, trans, ip))
            try:
                self._dl_simple(url, fpath)
            except Exception:
                pass
            with done_lock:
                done_count[0] += 1
                pct = int(done_count[0] * 100 / total)
                self.root.after(0, self._set_dl, pct,
                                f"Updating Fresh bridges… ({done_count[0]}/{total})")

        with ThreadPoolExecutor(max_workers=4) as ex:
            ex.map(_fetch, FRESH_DATA)
        self.root.after(0, self._hide_dl)

    def _download_all_bridges_parallel(self):
        os.makedirs(self.bridges_dir, exist_ok=True)
        self.root.after(0, self._show_dl, "Downloading all bridges…")
        self.root.after(0, self.update_status, "Downloading all bridge files…")
        total      = len(BRIDGE_DATA)
        done_lock  = threading.Lock()
        done_count = [0]

        def _fetch(entry):
            cat, trans, ip, url = entry
            fpath = os.path.join(self.bridges_dir, self.get_safe_filename(cat, trans, ip))
            try:
                self._dl_simple(url, fpath)
            except Exception:
                pass
            with done_lock:
                done_count[0] += 1
                pct = int(done_count[0] * 100 / total)
                self.root.after(0, self._set_dl, pct,
                                f"Downloading bridges… ({done_count[0]}/{total})")

        with ThreadPoolExecutor(max_workers=4) as ex:
            ex.map(_fetch, BRIDGE_DATA)
        self.root.after(0, self._hide_dl)
        self.root.after(0, self.update_status, "Ready. All bridges downloaded.")
        self.root.after(0, self._refresh_bridge_info)

    def start_download_bridges(self):
        threading.Thread(target=self._download_all_bridges_parallel, daemon=True).start()

    def show_help_window(self):
        w = tk.Toplevel(self.root)
        w.title("How to Use — Tor Client")
        w.geometry("700x640")
        w.configure(bg=C["BG"])
        w.resizable(False, False)
        w.update()
        apply_dark_titlebar(w)
        self._apply_icon_to(w)

        tk.Label(w, text="📖  How to Use — Tor Client",
                 font=('Segoe UI', 14, 'bold'), bg=C["BG"], fg=C["ACC"]).pack(pady=(15, 5))

        tf = tk.Frame(w, bg=C["BLK"])
        tf.pack(fill='both', expand=True, padx=20, pady=8)
        txt = tk.Text(tf, font=('Segoe UI', 10), wrap='word', bg=C["BLK"],
                      fg=C["FG"], bd=0, padx=15, pady=12, spacing2=4)
        sb  = ttk.Scrollbar(tf, command=txt.yview)
        txt.configure(yscrollcommand=sb.set)
        sb.pack(side='right', fill='y')
        txt.pack(fill='both', expand=True)

        txt.insert('1.0', f"""\
🔰  QUICK START
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  Bridge Source  →  Delta-Kronecker Tor-Bridges-Collector
  Category       →  Tested & Active
  Transport      →  obfs4
  IP Version     →  IPv4
  Then click 🤖 Auto Connect.

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
📋  STEP-BY-STEP
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

  Step 1  First launch: choose install folder (AppData\\Local recommended).
          All 15 bridge files download automatically (~1-2 min).

  Step 2  Tor downloads automatically. Wait for progress bar.

  Step 3  On every subsequent launch, only Fresh (72h) bridges
          update automatically. Press "Update All Bridges" to
          manually refresh everything.

  Step 4  Configure bridges as shown in Quick Start above.

  Step 5  Click 🤖 Auto Connect:
          • First tries last successful config ({self.cfg.get("auto_connect_timeout", 180)}s timeout)
          • Tries 9 configs automatically if needed
          System proxy is enabled automatically on success.

  Step 6  Wait for 100%. Status: "Tor is fully connected."
          A Windows notification confirms the connection.

  Step 7  Click 🔄 New Circuit anytime to get a new exit IP
          without restarting Tor.

  Step 8  Closing the window minimizes to the system tray.
          Right-click tray icon → Show Window or Quit.

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
🌐  SYSTEM PROXY
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  HTTP proxy: 127.0.0.1:{HTTP_PROXY_PORT}
  SOCKS5:     127.0.0.1:{TOR_SOCKS_PORT}
  DNS is resolved by Tor remotely — no DNS leaks.
  Instagram, YouTube work correctly through this proxy.

  ✅ Chrome, Edge, Telegram, most Windows apps — automatic.
  ❌ Firefox: Settings → Network → SOCKS5: 127.0.0.1:{TOR_SOCKS_PORT}
              Enable "Proxy DNS over SOCKS5".

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
🌍  EXIT NODES (YouTube / Instagram)
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  In Settings → Exit Nodes → Enable filter.
  Choose countries less blocked by Google/Meta:
  {{nl}} {{de}} {{fr}} {{ch}} {{at}} {{se}} {{no}} {{fi}} {{is}}
  Or click 🔄 New Circuit to try a different exit IP.

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
🔎  BRIDGE CATEGORIES
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  Tested & Active   Verified working bridges  ⭐ Best
  Fresh (72h)       Collected in last 72h
  Full Archive      Complete historical list
  Default           Bundled inside Tor itself

  Transports:
  obfs4      → Best for Iran/China/Russia — looks like random data
  webtunnel  → Disguised as HTTPS website traffic
  vanilla    → Plain Tor — only if Tor is not blocked

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
🧪  EXPERIMENTAL SETTINGS
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  All experimental options are OFF by default.
  They map directly to torrc directives.
  Wrong values can break connectivity.
  Restart Tor after changing any experimental setting.

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
⚠️  TROUBLESHOOTING
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  Stuck below 100%? → Update All Bridges, try Auto Connect.
  YouTube/Instagram? → Enable Exit Nodes in Settings.
  Port {TOR_SOCKS_PORT} busy?  → Another Tor instance is running.
  Firefox?          → Configure SOCKS5 manually in Firefox.
  No bridges found? → Update All Bridges and wait.

  Log colors: Green=notices  Yellow=warnings
              Red=errors     Blue=auto-connect  Orange=tests
""")

        txt.configure(state='disabled')
        tk.Button(w, text="Close", command=w.destroy,
                  bg=C["ACC"], fg=C["BLK"], font=('Segoe UI', 10, 'bold'),
                  relief="flat", cursor="hand2",
                  activebackground="#B4BEFE"
                  ).pack(pady=(0, 12), padx=120, fill='x')


if __name__ == "__main__":
    root = tk.Tk()
    root.withdraw()
    app = TorClientGUI(root)
    root.deiconify()
    root.mainloop()
