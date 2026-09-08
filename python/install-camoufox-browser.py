#!/usr/bin/env python3
"""Install official Windows x64 Camoufox releases beside the active browser."""
from __future__ import annotations

import argparse
import contextlib
import hashlib
import json
import os
import re
import sys
import tempfile
import time
import zipfile
from pathlib import Path

import requests
from yellowfox_camoufox_home import configure_camoufox_home

configure_camoufox_home()
from camoufox.multiversion import BROWSERS_DIR, COMPAT_FLAG, CONFIG_FILE

TARGET_REPO = "official"
GITHUB_REPO = "daijro/camoufox"
FOLDER_PATTERN = re.compile(r"\d+(?:\.\d+)+-[A-Za-z]+\.\d+")
CHUNK_SIZE = 1024 * 256


def progress(message):
    print(message, file=sys.stderr, flush=True)


def read_config():
    return json.loads(CONFIG_FILE.read_text(encoding="utf-8")) if CONFIG_FILE.exists() else {}


def atomic_json(path, payload):
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary = tempfile.mkstemp(dir=path.parent, prefix=path.name + ".", suffix=".tmp")
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as handle:
            json.dump(payload, handle, indent=2)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, path)
    finally:
        Path(temporary).unlink(missing_ok=True)


def version_key(folder):
    if not FOLDER_PATTERN.fullmatch(folder):
        raise ValueError(f"Invalid browser version: {folder}")
    version, build = folder.split("-", 1)
    return tuple(int(n) for n in version.split(".")), int(build.rsplit(".", 1)[1])


def asset_folder(asset):
    folder = f"{asset['version']}-{asset['build']}"
    version_key(folder)
    return folder


def asset_from_release(release):
    if release.get("draft") or release.get("prerelease"):
        raise RuntimeError("The release is not marked stable by its publisher.")
    folder = str(release.get("tag_name", "")).removeprefix("v")
    version_key(folder)
    name = f"camoufox-{folder}-win.x86_64.zip"
    for item in release.get("assets", []):
        if item.get("name") != name or item.get("state") != "uploaded":
            continue
        digest = item.get("digest") or ""
        if not re.fullmatch(r"sha256:[0-9a-fA-F]{64}", digest):
            raise RuntimeError("GitHub did not provide a SHA-256 digest for this release.")
        url = item["browser_download_url"]
        if not url.startswith(f"https://github.com/{GITHUB_REPO}/releases/download/"):
            raise RuntimeError("Unexpected browser download URL.")
        version, build = folder.split("-", 1)
        return dict(version=version, build=build, url=url, digest=digest.lower(),
                    asset_size=int(item["size"]), asset_id=item["id"],
                    asset_updated_at=item["updated_at"], is_prerelease=False)
    raise RuntimeError("This release has no Windows x64 browser archive yet.")


def load_latest_asset(folder=None):
    if folder:
        version_key(folder)
    suffix = "tags/v" + folder if folder else "latest"
    response = requests.get(f"https://api.github.com/repos/{GITHUB_REPO}/releases/{suffix}",
                            headers={"Accept": "application/vnd.github+json", "User-Agent": "YellowFox-Updater"},
                            timeout=(10, 25))
    response.raise_for_status()
    return asset_from_release(response.json())


def current_install_state():
    config = read_config()
    active = str(config.get("active_version") or "")
    parts = active.replace("\\", "/").split("/")
    repo = parts[-2] if len(parts) == 3 and parts[0] == "browsers" else str(config.get("channel") or "").split("/")[0]
    folder = str(config.get("pinned") or (parts[-1] if len(parts) == 3 else ""))
    metadata = {}
    installed = False
    if re.fullmatch(r"[a-zA-Z0-9_-]+", repo) and FOLDER_PATTERN.fullmatch(folder):
        directory = BROWSERS_DIR / repo / folder
        if (directory / "version.json").exists():
            metadata = json.loads((directory / "version.json").read_text(encoding="utf-8"))
            installed = (directory / "camoufox.exe").is_file()
    return dict(active_version=active, repo=repo, folder=folder, installed=installed,
                version=metadata.get("version"), build=metadata.get("build"),
                asset_updated_at=metadata.get("asset_updated_at"))


def is_update_available(current, latest):
    if not current["installed"]:
        return True
    return version_key(asset_folder(latest)) > version_key(current["folder"])


def check_update():
    latest = load_latest_asset()
    current = current_install_state()
    return dict(current=current, latest={**latest, "folder": asset_folder(latest)},
                update_available=is_update_available(current, latest))


def download_with_resume(url, destination, expected_size):
    destination.parent.mkdir(parents=True, exist_ok=True)
    for attempt in range(1, 4):
        size = destination.stat().st_size if destination.exists() else 0
        if size == expected_size:
            return
        if size > expected_size:
            destination.unlink()
            size = 0
        try:
            with requests.get(url, headers={"Range": f"bytes={size}-"} if size else {},
                              stream=True, timeout=(15, 60)) as response:
                response.raise_for_status()
                if response.status_code == 206:
                    if not response.headers.get("Content-Range", "").startswith(f"bytes {size}-"):
                        raise RuntimeError("Invalid download resume range.")
                elif response.status_code == 200:
                    size = 0
                else:
                    raise RuntimeError(f"Unexpected download status: {response.status_code}")
                last_percent = -1
                with destination.open("ab" if size else "wb") as handle:
                    for chunk in response.iter_content(CHUNK_SIZE):
                        if not chunk:
                            continue
                        handle.write(chunk)
                        size += len(chunk)
                        percent = size * 100 // expected_size
                        if percent != last_percent:
                            progress(f"Загрузка ядра: {percent}% ({size // 1048576} / {expected_size // 1048576} МБ)")
                            last_percent = percent
            if size == expected_size:
                return
        except requests.RequestException:
            if attempt == 3:
                raise
        if attempt < 3:
            progress("Соединение прервано. Повторная загрузка…")
            time.sleep(2)
    raise RuntimeError("Incomplete browser download.")


def verify_archive(path, asset):
    progress("Проверка SHA-256…")
    with path.open("rb") as handle:
        actual = "sha256:" + hashlib.file_digest(handle, "sha256").hexdigest()
    if path.stat().st_size != asset["asset_size"] or actual != asset["digest"]:
        path.unlink(missing_ok=True)
        raise RuntimeError("Archive checksum mismatch. Retry the update to download a fresh copy.")


@contextlib.contextmanager
def update_lock():
    CONFIG_FILE.parent.mkdir(parents=True, exist_ok=True)
    with (CONFIG_FILE.parent / "update.lock").open("a+b") as handle:
        handle.seek(0)
        if os.name == "nt":
            import msvcrt
            if not handle.read(1):
                handle.write(b"0")
                handle.flush()
            handle.seek(0)
            try:
                msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
            except OSError as error:
                raise RuntimeError("Another browser update is already running.") from error
        else:
            import fcntl
            fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
        try:
            yield
        finally:
            if os.name == "nt":
                handle.seek(0)
                msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)


def install_zip(zip_path, asset):
    folder = asset_folder(asset)
    parent = BROWSERS_DIR / TARGET_REPO
    target = parent / folder
    parent.mkdir(parents=True, exist_ok=True)
    if not (target / "version.json").exists() or not (target / "camoufox.exe").exists():
        verify_archive(zip_path, asset)
        progress("Распаковка файлов ядра…")
        with tempfile.TemporaryDirectory(dir=parent, prefix=".install-") as temporary:
            staging = Path(temporary) / "browser"
            staging.mkdir()
            with zipfile.ZipFile(zip_path) as archive:
                for member in archive.infolist():
                    resolved = (staging / member.filename.replace("\\", "/")).resolve()
                    if not resolved.is_relative_to(staging.resolve()) or (member.external_attr >> 16) & 0o170000 == 0o120000:
                        raise RuntimeError("Unsafe browser archive entry.")
                archive.extractall(staging)
            for required in ("camoufox.exe", "application.ini", "omni.ja"):
                if not (staging / required).is_file():
                    raise RuntimeError(f"Incomplete browser archive: {required} missing.")
            atomic_json(staging / "version.json", {**asset, "prerelease": False})
            if target.exists():
                # Preserve an incomplete previous install rather than touching active files.
                os.replace(target, parent / (folder + ".incomplete-" + str(time.time_ns())))
            os.replace(staging, target)
    progress("Переключение на новое ядро…")
    config = read_config()
    active = f"browsers/{TARGET_REPO}/{folder}"
    if config.get("active_version") != active:
        config["previous_version"] = config.get("active_version")
    config.update(active_version=active, channel=f"{TARGET_REPO}/stable", pinned=folder)
    COMPAT_FLAG.touch()
    atomic_json(CONFIG_FILE, config)
    return current_install_state()


def install_asset(asset):
    with update_lock():
        current = current_install_state()
        if current["installed"] and version_key(current["folder"]) > version_key(asset_folder(asset)):
            raise RuntimeError("Refusing to downgrade the active browser.")
        archive = CONFIG_FILE.parent / f"camoufox-{asset_folder(asset)}-win.x86_64.zip"
        target = BROWSERS_DIR / TARGET_REPO / asset_folder(asset)
        if not (target / "version.json").exists() or not (target / "camoufox.exe").exists():
            download_with_resume(asset["url"], archive, asset["asset_size"])
        return install_zip(archive, asset)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    action = parser.add_mutually_exclusive_group()
    action.add_argument("--check-installed", action="store_true")
    action.add_argument("--check-update", action="store_true")
    action.add_argument("--install-latest", action="store_true")
    action.add_argument("--install-version", metavar="VERSION-BUILD")
    args = parser.parse_args()
    if args.check_installed:
        result = current_install_state()
    elif args.check_update:
        result = check_update()
    else:
        result = install_asset(load_latest_asset(args.install_version))
    print(json.dumps(result, ensure_ascii=False), flush=True)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        progress(str(error))
        raise SystemExit(1)
