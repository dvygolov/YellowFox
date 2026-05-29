#!/usr/bin/env python3
import contextlib
import copy
import importlib.util
import json
import os
import re
import subprocess
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlparse

import websocket

try:
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
except Exception:
    pass


def load_launcher():
    path = Path(__file__).with_name("camoufox-server.py")
    spec = importlib.util.spec_from_file_location("yellowfox_camoufox_server", path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Cannot load launcher module from {path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def read_config():
    if len(sys.argv) > 1:
        with open(sys.argv[1], "r", encoding="utf-8") as handle:
            return json.load(handle)
    return json.load(sys.stdin)


def is_restorable_url(url):
    return isinstance(url, str) and (url.strip().lower().startswith("http://") or url.strip().lower().startswith("https://"))


def fit_visible_window(width, height, process_id):
    if not sys.platform.startswith("win"):
        return
    try:
        import ctypes
        from ctypes import wintypes

        user32 = ctypes.windll.user32
        enum_windows = user32.EnumWindows
        enum_windows_proc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
        is_window_visible = user32.IsWindowVisible
        get_window_thread_process_id = user32.GetWindowThreadProcessId
        set_window_pos = user32.SetWindowPos
        show_window = user32.ShowWindow
        hwnd_notopmost = wintypes.HWND(-2 & 0xFFFFFFFFFFFFFFFF)
        swp_showwindow = 0x0040
        sw_restore = 9
        handles = []
        work_area = wintypes.RECT()
        if user32.SystemParametersInfoW(0x0030, 0, ctypes.byref(work_area), 0):
            work_left = int(work_area.left)
            work_top = int(work_area.top)
            work_width = max(640, int(work_area.right - work_area.left))
            work_height = max(480, int(work_area.bottom - work_area.top))
        else:
            work_left = 0
            work_top = 0
            work_width = max(640, int(user32.GetSystemMetrics(0)))
            work_height = max(480, int(user32.GetSystemMetrics(1)))
        target_width = max(640, min(int(width), work_width - 20))
        target_height = max(480, min(int(height), work_height - 20))

        def process_tree_pids(root_pid):
            with contextlib.suppress(Exception):
                kernel32 = ctypes.windll.kernel32
                snapshot = kernel32.CreateToolhelp32Snapshot(0x00000002, 0)
                if snapshot == wintypes.HANDLE(-1).value:
                    return {int(root_pid)}

                class ProcessEntry32(ctypes.Structure):
                    _fields_ = [
                        ("dwSize", wintypes.DWORD),
                        ("cntUsage", wintypes.DWORD),
                        ("th32ProcessID", wintypes.DWORD),
                        ("th32DefaultHeapID", ctypes.POINTER(wintypes.ULONG)),
                        ("th32ModuleID", wintypes.DWORD),
                        ("cntThreads", wintypes.DWORD),
                        ("th32ParentProcessID", wintypes.DWORD),
                        ("pcPriClassBase", wintypes.LONG),
                        ("dwFlags", wintypes.DWORD),
                        ("szExeFile", wintypes.WCHAR * 260),
                    ]

                entry = ProcessEntry32()
                entry.dwSize = ctypes.sizeof(ProcessEntry32)
                parents = {}
                ok = kernel32.Process32FirstW(snapshot, ctypes.byref(entry))
                while ok:
                    parents[int(entry.th32ProcessID)] = int(entry.th32ParentProcessID)
                    ok = kernel32.Process32NextW(snapshot, ctypes.byref(entry))
                kernel32.CloseHandle(snapshot)

                pids = {int(root_pid)}
                changed = True
                while changed:
                    changed = False
                    for pid, parent_pid in parents.items():
                        if parent_pid in pids and pid not in pids:
                            pids.add(pid)
                            changed = True
                return pids

            return {int(root_pid)}

        def callback(hwnd, _):
            if not is_window_visible(hwnd):
                return True
            owner_pid = wintypes.DWORD()
            get_window_thread_process_id(hwnd, ctypes.byref(owner_pid))
            if int(owner_pid.value) in target_pids:
                handles.append(hwnd)
            return True

        for _ in range(30):
            target_pids = process_tree_pids(process_id)
            handles.clear()
            enum_windows(enum_windows_proc(callback), 0)
            if handles:
                break
            time.sleep(0.25)

        for _ in range(8):
            if not handles:
                break
            for hwnd in list(handles):
                show_window(hwnd, sw_restore)
                set_window_pos(hwnd, hwnd_notopmost, work_left + 10, work_top + 10, target_width, target_height, swp_showwindow)
            time.sleep(0.35)
    except Exception:
        pass


def kill_existing_profile_browsers(profile_dir):
    if not sys.platform.startswith("win"):
        return

    try:
        script = r"""
$profile = [System.IO.Path]::GetFullPath($args[0])
$processes = Get-CimInstance Win32_Process -Filter "Name = 'camoufox.exe'" |
  Where-Object { $_.CommandLine -and $_.CommandLine.Contains($profile) }
foreach ($process in $processes) {
  Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
}
"""
        subprocess.run(
            ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", script, profile_dir],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            timeout=10,
            check=False,
        )
    except Exception:
        pass

class BidiClient:
    def __init__(self, endpoint, executable_path=None, profile_dir=None, env=None):
        self.endpoint = endpoint.rstrip("/") + "/session"
        self.ws = None
        self.lock = threading.RLock()
        self.next_id = 1
        self.open_count = 0
        self.executable_path = executable_path
        self.profile_dir = profile_dir
        self.env = env

    def connect(self, timeout=30):
        with contextlib.suppress(Exception):
            if self.ws:
                self.ws.close()
        self.ws = websocket.create_connection(self.endpoint, timeout=timeout, suppress_origin=True)
        self._send_locked("session.new", {"capabilities": {}})
        self.ws.settimeout(8)

    def send(self, method, params=None):
        with self.lock:
            try:
                if not self.ws:
                    self.connect(timeout=5)
                return self._send_locked(method, params)
            except Exception:
                self.connect(timeout=5)
                return self._send_locked(method, params)

    def _send_locked(self, method, params=None):
        message_id = self.next_id
        self.next_id += 1
        self.ws.send(json.dumps({"id": message_id, "method": method, "params": params or {}}))
        while True:
            payload = json.loads(self.ws.recv())
            if payload.get("id") != message_id:
                continue
            if payload.get("type") == "error":
                raise RuntimeError(payload.get("message") or payload.get("error") or method)
            return payload.get("result") or {}

    def contexts(self):
        return self.send("browsingContext.getTree").get("contexts") or []

    def send_optional(self, method, params=None, timeout=2):
        with self.lock:
            old_timeout = self.ws.gettimeout() if self.ws else None
            try:
                if not self.ws:
                    self.connect(timeout=timeout)
                    old_timeout = self.ws.gettimeout()
                self.ws.settimeout(timeout)
                return self._send_locked(method, params)
            except Exception:
                with contextlib.suppress(Exception):
                    if self.ws:
                        self.ws.close()
                self.ws = None
                return None
            finally:
                if self.ws and old_timeout is not None:
                    with contextlib.suppress(Exception):
                        self.ws.settimeout(old_timeout)

    def contexts_optional(self, timeout=2):
        result = self.send_optional("browsingContext.getTree", timeout=timeout)
        if not isinstance(result, dict):
            return []
        return result.get("contexts") or []

    def evaluate(self, context_id, expression):
        result = self.send("script.evaluate", {
            "target": {"context": context_id},
            "expression": expression,
            "awaitPromise": False,
        }).get("result", {})
        return result.get("value") if isinstance(result, dict) else None

    def evaluate_optional(self, context_id, expression, timeout=2):
        with self.lock:
            if not self.ws:
                return None
            old_timeout = self.ws.gettimeout()
            try:
                self.ws.settimeout(timeout)
                result = self._send_locked("script.evaluate", {
                    "target": {"context": context_id},
                    "expression": expression,
                    "awaitPromise": False,
                }).get("result", {})
                return result.get("value") if isinstance(result, dict) else None
            except Exception:
                with contextlib.suppress(Exception):
                    self.ws.close()
                self.ws = None
                return None
            finally:
                if self.ws:
                    with contextlib.suppress(Exception):
                        self.ws.settimeout(old_timeout)

    def page_payload(self, include_text):
        pages = []
        contexts = self.contexts_optional(timeout=2)
        text_context_id = contexts[-1].get("context") if include_text and contexts else None
        for ctx in contexts:
            item = {"url": ctx.get("url"), "title": None, "text": None}
            if include_text and ctx.get("context") == text_context_id:
                item["title"] = self.evaluate_optional(ctx["context"], "document.title")
                item["text"] = self.evaluate_optional(ctx["context"], "document.body ? document.body.innerText : ''")
            pages.append(item)
        return pages

    def open_url(self, url):
        if self.executable_path and self.profile_dir:
            subprocess.Popen(
                [self.executable_path, "-profile", self.profile_dir, "-new-tab", url],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                env=self.env,
            )
            self.open_count += 1
            return {"url": url, "title": None}

        contexts = self.contexts()
        has_restorable_page = any(is_restorable_url(ctx.get("url")) for ctx in contexts)
        target = next((ctx for ctx in contexts if not is_restorable_url(ctx.get("url"))), None) if not has_restorable_page and self.open_count == 0 else None
        if target is None:
            target = self.send("browsingContext.create", {"type": "tab"})
        context_id = target["context"]
        self.send("browsingContext.activate", {"context": context_id})
        self.evaluate(context_id, f"window.location.href = {json.dumps(url)}; undefined")
        self.open_count += 1
        return {"url": url, "title": None}

    def click_text(self, text):
        contexts = self.contexts()
        if not contexts:
            return {"success": False, "message": "No open page found.", "url": None, "title": None}
        context_id = contexts[-1]["context"]
        expression = """
        (() => {
          const lowered = %s.toLowerCase();
          const candidates = Array.from(document.querySelectorAll('button,a,[role="button"],input[type="button"],input[type="submit"]'));
          const element = candidates.find((node) => {
            const text = (node.innerText || node.textContent || node.value || '').toLowerCase();
            return text.includes(lowered);
          });
          if (!element) return false;
          element.click();
          return true;
        })()
        """ % json.dumps(str(text))
        clicked = bool(self.evaluate(context_id, expression))
        if clicked:
            time.sleep(2)
        current = next((ctx for ctx in self.contexts() if ctx.get("context") == context_id), contexts[-1])
        return {"success": clicked, "message": "Clicked." if clicked else f"Clickable text not found: {text}", "url": current.get("url"), "title": None}

    def close(self):
        with contextlib.suppress(Exception):
            self.send("browser.close")
        with contextlib.suppress(Exception):
            self.ws.close()


class BrokerState:
    def __init__(self, process, bidi):
        self.process = process
        self.bidi = bidi
        self.stopping = False

    def close(self):
        self.stopping = True
        if self.process.poll() is None:
            self.bidi.close()
        else:
            with contextlib.suppress(Exception):
                self.bidi.ws.close()
        try:
            self.process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait(timeout=10)


def make_handler(state):
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, format, *args):
            return

        def _send(self, status, payload):
            body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def _read_json(self):
            length = int(self.headers.get("Content-Length") or "0")
            return json.loads(self.rfile.read(length).decode("utf-8")) if length > 0 else {}

        def do_GET(self):
            try:
                parsed = urlparse(self.path)
                if parsed.path == "/pages":
                    include_text = (parse_qs(parsed.query).get("text") or ["false"])[0].lower() == "true"
                    self._send(200, {"ok": True, "pages": state.bidi.page_payload(include_text)})
                    return
                if parsed.path == "/health":
                    self._send(200, {"ok": True})
                    return
                self._send(404, {"ok": False, "error": "not_found"})
            except Exception as exc:
                self._send(500, {"ok": False, "error": str(exc)})

        def do_POST(self):
            try:
                parsed = urlparse(self.path)
                data = self._read_json()
                if parsed.path == "/open":
                    self._send(200, {"ok": True, **state.bidi.open_url(data["url"])})
                    return
                if parsed.path == "/click":
                    self._send(200, {"ok": True, **state.bidi.click_text(data["text"])})
                    return
                if parsed.path == "/stop":
                    state.stopping = True
                    self._send(200, {"ok": True})
                    return
                self._send(404, {"ok": False, "error": "not_found"})
            except Exception as exc:
                self._send(500, {"ok": False, "error": str(exc)})

    return Handler


def main():
    config = read_config()
    launcher = load_launcher()
    launch_kwargs = launcher.build_launch_kwargs(config)
    bookmarks = launch_kwargs.pop("bookmarks", [])
    launch_kwargs.pop("addons", None)
    with contextlib.redirect_stdout(sys.stderr):
        options = launcher.launch_options(**copy.deepcopy(launch_kwargs))
    launcher.ensure_browser_policies(options.get("executable_path"), bookmarks)

    env = os.environ.copy()
    for key, value in (options.get("env") or {}).items():
        env[str(key)] = str(value)
    initial_urls = [url for url in (config.get("initial_urls") or []) if is_restorable_url(url)]
    kill_existing_profile_browsers(launch_kwargs["user_data_dir"])
    command = [
        options["executable_path"],
        "-no-remote",
        "-profile",
        launch_kwargs["user_data_dir"],
        "--remote-debugging-port",
        "0",
    ]
    if isinstance(launch_kwargs.get("window"), tuple):
        window_width, window_height = launch_kwargs["window"]
        command.extend(["--width", str(int(window_width)), "--height", str(int(window_height))])
    command.extend(initial_urls or ["about:blank"])
    process = subprocess.Popen(command, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, text=True, env=env)
    endpoint = None
    pattern = re.compile(r"WebDriver BiDi listening on (ws://[^\s]+)")
    deadline = time.time() + 60
    while time.time() < deadline and process.poll() is None:
        line = process.stderr.readline()
        if not line:
            continue
        print(line.rstrip(), file=sys.stderr, flush=True)
        match = pattern.search(line)
        if match:
            endpoint = match.group(1)
            break
    if not endpoint:
        raise RuntimeError("Timed out waiting for WebDriver BiDi endpoint.")

    state = BrokerState(process, BidiClient(endpoint, options["executable_path"], launch_kwargs["user_data_dir"], env))
    if isinstance(launch_kwargs.get("window"), tuple):
        fit_visible_window(*launch_kwargs["window"], process.pid)
    server = ThreadingHTTPServer(("127.0.0.1", 0), make_handler(state))
    server.timeout = 1
    print(f"YELLOWFOX_BROKER http://127.0.0.1:{server.server_port}", flush=True)
    while not state.stopping and process.poll() is None:
        server.handle_request()
    state.close()


if __name__ == "__main__":
    main()
