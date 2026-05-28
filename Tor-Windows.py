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
    "BG":    "#1A1D24",
    "PANEL": "#22262F",
    "CARD":  "#272B35",
    "BORDER":"#333848",
    "FG":    "#E8ECF4",
    "FG2":   "#8A93A8",
    "ACC":   "#7D4CDB",
    "ACC2":  "#9B6EF5",
    "GRN":   "#3FCF8E",
    "RED":   "#F04E4E",
    "YLW":   "#F0B429",
    "ORG":   "#F07540",
    "CYAN":  "#4EC9F0",
    "BTN":   "#2B3040",
    "BTN2":  "#363D58",
    "SEL":   "#2D1F5E",
    "BLK":   "#1A1D24",
    "PRP":   "#B99EFF",
}

if getattr(sys, 'frozen', False):
    BASE_DIR = os.path.dirname(os.path.abspath(sys.executable))
else:
    BASE_DIR = os.path.dirname(os.path.abspath(__file__))

def resource_path(filename):
    if hasattr(sys, '_MEIPASS'):
        return os.path.join(sys._MEIPASS, filename)
    return os.path.join(BASE_DIR, filename)

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
        cap = ctypes.c_int(0x100C0A)
        dwm.DwmSetWindowAttribute(hwnd, 35, ctypes.byref(cap), ctypes.sizeof(cap))
        txt = ctypes.c_int(0xF4ECE8)
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
        nid.szTip       = "Tor Client"
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
    SLOT_DEFS = [
        ("No Bridge",          9061,   9062,   19061,  True,  None,             None,         None),
        ("Tested obfs4 v4",    9063,   9064,   19063,  False, "Tested & Active","obfs4",      "IPv4"),
        ("Tested webtunnel v4",9065,   9066,   19065,  False, "Tested & Active","webtunnel",  "IPv4"),
        ("Tested webtunnel v6",9067,   9068,   19067,  False, "Tested & Active","webtunnel",  "IPv6"),
        ("Tested vanilla v4",  9069,   9070,   19069,  False, "Tested & Active","vanilla",    "IPv4"),
        ("Tested vanilla v6",  9071,   9072,   19071,  False, "Tested & Active","vanilla",    "IPv6"),
    ]

    CHECK_URL_HOST = "www.gstatic.com"
    CHECK_URL_PATH = "/generate_204"
    SPEED_HOST     = "cachefly.cachefly.net"
    SPEED_PATH     = "/0.5b.test"

    def __init__(self, parent, extract_dir, bridges_dir,
                 get_safe_filename, generate_torrc_fn, cfg, on_connected):
        self.extract_dir       = extract_dir
        self.bridges_dir       = bridges_dir
        self.get_safe_filename = get_safe_filename
        self.cfg               = cfg
        self.on_connected      = on_connected

        self._procs   = {}
        self._running = False
        self._lock    = threading.Lock()
        self._stop_events = {}
        self._active_proxy_label = None
        self._proxy_stop_ev = None
        self._slot_health = {}

        w = tk.Toplevel(parent)
        w.title("Multi-Connect")
        w.geometry("910x600")
        w.configure(bg=C["BG"])
        w.resizable(True, True)
        w.update()
        apply_dark_titlebar(w)
        set_window_icon(w)
        self._win = w
        w.protocol("WM_DELETE_WINDOW", self._on_close)

        tk.Frame(w, bg=C["ACC"], height=3).pack(fill='x')

        hdr = tk.Frame(w, bg=C["BG"])
        hdr.pack(fill='x', padx=16, pady=(10, 4))
        tk.Label(hdr, text="⬡  Parallel Multi-Connect (Tested Only)",
                 font=('Segoe UI', 13, 'bold'), bg=C["BG"], fg=C["ACC"]).pack(side='left')

        ctrl_f = tk.Frame(w, bg=C["BG"])
        ctrl_f.pack(fill='x', padx=16, pady=(0, 6))
        self._start_btn = tk.Button(ctrl_f, text="▶  Launch All",
                                    command=self._start_all,
                                    bg=C["ACC"], fg="white",
                                    font=('Segoe UI', 10, 'bold'),
                                    relief="flat", cursor="hand2",
                                    activebackground=C["ACC2"])
        self._start_btn.pack(side='left', ipady=6, padx=(0, 6))
        tk.Button(ctrl_f, text="⏹  Stop All",
                  command=self._stop_all,
                  bg=C["BTN2"], fg=C["FG"],
                  font=('Segoe UI', 10, 'bold'),
                  relief="flat", cursor="hand2").pack(side='left', ipady=6)

        self._auto_proxy_var = tk.BooleanVar(value=False)
        self._auto_proxy_chk = tk.Checkbutton(ctrl_f, text="🔄 Auto System Proxy (Best Ping)",
                                              variable=self._auto_proxy_var,
                                              bg=C["BG"], fg=C["FG"], selectcolor=C["PANEL"],
                                              activebackground=C["BG"], activeforeground=C["FG"],
                                              font=('Segoe UI', 9, 'bold'), relief="flat", cursor="hand2")
        self._auto_proxy_chk.pack(side='right', padx=12)

        self._info_lbl = tk.Label(ctrl_f, text="",
                                  font=('Segoe UI', 9), bg=C["BG"], fg=C["FG2"])
        self._info_lbl.pack(side='left', padx=12)

        tk.Label(w,
                 text="All connections stay alive. Auto System Proxy will automatically assign system proxy to the lowest-latency active slot.",
                 font=('Segoe UI', 8), bg=C["BG"], fg=C["FG2"]).pack(padx=16, anchor='w')

        canvas_frame = tk.Frame(w, bg=C["BG"])
        canvas_frame.pack(fill='both', expand=True, padx=16, pady=6)
        slots_canvas = tk.Canvas(canvas_frame, bg=C["BG"], highlightthickness=0)
        vsb = ttk.Scrollbar(canvas_frame, orient='vertical', command=slots_canvas.yview)

        slots_canvas.configure(yscrollcommand=vsb.set)
        vsb.pack(side='right', fill='y')
        slots_canvas.pack(fill='both', expand=True)
        slots_inner = tk.Frame(slots_canvas, bg=C["BG"])
        slots_canvas.create_window((0, 0), window=slots_inner, anchor='nw')
        slots_inner.bind("<Configure>", lambda e: slots_canvas.configure(
            scrollregion=slots_canvas.bbox("all")))
        slots_canvas.bind("<MouseWheel>",
                          lambda e: slots_canvas.yview_scroll(-1*(e.delta//120), "units"))

        slots_inner.columnconfigure(0, weight=1)
        slots_inner.columnconfigure(1, weight=1)

        self._slot_widgets = {}

        for i, (label, socks, ctrl, http, no_bridge, cat, trans, ip) in enumerate(self.SLOT_DEFS):
            sf = tk.Frame(slots_inner, bg=C["PANEL"], bd=0)
            sf.grid(row=i // 2, column=i % 2, padx=6, pady=6, sticky="nsew")

            tk.Frame(sf, bg=C["ACC"], width=4).pack(side='left', fill='y')
            inner = tk.Frame(sf, bg=C["PANEL"])
            inner.pack(fill='x', expand=True, padx=8, pady=6)

            top_r = tk.Frame(inner, bg=C["PANEL"])
            top_r.pack(fill='x')
            tk.Label(top_r, text=label,
                     font=('Segoe UI', 9, 'bold'), bg=C["PANEL"], fg=C["FG"],
                     width=18, anchor='w').pack(side='left')
            
            tk.Label(top_r, text=f"SOCKS:{socks}  HTTP:{http}",
                     font=('Consolas', 10, 'bold'), bg=C["PANEL"], fg=C["CYAN"]).pack(side='left', padx=6)
            
            status_lbl = tk.Label(top_r, text="Idle",
                                  font=('Segoe UI', 8), bg=C["PANEL"], fg=C["FG2"])
            status_lbl.pack(side='right')

            prog_row = tk.Frame(inner, bg=C["PANEL"])
            prog_row.pack(fill='x', pady=(3, 0))
            prog_var = tk.IntVar(value=0)
            bar = ttk.Progressbar(prog_row, variable=prog_var,
                                  maximum=100, mode='determinate')
            bar.pack(side='left', fill='x', expand=True)
            pct_lbl = tk.Label(prog_row, text="0%",
                               font=('Segoe UI', 8, 'bold'), bg=C["PANEL"], fg=C["FG2"],
                               width=5)
            pct_lbl.pack(side='right')

            info_row = tk.Frame(inner, bg=C["PANEL"])
            info_row.pack(fill='x', pady=(2, 0))
            health_lbl = tk.Label(info_row, text="⬤ —",
                                  font=('Segoe UI', 8), bg=C["PANEL"], fg=C["FG2"])
            health_lbl.pack(side='left')
            speed_lbl = tk.Label(info_row, text="",
                                 font=('Segoe UI', 8), bg=C["PANEL"], fg=C["FG2"])
            speed_lbl.pack(side='left', padx=(10, 0))
            
            log_lbl = tk.Label(inner, text="",
                               font=('Consolas', 7), bg=C["PANEL"], fg=C["FG2"],
                               anchor='w', wraplength=380)
            log_lbl.pack(fill='x')

            btn_row = tk.Frame(inner, bg=C["PANEL"])
            btn_row.pack(fill='x', pady=(4, 0))

            proxy_btn = tk.Button(btn_row, text="🌐 Set Proxy",
                                  bg=C["BTN"], fg=C["FG"],
                                  font=('Segoe UI', 8, 'bold'),
                                  relief="flat", cursor="hand2",
                                  activebackground=C["BTN2"])
            proxy_btn.pack(side='left', ipady=3, padx=(0, 4))
            proxy_btn.configure(
                command=lambda lbl=label, hp=http, sp=socks: self._set_proxy_to_slot(lbl, sp, hp))

            retry_btn = tk.Button(btn_row, text="↺ Retry",
                                  bg=C["BTN"], fg=C["FG"],
                                  font=('Segoe UI', 8, 'bold'),
                                  relief="flat", cursor="hand2",
                                  activebackground=C["BTN2"])
            retry_btn.pack(side='left', ipady=3, padx=(0, 4))
            retry_btn.configure(
                command=lambda lbl=label, s=socks, c=ctrl, hp=http, nb=no_bridge,
                                   ca=cat, tr=trans, iip=ip: self._retry_slot(lbl, s, c, hp, nb, ca, tr, iip))

            speed_btn = tk.Button(btn_row, text="⚡ Speed Test",
                                  bg=C["BTN"], fg=C["FG"],
                                  font=('Segoe UI', 8, 'bold'),
                                  relief="flat", cursor="hand2",
                                  activebackground=C["BTN2"])
            speed_btn.pack(side='left', ipady=3)
            speed_btn.configure(
                command=lambda lbl=label, s=socks: self._manual_speed_test(lbl, s))

            health_btn = tk.Button(btn_row, text="🔍 Health",
                                   bg=C["BTN"], fg=C["FG"],
                                   font=('Segoe UI', 8, 'bold'),
                                   relief="flat", cursor="hand2",
                                   activebackground=C["BTN2"])
            health_btn.pack(side='left', ipady=3, padx=(4, 0))
            health_btn.configure(
                command=lambda lbl=label, s=socks: self._manual_health_check(lbl, s))

            self._slot_widgets[label] = {
                "frame":      sf,
                "prog_var":   prog_var,
                "pct_lbl":    pct_lbl,
                "status_lbl": status_lbl,
                "log_lbl":    log_lbl,
                "bar":        bar,
                "health_lbl": health_lbl,
                "speed_lbl":  speed_lbl,
                "proxy_btn":  proxy_btn,
                "socks_port": socks,
                "http_port":  http,
            }

    def _on_close(self):
        self._stop_all()
        self._win.destroy()

    def _update_slot(self, label, pct=None, status=None, log=None,
                     connected=False, failed=False):
        w = self._slot_widgets.get(label)
        if not w:
            return
        if pct is not None:
            w["prog_var"].set(pct)
            w["pct_lbl"].configure(text=f"{pct}%",
                                   fg=C["FG"] if pct < 100 else C["FG"])
        if status is not None:
            color = C["FG"] if connected else (C["FG2"] if not failed else C["FG2"])
            w["status_lbl"].configure(text=status, fg=color)
        if log is not None:
            w["log_lbl"].configure(text=log[:120])
        if connected:
            w["frame"].configure(bg=C["PANEL"])
            w["bar"].configure(style='Won.Horizontal.TProgressbar')
        if failed:
            w["bar"].configure(style='Horizontal.TProgressbar')

    def _check_health_once(self, socks_port, timeout=8) -> tuple:
        t0 = time.time()
        try:
            s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            s.settimeout(timeout)
            s.connect(("127.0.0.1", socks_port))
            host_b = self.CHECK_URL_HOST.encode()
            s.sendall(b'\x05\x01\x00')
            if s.recv(2)[1] != 0x00:
                s.close(); return False, 999999.0
            s.sendall(b'\x05\x01\x00\x03' + bytes([len(host_b)]) + host_b + (443).to_bytes(2, 'big'))
            r = s.recv(10)
            if r[1] != 0x00:
                s.close(); return False, 999999.0
            ctx = ssl.create_default_context()
            s = ctx.wrap_socket(s, server_hostname=self.CHECK_URL_HOST)
            s.sendall((f"GET {self.CHECK_URL_PATH} HTTP/1.1\r\nHost: {self.CHECK_URL_HOST}\r\n"
                       f"Connection: close\r\nUser-Agent: Mozilla/5.0\r\n\r\n").encode())
            resp = s.recv(512).decode(errors="replace")
            s.close()
            latency = (time.time() - t0) * 1000.0
            if "204" in resp or "HTTP/1." in resp:
                return True, latency
            return False, 999999.0
        except Exception:
            return False, 999999.0

    def _run_health_loop(self, label, socks_port, stop_ev, auto_speed_done):
        first_success = False
        while not stop_ev.wait(10):
            ok, latency = self._check_health_once(socks_port)
            with self._lock:
                self._slot_health[label] = (ok, latency)

            if ok:
                health_txt = f"⬤ Online ({int(latency)} ms)"
                health_fg  = C["GRN"]
                if not first_success:
                    first_success = True
                    auto_speed_done.append(True)
                    threading.Thread(
                        target=self._run_speed_test,
                        args=(label, socks_port, True), daemon=True).start()
            else:
                health_txt = "⬤ Offline"
                health_fg  = C["FG2"]

            w = self._slot_widgets.get(label)
            if w:
                self._win.after(0, w["health_lbl"].configure,
                                {"text": health_txt, "fg": health_fg})

            if self._auto_proxy_var.get():
                self._evaluate_best_proxy()

    def _evaluate_best_proxy(self):
        with self._lock:
            best_label = None
            best_latency = 999999.0
            for label, (ok, latency) in self._slot_health.items():
                if ok and latency < best_latency:
                    best_latency = latency
                    best_label = label

            if best_label and best_label != self._active_proxy_label:
                for label_def, socks, ctrl, http, *rest in self.SLOT_DEFS:
                    if label_def == best_label:
                        self._win.after(0, self._set_proxy_to_slot, best_label, socks, http)
                        break

    def _run_speed_test(self, label, socks_port, auto=False):
        w = self._slot_widgets.get(label)
        if not w:
            return
        self._win.after(0, w["speed_lbl"].configure, {"text": "⏳ Testing…"})
        try:
            s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            s.settimeout(30)
            s.connect(("127.0.0.1", socks_port))
            host_b = self.SPEED_HOST.encode()
            s.sendall(b'\x05\x01\x00')
            if s.recv(2)[1] != 0x00:
                raise ConnectionError("SOCKS5 auth failed")
            s.sendall(b'\x05\x01\x00\x03' + bytes([len(host_b)]) + host_b + (80).to_bytes(2, 'big'))
            r = s.recv(10)
            if r[1] != 0x00:
                raise ConnectionError("SOCKS5 connect failed")
            request = (f"GET {self.SPEED_PATH} HTTP/1.1\r\nHost: {self.SPEED_HOST}\r\n"
                       f"Connection: close\r\nUser-Agent: Mozilla/5.0\r\n\r\n").encode()
            s.sendall(request)
            buf = b""
            while b"\r\n\r\n" not in buf:
                chunk = s.recv(4096)
                if not chunk:
                    break
                buf += chunk
            t0 = time.time()
            received = 0
            while True:
                chunk = s.recv(65536)
                if not chunk:
                    break
                received += len(chunk)
                if received >= 1024 * 1024:
                    break
            elapsed = time.time() - t0
            s.close()
            if elapsed > 0 and received > 0:
                kbps = received / elapsed / 1024
                if kbps >= 1024:
                    speed_str = f"⚡ {kbps/1024:.1f} MB/s"
                else:
                    speed_str = f"⚡ {kbps:.0f} KB/s"
            else:
                speed_str = "⚡ —"
        except Exception as e:
            speed_str = f"✗ Speed: {str(e)[:20]}"
        self._win.after(0, w["speed_lbl"].configure, {"text": speed_str})

    def _manual_speed_test(self, label, socks_port):
        w = self._slot_widgets.get(label)
        if not w:
            return
        status = w["status_lbl"].cget("text")
        if "Connected" not in status and "Bootstrapped 100" not in status:
            self._win.after(0, w["speed_lbl"].configure,
                            {"text": "⚠ Not connected"})
            return
        threading.Thread(
            target=self._run_speed_test, args=(label, socks_port, False),
            daemon=True).start()

    def _manual_health_check(self, label, socks_port):
        def _run():
            ok, latency = self._check_health_once(socks_port)
            w = self._slot_widgets.get(label)
            if w:
                health_txt = f"⬤ Online ({int(latency)} ms)" if ok else "⬤ Offline"
                self._win.after(0, w["health_lbl"].configure, {
                    "text": health_txt,
                    "fg": C["GRN"] if ok else C["FG2"]})
        threading.Thread(target=_run, daemon=True).start()

    def _set_proxy_to_slot(self, label, socks_port, http_port):
        if self._proxy_stop_ev is not None:
            self._proxy_stop_ev.set()
            self._proxy_stop_ev = None

        for lbl, wgt in self._slot_widgets.items():
            wgt["proxy_btn"].configure(
                text="🌐 Set Proxy", bg=C["BTN"], fg=C["FG"])

        if self._active_proxy_label == label and not self._auto_proxy_var.get():
            self._active_proxy_label = None
            self._disable_system_proxy()
            self._win.after(0, self._info_lbl.configure,
                            {"text": "Proxy disabled."})
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
            w["proxy_btn"].configure(
                text="🌐 Proxy ON", bg="#0E2A1A", fg=C["FG"])
        self._win.after(0, self._info_lbl.configure,
                        {"text": f"Proxy → {label}  HTTP:{http_port}  SOCKS:{socks_port}"})

    def _enable_system_proxy(self, http_port):
        try:
            import winreg as _wr
            key = _wr.OpenKey(
                _wr.HKEY_CURRENT_USER,
                r'Software\Microsoft\Windows\CurrentVersion\Internet Settings',
                0, _wr.KEY_ALL_ACCESS)
            proxy_str = f'127.0.0.1:{http_port}'
            _wr.SetValueEx(key, 'ProxyEnable',   0, _wr.REG_DWORD, 1)
            _wr.SetValueEx(key, 'ProxyServer',   0, _wr.REG_SZ, proxy_str)
            _wr.SetValueEx(key, 'ProxyOverride', 0, _wr.REG_SZ, '127.0.0.1;localhost;<local>')
            _wr.CloseKey(key)
            ctypes.windll.wininet.InternetSetOptionW(0, 39, 0, 0)
            ctypes.windll.wininet.InternetSetOptionW(0, 37, 0, 0)
        except Exception:
            pass

    def _disable_system_proxy(self):
        try:
            import winreg as _wr
            key = _wr.OpenKey(
                _wr.HKEY_CURRENT_USER,
                r'Software\Microsoft\Windows\CurrentVersion\Internet Settings',
                0, _wr.KEY_ALL_ACCESS)
            _wr.SetValueEx(key, 'ProxyEnable', 0, _wr.REG_DWORD, 0)
            _wr.SetValueEx(key, 'ProxyServer',  0, _wr.REG_SZ, '')
            _wr.CloseKey(key)
            ctypes.windll.wininet.InternetSetOptionW(0, 39, 0, 0)
            ctypes.windll.wininet.InternetSetOptionW(0, 37, 0, 0)
        except Exception:
            pass

    def _run_slot(self, label, socks_port, ctrl_port, http_port,
                  no_bridge, cat, trans, ip, retry_count=0, max_retries=5):
        tor_exe = os.path.join(self.extract_dir, "tor", "tor.exe")
        if not os.path.exists(tor_exe):
            self._win.after(0, self._update_slot, label,
                            None, "tor.exe not found", None, False, True)
            return

        with self._lock:
            old = self._procs.get(label)
            if old:
                try: old.terminate()
                except: pass

        data_dir   = os.path.join(self.extract_dir, f"data_par_{socks_port}")
        os.makedirs(data_dir, exist_ok=True)
        torrc_path = os.path.join(data_dir, "torrc")

        pt_dir   = os.path.join(self.extract_dir, "tor", "pluggable_transports")
        lyrebird = os.path.join(pt_dir, "lyrebird.exe")
        conjure  = os.path.join(pt_dir, "conjure-client.exe")

        if no_bridge:
            bridge_lines = []
            use = "0"
        else:
            bridge_lines = []
            limit = self.cfg.get("bridges_in_torrc", 100)
            do_shuffle = self.cfg.get("shuffle_bridges", True)
            for c, t, v, _ in BRIDGE_DATA:
                if c == cat and t == trans and (ip == "Both" or ip == v):
                    fn = os.path.join(self.bridges_dir,
                                      self.get_safe_filename(c, t, v))
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
        content += "CircuitBuildTimeout 30\n"
        content += "LearnCircuitBuildTimeout 0\n"
        content += "GuardLifetime 90 days\n"
        content += "NumDirectoryGuards 6\n"
        content += "TokenBucketRefillInterval 10 msec\n"
        content += (f"ClientTransportPlugin meek_lite,obfs2,obfs3,obfs4,"
                    f"scramblesuit,webtunnel exec {lyrebird}\n")
        content += f"ClientTransportPlugin snowflake exec {lyrebird}\n"
        content += (f"ClientTransportPlugin conjure exec {conjure}"
                    f" -registerURL \"https://registration.refraction.network/api\"\n\n")
        if use == "1":
            content += "".join(bridge_lines)

        with open(torrc_path, "w", encoding="utf-8") as f:
            f.write(content)

        try:
            proc = subprocess.Popen(
                [tor_exe, "-f", torrc_path],
                stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                text=True, creationflags=subprocess.CREATE_NO_WINDOW)
        except Exception as e:
            self._win.after(0, self._update_slot, label,
                            None, f"Launch error: {e}", None, False, True)
            return

        with self._lock:
            self._procs[label] = proc

        self._win.after(0, self._update_slot, label, 0, "Connecting…")

        last_pct  = -1
        last_move = time.time()
        timeout_s = self.cfg.get("auto_connect_timeout", 180)
        connected = False

        for line in iter(proc.stdout.readline, ''):
            if not self._running:
                break
            self._win.after(0, self._update_slot, label, None, None, line.strip()[:80])
            m = re.search(r'Bootstrapped (\d+)%', line)
            if m:
                pct = int(m.group(1))
                self._win.after(0, self._update_slot, label, pct, f"Bootstrapped {pct}%")
                if pct != last_pct:
                    last_pct  = pct
                    last_move = time.time()
                if pct == 100 and not connected:
                    connected = True
                    self._win.after(0, self._update_slot, label,
                                    100, "✔ Connected!", None, True, False)
                    stop_ev = threading.Event()
                    self._stop_events[label] = stop_ev
                    auto_done = []
                    threading.Thread(
                        target=self._run_health_loop,
                        args=(label, socks_port, stop_ev, auto_done),
                        daemon=True).start()
                    self._win.after(0, self._info_lbl.configure,
                                    {"text": f"✔ {label} connected on SOCKS:{socks_port}"})
                    if self.on_connected:
                        self._win.after(0, self.on_connected,
                                        label, socks_port, ctrl_port, http_port)

            if last_pct >= 0 and not connected and time.time() - last_move > timeout_s:
                self._win.after(0, self._update_slot, label,
                                None, f"⚠ Timed out at {last_pct}%", None, False, True)
                break

        try:
            proc.stdout.close()
        except Exception:
            pass

        if not connected:
            if retry_count >= max_retries:
                self._win.after(0, self._update_slot, label,
                                None, f"❌ Failed after {max_retries} retries", None, False, True)
                return
            
            delay = min(3 * (retry_count + 1), 15)
            self._win.after(0, self._update_slot, label,
                            None, f"↺ Retrying in {delay}s…", None, False, False)
            time.sleep(delay)
            if self._running:
                self._win.after(0, self._update_slot, label, 0, "Retrying…")
                self._run_slot(label, socks_port, ctrl_port, http_port,
                               no_bridge, cat, trans, ip, retry_count + 1, max_retries)
        else:
            try:
                proc.wait()
            except Exception:
                pass
            if self._running:
                w = self._slot_widgets.get(label)
                if w:
                    self._win.after(0, w["health_lbl"].configure,
                                    {"text": "⬤ Offline", "fg": C["FG2"]})
                self._win.after(0, self._update_slot, label,
                                0, "Died — retrying…", None, False, True)
                delay = min(3 * (retry_count + 1), 15)
                time.sleep(delay)
                if self._running:
                    self._run_slot(label, socks_port, ctrl_port, http_port,
                                   no_bridge, cat, trans, ip, retry_count + 1, max_retries)

    def _retry_slot(self, label, socks_port, ctrl_port, http_port,
                    no_bridge, cat, trans, ip):
        ev = self._stop_events.get(label)
        if ev:
            ev.set()
        with self._lock:
            old = self._procs.get(label)
            if old:
                try: old.terminate()
                except: pass
        self._win.after(0, self._update_slot, label, 0, "Retrying…")
        threading.Thread(
            target=self._run_slot,
            args=(label, socks_port, ctrl_port, http_port,
                  no_bridge, cat, trans, ip),
            daemon=True).start()

    def _start_all(self):
        if self._running:
            return
        self._running = True
        self._start_btn.configure(state='disabled')

        for label, wgt in self._slot_widgets.items():
            wgt["prog_var"].set(0)
            wgt["pct_lbl"].configure(text="0%")
            wgt["status_lbl"].configure(text="Waiting…", fg=C["FG2"])
            wgt["log_lbl"].configure(text="")
            wgt["health_lbl"].configure(text="⬤ —", fg=C["FG2"])
            wgt["speed_lbl"].configure(text="")
            wgt["bar"].configure(style='Horizontal.TProgressbar')

        for label, socks, ctrl, http, no_bridge, cat, trans, ip in self.SLOT_DEFS:
            threading.Thread(
                target=self._run_slot,
                args=(label, socks, ctrl, http, no_bridge, cat, trans, ip),
                daemon=True).start()

    def _stop_all(self):
        self._running = False
        for ev in self._stop_events.values():
            try: ev.set()
            except: pass
        self._stop_events.clear()
        with self._lock:
            for lbl, p in self._procs.items():
                try: p.terminate()
                except: pass
        self._procs.clear()
        self._slot_health.clear()
        if self._proxy_stop_ev:
            self._proxy_stop_ev.set()
            self._proxy_stop_ev = None
        if self._active_proxy_label:
            self._disable_system_proxy()
            self._active_proxy_label = None
        self._start_btn.configure(state='normal')
        for label, wgt in self._slot_widgets.items():
            wgt["status_lbl"].configure(text="Stopped", fg=C["FG2"])
            wgt["health_lbl"].configure(text="⬤ —", fg=C["FG2"])

class TorClientGUI:
    def __init__(self, root):
        self.root = root
        self.root.title("Tor Client")
        
        try:
            self.root.attributes("-alpha", 0.0)
        except Exception:
            pass

        self.root.configure(bg=C["BG"])
        self.root.geometry("800x980")
        apply_dark_titlebar(self.root)
        set_window_icon(self.root)

        self.setup_theme()

        self.cfg = load_config()
        self.extract_dir   = BASE_DIR
        self.archive_name  = os.path.join(BASE_DIR, "tor-expert-bundle.tar.gz")
        self.bridges_dir   = os.path.join(BASE_DIR, "bridges")
        self.logs_dir      = os.path.join(BASE_DIR, "logs")

        os.makedirs(self.bridges_dir, exist_ok=True)
        os.makedirs(self.logs_dir, exist_ok=True)

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

        self.root.after(1000, lambda: self.root.attributes("-alpha", 1.0))

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
        set_window_icon(dlg)
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
            try:
                import subprocess as _sp
                my_pid = os.getpid()
                _sp.run(
                    ["taskkill", "/F", "/FI", f"PID ne {my_pid}", "/IM", "tor.exe"],
                    creationflags=getattr(_sp, "CREATE_NO_WINDOW", 0x08000000),
                    capture_output=True, timeout=5)
            except Exception: pass
            self.root.destroy()

        tk.Button(bf, text="🗕  Minimize to Tray", command=_tray,
                  bg=C["BTN2"], fg=C["FG"], font=('Segoe UI', 9, 'bold'),
                  relief="flat", cursor="hand2"
                  ).pack(side='left', fill='x', expand=True, padx=(0, 4), ipady=4)
        tk.Button(bf, text="✕  Quit", command=_quit,
                  bg="#3A1010", fg=C["RED"], font=('Segoe UI', 9, 'bold'),
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
    
    def open_github_project(self):
        webbrowser.open("https://github.com/Delta-Kronecker/Tor-Windows")

    def setup_theme(self):
        s = ttk.Style()
        s.theme_use('clam')
        s.configure('.', background=C["BG"], foreground=C["FG"], font=('Segoe UI', 10))
        s.configure('TLabel',      background=C["BG"], foreground=C["FG"])
        s.configure('TLabelframe', background=C["BG"], foreground=C["ACC"],
                    bordercolor=C["BORDER"])
        s.configure('TLabelframe.Label', background=C["BG"], foreground=C["ACC"],
                    font=('Segoe UI', 10, 'bold'))
        s.configure('TCombobox', fieldbackground=C["BTN"], background=C["BTN"],
                    foreground=C["FG"], borderwidth=0, arrowcolor=C["ACC"],
                    selectbackground=C["BTN"], selectforeground=C["FG"])
        s.map('TCombobox',
              fieldbackground=[('readonly', C["BTN"])],
              foreground=[('readonly', C["FG"])],
              background=[('readonly', C["BTN"])])
        s.configure('TCheckbutton', background=C["BG"], foreground=C["FG"],
                    font=('Segoe UI', 10))
        s.map('TCheckbutton', background=[('active', C["BG"])])
        s.configure('Horizontal.TProgressbar',
                    background=C["ACC"], troughcolor=C["BORDER"],
                    bordercolor=C["BG"], lightcolor=C["ACC2"], darkcolor=C["ACC"])
        s.configure('Won.Horizontal.TProgressbar',
                    background=C["GRN"], troughcolor=C["BORDER"])
        s.configure('Stat.TLabel',    background=C["CARD"], foreground=C["FG2"],
                    font=('Segoe UI', 9))
        s.configure('StatVal.TLabel', background=C["CARD"], foreground=C["GRN"],
                    font=('Segoe UI', 9, 'bold'))
        self.root.option_add('*TCombobox*Listbox.background', C["BTN"])
        self.root.option_add('*TCombobox*Listbox.foreground', C["FG"])
        self.root.option_add('*TCombobox*Listbox.selectBackground', C["ACC"])
        self.root.option_add('*TCombobox*Listbox.selectForeground', "white")

        s.configure('Treeview', background=C["BLK"], foreground=C["FG"], fieldbackground=C["BLK"])
        s.configure('Treeview.Heading', background=C["BTN"], foreground=C["FG"], fieldbackground=C["BTN"])

        s.configure('Treeview', 
                    background=C["BLK"], 
                    foreground=C["FG"], 
                    fieldbackground=C["BLK"])
        
        s.configure('Treeview.Heading', 
                    background=C["BTN"], 
                    foreground=C["FG"])

    def setup_ui(self):
        BG = C["BG"]

        tk.Frame(self.root, bg=C["ACC"], height=3).pack(fill='x')

        nav = tk.Frame(self.root, bg=C["PANEL"], height=46)
        nav.pack(fill='x')
        nav.pack_propagate(False)

        tk.Label(nav, text="𝜹 Tor Client 1.1.0 Beta",
                 font=('Segoe UI', 11, 'bold'), bg=C["PANEL"], fg=C["ACC"]).pack(
                 side='left', padx=18)

        for txt, cmd in [
            ("📖 Help",     self.show_help_window),
            ("⚙ Settings", self.show_settings_window),
        ]:
            tk.Button(nav, text=txt, command=cmd,
                      bg=C["PANEL"], fg=C["FG2"],
                      font=('Segoe UI', 9), relief="flat", cursor="hand2",
                      activebackground=C["BTN"],
                      activeforeground=C["FG"],
                      bd=0, padx=12
                      ).pack(side='right', fill='y')
            
        tk.Button(nav, text="Visit GitHub Page", 
                command=self.open_github_project,
                bg="#251A4D", fg=C["CYAN"], 
                font=('Segoe UI', 9, 'bold'), relief="flat", cursor="hand2",
                activebackground="#34246B", activeforeground="white",
                bd=0, padx=16
                ).pack(side='right', fill='y')

        status_bar = tk.Frame(self.root, bg=C["CARD"], height=32)
        status_bar.pack(fill='x')
        status_bar.pack_propagate(False)
        self._status_dot = tk.Label(status_bar, text="●",
                                    font=('Segoe UI', 10), bg=C["CARD"], fg=C["RED"])
        self._status_dot.pack(side='left', padx=(16, 4))
        tk.Label(status_bar, textvariable=self.status_var,
                 font=('Segoe UI', 9), bg=C["CARD"], fg=C["FG2"]).pack(side='left')

        self._dl_outer = tk.Frame(self.root, bg=C["BG"])
        dl_hdr = tk.Frame(self._dl_outer, bg=C["BG"])
        dl_hdr.pack(fill='x', padx=20)
        self._dl_title_lbl = tk.Label(dl_hdr, text="", bg=C["BG"], fg=C["FG2"], font=('Segoe UI', 8))
        self._dl_title_lbl.pack(side='left')
        self._dl_pct_lbl = tk.Label(dl_hdr, text="", bg=C["BG"], fg=C["GRN"],
                                    font=('Segoe UI', 8, 'bold'))
        self._dl_pct_lbl.pack(side='right')
        self._dl_speed_lbl = tk.Label(dl_hdr, text="", bg=C["BG"], fg=C["CYAN"],
                                      font=('Segoe UI', 8))
        self._dl_speed_lbl.pack(side='right', padx=(10, 0))
        ttk.Progressbar(self._dl_outer, variable=self._dl_bar_var,
                        maximum=100, mode='determinate'
                        ).pack(fill='x', padx=20, pady=(2, 3))

        main = tk.Frame(self.root, bg=BG)
        main.pack(fill='both', expand=True, padx=20, pady=8)

        left = tk.Frame(main, bg=BG)
        left.pack(side='left', fill='both', expand=True)

        cfg_card = tk.Frame(left, bg=C["PANEL"], bd=0)
        cfg_card.pack(fill='x', pady=(0, 6))
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
                                    font=('Segoe UI', 8), relief="flat", cursor="hand2",
                                    activebackground=C["BTN2"])
        self.update_btn.pack(side='right')

        def _lbl_combo(parent, text, var, values, width=22, command=None):
            row = tk.Frame(parent, bg=C["PANEL"])
            row.pack(fill='x', pady=3)
            tk.Label(row, text=text, width=13, anchor='w',
                     bg=C["PANEL"], fg=C["FG2"],
                     font=('Segoe UI', 9)).pack(side='left')
            cb = ttk.Combobox(row, textvariable=var, values=values,
                              state="readonly", width=width)
            cb.pack(side='left', fill='x', expand=True)
            if command:
                cb.bind("<<ComboboxSelected>>", command)
            return cb

        _lbl_combo(cfg_inner, "Source:", self.source_var,
                   ["Default (Built-in)", "Delta-Kronecker Tor-Bridges-Collector", "Custom Bridges"],
                   command=self.on_source_changed)

        self.cat_row = tk.Frame(cfg_inner, bg=C["PANEL"])
        self.cat_row.pack(fill='x', pady=3)
        tk.Label(self.cat_row, text="Category:", width=13, anchor='w',
                 bg=C["PANEL"], fg=C["FG2"],
                 font=('Segoe UI', 9)).pack(side='left')
        self.cat_combo = ttk.Combobox(self.cat_row, textvariable=self.cat_var,
                                      values=["Tested & Active", "Fresh (72h)", "Full Archive"],
                                      state="readonly", width=22)
        self.cat_combo.pack(side='left', fill='x', expand=True)
        self.cat_combo.bind("<<ComboboxSelected>>", self._on_bridge_selection_change)

        self.trans_combo = _lbl_combo(cfg_inner, "Transport:", self.trans_var,
                                      [], command=self._on_bridge_selection_change)

        self.ip_combo = _lbl_combo(cfg_inner, "IP Version:", self.ip_var,
                                   ["Both", "IPv4", "IPv6"],
                                   command=self._on_bridge_selection_change)

        nb_row = tk.Frame(cfg_inner, bg=C["PANEL"])
        nb_row.pack(fill='x', pady=3)
        ttk.Checkbutton(nb_row, text="Connect without bridge  (direct Tor)",
                        variable=self.no_bridge_var,
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
        btn_card.pack(fill='x', pady=(0, 6))
        tk.Frame(btn_card, bg=C["ACC"], height=2).pack(fill='x')
        btn_inner = tk.Frame(btn_card, bg=C["PANEL"])
        btn_inner.pack(fill='x', padx=14, pady=10)

        r1 = tk.Frame(btn_inner, bg=C["PANEL"])
        r1.pack(fill='x', pady=(0, 4))
        for i in range(3):
            r1.columnconfigure(i, weight=1)

        self.auto_btn = tk.Button(r1, text="⚡ Auto Connect",
                                  command=self.start_auto_connect,
                                  bg=C["BTN2"], fg=C["FG"],
                                  font=('Segoe UI', 10, 'bold'),
                                  relief="flat", cursor="hand2",
                                  activebackground=C["BTN"])
        self.auto_btn.grid(row=0, column=0, padx=(0, 3), sticky="ew", ipady=7)

        self.start_btn = tk.Button(r1, text="▶ Start",
                                   command=self.start_tor_thread,
                                   bg=C["BTN2"], fg=C["FG"],
                                   font=('Segoe UI', 10, 'bold'),
                                   relief="flat", cursor="hand2",
                                   activebackground=C["BTN"])
        self.start_btn.grid(row=0, column=1, padx=3, sticky="ew", ipady=7)

        self.stop_btn = tk.Button(r1, text="■ Stop",
                                  command=self.stop_tor,
                                  bg=C["BTN"], fg=C["FG2"],
                                  font=('Segoe UI', 10, 'bold'),
                                  relief="flat", cursor="hand2",
                                  activebackground=C["BTN2"])
        self.stop_btn.grid(row=0, column=2, padx=(3, 0), sticky="ew", ipady=7)

        r2_3 = tk.Frame(btn_inner, bg=C["PANEL"])
        r2_3.pack(fill='x', pady=(0, 4))
        r2_3.columnconfigure(0, weight=1)
        r2_3.columnconfigure(1, weight=1)

        tk.Button(r2_3, text="🔍 Bridge Scanner",
                  command=self.show_bridge_scanner,
                  bg=C["BTN"], fg=C["FG"],
                  font=('Segoe UI', 9, 'bold'),
                  relief="flat", cursor="hand2",
                  activebackground=C["BTN2"]
                  ).grid(row=0, column=0, padx=(0, 3), pady=2, sticky="ew", ipady=6)

        self.multi_btn = tk.Button(r2_3, text="⚡ Multi-Connect",
                                   command=self.show_parallel_connect,
                                   bg=C["ACC"], fg="white",
                                   font=('Segoe UI', 9, 'bold'),
                                   relief="flat", cursor="hand2",
                                   activebackground=C["ACC2"])
        self.multi_btn.grid(row=0, column=1, padx=(3, 0), pady=2, sticky="ew", ipady=6)

        self.test_btn_top = tk.Button(r2_3, text="🔍 Test Connection",
                                  command=self.start_test_connection,
                                  bg=C["BTN"], fg=C["FG"],
                                  font=('Segoe UI', 9, 'bold'),
                                  relief="flat", cursor="hand2",
                                  activebackground=C["BTN2"])
        self.test_btn_top.grid(row=1, column=0, padx=(0, 3), pady=2, sticky="ew", ipady=6)

        self.newnym_btn = tk.Button(r2_3, text="↻ New Circuit",
                                    command=self.request_new_circuit,
                                    bg=C["BTN"], fg=C["FG"],
                                    font=('Segoe UI', 9, 'bold'),
                                    relief="flat", cursor="hand2",
                                    activebackground=C["BTN2"])
        self.newnym_btn.grid(row=1, column=1, padx=(3, 0), pady=2, sticky="ew", ipady=6)

        r4 = tk.Frame(btn_inner, bg=C["PANEL"])
        r4.pack(fill='x', pady=(0, 0))

        self.proxy_btn = tk.Button(r4,
                                   text="🌐  System Proxy   ●  OFF",
                                   command=self.toggle_proxy_button,
                                   bg=C["BTN"], fg=C["FG2"],
                                   font=('Segoe UI', 10, 'bold'),
                                   relief="flat", cursor="hand2",
                                   activebackground=C["BTN2"])
        self.proxy_btn.pack(fill='x', ipady=7)

        prog_card = tk.Frame(left, bg=C["PANEL"])
        prog_card.pack(fill='x', pady=(0, 6))
        tk.Frame(prog_card, bg=C["ACC"], height=2).pack(fill='x')
        prog_inner = tk.Frame(prog_card, bg=C["PANEL"])
        prog_inner.pack(fill='x', padx=14, pady=8)
        prog_hdr = tk.Frame(prog_inner, bg=C["PANEL"])
        prog_hdr.pack(fill='x')
        tk.Label(prog_hdr, text="Connection Progress",
                 font=('Segoe UI', 9, 'bold'), bg=C["PANEL"], fg=C["FG"]).pack(side='left')
        tk.Label(prog_hdr, textvariable=self.conn_pct_var,
                 font=('Segoe UI', 9, 'bold'), bg=C["PANEL"], fg=C["FG"]).pack(side='right')
        ttk.Progressbar(prog_inner, variable=self.conn_progress_var,
                        maximum=100, mode='determinate'
                        ).pack(fill='x', pady=(4, 0))

        stats_card = tk.Frame(left, bg=C["PANEL"])
        stats_card.pack(fill='x', pady=(0, 6))
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
        log_card.pack(fill='both', expand=True)
        tk.Frame(log_card, bg=C["ACC"], height=2).pack(fill='x')
        log_header = tk.Frame(log_card, bg=C["PANEL"])
        log_header.pack(fill='x', padx=14, pady=(6, 4))
        tk.Label(log_header, text="Tor Logs",
                 font=('Segoe UI', 9, 'bold'), bg=C["PANEL"], fg=C["FG"]).pack(side='left')
        tk.Label(log_header,
                 text="● white=log  ● notice=connected  ● warn=warning  ● err=error",
                 font=('Segoe UI', 7), bg=C["PANEL"], fg=C["FG2"]).pack(side='right')

        log_frame = tk.Frame(log_card, bg=C["BLK"])
        log_frame.pack(fill='both', expand=True, padx=0, pady=0)

        self.log_text = tk.Text(log_frame, font=('Consolas', 8), wrap='word',
                                state='disabled', bg=C["BLK"], fg=C["FG2"],
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
        self.cat_combo.configure(state=state)
        self.trans_combo.configure(state=state)
        self.ip_combo.configure(state=state)

    def show_custom_bridge_window(self):
        def _on_save(new_cfg):
            self.cfg.update(new_cfg)
            save_config(self.cfg, self.extract_dir)
        CustomBridgeWindow(self.root, self.cfg, _on_save)

    def show_bridge_scanner(self):
        BridgeScannerWindow(self.root, self.bridges_dir, self.get_safe_filename)

    def show_parallel_connect(self):
        def _on_connected(label, socks_port, ctrl_port, http_port):
            self.append_log(
                f"[Parallel] Winner: {label}  SOCKS:{socks_port}  HTTP:{http_port}\n", "auto")
        ParallelConnectWindow(
            self.root, self.extract_dir, self.bridges_dir,
            self.get_safe_filename, self.generate_torrc,
            self.cfg, _on_connected)

    def show_settings_window(self):
        def _on_save(new_cfg):
            self.cfg.update(new_cfg)
            if self.tor_connected:
                self._restart_keepalive()
                self._restart_watchdog()
        SettingsWindow(self.root, self.cfg, _on_save, on_clear_data=self._clear_data_dir)

    def _show_dl(self, title="Downloading…"):
        self._dl_title_lbl.configure(text=title)
        self._dl_pct_lbl.configure(text="0%")
        self._dl_bar_var.set(0)
        if not self._dl_outer.winfo_ismapped():
            self._dl_outer.pack(fill='x', after=self.root.winfo_children()[1])

    def _set_dl(self, pct, title=None, speed=None):
        self._dl_bar_var.set(pct)
        self._dl_pct_lbl.configure(text=f"{pct}%")
        if title:
            self._dl_title_lbl.configure(text=title)
        if speed:
            self._dl_speed_lbl.configure(text=speed)

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
                text="🌐  System Proxy   ●  ON",
                bg="#0E2A1A", fg=C["GRN"],
                activebackground="#163A22", activeforeground=C["GRN"])
        else:
            self.proxy_btn.configure(
                text="🌐  System Proxy   ●  OFF",
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
            self.trans_combo['values'] = opts
            if self.trans_var.get() not in opts:
                self.trans_var.set("obfs4")
            self.show_custom_bridge_window()
        else:
            try: self.cat_row.pack_forget()
            except Exception: pass
            opts = ["obfs4", "snowflake", "meek"]
            self.trans_combo['values'] = opts
            if self.trans_var.get() not in opts:
                self.trans_var.set("obfs4")
        self._refresh_bridge_info()

    def update_transports(self, event=None):
        src = self.source_var.get()
        if src == "Default (Built-in)":
            opts = ["obfs4", "snowflake", "meek"]
        elif src == "Custom Bridges":
            opts = ["obfs4", "webtunnel", "vanilla"]
        else:
            opts = ["obfs4", "webtunnel", "vanilla"]
        self.trans_combo['values'] = opts
        if self.trans_var.get() not in opts:
            self.trans_var.set(opts[0])

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
        self.auto_btn.configure(text="⏹ Stop Auto", command=self.stop_auto_connect,
                                bg="#3A1010", fg=C["RED"])
        self._auto_connect_active = True
        threading.Thread(target=self._run_auto_connect, daemon=True).start()

    def stop_auto_connect(self):
        self._auto_connect_active = False
        self.stop_tor()
        self.auto_btn.configure(text="⚡ Auto Connect", command=self.start_auto_connect,
                                bg=C["BTN2"], fg=C["FG"])

    def _run_auto_connect(self):
        if self.no_bridge_var.get():
            self.root.after(0, self.append_log,
                            "\n[Auto] No-bridge mode: connecting directly to Tor network.\n", "auto")
            if self._try_bridge_config(None, None, None, no_bridge=True):
                self.root.after(0, self.append_log, "[Auto] ✅ Connected (no bridge)\n", "auto")
                self.root.after(0, self.auto_btn.configure,
                                {"text": "⚡ Auto Connect", "command": self.start_auto_connect,
                                 "bg": C["BTN2"], "fg": C["FG"]})
                return
            self.root.after(0, self.append_log, "[Auto] ❌ Direct connection failed.\n", "auto")
            self._auto_connect_active = False
            self.root.after(0, self.auto_btn.configure,
                            {"text": "⚡ Auto Connect", "command": self.start_auto_connect,
                             "bg": C["BTN2"], "fg": C["FG"]})
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
                self.root.after(0, self.auto_btn.configure,
                                {"text": "⚡ Auto Connect",
                                 "command": self.start_auto_connect,
                                 "bg": C["BTN2"], "fg": C["FG"]})
                return
            if not self._auto_connect_active:
                self.root.after(0, self.auto_btn.configure,
                                {"text": "⚡ Auto Connect",
                                 "command": self.start_auto_connect,
                                 "bg": C["BTN2"], "fg": C["FG"]})
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
                self.root.after(0, self.auto_btn.configure,
                                {"text": "⚡ Auto Connect",
                                 "command": self.start_auto_connect,
                                 "bg": C["BTN2"], "fg": C["FG"]})
                return

        if self._auto_connect_active:
            self.root.after(0, self.update_status,
                            "Auto-connect failed. Try updating bridges or manual settings.")
            self.root.after(0, self.append_log, "[Auto] ❌ All bridge groups exhausted.\n")
        self._auto_connect_active = False
        self.root.after(0, self.auto_btn.configure,
                        {"text": "⚡ Auto Connect",
                         "command": self.start_auto_connect,
                         "bg": C["BTN2"], "fg": C["FG"]})

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
            bg="#3A1010", fg=C["RED"],
            activebackground="#4A1515", activeforeground="white"))

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
                    self._notify("Tor Client", "✅ Tor is fully connected!")
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
                bg="#3A1010", fg=C["RED"],
                activebackground="#4A1515", activeforeground="white"))

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
            bg=C["BTN"], fg=C["FG2"],
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
        self._update_fresh_bridges_parallel()
        self.root.after(0, self.update_status, "Ready.")
        self.root.after(0, self._refresh_bridge_info)

    def setup_tor(self):
        tor_exe = os.path.join(self.extract_dir, "tor", "tor.exe")
        if os.path.exists(tor_exe):
            return
        archive = os.path.join(BASE_DIR, "tor-expert-bundle-windows-x86_64-15.0.14.tar.gz")
        if not os.path.exists(archive):
            self.root.after(0, self.update_status, "Error: tor-expert-bundle.tar.gz missing!")
            self.root.after(0, lambda: messagebox.showerror("Error", "tor-expert-bundle.tar.gz not found in application directory!"))
            return
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
        w.title("Help — Tor Client")
        w.geometry("720x660")
        w.configure(bg=C["BG"])
        w.resizable(False, False)
        w.update()
        apply_dark_titlebar(w)
        set_window_icon(w)
        self._apply_icon_to(w)

        tk.Frame(w, bg=C["ACC"], height=3).pack(fill='x')
        tk.Label(w, text="⬡  How to Use — Tor Client",
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

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
⚠️  TROUBLESHOOTING
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
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
    root.configure(bg="#1A1D24")
    app = TorClientGUI(root)
    root.after(1000, root.deiconify)
    root.mainloop()
