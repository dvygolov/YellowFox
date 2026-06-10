"use strict";

const SOURCE = "yellowfox-ad-library-video-downloader";
const BUTTON_CLASS = "yellowfox-adlib-download-button";
const ANCHOR_CLASS = "yellowfox-adlib-download-anchor";
const ATTACHED_ATTR = "data-yellowfox-adlib-download-attached";
const adsById = new Map();
const videosByUrlToken = new Map();

function injectHook() {
  const script = document.createElement("script");
  script.src = browser.runtime.getURL("page-hook.js");
  script.onload = () => script.remove();
  (document.documentElement || document.head).appendChild(script);
}

function parsePayloadText(text) {
  if (!text || typeof text !== "string") {
    return;
  }
  try {
    const json = JSON.parse(text.replace(/^for\s*\(;;\);/, ""));
    ingestAds(findAds(json));
  } catch {}
}

function parseInitialScripts() {
  const scripts = document.querySelectorAll('script[type="application/json"][data-sjs]');
  for (const script of scripts) {
    parsePayloadText(script.textContent || "");
  }
}

function findAds(value, out = []) {
  if (!value || typeof value !== "object") {
    return out;
  }
  if (Array.isArray(value)) {
    for (const item of value) {
      findAds(item, out);
    }
    return out;
  }
  if (Array.isArray(value.collated_results)) {
    for (const ad of value.collated_results) {
      if (ad && typeof ad === "object" && ad.ad_archive_id) {
        out.push(ad);
      }
    }
  }
  for (const child of Object.values(value)) {
    findAds(child, out);
  }
  return out;
}

function ingestAds(ads) {
  let changed = false;
  for (const ad of ads) {
    const normalized = normalizeAd(ad);
    if (!normalized || !normalized.id) {
      continue;
    }
    adsById.set(normalized.id, normalized);
    for (const video of normalized.videos) {
      for (const url of [video.hdUrl, video.sdUrl, video.previewUrl]) {
        const token = urlToken(url);
        if (token) {
          videosByUrlToken.set(token, normalized);
        }
      }
    }
    changed = true;
  }
  if (changed) {
    attachButtons();
  }
}

function normalizeAd(ad) {
  const snapshot = ad.snapshot || {};
  const rawVideos = Array.isArray(snapshot.videos) ? snapshot.videos : [];
  const videos = rawVideos.map((video, index) => ({
    index,
    hdUrl: video.video_hd_url || null,
    sdUrl: video.video_sd_url || null,
    previewUrl: video.video_preview_image_url || null
  }));

  return {
    id: String(ad.ad_archive_id || ""),
    pageName: snapshot.page_name || ad.page_name || "",
    title: snapshot.title || "",
    videos
  };
}

function urlToken(url) {
  if (!url || typeof url !== "string") {
    return "";
  }
  try {
    const parsed = new URL(url);
    const file = parsed.pathname.split("/").pop() || "";
    return file.replace(/\.mp4$/i, "");
  } catch {
    const match = url.match(/\/([^/?#]+)\.mp4/i);
    return match ? match[1] : "";
  }
}

function findLibraryId(video) {
  let node = video;
  for (let depth = 0; node && depth < 12; depth += 1, node = node.parentElement) {
    const text = node.innerText || node.textContent || "";
    const match = text.match(/Library ID:\s*([0-9]+)/i);
    if (match) {
      return match[1];
    }
  }
  return "";
}

function findAdForVideo(video) {
  const id = findLibraryId(video);
  if (id && adsById.has(id)) {
    return adsById.get(id);
  }

  for (const candidate of [
    video.currentSrc,
    video.src,
    video.getAttribute("src"),
    video.poster,
    video.getAttribute("poster")
  ]) {
    const token = urlToken(candidate);
    if (token && videosByUrlToken.has(token)) {
      return videosByUrlToken.get(token);
    }
  }

  return null;
}

function chooseVideo(ad, videoElement) {
  if (!ad || !Array.isArray(ad.videos) || ad.videos.length === 0) {
    return null;
  }

  const elementTokens = [
    videoElement.currentSrc,
    videoElement.src,
    videoElement.getAttribute("src"),
    videoElement.poster,
    videoElement.getAttribute("poster")
  ].map(urlToken).filter(Boolean);

  for (const video of ad.videos) {
    const tokens = [video.hdUrl, video.sdUrl, video.previewUrl].map(urlToken).filter(Boolean);
    if (tokens.some((token) => elementTokens.includes(token))) {
      return video;
    }
  }

  return ad.videos[0];
}

function makeFileName(ad, video, quality) {
  const title = (ad.title || ad.pageName || "ad").replace(/[\\/:*?"<>|]+/g, "_").trim();
  const safeTitle = title ? `_${title.slice(0, 80)}` : "";
  return `Meta Ad Library/${ad.id}_${video.index}_${quality}${safeTitle}.mp4`;
}

async function handleDownloadClick(button, videoElement) {
    const ad = findAdForVideo(videoElement);
    const video = chooseVideo(ad, videoElement);
  const fallbackUrl = [videoElement.currentSrc, videoElement.src, videoElement.getAttribute("src")]
    .find((candidate) => typeof candidate === "string" && /^https:\/\/[^/]*fbcdn\.net\/.*\.mp4/i.test(candidate));
  const url = (video && (video.hdUrl || video.sdUrl)) || fallbackUrl;
  const quality = video && video.hdUrl ? "hd" : (video && video.sdUrl ? "sd" : "visible");

  if (!url) {
    button.textContent = "Нет URL";
    setTimeout(() => {
      button.textContent = "Download";
      button.disabled = false;
    }, 1800);
    return;
  }

  button.disabled = true;
  button.textContent = quality === "hd" ? "HD..." : "Скач...";
  try {
    await browser.runtime.sendMessage({
      type: "yellowfox-ad-library-download",
      url,
      filename: ad && video
        ? makeFileName(ad, video, quality)
        : `Meta Ad Library/video_${Date.now()}_${quality}.mp4`
    });
    button.textContent = "Готово";
  } catch {
    button.textContent = "Ошибка";
  } finally {
    setTimeout(() => {
      button.textContent = "Download";
      button.disabled = false;
    }, 1800);
  }
}

function pickAnchor(video) {
  let anchor = video.parentElement || video;
  for (let depth = 0; anchor.parentElement && depth < 4; depth += 1) {
    const style = window.getComputedStyle(anchor);
    const rect = anchor.getBoundingClientRect();
    if (style.position !== "static" && rect.width >= video.clientWidth && rect.height >= video.clientHeight) {
      return anchor;
    }
    anchor = anchor.parentElement;
  }
  return video.parentElement || video;
}

function attachButton(video) {
  if (video.getAttribute(ATTACHED_ATTR) === "1") {
    return;
  }
  video.setAttribute(ATTACHED_ATTR, "1");

  const anchor = pickAnchor(video);
  anchor.classList.add(ANCHOR_CLASS);

  const button = document.createElement("button");
  button.type = "button";
  button.className = BUTTON_CLASS;
  button.textContent = "Download";
  button.title = "Download video in the best available quality";
  button.addEventListener("click", (event) => {
    event.preventDefault();
    event.stopPropagation();
    handleDownloadClick(button, video);
  });
  anchor.appendChild(button);
}

function attachButtons() {
  for (const video of document.querySelectorAll("video")) {
    attachButton(video);
  }
}

function startObservers() {
  const observer = new MutationObserver(() => {
    parseInitialScripts();
    attachButtons();
  });
  observer.observe(document.documentElement, {
    childList: true,
    subtree: true
  });

  window.addEventListener("message", (event) => {
    if (event.source !== window || !event.data || event.data.source !== SOURCE) {
      return;
    }
    if (event.data.type === "graphql-payload") {
      parsePayloadText(event.data.text);
    }
  });
}

injectHook();
startObservers();

if (document.readyState === "loading") {
  document.addEventListener("DOMContentLoaded", () => {
    parseInitialScripts();
    attachButtons();
  }, { once: true });
} else {
  parseInitialScripts();
  attachButtons();
}
