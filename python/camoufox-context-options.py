#!/usr/bin/env python3
"""Generate Camoufox Playwright context options for YellowFox."""

import asyncio
import json
import re
import sys
from urllib.parse import quote
from yellowfox_camoufox_home import configure_camoufox_home

configure_camoufox_home()

import requests
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


def proxy_url_for_requests(proxy):
    if not isinstance(proxy, dict):
        return None

    server = str(proxy.get("server") or "").strip()
    if not server:
        return None

    if server.startswith("socks5://"):
        server = "socks5h://" + server[len("socks5://"):]

    username = proxy.get("username")
    password = proxy.get("password")
    if username and "@" not in server:
        scheme, rest = server.split("://", 1)
        server = f"{scheme}://{quote(str(username), safe='')}:{quote(str(password or ''), safe='')}@{rest}"

    return server


def fetch_proxy_geo_fallback(proxy):
    proxy_url = proxy_url_for_requests(proxy)
    if not proxy_url:
        return {}

    proxies = {
        "http": proxy_url,
        "https": proxy_url,
    }
    headers = {
        "User-Agent": "YellowFox/1.0",
    }

    for url in ("https://ipapi.co/json/",):
        try:
            response = requests.get(url, proxies=proxies, headers=headers, timeout=15)
            if response.ok:
                payload = response.json()
                return {
                    "ip": payload.get("ip"),
                    "timezone": payload.get("timezone"),
                    "country": payload.get("country_code") or payload.get("country"),
                }
        except Exception:
            pass

    ip = None
    for url in ("https://api64.ipify.org?format=json", "http://ifconfig.me/all.json"):
        try:
            response = requests.get(url, proxies=proxies, headers=headers, timeout=15)
            if not response.ok:
                continue
            payload = response.json()
            ip = payload.get("ip") or payload.get("ip_addr")
            if ip:
                break
        except Exception:
            pass

    if not ip:
        return {}

    try:
        response = requests.get(f"https://ipapi.co/{quote(ip, safe='')}/json/", headers=headers, timeout=15)
        if response.ok:
            payload = response.json()
            return {
                "ip": ip,
                "timezone": payload.get("timezone"),
                "country": payload.get("country_code") or payload.get("country"),
            }
    except Exception:
        pass

    return {"ip": ip}


async def main():
    if len(sys.argv) != 2:
        raise SystemExit("Usage: camoufox-context-options.py <config.json>")

    with open(sys.argv[1], "r", encoding="utf-8") as file:
        config = json.load(file)

    proxy = config.get("proxy")
    context_overrides = {}
    webrtc_ip = None
    if proxy:
        try:
            geo = await _resolve_proxy_geo(proxy)
        except Exception:
            geo = {}
        if not geo.get("timezone"):
            fallback_geo = fetch_proxy_geo_fallback(proxy)
            geo = {
                **fallback_geo,
                **{key: value for key, value in geo.items() if value},
            }
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
