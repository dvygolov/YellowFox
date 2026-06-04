#!/usr/bin/env python3
import base64
import importlib.util
import contextlib
import copy
import json
import os
import queue
import re
import subprocess
import sys
import threading
import time
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


def is_native_download_url(url):
    if not isinstance(url, str):
        return False
    lowered = url.strip().lower()
    return lowered.startswith("http://") or lowered.startswith("https://")


def get_download_dir():
    configured = os.environ.get("YELLOWFOX_DOWNLOAD_DIR")
    if configured:
        return Path(configured).expanduser()
    if sys.platform.startswith("win"):
        profile = os.environ.get("USERPROFILE")
        if profile:
            return Path(profile) / "Downloads"
    return Path.home() / "Downloads"


def sanitize_download_filename(file_name):
    name = str(file_name or "download").strip() or "download"
    name = re.sub(r'[<>:"/\\|?*\x00-\x1f]', "_", name)
    name = name.rstrip(" .")
    return name or "download"


def unique_download_path(directory, file_name):
    safe_name = sanitize_download_filename(file_name)
    candidate = directory / safe_name
    if not candidate.exists():
        return candidate
    stem = candidate.stem or "download"
    suffix = candidate.suffix
    for index in range(1, 1000):
        candidate = directory / f"{stem} ({index}){suffix}"
        if not candidate.exists():
            return candidate
    return directory / f"{stem} ({int(time.time())}){suffix}"


def recent_native_download(directory, file_name, since):
    safe_name = sanitize_download_filename(file_name)
    candidate = directory / safe_name
    candidates = [candidate]
    stem = candidate.stem or "download"
    suffix = candidate.suffix
    candidates.extend(directory / f"{stem} ({index}){suffix}" for index in range(1, 20))
    for path in candidates:
        try:
            stat = path.stat()
        except FileNotFoundError:
            continue
        if stat.st_size > 0 and stat.st_mtime >= since - 2:
            return path
    return None


class BrokerState:
    def __init__(self, playwright, browser, server_process, context):
        self.playwright = playwright
        self.browser = browser
        self.server_process = server_process
        self.context = context
        self.lock = threading.RLock()
        self.download_lock = threading.RLock()
        self.download_dir = get_download_dir()
        self.download_page_ids = set()
        self.stopping = False
        self.closed = False
        self.context.set_default_timeout(5000)
        self.context.set_default_navigation_timeout(15000)
        for page in list(self.context.pages):
            self.attach_download_handler(page)
        self.context.on("page", self.attach_download_handler)

    def attach_download_handler(self, page):
        page_id = id(page)
        with self.download_lock:
            if page_id in self.download_page_ids:
                return
            self.download_page_ids.add(page_id)
        page.on("download", self.save_download)

    def save_download(self, download):
        try:
            self.download_dir.mkdir(parents=True, exist_ok=True)
            started_at = time.time()
            source_url = str(getattr(download, "url", "") or "")
            if is_native_download_url(source_url):
                deadline = time.time() + 3
                while time.time() < deadline:
                    native_path = recent_native_download(self.download_dir, download.suggested_filename, started_at)
                    if native_path:
                        print(f"YELLOWFOX_DOWNLOAD_NATIVE {native_path}", file=sys.stderr, flush=True)
                        return
                    time.sleep(0.2)
                print(f"YELLOWFOX_DOWNLOAD_NATIVE_PENDING {download.suggested_filename}", file=sys.stderr, flush=True)
                return

            with self.download_lock:
                destination = self.download_dir / sanitize_download_filename(download.suggested_filename)
                if destination.exists():
                    try:
                        destination.unlink()
                    except Exception:
                        destination = unique_download_path(self.download_dir, download.suggested_filename)
                download.save_as(str(destination))
            print(f"YELLOWFOX_DOWNLOAD_SAVED {destination}", file=sys.stderr, flush=True)
        except Exception as exc:
            print(f"YELLOWFOX_DOWNLOAD_SAVE_ERROR {exc}", file=sys.stderr, flush=True)

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
                opener = self.context.pages[0] if self.context.pages else self.context.new_page()
                try:
                    opener.evaluate(
                        "(target) => window.open(target, '_blank', 'noopener,noreferrer')",
                        url,
                    )
                    deadline = time.time() + 5
                    while time.time() < deadline:
                        opened = next((p for p in self.context.pages if p.url == url), None)
                        if opened is not None:
                            page = opened
                            break
                        time.sleep(0.1)
                    if page is None:
                        page = self.context.pages[-1] if self.context.pages else opener
                except Exception:
                    page = self.context.new_page()
            try:
                page.goto(url, wait_until="commit", timeout=15000)
            except Exception:
                pass
            try:
                page.bring_to_front()
            except Exception:
                pass
            return {"url": page.url, "title": None}

    def restore_initial_urls(self, urls):
        restored = 0
        errors = []
        for url in urls or []:
            if not is_restorable_url(url):
                continue
            try:
                self.open_url(url)
                restored += 1
            except Exception as exc:
                errors.append(f"{urlparse(url).netloc or url}: {exc}")
        return {"restored": restored, "errors": errors}

    def import_cookies(self, cookies):
        payload = []
        for cookie in cookies or []:
            if not isinstance(cookie, dict):
                continue
            name = str(cookie.get("name") or "").strip()
            value = cookie.get("value")
            if not name or value is None:
                continue

            item = {
                "name": name,
                "value": str(value),
                "path": str(cookie.get("path") or "/"),
                "httpOnly": bool(cookie.get("httpOnly") or False),
                "secure": bool(cookie.get("secure") if cookie.get("secure") is not None else True),
            }
            if cookie.get("url"):
                item["url"] = str(cookie["url"])
            elif cookie.get("domain"):
                domain = str(cookie["domain"]).strip()
                item["domain"] = domain
            else:
                continue

            expires = cookie.get("expires")
            if expires not in (None, "", -1):
                try:
                    item["expires"] = int(float(expires))
                except Exception:
                    pass

            same_site = cookie.get("sameSite")
            if same_site:
                normalized = str(same_site).strip().lower().replace("_", "-")
                item["sameSite"] = {
                    "strict": "Strict",
                    "lax": "Lax",
                    "none": "None",
                    "no-restriction": "None",
                }.get(normalized, str(same_site))

            payload.append(item)

        if payload:
            with self.lock:
                self.context.add_cookies(payload)
        return len(payload)

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
                self.browser.close()
            except Exception:
                pass
            try:
                self.playwright.stop()
            except Exception:
                pass
            if self.server_process.poll() is None:
                self.server_process.terminate()
                try:
                    self.server_process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    self.server_process.kill()


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
                if parsed.path == "/cookies":
                    count = state.import_cookies(data.get("cookies") or [])
                    self._send(200, {"ok": True, "count": count})
                    return
                if parsed.path == "/stop":
                    state.stopping = True
                    self._send(200, {"ok": True})
                    return
                self._send(404, {"ok": False, "error": "not_found"})
            except Exception as exc:
                self._send(500, {"ok": False, "error": str(exc)})

    return Handler


def drain_stream(stream, label):
    def run():
        try:
            for line in iter(stream.readline, ""):
                print(f"{label} {line.rstrip()}", file=sys.stderr, flush=True)
        except Exception:
            pass

    threading.Thread(target=run, daemon=True).start()


def launch_playwright_server(launcher, launch_kwargs, bookmarks):
    with contextlib.redirect_stdout(sys.stderr):
        options = launcher.launch_options(**copy.deepcopy(launch_kwargs))
    launcher.ensure_browser_policies(options.get("executable_path"), bookmarks)

    import orjson
    from camoufox.server import get_nodejs, to_camel_case_dict

    nodejs = get_nodejs()
    data = orjson.dumps(to_camel_case_dict(options))
    process = subprocess.Popen(
        [
            nodejs,
            str(Path(__file__).with_name("launchPersistentServer.js")),
        ],
        cwd=Path(nodejs).parent / "package",
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )
    if process.stdin:
        process.stdin.write(base64.b64encode(data).decode())
        process.stdin.close()

    drain_stream(process.stderr, "YELLOWFOX_PLAYWRIGHT_STDERR")
    stdout_queue = queue.Queue()

    def collect_stdout():
        try:
            for line in iter(process.stdout.readline, ""):
                stdout_queue.put(line)
        except Exception as exc:
            stdout_queue.put(f"YELLOWFOX_STDOUT_ERROR {exc}")

    threading.Thread(target=collect_stdout, daemon=True).start()

    endpoint_pattern = re.compile(r"(wss?://[^\s\x1b]+)")
    deadline = time.time() + 120
    while time.time() < deadline:
        if process.poll() is not None:
            raise RuntimeError(f"Playwright server exited before endpoint. ExitCode={process.returncode}")
        try:
            line = stdout_queue.get(timeout=0.25)
        except queue.Empty:
            continue
        match = endpoint_pattern.search(line)
        if match:
            endpoint = match.group(1).rstrip("/")
            print(f"YELLOWFOX_PLAYWRIGHT {endpoint}", flush=True)
            return process, endpoint
        print(f"YELLOWFOX_PLAYWRIGHT_STDOUT {line.rstrip()}", file=sys.stderr, flush=True)

    raise TimeoutError("Timed out waiting for Playwright websocket endpoint.")


def wait_for_persistent_context(browser, timeout=30):
    deadline = time.time() + timeout
    while time.time() < deadline:
        contexts = browser.contexts
        if contexts:
            context = contexts[0]
            context.set_default_timeout(5000)
            context.set_default_navigation_timeout(15000)
            return context
        time.sleep(0.25)
    raise RuntimeError("Playwright server did not expose a persistent browser context.")


def main():
    config = read_config()
    launcher = load_launcher()
    launch_kwargs = launcher.build_launch_kwargs(config)
    bookmarks = launch_kwargs.pop("bookmarks", [])
    # Camoufox 142 hangs during persistent startup when WebExtensions are passed
    # through Playwright. Bookmarks are still imported through bookmarks.html.
    launch_kwargs.pop("addons", None)

    server_process, playwright_endpoint = launch_playwright_server(launcher, launch_kwargs, bookmarks)

    from playwright.sync_api import sync_playwright

    playwright = sync_playwright().start()
    browser = playwright.firefox.connect(playwright_endpoint)
    context = wait_for_persistent_context(browser)
    state = BrokerState(playwright, browser, server_process, context)
    startup_cookies = config.get("cookies") or []
    if startup_cookies:
        try:
            count = state.import_cookies(startup_cookies)
            print(f"YELLOWFOX_IMPORTED_COOKIES {count}", file=sys.stderr, flush=True)
        except Exception as exc:
            print(f"YELLOWFOX_IMPORT_COOKIES_ERROR {exc}", file=sys.stderr, flush=True)
    server = HTTPServer(("127.0.0.1", 0), make_handler(state))
    print(f"YELLOWFOX_BROKER http://127.0.0.1:{server.server_port}", flush=True)
    initial_urls = [url for url in config.get("initial_urls") or [] if is_restorable_url(url)]
    if initial_urls:
        def restore_tabs():
            result = state.restore_initial_urls(initial_urls)
            print(f"YELLOWFOX_RESTORED_TABS {json.dumps(result, ensure_ascii=False)}", file=sys.stderr, flush=True)

        threading.Thread(target=restore_tabs, daemon=True).start()
    while not state.stopping:
        server.handle_request()
    state.close()


if __name__ == "__main__":
    main()
