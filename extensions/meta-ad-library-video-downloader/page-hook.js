(function () {
  "use strict";

  if (window.__yellowfoxAdLibraryHookInstalled) {
    return;
  }
  window.__yellowfoxAdLibraryHookInstalled = true;

  function emitPayload(text) {
    if (!text || typeof text !== "string" || text.indexOf("collated_results") < 0) {
      return;
    }
    window.postMessage({
      source: "yellowfox-ad-library-video-downloader",
      type: "graphql-payload",
      text
    }, "*");
  }

  const originalFetch = window.fetch;
  if (typeof originalFetch === "function") {
    window.fetch = function (...args) {
      const result = originalFetch.apply(this, args);
      Promise.resolve(result).then((response) => {
        try {
          const url = response && response.url ? String(response.url) : "";
          if (url.indexOf("/api/graphql") < 0) {
            return;
          }
          response.clone().text().then(emitPayload).catch(() => {});
        } catch {}
      }).catch(() => {});
      return result;
    };
  }

  const OriginalXMLHttpRequest = window.XMLHttpRequest;
  if (typeof OriginalXMLHttpRequest === "function") {
    const originalOpen = OriginalXMLHttpRequest.prototype.open;
    const originalSend = OriginalXMLHttpRequest.prototype.send;

    OriginalXMLHttpRequest.prototype.open = function (method, url, ...rest) {
      this.__yellowfoxRequestUrl = url ? String(url) : "";
      return originalOpen.call(this, method, url, ...rest);
    };

    OriginalXMLHttpRequest.prototype.send = function (...args) {
      this.addEventListener("load", function () {
        try {
          if (String(this.__yellowfoxRequestUrl || "").indexOf("/api/graphql") < 0) {
            return;
          }
          emitPayload(typeof this.responseText === "string" ? this.responseText : "");
        } catch {}
      });
      return originalSend.apply(this, args);
    };
  }
})();
