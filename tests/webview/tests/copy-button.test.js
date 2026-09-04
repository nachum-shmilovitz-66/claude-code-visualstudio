"use strict";
// The copy button on fenced code blocks (media/markdown.js emits it, media/app.js handles it).
// The behaviour that matters is that the text landing on the clipboard is byte-identical to what
// the model suggested — a command copied with &quot; in it instead of a real quote is worse than
// no button, because it fails at the shell rather than at the click.

const assert = require("assert");
const { describe, it } = require("../runner");
const { boot } = require("../harness");
const { DomEvent } = require("../dom-stub");

const FENCE = "```powershell\nInvoke-WebRequest https://tls-test.twilio.com -UseBasicParsing\n```";

/** Render markdown into #messages the way a restored transcript does, and hand back the block. */
function renderInto(app, markdown) {
  const host = app.document.createElement("div");
  host.className = "node-main";
  host.innerHTML = app.context.window.md.render(markdown);
  app.$("messages").appendChild(host);
  return host;
}

describe("copy button — markup", () => {
  it("gives every fenced block a bar, a language and a copy button", () => {
    const app = boot();
    const host = renderInto(app, FENCE);

    const block = host.querySelector(".cblock");
    assert.ok(block, "expected a .cblock wrapper");
    assert.strictEqual(block.querySelector(".clang").textContent, "powershell");

    const btn = block.querySelector(".ccopy");
    assert.ok(btn, "expected a .ccopy button");
    assert.strictEqual(btn.tagName, "BUTTON");
    assert.strictEqual(btn.getAttribute("type"), "button", "must not submit anything");
    assert.strictEqual(btn.getAttribute("title"), "Copy code");
    assert.strictEqual(btn.querySelector(".clabel").textContent, "Copy");
    assert.ok(btn.querySelector("svg"), "expected the inline copy icon");
  });

  it("keeps the bar above the code, not inside the scrolling <pre>", () => {
    // A button inside the <pre> scrolls away with a long line; the point of the bar is that it
    // cannot.
    const app = boot();
    const block = renderInto(app, FENCE).querySelector(".cblock");
    const btn = block.querySelector(".ccopy");
    assert.strictEqual(btn.closest("pre"), null, "the button must live outside the <pre>");
    assert.strictEqual(btn.closest(".cbar").parentNode, block);
    assert.strictEqual(block.children[0].className, "cbar");
    assert.strictEqual(block.children[1].localName, "pre");
  });

  it("still renders a usable button for a fence with no language", () => {
    const app = boot();
    const block = renderInto(app, "```\nnpm install\n```").querySelector(".cblock");
    assert.strictEqual(block.querySelector(".clang").textContent, "");
    assert.ok(block.querySelector(".ccopy"), "a fence without a language still needs the button");
  });

  it("does not put a button on inline code", () => {
    const app = boot();
    const host = renderInto(app, "Run `npm install` first.");
    assert.strictEqual(host.querySelector(".ccopy"), null);
  });
});

describe("copy button — clipboard", () => {
  it("copies the code exactly, including quotes, ampersands and angle brackets", async () => {
    const app = boot();
    // These characters are HTML-escaped on the way into the DOM; the copy has to undo that.
    const code = 'Get-TlsCipherSuite | ? Name -match "AES_(128|256)" & echo <ok>';
    const host = renderInto(app, "```powershell\n" + code + "\n```");

    host.querySelector(".ccopy").click();
    await app.settle();

    assert.deepStrictEqual(app.clipboard.writes, [code]);
  });

  it("copies every line of a multi-line block", async () => {
    const app = boot();
    const code = "Invoke-WebRequest https://tls-test.twilio.com -UseBasicParsing\nGet-TlsCipherSuite";
    const host = renderInto(app, "```powershell\n" + code + "\n```");

    host.querySelector(".ccopy").click();
    await app.settle();

    assert.strictEqual(app.clipboard.writes[0], code);
    assert.strictEqual(app.clipboard.writes[0].split("\n").length, 2);
  });

  it("copies the block its own button belongs to", async () => {
    const app = boot();
    const host = renderInto(app, "```sh\nfirst\n```\n\ntext\n\n```sh\nsecond\n```");
    const buttons = host.querySelectorAll(".ccopy");
    assert.strictEqual(buttons.length, 2);

    buttons[1].click();
    await app.settle();
    buttons[0].click();
    await app.settle();

    assert.deepStrictEqual(app.clipboard.writes, ["second", "first"]);
  });

  it("copies from a block that arrived over the streaming host protocol", async () => {
    // The real path: assistantDelta re-renders the whole message on every chunk, which is why the
    // click handler is delegated. A button from the final render must still work.
    const app = boot();
    app.pushMessage("assistantStart", {});
    app.pushMessage("assistantDelta", { text: "Try this:\n\n```bash\nnpm ci" });
    app.pushMessage("assistantDelta", { text: " && npm test\n```\n" });
    app.pushMessage("assistantEnd", {});

    const btn = app.$("messages").querySelector(".ccopy");
    assert.ok(btn, "streamed markdown should render a copy button");
    btn.click();
    await app.settle();

    assert.deepStrictEqual(app.clipboard.writes, ["npm ci && npm test"]);
  });

  it("ignores clicks elsewhere in the transcript", async () => {
    const app = boot();
    const host = renderInto(app, FENCE);

    host.querySelector("pre").click();
    host.querySelector(".clang").click();
    await app.settle();

    assert.deepStrictEqual(app.clipboard.writes, []);
  });

  it("copies without sending anything to the host", async () => {
    // Copying is a pure webview affair; it must not wake the CLI or the extension.
    const app = boot();
    const host = renderInto(app, FENCE);
    const before = app.posted.length;

    host.querySelector(".ccopy").click();
    await app.settle();

    assert.strictEqual(app.posted.length, before, "copying should post no host message");
  });

  it("swallows the click so it cannot reach other handlers", async () => {
    const app = boot();
    const host = renderInto(app, FENCE);
    let sawClick = false;
    app.$("messages").parentNode.addEventListener("click", () => { sawClick = true; });

    host.querySelector(".ccopy").click();
    await app.settle();

    assert.strictEqual(sawClick, false, "the copy click must not bubble past #messages");
  });
});

describe("copy button — feedback", () => {
  it("reports success on the button", async () => {
    const app = boot();
    const btn = renderInto(app, FENCE).querySelector(".ccopy");

    btn.click();
    await app.settle();

    assert.strictEqual(btn.querySelector(".clabel").textContent, "Copied");
    assert.ok(btn.classList.contains("copied"));
    assert.ok(!btn.classList.contains("failed"));
  });

  it("returns to its resting state after the flash", async () => {
    const app = boot();
    const btn = renderInto(app, FENCE).querySelector(".ccopy");

    btn.click();
    await app.settle();
    assert.strictEqual(btn.querySelector(".clabel").textContent, "Copied");

    await new Promise((r) => setTimeout(r, 1500));

    assert.strictEqual(btn.querySelector(".clabel").textContent, "Copy");
    assert.ok(!btn.classList.contains("copied"));
  });

  it("says so when the copy genuinely failed", async () => {
    // Both paths dead: no async clipboard, and execCommand reports failure.
    const app = boot({ clipboard: "absent", execCommandResult: false });
    const btn = renderInto(app, FENCE).querySelector(".ccopy");

    btn.click();
    await app.settle();

    assert.strictEqual(btn.querySelector(".clabel").textContent, "Failed");
    assert.ok(btn.classList.contains("failed"));
    assert.ok(!btn.classList.contains("copied"));
  });
});

describe("copy button — clipboard fallback", () => {
  it("falls back to execCommand when the async clipboard rejects", async () => {
    const app = boot({ clipboard: "reject" });
    const code = "Invoke-WebRequest https://tls-test.twilio.com -UseBasicParsing";
    const btn = renderInto(app, "```powershell\n" + code + "\n```").querySelector(".ccopy");

    btn.click();
    await app.settle();

    assert.deepStrictEqual(app.document.execCommands, [{ name: "copy", text: code }]);
    assert.strictEqual(btn.querySelector(".clabel").textContent, "Copied");
  });

  it("falls back when navigator.clipboard is missing entirely", async () => {
    const app = boot({ clipboard: "absent" });
    const btn = renderInto(app, FENCE).querySelector(".ccopy");

    btn.click();
    await app.settle();

    assert.strictEqual(app.document.execCommands.length, 1);
    assert.strictEqual(btn.querySelector(".clabel").textContent, "Copied");
  });

  it("cleans up the scratch textarea and restores focus", async () => {
    const app = boot({ clipboard: "absent" });
    const input = app.$("input");
    input.focus();
    const btn = renderInto(app, FENCE).querySelector(".ccopy");

    btn.click();
    await app.settle();

    const textareas = app.document.body.querySelectorAll("textarea");
    assert.deepStrictEqual(textareas.map((t) => t.getAttribute("id")), ["input"],
      "the scratch textarea must be removed again");
    assert.strictEqual(app.document.activeElement, input, "focus should go back to the composer");
  });
});

describe("copy button — keyboard", () => {
  it("is a real button, so Enter and Space reach it", async () => {
    // No custom key handling: the fix is that .ccopy is a <button>, which the browser activates on
    // Enter/Space. This asserts the click path the browser would synthesise.
    const app = boot();
    const btn = renderInto(app, FENCE).querySelector(".ccopy");

    btn.dispatchEvent(new DomEvent("click", { bubbles: true }));
    await app.settle();

    assert.strictEqual(app.clipboard.writes.length, 1);
  });
});
