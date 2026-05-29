const playwright = require(process.cwd());

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
