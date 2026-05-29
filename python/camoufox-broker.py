#!/usr/bin/env python3
import importlib.util
import contextlib
import copy
import json
import sys
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlparse

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
    if not isinstance(url, str):
        return False
    lowered = url.strip().lower()
    return lowered.startswith("http://") or lowered.startswith("https://")


class BrokerState:
    def __init__(self, context_manager, context):
        self.context_manager = context_manager
        self.context = context
        self.lock = threading.RLock()
        self.stopping = False
        self.closed = False
        self.context.set_default_timeout(5000)
        self.context.set_default_navigation_timeout(15000)

    def page_payload(self, include_text):
        result = []
        with self.lock:
            for page in list(self.context.pages):
                item = {"url": page.url, "title": None, "text": None}
                try:
                    if include_text:
                        item["text"] = page.locator("body").inner_text(timeout=3000)
                except Exception as exc:
                    if include_text:
                        item["text"] = f"<page inspection failed: {exc}>"
                result.append(item)
        return result

    def open_url(self, url):
        with self.lock:
            page = next((p for p in self.context.pages if not is_restorable_url(p.url)), None)
            if page is None:
                page = self.context.new_page()
            try:
                page.goto(url, wait_until="commit", timeout=15000)
            except Exception:
                pass
            page.bring_to_front()
            return {"url": page.url, "title": None}

    def click_text(self, text):
        with self.lock:
            pages = list(self.context.pages)
            if not pages:
                return {"success": False, "message": "No open page found.", "url": None, "title": None}
            page = pages[-1]
            clicked = page.evaluate(
                """
                (needle) => {
                    const lowered = String(needle).toLowerCase();
                    const candidates = Array.from(document.querySelectorAll('button,a,[role="button"],input[type="button"],input[type="submit"]'));
                    const element = candidates.find((node) => {
                        const text = (node.innerText || node.textContent || node.value || '').toLowerCase();
                        return text.includes(lowered);
                    });
                    if (!element) return false;
                    element.click();
                    return true;
                }
                """,
                text,
            )
            if clicked:
                page.wait_for_timeout(15000)
            return {
                "success": bool(clicked),
                "message": "Clicked." if clicked else f"Clickable text not found: {text}",
                "url": page.url,
                "title": None,
            }

    def close(self):
        with self.lock:
            if self.closed:
                return
            self.closed = True
            try:
                self.context.close()
            finally:
                self.context_manager.__exit__(None, None, None)


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
            if length <= 0:
                return {}
            return json.loads(self.rfile.read(length).decode("utf-8"))

        def do_GET(self):
            try:
                parsed = urlparse(self.path)
                if parsed.path == "/pages":
                    query = parse_qs(parsed.query)
                    include_text = (query.get("text") or ["false"])[0].lower() == "true"
                    self._send(200, {"ok": True, "pages": state.page_payload(include_text)})
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
                    payload = state.open_url(data["url"])
                    self._send(200, {"ok": True, **payload})
                    return
                if parsed.path == "/click":
                    payload = state.click_text(data["text"])
                    self._send(200, {"ok": True, **payload})
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
    # Camoufox 142 hangs during persistent startup when WebExtensions are passed
    # through Playwright. Bookmarks are still imported through bookmarks.html.
    launch_kwargs.pop("addons", None)
    with contextlib.redirect_stdout(sys.stderr):
        options = launcher.launch_options(**copy.deepcopy(launch_kwargs))
    launcher.ensure_browser_policies(options.get("executable_path"), bookmarks)

    from camoufox.sync_api import Camoufox

    manager = Camoufox(**launch_kwargs)
    context = manager.__enter__()
    state = BrokerState(manager, context)
    server = HTTPServer(("127.0.0.1", 0), make_handler(state))
    print(f"YELLOWFOX_BROKER http://127.0.0.1:{server.server_port}", flush=True)
    while not state.stopping:
        server.handle_request()
    state.close()


if __name__ == "__main__":
    main()
