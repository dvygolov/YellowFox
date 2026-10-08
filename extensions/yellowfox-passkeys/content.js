"use strict";

// YellowFox ships a per-profile software (virtual) platform authenticator
// instead of relying on Windows Hello. Firefox's software authenticator rejects
// create() requests that force `authenticatorSelection.authenticatorAttachment`
// to "platform" -- which is exactly what most passkey UIs (Meta, Google) send.
//
// Camoufox evaluates Playwright init scripts inside an Xray sandbox, so they
// cannot patch page JS state. A content script can still inject a page-world
// script, which is the same mechanism the Meta Ad Library Video Downloader uses.
(function () {
  if (window.__yellowfoxPasskeysInjected) {
    return;
  }
  window.__yellowfoxPasskeysInjected = true;

  function inject() {
    const root = document.documentElement || document.head || document.body;
    if (!root) {
      return false;
    }
    const script = document.createElement("script");
    script.src = browser.runtime.getURL("page-hook.js");
    script.onload = () => script.remove();
    script.onerror = () => script.remove();
    root.appendChild(script);
    return true;
  }

  if (!inject()) {
    document.addEventListener("DOMContentLoaded", inject, { once: true });
  }
})();
