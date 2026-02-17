import ctypes
from ctypes import wintypes
import json
import os
import re
import subprocess
import sys
import threading
import time
import urllib.parse
from pathlib import Path
from tkinter import BOTH, CENTER, LEFT, Canvas, Frame, Label, Tk

# ---------------------------
# Paths and constants
# ---------------------------
APP_DIR = Path(__file__).resolve().parent
CONFIG_DIR = APP_DIR / "config"
RUNTIME_DIR = APP_DIR / "runtime"
CORE_DIR = APP_DIR / "core"
PROFILE_PATH = CONFIG_DIR / "profile.json"
STATE_PATH = CONFIG_DIR / "state.json"
XRAY_EXE = CORE_DIR / "xray.exe"
ACTIVE_CONFIG_PATH = RUNTIME_DIR / "active-config.json"

WINDOW_WIDTH = 250
WINDOW_HEIGHT = 500
LOCAL_SOCKS_PORT = 10808
LOCAL_HTTP_PORT = 10809
API_PORT = 10085

STATUS_OFF = "Отключен"

# WinAPI constants
NIM_ADD = 0x00000000
NIM_MODIFY = 0x00000001
NIM_DELETE = 0x00000002
NIF_MESSAGE = 0x00000001
NIF_ICON = 0x00000002
NIF_TIP = 0x00000004
WM_USER = 0x0400
WM_DESTROY = 0x0002
WM_COMMAND = 0x0111
WM_LBUTTONUP = 0x0202
WM_RBUTTONUP = 0x0205
WM_CLOSE = 0x0010
MF_STRING = 0x0000
MF_SEPARATOR = 0x0800
TPM_LEFTALIGN = 0x0000
TPM_RIGHTBUTTON = 0x0002
IDI_APPLICATION = 32512
IMAGE_ICON = 1
LR_SHARED = 0x00008000

SPI_SETINTERNETOPTION = 39
INTERNET_OPTION_SETTINGS_CHANGED = 39
INTERNET_OPTION_REFRESH = 37


class NOTIFYICONDATAW(ctypes.Structure):
    _fields_ = [
        ("cbSize", ctypes.c_uint32),
        ("hWnd", ctypes.c_void_p),
        ("uID", ctypes.c_uint32),
        ("uFlags", ctypes.c_uint32),
        ("uCallbackMessage", ctypes.c_uint32),
        ("hIcon", ctypes.c_void_p),
        ("szTip", ctypes.c_wchar * 128),
        ("dwState", ctypes.c_uint32),
        ("dwStateMask", ctypes.c_uint32),
        ("szInfo", ctypes.c_wchar * 256),
        ("uTimeoutOrVersion", ctypes.c_uint32),
        ("szInfoTitle", ctypes.c_wchar * 64),
        ("dwInfoFlags", ctypes.c_uint32),
        ("guidItem", ctypes.c_byte * 16),
        ("hBalloonIcon", ctypes.c_void_p),
    ]


def ensure_dirs() -> None:
    CONFIG_DIR.mkdir(parents=True, exist_ok=True)
    RUNTIME_DIR.mkdir(parents=True, exist_ok=True)
    if not PROFILE_PATH.exists():
        template = {
            "vless_uri": "vless://UUID@server:443?encryption=none&security=reality&sni=example.com&fp=chrome&pbk=PUBLIC_KEY&type=tcp#MyServer"
        }
        PROFILE_PATH.write_text(json.dumps(template, indent=2, ensure_ascii=False), encoding="utf-8")


def load_json(path: Path, default: dict) -> dict:
    if not path.exists():
        return default
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except Exception:
        return default


def save_json(path: Path, data: dict) -> None:
    path.write_text(json.dumps(data, indent=2, ensure_ascii=False), encoding="utf-8")


def parse_vless_uri(uri: str) -> dict:
    parsed = urllib.parse.urlparse(uri)
    if parsed.scheme.lower() != "vless":
        raise ValueError("Поддерживается только схема vless://")

    user_id = urllib.parse.unquote(parsed.username or "")
    host = parsed.hostname
    port = parsed.port or 443
    if not user_id or not host:
        raise ValueError("В VLESS URI отсутствует UUID или host")

    query = urllib.parse.parse_qs(parsed.query)
    q = {k: v[-1] for k, v in query.items() if v}

    outbound = {
        "tag": "proxy",
        "protocol": "vless",
        "settings": {
            "vnext": [
                {
                    "address": host,
                    "port": port,
                    "users": [
                        {
                            "id": user_id,
                            "encryption": q.get("encryption", "none"),
                            "flow": q.get("flow", ""),
                        }
                    ],
                }
            ]
        },
        "streamSettings": {
            "network": q.get("type", "tcp"),
            "security": q.get("security", "none"),
        },
    }

    stream = outbound["streamSettings"]
    net = stream["network"]
    sec = stream["security"]

    if "sni" in q:
        stream["serverName"] = q["sni"]
    if "alpn" in q:
        stream["alpn"] = [x.strip() for x in q["alpn"].split(",") if x.strip()]
    if "fp" in q:
        stream["fingerprint"] = q["fp"]

    if sec == "reality":
        reality = {}
        if "pbk" in q:
            reality["publicKey"] = q["pbk"]
        if "sid" in q:
            reality["shortId"] = q["sid"]
        if "spx" in q:
            reality["spiderX"] = q["spx"]
        stream["realitySettings"] = reality

    if sec == "tls":
        tls = {}
        if "sni" in q:
            tls["serverName"] = q["sni"]
        if "alpn" in q:
            tls["alpn"] = [x.strip() for x in q["alpn"].split(",") if x.strip()]
        if "fp" in q:
            tls["fingerprint"] = q["fp"]
        if "allowInsecure" in q:
            tls["allowInsecure"] = q["allowInsecure"].lower() == "true"
        stream["tlsSettings"] = tls

    if net == "ws":
        stream["wsSettings"] = {
            "path": q.get("path", "/"),
            "headers": {"Host": q.get("host", "")},
        }
    elif net == "grpc":
        stream["grpcSettings"] = {
            "serviceName": q.get("serviceName", q.get("service", "")),
            "multiMode": q.get("mode", "").lower() == "multi",
        }
    elif net in ("http", "h2"):
        stream["httpSettings"] = {
            "host": [q["host"]] if "host" in q else [],
            "path": q.get("path", "/"),
        }
    elif net == "kcp":
        stream["kcpSettings"] = {
            "header": {"type": q.get("headerType", "none")},
            "seed": q.get("seed", ""),
        }
    elif net == "quic":
        stream["quicSettings"] = {
            "security": q.get("quicSecurity", "none"),
            "key": q.get("key", ""),
            "header": {"type": q.get("headerType", "none")},
        }
    elif net in ("xhttp", "splithttp"):
        stream["xhttpSettings"] = {
            "path": q.get("path", "/"),
            "host": q.get("host", ""),
            "mode": q.get("mode", "auto"),
        }

    return outbound


def build_xray_config(profile: dict, tun_enabled: bool) -> dict:
    if "outbound" in profile and isinstance(profile["outbound"], dict):
        outbound = profile["outbound"]
        if "tag" not in outbound:
            outbound["tag"] = "proxy"
    elif "vless_uri" in profile:
        outbound = parse_vless_uri(profile["vless_uri"])
    else:
        raise ValueError("В profile.json нужен ключ outbound (объект) или vless_uri (строка)")

    inbounds = [
        {
            "tag": "socks-in",
            "port": LOCAL_SOCKS_PORT,
            "listen": "127.0.0.1",
            "protocol": "socks",
            "settings": {"udp": True},
        },
        {
            "tag": "http-in",
            "port": LOCAL_HTTP_PORT,
            "listen": "127.0.0.1",
            "protocol": "http",
            "settings": {},
        },
        {
            "tag": "api",
            "listen": "127.0.0.1",
            "port": API_PORT,
            "protocol": "dokodemo-door",
            "settings": {"address": "127.0.0.1"},
        },
    ]

    if tun_enabled:
        inbounds.append(
            {
                "tag": "tun-in",
                "protocol": "tun",
                "settings": {
                    "name": "xray-tun",
                    "mtu": 1500,
                    "stack": "system",
                    "autoRoute": True,
                    "strictRoute": True,
                },
            }
        )

    config = {
        "log": {
            "loglevel": "warning",
            "access": str((RUNTIME_DIR / "access.log").resolve()),
            "error": str((RUNTIME_DIR / "error.log").resolve()),
        },
        "api": {
            "tag": "api",
            "services": [
                "StatsService",
            ],
        },
        "stats": {},
        "policy": {
            "system": {
                "statsOutboundUplink": True,
                "statsOutboundDownlink": True,
            }
        },
        "inbounds": inbounds,
        "outbounds": [
            outbound,
            {"tag": "direct", "protocol": "freedom"},
            {"tag": "block", "protocol": "blackhole"},
        ],
        "routing": {
            "domainStrategy": "AsIs",
            "rules": [
                {
                    "type": "field",
                    "inboundTag": ["api"],
                    "outboundTag": "direct",
                }
            ],
        },
    }
    return config


def run_cmd(cmd: list[str], timeout: float = 8.0) -> tuple[int, str, str]:
    try:
        proc = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout, creationflags=0x08000000)
        return proc.returncode, proc.stdout.strip(), proc.stderr.strip()
    except Exception as exc:
        return 1, "", str(exc)


def set_system_proxy(enabled: bool) -> None:
    import winreg

    key_path = r"Software\Microsoft\Windows\CurrentVersion\Internet Settings"
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, key_path, 0, winreg.KEY_SET_VALUE) as key:
        if enabled:
            winreg.SetValueEx(key, "ProxyEnable", 0, winreg.REG_DWORD, 1)
            winreg.SetValueEx(key, "ProxyServer", 0, winreg.REG_SZ, f"127.0.0.1:{LOCAL_HTTP_PORT}")
            winreg.SetValueEx(key, "ProxyOverride", 0, winreg.REG_SZ, "<local>")
        else:
            winreg.SetValueEx(key, "ProxyEnable", 0, winreg.REG_DWORD, 0)

    wininet = ctypes.WinDLL("wininet", use_last_error=True)
    wininet.InternetSetOptionW(0, INTERNET_OPTION_SETTINGS_CHANGED, 0, 0)
    wininet.InternetSetOptionW(0, INTERNET_OPTION_REFRESH, 0, 0)


def create_autostart_task() -> tuple[bool, str]:
    exe = Path(sys.executable).resolve()
    script = Path(__file__).resolve()

    if exe.name.lower().startswith("python"):
        tr = f'"{exe}" "{script}"'
    else:
        tr = f'"{exe}"'

    cmd = [
        "schtasks",
        "/Create",
        "/TN",
        "CorpVPN-Autostart",
        "/SC",
        "ONLOGON",
        "/TR",
        tr,
        "/F",
    ]
    code, out, err = run_cmd(cmd, timeout=10)
    if code == 0:
        return True, out or "Автозапуск добавлен"
    return False, err or out or "Не удалось добавить задачу автозапуска"


def query_xray_stats_kbps() -> float | None:
    if not XRAY_EXE.exists():
        return None
    cmd = [str(XRAY_EXE), "api", "statsquery", "--server", f"127.0.0.1:{API_PORT}"]
    code, out, _ = run_cmd(cmd, timeout=4)
    if code != 0 or not out:
        return None

    up = 0
    down = 0
    for line in out.splitlines():
        m = re.search(r"(outbound>>>proxy>>>traffic>>>(uplink|downlink))\s*[:=]\s*(\d+)", line)
        if not m:
            continue
        if m.group(2) == "uplink":
            up = int(m.group(3))
        else:
            down = int(m.group(3))

    return float(up + down)


class TrayIcon:
    def __init__(self, app: "VPNApp"):
        self.app = app
        self.hwnd = None
        self.hicon = None
        self._register_window()
        self._create_icon()

    def _register_window(self):
        user32 = ctypes.windll.user32
        kernel32 = ctypes.windll.kernel32

        WNDPROCTYPE = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.c_uint, ctypes.c_uint, ctypes.c_long)

        self._wnd_proc_ref = WNDPROCTYPE(self._wnd_proc)

        class WNDCLASS(ctypes.Structure):
            _fields_ = [
                ("style", ctypes.c_uint),
                ("lpfnWndProc", ctypes.c_void_p),
                ("cbClsExtra", ctypes.c_int),
                ("cbWndExtra", ctypes.c_int),
                ("hInstance", ctypes.c_void_p),
                ("hIcon", ctypes.c_void_p),
                ("hCursor", ctypes.c_void_p),
                ("hbrBackground", ctypes.c_void_p),
                ("lpszMenuName", ctypes.c_wchar_p),
                ("lpszClassName", ctypes.c_wchar_p),
            ]

        self.class_name = "CorpVPNTrayWindow"
        h_instance = kernel32.GetModuleHandleW(None)

        wnd_class = WNDCLASS()
        wnd_class.lpfnWndProc = ctypes.cast(self._wnd_proc_ref, ctypes.c_void_p).value
        wnd_class.lpszClassName = self.class_name
        wnd_class.hInstance = h_instance

        user32.RegisterClassW(ctypes.byref(wnd_class))

        self.hwnd = user32.CreateWindowExW(
            0,
            self.class_name,
            "CorpVPNTrayHidden",
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            h_instance,
            0,
        )

    def _create_icon(self):
        user32 = ctypes.windll.user32
        shell32 = ctypes.windll.shell32

        self.hicon = user32.LoadIconW(0, ctypes.c_void_p(IDI_APPLICATION))
        nid = NOTIFYICONDATAW()
        nid.cbSize = ctypes.sizeof(NOTIFYICONDATAW)
        nid.hWnd = self.hwnd
        nid.uID = 1
        nid.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP
        nid.uCallbackMessage = WM_USER + 1
        nid.hIcon = self.hicon
        nid.szTip = "CorpVPN"
        shell32.Shell_NotifyIconW(NIM_ADD, ctypes.byref(nid))

    def remove(self):
        shell32 = ctypes.windll.shell32
        if self.hwnd:
            nid = NOTIFYICONDATAW()
            nid.cbSize = ctypes.sizeof(NOTIFYICONDATAW)
            nid.hWnd = self.hwnd
            nid.uID = 1
            shell32.Shell_NotifyIconW(NIM_DELETE, ctypes.byref(nid))

    def _show_menu(self):
        user32 = ctypes.windll.user32
        menu = user32.CreatePopupMenu()
        user32.AppendMenuW(menu, MF_STRING, 1001, "Открыть")
        user32.AppendMenuW(menu, MF_SEPARATOR, 0, None)
        user32.AppendMenuW(menu, MF_STRING, 1002, "Выход")

        point = wintypes.POINT()
        user32.GetCursorPos(ctypes.byref(point))
        user32.SetForegroundWindow(self.hwnd)
        user32.TrackPopupMenu(menu, TPM_LEFTALIGN | TPM_RIGHTBUTTON, point.x, point.y, 0, self.hwnd, None)
        user32.DestroyMenu(menu)

    def _wnd_proc(self, hwnd, msg, wparam, lparam):
        user32 = ctypes.windll.user32
        if msg == WM_USER + 1:
            if lparam == WM_LBUTTONUP:
                self.app.show_window()
            elif lparam == WM_RBUTTONUP:
                self._show_menu()
            return 0
        if msg == WM_COMMAND:
            cmd = wparam & 0xFFFF
            if cmd == 1001:
                self.app.show_window()
            elif cmd == 1002:
                self.app.shutdown_from_tray()
            return 0
        if msg == WM_DESTROY:
            user32.PostQuitMessage(0)
            return 0
        return user32.DefWindowProcW(hwnd, msg, wparam, lparam)


class VPNApp:
    def __init__(self):
        ensure_dirs()
        self.state = load_json(STATE_PATH, {"autostart_done": False, "tun_enabled": False})

        self.root = Tk()
        self.root.title("CorpVPN")
        self.root.geometry(f"{WINDOW_WIDTH}x{WINDOW_HEIGHT}")
        self.root.configure(bg="#2e2e2e")
        self.root.overrideredirect(True)
        self.root.attributes("-topmost", False)

        self.drag_x = 0
        self.drag_y = 0

        self.connected = False
        self.xray_proc: subprocess.Popen | None = None
        self.last_total_bytes: float | None = None
        self.last_sample_time: float | None = None
        self.status_text = "Готов"

        self._build_ui()

        self.tray = TrayIcon(self)

        self.root.protocol("WM_DELETE_WINDOW", self.on_close_click)
        self.root.bind("<Escape>", lambda _: self.on_close_click())

        self.stats_stop = False
        self.stats_thread = threading.Thread(target=self._stats_loop, daemon=True)
        self.stats_thread.start()

    def _build_ui(self):
        title = Frame(self.root, bg="#3a3a3a", height=32)
        title.pack(fill="x")

        title.bind("<ButtonPress-1>", self._start_drag)
        title.bind("<B1-Motion>", self._on_drag)

        Label(title, text="CorpVPN", fg="#f2f2f2", bg="#3a3a3a").pack(side=LEFT, padx=8)

        btn_close = Label(title, text="✕", fg="#f2f2f2", bg="#3a3a3a", width=3, cursor="hand2")
        btn_close.pack(side="right")
        btn_close.bind("<Button-1>", lambda _: self.on_close_click())

        btn_min = Label(title, text="_", fg="#f2f2f2", bg="#3a3a3a", width=3, cursor="hand2")
        btn_min.pack(side="right")
        btn_min.bind("<Button-1>", lambda _: self.hide_to_tray())

        content = Frame(self.root, bg="#2e2e2e")
        content.pack(fill=BOTH, expand=True)

        self.power_canvas = Canvas(content, width=120, height=120, bg="#2e2e2e", highlightthickness=0)
        self.power_canvas.place(relx=0.5, rely=0.35, anchor=CENTER)
        self.power_canvas.bind("<Button-1>", lambda _: self.toggle_connection())

        self.tun_canvas = Canvas(content, width=110, height=40, bg="#2e2e2e", highlightthickness=0)
        self.tun_canvas.place(relx=0.5, rely=0.55, anchor=CENTER)
        self.tun_canvas.bind("<Button-1>", lambda _: self.toggle_tun())

        self.tun_label = Label(content, text="TUN: OFF", fg="#d0d0d0", bg="#2e2e2e")
        self.tun_label.place(relx=0.5, rely=0.62, anchor=CENTER)

        self.status_label = Label(content, text=STATUS_OFF, fg="#e8e8e8", bg="#2e2e2e", wraplength=220, justify=CENTER)
        self.status_label.place(relx=0.5, rely=0.75, anchor=CENTER)

        self.speed_label = Label(content, text="0.00 Kbps", fg="#aaaaaa", bg="#2e2e2e")
        self.speed_label.place(relx=0.5, rely=0.81, anchor=CENTER)

        self._draw_power_button()
        self._draw_tun_switch()

    def _draw_power_button(self):
        self.power_canvas.delete("all")
        color = "#1fa24a" if self.connected else "#b93333"
        self.power_canvas.create_oval(8, 8, 112, 112, fill=color, outline="#202020", width=3)
        self.power_canvas.create_text(60, 62, text="⏻", fill="white", font=("Segoe UI Symbol", 34, "bold"))

    def _draw_tun_switch(self):
        self.tun_canvas.delete("all")
        enabled = bool(self.state.get("tun_enabled", False))
        bg = "#1fa24a" if enabled else "#6a6a6a"
        self.tun_canvas.create_rectangle(10, 10, 100, 30, fill=bg, outline=bg, width=1)
        knob_x = 84 if enabled else 26
        self.tun_canvas.create_oval(knob_x - 12, 8, knob_x + 12, 32, fill="#f0f0f0", outline="#f0f0f0")
        self.tun_label.config(text=f"TUN: {'ON' if enabled else 'OFF'}")

    def _start_drag(self, event):
        self.drag_x = event.x
        self.drag_y = event.y

    def _on_drag(self, event):
        x = event.x_root - self.drag_x
        y = event.y_root - self.drag_y
        self.root.geometry(f"+{x}+{y}")

    def hide_to_tray(self):
        self.root.withdraw()

    def show_window(self):
        self.root.deiconify()
        self.root.lift()
        self.root.focus_force()

    def set_status(self, text: str):
        self.status_text = text
        self.status_label.config(text=text)

    def toggle_tun(self):
        self.state["tun_enabled"] = not bool(self.state.get("tun_enabled", False))
        save_json(STATE_PATH, self.state)
        self._draw_tun_switch()

        if self.connected:
            self.set_status("Перезапуск для применения TUN...")
            self.disconnect()
            ok, msg = self.connect()
            self.set_status(msg)
            self._draw_power_button()

    def toggle_connection(self):
        if self.connected:
            self.disconnect()
            self.set_status("Отключен")
        else:
            ok, msg = self.connect()
            self.set_status(msg)
        self._draw_power_button()

    def connect(self) -> tuple[bool, str]:
        if not XRAY_EXE.exists():
            return False, f"Не найден {XRAY_EXE}"

        profile = load_json(PROFILE_PATH, {})
        try:
            config = build_xray_config(profile, bool(self.state.get("tun_enabled", False)))
        except Exception as exc:
            return False, f"Ошибка profile.json: {exc}"

        ACTIVE_CONFIG_PATH.write_text(json.dumps(config, indent=2, ensure_ascii=False), encoding="utf-8")

        cmd = [str(XRAY_EXE), "run", "-c", str(ACTIVE_CONFIG_PATH)]
        try:
            self.xray_proc = subprocess.Popen(
                cmd,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                creationflags=0x08000000,
            )
        except Exception as exc:
            return False, f"Не удалось запустить xray: {exc}"

        time.sleep(0.8)
        if self.xray_proc.poll() is not None:
            return False, "xray завершился сразу после запуска"

        try:
            set_system_proxy(True)
        except Exception as exc:
            self.disconnect()
            return False, f"Не удалось включить системный прокси: {exc}"

        self.connected = True
        self.last_total_bytes = None
        self.last_sample_time = None

        if not self.state.get("autostart_done", False):
            ok, _ = create_autostart_task()
            if ok:
                self.state["autostart_done"] = True
                save_json(STATE_PATH, self.state)

        return True, "Подключено"

    def disconnect(self):
        try:
            set_system_proxy(False)
        except Exception:
            pass

        proc = self.xray_proc
        self.xray_proc = None

        if proc is not None:
            try:
                proc.terminate()
                proc.wait(timeout=2)
            except Exception:
                try:
                    proc.kill()
                except Exception:
                    pass

        self.connected = False
        self.last_total_bytes = None
        self.last_sample_time = None
        self.speed_label.config(text="0.00 Kbps")

    def on_close_click(self):
        self.shutdown()

    def shutdown_from_tray(self):
        self.shutdown()

    def shutdown(self):
        self.stats_stop = True
        self.disconnect()
        try:
            self.tray.remove()
        except Exception:
            pass
        self.root.after(100, self.root.destroy)

    def _stats_loop(self):
        while not self.stats_stop:
            time.sleep(1.0)
            if not self.connected:
                continue
            total = query_xray_stats_kbps()
            now = time.time()

            if total is None:
                self.root.after(0, lambda: self.speed_label.config(text="0.00 Kbps"))
                continue

            if self.last_total_bytes is None or self.last_sample_time is None:
                self.last_total_bytes = total
                self.last_sample_time = now
                self.root.after(0, lambda: self.speed_label.config(text="0.00 Kbps"))
                continue

            delta_bytes = max(0.0, total - self.last_total_bytes)
            delta_time = max(0.001, now - self.last_sample_time)
            kbps = (delta_bytes * 8.0 / 1000.0) / delta_time

            self.last_total_bytes = total
            self.last_sample_time = now

            self.root.after(0, lambda value=kbps: self.speed_label.config(text=f"{value:.2f} Kbps"))

    def run(self):
        self.root.mainloop()


if __name__ == "__main__":
    app = VPNApp()
    app.run()
