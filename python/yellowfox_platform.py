"""Platform-specific Camoufox runtime layout shared by the YellowFox scripts.

Camoufox ships a different archive and a different executable layout per platform:

- Windows: ``camoufox-<version>-win.x86_64.zip`` with ``camoufox.exe`` in the root.
- Linux:   ``camoufox-<version>-lin.<arch>.zip`` with ``camoufox-bin`` in the root.
- macOS:   ``camoufox-<version>-mac.<arch>.zip`` with ``Camoufox.app/Contents/MacOS/camoufox``.
"""
from __future__ import annotations

import os
import platform
import sys
from pathlib import Path

OS_NAME = {"win32": "win", "linux": "lin", "darwin": "mac"}.get(sys.platform, "")

ARCH_NAMES = {
    "amd64": "x86_64",
    "x86_64": "x86_64",
    "arm64": "arm64",
    "aarch64": "arm64",
    "x86": "i686",
    "i386": "i686",
}

# Supported architecture per platform, mirroring the official Camoufox releases.
SUPPORTED_ARCHES = {
    "win": ("x86_64",),
    "lin": ("x86_64", "arm64"),
    "mac": ("x86_64", "arm64"),
}

BROWSER_BINARY = {
    "win": "camoufox.exe",
    "lin": "camoufox-bin",
    "mac": "Camoufox.app/Contents/MacOS/camoufox",
}

BROWSER_REQUIRED_FILES = {
    "win": ("camoufox.exe", "application.ini", "omni.ja"),
    "lin": ("camoufox-bin", "application.ini", "omni.ja"),
    "mac": (
        "Camoufox.app/Contents/MacOS/camoufox",
        "Camoufox.app/Contents/Resources/application.ini",
        "Camoufox.app/Contents/Resources/omni.ja",
    ),
}


def browser_arch() -> str:
    return ARCH_NAMES.get(platform.machine().lower(), platform.machine().lower())


def require_platform(os_name: str | None = None) -> str:
    resolved = os_name or OS_NAME
    if resolved not in BROWSER_BINARY:
        raise RuntimeError(f"Camoufox is not supported on this platform: {sys.platform}")
    return resolved


def browser_asset_name(folder: str, os_name: str | None = None, arch: str | None = None) -> str:
    resolved = require_platform(os_name)
    resolved_arch = arch or browser_arch()
    if resolved_arch not in SUPPORTED_ARCHES[resolved]:
        raise RuntimeError(
            f"Camoufox has no {resolved_arch} build for {resolved}. "
            f"Supported: {', '.join(SUPPORTED_ARCHES[resolved])}."
        )
    return f"camoufox-{folder}-{resolved}.{resolved_arch}.zip"


def browser_binary(directory: Path, os_name: str | None = None) -> Path:
    return Path(directory) / BROWSER_BINARY[require_platform(os_name)]


def browser_is_installed(directory: Path, os_name: str | None = None) -> bool:
    resolved = require_platform(os_name)
    return all(
        (Path(directory) / required).is_file()
        for required in BROWSER_REQUIRED_FILES[resolved]
    )


def browser_root_for(executable_path, os_name: str | None = None) -> Path:
    """Directory whose ``distribution`` and ``defaults/pref`` Camoufox reads.

    The macOS executable lives in ``Camoufox.app/Contents/MacOS`` while Firefox reads
    its policy and autoconfig files from ``Contents/Resources``. On Windows and Linux
    the executable already sits in the browser root.
    """
    resolved = os_name or OS_NAME
    executable = Path(executable_path)
    if resolved == "mac" and executable.parent.name == "MacOS":
        return executable.parent.parent / "Resources"
    return executable.parent


def browser_process_names(os_name: str | None = None) -> tuple[str, ...]:
    resolved = os_name or OS_NAME
    return ("camoufox-bin",) if resolved == "lin" else ("camoufox",)


def data_directory(os_name: str | None = None) -> Path:
    """Per-user directory for mutable runtime data.

    Windows keeps the portable layout next to the executable; macOS and Linux
    installs are read-only (.app bundle, /opt, AppImage), so their data lives in
    the user profile. YELLOWFOX_DATA_DIR overrides the location everywhere.
    """
    override = os.environ.get("YELLOWFOX_DATA_DIR")
    if override:
        return Path(override).expanduser()

    resolved = os_name or OS_NAME
    if resolved == "win":
        return Path(__file__).resolve().parent.parent / "data"
    if resolved == "mac":
        return Path.home() / "Library" / "Application Support" / "YellowFox"

    xdg_data_home = os.environ.get("XDG_DATA_HOME")
    base = Path(xdg_data_home).expanduser() if xdg_data_home else Path.home() / ".local" / "share"
    return base / "YellowFox"


def camoufox_home(os_name: str | None = None) -> Path:
    """Where the Camoufox browsers and their version config are stored."""
    resolved = os_name or OS_NAME
    if resolved == "win":
        return Path(__file__).resolve().parent / ".camoufox"
    return data_directory(resolved) / "camoufox"
