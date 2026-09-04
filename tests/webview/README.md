# WebView UI tests

Unit tests for the chat panel's front end — `src/ClaudeCode.VisualStudio/media/{app,markdown}.js`.

```sh
node tests/webview/run.js
```

They also run as part of the normal test pass: `WebViewUiTests` shells out to this runner and
reports it as one MSTest result, so `dotnet test` / Test Explorer covers the JavaScript too. If node
is not on PATH that test reports **inconclusive** rather than passing quietly.

## Why it is built this way

Release builds set `AreDevToolsEnabled = false`, so the panel has no console inside VS — a JS bug is
otherwise invisible until a user reports it. The repo also carries no npm dependencies, and adding a
`node_modules` to a VSIX repo to get jsdom is a poor trade. So:

- **`dom-stub.js`** — a small DOM (elements, events with bubbling, class lists, an HTML parser, and
  the subset of CSS selectors `app.js` actually uses). It implements what the app touches and
  nothing more; if app.js starts using something new, the boot throws a clear `TypeError`.
- **`harness.js`** — parses the *shipped* `media/index.html`, then runs `markdown.js` and `app.js`
  inside a `vm` context on top of that DOM. Because it loads the real page, a missing element id or
  a stale `?v=` cache-buster fails the suite instead of shipping.
- **`runner.js`** — `describe`/`it` and a summary. Node here is v16, which predates `node --test`.

`boot()` hands a test the document, the captured host messages (`sent()`), a `pushMessage()` that
delivers a host → page message the way `WebViewHost.PostWebMessageAsJson` does, and a fake clipboard.

> WebView2 delivers host messages to `chrome.webview` listeners only. `app.js` also listens on
> `window` as a fallback for a different host; the harness fires **only** the `chrome.webview`
> channel, because firing both would run every handler twice — which is not what happens in VS.

## What is covered

| File | Area | Shipped in |
|---|---|---|
| `copy-button.test.js` | Copy button on fenced code blocks: markup, exact clipboard text, fallbacks, feedback | v1.0.15 |
| `markdown.test.js` | The renderer: blocks, inline, tables, and escaping / URL-scheme filtering of untrusted model output | v1.0.0 |
| `diff.test.js` | Edit / MultiEdit / Write tool cards rendered as real diffs, char-level marks, truncation | v1.0.10–11 |
| `setup-banner.test.js` | CLI missing / signed out / update reminder, and the `periodic` flag that un-snoozes a dismiss | v1.0.9–13 |
| `panel-state.test.js` | Context ring and its window, model button naming, live permission-mode switch, transcript restore | v1.0.5–14 |

## Adding a test

Drop a `*.test.js` in `tests/`; `run.js` picks it up automatically.

```js
const { describe, it } = require("../runner");
const { boot } = require("../harness");

describe("thing", () => {
  it("does what it says", () => {
    const app = boot();
    app.pushMessage("setup", { cliFound: true, loggedIn: true });
    // assert against app.document / app.sent("...")
  });
});
```

Anything asynchronous (the clipboard, a timer) needs `await app.settle()` before asserting.
