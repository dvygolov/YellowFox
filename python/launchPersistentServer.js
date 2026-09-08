const playwright = require(process.cwd());

let lastPageErrorLocationBugLogAt = 0;
let suppressedPageErrorLocationBugCount = 0;

function isKnownPageErrorLocationBug(error) {
  const message = String(error && error.message || "");
  const stack = String(error && error.stack || "");
  return message.includes("Cannot read properties of undefined (reading 'url')") &&
    stack.includes("FFBrowserContext") &&
    stack.includes("coreBundle.js:49624");
}

process.on("uncaughtException", (error) => {
  if (isKnownPageErrorLocationBug(error)) {
    suppressedPageErrorLocationBugCount += 1;
    const now = Date.now();
    if (now - lastPageErrorLocationBugLogAt > 30000) {
      console.error(
        `YellowFox suppressed Playwright pageError.location crash. Count=${suppressedPageErrorLocationBugCount}`,
        error.stack || error.message || error
      );
      lastPageErrorLocationBugLogAt = now;
      suppressedPageErrorLocationBugCount = 0;
    }
    return;
  }

  console.error("Unhandled exception in YellowFox persistent server:", error && error.stack || error);
  process.exit(1);
});

function collectData() {
  return new Promise((resolve) => {
    let data = "";
    process.stdin.setEncoding("utf8");
    process.stdin.on("data", (chunk) => {
      data += chunk;
    });
    process.stdin.on("end", () => {
      resolve(JSON.parse(Buffer.from(data, "base64").toString()));
    });
  });
}

collectData().then(async (options) => {
  console.time("Server launched");
  console.info("Launching persistent server...");

  const userDataDir = options.userDataDir;
  if (!userDataDir) {
    throw new Error("userDataDir is required for YellowFox persistent server");
  }

  delete options.userDataDir;
  delete options.persistentContext;

  const browserServer = await playwright.firefox.launchServer({
    ...options,
    ignoreDefaultArgs: Array.isArray(options.ignoreDefaultArgs) ? options.ignoreDefaultArgs : undefined,
    ignoreAllDefaultArgs: !!options.ignoreDefaultArgs && !Array.isArray(options.ignoreDefaultArgs),
    _userDataDir: userDataDir,
    _sharedBrowser: true
  }).catch((error) => {
    error.message = `${error.message} Failed to launch persistent browser.`;
    throw error;
  });

  console.timeEnd("Server launched");
  console.log("Websocket endpoint:\x1b[93m", browserServer.wsEndpoint(), "\x1b[0m");
  process.stdin.resume();
}).catch((error) => {
  console.error("Error launching persistent server:", error.message);
  process.exit(1);
});
