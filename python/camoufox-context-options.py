#!/usr/bin/env python3
"""Generate Camoufox Playwright context options for YellowFox."""

import asyncio
import json
import re
import sys

from camoufox.async_api import _resolve_proxy_geo
from camoufox.fingerprints import generate_context_fingerprint
from camoufox.pkgman import installed_verstr


def normalize_os(value):
    lowered = (value or "windows").strip().lower()
    if lowered in ("mac", "macos"):
        return "macos"
    if lowered in ("linux", "lin"):
        return "linux"
    return "windows"


async def main():
    if len(sys.argv) != 2:
        raise SystemExit("Usage: camoufox-context-options.py <config.json>")

    with open(sys.argv[1], "r", encoding="utf-8") as file:
        config = json.load(file)

    proxy = config.get("proxy")
    context_overrides = {}
    webrtc_ip = None
    if proxy:
        geo = await _resolve_proxy_geo(proxy)
        webrtc_ip = geo.get("ip")
        if geo.get("timezone"):
            context_overrides["timezone_id"] = geo["timezone"]

    ff_version = installed_verstr().split(".", 1)[0]
    fingerprint = generate_context_fingerprint(
        os=normalize_os(config.get("os")),
        ff_version=ff_version,
        webrtc_ip=webrtc_ip,
    )

    context_options = {
        **fingerprint.get("context_options", {}),
        **context_overrides,
    }
    init_script = fingerprint.get("init_script") or ""
    if context_overrides.get("timezone_id"):
        init_script = re.sub(
            r'if \(typeof w\.setTimezone === "function"\) w\.setTimezone\([^\n]*\);',
            f'if (typeof w.setTimezone === "function") w.setTimezone({json.dumps(context_overrides["timezone_id"])});',
            init_script,
            count=1,
        )

    print(json.dumps({
        "contextOptions": context_options,
        "initScript": init_script,
        "geoIp": webrtc_ip,
        "camoufoxConfig": {
            **({"webrtc:ipv4": webrtc_ip} if webrtc_ip else {}),
            **({"timezone": context_overrides["timezone_id"]} if context_overrides.get("timezone_id") else {}),
        },
    }), flush=True)


if __name__ == "__main__":
    asyncio.run(main())
