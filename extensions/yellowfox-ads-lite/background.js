// YellowFox Ads Lite — network-level relief for Facebook Ads Manager.
// Blocks growth nudges, client-side tips, help-tray content, notification
// feeds and telemetry that Ads Manager loads on every page view.
// Core Ads Manager data paths (graphql, bootloader-endpoint, adsmanager-graph)
// are NOT touched, so campaign management keeps working.
//
// Known limit: gateway.facebook.com websockets cannot be cancelled via
// webRequest in Firefox; they are left alone (~2KB, idle after handshake).

"use strict";

const BLOCK_PATTERNS = [
  // Telemetry / logging
  /\/ajax\/bz\b/,
  /\/ajax\/qm\b/,
  // Growth nudges & client-side tips (AYMT)
  /ads\/growth\/aymt\//,
  // Contextual help content (metric tooltips, help tray)
  /ads\/help_tray\/ajax\/help_content\//,
  // Notification badge/jewel feed
  /business\/navigation\/notifications\//,
];

browser.webRequest.onBeforeRequest.addListener(
  (details) => {
    for (const pattern of BLOCK_PATTERNS) {
      if (pattern.test(details.url)) {
        return { cancel: true };
      }
    }
    return {};
  },
  { urls: ["https://adsmanager.facebook.com/*", "https://*.facebook.com/*"] },
  ["blocking"]
);
