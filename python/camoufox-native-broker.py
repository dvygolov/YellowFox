#!/usr/bin/env python3
import contextlib
import copy
import importlib.util
import json
import os
import re
import struct
import subprocess
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlparse

import websocket

_window_icon_handles = []

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


def drain_process_stderr(process):
    def run():
        try:
            for _ in iter(process.stderr.readline, ""):
                pass
        except Exception:
            pass

    thread = threading.Thread(target=run, daemon=True)
    thread.start()


def firefox_pref_value(value):
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, (int, float)) and not isinstance(value, bool):
        return str(value)
    if value is None:
        return '""'
    return json.dumps(str(value), ensure_ascii=False)


def upsert_firefox_user_prefs(profile_dir, prefs):
    if not prefs:
        return

    user_js = Path(profile_dir) / "user.js"
    user_js.parent.mkdir(parents=True, exist_ok=True)
    managed_keys = {str(key) for key in prefs}
    existing_lines = []
    if user_js.exists():
        existing_lines = user_js.read_text(encoding="utf-8", errors="replace").splitlines()

    pattern = re.compile(r'^\s*user_pref\("([^"]+)"\s*,')
    kept_lines = []
    for line in existing_lines:
        match = pattern.match(line)
        if match and match.group(1) in managed_keys:
            continue
        kept_lines.append(line)

    if kept_lines and kept_lines[-1].strip():
        kept_lines.append("")
    kept_lines.extend(
        f'user_pref("{key}", {firefox_pref_value(prefs[key])});'
        for key in sorted(managed_keys)
    )
    user_js.write_text("\n".join(kept_lines) + "\n", encoding="utf-8")


def proxy_firefox_prefs(proxy):
    proxy_keys = {
        "network.proxy.type",
        "network.proxy.http",
        "network.proxy.http_port",
        "network.proxy.ssl",
        "network.proxy.ssl_port",
        "network.proxy.socks",
        "network.proxy.socks_port",
        "network.proxy.socks_version",
        "network.proxy.socks_remote_dns",
        "network.proxy.share_proxy_settings",
        "network.proxy.no_proxies_on",
        "network.proxy.failover_direct",
        "media.peerconnection.ice.proxy_only_if_behind_proxy",
    }
    reset = {
        "network.proxy.type": 0,
        "network.proxy.http": "",
        "network.proxy.http_port": 0,
        "network.proxy.ssl": "",
        "network.proxy.ssl_port": 0,
        "network.proxy.socks": "",
        "network.proxy.socks_port": 0,
        "network.proxy.socks_version": 5,
        "network.proxy.socks_remote_dns": True,
        "network.proxy.share_proxy_settings": False,
        "network.proxy.no_proxies_on": "",
        "network.proxy.failover_direct": False,
        "media.peerconnection.ice.proxy_only_if_behind_proxy": True,
    }

    if not isinstance(proxy, dict):
        return reset

    server = str(proxy.get("server") or "").strip()
    if not server:
        return reset

    parsed = urlparse(server if "://" in server else f"http://{server}")
    host = parsed.hostname
    port = parsed.port
    scheme = (parsed.scheme or "http").lower()
    if not host or not port:
        raise RuntimeError(f"Invalid proxy server for Firefox prefs: {server}")

    prefs = dict(reset)
    prefs["network.proxy.type"] = 1
    if scheme.startswith("socks"):
        prefs["network.proxy.socks"] = host
        prefs["network.proxy.socks_port"] = port
        prefs["network.proxy.socks_version"] = 5
        prefs["network.proxy.socks_remote_dns"] = True
    else:
        prefs["network.proxy.http"] = host
        prefs["network.proxy.http_port"] = port
        prefs["network.proxy.ssl"] = host
        prefs["network.proxy.ssl_port"] = port

    return {key: prefs[key] for key in proxy_keys}


def profile_icon_lines(profile_name):
    words = [part for part in re.split(r"\s+", str(profile_name or "").strip()) if part]
    if not words:
        return ["YF"]
    return [word[:6] for word in words[:3]]


def ensure_profile_icon(icon_path, profile_name):
    if not sys.platform.startswith("win") or not icon_path:
        return None

    try:
        path = Path(icon_path)
        path.parent.mkdir(parents=True, exist_ok=True)
        lines = profile_icon_lines(profile_name)
        images = [_render_profile_icon_image(size, lines) for size in (32, 48, 64)]
        offset = 6 + (16 * len(images))
        entries = []
        chunks = []
        for size, data in images:
            width = 0 if size >= 256 else size
            entries.append(struct.pack("<BBBBHHII", width, width, 0, 0, 1, 32, len(data), offset))
            chunks.append(data)
            offset += len(data)

        payload = struct.pack("<HHH", 0, 1, len(images)) + b"".join(entries) + b"".join(chunks)
        path.write_bytes(payload)
        (path.with_suffix(".txt")).write_text(str(profile_name or ""), encoding="utf-8")
        return str(path)
    except Exception as exc:
        print(f"YELLOWFOX_PROFILE_ICON_ERROR {exc}", file=sys.stderr, flush=True)
        return None


def _render_profile_icon_image(size, lines):
    import ctypes
    from ctypes import wintypes

    user32 = ctypes.windll.user32
    gdi32 = ctypes.windll.gdi32
    user32.GetDC.restype = ctypes.c_void_p
    gdi32.CreateCompatibleDC.restype = ctypes.c_void_p
    gdi32.CreateDIBSection.restype = ctypes.c_void_p
    gdi32.SelectObject.restype = ctypes.c_void_p
    gdi32.CreateSolidBrush.restype = ctypes.c_void_p
    gdi32.CreateFontW.restype = ctypes.c_void_p

    class BitmapInfoHeader(ctypes.Structure):
        _fields_ = [
            ("biSize", wintypes.DWORD),
            ("biWidth", wintypes.LONG),
            ("biHeight", wintypes.LONG),
            ("biPlanes", wintypes.WORD),
            ("biBitCount", wintypes.WORD),
            ("biCompression", wintypes.DWORD),
            ("biSizeImage", wintypes.DWORD),
            ("biXPelsPerMeter", wintypes.LONG),
            ("biYPelsPerMeter", wintypes.LONG),
            ("biClrUsed", wintypes.DWORD),
            ("biClrImportant", wintypes.DWORD),
        ]

    class BitmapInfo(ctypes.Structure):
        _fields_ = [("bmiHeader", BitmapInfoHeader), ("bmiColors", wintypes.DWORD * 1)]

    user32.ReleaseDC.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    user32.FillRect.argtypes = [ctypes.c_void_p, ctypes.POINTER(wintypes.RECT), ctypes.c_void_p]
    user32.FrameRect.argtypes = [ctypes.c_void_p, ctypes.POINTER(wintypes.RECT), ctypes.c_void_p]
    user32.DrawTextW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR, ctypes.c_int, ctypes.POINTER(wintypes.RECT), wintypes.UINT]
    gdi32.CreateCompatibleDC.argtypes = [ctypes.c_void_p]
    gdi32.CreateDIBSection.argtypes = [ctypes.c_void_p, ctypes.POINTER(BitmapInfo), wintypes.UINT, ctypes.POINTER(ctypes.c_void_p), ctypes.c_void_p, wintypes.DWORD]
    gdi32.SelectObject.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    gdi32.CreateSolidBrush.argtypes = [wintypes.DWORD]
    gdi32.CreateFontW.argtypes = [
        ctypes.c_int,
        ctypes.c_int,
        ctypes.c_int,
        ctypes.c_int,
        ctypes.c_int,
        wintypes.DWORD,
        wintypes.DWORD,
        wintypes.DWORD,
        wintypes.DWORD,
        wintypes.DWORD,
        wintypes.DWORD,
        wintypes.DWORD,
        wintypes.DWORD,
        wintypes.LPCWSTR,
    ]
    gdi32.SetBkMode.argtypes = [ctypes.c_void_p, ctypes.c_int]
    gdi32.SetTextColor.argtypes = [ctypes.c_void_p, wintypes.DWORD]
    gdi32.DeleteObject.argtypes = [ctypes.c_void_p]
    gdi32.DeleteDC.argtypes = [ctypes.c_void_p]

    hdc = user32.GetDC(None)
    memdc = None
    hbitmap = None
    old_bitmap = None
    font = None
    old_font = None
    try:
        dib_bits = ctypes.c_void_p()
        info = BitmapInfo()
        info.bmiHeader.biSize = ctypes.sizeof(BitmapInfoHeader)
        info.bmiHeader.biWidth = int(size)
        info.bmiHeader.biHeight = -int(size)
        info.bmiHeader.biPlanes = 1
        info.bmiHeader.biBitCount = 32
        info.bmiHeader.biCompression = 0

        memdc = gdi32.CreateCompatibleDC(hdc)
        hbitmap = gdi32.CreateDIBSection(hdc, ctypes.byref(info), 0, ctypes.byref(dib_bits), None, 0)
        if not memdc or not hbitmap or not dib_bits.value:
            raise RuntimeError("CreateDIBSection failed")

        old_bitmap = gdi32.SelectObject(memdc, hbitmap)
        rect = wintypes.RECT(0, 0, int(size), int(size))
        background = gdi32.CreateSolidBrush(0x00FAA560)
        user32.FillRect(memdc, ctypes.byref(rect), background)
        gdi32.DeleteObject(background)

        inset = max(1, int(size / 20))
        border_rect = wintypes.RECT(inset, inset, int(size) - inset, int(size) - inset)
        border = gdi32.CreateSolidBrush(0x00E98738)
        user32.FrameRect(memdc, ctypes.byref(border_rect), border)
        gdi32.DeleteObject(border)

        text = "\n".join(lines)
        font_height = -max(7, min(16, int(size / (len(lines) + 1.15))))
        font = gdi32.CreateFontW(font_height, 0, 0, 0, 800, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI")
        old_font = gdi32.SelectObject(memdc, font)
        gdi32.SetBkMode(memdc, 1)
        gdi32.SetTextColor(memdc, 0x00000000)
        text_rect = wintypes.RECT(2, 2, int(size) - 2, int(size) - 2)
        user32.DrawTextW(memdc, text, -1, ctypes.byref(text_rect), 0x00000001 | 0x00000004 | 0x00000010)

        stride = int(size) * 4
        raw = bytearray((ctypes.c_ubyte * (stride * int(size))).from_address(dib_bits.value))
        for index in range(3, len(raw), 4):
            raw[index] = 255
        pixels = b"".join(raw[row * stride:(row + 1) * stride] for row in range(int(size) - 1, -1, -1))
        mask_stride = ((int(size) + 31) // 32) * 4
        mask = b"\x00" * (mask_stride * int(size))
        header = struct.pack("<IiiHHIIiiII", 40, int(size), int(size) * 2, 1, 32, 0, len(pixels), 0, 0, 0, 0)
        return int(size), header + pixels + mask
    finally:
        if old_font:
            gdi32.SelectObject(memdc, old_font)
        if font:
            gdi32.DeleteObject(font)
        if old_bitmap:
            gdi32.SelectObject(memdc, old_bitmap)
        if hbitmap:
            gdi32.DeleteObject(hbitmap)
        if memdc:
            gdi32.DeleteDC(memdc)
        if hdc:
            user32.ReleaseDC(None, hdc)


def apply_taskbar_identity(process_id, profile_name, profile_id, profile_dir, icon_path, executable_path, app_user_model_id=None):
    if not sys.platform.startswith("win") or not profile_name:
        return

    try:
        import ctypes
        from ctypes import wintypes

        user32 = ctypes.windll.user32
        shell32 = ctypes.windll.shell32
        ole32 = ctypes.windll.ole32
        user32.LoadImageW.restype = ctypes.c_void_p
        enum_windows = user32.EnumWindows
        enum_windows_proc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
        is_window_visible = user32.IsWindowVisible
        get_window_thread_process_id = user32.GetWindowThreadProcessId
        get_window_long = user32.GetWindowLongPtrW if ctypes.sizeof(ctypes.c_void_p) == 8 else user32.GetWindowLongW
        set_window_long = user32.SetWindowLongPtrW if ctypes.sizeof(ctypes.c_void_p) == 8 else user32.SetWindowLongW
        if ctypes.sizeof(ctypes.c_void_p) == 8:
            get_window_long.restype = ctypes.c_longlong
            get_window_long.argtypes = [wintypes.HWND, ctypes.c_int]
            set_window_long.restype = ctypes.c_longlong
            set_window_long.argtypes = [wintypes.HWND, ctypes.c_int, ctypes.c_longlong]
        else:
            get_window_long.restype = ctypes.c_long
            get_window_long.argtypes = [wintypes.HWND, ctypes.c_int]
            set_window_long.restype = ctypes.c_long
            set_window_long.argtypes = [wintypes.HWND, ctypes.c_int, ctypes.c_long]
        user32.SetWindowPos.argtypes = [
            wintypes.HWND,
            wintypes.HWND,
            ctypes.c_int,
            ctypes.c_int,
            ctypes.c_int,
            ctypes.c_int,
            wintypes.UINT,
        ]
        user32.SendMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]

        class GUID(ctypes.Structure):
            _fields_ = [
                ("Data1", wintypes.DWORD),
                ("Data2", wintypes.WORD),
                ("Data3", wintypes.WORD),
                ("Data4", wintypes.BYTE * 8),
            ]

        class PROPERTYKEY(ctypes.Structure):
            _fields_ = [("fmtid", GUID), ("pid", wintypes.DWORD)]

        class PROPVARIANT(ctypes.Structure):
            _fields_ = [
                ("vt", wintypes.USHORT),
                ("wReserved1", wintypes.USHORT),
                ("wReserved2", wintypes.USHORT),
                ("wReserved3", wintypes.USHORT),
                ("pwszVal", wintypes.LPWSTR),
            ]

        def guid(value):
            result = GUID()
            if ole32.CLSIDFromString(str(value), ctypes.byref(result)) != 0:
                raise RuntimeError(f"Invalid GUID: {value}")
            return result

        iid_property_store = guid("{886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99}")
        fmtid_app_user_model = guid("{9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}")

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

        def window_handles():
            handles = []
            target_pids = process_tree_pids(process_id)

            def callback(hwnd, _):
                if not is_window_visible(hwnd):
                    return True
                owner_pid = wintypes.DWORD()
                get_window_thread_process_id(hwnd, ctypes.byref(owner_pid))
                if int(owner_pid.value) in target_pids:
                    handles.append(hwnd)
                return True

            enum_windows(enum_windows_proc(callback), 0)
            return handles

        handles = []
        for _ in range(40):
            handles = window_handles()
            if handles:
                break
            time.sleep(0.25)
        if not handles:
            return

        fallback_slug = re.sub(r"[^A-Za-z0-9]", "", str(profile_id or profile_dir))[:48]
        app_id = app_user_model_id or f"YellowFox.Camoufox.{fallback_slug}"
        icon_resource = f"{icon_path},0" if icon_path else None
        relaunch_command = f'"{executable_path}" -no-remote -profile "{profile_dir}"' if executable_path and profile_dir else None
        gwl_style = -16
        ws_caption = 0x00C00000
        ws_sysmenu = 0x00080000
        ws_thickframe = 0x00040000
        ws_minimizebox = 0x00020000
        ws_maximizebox = 0x00010000
        swp_nomove = 0x0002
        swp_nosize = 0x0001
        swp_nozorder = 0x0004
        swp_noactivate = 0x0010
        swp_framechanged = 0x0020

        shell32.SHGetPropertyStoreForWindow.argtypes = [wintypes.HWND, ctypes.POINTER(GUID), ctypes.POINTER(ctypes.c_void_p)]
        shell32.SHGetPropertyStoreForWindow.restype = ctypes.c_long
        vt_lpwstr = 31
        property_refs = []

        def set_window_properties(hwnd):
            store = ctypes.c_void_p()
            hr = shell32.SHGetPropertyStoreForWindow(hwnd, ctypes.byref(iid_property_store), ctypes.byref(store))
            if hr != 0 or not store.value:
                return
            vtbl = ctypes.cast(store, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
            release = ctypes.WINFUNCTYPE(ctypes.c_ulong, ctypes.c_void_p)(vtbl[2])
            set_value = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.POINTER(PROPERTYKEY), ctypes.POINTER(PROPVARIANT))(vtbl[6])
            commit = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p)(vtbl[7])

            def set_string(pid, value):
                if not value:
                    return
                key = PROPERTYKEY(fmtid_app_user_model, int(pid))
                ref = ctypes.c_wchar_p(str(value))
                property_refs.append(ref)
                variant = PROPVARIANT(vt_lpwstr, 0, 0, 0, ref)
                set_value(store, ctypes.byref(key), ctypes.byref(variant))

            try:
                set_string(5, app_id)
                set_string(2, relaunch_command)
                set_string(3, icon_resource)
                commit(store)
            finally:
                release(store)

        def set_window_icon(hwnd):
            if not icon_path:
                return
            image_icon = 1
            lr_loadfromfile = 0x0010
            wm_seticon = 0x0080
            icon_small = 0
            icon_big = 1
            big = user32.LoadImageW(None, str(icon_path), image_icon, 64, 64, lr_loadfromfile)
            small = user32.LoadImageW(None, str(icon_path), image_icon, 32, 32, lr_loadfromfile)
            if big:
                user32.SendMessageW(hwnd, wm_seticon, icon_big, big)
                _window_icon_handles.append(big)
            if small:
                user32.SendMessageW(hwnd, wm_seticon, icon_small, small)
                _window_icon_handles.append(small)

        def ensure_native_window_frame(hwnd):
            style = int(get_window_long(hwnd, gwl_style))
            style |= ws_caption | ws_sysmenu | ws_thickframe | ws_minimizebox | ws_maximizebox
            set_window_long(hwnd, gwl_style, style)
            user32.SetWindowPos(hwnd, None, 0, 0, 0, 0, swp_nomove | swp_nosize | swp_nozorder | swp_noactivate | swp_framechanged)

        ole32.CoInitialize(None)
        for hwnd in handles:
            ensure_native_window_frame(hwnd)
            set_window_properties(hwnd)
            set_window_icon(hwnd)
    except Exception as exc:
        print(f"YELLOWFOX_TASKBAR_IDENTITY_ERROR {exc}", file=sys.stderr, flush=True)


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


def _cookie_bool(cookie, key, default=False):
    value = cookie.get(key)
    if isinstance(value, bool):
        return value
    if isinstance(value, str):
        return value.strip().lower() == "true"
    return default


def _cookie_expiry(cookie):
    value = cookie.get("expires", cookie.get("expirationDate"))
    if value in (None, "", -1):
        return None
    try:
        expiry = int(float(value))
        return expiry if expiry > 0 else None
    except Exception:
        return None


def _cookie_same_site(cookie):
    value = cookie.get("sameSite")
    if value in (None, ""):
        return None
    normalized = str(value).strip().lower().replace("_", "-")
    return {
        "strict": "strict",
        "0": "strict",
        "lax": "lax",
        "1": "lax",
        "none": "none",
        "no-restriction": "none",
        "2": "none",
    }.get(normalized)


def _cookie_origin(cookie, domain):
    url = cookie.get("url")
    if isinstance(url, str):
        parsed = urlparse(url)
        if parsed.scheme in ("http", "https") and parsed.hostname:
            return f"{parsed.scheme}://{parsed.hostname}"

    host = str(domain or "").strip().lstrip(".")
    if not host:
        return None
    return f"https://{host}"


def _to_bidi_cookie_params(cookie):
    if not isinstance(cookie, dict):
        return None

    name = str(cookie.get("name") or "").strip()
    value = cookie.get("value")
    domain = str(cookie.get("domain") or "").strip()
    if not name or value is None:
        return None

    source_origin = _cookie_origin(cookie, domain)
    if not source_origin:
        return None

    path = str(cookie.get("path") or "/")
    bidi_cookie = {
        "name": name,
        "value": {"type": "string", "value": str(value)},
        "path": path if path.startswith("/") else f"/{path}",
        "httpOnly": _cookie_bool(cookie, "httpOnly"),
        "secure": _cookie_bool(cookie, "secure", True),
    }

    if domain.startswith("."):
        bidi_cookie["domain"] = domain.lstrip(".")

    expiry = _cookie_expiry(cookie)
    if expiry is not None:
        bidi_cookie["expiry"] = expiry

    same_site = _cookie_same_site(cookie)
    if same_site:
        bidi_cookie["sameSite"] = same_site

    return {
        "cookie": bidi_cookie,
        "partition": {
            "type": "storageKey",
            "sourceOrigin": source_origin,
        },
    }


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

    def import_cookies(self, cookies):
        imported = 0
        errors = []
        for cookie in cookies or []:
            params = _to_bidi_cookie_params(cookie)
            if not params:
                continue

            try:
                self.send("storage.setCookie", params)
                imported += 1
                continue
            except Exception as exc:
                errors.append(str(exc))

            if "domain" in params.get("cookie", {}):
                retry_params = copy.deepcopy(params)
                retry_params["cookie"].pop("domain", None)
                try:
                    self.send("storage.setCookie", retry_params)
                    imported += 1
                    continue
                except Exception as exc:
                    errors.append(str(exc))

        if imported == 0 and cookies:
            detail = "; ".join(errors[:3]) if errors else "no valid cookies"
            raise RuntimeError(f"Failed to import cookies: {detail}")

        return imported

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
                if parsed.path == "/cookies":
                    count = state.bidi.import_cookies(data.get("cookies") or [])
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
    profile_icon_path = ensure_profile_icon(config.get("profile_icon_path"), config.get("profile_name"))
    kill_existing_profile_browsers(launch_kwargs["user_data_dir"])
    firefox_prefs = dict(options.get("firefox_user_prefs") or {})
    firefox_prefs.update(proxy_firefox_prefs(options.get("proxy")))
    upsert_firefox_user_prefs(launch_kwargs["user_data_dir"], firefox_prefs)
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
    drain_process_stderr(process)

    state = BrokerState(process, BidiClient(endpoint, options["executable_path"], launch_kwargs["user_data_dir"], env))
    startup_cookies = config.get("cookies") or []
    if startup_cookies:
        try:
            count = state.bidi.import_cookies(startup_cookies)
            print(f"YELLOWFOX_COOKIES_IMPORTED {count}", file=sys.stderr, flush=True)
        except Exception as exc:
            print(f"YELLOWFOX_COOKIES_IMPORT_ERROR {exc}", file=sys.stderr, flush=True)
    if isinstance(launch_kwargs.get("window"), tuple):
        fit_visible_window(*launch_kwargs["window"], process.pid)
    apply_taskbar_identity(
        process.pid,
        config.get("profile_name"),
        config.get("profile_id"),
        launch_kwargs["user_data_dir"],
        profile_icon_path,
        options.get("executable_path"),
        config.get("profile_app_user_model_id"),
    )
    server = ThreadingHTTPServer(("127.0.0.1", 0), make_handler(state))
    server.timeout = 1
    print(f"YELLOWFOX_BROKER http://127.0.0.1:{server.server_port}", flush=True)
    while not state.stopping and process.poll() is None:
        server.handle_request()
    state.close()


if __name__ == "__main__":
    main()
