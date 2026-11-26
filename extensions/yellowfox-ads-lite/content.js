// YellowFox Ads Lite — text-based hiding for Ads Manager clutter that has no
// stable selectors (Meta ships anonymous divs with randomized x-classes).
//
// Hiding rules (verified against live DOM):
//  - rotating education/announcement banner strips at the top of the app
//  - "Meta AI business assistant" entry in the left nav
// Table, editor, toolbars, menus, notification bell are never touched.
//
// Notes: Facebook's CSP blocks injecting moz-extension:// script tags, so
// page-world hooks are off the table; gateway websockets (~2KB, idle) are
// left alone — Firefox webRequest cannot cancel them and a fake socket risks
// breaking Meta's MQTT reconnect loop.

"use strict";

const HIDE_TEXTS = [
  "we're evolving how we measure results",
  "meta ai business assistant",
  "starting november 6, 2026", // education banner body variant
];

const MARK_ATTR = "data-yf-lite-hidden";
const MAX_CLIMB = 8;

function isHidden(el) {
  return el.hasAttribute && el.hasAttribute(MARK_ATTR);
}

function hide(el) {
  if (!el || !el.style) return;
  el.style.display = "none";
  el.setAttribute(MARK_ATTR, "1");
}

// Climb from a text node's parent to the container worth hiding:
// the first ancestor that is a nav listitem, or a near-full-width strip.
function findStrip(startEl) {
  const viewportWidth = window.innerWidth || document.documentElement.clientWidth;
  let el = startEl;
  let climb = 0;
  let fallback = null;
  while (el && el !== document.body && climb < MAX_CLIMB) {
    const rect = el.getBoundingClientRect();
    if (el.getAttribute && el.getAttribute("role") === "listitem") {
      return el;
    }
    if (!fallback && rect.width >= viewportWidth * 0.85) {
      fallback = el;
    }
    el = el.parentElement;
    climb++;
  }
  return fallback;
}

function scan(root) {
  if (!root) return;
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
    acceptNode(node) {
      const parent = node.parentElement;
      if (!parent || isHidden(parent)) return NodeFilter.FILTER_REJECT;
      const value = node.nodeValue.trim().toLowerCase();
      if (!value || value.length > 160) return NodeFilter.FILTER_REJECT;
      for (const text of HIDE_TEXTS) {
        if (value.includes(text)) return NodeFilter.FILTER_ACCEPT;
      }
      return NodeFilter.FILTER_REJECT;
    },
  });

  const matches = [];
  let node;
  while ((node = walker.nextNode())) {
    if (matches.length < 24) matches.push(node);
  }
  for (const match of matches) {
    const strip = findStrip(match.parentElement);
    if (strip) hide(strip);
  }
}

function start() {
  scan(document.body);
  const observer = new MutationObserver((mutations) => {
    for (const m of mutations) {
      if (m.type === "childList") {
        for (const added of m.addedNodes) {
          if (added.nodeType === Node.ELEMENT_NODE) scan(added);
        }
      } else if (m.type === "characterData" && m.target.parentElement) {
        scan(m.target.parentElement);
      }
    }
  });
  observer.observe(document.body, { childList: true, subtree: true, characterData: true });
}

if (document.readyState === "loading") {
  document.addEventListener("DOMContentLoaded", start);
} else {
  start();
}
