#!/usr/bin/env python3
"""Install the YellowFox target Camoufox browser build with resumable download."""

from __future__ import annotations

import json
import argparse
import shutil
import time
import zipfile
from pathlib import Path

import requests
from yellowfox_camoufox_home import configure_camoufox_home

configure_camoufox_home()

from camoufox.multiversion import BROWSERS_DIR, COMPAT_FLAG, CONFIG_FILE, REPO_CACHE_FILE


TARGET_REPO = "coryking"
TARGET_VERSION = "142.0.1"
TARGET_BUILD = "fork.26"
TARGET_FOLDER = f"{TARGET_VERSION}-{TARGET_BUILD}"
TARGET_ACTIVE = f"browsers/{TARGET_REPO}/{TARGET_FOLDER}"
CHUNK_SIZE = 1024 * 256
MAX_ATTEMPTS = 30


def sync_repo_cache() -> None:
    from camoufox.__main__ import _do_sync

    _do_sync()


def load_repo_cache(force_sync: bool = False) -> dict:
    if force_sync or not REPO_CACHE_FILE.exists():
        sync_repo_cache()

    return json.loads(REPO_CACHE_FILE.read_text(encoding="utf-8"))


def find_repo(cache: dict) -> dict:
    for repo in cache.get("repos", []):
        if repo.get("name", "").lower() == TARGET_REPO:
            return repo

    raise RuntimeError(f"Camoufox repo not found: {TARGET_REPO}")


def asset_folder(asset: dict) -> str:
    return f"{asset.get('version')}-{asset.get('build')}"


def load_target_asset() -> dict:
    if not REPO_CACHE_FILE.exists():
        sync_repo_cache()

    cache = load_repo_cache()
    repo = find_repo(cache)
    for version in repo.get("versions", []):
        if version.get("version") == TARGET_VERSION and version.get("build") == TARGET_BUILD:
            return version

    sync_repo_cache()
    cache = load_repo_cache()
    repo = find_repo(cache)
    for version in repo.get("versions", []):
        if version.get("version") == TARGET_VERSION and version.get("build") == TARGET_BUILD:
            return version

    raise RuntimeError(f"Camoufox build not found: {TARGET_REPO}/{TARGET_FOLDER}")


def load_latest_asset() -> dict:
    cache = load_repo_cache(force_sync=True)
    repo = find_repo(cache)
    versions = [
        item for item in repo.get("versions", [])
        if item.get("url") and not bool(item.get("is_prerelease", False))
    ]
    if not versions:
        raise RuntimeError(f"No stable Camoufox builds found for repo: {TARGET_REPO}")

    return max(versions, key=lambda item: str(item.get("asset_updated_at") or ""))


def current_install_state() -> dict:
    config = json.loads(CONFIG_FILE.read_text(encoding="utf-8")) if CONFIG_FILE.exists() else {}
    active = str(config.get("active_version") or "")
    pinned = str(config.get("pinned") or "")
    current_folder = pinned or active.rsplit("/", 1)[-1]
    version_path = BROWSERS_DIR / TARGET_REPO / current_folder / "version.json" if current_folder else None
    metadata = {}
    if version_path and version_path.exists():
        metadata = json.loads(version_path.read_text(encoding="utf-8"))

    return {
        "active_version": active,
        "folder": current_folder,
        "version": metadata.get("version"),
        "build": metadata.get("build"),
        "asset_updated_at": metadata.get("asset_updated_at"),
        "installed": bool(version_path and version_path.exists()),
    }


def check_update() -> dict:
    latest = load_latest_asset()
    current = current_install_state()
    latest_folder = asset_folder(latest)
    return {
        "current": current,
        "latest": {
            "folder": latest_folder,
            "version": latest.get("version"),
            "build": latest.get("build"),
            "asset_updated_at": latest.get("asset_updated_at"),
            "is_prerelease": bool(latest.get("is_prerelease", False)),
        },
        "update_available": current.get("folder") != latest_folder,
    }


def download_with_resume(url: str, destination: Path, expected_size: int) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)

    for attempt in range(1, MAX_ATTEMPTS + 1):
        current_size = destination.stat().st_size if destination.exists() else 0
        if current_size == expected_size:
            return
        if current_size > expected_size:
            destination.unlink()
            current_size = 0

        headers = {"Range": f"bytes={current_size}-"} if current_size else {}
        print(f"Downloading Camoufox {TARGET_FOLDER}: attempt {attempt}, {current_size}/{expected_size} bytes")

        with requests.get(url, headers=headers, stream=True, timeout=(60, 120)) as response:
            if current_size and response.status_code == 200:
                destination.unlink(missing_ok=True)
                current_size = 0
            response.raise_for_status()

            mode = "ab" if current_size and response.status_code == 206 else "wb"
            with destination.open(mode) as file:
                for chunk in response.iter_content(chunk_size=CHUNK_SIZE):
                    if chunk:
                        file.write(chunk)

        downloaded = destination.stat().st_size if destination.exists() else 0
        if downloaded == expected_size:
            return

        time.sleep(5)

    downloaded = destination.stat().st_size if destination.exists() else 0
    raise RuntimeError(f"Download incomplete: {downloaded}/{expected_size} bytes")


def install_zip(zip_path: Path, asset: dict) -> None:
    folder = asset_folder(asset)
    active = f"browsers/{TARGET_REPO}/{folder}"
    install_path = BROWSERS_DIR / TARGET_REPO / folder
    version_path = install_path / "version.json"

    if version_path.exists():
        print(f"Camoufox {TARGET_REPO}/stable/{folder} already installed.")
    else:
        if install_path.exists():
            shutil.rmtree(install_path)
        install_path.mkdir(parents=True, exist_ok=True)

        print(f"Extracting Camoufox to {install_path}")
        with zipfile.ZipFile(zip_path) as archive:
            archive.extractall(install_path)

        metadata = {
            "asset_size": asset.get("asset_size"),
            "build": asset.get("build"),
            "version": asset.get("version"),
            "prerelease": bool(asset.get("is_prerelease", False)),
            "asset_id": asset.get("asset_id"),
            "asset_updated_at": asset.get("asset_updated_at"),
        }
        version_path.write_text(json.dumps(metadata, indent=2), encoding="utf-8")

    config = json.loads(CONFIG_FILE.read_text(encoding="utf-8")) if CONFIG_FILE.exists() else {}
    config["active_version"] = active
    config["channel"] = f"{TARGET_REPO}/stable"
    config["pinned"] = folder
    CONFIG_FILE.parent.mkdir(parents=True, exist_ok=True)
    CONFIG_FILE.write_text(json.dumps(config, indent=2), encoding="utf-8")
    COMPAT_FLAG.touch()

    print(f"Active Camoufox browser: {TARGET_REPO}/stable/{folder}")


def install_asset(asset: dict) -> None:
    folder = asset_folder(asset)
    expected_size = int(asset["asset_size"])
    zip_path = CONFIG_FILE.parent / f"camoufox-{folder}-win.x86_64.zip"
    version_path = BROWSERS_DIR / TARGET_REPO / folder / "version.json"
    if not version_path.exists():
        download_with_resume(asset["url"], zip_path, expected_size)
    install_zip(zip_path, asset)


def main() -> int:
    parser = argparse.ArgumentParser(description="Install or update YellowFox Camoufox browser builds.")
    parser.add_argument("--check-installed", action="store_true", help="Print JSON current installation status and exit.")
    parser.add_argument("--check-update", action="store_true", help="Print JSON update status and exit.")
    parser.add_argument("--install-latest", action="store_true", help="Install the latest stable Camoufox build.")
    args = parser.parse_args()

    if args.check_installed:
        print(json.dumps(current_install_state(), ensure_ascii=False))
        return 0

    if args.check_update:
        print(json.dumps(check_update(), ensure_ascii=False))
        return 0

    asset = load_latest_asset() if args.install_latest else load_target_asset()
    install_asset(asset)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
