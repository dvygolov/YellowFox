(function () {
  "use strict";

  // Runs in the page's own JS world (injected through a web_accessible_resources
  // script tag), so it can replace navigator.credentials.create for real.
  if (window.__yellowfoxPasskeyShim) {
    return;
  }
  window.__yellowfoxPasskeyShim = true;

  try {
    const container = navigator.credentials;
    if (!container || typeof container.create !== "function") {
      return;
    }

    const original = container.create;
    const patched = function create(options) {
      try {
        const selection = options && options.publicKey && options.publicKey.authenticatorSelection;
        if (selection && selection.authenticatorAttachment === "platform") {
          const nextSelection = Object.assign({}, selection);
          delete nextSelection.authenticatorAttachment;
          const nextPublicKey = Object.assign({}, options.publicKey, { authenticatorSelection: nextSelection });
          options = Object.assign({}, options, { publicKey: nextPublicKey });
        }
      } catch (e) {}
      return original.call(container, options);
    };

    patched.__yellowfoxPasskey = true;
    container.create = patched;
  } catch (e) {}
})();
