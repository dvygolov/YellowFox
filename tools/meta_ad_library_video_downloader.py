#!/usr/bin/env python
"""Download video creatives from a Meta/Facebook Ad Library result page.

The Ad Library UI does not expose a download button, but its Relay payload
contains snapshot.videos[].video_hd_url and video_sd_url for video ads.
"""

from __future__ import annotations

import argparse
import asyncio
import json
import re
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from playwright.async_api import async_playwright


DEFAULT_OUTPUT = Path(".artifacts/meta-ad-library-videos")


EXTRACT_ADS_JS = r"""
() => {
  function walk(obj, out = []) {
    if (!obj || typeof obj !== 'object') return out;
    if (Array.isArray(obj)) {
      for (const item of obj) walk(item, out);
      return out;
    }
    if (Array.isArray(obj.collated_results)) {
      for (const ad of obj.collated_results) {
        if (ad && typeof ad === 'object' && ad.ad_archive_id) out.push(ad);
      }
    }
    for (const value of Object.values(obj)) walk(value, out);
    return out;
  }

  const ads = [];
  for (const script of document.querySelectorAll('script[type="application/json"][data-sjs]')) {
    try {
      ads.push(...walk(JSON.parse(script.textContent || '')));
    } catch {}
  }
  return ads;
}
"""


def walk_for_ads(value: Any, out: list[dict[str, Any]]) -> None:
    if isinstance(value, dict):
        collated = value.get("collated_results")
        if isinstance(collated, list):
            for ad in collated:
                if isinstance(ad, dict) and ad.get("ad_archive_id"):
                    out.append(ad)
        for child in value.values():
            walk_for_ads(child, out)
    elif isinstance(value, list):
        for child in value:
            walk_for_ads(child, out)


def normalize_ad(ad: dict[str, Any]) -> dict[str, Any]:
    snapshot = ad.get("snapshot") or {}
    videos = []
    for index, video in enumerate(snapshot.get("videos") or []):
        if not isinstance(video, dict):
            continue
        videos.append(
            {
                "index": index,
                "video_hd_url": video.get("video_hd_url"),
                "video_sd_url": video.get("video_sd_url"),
                "video_preview_image_url": video.get("video_preview_image_url"),
                "watermarked_video_hd_url": video.get("watermarked_video_hd_url"),
                "watermarked_video_sd_url": video.get("watermarked_video_sd_url"),
            }
        )

    body = snapshot.get("body") or {}
    return {
        "ad_archive_id": ad.get("ad_archive_id"),
        "page_id": ad.get("page_id"),
        "page_name": snapshot.get("page_name") or ad.get("page_name"),
        "display_format": snapshot.get("display_format"),
        "title": snapshot.get("title"),
        "body": body.get("text") if isinstance(body, dict) else None,
        "caption": snapshot.get("caption"),
        "cta_text": snapshot.get("cta_text"),
        "link_url": snapshot.get("link_url"),
        "start_date": ad.get("start_date"),
        "end_date": ad.get("end_date"),
        "publisher_platform": ad.get("publisher_platform"),
        "videos": videos,
    }


def choose_video_url(video: dict[str, Any], quality: str) -> tuple[str | None, str | None]:
    if quality == "hd":
        return video.get("video_hd_url"), "hd"
    if quality == "sd":
        return video.get("video_sd_url"), "sd"
    if video.get("video_hd_url"):
        return video.get("video_hd_url"), "hd"
    if video.get("video_sd_url"):
        return video.get("video_sd_url"), "sd"
    return None, None


def safe_name(value: str) -> str:
    value = re.sub(r"[^a-zA-Z0-9._-]+", "_", value.strip())
    return value.strip("._") or "video"


async def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("url", help="Meta Ad Library result URL")
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--quality", choices=["best", "hd", "sd"], default="best")
    parser.add_argument("--scrolls", type=int, default=8)
    parser.add_argument("--wait-ms", type=int, default=8000)
    parser.add_argument("--headed", action="store_true")
    parser.add_argument("--no-download", action="store_true")
    args = parser.parse_args()

    args.output.mkdir(parents=True, exist_ok=True)

    raw_ads: list[dict[str, Any]] = []
    response_tasks: set[asyncio.Task[None]] = set()

    async with async_playwright() as pw:
        browser = await pw.chromium.launch(headless=not args.headed)
        context = await browser.new_context(
            locale="en-US",
            viewport={"width": 1365, "height": 900},
        )
        page = await context.new_page()

        async def capture_response(response: Any) -> None:
            if "/api/graphql" not in response.url:
                return
            try:
                text = await response.text()
                data = json.loads(text.removeprefix("for (;;);"))
            except Exception:
                return
            walk_for_ads(data, raw_ads)

        def schedule_response(response: Any) -> None:
            task = asyncio.create_task(capture_response(response))
            response_tasks.add(task)
            task.add_done_callback(response_tasks.discard)

        page.on("response", schedule_response)

        await page.goto(args.url, wait_until="domcontentloaded", timeout=60_000)
        await page.wait_for_timeout(args.wait_ms)
        for _ in range(max(args.scrolls, 0)):
            await page.mouse.wheel(0, 1400)
            await page.wait_for_timeout(1500)

        raw_ads.extend(await page.evaluate(EXTRACT_ADS_JS))
        if response_tasks:
            await asyncio.gather(*response_tasks, return_exceptions=True)

        ads_by_id: dict[str, dict[str, Any]] = {}
        for raw_ad in raw_ads:
            ad = normalize_ad(raw_ad)
            ad_id = ad.get("ad_archive_id")
            if ad_id:
                ads_by_id.setdefault(str(ad_id), ad)

        ads = list(ads_by_id.values())
        downloads = []
        if not args.no_download:
            for ad in ads:
                for video in ad["videos"]:
                    chosen_url, chosen_quality = choose_video_url(video, args.quality)
                    video["chosen_quality"] = chosen_quality
                    if not chosen_url:
                        continue
                    file_name = safe_name(f"{ad['ad_archive_id']}_{video['index']}_{chosen_quality}.mp4")
                    file_path = args.output / file_name
                    response = await context.request.get(
                        chosen_url,
                        headers={"referer": page.url},
                        timeout=120_000,
                    )
                    entry = {
                        "ad_archive_id": ad["ad_archive_id"],
                        "file": str(file_path),
                        "quality": chosen_quality,
                        "status": response.status,
                    }
                    if response.ok:
                        body = await response.body()
                        file_path.write_bytes(body)
                        video["downloaded_file"] = str(file_path)
                        video["downloaded_bytes"] = len(body)
                        entry["bytes"] = len(body)
                    downloads.append(entry)

        manifest = {
            "source_url": args.url,
            "extracted_at": datetime.now(timezone.utc).isoformat(),
            "ads": ads,
            "downloads": downloads,
        }
        manifest_path = args.output / "manifest.json"
        manifest_path.write_text(json.dumps(manifest, indent=2, ensure_ascii=False), encoding="utf-8")

        await browser.close()

    video_count = sum(len(ad["videos"]) for ad in ads)
    ok_count = sum(1 for item in downloads if item.get("bytes"))
    print(
        json.dumps(
            {
                "output": str(args.output.resolve()),
                "manifest": str(manifest_path.resolve()),
                "ads": len(ads),
                "videos": video_count,
                "downloaded": ok_count,
            },
            indent=2,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
