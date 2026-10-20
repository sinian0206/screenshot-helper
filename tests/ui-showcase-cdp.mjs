import { mkdir, writeFile } from "node:fs/promises";
import { spawn } from "node:child_process";
import path from "node:path";
import { pathToFileURL } from "node:url";

const [edgePath, htmlPath, outputDir] = process.argv.slice(2);
if (!edgePath || !htmlPath || !outputDir) {
  console.error("usage: node ui-showcase-cdp.mjs <edge> <html> <output>");
  process.exit(2);
}

await mkdir(outputDir, { recursive: true });
const port = 9300 + (process.pid % 400);
const profile = path.join(outputDir, "edge-cdp-profile");
const edge = spawn(edgePath, [
  "--headless=new",
  "--disable-gpu",
  "--disable-features=msEdgeFirstRunExperience",
  "--no-first-run",
  "--hide-scrollbars",
  `--remote-debugging-port=${port}`,
  `--user-data-dir=${profile}`,
  "about:blank"
], { windowsHide: true, stdio: "ignore" });

const delay = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
async function waitForDebugger() {
  for (let attempt = 0; attempt < 100; attempt++) {
    try {
      const response = await fetch(`http://127.0.0.1:${port}/json/version`);
      if (response.ok) return;
    } catch {}
    await delay(100);
  }
  throw new Error("Edge remote debugger did not start");
}

let socket;
let nextId = 1;
const pending = new Map();
function command(method, params = {}) {
  const id = nextId++;
  socket.send(JSON.stringify({ id, method, params }));
  return new Promise((resolve, reject) => pending.set(id, { resolve, reject }));
}

try {
  await waitForDebugger();
  const targetResponse = await fetch(
    `http://127.0.0.1:${port}/json/new?${encodeURIComponent("about:blank")}`,
    { method: "PUT" }
  );
  if (!targetResponse.ok) throw new Error(`create target failed: ${targetResponse.status}`);
  const target = await targetResponse.json();

  socket = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => {
    socket.addEventListener("open", resolve, { once: true });
    socket.addEventListener("error", reject, { once: true });
  });
  socket.addEventListener("message", (event) => {
    const message = JSON.parse(event.data);
    if (!message.id || !pending.has(message.id)) return;
    const request = pending.get(message.id);
    pending.delete(message.id);
    if (message.error) request.reject(new Error(message.error.message));
    else request.resolve(message.result);
  });

  await command("Page.enable");
  await command("Runtime.enable");
  const baseUrl = pathToFileURL(path.resolve(htmlPath)).href;

  async function setViewport(width, height, mobile) {
    await command("Emulation.setDeviceMetricsOverride", {
      width,
      height,
      deviceScaleFactor: 1,
      mobile,
      screenWidth: width,
      screenHeight: height
    });
  }

  async function navigate(url) {
    await command("Page.navigate", { url });
    await delay(600);
  }

  async function evaluate(expression) {
    const result = await command("Runtime.evaluate", {
      expression,
      returnByValue: true,
      awaitPromise: true
    });
    return result.result?.value;
  }

  async function screenshot(file) {
    const result = await command("Page.captureScreenshot", {
      format: "png",
      fromSurface: true,
      captureBeyondViewport: false
    });
    await writeFile(file, Buffer.from(result.data, "base64"));
  }

  await setViewport(390, 844, true);
  await navigate(`${baseUrl}?selftest=1`);
  const mobileState = await evaluate(`JSON.stringify({
    passed: document.body.dataset.selftest,
    output: document.getElementById("selftestOutput").textContent,
    innerWidth,
    scrollWidth: document.documentElement.scrollWidth,
    innerHeight,
    scrollHeight: document.documentElement.scrollHeight
  })`);
  const mobile = JSON.parse(mobileState);
  await writeFile(
    path.join(outputDir, "selftest-dom-exact-mobile.html"),
    await evaluate("document.documentElement.outerHTML"),
    "utf8"
  );
  if (mobile.passed !== "passed") throw new Error(mobile.output || "mobile self-test failed");
  if (!mobile.output.includes("SUMMARY 41 PASS / 0 FAIL")) {
    throw new Error(`unexpected interaction summary: ${mobile.output}`);
  }
  if (mobile.scrollWidth > 390) {
    throw new Error(`mobile overflow: 390 -> ${mobile.scrollWidth}`);
  }

  await navigate(baseUrl);
  await screenshot(path.join(outputDir, "showcase-mobile.png"));

  await setViewport(1440, 1000, false);
  await navigate(`${baseUrl}?selftest=1`);
  const desktopState = await evaluate(`JSON.stringify({
    passed: document.body.dataset.selftest,
    output: document.getElementById("selftestOutput").textContent,
    innerWidth,
    scrollWidth: document.documentElement.scrollWidth,
    innerHeight,
    scrollHeight: document.documentElement.scrollHeight
  })`);
  const desktop = JSON.parse(desktopState);
  await writeFile(
    path.join(outputDir, "selftest-dom-exact-desktop.html"),
    await evaluate("document.documentElement.outerHTML"),
    "utf8"
  );
  if (desktop.passed !== "passed") throw new Error(desktop.output || "desktop self-test failed");
  if (desktop.scrollWidth > 1440) {
    throw new Error(`desktop overflow: 1440 -> ${desktop.scrollWidth}`);
  }

  await navigate(baseUrl);
  await screenshot(path.join(outputDir, "showcase-desktop.png"));
  await writeFile(
    path.join(outputDir, "cdp-report.json"),
    JSON.stringify({ mobile, desktop, interactions: 41, failures: 0 }, null, 2),
    "utf8"
  );
  await command("Browser.close");
} finally {
  try { socket?.close(); } catch {}
  try { edge.kill(); } catch {}
}
