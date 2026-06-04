#!/usr/bin/env python3
"""
CamouFox Server Launcher
Launches CamouFox browser with provided configuration and prints CDP URL.
"""
import sys
import json
import os
import base64
import subprocess
import contextlib
from pathlib import Path

from yellowfox_camoufox_home import configure_camoufox_home

configure_camoufox_home()

import orjson
from camoufox.server import get_nodejs, to_camel_case_dict
from camoufox.utils import launch_options
from camoufox.addons import DefaultAddons
from browserforge.fingerprints import Screen


LAUNCH_PERSISTENT_SCRIPT = Path(__file__).with_name("launchPersistentServer.js")
DOWNLOAD_MIME_TYPES = ",".join([
    "application/json",
    "application/octet-stream",
    "application/pdf",
    "application/zip",
    "image/jpeg",
    "image/png",
    "image/webp",
    "text/csv",
    "text/plain",
    "video/mp4",
    "video/quicktime",
    "video/webm",
])

try:
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
except Exception:
    pass


def launch_persistent_server(**kwargs):
    bookmarks = kwargs.pop("bookmarks", [])
    config = launch_options(**kwargs)
    ensure_browser_policies(config.get("executable_path"), bookmarks)
    nodejs = get_nodejs()
    data = orjson.dumps(to_camel_case_dict(config))

    process = subprocess.Popen(
        [
            nodejs,
            str(LAUNCH_PERSISTENT_SCRIPT),
        ],
        cwd=Path(nodejs).parent / "package",
        stdin=subprocess.PIPE,
        text=True,
    )
    if process.stdin:
        process.stdin.write(base64.b64encode(data).decode())
        process.stdin.close()

    process.wait()
    raise RuntimeError("Persistent server process terminated unexpectedly")


def build_launch_kwargs(config):
    screen_width = int(config['screen']['maxWidth'])
    screen_height = int(config['screen']['maxHeight'])
    constrains = Screen(
        min_width=screen_width,
        max_width=screen_width,
        min_height=screen_height,
        max_height=screen_height,
    )
    proxy = config.get('proxy')
    addons = config.get('addons') or []
    user_data_dir = config['user_data_dir']
    download_dir = default_download_dir()
    os.makedirs(user_data_dir, exist_ok=True)

    if isinstance(proxy, str):
        proxy = {"server": proxy}

    window_width, window_height = visible_window_size(screen_width, screen_height)

    camoufox_config = {
        "showcursor": False,
        "window.screenX": 0,
        "window.screenY": 0
    }
    camoufox_config.update(config.get("camoufox_config") or {})
    camoufox_config["showcursor"] = False

    launch_kwargs = {
        "headless": False,
        "geoip": config.get("geoip"),
        "humanize": False,
        "i_know_what_im_doing": True,
        "os": config['os'],
        "screen": constrains,
        "window": (window_width, window_height),
        "config": camoufox_config,
        "firefox_user_prefs": {
            "browser.places.importBookmarksHTML": True,
            "browser.bookmarks.restore_default_bookmarks": False,
            "browser.toolbars.bookmarks.visibility": "always",
            "browser.toolbars.bookmarks.showOtherBookmarks": False,
            "browser.toolbars.bookmarks.showInPrivateBrowsing": True,
            "browser.bookmarks.addedImportButton": True,
            "browser.policies.runOncePerModification.displayBookmarksToolbar": "always",
            "browser.startup.page": 0,
            "keyword.enabled": True,
            "dom.event.contextmenu.enabled": False,
            "browser.fixup.fallback-to-https": True,
            "browser.fixup.upgrade_to_https": True,
            "dom.security.https_first": True,
            "dom.security.https_first_pbm": True,
            "dom.security.https_only_mode": True,
            "dom.security.https_only_mode_pbm": True,
            "dom.security.https_only_mode.upgrade_local": True,
            "dom.security.https_only_mode_ever_enabled": True,
            "browser.search.defaultenginename": "Google",
            "browser.search.selectedEngine": "Google",
            "browser.search.order.1": "Google",
            "browser.search.update": False,
            "browser.urlbar.placeholderName": "Google",
            "browser.urlbar.placeholderName.private": "Google",
            "browser.sessionstore.resume_from_crash": False,
            "browser.sessionstore.max_tabs_undo": 25,
            "browser.sessionstore.max_windows_undo": 0,
            "browser.link.open_newwindow": 3,
            "browser.link.open_newwindow.restriction": 0,
            "browser.shell.checkDefaultBrowser": False,
            "browser.download.useDownloadDir": True,
            "browser.download.folderList": 2,
            "browser.download.dir": download_dir,
            "browser.download.alwaysOpenPanel": True,
            "browser.download.start_downloads_in_tmp_dir": False,
            "browser.helperApps.neverAsk.saveToDisk": DOWNLOAD_MIME_TYPES,
            "browser.aboutwelcome.enabled": False,
            "browser.preonboarding.enabled": False,
            "extensions.autoDisableScopes": 0,
            "extensions.enabledScopes": 5,
            "datareporting.policy.dataSubmissionEnabled": False,
            "datareporting.policy.dataSubmissionPolicyAcceptedVersion": 999,
            "datareporting.policy.dataSubmissionPolicyNotifiedTime": "0",
            "datareporting.healthreport.uploadEnabled": False,
            "toolkit.telemetry.enabled": False,
            "toolkit.telemetry.unified": False,
            "toolkit.telemetry.archive.enabled": False,
            "toolkit.telemetry.newProfilePing.enabled": False,
            "toolkit.telemetry.reportingpolicy.firstRun": False,
            "toolkit.telemetry.shutdownPingSender.enabled": False,
            "app.shield.optoutstudies.enabled": False
        },
        "accept_downloads": "internal-browser-default",
        "downloads_path": download_dir,
        "timeout": 120000,
        "persistent_context": True,
        "user_data_dir": user_data_dir,
        "exclude_addons": list(DefaultAddons),
        "bookmarks": config.get("bookmarks") or []
    }

    if isinstance(proxy, dict) and proxy:
        launch_kwargs["proxy"] = proxy
    if addons:
        launch_kwargs["addons"] = addons

    return launch_kwargs


def default_download_dir():
    configured = os.environ.get("YELLOWFOX_DOWNLOAD_DIR")
    if configured:
        return str(Path(configured).expanduser())
    if sys.platform.startswith("win"):
        profile = os.environ.get("USERPROFILE")
        if profile:
            return str(Path(profile) / "Downloads")
    return str(Path.home() / "Downloads")


def print_launch_options(config):
    launch_kwargs = build_launch_kwargs(config)
    bookmarks = launch_kwargs.pop("bookmarks", [])
    with contextlib.redirect_stdout(sys.stderr):
        options = launch_options(**launch_kwargs)
    ensure_browser_policies(options.get("executable_path"), bookmarks)
    print(json.dumps(to_camel_case_dict(options), ensure_ascii=False, default=str))


def ensure_browser_policies(executable_path, bookmarks):
    if not executable_path:
        return

    try:
        browser_root = Path(executable_path).parent
        remove_legacy_browser_autoconfig(browser_root)
        ensure_minimal_browser_autoconfig(browser_root)
        remove_yellowfox_native_window_button_css(browser_root)

        policies = {
            "policies": {
                "DisplayBookmarksToolbar": "always",
                "DontCheckDefaultBrowser": True,
                "SkipTermsOfUse": True,
                "SearchEngines": {
                    "Default": "Google",
                    "PreventInstalls": False
                }
            }
        }
        for distribution_dir in (browser_root / "distribution", browser_root / "browser" / "distribution"):
            distribution_dir.mkdir(parents=True, exist_ok=True)
            (distribution_dir / "policies.json").write_text(
                json.dumps(policies, ensure_ascii=False, indent=2),
                encoding="utf-8"
            )
    except Exception:
        pass


def ensure_minimal_browser_autoconfig(browser_root):
    autoconfig_js = (
        'pref("general.config.filename", "yellowfox.cfg");\n'
        'pref("general.config.obscure_value", 0);\n'
        'pref("general.config.sandbox_enabled", false);\n'
    )
    for defaults_pref_dir in (
        browser_root / "defaults" / "pref",
        browser_root / "defaults" / "preferences",
        browser_root / "browser" / "defaults" / "preferences",
    ):
        defaults_pref_dir.mkdir(parents=True, exist_ok=True)
        (defaults_pref_dir / "autoconfig.js").write_text(autoconfig_js, encoding="utf-8")

    cfg = r'''
// YellowFox minimal startup chrome visibility.
lockPref("keyword.enabled", true);
lockPref("browser.fixup.fallback-to-https", true);
lockPref("browser.fixup.upgrade_to_https", true);
lockPref("dom.security.https_first", true);
lockPref("dom.security.https_first_pbm", true);
lockPref("dom.security.https_only_mode", true);
lockPref("dom.security.https_only_mode_pbm", true);
lockPref("dom.security.https_only_mode.upgrade_local", true);
lockPref("dom.security.https_only_mode_ever_enabled", true);
lockPref("browser.search.defaultenginename", "Google");
lockPref("browser.search.selectedEngine", "Google");
lockPref("browser.search.order.1", "Google");
lockPref("browser.search.update", false);
lockPref("browser.urlbar.placeholderName", "Google");
lockPref("browser.urlbar.placeholderName.private", "Google");

try {
  const { Services } = ChromeUtils.importESModule("resource://gre/modules/Services.sys.mjs");
  Services.prefs.setCharPref("browser.toolbars.bookmarks.visibility", "always");

  async function enforceGoogleSearchEngine() {
    try {
      await Services.search.init();
      const google = Services.search.getEngineByName("Google");
      if (!google) {
        return;
      }
      Services.search.defaultEngine = google;
      if ("defaultPrivateEngine" in Services.search) {
        Services.search.defaultPrivateEngine = google;
      }
    } catch (e) {}
  }
  enforceGoogleSearchEngine();

  function showBookmarksToolbar(win) {
    try {
      if (!win || !win.document) {
        return;
      }

      const root = win.document.documentElement;
      const chromeHidden = root.getAttribute("chromehidden");
      if (chromeHidden) {
        root.setAttribute(
          "chromehidden",
          chromeHidden
            .split(" ")
            .filter((value) => value !== "directories" && value !== "toolbar")
            .join(" ")
        );
      }

      const toolbar = win.document.getElementById("PersonalToolbar");
      if (toolbar) {
        toolbar.removeAttribute("collapsed");
        toolbar.removeAttribute("hidden");
        toolbar.collapsed = false;
        toolbar.hidden = false;
      }
    } catch (e) {}
  }

  Services.obs.addObserver((subject) => showBookmarksToolbar(subject), "browser-delayed-startup-finished");
  for (const win of Services.wm.getEnumerator("navigator:browser")) {
    showBookmarksToolbar(win);
  }
} catch (e) {}
'''
    (browser_root / "yellowfox.cfg").write_text(cfg.lstrip(), encoding="utf-8")


def remove_yellowfox_native_window_button_css(browser_root):
    chrome_css_path = browser_root / "chrome.css"
    if not chrome_css_path.exists():
        return

    marker_start = "/* YellowFox native window controls start */"
    marker_end = "/* YellowFox native window controls end */"

    text = chrome_css_path.read_text(encoding="utf-8", errors="ignore")
    if marker_start in text and marker_end in text:
        before, rest = text.split(marker_start, 1)
        _, after = rest.split(marker_end, 1)
        chrome_css_path.write_text(before.rstrip() + after, encoding="utf-8")


def remove_legacy_browser_autoconfig(browser_root):
    """Remove old YellowFox chrome hooks that could deadlock native menus."""
    legacy_cfg = browser_root / "yellowfox.cfg"
    with contextlib.suppress(Exception):
        if legacy_cfg.exists() and "YellowFox startup chrome customizations" in legacy_cfg.read_text(encoding="utf-8", errors="ignore"):
            legacy_cfg.unlink()

    for defaults_pref_dir in (
        browser_root / "defaults" / "pref",
        browser_root / "defaults" / "preferences",
        browser_root / "browser" / "defaults" / "preferences",
    ):
        autoconfig_path = defaults_pref_dir / "autoconfig.js"
        with contextlib.suppress(Exception):
            if autoconfig_path.exists() and "yellowfox.cfg" in autoconfig_path.read_text(encoding="utf-8", errors="ignore"):
                autoconfig_path.unlink()


def visible_window_size(width, height):
    """Clamp the browser window to the visible desktop in user-facing pixels."""
    monitor_width = None
    monitor_height = None

    if sys.platform.startswith("win"):
        try:
            import ctypes
            from ctypes import wintypes
            user32 = ctypes.windll.user32
            rect = wintypes.RECT()
            if user32.SystemParametersInfoW(0x0030, 0, ctypes.byref(rect), 0):
                monitor_width = int(rect.right - rect.left)
                monitor_height = int(rect.bottom - rect.top)
            else:
                monitor_width = int(user32.GetSystemMetrics(0))
                monitor_height = int(user32.GetSystemMetrics(1))
        except Exception:
            monitor_width = None
            monitor_height = None

    try:
        from screeninfo import get_monitors
        monitors = get_monitors()
        monitor = next((m for m in monitors if getattr(m, "is_primary", False)), monitors[0] if monitors else None)
        if monitor and (monitor_width is None or monitor_height is None):
            monitor_width = int(monitor.width)
            monitor_height = int(monitor.height)
    except Exception:
        pass

    if monitor_width and monitor_height:
        usable_width = max(640, monitor_width - 20)
        usable_height = max(480, monitor_height - 20)
        width = min(width, usable_width)
        height = min(height, usable_height)

    return int(width), int(height)


def main():
    """Launch CamouFox browser and print CDP URL."""
    # Read config from command line argument or stdin
    print_options = len(sys.argv) > 1 and sys.argv[1] == "--print-options"
    if len(sys.argv) > (2 if print_options else 1):
        config_path = sys.argv[2] if print_options else sys.argv[1]
        with open(config_path, 'r', encoding='utf-8') as f:
            config = json.load(f)
    else:
        # Read from stdin
        config = json.load(sys.stdin)

    launch_kwargs = build_launch_kwargs(config)

    if print_options:
        print_launch_options(config)
        return

    # Launch CamouFox with configuration
    launch_persistent_server(**launch_kwargs)

if __name__ == '__main__':
    main()
