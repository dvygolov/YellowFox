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
from pathlib import Path

import orjson
from camoufox.server import get_nodejs, to_camel_case_dict
from camoufox.utils import launch_options
from camoufox.addons import DefaultAddons
from browserforge.fingerprints import Screen


LAUNCH_PERSISTENT_SCRIPT = Path(__file__).with_name("launchPersistentServer.js")


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


def ensure_browser_policies(executable_path, bookmarks):
    if not executable_path:
        return

    try:
        browser_root = Path(executable_path).parent
        ensure_browser_autoconfig(browser_root)
        managed_bookmarks = [{"toplevel_name": "yellowfox shared"}]
        folders = {}
        for bookmark in bookmarks:
            title = bookmark.get("title")
            url = bookmark.get("url")
            if not title or not url:
                continue

            folder = bookmark.get("folder")
            item = {"name": title, "url": url}
            if folder:
                folders.setdefault(folder, []).append(item)
            else:
                managed_bookmarks.append(item)

        for folder, children in folders.items():
            managed_bookmarks.append({"name": folder, "children": children})

        distribution_dir = browser_root / "distribution"
        distribution_dir.mkdir(parents=True, exist_ok=True)
        policies = {
            "policies": {
                "DisplayBookmarksToolbar": "always",
                "DontCheckDefaultBrowser": True,
                "SkipTermsOfUse": True,
                "ManagedBookmarks": managed_bookmarks
            }
        }
        (distribution_dir / "policies.json").write_text(
            json.dumps(policies, ensure_ascii=False, indent=2),
            encoding="utf-8"
        )
    except Exception:
        pass


def ensure_browser_autoconfig(browser_root):
    defaults_pref_dir = browser_root / "defaults" / "pref"
    defaults_pref_dir.mkdir(parents=True, exist_ok=True)
    (defaults_pref_dir / "autoconfig.js").write_text(
        'pref("general.config.filename", "yellowfox.cfg");\n'
        'pref("general.config.obscure_value", 0);\n',
        encoding="utf-8"
    )

    cfg = r'''
// YellowFox startup chrome customizations
try {
  const { Services } = ChromeUtils.importESModule("resource://gre/modules/Services.sys.mjs");
  const { CustomizableUI } = ChromeUtils.importESModule("resource:///modules/CustomizableUI.sys.mjs");
  Services.prefs.setCharPref("yellowfox.autoconfig.ran", "yes");

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
      if (!toolbar) {
        return;
      }

      CustomizableUI.setToolbarVisibility("PersonalToolbar", true);
      toolbar.removeAttribute("collapsed");
      toolbar.removeAttribute("hidden");
      toolbar.collapsed = false;
      toolbar.hidden = false;
      toolbar.style.setProperty("display", "flex", "important");
      toolbar.style.setProperty("visibility", "visible", "important");
      toolbar.style.setProperty("height", "30px", "important");
      toolbar.style.setProperty("min-height", "28px", "important");
      toolbar.style.setProperty("opacity", "1", "important");
    } catch (e) {}
  }

  function schedule(win) {
    showBookmarksToolbar(win);
    for (const delay of [250, 1000, 3000]) {
      win.setTimeout(() => showBookmarksToolbar(win), delay);
    }
  }

  Services.obs.addObserver((subject) => schedule(subject), "browser-delayed-startup-finished");
  for (const win of Services.wm.getEnumerator("navigator:browser")) {
    schedule(win);
  }
} catch (e) {}
'''
    (browser_root / "yellowfox.cfg").write_text(cfg.lstrip(), encoding="utf-8")


def visible_window_size(width, height):
    """Keep spoofed screen independent from the real window size."""
    monitor_width = None
    monitor_height = None

    if sys.platform.startswith("win"):
        try:
            import ctypes
            user32 = ctypes.windll.user32
            user32.SetProcessDPIAware()
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
        width = min(width, max(800, monitor_width - 80))
        height = min(height, max(600, monitor_height - 120))

    return int(width), int(height)


def main():
    """Launch CamouFox browser and print CDP URL."""
    # Read config from command line argument or stdin
    if len(sys.argv) > 1:
        config_path = sys.argv[1]
        with open(config_path, 'r', encoding='utf-8') as f:
            config = json.load(f)
    else:
        # Read from stdin
        config = json.load(sys.stdin)
    
    constrains = Screen(max_width=config['screen']['maxWidth'], max_height=config['screen']['maxHeight'])
    proxy = config.get('proxy')
    addons = config.get('addons') or []
    user_data_dir = config['user_data_dir']
    os.makedirs(user_data_dir, exist_ok=True)

    if isinstance(proxy, str):
        proxy = {"server": proxy}

    screen_width = int(config['screen']['maxWidth'])
    screen_height = int(config['screen']['maxHeight'])
    window_width, window_height = visible_window_size(screen_width, screen_height)

    camoufox_config = {
        "showcursor": False,
        "window.screenX": 0,
        "window.screenY": 0
    }
    camoufox_config.update(config.get("camoufox_config") or {})

    launch_kwargs = {
        "headless": False,
        "geoip": config.get("geoip"),
        "humanize": False,
        "i_know_what_im_doing": True,
        "os": config['os'],
        "screen": constrains,
        "window": (window_width, window_height),
        "config": camoufox_config,
        "args": [
            "-width",
            str(window_width),
            "-height",
            str(window_height)
        ],
        "firefox_user_prefs": {
            "browser.places.importBookmarksHTML": True,
            "browser.bookmarks.restore_default_bookmarks": False,
            "browser.toolbars.bookmarks.visibility": "always",
            "browser.toolbars.bookmarks.showOtherBookmarks": False,
            "browser.toolbars.bookmarks.showInPrivateBrowsing": True,
            "browser.bookmarks.addedImportButton": True,
            "browser.policies.runOncePerModification.displayBookmarksToolbar": "always",
            "toolkit.legacyUserProfileCustomizations.stylesheets": True,
            "browser.startup.page": 0,
            "browser.sessionstore.resume_from_crash": False,
            "browser.sessionstore.max_tabs_undo": 0,
            "browser.sessionstore.max_windows_undo": 0,
            "browser.shell.checkDefaultBrowser": False
        },
        "persistent_context": True,
        "user_data_dir": user_data_dir,
        "exclude_addons": list(DefaultAddons),
        "bookmarks": config.get("bookmarks") or []
    }

    if isinstance(proxy, dict) and proxy:
        launch_kwargs["proxy"] = proxy
    if addons:
        launch_kwargs["addons"] = addons

    # Launch CamouFox with configuration
    launch_persistent_server(**launch_kwargs)

if __name__ == '__main__':
    main()
