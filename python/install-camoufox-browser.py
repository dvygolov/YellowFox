#!/usr/bin/env python3
"""Install the YellowFox target Camoufox browser build with resumable download."""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import time
import zipfile
from pathlib import Path

import requests
from camoufox.multiversion import BROWSERS_DIR, COMPAT_FLAG, CONFIG_FILE, REPO_CACHE_FILE


TARGET_REPO = "coryking"
TARGET_VERSION = "142.0.1"
TARGET_BUILD = "fork.26"
TARGET_FOLDER = f"{TARGET_VERSION}-{TARGET_BUILD}"
TARGET_ACTIVE = f"browsers/{TARGET_REPO}/{TARGET_FOLDER}"
CHUNK_SIZE = 1024 * 256
MAX_ATTEMPTS = 30


def sync_repo_cache() -> None:
    env = os.environ.copy()
    env["PYTHONUTF8"] = "1"
    subprocess.run(
        [sys.executable, "-m", "camoufox", "sync"],
        check=True,
        env=env,
    )


def load_target_asset() -> dict:
    if not REPO_CACHE_FILE.exists():
        sync_repo_cache()

    cache = json.loads(REPO_CACHE_FILE.read_text(encoding="utf-8"))
    for repo in cache.get("repos", []):
        if repo.get("name", "").lower() != TARGET_REPO:
            continue

        for version in repo.get("versions", []):
            if version.get("version") == TARGET_VERSION and version.get("build") == TARGET_BUILD:
                return version

    sync_repo_cache()
    cache = json.loads(REPO_CACHE_FILE.read_text(encoding="utf-8"))
    for repo in cache.get("repos", []):
        if repo.get("name", "").lower() != TARGET_REPO:
            continue

        for version in repo.get("versions", []):
            if version.get("version") == TARGET_VERSION and version.get("build") == TARGET_BUILD:
                return version

    raise RuntimeError(f"Camoufox build not found: {TARGET_REPO}/{TARGET_FOLDER}")


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
    install_path = BROWSERS_DIR / TARGET_REPO / TARGET_FOLDER
    version_path = install_path / "version.json"

    if version_path.exists():
        print(f"Camoufox {TARGET_REPO}/stable/{TARGET_FOLDER} already installed.")
    else:
        if install_path.exists():
            shutil.rmtree(install_path)
        install_path.mkdir(parents=True, exist_ok=True)

        print(f"Extracting Camoufox to {install_path}")
        with zipfile.ZipFile(zip_path) as archive:
            archive.extractall(install_path)

        metadata = {
            "asset_size": asset.get("asset_size"),
            "build": TARGET_BUILD,
            "version": TARGET_VERSION,
            "prerelease": bool(asset.get("is_prerelease", False)),
            "asset_id": asset.get("asset_id"),
            "asset_updated_at": asset.get("asset_updated_at"),
        }
        version_path.write_text(json.dumps(metadata, indent=2), encoding="utf-8")

    config = json.loads(CONFIG_FILE.read_text(encoding="utf-8")) if CONFIG_FILE.exists() else {}
    config["active_version"] = TARGET_ACTIVE
    config["channel"] = f"{TARGET_REPO}/stable"
    config["pinned"] = TARGET_FOLDER
    CONFIG_FILE.parent.mkdir(parents=True, exist_ok=True)
    CONFIG_FILE.write_text(json.dumps(config, indent=2), encoding="utf-8")
    COMPAT_FLAG.touch()

    print(f"Active Camoufox browser: {TARGET_REPO}/stable/{TARGET_FOLDER}")


def main() -> int:
    asset = load_target_asset()
    expected_size = int(asset["asset_size"])
    zip_path = CONFIG_FILE.parent / f"camoufox-{TARGET_FOLDER}-win.x86_64.zip"
    version_path = BROWSERS_DIR / TARGET_REPO / TARGET_FOLDER / "version.json"
    if not version_path.exists():
        download_with_resume(asset["url"], zip_path, expected_size)
    install_zip(zip_path, asset)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
