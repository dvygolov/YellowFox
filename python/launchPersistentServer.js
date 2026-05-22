const { createPlaywright } = require(`${process.cwd()}/lib/server/playwright.js`);
const { PlaywrightServer } = require(`${process.cwd()}/lib/remote/playwrightServer.js`);
const { serverSideCallMetadata } = require(`${process.cwd()}/lib/server/instrumentation.js`);
const { helper } = require(`${process.cwd()}/lib/server/helper.js`);
const { createGuid } = require(`${process.cwd()}/lib/server/utils/crypto.js`);
const { DEFAULT_PLAYWRIGHT_LAUNCH_TIMEOUT } = require(`${process.cwd()}/lib/utils/isomorphic/time.js`);
const { rewriteErrorMessage } = require(`${process.cwd()}/lib/utils/isomorphic/stackTrace.js`);
const { EventEmitter } = require(`${process.cwd()}/lib/utilsBundle.js`).ws;

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

function envObjectToArray(env) {
  const result = [];
  for (const name in env) {
    if (!Object.is(env[name], undefined)) {
      result.push({ name, value: String(env[name]) });
    }
  }
  return result;
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

  const playwright = createPlaywright({ sdkLanguage: "javascript", isServer: true });
  const metadata = serverSideCallMetadata();
  const context = await playwright.firefox.launchPersistentContext(metadata, userDataDir, {
    ...options,
    ignoreDefaultArgs: Array.isArray(options.ignoreDefaultArgs) ? options.ignoreDefaultArgs : undefined,
    ignoreAllDefaultArgs: !!options.ignoreDefaultArgs && !Array.isArray(options.ignoreDefaultArgs),
    env: options.env ? envObjectToArray(options.env) : undefined,
    timeout: options.timeout ?? DEFAULT_PLAYWRIGHT_LAUNCH_TIMEOUT
  }).catch((error) => {
    const log = helper.formatBrowserLogs(metadata.log);
    rewriteErrorMessage(error, `${error.message} Failed to launch persistent browser.${log}`);
    throw error;
  });

  const browser = context._browser;
  const path = options.wsPath ? (options.wsPath.startsWith("/") ? options.wsPath : `/${options.wsPath}`) : `/${createGuid()}`;
  const server = new PlaywrightServer({
    mode: "launchServerShared",
    path,
    maxConnections: Infinity,
    preLaunchedBrowser: browser,
    preLaunchedSocksProxy: undefined
  });
  const wsEndpoint = await server.listen(options.port, options.host);
  const browserServer = new EventEmitter();
  browserServer.process = () => browser.options.browserProcess.process;
  browserServer.wsEndpoint = () => wsEndpoint;
  browserServer.close = () => browser.options.browserProcess.close();
  browserServer.kill = () => browser.options.browserProcess.kill();
  browser.options.browserProcess.onclose = (exitCode, signal) => {
    server.close();
    browserServer.emit("close", exitCode, signal);
  };

  console.timeEnd("Server launched");
  console.log("Websocket endpoint:\x1b[93m", browserServer.wsEndpoint(), "\x1b[0m");
  process.stdin.resume();
}).catch((error) => {
  console.error("Error launching persistent server:", error.message);
  process.exit(1);
});
