"use strict";
// Boots the real media/index.html + markdown.js + app.js inside a vm sandbox on top of dom-stub,
// and hands the test back the levers it needs: the host message channel, the clipboard, and the
// document itself. Loading the shipped index.html rather than a hand-written fixture means a
// missing element id or a stale ?v= cache-buster fails the suite instead of shipping.

const fs = require("fs");
const path = require("path");
const vm = require("vm");
const { createDom, DomEvent } = require("./dom-stub");

const MEDIA = path.join(__dirname, "..", "..", "src", "ClaudeCode.VisualStudio", "media");

function readMedia(name) { return fs.readFileSync(path.join(MEDIA, name), "utf8"); }

/**
 * @param {object} opts
 *   clipboard: "ok" (default) | "reject" (writeText rejects) | "absent" (no navigator.clipboard)
 *   execCommandResult: what document.execCommand("copy") returns in the fallback path
 */
function boot(opts) {
  opts = opts || {};
  const { document, window } = createDom(readMedia("index.html"));

  // Host channel. WebView2 delivers PostWebMessageAsJson to listeners on `chrome.webview` and
  // nowhere else, so the harness keeps its own registry: app.js also listens on `window` as a
  // belt-and-braces fallback for a host that delivers there instead, and firing both would run
  // every handler twice — which is not what happens inside VS.
  const posted = [];
  const hostListeners = [];
  window.chrome = {
    webview: {
      postMessage: (m) => posted.push(typeof m === "string" ? JSON.parse(m) : JSON.parse(JSON.stringify(m))),
      addEventListener: (type, fn) => { if (type === "message") hostListeners.push(fn); },
      removeEventListener: (type, fn) => {
        const i = hostListeners.indexOf(fn);
        if (type === "message" && i !== -1) hostListeners.splice(i, 1);
      },
    },
  };

  const clipboard = { writes: [], mode: opts.clipboard || "ok" };
  if (clipboard.mode !== "absent") {
    window.navigator.clipboard = {
      writeText: (text) => {
        if (clipboard.mode === "reject") return Promise.reject(new Error("blocked"));
        clipboard.writes.push(String(text));
        return Promise.resolve();
      },
    };
  }
  if (opts.execCommandResult !== undefined) document.execCommandResult = opts.execCommandResult;

  const errors = [];
  const sandbox = {
    window, document,
    navigator: window.navigator,
    location: window.location,
    setTimeout, clearTimeout, setInterval, clearInterval,
    requestAnimationFrame: window.requestAnimationFrame,
    Promise, Math, Date, JSON, Object, Array, String, Number, Boolean, RegExp, Error, Map, Set,
    isNaN, parseInt, parseFloat, encodeURIComponent, decodeURIComponent,
    console: { log: () => {}, warn: () => {}, error: (...a) => errors.push(a.join(" ")) },
  };
  sandbox.globalThis = sandbox;
  const context = vm.createContext(sandbox);

  vm.runInContext(readMedia("markdown.js"), context, { filename: "markdown.js" });
  vm.runInContext(readMedia("app.js"), context, { filename: "app.js" });

  const $ = (id) => document.getElementById(id);

  return {
    document, window, context, posted, clipboard, errors, $,
    /** Deliver a host -> webview message the way WebViewHost.PostWebMessageAsJson does. */
    pushMessage(type, payload) {
      const ev = new DomEvent("message", {
        bubbles: false,
        props: { data: { type, payload: payload || {} } },
      });
      hostListeners.slice().forEach((fn) => fn(ev));
    },
    /** Messages app.js posted back to the host, optionally filtered by type. */
    sent(type) { return type ? posted.filter((m) => m.type === type) : posted; },
    /** Let queued promise callbacks and 0ms timers run. */
    settle() { return new Promise((resolve) => setTimeout(resolve, 0)); },
  };
}

module.exports = { boot, readMedia, MEDIA, DomEvent };
