"use strict";
// Remote Control: the host bridges the CLI session to claude.ai/code and the Claude apps and
// relays the session link; the page offers the toggle, shows the link and keeps it visible.

const assert = require("assert");
const { describe, it } = require("../runner");
const { boot } = require("../harness");

const URL1 = "https://claude.ai/code/session_01AXxzQLenDiPAXUiRhTEjph";
const MODES = [{ id: "default", name: "Ask before edits" }, { id: "bypassPermissions", name: "Auto mode" }];

function booted() {
  const app = boot();
  app.pushMessage("init", { models: [{ id: "default", name: "Default" }], model: "default", modes: MODES, permissionMode: "bypassPermissions", efforts: [] });
  return app;
}
function openMode(app) { app.$("modeBtn").click(); return app.$("cpop"); }

describe("remote control", () => {
  it("offers no toggle until the host says the account can use it", () => {
    const app = booted();
    assert.strictEqual(openMode(app).querySelector("#rcToggle"), null);
    app.$("modeBtn").click();
    app.pushMessage("remoteControl", { available: false, on: false });
    assert.strictEqual(openMode(app).querySelector("#rcToggle"), null);
  });

  it("turns it on from the mode popover and says it is connecting", () => {
    const app = booted();
    app.pushMessage("remoteControl", { available: true, on: false });
    openMode(app).querySelector("#rcToggle").click();
    const sent = app.sent("setRemoteControl");
    assert.strictEqual(sent.length, 1);
    assert.strictEqual(sent[0].payload.on, true);
    assert.ok(app.$("cpop").textContent.includes("Connecting"), app.$("cpop").textContent);
    assert.ok(app.$("modeBtn").classList.contains("rc-on"));
  });

  it("shows the session link in the chat once, and in the popover with Copy", async () => {
    const app = booted();
    app.pushMessage("remoteControl", { available: true, on: true, url: URL1 });
    app.pushMessage("remoteControl", { available: true, on: true, url: URL1 });
    const dividers = app.$("messages").querySelectorAll(".rc-divider");
    assert.strictEqual(dividers.length, 1, "the same link is announced once");
    assert.ok(dividers[0].textContent.includes("claude.ai/code/session_01AX"), dividers[0].textContent);
    dividers[0].querySelector(".ulink").click();
    assert.strictEqual(app.sent("openExternal").pop().payload.url, URL1);

    const pop = openMode(app);
    assert.strictEqual(pop.querySelector("#rcToggle").textContent, "On");
    pop.querySelector("#rcCopy").click();
    await app.settle();
    assert.deepStrictEqual(app.clipboard.writes, [URL1]);
  });

  it("announces a new link after a relaunch", () => {
    const app = booted();
    app.pushMessage("remoteControl", { available: true, on: true, url: URL1 });
    app.pushMessage("remoteControl", { available: true, on: true, url: null });
    app.pushMessage("remoteControl", { available: true, on: true, url: "https://claude.ai/code/session_02" });
    assert.strictEqual(app.$("messages").querySelectorAll(".rc-divider").length, 2);
  });

  it("never links anything but claude.ai", () => {
    const app = booted();
    app.pushMessage("remoteControl", { available: true, on: true, url: "https://evil.example/x" });
    assert.strictEqual(app.$("messages").querySelectorAll(".rc-divider").length, 0);
    assert.strictEqual(openMode(app).querySelector("#rcOpen"), null);
  });

  it("reports a refusal and shows it off", () => {
    const app = booted();
    app.pushMessage("remoteControl", { available: true, on: false, error: "not signed in" });
    assert.ok(app.$("messages").textContent.includes("Remote Control: not signed in"), app.$("messages").textContent);
    assert.ok(!app.$("modeBtn").classList.contains("rc-on"));
  });

  it("turns off from the popover", () => {
    const app = booted();
    app.pushMessage("remoteControl", { available: true, on: true, url: URL1 });
    openMode(app).querySelector("#rcToggle").click();
    assert.strictEqual(app.sent("setRemoteControl").pop().payload.on, false);
    assert.ok(!app.$("modeBtn").classList.contains("rc-on"));
    assert.strictEqual(app.$("cpop").querySelector("#rcOpen"), null);
  });
});
