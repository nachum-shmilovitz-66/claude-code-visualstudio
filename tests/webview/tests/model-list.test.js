"use strict";
// The model picker follows the CLI's own model list: the host relays the rows the CLI reports on
// its initialize response, so a new model release renames or adds a row without an extension
// update. Until that lands the page shows the host's version-free fallback rows.

const assert = require("assert");
const { describe, it } = require("../runner");
const { boot } = require("../harness");

// What the host sends on init: fallback rows, aliases only, no version numbers.
const FALLBACK = [
  { id: "default", name: "Default (recommended)", label: "Opus with 1M context", desc: "Best for everyday, complex tasks", wire: "opus[1m]", ratio: 5, autoMode: true },
  { id: "opus[1m]", name: "Opus (1M context)", label: "Opus with 1M context", desc: "Best for everyday, complex tasks", wire: "opus[1m]", ratio: 5, autoMode: true },
  { id: "fable", name: "Fable", label: "Fable", desc: "Most capable for your hardest and longest-running tasks", wire: "fable", ratio: 10, autoMode: true },
  { id: "sonnet", name: "Sonnet", label: "Sonnet", desc: "Efficient for routine tasks", wire: "sonnet", ratio: 2, autoMode: true },
  { id: "haiku", name: "Haiku", label: "Haiku", desc: "Fastest for quick answers", wire: "haiku", ratio: 1, autoMode: false },
];
// What CLI 2.1.263 reports, as the host relays it (captured 2026-09-07).
const CLI = [
  { id: "default", name: "Default (recommended)", label: "Opus 5 with 1M context", desc: "Best for everyday, complex tasks", wire: "claude-opus-5[1m]", ratio: 5, autoMode: true },
  { id: "opus[1m]", name: "Opus (1M context)", label: "Opus 5 with 1M context", desc: "Best for everyday, complex tasks", wire: "claude-opus-5[1m]", ratio: 5, autoMode: true },
  { id: "claude-fable-5-1[1m]", name: "Fable", label: "Fable 5.1", desc: "Most capable for your hardest and longest-running tasks", wire: "claude-fable-5-1", ratio: 10, autoMode: true },
  { id: "sonnet", name: "Sonnet", label: "Sonnet 5", desc: "Efficient for routine tasks", wire: "claude-sonnet-5", ratio: 2, autoMode: true },
  { id: "haiku", name: "Haiku", label: "Haiku 4.5", desc: "Fastest for quick answers", wire: "claude-haiku-4-5-20251001", ratio: 1, autoMode: false },
];
const steps = (ids) => ids.map((id) => ({ id, name: id }));
const FULL = ["none", "low", "medium", "high", "extrahigh", "max", "ultracode"];
const EFFORTS = {
  default: steps(FULL),
  "opus[1m]": steps(FULL),
  "claude-fable-5-1[1m]": steps(FULL),
  sonnet: steps(["none", "low", "medium", "high", "extrahigh", "max"]),
  haiku: steps(["none", "low", "medium", "high"]),
};
const MODES = [
  { id: "default", name: "Ask before edits" },
  { id: "acceptEdits", name: "Edit automatically" },
  { id: "bypassPermissions", name: "Auto mode" },
];

function booted(model, extra) {
  const app = boot();
  app.pushMessage("init", Object.assign(
    { models: FALLBACK, model: model || "default", modes: MODES, efforts: EFFORTS.default, effortsByModel: EFFORTS },
    extra || {}));
  return app;
}
function cliList(app) { app.pushMessage("models", { models: CLI, effortsByModel: EFFORTS, source: "cli" }); }
function pickerRows(app) {
  app.$("modelBtn").click();
  return app.$("popover").querySelectorAll(".opt").map((o) => ({ id: o.getAttribute("data-id"), text: o.textContent }));
}
// Open the picker, then the Custom model screen, and read its quick-picks.
function quickPicks(app) {
  app.$("modelBtn").click();
  app.$("popover").querySelectorAll(".opt").find((o) => o.getAttribute("data-id") === "__custom").click();
  return app.$("popover").querySelectorAll("#customSuggest .opt")
    .map((o) => ({ id: o.getAttribute("data-id"), text: o.textContent }));
}

describe("model list from the CLI", () => {
  it("shows version-free fallback rows until the CLI list lands", () => {
    const rows = pickerRows(booted());
    const fable = rows.find((r) => r.id === "fable");
    assert.ok(fable && fable.text.includes("Fable · Most capable"), fable && fable.text);
    assert.ok(!rows.some((r) => /Fable 5/.test(r.text)), "no version number is baked in");
  });

  it("renames rows to what the CLI reports", () => {
    const app = booted();
    cliList(app);
    const rows = pickerRows(app);
    const fable = rows.find((r) => r.id === "claude-fable-5-1[1m]");
    assert.ok(fable && fable.text.includes("Fable 5.1 · Most capable"), fable && fable.text);
    assert.ok(!rows.some((r) => r.id === "fable"), "the fallback alias row is gone");
    assert.ok(rows.some((r) => r.text.includes("Haiku 4.5 · Fastest")), "every row is renamed");
  });

  it("re-renders an open picker when the list arrives", () => {
    const app = booted();
    app.$("modelBtn").click();
    assert.ok(!app.$("popover").textContent.includes("Fable 5.1"));
    cliList(app);
    assert.ok(app.$("popover").textContent.includes("Fable 5.1"), app.$("popover").textContent);
  });

  it("moves a stored fallback alias onto the CLI's row for that family", () => {
    const app = booted("fable");
    assert.strictEqual(app.sent("setModel").length, 0, "the fallback list has a fable row");
    cliList(app);
    const sent = app.sent("setModel");
    assert.strictEqual(sent.length, 1, "the new id is persisted");
    assert.strictEqual(sent[0].payload.model, "claude-fable-5-1[1m]");
    assert.ok(app.$("modelBtn").textContent.includes("Fable 5.1"), app.$("modelBtn").textContent);
    const sel = pickerRows(app).find((r) => r.text.includes("✓"));
    assert.strictEqual(sel && sel.id, "claude-fable-5-1[1m]");
  });

  it("matches a typed wire id to the alias row that covers it", () => {
    const app = booted("claude-sonnet-5");
    cliList(app);
    const sent = app.sent("setModel");
    assert.strictEqual(sent.length, 1);
    assert.strictEqual(sent[0].payload.model, "sonnet");
  });

  it("leaves a pinned id and an unlisted alias alone", () => {
    for (const id of ["claude-opus-4-8[1m]", "opus"]) {
      const app = booted(id);
      cliList(app);
      assert.strictEqual(app.sent("setModel").length, 0, id + " must stay as chosen");
      assert.ok(app.$("modelBtn").title.includes(id), app.$("modelBtn").title);
    }
  });

  it("names the CLI's canonical id in the tooltip and the switch divider", () => {
    const app = booted();
    cliList(app);
    assert.ok(app.$("modelBtn").title.includes("claude-opus-5[1m]"), app.$("modelBtn").title);
    app.$("modelBtn").click();
    app.$("popover").querySelectorAll(".opt").find((o) => o.getAttribute("data-id") === "sonnet").click();
    assert.strictEqual(app.sent("setModel")[0].payload.model, "sonnet");
    assert.ok(app.$("messages").textContent.includes("Switched to claude-sonnet-5"), app.$("messages").textContent);
  });

  it("takes the effort range from the CLI and clamps a level it no longer offers", () => {
    // Pretend an older host offered Ultracode on Sonnet; the CLI list does not.
    const app = booted("sonnet", { effort: "ultracode", effortsByModel: Object.assign({}, EFFORTS, { sonnet: steps(FULL) }) });
    assert.strictEqual(app.sent("setEffort").length, 0);
    cliList(app);
    const sent = app.sent("setEffort");
    assert.strictEqual(sent.length, 1, "the stale level is replaced once");
    assert.notStrictEqual(sent[0].payload.effort, "ultracode");
    app.$("modelBtn").click();
    assert.strictEqual(app.$("popover").querySelector("#effslider").getAttribute("max"), "5", "Off..Max for Sonnet");
  });

  it("hides Auto mode for a model the CLI says cannot run it", () => {
    const app = booted("haiku");
    cliList(app);
    app.$("modeBtn").click();
    const ids = app.$("cpop").querySelectorAll(".opt").map((o) => o.getAttribute("data-id"));
    assert.ok(ids.includes("acceptEdits") && !ids.includes("bypassPermissions"), ids.join(","));
  });

  it("offers the Fable ids the main picker does not, and names them", () => {
    const app = booted();
    cliList(app);
    const picks = quickPicks(app);
    const ids = picks.map((p) => p.id);
    // The CLI pins its Fable row to 5.1, so neither the always-newest alias nor the previous
    // generation is reachable in one click — both belong here.
    assert.ok(ids.includes("fable"), ids.join(","));
    assert.ok(ids.includes("claude-fable-5[1m]"), ids.join(","));
    const prev = picks.find((p) => p.id === "claude-fable-5[1m]");
    assert.ok(prev.text.includes("Fable 5 with 1M context"), prev.text);
    assert.ok(prev.text.includes("previous Fable generation"), prev.text);
  });

  it("hides a quick-pick the main picker already offers", () => {
    const app = booted();
    // A CLI that lists Fable as the bare alias, and Opus 4.8 as a row of its own.
    app.pushMessage("models", {
      models: CLI.map((m) => (m.id === "claude-fable-5-1[1m]" ? Object.assign({}, m, { id: "fable", wire: "claude-fable-5-1" }) : m))
        .concat([{ id: "claude-opus-4-8[1m]", name: "Opus 4.8", label: "Opus 4.8 with 1M context", desc: "Pinned", wire: "claude-opus-4-8[1m]", ratio: 5, autoMode: true }]),
      effortsByModel: EFFORTS,
      source: "cli",
    });
    const ids = quickPicks(app).map((p) => p.id);
    assert.ok(!ids.includes("fable"), "the alias is one click in the main picker: " + ids.join(","));
    assert.ok(!ids.includes("claude-opus-4-8[1m]"), "already a row: " + ids.join(","));
    assert.ok(ids.includes("claude-fable-5[1m]") && ids.includes("opus"), ids.join(","));
  });

  it("applies a quick-pick straight to the host", () => {
    const app = booted();
    cliList(app);
    quickPicks(app);
    app.$("popover").querySelectorAll("#customSuggest .opt")
      .find((o) => o.getAttribute("data-id") === "claude-fable-5[1m]").click();
    const sent = app.sent("setModel");
    assert.strictEqual(sent[sent.length - 1].payload.model, "claude-fable-5[1m]");
  });

  it("measures Fable against its 1M window before and after the CLI names it", () => {
    const app = booted("claude-fable-5-1[1m]");
    cliList(app);
    app.pushMessage("contextUsage", { totalTokens: 50000 });
    assert.ok(app.$("ringBtn").title.startsWith("95%"), app.$("ringBtn").title);
    // The CLI reports the resolved id without the [1m] suffix; it is still the row's window.
    app.pushMessage("system", { subtype: "init", model: "claude-fable-5-1" });
    app.pushMessage("contextUsage", { totalTokens: 50000 });
    assert.ok(app.$("ringBtn").title.startsWith("95%"), app.$("ringBtn").title);
  });
});
