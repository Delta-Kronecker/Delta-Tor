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
import webbrowser
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
    ("Tested & Active", "obfs4",     "IPv4"),
    ("Tested & Active", "vanilla",   "IPv4"),
    ("Tested & Active", "webtunnel", "IPv4"),
    ("Fresh (72h)",     "obfs4",     "IPv4"),
    ("Fresh (72h)",     "vanilla",   "IPv4"),
    ("Fresh (72h)",     "webtunnel", "IPv4"),
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
    "auto_proxy_on_connect": False,
    "sni_enabled":           False,
    "sni_host":              "www.google.com",
    "custom_bridges":        "",
    "use_custom_bridges":    False,
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
    "BG":    "#13171F",
    "PANEL": "#1A1F2B",
    "CARD":  "#1F2535",
    "BORDER":"#2C3347",
    "FG":    "#C8D0DC",
    "FG2":   "#6B7A94",
    "ACC":   "#3A72B0",
    "ACC2":  "#4D88C8",
    "GRN":   "#2EB87A",
    "RED":   "#D95555",
    "YLW":   "#C9A020",
    "ORG":   "#C06830",
    "CYAN":  "#3AA8C0",
    "BTN":   "#1E2535",
    "BTN2":  "#273048",
    "SEL":   "#1A2E50",
    "BLK":   "#13171F",
    "PRP":   "#7080BB",
}

if getattr(sys, 'frozen', False):
    _EXE_DIR = os.path.dirname(os.path.abspath(sys.executable))
else:
    _EXE_DIR = os.path.dirname(os.path.abspath(__file__))

def verify_file_sha256(filepath, expected_hash=None):
    import hashlib
    sha256_hash = hashlib.sha256()
    with open(filepath, "rb") as f:
        for byte_block in iter(lambda: f.read(4096), b""):
            sha256_hash.update(byte_block)
    computed_hash = sha256_hash.hexdigest()
    if expected_hash:
        return computed_hash.lower() == expected_hash.lower()
    return computed_hash

def set_window_icon(window):
    try:
        ico = resource_path("icon.ico")
        if os.path.exists(ico):
            window.iconbitmap(ico)
    except Exception:
        try:
            img = tk.PhotoImage(file=resource_path("icon.ico"))
            window.iconphoto(True, img)
        except Exception:
            pass

def _bootstrap_config_path():
    return os.path.join(BASE_DIR, "tor_client_config.json")

def load_config() -> dict:
    bootstrap = _bootstrap_config_path()
    data = {}
    if os.path.exists(bootstrap):
        try:
            with open(bootstrap, encoding="utf-8") as f:
                data = json.load(f)
        except Exception:
            data = {}
    for k, v in DEFAULT_CFG.items():
        data.setdefault(k, v)
    data["extract_dir"] = BASE_DIR
    return data

def save_config(cfg: dict, extract_dir: str = ""):
    try:
        with open(_bootstrap_config_path(), "w", encoding="utf-8") as f:
            json.dump(cfg, f, indent=2)
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
        cap = ctypes.c_int(0x1F1713)
        dwm.DwmSetWindowAttribute(hwnd, 35, ctypes.byref(cap), ctypes.sizeof(cap))
        txt = ctypes.c_int(0xDCD0C8)
        dwm.DwmSetWindowAttribute(hwnd, 36, ctypes.byref(txt), ctypes.sizeof(txt))
        brd = ctypes.c_int(0x47332C)
        dwm.DwmSetWindowAttribute(hwnd, 34, ctypes.byref(brd), ctypes.sizeof(brd))
    except Exception:
        pass

_APPDATA_DELTATOR = os.path.join(
    os.environ.get("LOCALAPPDATA", os.path.expanduser("~")),
    "DeltaTor"
)
os.makedirs(_APPDATA_DELTATOR, exist_ok=True)

_PTR_FILE = os.path.join(_APPDATA_DELTATOR, "datadir.txt")

_DEFAULT_DATA_DIR = _APPDATA_DELTATOR

def _read_data_dir():
    if os.path.exists(_PTR_FILE):
        try:
            path = open(_PTR_FILE, encoding="utf-8").read().strip()
            if path:
                return path
        except Exception:
            pass
    return None

def _save_data_dir(path):
    try:
        os.makedirs(_APPDATA_DELTATOR, exist_ok=True)
        with open(_PTR_FILE, "w", encoding="utf-8") as f:
            f.write(path)
    except Exception:
        pass

def _ask_data_dir():
    import tkinter as tk
    from tkinter import filedialog

    dlg_root = tk.Tk()
    dlg_root.withdraw()
    dlg_root.configure(bg=C["BG"])

    dlg = tk.Toplevel(dlg_root)
    dlg.title("Choose Data Directory")
    dlg.geometry("520x280")
    dlg.configure(bg=C["BG"])
    dlg.resizable(False, False)
    dlg.update()
    apply_dark_titlebar(dlg)
    dlg.protocol("WM_DELETE_WINDOW", lambda: None)

    chosen = tk.StringVar(value=_DEFAULT_DATA_DIR)
    confirmed = [False]

    tk.Frame(dlg, bg=C["ACC"], height=4).pack(fill='x')

    tk.Label(dlg,
             text="📁  Choose Data Directory",
             font=('Segoe UI', 13, 'bold'),
             bg=C["BG"], fg=C["ACC"]).pack(pady=(18, 4))

    tk.Label(dlg,
             text="All bridges, logs, Tor binaries and config files\nwill be stored in this folder.",
             font=('Segoe UI', 10), bg=C["BG"], fg=C["FG2"],
             justify='center').pack(pady=(0, 14))

    path_row = tk.Frame(dlg, bg=C["BG"])
    path_row.pack(fill='x', padx=24)

    path_entry = tk.Entry(path_row, textvariable=chosen,
                          bg=C["BTN"], fg=C["FG"],
                          insertbackground=C["FG"],
                          relief="flat", bd=6,
                          font=('Segoe UI', 10))
    path_entry.pack(side='left', fill='x', expand=True, ipady=5)

    def _browse():
        d = filedialog.askdirectory(
            title="Select Data Directory",
            initialdir=chosen.get() if os.path.exists(chosen.get()) else os.path.expanduser("~"))
        if d:
            chosen.set(os.path.normpath(d))

    tk.Button(path_row, text="Browse…",
              command=_browse,
              bg=C["BTN2"], fg=C["FG"],
              font=('Segoe UI', 9), relief="flat", cursor="hand2",
              activebackground=C["ACC"]).pack(side='left', padx=(6, 0), ipady=5, ipadx=6)

    tk.Label(dlg,
             text=f"Default:  {_DEFAULT_DATA_DIR}",
             font=('Segoe UI', 8), bg=C["BG"], fg=C["FG2"]).pack(pady=(6, 0))

    def _confirm():
        p = chosen.get().strip()
        if not p:
            p = _DEFAULT_DATA_DIR
        chosen.set(p)
        confirmed[0] = True
        dlg.destroy()

    def _use_default():
        chosen.set(_DEFAULT_DATA_DIR)
        confirmed[0] = True
        dlg.destroy()

    btn_row = tk.Frame(dlg, bg=C["BG"])
    btn_row.pack(fill='x', padx=24, pady=(16, 0))

    tk.Button(btn_row, text="✔  Use This Folder",
              command=_confirm,
              bg=C["ACC"], fg=C["FG"],
              font=('Segoe UI', 10, 'bold'), relief="flat", cursor="hand2",
              activebackground=C["ACC2"]).pack(side='left', fill='x', expand=True,
                                               ipady=6, padx=(0, 6))

    tk.Button(btn_row, text="Use Default",
              command=_use_default,
              bg=C["BTN"], fg=C["FG2"],
              font=('Segoe UI', 10), relief="flat", cursor="hand2",
              activebackground=C["BTN2"]).pack(side='left', ipady=6, ipadx=10)

    dlg.update_idletasks()
    sw = dlg.winfo_screenwidth()
    sh = dlg.winfo_screenheight()
    x  = (sw - 520) // 2
    y  = (sh - 280) // 2
    dlg.geometry(f"520x280+{x}+{y}")
    dlg.lift()
    dlg.focus_force()
    dlg_root.wait_window(dlg)
    dlg_root.destroy()

    result = chosen.get().strip() or _DEFAULT_DATA_DIR
    return os.path.normpath(result)

_saved = _read_data_dir()
if _saved:
    BASE_DIR = _saved
else:
    BASE_DIR = _ask_data_dir()
    _save_data_dir(BASE_DIR)

def _migrate_existing_files(src_dir, dst_dir):
    if os.path.normpath(src_dir) == os.path.normpath(dst_dir):
        return
    items_to_move = [
        "bridges", "logs", "tor", "data",
        "tor-expert-bundle.tar.gz",
        "config.json", "tor_client_config.json",
        "geoip", "geoip6",
    ]
    os.makedirs(dst_dir, exist_ok=True)
    for item in items_to_move:
        src = os.path.join(src_dir, item)
        dst = os.path.join(dst_dir, item)
        if os.path.exists(src) and not os.path.exists(dst):
            try:
                shutil.move(src, dst)
            except Exception:
                pass
    old_ptr = os.path.join(src_dir, "datadir.txt")
    if os.path.exists(old_ptr):
        try:
            os.remove(old_ptr)
        except Exception:
            pass

if os.path.normpath(BASE_DIR) != os.path.normpath(_EXE_DIR):
    _migrate_existing_files(_EXE_DIR, BASE_DIR)

def resource_path(filename):
    if hasattr(sys, '_MEIPASS'):
        return os.path.join(sys._MEIPASS, filename)
    return os.path.join(BASE_DIR, filename)

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

def recv_exact(sock, n):
    data = b""
    while len(data) < n:
        chunk = sock.recv(n - len(data))
        if not chunk:
            raise ConnectionError("Socket closed unexpectedly")
        data += chunk
    return data

def socks5_request(host, port, path,
                   proxy_host="127.0.0.1", proxy_port=TOR_SOCKS_PORT,
                   use_ssl=True, timeout=20):
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    s.settimeout(timeout)
    s.connect((proxy_host, proxy_port))
    s.sendall(b'\x05\x01\x00')
    if recv_exact(s, 2)[1] != 0x00:
        raise ConnectionError("SOCKS5 handshake failed")
    hb = host.encode()
    s.sendall(b'\x05\x01\x00\x03' + bytes([len(hb)]) + hb + port.to_bytes(2, 'big'))
    r = recv_exact(s, 10)
    if r[1] != 0x00:
        raise ConnectionError(f"SOCKS5 connect error {r[1]}")
    if use_ssl:
        ctx = ssl.create_default_context()
        s = ctx.wrap_socket(s, server_hostname=host)
    s.sendall((f"GET {path} HTTP/1.1\r\nHost: {host}\r\n"
               f"Connection: close\r\nUser-Agent: Mozilla/5.0\r\n\r\n").encode())
    import io
    buf = io.BytesIO()
    while True:
        chunk = s.recv(4096)
        if not chunk:
            break
        buf.write(chunk)
    s.close()
    data = buf.getvalue()
    sep = data.find(b"\r\n\r\n")
    return (data[sep + 4:] if sep != -1 else data).decode(errors="replace")

def _http_proxy_relay(client_sock, socks_host, socks_port, host, port, initial_data=b""):
    try:
        tor = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        tor.settimeout(30)
        tor.connect((socks_host, socks_port))
        tor.sendall(b'\x05\x01\x00')
        if recv_exact(tor, 2)[1] != 0x00:
            client_sock.close(); tor.close(); return
        host_b = host.encode()
        tor.sendall(b'\x05\x01\x00\x03' + bytes([len(host_b)]) + host_b + port.to_bytes(2, 'big'))
        resp = recv_exact(tor, 10)
        if resp[1] != 0x00:
            client_sock.close(); tor.close(); return
        if initial_data:
            tor.sendall(initial_data)
        tor.settimeout(None)
        client_sock.settimeout(None)
        while True:
            try:
                r, _, _ = select.select([client_sock, tor], [], [], 30)
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
        MAX_HEADER_SIZE = 65536
        while b"\r\n\r\n" not in buf:
            chunk = client_sock.recv(4096)
            if not chunk:
                client_sock.close(); return
            buf += chunk
            if len(buf) > MAX_HEADER_SIZE:
                client_sock.close(); return
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
        nid.szTip       = "Delta Tor"
        nid.szInfo      = msg[:255]
        nid.szInfoTitle = title[:63]
        nid.dwInfoFlags = NIIF_INFO
        shell32.Shell_NotifyIconW(NIM_ADD, ctypes.byref(nid))
        time.sleep(4)
        shell32.Shell_NotifyIconW(NIM_DELETE, ctypes.byref(nid))
    except Exception:
        pass

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

class CustomBridgeWindow:
    def __init__(self, parent, cfg: dict, on_save):
        self.on_save = on_save
        self.cfg = cfg
        w = tk.Toplevel(parent)
        w.title("Custom Bridges")
        w.geometry("700x650")
        w.configure(bg=C["BG"])
        w.resizable(True, True)
        w.grab_set()
        w.update()
        apply_dark_titlebar(w)
        set_window_icon(w)

        tk.Frame(w, bg=C["ACC"], height=3).pack(fill='x')
        tk.Label(w, text="⬡  Custom Bridge Lines",
                 font=('Segoe UI', 13, 'bold'), bg=C["BG"], fg=C["ACC"]).pack(pady=(12, 2))
        tk.Label(w,
                 text="Enter one bridge per line.  Format: obfs4 1.2.3.4:1234 FINGERPRINT cert=… iat-mode=0",
                 font=('Segoe UI', 8), bg=C["BG"], fg=C["FG2"]).pack()

        bf = tk.Frame(w, bg=C["BG"])
        bf.pack(fill='x', padx=20, pady=(10, 6))
        tk.Button(bf, text="🔍  Ping All Bridges", command=self._ping_all,
                  bg=C["BTN2"], fg=C["CYAN"], font=('Segoe UI', 9, 'bold'),
                  relief="flat", cursor="hand2",
                  activebackground=C["CARD"]).pack(side='left', ipady=4, padx=(0, 6))
        tk.Button(bf, text="✔  Save", command=self._save,
                  bg=C["ACC"], fg="white", font=('Segoe UI', 10, 'bold'),
                  relief="flat", cursor="hand2",
                  activebackground=C["ACC2"]).pack(side='left', ipady=4, padx=(0, 6))
        tk.Button(bf, text="Cancel", command=w.destroy,
                  bg=C["BTN"], fg=C["FG2"], font=('Segoe UI', 10),
                  relief="flat", cursor="hand2").pack(side='left', ipady=4)

        top_row = tk.Frame(w, bg=C["BG"])
        top_row.pack(fill='x', padx=20, pady=(4, 0))
        self._use_var = tk.BooleanVar(value=cfg.get("use_custom_bridges", False))
        ttk.Checkbutton(top_row, text="Use custom bridges (overrides category selection)",
                        variable=self._use_var).pack(side='left')

        txt_frame = tk.Frame(w, bg=C["BLK"], bd=0)
        txt_frame.pack(fill='both', expand=True, padx=20, pady=8)
        self._txt = tk.Text(txt_frame, font=('Consolas', 9),
                            bg=C["BLK"], fg=C["GRN"], insertbackground=C["ACC"],
                            wrap='none', relief="flat", padx=10, pady=8)
        sb = ttk.Scrollbar(txt_frame, command=self._txt.yview)
        self._txt.configure(yscrollcommand=sb.set)
        sb.pack(side='right', fill='y')
        self._txt.pack(fill='both', expand=True)
        self._txt.insert('1.0', cfg.get("custom_bridges", ""))

        res_lbl = tk.Label(w, text="Ping Results:", font=('Segoe UI', 9, 'bold'),
                           bg=C["BG"], fg=C["FG2"])
        res_lbl.pack(anchor='w', padx=20)
        res_frame = tk.Frame(w, bg=C["CARD"], height=100)
        res_frame.pack(fill='x', padx=20, pady=(0, 4))
        res_frame.pack_propagate(False)
        self._res_text = tk.Text(res_frame, font=('Consolas', 8),
                                 bg=C["CARD"], fg=C["FG2"],
                                 wrap='word', relief="flat", padx=8, pady=6,
                                 state='disabled')
        self._res_text.tag_configure("ok",  foreground=C["GRN"])
        self._res_text.tag_configure("bad", foreground=C["RED"])
        self._res_text.tag_configure("inf", foreground=C["CYAN"])
        sb2 = ttk.Scrollbar(res_frame, command=self._res_text.yview)
        self._res_text.configure(yscrollcommand=sb2.set)
        sb2.pack(side='right', fill='y')
        self._res_text.pack(fill='both', expand=True)

        self._win = w

    def _log_res(self, msg, tag="inf"):
        self._res_text.configure(state='normal')
        self._res_text.insert(tk.END, msg, tag)
        self._res_text.see(tk.END)
        self._res_text.configure(state='disabled')

    def _ping_bridge(self, line):
        line = line.strip()
        if not line or line.startswith('#'):
            return
        m = re.search(r'(\d{1,3}(?:\.\d{1,3}){3}):(\d+)', line)
        if not m:
            m = re.search(r'\[([0-9a-fA-F:]+)\]:(\d+)', line)
            if not m:
                self._win.after(0, self._log_res, f"  ⚠ Could not parse: {line[:60]}\n", "bad")
                return
        host = m.group(1)
        port = int(m.group(2))
        t0 = time.time()
        try:
            s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            s.settimeout(5)
            s.connect((host, port))
            s.close()
            ms = int((time.time() - t0) * 1000)
            tag = "ok" if ms < 500 else "inf"
            self._win.after(0, self._log_res,
                            f"  ✔ {host}:{port}  {ms} ms\n", tag)
        except Exception as e:
            self._win.after(0, self._log_res,
                            f"  ✘ {host}:{port}  {e}\n", "bad")

    def _ping_all(self):
        lines = self._txt.get("1.0", tk.END).strip().splitlines()
        valid = [l for l in lines if l.strip() and not l.strip().startswith('#')]
        if not valid:
            messagebox.showinfo("No Bridges", "Enter at least one bridge line first.",
                                parent=self._win)
            return
        self._res_text.configure(state='normal')
        self._res_text.delete('1.0', tk.END)
        self._res_text.configure(state='disabled')
        self._log_res(f"Pinging {len(valid)} bridges…\n", "inf")

        def _run():
            with ThreadPoolExecutor(max_workers=8) as ex:
                ex.map(self._ping_bridge, valid)
            self._win.after(0, self._log_res, "Done.\n", "inf")
        threading.Thread(target=_run, daemon=True).start()

    def _save(self):
        text = self._txt.get("1.0", tk.END).strip()
        self.cfg["custom_bridges"]   = text
        self.cfg["use_custom_bridges"] = self._use_var.get()
        self.on_save(self.cfg)
        self._win.destroy()

class BridgeScannerWindow:
    def __init__(self, parent, bridges_dir, get_safe_filename):
        self.bridges_dir = bridges_dir
        self.get_safe_filename = get_safe_filename
        self._stop = False

        w = tk.Toplevel(parent)
        w.title("Bridge Scanner")
        w.geometry("780x600")
        w.configure(bg=C["BG"])
        w.resizable(True, True)
        w.update()
        apply_dark_titlebar(w)
        set_window_icon(w)
        self._win = w

        tk.Frame(w, bg=C["ACC"], height=3).pack(fill='x')
        tk.Label(w, text="⬡  Bridge Scanner",
                 font=('Segoe UI', 13, 'bold'), bg=C["BG"], fg=C["ACC"]).pack(pady=(10, 2))
        tk.Label(w,
                 text="TCP-ping each bridge in the selected file. Green = reachable, Red = unreachable.",
                 font=('Segoe UI', 8), bg=C["BG"], fg=C["FG2"]).pack()

        ctrl = tk.Frame(w, bg=C["PANEL"])
        ctrl.pack(fill='x', padx=16, pady=8)
        tk.Label(ctrl, text="Category:", bg=C["PANEL"], fg=C["FG2"],
                 font=('Segoe UI', 9)).grid(row=0, column=0, padx=(12,4), pady=8)
        self._cat_v = tk.StringVar(value="Tested & Active")
        ttk.Combobox(ctrl, textvariable=self._cat_v,
                     values=["Tested & Active", "Fresh (72h)", "Full Archive"],
                     state="readonly", width=18).grid(row=0, column=1, padx=4)

        tk.Label(ctrl, text="Transport:", bg=C["PANEL"], fg=C["FG2"],
                 font=('Segoe UI', 9)).grid(row=0, column=2, padx=(12,4))
        self._trans_v = tk.StringVar(value="obfs4")
        ttk.Combobox(ctrl, textvariable=self._trans_v,
                     values=["obfs4", "webtunnel", "vanilla"],
                     state="readonly", width=12).grid(row=0, column=3, padx=4)

        tk.Label(ctrl, text="IP:", bg=C["PANEL"], fg=C["FG2"],
                 font=('Segoe UI', 9)).grid(row=0, column=4, padx=(12,4))
        self._ip_v = tk.StringVar(value="IPv4")
        ttk.Combobox(ctrl, textvariable=self._ip_v,
                     values=["IPv4", "IPv6"],
                     state="readonly", width=7).grid(row=0, column=5, padx=4)

        tk.Label(ctrl, text="Workers:", bg=C["PANEL"], fg=C["FG2"],
                 font=('Segoe UI', 9)).grid(row=0, column=6, padx=(12,4))
        self._workers_v = tk.IntVar(value=20)
        tk.Spinbox(ctrl, textvariable=self._workers_v, from_=1, to=50, width=5,
                   bg=C["BTN"], fg=C["FG"], buttonbackground=C["BTN2"],
                   relief="flat").grid(row=0, column=7, padx=4)

        tk.Label(ctrl, text="Timeout(s):", bg=C["PANEL"], fg=C["FG2"],
                 font=('Segoe UI', 9)).grid(row=0, column=8, padx=(12,4))
        self._timeout_v = tk.IntVar(value=5)
        tk.Spinbox(ctrl, textvariable=self._timeout_v, from_=1, to=30, width=5,
                   bg=C["BTN"], fg=C["FG"], buttonbackground=C["BTN2"],
                   relief="flat").grid(row=0, column=9, padx=4)

        prog_f = tk.Frame(w, bg=C["BG"])
        prog_f.pack(fill='x', padx=16)
        self._prog_var = tk.IntVar(value=0)
        self._prog_lbl = tk.StringVar(value="Ready.")
        tk.Label(prog_f, textvariable=self._prog_lbl, bg=C["BG"], fg=C["FG2"],
                 font=('Segoe UI', 8)).pack(anchor='w')
        ttk.Progressbar(prog_f, variable=self._prog_var,
                        maximum=100, mode='determinate').pack(fill='x', pady=(2,4))

        cols = ("bridge", "host", "port", "ping", "status")
        tree_f = tk.Frame(w, bg=C["BLK"])
        tree_f.pack(fill='both', expand=True, padx=16, pady=(0,4))
        self._tree = ttk.Treeview(tree_f, columns=cols, show='headings',
                                  selectmode='browse')
        self._tree.heading("bridge", text="Bridge Type")
        self._tree.heading("host",   text="Host")
        self._tree.heading("port",   text="Port")
        self._tree.heading("ping",   text="Ping (ms)")
        self._tree.heading("status", text="Status")
        self._tree.column("bridge", width=110, minwidth=80)
        self._tree.column("host",   width=200, minwidth=120)
        self._tree.column("port",   width=60,  minwidth=50)
        self._tree.column("ping",   width=80,  minwidth=60)
        self._tree.column("status", width=120, minwidth=80)
        self._tree.tag_configure("ok",  foreground=C["GRN"])
        self._tree.tag_configure("bad", foreground=C["RED"])
        self._tree.tag_configure("slow",foreground=C["YLW"])
        vsb = ttk.Scrollbar(tree_f, orient='vertical', command=self._tree.yview)
        self._tree.configure(yscrollcommand=vsb.set)
        vsb.pack(side='right', fill='y')
        self._tree.pack(fill='both', expand=True)

        self._summary_var = tk.StringVar(value="")
        tk.Label(w, textvariable=self._summary_var, bg=C["BG"], fg=C["GRN"],
                 font=('Segoe UI', 9, 'bold')).pack(pady=(0,4))

        bf = tk.Frame(w, bg=C["BG"])
        bf.pack(fill='x', padx=16, pady=(0,12))
        self._scan_btn = tk.Button(bf, text="▶  Start Scan", command=self._start_scan,
                  bg=C["ACC"], fg="white", font=('Segoe UI', 10, 'bold'),
                  relief="flat", cursor="hand2",
                  activebackground=C["ACC2"])
        self._scan_btn.pack(side='left', ipady=5, padx=(0,6))
        tk.Button(bf, text="⏹  Stop", command=self._stop_scan,
                  bg=C["BTN2"], fg=C["RED"], font=('Segoe UI', 10, 'bold'),
                  relief="flat", cursor="hand2").pack(side='left', ipady=5, padx=(0,6))
        tk.Button(bf, text="💾  Export Working", command=self._export,
                  bg=C["BTN"], fg=C["CYAN"], font=('Segoe UI', 9, 'bold'),
                  relief="flat", cursor="hand2").pack(side='left', ipady=5)

        self._working = []
        self._lock = threading.Lock()

    def _stop_scan(self):
        self._stop = True

    def _start_scan(self):
        fn = os.path.join(
            self.bridges_dir,
            self.get_safe_filename(self._cat_v.get(),
                                   self._trans_v.get(),
                                   self._ip_v.get()))
        if not os.path.exists(fn):
            messagebox.showwarning("Not Found",
                "Bridge file not found. Update bridges first.",
                parent=self._win)
            return
        with open(fn, encoding="utf-8") as f:
            lines = [l.strip() for l in f if l.strip()]
        if not lines:
            messagebox.showinfo("Empty", "Bridge file is empty.", parent=self._win)
            return

        for item in self._tree.get_children():
            self._tree.delete(item)
        self._working.clear()
        self._stop = False
        self._prog_var.set(0)
        self._summary_var.set("")
        self._scan_btn.configure(state='disabled')

        total = len(lines)
        done_count = [0]
        lock = threading.Lock()
        timeout = self._timeout_v.get()

        def _scan_one(line):
            if self._stop:
                return
            m = re.search(r'(\d{1,3}(?:\.\d{1,3}){3}):(\d+)', line)
            if not m:
                m6 = re.search(r'\[([0-9a-fA-F:]+)\]:(\d+)', line)
                if m6:
                    host, port = m6.group(1), int(m6.group(2))
                else:
                    with lock:
                        done_count[0] += 1
                    return
            else:
                host, port = m.group(1), int(m.group(2))

            t0 = time.time()
            try:
                s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
                s.settimeout(timeout)
                s.connect((host, port))
                s.close()
                ms = int((time.time() - t0) * 1000)
                tag = "ok" if ms < 500 else "slow"
                status = f"✔ {ms} ms"
                with self._lock:
                    self._working.append(line)
            except Exception as e:
                ms = None
                tag = "bad"
                status = f"✘ {str(e)[:30]}"

            self._win.after(0, self._tree.insert, '', 'end',
                            values=(self._trans_v.get(), host, port,
                                    str(ms) if ms is not None else "—", status),
                            tags=(tag,))
            with lock:
                done_count[0] += 1
                pct = int(done_count[0] * 100 / total)
                self._win.after(0, self._prog_var.set, pct)
                self._win.after(0, self._prog_lbl.set,
                                f"Scanning… {done_count[0]}/{total}")

        def _run():
            with ThreadPoolExecutor(max_workers=self._workers_v.get()) as ex:
                ex.map(_scan_one, lines)
            ok = len(self._working)
            self._win.after(0, self._prog_lbl.set, "Done.")
            self._win.after(0, self._summary_var.set,
                            f"✔ {ok} reachable  /  {total - ok} unreachable  /  {total} total")
            self._win.after(0, self._scan_btn.configure, {"state": "normal"})

        threading.Thread(target=_run, daemon=True).start()

    def _export(self):
        if not self._working:
            messagebox.showinfo("No Results",
                "Run a scan first, then export working bridges.",
                parent=self._win)
            return
        path = filedialog.asksaveasfilename(
            defaultextension=".txt",
            filetypes=[("Text files", "*.txt"), ("All files", "*.*")],
            title="Export Working Bridges",
            parent=self._win)
        if not path:
            return
        with open(path, "w", encoding="utf-8") as f:
            f.write("\n".join(self._working))
        messagebox.showinfo("Exported",
            f"Saved {len(self._working)} working bridges to:\n{path}",
            parent=self._win)

class SettingsWindow:
    def __init__(self, parent, cfg: dict, on_save, on_clear_data=None):
        self.on_save = on_save
        w = tk.Toplevel(parent)
        w.title("Settings")
        w.geometry("580x760")
        w.configure(bg=C["BG"])
        w.resizable(False, True)
        w.grab_set()
        w.update()
        apply_dark_titlebar(w)
        set_window_icon(w)

        tk.Frame(w, bg=C["ACC"], height=3).pack(fill='x')
        tk.Label(w, text="⬡  Settings",
                 font=('Segoe UI', 14, 'bold'), bg=C["BG"], fg=C["ACC"]).pack(pady=(12, 6))

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

        canvas.bind("<MouseWheel>", lambda e: canvas.yview_scroll(-1 * (e.delta // 120), "units"))
        inner.bind("<MouseWheel>",  lambda e: canvas.yview_scroll(-1 * (e.delta // 120), "units"))

        def _section(t, color=C["BTN"], fg=C["ACC"]):
            lbl = tk.Label(inner, text=t, font=('Segoe UI', 10, 'bold'),
                           bg=color, fg=fg, anchor='w', padx=10)
            lbl.pack(fill='x', pady=(10, 2))
            _bind_scroll(lbl)

        def _hint(t):
            lbl = tk.Label(inner, text=t, bg=C["BG"], fg=C["FG2"],
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
        _hint("  How long to wait at a stuck bootstrap % before trying next bridge group.")
        v_apc = tk.BooleanVar(value=cfg.get("auto_proxy_on_connect", True))
        _row("Auto-enable proxy on connect:", lambda p: _chk(p, v_apc))
        _hint("  Automatically turns on System Proxy when Tor reaches 100%.")

        _section("🌉  Bridges")
        v_bnum = tk.IntVar(value=cfg.get("bridges_in_torrc", 100))
        _row("Bridges written to torrc:", lambda p: _spin(p, v_bnum, 5, 300))
        v_shuf = tk.BooleanVar(value=cfg.get("shuffle_bridges", True))
        _row("Shuffle bridge order:", lambda p: _chk(p, v_shuf))
        _hint("  Randomising ensures different bridges are tried each session.")

        _section("🔐  SNI Settings")
        v_sni_e = tk.BooleanVar(value=cfg.get("sni_enabled", False))
        _row("Enable SNI override:", lambda p: _chk(p, v_sni_e))
        _hint("  Overrides the TLS SNI hostname sent during bridge handshake.\n"
              "  Useful to mimic popular HTTPS traffic and bypass DPI/censorship.")
        v_sni_h = tk.StringVar(value=cfg.get("sni_host", "www.google.com"))
        _row("SNI hostname:", lambda p: _entry(p, v_sni_h, 28))
        _hint("  Example: www.google.com  |  cloudflare.com  |  cdn.jsdelivr.net")

        _section("🔒  Privacy / DNS")
        v_dns = tk.BooleanVar(value=cfg.get("dns_over_tor", False))
        _row("DNS over Tor (DNSPort 9053):", lambda p: _chk(p, v_dns))
        _hint("  Routes DNS queries through Tor. Requires apps to use 127.0.0.1:9053.")

        _section("⚡  Circuit Building")
        v_mcd = tk.IntVar(value=cfg.get("max_circuit_dirtiness", 1800))
        _row("MaxCircuitDirtiness (sec):", lambda p: _spin(p, v_mcd, 60, 7200))
        v_ncp = tk.IntVar(value=cfg.get("new_circuit_period", 10))
        _row("NewCircuitPeriod (sec):", lambda p: _spin(p, v_ncp, 5, 300))
        v_neg = tk.IntVar(value=cfg.get("num_entry_guards", 15))
        _row("NumEntryGuards:", lambda p: _spin(p, v_neg, 1, 30))

        _section("💓  Keep-Alive")
        v_kae = tk.BooleanVar(value=cfg.get("keep_alive_enabled", True))
        _row("Keep-Alive enabled:", lambda p: _chk(p, v_kae))
        v_kai = tk.IntVar(value=cfg.get("keep_alive_interval", 120))
        _row("Keep-Alive interval (sec):", lambda p: _spin(p, v_kai, 30, 600))

        _section("🐕  Watchdog")
        v_wde = tk.BooleanVar(value=cfg.get("watchdog_enabled", True))
        _row("Watchdog enabled:", lambda p: _chk(p, v_wde))
        v_wdi = tk.IntVar(value=cfg.get("watchdog_interval", 30))
        _row("Check interval (sec):", lambda p: _spin(p, v_wdi, 10, 300))

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
        _row("StrictNodes:", lambda p: _chk(p, v_sne))

        _section("🗑️  Maintenance")
        _hint("  Clear cached Tor circuits and state (data directory).")

        def _do_clear():
            if on_clear_data:
                on_clear_data()
            messagebox.showinfo("Done", "Data directory cleared.", parent=w)

        tk.Button(inner, text="🗑️  Clear Data Directory", command=_do_clear,
                  bg=C["BTN2"], fg=C["RED"], font=('Segoe UI', 9, 'bold'),
                  relief="flat", cursor="hand2",
                  activebackground=C["BTN"]).pack(fill='x', padx=14, pady=(4, 6))

        _section("🧪  Experimental (Advanced torrc)",
                 color="#1A0F2E", fg=C["PRP"])
        warn_lbl = tk.Label(inner,
                            text="  ⚠️  All options below are OFF by default.\n"
                                 "  Wrong settings can break connectivity. Use with caution.",
                            bg="#1A0F2E", fg=C["YLW"],
                            font=('Segoe UI', 8, 'bold'), anchor='w', justify='left')
        warn_lbl.pack(fill='x', pady=(0, 4))
        _bind_scroll(warn_lbl)

        def _exp_section(t):
            lbl = tk.Label(inner, text=t, font=('Segoe UI', 9, 'bold'),
                           bg="#1A1030", fg=C["PRP"], anchor='w', padx=14)
            lbl.pack(fill='x', pady=(6, 1))
            _bind_scroll(lbl)

        _exp_section("― Connection & Padding")
        v_cp  = tk.BooleanVar(value=cfg.get("exp_connection_padding", False))
        _row("ConnectionPadding:", lambda p: _chk(p, v_cp))
        v_rcp = tk.BooleanVar(value=cfg.get("exp_reduced_connection_padding", False))
        _row("ReducedConnectionPadding:", lambda p: _chk(p, v_rcp))

        _exp_section("― Streams & Timeouts")
        v_cst = tk.IntVar(value=cfg.get("exp_circuit_stream_timeout", 0))
        _row("CircuitStreamTimeout (sec):", lambda p: _spin(p, v_cst, 0, 3600))
        v_st  = tk.IntVar(value=cfg.get("exp_socks_timeout", 0))
        _row("SocksTimeout (sec):", lambda p: _spin(p, v_st, 0, 600))

        _exp_section("― Stream Isolation")
        v_ida = tk.BooleanVar(value=cfg.get("exp_isolate_dest_addr", False))
        _row("IsolateDestAddr:", lambda p: _chk(p, v_ida))
        v_idp = tk.BooleanVar(value=cfg.get("exp_isolate_dest_port", False))
        _row("IsolateDestPort:", lambda p: _chk(p, v_idp))

        _exp_section("― Security & Disk")
        v_sl   = tk.BooleanVar(value=cfg.get("exp_safe_logging", False))
        _row("SafeLogging:", lambda p: _chk(p, v_sl))
        v_adw  = tk.BooleanVar(value=cfg.get("exp_avoid_disk_writes", False))
        _row("AvoidDiskWrites:", lambda p: _chk(p, v_adw))
        v_ha   = tk.BooleanVar(value=cfg.get("exp_hardware_accel", False))
        _row("HardwareAccel:", lambda p: _chk(p, v_ha))
        v_cdri = tk.BooleanVar(value=cfg.get("exp_client_dns_reject_internal", False))
        _row("ClientDNSRejectInternalAddresses:", lambda p: _chk(p, v_cdri))

        _exp_section("― Firewall & Network")
        v_ff = tk.BooleanVar(value=cfg.get("exp_fascist_firewall", False))
        _row("FascistFirewall:", lambda p: _chk(p, v_ff))
        v_fp = tk.StringVar(value=cfg.get("exp_firewall_ports", "80,443"))
        _row("FirewallPorts:", lambda p: _entry(p, v_fp, 20))
        v_ra = tk.StringVar(value=cfg.get("exp_reachable_addresses", ""))
        _row("ReachableAddresses:", lambda p: _entry(p, v_ra, 30))
        v_nc = tk.IntVar(value=cfg.get("exp_num_cpus", 0))
        _row("NumCPUs:", lambda p: _spin(p, v_nc, 0, 32))

        _section("― Node Selection")
        v_en    = tk.StringVar(value=cfg.get("exp_exclude_nodes", ""))
        _row("ExcludeNodes:", lambda p: _entry(p, v_en, 30))
        v_een   = tk.StringVar(value=cfg.get("exp_exclude_exit_nodes", ""))
        _row("ExcludeExitNodes:", lambda p: _entry(p, v_een, 30))
        v_nesp  = tk.StringVar(value=cfg.get("exp_no_exit_stream_ports", ""))
        _row("Reject exit ports:", lambda p: _entry(p, v_nesp, 20))
        v_ueag  = tk.BooleanVar(value=cfg.get("exp_use_entry_guards_as_dir_guards", False))
        _row("UseEntryGuardsAsDirGuards:", lambda p: _chk(p, v_ueag))
        v_pbct  = tk.IntVar(value=cfg.get("exp_path_bias_circ_threshold", 0))
        _row("PathBiasCircThreshold:", lambda p: _spin(p, v_pbct, 0, 200))

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
                "sni_enabled":                       v_sni_e.get(),
                "sni_host":                          v_sni_h.get().strip(),
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
                  bg=C["ACC"], fg="white", font=('Segoe UI', 10, 'bold'),
                  relief="flat", cursor="hand2",
                  activebackground=C["ACC2"]).pack(side='left', fill='x',
                                                    expand=True, padx=(0, 5), ipady=4)
        tk.Button(bf, text="Cancel", command=w.destroy,
                  bg=C["BTN"], fg=C["FG2"], font=('Segoe UI', 10),
                  relief="flat", cursor="hand2").pack(side='left', ipady=4, padx=40)

class ParallelConnectWindow:

    SRC_BUILTIN = "Default (Built-in)"
    SRC_DK      = "Delta-Kronecker"
    SRC_DIRECT  = "Direct (No Bridge)"

    BUILTIN_TRANSPORTS = ["snowflake", "meek"]
    DK_CATEGORIES  = ["Tested & Active", "Fresh (72h)", "Full Archive"]
    DK_TRANSPORTS  = ["obfs4", "webtunnel", "vanilla"]

    DEFAULT_SLOTS = [
        ("Snowflake",            SRC_BUILTIN, None,             "snowflake", None,   False),
        ("obfs4 · Tested IPv4",  SRC_DK,      "Tested & Active","obfs4",    "IPv4", False),
        ("Vanilla · Tested IPv4",SRC_DK,      "Tested & Active","vanilla",  "IPv4", False),
        ("WebTunnel · Tested",   SRC_DK,      "Tested & Active","webtunnel","IPv4", False),
    ]

    CHECK_HOST = "www.gstatic.com"
    CHECK_PATH = "/generate_204"

    def __init__(self, parent_frame, extract_dir, bridges_dir,
                 get_safe_filename, generate_torrc_fn, cfg, on_connected,
                 append_log_fn=None, on_status_change=None):
        self.extract_dir       = extract_dir
        self.bridges_dir       = bridges_dir
        self.get_safe_filename = get_safe_filename
        self.cfg               = cfg
        self.on_connected      = on_connected
        self._append_log       = append_log_fn
        self._on_status_change = on_status_change

        self._procs            = {}
        self._slot_logs        = {}
        self._running          = False
        self._lock             = threading.Lock()
        self._stop_events      = {}
        self._active_proxy_label = None
        self._proxy_stop_ev    = None
        self._slot_health      = {}
        self._slot_ping_history = {}
        self._slot_enabled     = {}
        self._slot_widgets     = {}
        self._slot_state       = {}

        saved = cfg.get("multi_slots", [])
        self._slot_defs = [list(s) for s in saved] if saved else [list(s) for s in self.DEFAULT_SLOTS]

        self._frame = parent_frame
        self._build_ui()

    def _build_ui(self):
        f = self._frame
        for w in f.winfo_children():
            w.destroy()
        self._slot_widgets.clear()

        self._info_lbl      = tk.Label(f, bg=C["BG"], fg=C["FG2"])
        self._port_info_lbl = tk.Label(f, bg=C["BG"], fg=C["CYAN"])

        toolbar = tk.Frame(f, bg=C["CARD"], bd=0)
        toolbar.pack(fill='x')
        tk.Frame(toolbar, bg=C["ACC"], height=2).pack(fill='x')

        inner = tk.Frame(toolbar, bg=C["CARD"])
        inner.pack(fill='x', padx=8, pady=6)

        _b = dict(font=('Segoe UI', 10, 'bold'), relief="flat", cursor="hand2", bd=0)

        left = tk.Frame(inner, bg=C["CARD"])
        left.pack(side='left')

        tk.Button(left, text="◀ Normal", command=self._go_back,
                  bg=C["BTN"], fg=C["FG2"],
                  activebackground=C["BTN2"], activeforeground=C["FG"],
                  **_b).pack(side='left', ipady=5, ipadx=10, padx=(0, 2))

        self._start_btn = tk.Button(
            left, text="▶ Start", command=self._start_all,
            bg=C["GRN"], fg="#0D1A13",
            disabledforeground="#0D1A13",
            activebackground="#24A066",
            **_b)
        self._start_btn.pack(side='left', ipady=5, ipadx=12, padx=(0, 2))

        tk.Button(left, text="⏹ Stop", command=self._stop_all,
                  bg=C["BTN"], fg=C["RED"],
                  activebackground=C["BTN2"], activeforeground=C["RED"],
                  **_b).pack(side='left', ipady=5, ipadx=12)

        self._auto_proxy_var = tk.BooleanVar(value=False)
        self._proxy_toggle_btn = tk.Button(
            inner, text="Auto Proxy :  OFF",
            command=self._toggle_auto_proxy,
            bg=C["BTN2"], fg=C["FG"],
            activebackground=C["ACC2"], activeforeground=C["FG"],
            **_b)
        self._proxy_toggle_btn.pack(side='right', ipady=5, ipadx=14)

        tk.Frame(f, bg=C["BTN2"], height=1).pack(fill='x')

        canvas_frame = tk.Frame(f, bg=C["BG"])
        canvas_frame.pack(fill='both', expand=True)
        self._canvas = tk.Canvas(canvas_frame, bg=C["BG"], highlightthickness=0)
        vsb = ttk.Scrollbar(canvas_frame, orient='vertical', command=self._canvas.yview)
        self._canvas.configure(yscrollcommand=vsb.set)
        vsb.pack(side='right', fill='y')
        self._canvas.pack(fill='both', expand=True)
        self._slots_inner = tk.Frame(self._canvas, bg=C["BG"])
        self._slots_win = self._canvas.create_window((0, 0), window=self._slots_inner, anchor='nw')
        self._slots_inner.bind("<Configure>", lambda e: self._canvas.configure(
            scrollregion=self._canvas.bbox("all")))
        self._canvas.bind("<Configure>",
            lambda e: self._canvas.itemconfig(self._slots_win, width=e.width))
        self._canvas.bind("<MouseWheel>",
            lambda e: self._canvas.yview_scroll(-1*(e.delta//120), "units"))

        bottom_bar = tk.Frame(f, bg=C["BG"])
        bottom_bar.pack(fill='x', pady=(6, 2))
        tk.Button(bottom_bar, text="➕  Add Connection Mode",
                  command=self._open_add_slot_dialog,
                  bg=C["BTN"], fg=C["CYAN"],
                  font=('Segoe UI', 10, 'bold'),
                  relief="flat", cursor="hand2",
                  activebackground=C["BTN2"]
                  ).pack(ipady=7, ipadx=20)

        self._rebuild_cards()

    def _go_back(self):
        if self._proxy_stop_ev:
            self._proxy_stop_ev.set()
            self._proxy_stop_ev = None
        if self._active_proxy_label:
            self._disable_system_proxy()
            prev = self._active_proxy_label
            self._active_proxy_label = None
            w = self._slot_widgets.get(prev)
            if w:
                try:
                    w["proxy_btn"].configure(text="🌐 Set Proxy", bg=C["BTN"], fg=C["FG"])
                except Exception:
                    pass
        self._update_proxy_status_bar(None, None, None)
        try:
            w = self._frame.winfo_toplevel()
            app = getattr(w, '_app', None)
            if app and hasattr(app, '_switch_to_mode'):
                app._switch_to_mode("normal")
        except Exception:
            pass

    def _toggle_auto_proxy(self):
        new_val = not self._auto_proxy_var.get()
        self._auto_proxy_var.set(new_val)
        if new_val:
            self._proxy_toggle_btn.configure(
                bg=C["ACC"], fg=C["FG"],
                text="Auto Proxy :  ON")
        else:
            self._proxy_toggle_btn.configure(
                bg=C["BTN2"], fg=C["FG"],
                text="Auto Proxy :  OFF")
            if self._proxy_stop_ev:
                self._proxy_stop_ev.set()
                self._proxy_stop_ev = None
            if self._active_proxy_label:
                self._disable_system_proxy()
                prev = self._active_proxy_label
                self._active_proxy_label = None
                w = self._slot_widgets.get(prev)
                if w:
                    w["proxy_btn"].configure(text="🌐 Set Proxy", bg=C["BTN"], fg=C["FG"])
            self._update_proxy_status_bar(None, None, None)

    def _rebuild_cards(self):
        for w in self._slots_inner.winfo_children():
            w.destroy()
        self._slot_widgets.clear()
        self._slots_inner.columnconfigure(0, weight=1)
        self._slots_inner.columnconfigure(1, weight=1)
        for i, sdef in enumerate(self._slot_defs):
            label, source, cat, trans, ip, no_bridge = sdef
            socks, ctrl, http = self._slot_ports(i)
            if label not in self._slot_enabled:
                self._slot_enabled[label] = tk.BooleanVar(value=True)
            self._build_card(i, label, source, cat, trans, ip, no_bridge, socks, ctrl, http)

        for label, st in self._slot_state.items():
            w = self._slot_widgets.get(label)
            if not w:
                continue
            pct = st.get("pct")
            if pct is not None:
                w["prog_var"].set(pct)
                w["pct_lbl"].configure(text=f"{pct}%")
            bar_style = st.get("bar_style")
            if bar_style:
                w["bar"].configure(style=bar_style)
            status = st.get("status")
            if status is not None:
                w["status_lbl"].configure(
                    text=status,
                    fg=st.get("status_fg", C["FG2"]))
            health = st.get("health")
            if health is not None:
                w["health_lbl"].configure(
                    text=health,
                    fg=st.get("health_fg", C["FG2"]))

    def _slot_ports(self, index):
        return (9061 + index, 9071 + index, 19061 + index)

    def _build_card(self, idx, label, source, cat, trans, ip, no_bridge, socks, ctrl, http):
        si = self._slots_inner
        sf = tk.Frame(si, bg=C["PANEL"], bd=0)
        sf.grid(row=idx // 2, column=idx % 2, padx=6, pady=6, sticky="nsew")

        strip_color = C["ACC"]
        tk.Frame(sf, bg=strip_color, width=4).pack(side='left', fill='y')

        inner = tk.Frame(sf, bg=C["PANEL"])
        inner.pack(fill='both', expand=True, padx=10, pady=8)

        top_r = tk.Frame(inner, bg=C["PANEL"])
        top_r.pack(fill='x')

        ev = self._slot_enabled.get(label, tk.BooleanVar(value=True))
        self._slot_enabled[label] = ev
        _cv = tk.Canvas(top_r, width=22, height=22, bg=C["PANEL"],
                        highlightthickness=0, cursor="hand2")
        _cv.pack(side='left', padx=(0, 4))
        def _draw_toggle(canvas, var):
            canvas.delete("all")
            canvas.create_rectangle(1, 1, 21, 21, outline=C["BORDER"], fill=C["BTN"], width=1)
            if var.get():
                canvas.create_rectangle(4, 4, 18, 18, fill=C["GRN"], outline="")
        def _click_toggle(event, lbl=label, canvas=_cv, var=ev):
            var.set(not var.get())
            _draw_toggle(canvas, var)
            self._on_toggle_slot(lbl)
        _cv.bind("<Button-1>", _click_toggle)
        _draw_toggle(_cv, ev)
        self._slot_enabled[label + "__cv"] = (_cv, ev)

        tk.Label(top_r, text=label,
                 font=('Segoe UI', 11, 'bold'), bg=C["PANEL"], fg=C["FG"]
                 ).pack(side='left', padx=(2, 8))

        status_lbl = tk.Label(top_r, text="Idle",
                              font=('Segoe UI', 9), bg=C["PANEL"], fg=C["FG2"])
        status_lbl.pack(side='right')

        src_text = f"{source}  ·  {cat or '—'}  ·  {trans}  ·  {ip or 'auto'}"
        tk.Label(inner, text=src_text,
                 font=('Segoe UI', 8), bg=C["PANEL"], fg=C["FG2"]).pack(anchor='w')

        tk.Label(inner, text=f"SOCKS {socks}  ·  HTTP {http}",
                 font=('Consolas', 9, 'bold'), bg=C["PANEL"], fg=C["CYAN"]).pack(anchor='w')

        prog_row = tk.Frame(inner, bg=C["PANEL"])
        prog_row.pack(fill='x', pady=(4, 0))
        prog_var = tk.IntVar(value=0)
        bar = ttk.Progressbar(prog_row, variable=prog_var, maximum=100, mode='determinate')
        bar.pack(side='left', fill='x', expand=True)
        pct_lbl = tk.Label(prog_row, text="0%",
                           font=('Segoe UI', 9, 'bold'), bg=C["PANEL"], fg=C["FG2"], width=5)
        pct_lbl.pack(side='right')

        health_row = tk.Frame(inner, bg=C["PANEL"])
        health_row.pack(fill='x', pady=(2, 0))
        health_lbl = tk.Label(health_row, text="⬤ —",
                              font=('Segoe UI', 9), bg=C["PANEL"], fg=C["FG2"])
        health_lbl.pack(side='left')

        btn_row = tk.Frame(inner, bg=C["PANEL"])
        btn_row.pack(fill='x', pady=(6, 0))

        proxy_btn = tk.Button(btn_row, text="🌐 Set Proxy",
                              bg=C["BTN"], fg=C["FG"],
                              font=('Segoe UI', 9, 'bold'), relief="flat", cursor="hand2",
                              activebackground=C["BTN2"])
        proxy_btn.pack(side='left', ipady=4, padx=(0, 4))
        proxy_btn.configure(command=lambda lbl=label, sp=socks, hp=http:
                            self._set_proxy_to_slot(lbl, sp, hp))

        retry_btn = tk.Button(btn_row, text="↺ Retry",
                              bg=C["BTN"], fg=C["FG"],
                              font=('Segoe UI', 9, 'bold'), relief="flat", cursor="hand2",
                              activebackground=C["BTN2"])
        retry_btn.pack(side='left', ipady=4, padx=(0, 4))
        retry_btn.configure(command=lambda lbl=label, s=socks, c=ctrl, hp=http,
                                       nb=no_bridge, src=source, ca=cat,
                                       tr=trans, iip=ip:
                            self._retry_slot(lbl, s, c, hp, nb, src, ca, tr, iip))

        health_btn = tk.Button(btn_row, text="🔍 Health",
                               bg=C["BTN"], fg=C["FG"],
                               font=('Segoe UI', 9, 'bold'), relief="flat", cursor="hand2",
                               activebackground=C["BTN2"])
        health_btn.pack(side='left', ipady=4, padx=(0, 4))
        health_btn.configure(command=lambda lbl=label, s=socks: self._manual_health(lbl, s))

        log_btn = tk.Button(btn_row, text="📋 Log",
                            bg=C["BTN"], fg=C["FG"],
                            font=('Segoe UI', 9, 'bold'), relief="flat", cursor="hand2",
                            activebackground=C["BTN2"])
        log_btn.pack(side='left', ipady=4, padx=(0, 4))
        log_btn.configure(command=lambda lbl=label: self._show_slot_log(lbl))

        del_btn = tk.Button(btn_row, text="🗑",
                            bg=C["BTN"], fg=C["RED"],
                            font=('Segoe UI', 9), relief="flat", cursor="hand2",
                            activebackground=C["BTN2"])
        del_btn.pack(side='right', ipady=4)
        del_btn.configure(command=lambda lbl=label: self._delete_slot(lbl))

        self._slot_widgets[label] = {
            "frame":      sf,
            "prog_var":   prog_var,
            "pct_lbl":    pct_lbl,
            "status_lbl": status_lbl,
            "bar":        bar,
            "health_lbl": health_lbl,
            "proxy_btn":  proxy_btn,
            "socks_port": socks,
            "http_port":  http,
        }

    def _open_add_slot_dialog(self):
        dlg = tk.Toplevel(self._frame.winfo_toplevel())
        dlg.title("Add Connection Mode")
        dlg.geometry("440x360")
        dlg.configure(bg=C["BG"])
        dlg.resizable(False, False)
        dlg.update()
        apply_dark_titlebar(dlg)

        tk.Frame(dlg, bg=C["ACC"], height=3).pack(fill='x')
        tk.Label(dlg, text="➕  Add New Mode",
                 font=('Segoe UI', 12, 'bold'), bg=C["BG"], fg=C["ACC"]).pack(pady=(12, 8))

        frm = tk.Frame(dlg, bg=C["BG"])
        frm.pack(fill='x', padx=24)

        OM_CFG = dict(bg=C["BTN"], fg=C["FG"], activebackground=C["BTN2"],
                      activeforeground=C["FG"], highlightthickness=0,
                      relief="flat", font=('Segoe UI', 10))
        MENU_CFG = dict(bg=C["BTN"], fg=C["FG"], activebackground=C["ACC"],
                        activeforeground=C["FG"], borderwidth=0,
                        font=('Segoe UI', 10))

        def om_row(txt, var, options):
            r = tk.Frame(frm, bg=C["BG"])
            r.pack(fill='x', pady=5)
            tk.Label(r, text=txt, width=14, anchor='w',
                     bg=C["BG"], fg=C["FG"], font=('Segoe UI', 10)).pack(side='left')
            om = tk.OptionMenu(r, var, *options)
            om.configure(**OM_CFG)
            om["menu"].configure(**MENU_CFG)
            om.pack(side='left', fill='x', expand=True, ipady=3)
            return om

        v_name   = tk.StringVar(value="New Mode")
        v_source = tk.StringVar(value=self.SRC_DK)
        v_cat    = tk.StringVar(value="Tested & Active")
        v_trans  = tk.StringVar(value="obfs4")
        v_ip     = tk.StringVar(value="IPv4")

        nr = tk.Frame(frm, bg=C["BG"])
        nr.pack(fill='x', pady=5)
        tk.Label(nr, text="Name:", width=14, anchor='w',
                 bg=C["BG"], fg=C["FG"], font=('Segoe UI', 10)).pack(side='left')
        tk.Entry(nr, textvariable=v_name,
                 bg=C["BTN"], fg=C["FG"], insertbackground=C["FG"],
                 relief="flat", bd=6, font=('Segoe UI', 10)).pack(side='left', fill='x', expand=True)

        src_om  = om_row("Source:",    v_source, [self.SRC_BUILTIN, self.SRC_DK, self.SRC_DIRECT])

        cat_frame = tk.Frame(frm, bg=C["BG"])
        cat_frame.pack(fill='x', pady=5)
        tk.Label(cat_frame, text="Category:", width=14, anchor='w',
                 bg=C["BG"], fg=C["FG"], font=('Segoe UI', 10)).pack(side='left')
        cat_om = tk.OptionMenu(cat_frame, v_cat, *self.DK_CATEGORIES)
        cat_om.configure(**OM_CFG)
        cat_om["menu"].configure(**MENU_CFG)
        cat_om.pack(side='left', fill='x', expand=True, ipady=3)

        trans_om = om_row("Transport:", v_trans, self.DK_TRANSPORTS)
        ip_om    = om_row("IP Version:", v_ip,   ["IPv4", "IPv6", "Both"])

        def _on_source_change(*_):
            src = v_source.get()
            if src == self.SRC_BUILTIN:
                trans_om["menu"].delete(0, "end")
                for opt in self.BUILTIN_TRANSPORTS:
                    trans_om["menu"].add_command(label=opt,
                                                  command=lambda o=opt: v_trans.set(o))
                v_trans.set("snowflake")
                cat_om.configure(state='disabled')
                ip_om.configure(state='disabled')
                trans_om.configure(state='normal')
            elif src == self.SRC_DIRECT:
                cat_om.configure(state='disabled')
                trans_om.configure(state='disabled')
                ip_om.configure(state='disabled')
            else:
                trans_om["menu"].delete(0, "end")
                for opt in self.DK_TRANSPORTS:
                    trans_om["menu"].add_command(label=opt,
                                                  command=lambda o=opt: v_trans.set(o))
                v_trans.set("obfs4")
                cat_om.configure(state='normal')
                ip_om.configure(state='normal')
                trans_om.configure(state='normal')

        v_source.trace_add("write", _on_source_change)
        _on_source_change()

        def _add():
            name = v_name.get().strip() or "Mode"
            existing = [s[0] for s in self._slot_defs]
            if name in existing:
                name = f"{name} ({len(existing)+1})"
            src = v_source.get()
            if src == self.SRC_BUILTIN:
                new_slot = [name, src, None, v_trans.get(), None, False]
            elif src == self.SRC_DIRECT:
                new_slot = [name, src, None, None, None, True]
            else:
                new_slot = [name, src, v_cat.get(), v_trans.get(), v_ip.get(), False]
            self._slot_defs.append(new_slot)
            self._save_slots()
            dlg.destroy()
            self._rebuild_cards()

        bf = tk.Frame(dlg, bg=C["BG"])
        bf.pack(fill='x', padx=24, pady=14)
        tk.Button(bf, text="✔  Add", command=_add,
                  bg=C["ACC"], fg=C["FG"],
                  font=('Segoe UI', 10, 'bold'), relief="flat", cursor="hand2",
                  activebackground=C["ACC2"]).pack(side='left', ipady=5, padx=(0, 8))
        tk.Button(bf, text="Cancel", command=dlg.destroy,
                  bg=C["BTN"], fg=C["FG2"],
                  font=('Segoe UI', 10), relief="flat", cursor="hand2").pack(side='left', ipady=5)

    def _delete_slot(self, label):
        slot_idx = next((i for i, s in enumerate(self._slot_defs) if s[0] == label), None)
        socks_port = self._slot_ports(slot_idx)[0] if slot_idx is not None else None

        if self._running:
            ev = self._stop_events.pop(label, None)
            if ev: ev.set()
            with self._lock:
                p = self._procs.pop(label, None)
                if p:
                    try:
                        p.terminate()
                        p.wait(timeout=3)
                    except: pass
        self._slot_defs = [s for s in self._slot_defs if s[0] != label]
        self._slot_widgets.pop(label, None)
        self._slot_enabled.pop(label, None)
        self._slot_health.pop(label, None)
        self._slot_ping_history.pop(label, None)
        self._slot_state.pop(label, None)

        if socks_port is not None:
            data_dir = os.path.join(self.extract_dir, f"data_par_{socks_port}")
            if os.path.isdir(data_dir):
                try:
                    shutil.rmtree(data_dir)
                except Exception:
                    pass

        self._save_slots()
        self._rebuild_cards()

    def _save_slots(self):
        self.cfg["multi_slots"] = [list(s) for s in self._slot_defs]
        save_config(self.cfg)

    def _on_toggle_slot(self, label):
        enabled = self._slot_enabled[label].get()
        if self._running:
            if enabled:
                idx = next((i for i, s in enumerate(self._slot_defs) if s[0] == label), 0)
                sdef = self._slot_defs[idx]
                _, src, cat, trans, ip, nb = sdef
                socks, ctrl, http = self._slot_ports(idx)
                threading.Thread(target=self._run_slot,
                                 args=(label, socks, ctrl, http, nb, src, cat, trans, ip),
                                 daemon=True).start()
            else:
                ev = self._stop_events.pop(label, None)
                if ev: ev.set()
                with self._lock:
                    p = self._procs.get(label)
                    if p:
                        try: p.terminate()
                        except: pass
                w = self._slot_widgets.get(label)
                if w:
                    self._frame.after(0, w["status_lbl"].configure,
                                      {"text": "Disabled", "fg": C["FG2"]})
                    self._frame.after(0, w["prog_var"].set, 0)

    def _update_slot(self, label, pct=None, status=None,
                     connected=False, failed=False):
        w = self._slot_widgets.get(label)
        st = self._slot_state.setdefault(label, {})
        if pct is not None:
            st["pct"] = pct
            if w:
                w["prog_var"].set(pct)
                w["pct_lbl"].configure(text=f"{pct}%")
        if status is not None:
            color = C["GRN"] if connected else (C["RED"] if failed else C["FG2"])
            st["status"] = status
            st["status_fg"] = color
            if w:
                w["status_lbl"].configure(text=status, fg=color)
        if connected:
            st["bar_style"] = 'Won.Horizontal.TProgressbar'
            if w:
                w["bar"].configure(style='Won.Horizontal.TProgressbar')
        elif failed:
            st["bar_style"] = 'Horizontal.TProgressbar'
            if w:
                w["bar"].configure(style='Horizontal.TProgressbar')

    def _check_health(self, socks_port, timeout=15):
        result = [False, float(timeout * 1000)]

        def _do():
            t0 = time.time()
            s = None
            try:
                s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
                s.settimeout(timeout)
                s.connect(("127.0.0.1", socks_port))
                host_b = self.CHECK_HOST.encode()
                s.sendall(b'\x05\x01\x00')
                if s.recv(2)[1] != 0x00:
                    return
                s.sendall(b'\x05\x01\x00\x03' + bytes([len(host_b)]) +
                          host_b + (443).to_bytes(2, 'big'))
                if s.recv(10)[1] != 0x00:
                    return
                ctx = ssl.create_default_context()
                s = ctx.wrap_socket(s, server_hostname=self.CHECK_HOST)
                s.sendall((f"GET {self.CHECK_PATH} HTTP/1.1\r\nHost: {self.CHECK_HOST}\r\n"
                           f"Connection: close\r\nUser-Agent: Mozilla/5.0\r\n\r\n").encode())
                resp = s.recv(512).decode(errors="replace")
                latency = (time.time() - t0) * 1000.0
                if "204" in resp or "HTTP/1." in resp:
                    result[0] = True
                    result[1] = latency
            except Exception:
                pass
            finally:
                try:
                    if s:
                        s.close()
                except Exception:
                    pass

        t = threading.Thread(target=_do, daemon=True)
        t.start()
        t.join(timeout)
        return result[0], result[1]

    def _health_loop(self, label, socks_port, stop_ev):
        while not stop_ev.wait(15):
            ok, lat = self._check_health(socks_port, timeout=15)
            if not ok:
                lat = 15000.0

            with self._lock:
                history = self._slot_ping_history.setdefault(label, [])
                history.append(lat)
                if len(history) > 20:
                    history[:] = history[-20:]
                avg_lat = sum(history) / len(history)
                self._slot_health[label] = (ok, avg_lat)

            w = self._slot_widgets.get(label)
            if ok:
                txt = f"⬤ Online  {int(lat)} ms  (avg {int(avg_lat)} ms)"
            else:
                txt = f"⬤ Offline  (avg {int(avg_lat)} ms)"
            fg = C["GRN"] if ok else C["RED"]
            st = self._slot_state.setdefault(label, {})
            st["health"] = txt
            st["health_fg"] = fg
            if w:
                self._frame.after(0, w["health_lbl"].configure, {"text": txt, "fg": fg})
            if self._auto_proxy_var.get():
                self._best_proxy()

    def _best_proxy(self):
        with self._lock:
            best, best_avg = None, float('inf')
            for lbl, (ok, avg_lat) in self._slot_health.items():
                if avg_lat < best_avg:
                    best_avg, best = avg_lat, lbl
        if best and best != self._active_proxy_label:
            for i, sdef in enumerate(self._slot_defs):
                if sdef[0] == best:
                    socks, _, http = self._slot_ports(i)
                    self._frame.after(0, self._set_proxy_to_slot, best, socks, http)
                    break

    def _manual_health(self, label, socks_port):
        def _run():
            ok, lat = self._check_health(socks_port)
            w = self._slot_widgets.get(label)
            if w:
                txt = f"⬤ Online  {int(lat)} ms" if ok else "⬤ Offline"
                self._frame.after(0, w["health_lbl"].configure,
                                  {"text": txt, "fg": C["GRN"] if ok else C["RED"]})
        threading.Thread(target=_run, daemon=True).start()

    def _show_slot_log(self, label):
        self._canvas.pack_forget()
        if hasattr(self, '_log_view_frame') and self._log_view_frame.winfo_exists():
            self._log_view_frame.destroy()

        lf = tk.Frame(self._frame, bg=C["BG"])
        lf = tk.Frame(self._frame, bg=C["BG"])
        lf = tk.Frame(self._frame, bg=C["BG"])
        lf.place(x=0, y=0, relwidth=1, height=1200)  # ارتفاع دقیق 800 پیکسل
        lf.pack_propagate(False)

        self._log_view_frame = lf

        hdr = tk.Frame(lf, bg=C["CARD"])
        hdr.pack(fill='x')
        tk.Frame(hdr, bg=C["ACC"], height=2).pack(fill='x', side='top')
        _h = tk.Frame(hdr, bg=C["CARD"])
        _h.pack(fill='x', padx=10, pady=6)
        tk.Label(_h, text=f"📋  Log — {label}",
                 font=('Segoe UI', 11, 'bold'), bg=C["CARD"], fg=C["ACC"]).pack(side='left')
        tk.Button(_h, text="◀  Back to Slots",
                  command=self._hide_slot_log,
                  bg=C["BTN"], fg=C["FG"],
                  font=('Segoe UI', 9, 'bold'), relief="flat", cursor="hand2",
                  activebackground=C["BTN2"]).pack(side='right', ipady=4, ipadx=8)

        txt_f = tk.Frame(lf, bg=C["PANEL"])
        txt_f.pack(fill='both', expand=True)
        txt = tk.Text(txt_f, font=('Consolas', 9), wrap='word',
                      state='disabled', bg=C["PANEL"], fg=C["FG2"],
                      bd=0, padx=10, pady=8)
        txt.tag_configure("warn",   foreground=C["YLW"])
        txt.tag_configure("err",    foreground=C["RED"])
        txt.tag_configure("notice", foreground=C["GRN"])
        sb = ttk.Scrollbar(txt_f, command=txt.yview)
        txt.configure(yscrollcommand=sb.set)
        sb.pack(side='right', fill='y')
        txt.pack(side='left', fill='both', expand=True)

        logs = self._slot_logs.get(label, [])
        txt.configure(state='normal')
        if logs:
            for line in logs:
                tag = "err" if " [err]" in line or "Error" in line else                       "warn" if " [warn]" in line or "Warn" in line else                       "notice" if "Bootstrapped" in line or "Done" in line else ""
                txt.insert('end', line + "\n", tag)
        else:
            txt.insert('end', "No log yet. Start the connection first.\n", "")
        txt.configure(state='disabled')
        txt.see('end')
        self._log_view_label = label
        self._log_view_txt   = txt

        self._log_view_active = True
        self._frame.after(1000, self._refresh_slot_log)

    def _refresh_slot_log(self):
        if not getattr(self, '_log_view_active', False):
            return
        if not hasattr(self, '_log_view_txt') or not self._log_view_txt.winfo_exists():
            return
        label = self._log_view_label
        logs  = self._slot_logs.get(label, [])
        txt   = self._log_view_txt
        current = int(txt.index('end-1c').split('.')[0]) - 1
        new_lines = logs[current:]
        if new_lines:
            txt.configure(state='normal')
            for line in new_lines:
                tag = "err"    if " [err]"  in line or "Error" in line else                       "warn"   if " [warn]" in line or "Warn"  in line else                       "notice" if "Bootstrapped" in line or "Done" in line else ""
                txt.insert('end', line + "\n", tag)
            txt.configure(state='disabled')
            txt.see('end')
        self._frame.after(1000, self._refresh_slot_log)

    def _hide_slot_log(self):
        self._log_view_active = False
        if hasattr(self, '_log_view_frame') and self._log_view_frame.winfo_exists():
            self._log_view_frame.destroy()
        self._canvas.pack(fill='both', expand=True)

    def _set_proxy_to_slot(self, label, socks_port, http_port):
        if self._proxy_stop_ev:
            self._proxy_stop_ev.set()
            self._proxy_stop_ev = None
        for lbl, wgt in self._slot_widgets.items():
            wgt["proxy_btn"].configure(text="🌐 Set Proxy", bg=C["BTN"], fg=C["FG"])
        if self._active_proxy_label == label and not self._auto_proxy_var.get():
            self._active_proxy_label = None
            self._disable_system_proxy()
            self._frame.after(0, self._info_lbl.configure,
                              {"text": "Proxy disabled.", "fg": C["FG2"]})
            self._frame.after(0, self._port_info_lbl.configure, {"text": ""})
            self._update_proxy_status_bar(None, None, None, None)
            return
        ev = threading.Event()
        self._proxy_stop_ev = ev
        threading.Thread(target=run_http_proxy_server,
                         args=(ev, "127.0.0.1", socks_port, http_port),
                         daemon=True).start()
        self._enable_system_proxy(http_port)
        self._active_proxy_label = label
        w = self._slot_widgets.get(label)
        if w:
            w["proxy_btn"].configure(text="🌐 Proxy ON", bg=C["SEL"], fg=C["GRN"])
        self._frame.after(0, self._info_lbl.configure,
                          {"text": f"✔ Proxy → {label}", "fg": C["GRN"]})
        self._frame.after(0, self._port_info_lbl.configure,
                          {"text": f"SOCKS {socks_port}  ·  HTTP {http_port}"})
        sdef = next((s for s in self._slot_defs if s[0] == label), None)
        if sdef:
            _, src, cat, trans, ip, _ = sdef
            self._update_proxy_status_bar(label, trans, ip, socks_port, http_port, cat)
        else:
            self._update_proxy_status_bar(label, None, None, socks_port, http_port)

    def _enable_system_proxy(self, http_port):
        try:
            key = winreg.OpenKey(winreg.HKEY_CURRENT_USER,
                r'Software\Microsoft\Windows\CurrentVersion\Internet Settings',
                0, winreg.KEY_ALL_ACCESS)
            winreg.SetValueEx(key, 'ProxyEnable',   0, winreg.REG_DWORD, 1)
            winreg.SetValueEx(key, 'ProxyServer',   0, winreg.REG_SZ, f'127.0.0.1:{http_port}')
            winreg.SetValueEx(key, 'ProxyOverride', 0, winreg.REG_SZ, '127.0.0.1;localhost;<local>')
            winreg.CloseKey(key)
            ctypes.windll.wininet.InternetSetOptionW(0, 39, 0, 0)
            ctypes.windll.wininet.InternetSetOptionW(0, 37, 0, 0)
        except Exception:
            pass

    def _disable_system_proxy(self):
        try:
            key = winreg.OpenKey(winreg.HKEY_CURRENT_USER,
                r'Software\Microsoft\Windows\CurrentVersion\Internet Settings',
                0, winreg.KEY_ALL_ACCESS)
            winreg.SetValueEx(key, 'ProxyEnable',   0, winreg.REG_DWORD, 0)
            winreg.SetValueEx(key, 'ProxyServer',   0, winreg.REG_SZ, '')
            winreg.SetValueEx(key, 'ProxyOverride', 0, winreg.REG_SZ, '')
            winreg.CloseKey(key)
            ctypes.windll.wininet.InternetSetOptionW(0, 39, 0, 0)
            ctypes.windll.wininet.InternetSetOptionW(0, 37, 0, 0)
        except Exception:
            pass

    def _update_proxy_status_bar(self, label, trans, ip, socks_port=None, http_port=None, cat=None):
        try:
            app = self._frame.winfo_toplevel()._app
            lbl = getattr(app, '_proxy_status_lbl', None)
            if lbl is None:
                return
            if label is None:
                lbl.configure(text="", fg=C["FG2"])
                return
            parts = []
            parts.append("proxy: active")
            desc = ""
            if trans:
                desc += trans
            if cat:
                desc += f" {cat}"
            if ip:
                desc += f" {ip}"
            if desc:
                parts.append(desc.strip())
            if http_port:
                parts.append(f"HTTP: {http_port}")
            if socks_port:
                parts.append(f"SOCKS: {socks_port}")
            lbl.configure(text="  |  ".join(parts), fg=C["GRN"])
        except Exception:
            pass

    def _run_slot(self, label, socks_port, ctrl_port, http_port,
                  no_bridge, source, cat, trans, ip,
                  retry_count=0, max_retries=5):
        ev = self._slot_enabled.get(label)
        if ev and not ev.get():
            return

        tor_exe = os.path.join(self.extract_dir, "tor", "tor.exe")
        if not os.path.exists(tor_exe):
            self._frame.after(0, self._update_slot, label, None, "tor.exe not found", False, True)
            return

        def _port_in_use(port):
            try:
                s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
                s.settimeout(0.3)
                result = s.connect_ex(("127.0.0.1", port))
                s.close()
                return result == 0
            except OSError:
                return False

        if _port_in_use(socks_port):
            self._frame.after(0, self._update_slot, label, None,
                              f"Port {socks_port} busy — waiting…")
            for _ in range(15):
                if not self._running:
                    return
                time.sleep(2)
                if not _port_in_use(socks_port):
                    break

        with self._lock:
            old = self._procs.get(label)
            if old:
                try: old.terminate()
                except: pass

        data_dir     = os.path.join(self.extract_dir, f"data_par_{socks_port}")
        pt_state_dir = os.path.join(data_dir, "pt_state")
        os.makedirs(data_dir, exist_ok=True)
        os.makedirs(pt_state_dir, exist_ok=True)
        torrc_path = os.path.join(data_dir, "torrc")
        pt_dir     = os.path.join(self.extract_dir, "tor", "pluggable_transports")
        lyrebird   = os.path.join(pt_dir, "lyrebird.exe")
        conjure    = os.path.join(pt_dir, "conjure-client.exe")

        if no_bridge:
            bridge_lines, use = [], "0"
        else:
            bridge_lines = []
            limit      = self.cfg.get("bridges_in_torrc", 100)
            do_shuffle = self.cfg.get("shuffle_bridges", True)

            if source == self.SRC_BUILTIN:
                cfg_file = os.path.join(pt_dir, "pt_config.json")
                if os.path.exists(cfg_file):
                    try:
                        with open(cfg_file, encoding="utf-8") as f:
                            entries = json.load(f).get("bridges", {}).get(trans or "snowflake", [])
                        if do_shuffle:
                            entries = list(entries); random.shuffle(entries)
                        for b in entries[:limit]:
                            bridge_lines.append(f"Bridge {b}\n")
                    except Exception:
                        pass
            else:
                for c, t, v, _ in BRIDGE_DATA:
                    match_cat   = (cat is None) or (c == cat)
                    match_trans = (t == trans)
                    match_ip    = (ip is None) or (ip == "Both") or (ip == v)
                    if match_cat and match_trans and match_ip:
                        fn = os.path.join(self.bridges_dir, self.get_safe_filename(c, t, v))
                        if os.path.exists(fn):
                            with open(fn, encoding="utf-8") as f:
                                lines = [l.strip() for l in f if l.strip()]
                            if do_shuffle:
                                random.shuffle(lines)
                            for line in lines[:limit]:
                                bridge_lines.append(f"Bridge {line}\n")
            use = "1" if bridge_lines else "0"

        content  = "Log notice stdout\n"
        content += f"DataDirectory {data_dir}\n"
        content += f"GeoIPFile {os.path.join(data_dir, 'geoip')}\n"
        content += f"GeoIPv6File {os.path.join(data_dir, 'geoip6')}\n"
        content += f"SOCKSPort 127.0.0.1:{socks_port}\n"
        content += f"ControlPort 127.0.0.1:{ctrl_port}\n"
        content += "CookieAuthentication 1\n"
        content += "DormantClientTimeout 24 hours\n"
        content += "DormantOnFirstStartup 0\n"
        content += "DormantCanceledByStartup 1\n"
        content += f"UseBridges {use}\n"
        content += f"MaxCircuitDirtiness {self.cfg.get('max_circuit_dirtiness', 1800)}\n"
        content += f"NewCircuitPeriod {self.cfg.get('new_circuit_period', 10)}\n"
        content += f"NumEntryGuards {self.cfg.get('num_entry_guards', 15)}\n"
        content += "AllowNonRFC953Hostnames 1\n"
        content += "EnforceDistinctSubnets 0\n"
        content += "MaxClientCircuitsPending 64\n"
        content += "CircuitBuildTimeout 60\n"
        content += "LearnCircuitBuildTimeout 0\n"
        content += "GuardLifetime 90 days\n"
        content += "NumDirectoryGuards 6\n"
        content += "TokenBucketRefillInterval 10 msec\n"
        if use == "1":
            content += (f"ClientTransportPlugin meek_lite,obfs2,obfs3,obfs4,"
                        f"scramblesuit,webtunnel exec {lyrebird}\n")
            content += f"ClientTransportPlugin snowflake exec {lyrebird}\n"
            content += (f"ClientTransportPlugin conjure exec {conjure}"
                        f" -registerURL \"https://registration.refraction.network/api\"\n")
        content += "\n"
        if use == "1":
            content += "".join(bridge_lines)

        with open(torrc_path, "w", encoding="utf-8") as f:
            f.write(content)

        slot_env = os.environ.copy()
        slot_env["TOR_PT_STATE_LOCATION"] = pt_state_dir

        try:
            proc = subprocess.Popen(
                [tor_exe, "-f", torrc_path],
                stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                text=True, creationflags=subprocess.CREATE_NO_WINDOW,
                env=slot_env)
        except Exception as e:
            self._frame.after(0, self._update_slot, label, None, f"Launch error: {e}", False, True)
            return

        with self._lock:
            self._procs[label] = proc

        self._frame.after(0, self._update_slot, label, 0, "Connecting…")

        last_pct  = -1
        last_move = time.time()
        timeout_s = self.cfg.get("auto_connect_timeout", 180)
        connected = False
        started   = False

        for line in iter(proc.stdout.readline, ''):
            if not self._running:
                break
            ev = self._slot_enabled.get(label)
            if ev and not ev.get():
                break
            if label not in self._slot_logs:
                self._slot_logs[label] = []
            self._slot_logs[label].append(line.rstrip())
            if len(self._slot_logs[label]) > 500:
                self._slot_logs[label] = self._slot_logs[label][-500:]

            m = re.search(r'Bootstrapped (\d+)%', line)
            if m:
                pct = int(m.group(1))
                started = True
                self._frame.after(0, self._update_slot, label, pct, f"Bootstrapped {pct}%")
                if pct != last_pct:
                    last_pct = pct
                    last_move = time.time()
                if pct == 100 and not connected:
                    connected = True
                    self._frame.after(0, self._update_slot, label, 100, "✔ Connected!", True, False)
                    stop_ev = threading.Event()
                    self._stop_events[label] = stop_ev
                    threading.Thread(target=self._health_loop,
                                     args=(label, socks_port, stop_ev),
                                     daemon=True).start()
                    self._frame.after(0, self._info_lbl.configure,
                                      {"text": f"✔ {label} connected", "fg": C["GRN"]})
                    self._frame.after(0, self._port_info_lbl.configure,
                                      {"text": f"SOCKS {socks_port}  ·  HTTP {http_port}"})
                    if self.on_connected:
                        self._frame.after(0, self.on_connected,
                                          label, socks_port, ctrl_port, http_port)
            if started and last_pct >= 0 and not connected and time.time() - last_move > timeout_s:
                self._frame.after(0, self._update_slot, label,
                                  None, f"⚠ Timeout at {last_pct}%", False, True)
                break

        try: proc.stdout.close()
        except: pass

        if not connected:
            if not self._running:
                return
            if retry_count >= max_retries:
                self._frame.after(0, self._update_slot, label,
                                  None, f"❌ Failed after {max_retries} retries", False, True)
                return
            delay = min(90 + retry_count * 30, 180)
            self._frame.after(0, self._update_slot, label,
                              None, f"↺ Retry {retry_count+1}/{max_retries} in {delay}s…")
            for remaining in range(delay, 0, -10):
                if not self._running:
                    return
                time.sleep(min(10, remaining))
                if not self._running:
                    return
            if self._running:
                self._run_slot(label, socks_port, ctrl_port, http_port,
                               no_bridge, source, cat, trans, ip,
                               retry_count + 1, max_retries)
        else:
            try: proc.wait()
            except: pass
            if self._running:
                w = self._slot_widgets.get(label)
                if w:
                    self._frame.after(0, w["health_lbl"].configure,
                                      {"text": "⬤ Offline", "fg": C["RED"]})
                self._frame.after(0, self._update_slot, label, 0, "Died — restarting in 120s…", False, True)
                for remaining in range(120, 0, -10):
                    if not self._running:
                        return
                    time.sleep(min(10, remaining))
                if self._running:
                    self._run_slot(label, socks_port, ctrl_port, http_port,
                                   no_bridge, source, cat, trans, ip, 0, max_retries)

    def _retry_slot(self, label, socks_port, ctrl_port, http_port,
                    no_bridge, source, cat, trans, ip):
        ev = self._stop_events.pop(label, None)
        if ev: ev.set()
        with self._lock:
            old = self._procs.get(label)
            if old:
                try: old.terminate()
                except: pass
        self._frame.after(0, self._update_slot, label, 0, "Retrying…")
        threading.Thread(target=self._run_slot,
                         args=(label, socks_port, ctrl_port, http_port,
                               no_bridge, source, cat, trans, ip),
                         daemon=True).start()

    def _start_all(self):
        if self._running:
            return
        self._running = True
        self._start_btn.configure(state='disabled')
        if self._on_status_change:
            try: self._on_status_change(True)
            except: pass
        for label, wgt in self._slot_widgets.items():
            wgt["prog_var"].set(0)
            wgt["pct_lbl"].configure(text="0%")
            wgt["status_lbl"].configure(text="Waiting…", fg=C["FG2"])
            wgt["health_lbl"].configure(text="⬤ —", fg=C["FG2"])
            wgt["bar"].configure(style='Horizontal.TProgressbar')
        for i, sdef in enumerate(self._slot_defs):
            label, source, cat, trans, ip, no_bridge = sdef
            socks, ctrl, http = self._slot_ports(i)
            ev = self._slot_enabled.get(label, tk.BooleanVar(value=True))
            if ev.get():
                threading.Thread(target=self._run_slot,
                                 args=(label, socks, ctrl, http,
                                       no_bridge, source, cat, trans, ip),
                                 daemon=True).start()

    def _stop_all(self):
        self._running = False
        for ev in self._stop_events.values():
            try: ev.set()
            except: pass
        self._stop_events.clear()
        with self._lock:
            for lbl, p in self._procs.items():
                try:
                    p.terminate()
                    p.wait(timeout=3)
                except: pass
        self._procs.clear()
        self._slot_health.clear()
        self._slot_ping_history.clear()
        self._slot_state.clear()
        if self._proxy_stop_ev:
            self._proxy_stop_ev.set()
            self._proxy_stop_ev = None
        if self._active_proxy_label:
            self._disable_system_proxy()
            self._active_proxy_label = None
        else:
            self._disable_system_proxy()
        self._cleanup_data_dirs()
        self._start_btn.configure(state='normal')
        for label, wgt in self._slot_widgets.items():
            wgt["status_lbl"].configure(text="Stopped", fg=C["FG2"])
            wgt["health_lbl"].configure(text="⬤ —", fg=C["FG2"])
            wgt["prog_var"].set(0)
            wgt["pct_lbl"].configure(text="0%")
            wgt["proxy_btn"].configure(text="🌐 Set Proxy", bg=C["BTN"], fg=C["FG"])
        self._update_proxy_status_bar(None, None, None)
        if self._on_status_change:
            try: self._on_status_change(False)
            except: pass

    def _cleanup_data_dirs(self):
        try:
            for entry in os.listdir(self.extract_dir):
                if entry.startswith("data_par_"):
                    full = os.path.join(self.extract_dir, entry)
                    if os.path.isdir(full):
                        try:
                            shutil.rmtree(full)
                        except Exception:
                            pass
        except Exception:
            pass

class DeltaTorGUI:
    def __init__(self, root):
        self.root = root
        self.root.title("Delta Tor 1.2.2")

        try:
            self.root.attributes("-alpha", 0.0)
        except Exception:
            pass

        self.root.configure(bg=C["BG"], bd=0, relief='flat')
        self.root.geometry("800x980")
        set_window_icon(self.root)

        self.setup_theme()

        self.cfg = load_config()
        self.extract_dir   = BASE_DIR
        self.archive_name  = os.path.join(BASE_DIR, "tor-expert-bundle.tar.gz")
        self.bridges_dir   = os.path.join(BASE_DIR, "bridges")
        self.logs_dir      = os.path.join(BASE_DIR, "logs")

        os.makedirs(self.bridges_dir, exist_ok=True)
        os.makedirs(self.logs_dir,    exist_ok=True)

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

        self.status_var         = tk.StringVar(value="Initializing…")
        self.proxy_var          = tk.BooleanVar()
        self.source_var         = tk.StringVar(value="Delta-Kronecker Tor-Bridges-Collector")
        self.cat_var            = tk.StringVar(value="Tested & Active")
        self.trans_var          = tk.StringVar()
        self.ip_var             = tk.StringVar(value="IPv4")
        self.no_bridge_var      = tk.BooleanVar(value=False)
        self.conn_progress_var  = tk.IntVar(value=0)
        self.conn_pct_var       = tk.StringVar(value="0%")
        self.stat_ip_var        = tk.StringVar(value="—")
        self.stat_country_var   = tk.StringVar(value="—")
        self.stat_uptime_var    = tk.StringVar(value="—")
        self.stat_tor_var       = tk.StringVar(value="—")
        self._dl_bar_var        = tk.IntVar(value=0)
        self.bridge_count_var   = tk.StringVar(value="")
        self.bridge_updated_var = tk.StringVar(value="")

        self.setup_ui()

        self.root.update_idletasks()
        self.root.protocol("WM_DELETE_WINDOW", self._on_close_btn)
        self.root._app = self

        self.root.after(1000, lambda: self.root.attributes("-alpha", 1.0))

        threading.Thread(target=self.auto_initialize, daemon=True).start()

    def _on_close_btn(self):
        self.root.withdraw()
        if not getattr(self, '_tray_running', False):
            self._tray_running = True
            threading.Thread(target=self._tray_icon_loop, daemon=True).start()

    def _show_from_tray(self):
        """Show main window and allow tray to be re-created next time."""
        self._tray_running = False
        # Remove tray icon immediately so it doesn't linger or duplicate
        nid = getattr(self, '_tray_nid', None)
        if nid is not None:
            try:
                ctypes.windll.shell32.Shell_NotifyIconW(0x00000002, ctypes.byref(nid))
            except Exception:
                pass
            self._tray_nid = None
        hwnd = getattr(self, '_tray_hwnd', 0)
        self._tray_hwnd = 0
        if hwnd:
            try:
                ctypes.windll.user32.DestroyWindow(hwnd)
            except Exception:
                pass
        self.root.deiconify()
        self.root.lift()
        self.root.focus_force()

    def _tray_icon_loop(self):
        NIM_ADD     = 0x00000000
        NIM_DELETE  = 0x00000002
        NIF_ICON    = 0x00000002
        NIF_TIP     = 0x00000004
        NIF_MESSAGE = 0x00000001
        TRAY_MSG    = 0x0400 + 20
        ID_SHOW     = 1001
        ID_QUIT     = 1002

        user32  = ctypes.windll.user32
        shell32 = ctypes.windll.shell32

        WNDPROCTYPE = ctypes.WINFUNCTYPE(
            ctypes.c_long, ctypes.wintypes.HWND,
            ctypes.wintypes.UINT, ctypes.wintypes.WPARAM, ctypes.wintypes.LPARAM)

        def wnd_proc(hwnd, msg, wparam, lparam):
            if msg == TRAY_MSG:
                if lparam in (0x0202, 0x0203):
                    self.root.after(0, self._show_from_tray)
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
                        self.root.after(0, self._show_from_tray)
                    elif cmd == ID_QUIT:
                        self.root.after(0, lambda: (self.stop_tor(), self.root.destroy()))
            elif msg == 0x0002:  # WM_DESTROY
                user32.PostQuitMessage(0)
                return 0
            return user32.DefWindowProcW(hwnd, msg, wparam, lparam)

        # Store as instance vars to prevent GC
        self._wnd_proc_ptr = WNDPROCTYPE(wnd_proc)

        class WNDCLASS(ctypes.Structure):
            _fields_ = [
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
            ]

        # class name kept alive via self._tray_class_name
        import uuid as _uuid2
        class_name = f"DeltaTorTray_{_uuid2.uuid4().hex[:8]}"

        hInstance = ctypes.windll.kernel32.GetModuleHandleW(None)
        wc = WNDCLASS()
        wc.lpfnWndProc   = self._wnd_proc_ptr
        wc.hInstance     = hInstance
        wc.lpszClassName = class_name
        self._wnd_class  = wc

        if not user32.RegisterClassW(ctypes.byref(wc)):
            self._tray_running = False
            self.root.after(0, self.root.deiconify)
            return

        hwnd = user32.CreateWindowExW(
            0, class_name, class_name,
            0, 0, 0, 0, 0, None, None, hInstance, None)
        if not hwnd:
            user32.UnregisterClassW(class_name, hInstance)
            self._tray_running = False
            self.root.after(0, self.root.deiconify)
            return
        self._tray_class_name = class_name  # keep reference

        self._tray_hwnd = hwnd

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

        nid = NOTIFYICONDATA()
        nid.cbSize           = ctypes.sizeof(NOTIFYICONDATA)
        nid.hWnd             = hwnd
        nid.uID              = 1
        nid.uFlags           = NIF_ICON | NIF_TIP | NIF_MESSAGE
        nid.uCallbackMessage = TRAY_MSG
        nid.hIcon            = _load_tray_icon()
        nid.szTip            = "Delta Tor"
        shell32.Shell_NotifyIconW(NIM_ADD, ctypes.byref(nid))
        self._tray_nid       = nid   # keep ref for early delete

        msg_buf = ctypes.wintypes.MSG()
        while user32.GetMessageW(ctypes.byref(msg_buf), None, 0, 0) != 0:
            user32.TranslateMessage(ctypes.byref(msg_buf))
            user32.DispatchMessageW(ctypes.byref(msg_buf))

        shell32.Shell_NotifyIconW(NIM_DELETE, ctypes.byref(nid))
        self._tray_nid = None
        user32.UnregisterClassW(self._tray_class_name, hInstance)
        self._tray_running = False

    def _notify(self, title: str, msg: str):
        threading.Thread(
            target=_win_notify, args=(title, msg, self._tray_hwnd),
            daemon=True).start()

    def open_github_project(self):
        webbrowser.open("https://github.com/Delta-Kronecker/Tor-Windows")

    def _show_donate_window(self):
        WALLET = "0x2a434FF74737be5B94634040D010a458507b0741"
        w = tk.Toplevel(self.root)
        w.title("Donate — Delta Tor")
        w.geometry("460x240")
        w.configure(bg=C["BG"])
        w.resizable(False, False)
        w.update()
        apply_dark_titlebar(w)
        set_window_icon(w)
        self._apply_icon_to(w)

        tk.Frame(w, bg=C["YLW"], height=3).pack(fill='x')
        tk.Label(w, text="💎  Support the Project",
                 font=('Segoe UI', 13, 'bold'), bg=C["BG"], fg=C["YLW"]).pack(pady=(14, 4))
        tk.Label(w, text="USDT BEP20 (BNB Smart Chain):",
                 font=('Segoe UI', 10), bg=C["BG"], fg=C["FG2"]).pack(pady=(0, 4))

        addr_frame = tk.Frame(w, bg=C["BTN"], bd=0)
        addr_frame.pack(fill='x', padx=30, pady=(0, 6))
        addr_lbl = tk.Label(addr_frame, text=WALLET,
                            font=('Consolas', 10), bg=C["BTN"], fg=C["CYAN"],
                            padx=10, pady=8, wraplength=380, justify='center')
        addr_lbl.pack(fill='x')

        copied_var = tk.StringVar(value="📋  Copy Address")

        def _copy():
            self.root.clipboard_clear()
            self.root.clipboard_append(WALLET)
            copied_var.set("✔  Copied!")
            w.after(2000, lambda: copied_var.set("📋  Copy Address"))

        tk.Button(w, textvariable=copied_var, command=_copy,
                  bg=C["ACC"], fg="white", font=('Segoe UI', 10, 'bold'),
                  relief="flat", cursor="hand2",
                  activebackground=C["ACC2"]
                  ).pack(pady=(0, 6), padx=80, fill='x', ipady=5)
        tk.Label(w, text="⚠  BEP20 network only — send only USDT on BNB Smart Chain",
                 font=('Segoe UI', 9), bg=C["BG"], fg=C["YLW"]).pack(pady=(0, 4))
        tk.Button(w, text="Close", command=w.destroy,
                  bg=C["BTN"], fg=C["FG2"], font=('Segoe UI', 9),
                  relief="flat", cursor="hand2",
                  activebackground=C["BTN2"]
                  ).pack(pady=(0, 12), padx=140, fill='x', ipady=4)

    def setup_theme(self):
        s = ttk.Style()
        s.theme_use('clam')
        s.configure('.', background=C["BG"], foreground=C["FG"], font=('Segoe UI', 10))
        s.configure('TLabel',      background=C["BG"], foreground=C["FG"], font=('Segoe UI', 10))
        s.configure('TLabelframe', background=C["BG"], foreground=C["ACC"],
                    bordercolor=C["BORDER"])
        s.configure('TLabelframe.Label', background=C["BG"], foreground=C["ACC"],
                    font=('Segoe UI', 11, 'bold'))
        s.configure('TCombobox',
                    fieldbackground=C["BTN"],
                    background=C["BTN"],
                    foreground=C["FG"],
                    borderwidth=1,
                    relief='flat',
                    arrowcolor=C["FG2"],
                    selectbackground=C["BTN"],
                    selectforeground=C["FG"],
                    insertcolor=C["FG"],
                    padding=(4, 4))
        s.map('TCombobox',
              fieldbackground=[('readonly', C["BTN"]), ('disabled', C["PANEL"])],
              foreground=[('readonly', C["FG"]), ('disabled', C["FG2"])],
              background=[('readonly', C["BTN"])],
              selectbackground=[('readonly', C["BTN"])],
              arrowcolor=[('readonly', C["FG2"]), ('disabled', C["FG2"])])
        s.configure('TScrollbar',
                    background=C["BTN2"],
                    troughcolor=C["PANEL"],
                    bordercolor=C["PANEL"],
                    arrowcolor=C["FG2"],
                    relief='flat')
        s.map('TScrollbar',
              background=[('active', C["ACC"]), ('pressed', C["ACC2"])])
        s.configure('TCheckbutton',
                    background=C["PANEL"],
                    foreground=C["FG"],
                    font=('Segoe UI', 10),
                    indicatorcolor=C["BTN2"],
                    indicatorrelief='flat')
        s.map('TCheckbutton',
              background=[('active', C["PANEL"])],
              indicatorcolor=[('selected', C["ACC"]), ('!selected', C["BTN2"])])
        s.configure('Horizontal.TProgressbar',
                    background=C["ACC"],
                    troughcolor=C["BTN"],
                    bordercolor=C["PANEL"],
                    lightcolor=C["ACC2"],
                    darkcolor=C["ACC"])
        s.configure('Won.Horizontal.TProgressbar',
                    background=C["GRN"],
                    troughcolor=C["BTN"])
        s.configure('Stat.TLabel',    background=C["CARD"], foreground=C["FG2"],
                    font=('Segoe UI', 10))
        s.configure('StatVal.TLabel', background=C["CARD"], foreground=C["GRN"],
                    font=('Segoe UI', 10, 'bold'))
        self.root.option_add('*Listbox.background',       C["BTN"])
        self.root.option_add('*Listbox.foreground',       C["FG"])
        self.root.option_add('*Listbox.selectBackground', C["ACC"])
        self.root.option_add('*Listbox.selectForeground', C["FG"])
        self.root.option_add('*Listbox.borderWidth',      '0')
        self.root.option_add('*Listbox.relief',           'flat')
        self.root.option_add('*TCombobox*Listbox.background',       C["BTN"])
        self.root.option_add('*TCombobox*Listbox.foreground',       C["FG"])
        self.root.option_add('*TCombobox*Listbox.selectBackground', C["ACC"])
        self.root.option_add('*TCombobox*Listbox.selectForeground', C["FG"])
        self.root.option_add('*TCombobox*Listbox.borderWidth',      '0')
        self.root.option_add('*TCombobox*Listbox.relief',           'flat')
        self.root.option_add('*Toplevel.background',  C["PANEL"])
        self.root.option_add('*Frame.background',     C["BG"])
        s.configure('Treeview',
                    background=C["PANEL"],
                    foreground=C["FG"],
                    fieldbackground=C["PANEL"],
                    rowheight=26,
                    font=('Segoe UI', 10))
        s.configure('Treeview.Heading',
                    background=C["BTN"],
                    foreground=C["FG"],
                    font=('Segoe UI', 10, 'bold'),
                    relief='flat')
        s.map('Treeview',
              background=[('selected', C["ACC"])],
              foreground=[('selected', C["FG"])])

    def setup_ui(self):
        BG = C["BG"]

        tk.Frame(self.root, bg=C["ACC"], height=3).pack(fill='x')

        nav = tk.Frame(self.root, bg=C["PANEL"], height=46)
        nav.pack(fill='x')
        nav.pack_propagate(False)

        tk.Label(nav, text="Delta Tor",
                 font=('Segoe UI', 16, 'bold'), bg=C["PANEL"], fg=C["ACC"]).pack(
                 side='left', padx=18)

        for txt, cmd in [
            ("Help",     self.show_help_window),
            ("Settings", self.show_settings_window),
            ("Data Folder", self._change_data_folder),
        ]:
            tk.Button(nav, text=txt, command=cmd,
                      bg=C["PANEL"], fg=C["FG2"],
                      font=('Segoe UI', 10), relief="flat", cursor="hand2",
                      activebackground=C["BTN"],
                      activeforeground=C["FG"],
                      bd=0, padx=12
                      ).pack(side='right', fill='y')

        for txt9, cmd9 in [
            ("GitHub",   self.open_github_project),
            ("Telegram", lambda: webbrowser.open("https://t.me/DeltaKroneckerGithub")),
        ]:
            tk.Button(nav, text=txt9, command=cmd9,
                      bg=C["PANEL"], fg=C["FG2"],
                      font=('Segoe UI', 10), relief="flat", cursor="hand2",
                      activebackground=C["BTN"],
                      activeforeground=C["FG"],
                      bd=0, padx=10
                      ).pack(side='right', fill='y')

        tk.Button(nav, text="💎Support the Project  ",
                  command=self._show_donate_window,
                  bg=C["PANEL"], fg=C["YLW"],
                  font=('Segoe UI', 10, 'bold'), relief="flat", cursor="hand2",
                  activebackground=C["BTN"],
                  activeforeground=C["YLW"],
                  bd=0, padx=10
                  ).pack(side='right', fill='y')

        status_bar = tk.Frame(self.root, bg=C["CARD"], height=32)
        status_bar.pack(fill='x')
        status_bar.pack_propagate(False)
        self._status_dot = tk.Label(status_bar, text="●",
                                    font=('Segoe UI', 10), bg=C["CARD"], fg=C["RED"])
        self._status_dot.pack(side='left', padx=(16, 4))
        tk.Label(status_bar, textvariable=self.status_var,
                 font=('Segoe UI', 9), bg=C["CARD"], fg=C["FG2"]).pack(side='left')
        self._proxy_status_lbl = tk.Label(status_bar, text="",
                 font=('Consolas', 9), bg=C["CARD"], fg=C["GRN"])
        self._proxy_status_lbl.pack(side='right', padx=(0, 16))

        self._dl_outer = tk.Frame(self.root, bg=C["BG"])

        main = tk.Frame(self.root, bg=BG)
        main.pack(fill='both', expand=True, padx=0, pady=0)

        self._normal_frame = tk.Frame(main, bg=BG)
        self._normal_frame.pack(fill='both', expand=True, padx=0)
        self._multi_frame  = tk.Frame(main, bg=BG)

        left = self._normal_frame

        cfg_card = tk.Frame(left, bg=C["PANEL"], bd=0)
        cfg_card.pack(fill='x', padx=0, pady=(0, 6))
        tk.Frame(cfg_card, bg=C["ACC"], height=2).pack(fill='x')

        cfg_inner = tk.Frame(cfg_card, bg=C["PANEL"])
        cfg_inner.pack(fill='x', padx=14, pady=10)

        ttl_row = tk.Frame(cfg_inner, bg=C["PANEL"])
        ttl_row.pack(fill='x', pady=(0, 8))
        tk.Label(ttl_row, text="Bridge Configuration",
                 font=('Segoe UI', 10, 'bold'), bg=C["PANEL"], fg=C["FG"]).pack(side='left')
        self.update_btn = tk.Button(ttl_row, text="↺ Update Bridges",
                                    command=self.start_download_bridges,
                                    bg=C["BTN"], fg=C["CYAN"],
                                    font=('Segoe UI', 10), relief="flat", cursor="hand2",
                                    activebackground=C["BTN2"])
        self.update_btn.pack(side='right')

        OM_CFG   = dict(bg=C["BTN"], fg=C["FG"], activebackground=C["BTN2"],
                        activeforeground=C["FG"], highlightthickness=0,
                        relief="flat", font=('Segoe UI', 10), anchor='w')
        MENU_CFG = dict(bg=C["BTN"], fg=C["FG"], activebackground=C["ACC"],
                        activeforeground=C["FG"], borderwidth=0, font=('Segoe UI', 10))

        def _opt_row(parent, text, var, options, command=None):
            row = tk.Frame(parent, bg=C["PANEL"])
            row.pack(fill='x', pady=3)
            tk.Label(row, text=text, width=13, anchor='w',
                     bg=C["PANEL"], fg=C["FG2"],
                     font=('Segoe UI', 10)).pack(side='left')
            om = tk.OptionMenu(row, var, *options)
            om.configure(**OM_CFG)
            om["menu"].configure(**MENU_CFG)
            om.pack(side='left', fill='x', expand=True, ipady=3)
            if command:
                var.trace_add("write", lambda *_: command())
            return om

        _opt_row(cfg_inner, "Source:", self.source_var,
                 ["Default (Built-in)", "Delta-Kronecker Tor-Bridges-Collector", "Custom Bridges"],
                 command=self.on_source_changed)

        self.cat_row = tk.Frame(cfg_inner, bg=C["PANEL"])
        self.cat_row.pack(fill='x', pady=3)
        tk.Label(self.cat_row, text="Category:", width=13, anchor='w',
                 bg=C["PANEL"], fg=C["FG2"],
                 font=('Segoe UI', 10)).pack(side='left')
        self.cat_combo = tk.OptionMenu(self.cat_row, self.cat_var,
                                       "Tested & Active", "Fresh (72h)", "Full Archive")
        self.cat_combo.configure(**OM_CFG)
        self.cat_combo["menu"].configure(**MENU_CFG)
        self.cat_combo.pack(side='left', fill='x', expand=True, ipady=3)
        self.cat_var.trace_add("write", lambda *_: self._on_bridge_selection_change())

        self.trans_combo = _opt_row(cfg_inner, "Transport:", self.trans_var,
                                    ["obfs4"],
                                    command=self._on_bridge_selection_change)

        self.ip_combo = _opt_row(cfg_inner, "IP Version:", self.ip_var,
                                 ["Both", "IPv4", "IPv6"],
                                 command=self._on_bridge_selection_change)

        nb_row = tk.Frame(cfg_inner, bg=C["PANEL"])
        nb_row.pack(fill='x', pady=3)
        ttk.Checkbutton(nb_row, text="Connect without bridge  (direct Tor)",
                        variable=self.no_bridge_var, style='TCheckbutton',
                        command=self._on_no_bridge_toggle).pack(side='left')

        info_row = tk.Frame(cfg_inner, bg=C["PANEL"])
        info_row.pack(fill='x', pady=(4, 0))
        tk.Label(info_row, text="Available:", bg=C["PANEL"], fg=C["FG2"],
                 font=('Segoe UI', 8)).pack(side='left')
        tk.Label(info_row, textvariable=self.bridge_count_var,
                 bg=C["PANEL"], fg=C["FG"],
                 font=('Segoe UI', 8, 'bold')).pack(side='left', padx=(4, 16))
        tk.Label(info_row, text="Updated:", bg=C["PANEL"], fg=C["FG2"],
                 font=('Segoe UI', 8)).pack(side='left')
        tk.Label(info_row, textvariable=self.bridge_updated_var,
                 bg=C["PANEL"], fg=C["FG2"],
                 font=('Segoe UI', 8)).pack(side='left', padx=4)

        btn_card = tk.Frame(left, bg=C["PANEL"], bd=0)
        btn_card.pack(fill='x', padx=0, pady=(0, 6))
        tk.Frame(btn_card, bg=C["ACC"], height=2).pack(fill='x')
        btn_inner = tk.Frame(btn_card, bg=C["PANEL"])
        btn_inner.pack(fill='x', padx=14, pady=10)

        _BTN_BG  = "#1E2535"
        _BTN_HOV = "#273048"

        r0 = tk.Frame(btn_inner, bg=C["PANEL"])
        r0.pack(fill='x', pady=(0, 6))

        self.multi_btn = tk.Button(
            r0,
            text="🔗  Multi-Connect  —  Recommended",
            command=lambda: self._switch_to_mode("multi"),
            bg="#1A3A5C", fg="#7DC8F7",
            activebackground="#1F4878", activeforeground="#A8DCFF",
            font=('Segoe UI', 11, 'bold'),
            relief="flat", cursor="hand2", bd=0,
        )
        self.multi_btn.pack(fill='x', ipady=10)
        def _multi_enter(e):
            if not getattr(self, '_multi_active', False):
                self.multi_btn.configure(bg="#1F4878", fg="#A8DCFF")
        def _multi_leave(e):
            if not getattr(self, '_multi_active', False):
                self.multi_btn.configure(bg="#1A3A5C", fg="#7DC8F7")
        self.multi_btn.bind("<Enter>", _multi_enter)
        self.multi_btn.bind("<Leave>", _multi_leave)

        grid6 = tk.Frame(btn_inner, bg=C["PANEL"])
        grid6.pack(fill='x', pady=(0, 6))
        grid6.columnconfigure(0, weight=1, uniform="btn6")
        grid6.columnconfigure(1, weight=1, uniform="btn6")
        grid6.columnconfigure(2, weight=1, uniform="btn6")

        _b = dict(font=('Segoe UI', 10, 'bold'), relief="flat",
                  cursor="hand2", bd=0,
                  bg=_BTN_BG, fg=C["FG"],
                  activebackground=_BTN_HOV, activeforeground=C["FG"])

        self.auto_btn = tk.Button(grid6, text=" Auto",
                                  command=self.start_auto_connect,
                                  bg=_BTN_BG, fg=C["FG"],
                                  activebackground=_BTN_HOV, activeforeground=C["FG"],
                                  font=('Segoe UI', 10, 'bold'), relief="flat",
                                  cursor="hand2", bd=0)
        self.auto_btn.grid(row=0, column=0, padx=(0, 2), pady=(0, 4), sticky="ew", ipady=7)

        self.start_btn = tk.Button(grid6, text=" Start",
                                   command=self.start_tor_thread, **_b)
        self.start_btn.grid(row=0, column=1, padx=2, pady=(0, 4), sticky="ew", ipady=7)

        self.stop_btn = tk.Button(grid6, text=" Stop",
                                  command=self.stop_tor,
                                  bg=_BTN_BG, fg=C["FG"],
                                  activebackground=_BTN_HOV, activeforeground=C["FG"],
                                  font=('Segoe UI', 10, 'bold'), relief="flat",
                                  cursor="hand2", bd=0)
        self.stop_btn.grid(row=0, column=2, padx=(2, 0), pady=(0, 4), sticky="ew", ipady=7)

        tk.Button(grid6, text=" Bridge Scanner",
                  command=self.show_bridge_scanner, **_b
                  ).grid(row=1, column=0, padx=(0, 2), sticky="ew", ipady=7)

        self.test_btn_top = tk.Button(grid6, text=" Test Connection",
                                      command=self.start_test_connection, **_b)
        self.test_btn_top.grid(row=1, column=1, padx=2, sticky="ew", ipady=7)

        self.newnym_btn = tk.Button(grid6, text=" New Circuit",
                                    command=self.request_new_circuit, **_b)
        self.newnym_btn.grid(row=1, column=2, padx=(2, 0), sticky="ew", ipady=7)

        r4 = tk.Frame(btn_inner, bg=C["PANEL"])
        r4.pack(fill='x', pady=(6, 0))

        self.proxy_btn = tk.Button(r4,
                                   text="  System Proxy :  OFF",
                                   command=self.toggle_proxy_button,
                                   bg=_BTN_BG, fg=C["FG2"],
                                   font=('Segoe UI', 10, 'bold'),
                                   relief="flat", cursor="hand2",
                                   activebackground=_BTN_HOV)
        self.proxy_btn.pack(fill='x', ipady=7)

        status_inline = tk.Frame(left, bg=C["CARD"])
        status_inline.pack(fill='x', padx=0, pady=(0, 6))
        tk.Frame(status_inline, bg=C["ACC"], height=2).pack(fill='x')
        _si = tk.Frame(status_inline, bg=C["CARD"])
        _si.pack(fill='x', padx=14, pady=6)
        tk.Label(_si, text="Progress:",
                 font=('Segoe UI', 9), bg=C["CARD"], fg=C["FG2"]).pack(side='left')
        tk.Label(_si, textvariable=self.conn_pct_var,
                 font=('Segoe UI', 9, 'bold'), bg=C["CARD"], fg=C["FG"]).pack(side='left', padx=(4,0))
        ttk.Progressbar(_si, variable=self.conn_progress_var,
                        maximum=100, mode='determinate'
                        ).pack(side='left', fill='x', expand=True, padx=(10, 0))

        stats_card = tk.Frame(left, bg=C["PANEL"])
        stats_card.pack(fill='x', padx=0, pady=(0, 6))
        tk.Frame(stats_card, bg=C["ACC"], height=2).pack(fill='x')
        stats_inner = tk.Frame(stats_card, bg=C["CARD"])
        stats_inner.pack(fill='x', padx=0, pady=0)

        sg = tk.Frame(stats_inner, bg=C["CARD"])
        sg.pack(fill='x', padx=10, pady=8)
        sg.columnconfigure(1, weight=1)
        sg.columnconfigure(3, weight=1)

        def _sl(t, r, c):
            ttk.Label(sg, text=t, style='Stat.TLabel').grid(
                row=r, column=c, padx=(8, 3), pady=4, sticky="w")
        def _sv(var, r, c):
            ttk.Label(sg, textvariable=var, style='StatVal.TLabel').grid(
                row=r, column=c, padx=(0, 8), pady=4, sticky="w")

        _sl("Exit IP:",    0, 0); _sv(self.stat_ip_var,      0, 1)
        _sl("Country:",    0, 2); _sv(self.stat_country_var,  0, 3)
        _sl("Uptime:",     1, 0); _sv(self.stat_uptime_var,   1, 1)
        _sl("Status:",     1, 2); _sv(self.stat_tor_var,      1, 3)

        stat_btns = tk.Frame(stats_card, bg=C["PANEL"])
        stat_btns.pack(fill='x', padx=14, pady=(4, 10))

        self.save_log_btn = tk.Button(stat_btns, text="💾 Save Log",
                                      command=self.save_log_to_file,
                                      bg=C["BTN"], fg=C["FG"],
                                      font=('Segoe UI', 9, 'bold'),
                                      relief="flat", cursor="hand2",
                                      activebackground=C["BTN2"])
        self.save_log_btn.pack(fill='x', ipady=4)
        self.test_btn = self.test_btn_top

        log_card = tk.Frame(left, bg=C["PANEL"])
        log_card.pack(fill='both', expand=True, padx=0, pady=(0, 6))
        tk.Frame(log_card, bg=C["ACC"], height=2).pack(fill='x')
        log_header = tk.Frame(log_card, bg=C["PANEL"])
        log_header.pack(fill='x', padx=14, pady=(6, 4))
        tk.Label(log_header, text="Tor Logs",
                 font=('Segoe UI', 9, 'bold'), bg=C["PANEL"], fg=C["FG"]).pack(side='left')

        log_frame = tk.Frame(log_card, bg=C["PANEL"])
        log_frame.pack(fill='both', expand=True, padx=0, pady=0)

        self.log_text = tk.Text(log_frame, font=('Consolas', 9), wrap='word',
                                state='disabled', bg=C["PANEL"], fg=C["FG2"],
                                bd=0, padx=10, pady=8)
        self.log_text.tag_configure("warn",   foreground=C["YLW"])
        self.log_text.tag_configure("err",    foreground=C["RED"])
        self.log_text.tag_configure("notice", foreground=C["GRN"])
        self.log_text.tag_configure("info",   foreground=C["FG2"])
        self.log_text.tag_configure("auto",   foreground=C["CYAN"])
        self.log_text.tag_configure("test",   foreground=C["ORG"])
        sb_log = ttk.Scrollbar(log_frame, command=self.log_text.yview)
        self.log_text.configure(yscrollcommand=sb_log.set)
        self.log_text.pack(side='left', fill='both', expand=True)
        sb_log.pack(side='right', fill='y')

        self.update_transports()
        self.on_source_changed()

    def _on_no_bridge_toggle(self):
        nb = self.no_bridge_var.get()
        state = 'disabled' if nb else 'readonly'
        try:
            self.cat_combo.configure(state=state)
        except Exception:
            pass
        self.trans_combo.configure(state=state)
        self.ip_combo.configure(state=state)

    def show_custom_bridge_window(self):
        def _on_save(new_cfg):
            self.cfg.update(new_cfg)
            save_config(self.cfg, self.extract_dir)
        CustomBridgeWindow(self.root, self.cfg, _on_save)

    def show_bridge_scanner(self):
        BridgeScannerWindow(self.root, self.bridges_dir, self.get_safe_filename)

    def _change_data_folder(self):
        from tkinter import filedialog, messagebox
        new_dir = filedialog.askdirectory(
            title="Choose New Data Directory",
            initialdir=BASE_DIR,
            parent=self.root)
        if not new_dir:
            return
        new_dir = os.path.normpath(new_dir)
        if os.path.normpath(new_dir) == os.path.normpath(BASE_DIR):
            return
        ans = messagebox.askyesno(
            "Move Data?",
            f"Move all data to:\n{new_dir}\n\nThe app will restart to apply the change.",
            parent=self.root)
        if not ans:
            return
        self.stop_tor()
        _migrate_existing_files(BASE_DIR, new_dir)
        _save_data_dir(new_dir)
        try:
            subprocess.Popen([sys.executable] + sys.argv)
        except Exception:
            pass
        self.root.destroy()

    def _set_multi_active(self, active: bool):
        self._multi_active = active
        if active:
            self.multi_btn.configure(
                bg="#1A5C2E", fg="#7DF7A8",
                activebackground="#1F7838", activeforeground="#A8FFD0")
        else:
            self.multi_btn.configure(
                bg="#1A3A5C", fg="#7DC8F7",
                activebackground="#1F4878", activeforeground="#A8DCFF")

    def show_parallel_connect(self):
        self._switch_to_mode("multi")

    def _switch_to_mode(self, mode):
        if mode == "multi":
            self._normal_frame.pack_forget()
            self._multi_frame.pack(fill='both', expand=True, padx=0)
            if not hasattr(self, '_parallel_win') or self._parallel_win is None:
                def _on_connected(label, socks_port, ctrl_port, http_port):
                    self.append_log(
                        f"[Multi] Connected: {label}  SOCKS:{socks_port}  HTTP:{http_port}\n",
                        "auto")
                self._parallel_win = ParallelConnectWindow(
                    self._multi_frame,
                    self.extract_dir, self.bridges_dir,
                    self.get_safe_filename, self.generate_torrc,
                    self.cfg, _on_connected,
                    append_log_fn=self.append_log,
                    on_status_change=self._set_multi_active)
        else:
            self._multi_frame.pack_forget()
            self._normal_frame.pack(fill='both', expand=True, padx=0)

    def show_settings_window(self):
        def _on_save(new_cfg):
            self.cfg.update(new_cfg)
            if self.tor_connected:
                self._restart_keepalive()
                self._restart_watchdog()
        SettingsWindow(self.root, self.cfg, _on_save, on_clear_data=self._clear_data_dir)

    def _show_dl(self, title="Downloading…"):
        self.root.after(0, self.append_log, f"[Bridges] {title}\n", "auto")

    def _set_dl(self, pct, title=None, speed=None):
        msg = title or f"Progress: {pct}%"
        if pct == 0 or pct == 100 or pct % 25 == 0:
            self.root.after(0, self.append_log, f"[Bridges] {msg} ({pct}%)\n", "auto")

    def _hide_dl(self, delay=900):
        pass

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
        self.status_var.set(msg)
        connected = self.tor_connected
        dot_color = C["FG"] if connected else (C["FG2"] if "connect" in msg.lower() else C["FG2"])
        try:
            self._status_dot.configure(fg=dot_color)
        except Exception:
            pass
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
                            {"text": "🔍 Test Connection", "state": "normal"})

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
                self._notify("Delta Tor", "New circuit obtained.")
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
            self._notify("Delta Tor", "Tor process died — restarting…")
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
                text="  System Proxy :  ON",
                bg="#0E2A1A", fg=C["GRN"],
                activebackground="#163A22", activeforeground=C["GRN"])
        else:
            self.proxy_btn.configure(
                text="  System Proxy :  OFF",
                bg=C["BTN"], fg=C["FG2"],
                activebackground=C["BTN2"], activeforeground=C["FG"])

    def set_system_proxy(self, enable):
        try:
            key = winreg.OpenKey(
                winreg.HKEY_CURRENT_USER,
                r'Software\Microsoft\Windows\CurrentVersion\Internet Settings',
                0, winreg.KEY_ALL_ACCESS)
            if enable:
                self._start_http_proxy()
                proxy_str = f'127.0.0.1:{HTTP_PROXY_PORT}'
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
        src = self.source_var.get()
        if src == "Delta-Kronecker Tor-Bridges-Collector":
            try: self.cat_row.pack(fill='x', pady=3)
            except Exception: pass
            self.update_transports()
        elif src == "Custom Bridges":
            try: self.cat_row.pack_forget()
            except Exception: pass
            opts = ["obfs4", "webtunnel", "vanilla"]
            self._set_om_options(self.trans_combo, self.trans_var, opts)
            self.show_custom_bridge_window()
        else:
            try: self.cat_row.pack_forget()
            except Exception: pass
            opts = ["obfs4", "snowflake", "meek"]
            self._set_om_options(self.trans_combo, self.trans_var, opts)
        self._refresh_bridge_info()

    def _set_om_options(self, om, var, opts):
        menu = om["menu"]
        menu.delete(0, "end")
        for opt in opts:
            menu.add_command(label=opt, command=lambda v=opt: var.set(v))
        if var.get() not in opts:
            var.set(opts[0] if opts else "")

    def update_transports(self, event=None):
        src = self.source_var.get()
        if src == "Default (Built-in)":
            opts = ["obfs4", "snowflake", "meek"]
        elif src == "Custom Bridges":
            opts = ["obfs4", "webtunnel", "vanilla"]
        else:
            opts = ["obfs4", "webtunnel", "vanilla"]
        self._set_om_options(self.trans_combo, self.trans_var, opts)

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
        if src == "Custom Bridges" or self.source_var.get() == "Custom Bridges":
            raw = self.cfg.get("custom_bridges", "").strip()
            if raw:
                limit = self.cfg.get("bridges_in_torrc", 100)
                do_shuffle = self.cfg.get("shuffle_bridges", True)
                lines = [l.strip() for l in raw.splitlines() if l.strip() and not l.startswith('#')]
                if do_shuffle:
                    import random as _random; _random.shuffle(lines)
                return [f"Bridge {line}\n" for line in lines[:limit]]
            return []
        bridge_lines = []
        limit      = self.cfg.get("bridges_in_torrc", 100)
        do_shuffle = self.cfg.get("shuffle_bridges", True)

        if self.cfg.get("use_custom_bridges", False):
            raw = self.cfg.get("custom_bridges", "").strip()
            if raw:
                lines = [l.strip() for l in raw.splitlines() if l.strip() and not l.startswith('#')]
                if do_shuffle:
                    random.shuffle(lines)
                for line in lines[:limit]:
                    bridge_lines.append(f"Bridge {line}\n")
                return bridge_lines

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

    def generate_torrc(self, cat_ov=None, trans_ov=None, ip_ov=None, src_ov=None,
                       no_bridge_ov=None):
        base     = os.path.abspath(self.extract_dir)
        data_dir = os.path.join(base, "data")
        tor_dir  = os.path.join(base, "tor")
        pt_dir   = os.path.join(tor_dir, "pluggable_transports")
        lyrebird = os.path.join(pt_dir, "lyrebird.exe")
        conjure  = os.path.join(pt_dir, "conjure-client.exe")
        torrc    = os.path.join(tor_dir, "torrc")
        os.makedirs(data_dir, exist_ok=True)

        no_bridge = (no_bridge_ov if no_bridge_ov is not None
                     else self.no_bridge_var.get())

        src   = src_ov   or self.source_var.get()
        cat   = cat_ov   or self.cat_var.get()
        trans = trans_ov or self.trans_var.get()
        ip    = ip_ov    or self.ip_var.get()

        if no_bridge:
            bridge_lines = []
            use = "0"
        else:
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
        content += "CircuitBuildTimeout 30\n"
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

        if cfg.get("sni_enabled", False):
            sni_host = cfg.get("sni_host", "www.google.com").strip()
            if sni_host:
                content += f"# SNI override active: {sni_host}\n"
                content += f"# TLSHostname={sni_host} (applied per bridge when supported)\n"

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
        self.auto_btn.configure(text="⚡ Auto (active)",
                                bg="#1E2535", fg=C["RED"],
                                activebackground="#273048", activeforeground=C["RED"])
        self.stop_btn.configure(fg=C["RED"], activeforeground=C["RED"])
        self._auto_connect_active = True
        threading.Thread(target=self._run_auto_connect, daemon=True).start()

    def _reset_auto_btn(self):
        self.auto_btn.configure(text="⚡ Auto", command=self.start_auto_connect,
                                bg="#1E2535", fg=C["FG"],
                                activebackground="#273048", activeforeground=C["FG"])

    def _run_auto_connect(self):
        if self.no_bridge_var.get():
            self.root.after(0, self.append_log,
                            "\n[Auto] No-bridge mode: connecting directly to Tor network.\n", "auto")
            if self._try_bridge_config(None, None, None, no_bridge=True):
                self.root.after(0, self.append_log, "[Auto] ✅ Connected (no bridge)\n", "auto")
                self.root.after(0, self._reset_auto_btn)
                return
            self.root.after(0, self.append_log, "[Auto] ❌ Direct connection failed.\n", "auto")
            self._auto_connect_active = False
            self.root.after(0, self._reset_auto_btn)
            return

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
                self.root.after(0, self._reset_auto_btn)
                return
            if not self._auto_connect_active:
                self.root.after(0, self._reset_auto_btn)
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
                self.root.after(0, self.append_log, f"[Auto] ✅ Connected with {label}\n")
                self.root.after(0, self._reset_auto_btn)
                return

        if self._auto_connect_active:
            self.root.after(0, self.update_status,
                            "Auto-connect failed. Try updating bridges or manual settings.")
            self.root.after(0, self.append_log, "[Auto] ❌ All bridge groups exhausted.\n")
        self._auto_connect_active = False
        self.root.after(0, self._reset_auto_btn)

    def _try_bridge_config(self, cat, trans, ip,
                           timeout_override=None, no_bridge=False) -> bool:
        timeout_s = (timeout_override if timeout_override is not None
                     else self.cfg.get("auto_connect_timeout", 180))

        if not self._check_port_free(TOR_SOCKS_PORT):
            self.root.after(0, self.append_log,
                            f"[Auto] Port {TOR_SOCKS_PORT} is already in use.\n", "err")
            return False

        try:
            torrc, tor_exe, _, bridge_lines = self.generate_torrc(
                cat_ov=cat, trans_ov=trans, ip_ov=ip,
                src_ov="Delta-Kronecker Tor-Bridges-Collector",
                no_bridge_ov=no_bridge)
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
            bg="#1E2535", fg=C["RED"],
            activebackground="#273048", activeforeground=C["RED"]))

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

            last_move = time.time()
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
                if pct == 100:
                    self.tor_connected = True
                    if not no_bridge and cat:
                        self._save_last_success(cat, trans, ip)
                    self.root.after(0, self.update_status, "Tor is fully connected.")
                    self.root.after(0, self._start_uptime)
                    self.root.after(0, self.stat_tor_var.set, "✅ Connected")
                    self.root.after(0, self._auto_enable_proxy)
                    self.root.after(500, self._schedule_auto_test)
                    self.root.after(0, self._start_watchdog)
                    self.root.after(0, self._start_keepalive)
                    self._notify("Delta Tor", "✅ Tor is fully connected!")
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
            if (self.source_var.get() != "Default (Built-in)"
                    and use_bridges == "0"
                    and not self.no_bridge_var.get()):
                self.root.after(0, self.append_log,
                                "Warning: No bridges found. Starting without bridges.\n", "warn")
            self.root.after(0, self.update_status, "Starting Tor…")
            self.tor_process = subprocess.Popen(
                [tor_exe, "-f", torrc],
                stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                text=True, creationflags=subprocess.CREATE_NO_WINDOW)
            self.root.after(0, self.update_status, "Tor is running.")
            self.root.after(0, lambda: self.stop_btn.config(
                bg="#1E2535", fg=C["RED"],
                activebackground="#273048", activeforeground=C["RED"]))

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
                        self._notify("Delta Tor", "✅ Tor is fully connected!")

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
        try:
            key = winreg.OpenKey(winreg.HKEY_CURRENT_USER,
                r'Software\Microsoft\Windows\CurrentVersion\Internet Settings',
                0, winreg.KEY_ALL_ACCESS)
            winreg.SetValueEx(key, 'ProxyEnable',   0, winreg.REG_DWORD, 0)
            winreg.SetValueEx(key, 'ProxyServer',   0, winreg.REG_SZ, '')
            winreg.SetValueEx(key, 'ProxyOverride', 0, winreg.REG_SZ, '')
            winreg.CloseKey(key)
            ctypes.windll.wininet.InternetSetOptionW(0, 39, 0, 0)
            ctypes.windll.wininet.InternetSetOptionW(0, 37, 0, 0)
        except Exception:
            pass
        self.proxy_var.set(False)
        self.root.after(0, self._refresh_proxy_btn)
        self.root.after(0, lambda: self.stop_btn.config(
            bg="#1E2535", fg=C["FG"],
            activebackground="#273048", activeforeground=C["FG"]))
        self.root.after(0, self._reset_auto_btn)
        self.root.after(0, self.update_status,    "Tor stopped.")
        self.root.after(0, self.update_conn_progress, 0)
        self.root.after(0, self._stop_uptime)
        self.root.after(0, self.stat_tor_var.set,     "—")
        self.root.after(0, self.stat_ip_var.set,      "—")
        self.root.after(0, self.stat_country_var.set, "—")
        self._notify("Delta Tor", "Tor has stopped.")

    def stop_tor(self):
        self._auto_connect_active = False
        if self.tor_process:
            try:
                self.tor_process.terminate()
                try: self.tor_process.wait(timeout=3)
                except Exception:
                    try: self.tor_process.kill()
                    except Exception: pass
                    try: self.tor_process.wait(timeout=2)
                    except Exception: pass
            except Exception: pass
            finally:
                self.tor_process = None

        try:
            import subprocess as _sp
            my_pid = os.getpid()
            _sp.run(
                ["taskkill", "/F", "/FI", f"PID ne {my_pid}", "/IM", "tor.exe"],
                creationflags=getattr(_sp, "CREATE_NO_WINDOW", 0x08000000),
                capture_output=True, timeout=5)
        except Exception: pass
        self._on_tor_stopped()

    def _cleanup_old_data_dirs(self, days_old=7):
        import glob
        try:
            extract_dir = self.extract_dir
            pattern = os.path.join(extract_dir, "data_par_*")
            current_time = time.time()
            cutoff_time = current_time - (days_old * 86400)

            for dir_path in glob.glob(pattern):
                if os.path.isdir(dir_path):
                    mtime = os.path.getmtime(dir_path)
                    if mtime < cutoff_time:
                        try:
                            shutil.rmtree(dir_path)
                            self.append_log(f"[Cleanup] Removed old directory: {os.path.basename(dir_path)}\n", "info")
                        except Exception as e:
                            self.append_log(f"[Cleanup] Failed to remove {dir_path}: {e}\n", "warn")
        except Exception as e:
            self.append_log(f"[Cleanup] Error during cleanup: {e}\n", "warn")

    def _dl_simple(self, url, dest, retries=4, timeout=45):
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
                delay = min(2 ** attempt, 16)
                time.sleep(delay)

    def auto_initialize(self):
        self.setup_tor()
        self._download_all_bridges_parallel()
        self.root.after(0, self.update_status, "Ready.")
        self.root.after(0, self._refresh_bridge_info)

    def setup_tor(self):
        tor_exe = os.path.join(self.extract_dir, "tor", "tor.exe")
        if os.path.exists(tor_exe):
            return
        bundle_name_versioned = "tor-expert-bundle-windows-x86_64-15.0.14.tar.gz"
        bundle_name_generic   = "tor-expert-bundle.tar.gz"
        archive = None
        for name in (bundle_name_versioned, bundle_name_generic):
            for search_dir in (BASE_DIR, _EXE_DIR):
                candidate = os.path.join(search_dir, name)
                if os.path.exists(candidate):
                    archive = candidate
                    break
            if archive:
                break
        if not archive:
            self.root.after(0, self.update_status, "Error: tor-expert-bundle.tar.gz missing!")
            self.root.after(0, lambda: messagebox.showerror("Error", "tor-expert-bundle.tar.gz not found in application directory!"))
            return
        target_archive = os.path.join(BASE_DIR, os.path.basename(archive))
        if os.path.normpath(archive) != os.path.normpath(target_archive):
            try:
                os.makedirs(BASE_DIR, exist_ok=True)
                shutil.move(archive, target_archive)
                archive = target_archive
            except Exception:
                pass
        self.root.after(0, self.update_status, "Extracting Tor Bundle...")
        try:
            with tarfile.open(archive, "r:gz") as tar:
                if hasattr(tar, 'extractall'):
                    try:
                        tar.extractall(path=self.extract_dir, filter='data')
                    except TypeError:
                        tar.extractall(path=self.extract_dir)
                else:
                    tar.extractall(path=self.extract_dir)
            self.root.after(0, self.update_status, "Extraction complete.")
        except Exception as e:
            self.root.after(0, self.update_status, f"Extraction failed: {e}")
            self.root.after(0, lambda: messagebox.showerror("Error", f"Failed to extract Tor bundle:\n{e}"))

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

        with ThreadPoolExecutor(max_workers=2) as ex:
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

        with ThreadPoolExecutor(max_workers=2) as ex:
            ex.map(_fetch, BRIDGE_DATA)
        self.root.after(0, self._hide_dl)
        self.root.after(0, self.update_status, "Ready. All bridges downloaded.")
        self.root.after(0, self._refresh_bridge_info)

    def start_download_bridges(self):
        threading.Thread(target=self._download_all_bridges_parallel, daemon=True).start()

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

    def show_help_window(self):
        w = tk.Toplevel(self.root)
        w.title("Help — Delta Tor")
        w.geometry("720x660")
        w.configure(bg=C["BG"])
        w.resizable(False, False)
        w.update()
        apply_dark_titlebar(w)
        set_window_icon(w)
        self._apply_icon_to(w)

        tk.Frame(w, bg=C["ACC"], height=3).pack(fill='x')
        tk.Label(w, text="⬡  How to Use — Delta Tor",
                 font=('Segoe UI', 14, 'bold'), bg=C["BG"], fg=C["ACC"]).pack(pady=(14, 4))

        tf = tk.Frame(w, bg=C["BLK"])
        tf.pack(fill='both', expand=True, padx=20, pady=8)
        txt = tk.Text(tf, font=('Segoe UI', 9), wrap='word', bg=C["BLK"],
                      fg=C["FG"], bd=0, padx=16, pady=12, spacing2=4)
        sb  = ttk.Scrollbar(tf, command=txt.yview)
        txt.configure(yscrollcommand=sb.set)
        sb.pack(side='right', fill='y')
        txt.pack(fill='both', expand=True)

        txt.insert('1.0', f"""\
⚡  QUICK START
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  1. Category  →  Tested & Active
  2. Transport →  obfs4
  3. IP        →  IPv4
  4. Click ⚡ Auto Connect

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
🆕  NEW FEATURES
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

  ⚙ Custom Bridges  (Mahsa)
     Enter your own bridge lines and ping each one
     to see latency before connecting.

  🔐 SNI Override  (Pedram)
     Settings → SNI Settings. Enter a hostname like
     www.google.com to disguise TLS traffic. Helps
     against deep packet inspection (DPI).

  🔍 Bridge Scanner  (PRINCO)
     Scan any bridge file — TCP-pings every entry,
     shows reachability and latency. Export working
     bridges to a file.

  🚫 No Bridge Mode
     Check "Connect without bridge" to connect
     directly to the Tor network (no obfs4/tunnel).
     Use only if Tor is NOT blocked in your country.

  ⚡ Multi-Connect
     Launches all connection types simultaneously
     on separate ports. The fastest one wins.
     Ports: SOCKS 9060-9072  HTTP 19062-19074.

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
🌐  SYSTEM PROXY
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  HTTP proxy: 127.0.0.1:{HTTP_PROXY_PORT}
  SOCKS5:     127.0.0.1:{TOR_SOCKS_PORT}
  DNS resolved by Tor — no DNS leaks.

  ✅ Chrome, Edge, Telegram — automatic.
  ❌ Firefox: Settings → Network → SOCKS5 manually.

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
🔎  BRIDGE TYPES
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  obfs4      → Best for Iran/China — random data
  webtunnel  → Looks like HTTPS traffic
  vanilla    → Plain Tor — only if not blocked

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
⚠️  TROUBLESHOOTING
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  Stuck below 100%?  → Update bridges, try Auto.
  YouTube/Instagram? → Enable Exit Nodes in Settings.
  Port {TOR_SOCKS_PORT} busy?   → Another Tor is running.
  No bridges?        → Click ↺ Update Bridges.
""")
        txt.configure(state='disabled')
        tk.Button(w, text="Close", command=w.destroy,
                  bg=C["ACC"], fg="white", font=('Segoe UI', 10, 'bold'),
                  relief="flat", cursor="hand2",
                  activebackground=C["ACC2"]
                  ).pack(pady=(0, 14), padx=120, fill='x', ipady=5)

if __name__ == "__main__":
    root = tk.Tk()
    root.withdraw()
    root.configure(bg=C["BG"], bd=0, relief='flat', padx=0, pady=0)
    app = DeltaTorGUI(root)

    def _show():
        root.deiconify()
        root.update()
        apply_dark_titlebar(root)

    root.after(200, _show)
    root.mainloop()
