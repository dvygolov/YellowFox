"use strict";

const DEFAULT_FILE_NAME = "meta-ad-library-video.mp4";

function safeFileName(value) {
  const cleaned = String(value || DEFAULT_FILE_NAME)
    .replace(/[\\/:*?"<>|]+/g, "_")
    .replace(/\s+/g, "_")
    .replace(/^_+|_+$/g, "");
  return cleaned || DEFAULT_FILE_NAME;
}

browser.runtime.onMessage.addListener(async (message) => {
  if (!message || message.type !== "yellowfox-ad-library-download") {
    return undefined;
  }

  const url = typeof message.url === "string" ? message.url : "";
  if (!/^https:\/\/[^/]*fbcdn\.net\//i.test(url)) {
    throw new Error("Unsupported video URL.");
  }

  const filename = safeFileName(message.filename);
  const downloadId = await browser.downloads.download({
    url,
    filename,
    conflictAction: "uniquify",
    saveAs: false
  });

  return { downloadId };
});
