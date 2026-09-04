"use strict";
// Panel state that the last two months of releases churned: the context ring and its usage numbers
// (v1.0.5 "correct context usage", v1.0.14 "the ring goes green"), the model button naming the
// model that actually answered (v1.0.14), the live permission-mode switch (v1.0.5), and transcript
// restore (v1.0.6 / v1.0.7). All of it shipped on visual inspection alone.

const assert = require("assert");
const { describe, it } = require("../runner");
const { boot } = require("../harness");

const MODELS = [
  { id: "default", name: "Default", label: "Default (recommended)" },
  { id: "opus", name: "Opus 5", label: "Opus 5" },
  { id: "sonnet", name: "Sonnet 5", label: "Sonnet 5" },
];

function booted() {
  const app = boot();
  app.pushMessage("init", { models: MODELS, model: "default", modes: [], efforts: [] });
  return app;
}

describe("context ring", () => {
  it("starts empty and claims all the context is available", () => {
    const app = booted();
    assert.ok(app.$("ringBtn").title.startsWith("100%"), app.$("ringBtn").title);
  });

  it("fills as context is used, against the selected model's window", () => {
    // The default model is opus[1m], so 50k is 5% of a 1M window — not 25% of 200k.
    const app = booted();
    app.pushMessage("contextUsage", { totalTokens: 50000 });

    const title = app.$("ringBtn").title;
    assert.ok(title.startsWith("95%"), title);
    // The arc is drawn with a dash offset: less remaining means less offset.
    const offset = parseFloat(app.$("ringFg").style.strokeDashoffset);
    assert.ok(offset > 0 && offset < 94.2, "offset " + offset);
  });

  it("measures against a 200k window for a model that has one", () => {
    const app = booted();
    app.pushMessage("system", { subtype: "init", model: "claude-sonnet-5" });
    app.pushMessage("contextUsage", { totalTokens: 50000 });

    assert.ok(app.$("ringBtn").title.startsWith("75%"), app.$("ringBtn").title);
  });

  it("never reports past full, however large the reported usage", () => {
    const app = booted();
    app.pushMessage("contextUsage", { totalTokens: 999999999 });

    assert.ok(app.$("ringBtn").title.startsWith("0%"), app.$("ringBtn").title);
    assert.strictEqual(parseFloat(app.$("ringFg").style.strokeDashoffset), 0);
  });

  it("takes the system prefix only from a cold request", () => {
    // On a resumed session the first request reads the whole conversation back from cache; counting
    // that as the system prefix would swallow every message into the wrong bucket.
    const app = booted();
    app.pushMessage("contextUsage", { totalTokens: 90000, cacheReadTokens: 80000, cacheCreationTokens: 80000 });
    app.pushMessage("contextUsage", { totalTokens: 95000, cacheReadTokens: 0, cacheCreationTokens: 12000 });

    // Nothing user-visible names the buckets outside the popover, so assert via the popover.
    app.$("contextBtn").click();
    const text = app.$("popover").textContent;
    assert.ok(/12,?000|12\.0k|12k/i.test(text), "expected the cold-write prefix in the breakdown: " + text);
  });

  it("asks the host to compact when the ring is clicked", () => {
    const app = booted();
    app.$("ringBtn").click();
    assert.strictEqual(app.sent("compact").length, 1);
  });
});

describe("model button", () => {
  it("names the selected model before a turn has run", () => {
    const app = booted();
    app.pushMessage("init", { models: MODELS, model: "sonnet" });
    assert.ok(app.$("modelBtn").textContent.includes("Sonnet"), app.$("modelBtn").textContent);
  });

  it("names the model that actually answered once the session reports one", () => {
    // The picker can offer an alias the CLI resolves differently; after a turn the button should
    // show what ran, not what was asked for.
    const app = booted();
    app.pushMessage("system", { subtype: "init", model: "claude-opus-5" });

    const label = app.$("modelBtn").textContent;
    assert.ok(/opus/i.test(label), label);
    assert.ok(app.$("modelBtn").title.includes("claude-opus-5"), app.$("modelBtn").title);
  });

  it("goes back to the chosen model when a conversation is restored", () => {
    const app = booted();
    app.pushMessage("system", { subtype: "init", model: "claude-opus-5" });
    app.pushMessage("restore", { model: "sonnet", messages: [] });

    assert.ok(/sonnet/i.test(app.$("modelBtn").textContent), app.$("modelBtn").textContent);
  });
});

describe("permission mode", () => {
  it("labels the current mode and switches live", () => {
    const app = boot();
    app.pushMessage("init", {
      models: MODELS, model: "default",
      modes: [
        { id: "default", name: "Ask before edits" },
        { id: "acceptEdits", name: "Edit automatically" },
        { id: "bypassPermissions", name: "Bypass permissions" },
      ],
    });

    app.$("modeBtn").click();
    const option = app.$("cpop").querySelectorAll(".opt")
      .find((o) => o.getAttribute("data-id") === "acceptEdits");
    assert.ok(option, "expected the mode list to offer acceptEdits");
    option.click();

    const sent = app.sent("setPermissionMode");
    assert.strictEqual(sent.length, 1, "switching mode should reach the host without a restart");
    assert.strictEqual(sent[0].payload.mode, "acceptEdits");
    assert.strictEqual(app.$("modeLabel").textContent, "Auto-edit",
      "the pill shortens the mode name to fit the composer bar");
  });
});

describe("transcript restore", () => {
  it("replays both sides of a restored conversation", () => {
    const app = booted();
    app.pushMessage("restore", {
      messages: [
        { role: "user", text: "how do I test this?" },
        { role: "assistant", text: "Run:\n\n```sh\nnpm test\n```" },
      ],
    });

    const messages = app.$("messages");
    assert.ok(messages.textContent.includes("how do I test this?"));
    assert.ok(messages.textContent.includes("Restored previous conversation"),
      "the divider tells the user this is history, not a live turn");
    // Restored assistant markdown is real markdown, copy button and all.
    assert.ok(messages.querySelector(".ccopy"), "a restored code block still needs its copy button");
  });

  it("clears whatever was on screen first", () => {
    const app = booted();
    app.pushMessage("assistantStart", {});
    app.pushMessage("assistantDelta", { text: "stale turn" });
    app.pushMessage("restore", { messages: [{ role: "user", text: "fresh" }] });

    const text = app.$("messages").textContent;
    assert.ok(!text.includes("stale turn"), text);
    assert.ok(text.includes("fresh"));
  });

  it("shows nothing but the transcript when there is no history", () => {
    const app = booted();
    app.pushMessage("restore", { messages: [] });

    assert.strictEqual(app.$("messages").textContent.trim(), "");
  });
});

describe("turn status", () => {
  it("swaps Send for Stop while a turn runs", () => {
    const app = booted();
    app.pushMessage("status", { state: "running", running: true });
    assert.ok(app.$("sendBtn").classList.contains("hidden"));
    assert.ok(!app.$("stopBtn").classList.contains("hidden"));

    app.pushMessage("status", { state: "ready", running: false });
    assert.ok(!app.$("sendBtn").classList.contains("hidden"));
    assert.ok(app.$("stopBtn").classList.contains("hidden"));
  });

  it("reports cost and tokens when a turn finishes", () => {
    const app = booted();
    app.pushMessage("result", { costUsd: 0.0123, inputTokens: 100, outputTokens: 250, durationMs: 3400 });

    const usage = app.$("usage").textContent;
    assert.ok(usage.includes("$0.0123"), usage);
    assert.ok(usage.includes("100 in") && usage.includes("250 out"), usage);
    assert.ok(usage.includes("3.4s"), usage);
  });
});
