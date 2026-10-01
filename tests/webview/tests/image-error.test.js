"use strict";
// A long, image-heavy conversation can trip the API's many-image rule on an image pasted long ago;
// the CLI then answers every turn with the same API error. The panel explains it and offers to
// compact, which drops the old images.

const assert = require("assert");
const { describe, it } = require("../runner");
const { boot } = require("../harness");

const CLI_ERROR = "API Error: an image in the conversation could not be processed and was removed. Re-read the file with a different approach if you still need it.";

function booted() {
  const app = boot();
  app.pushMessage("init", { models: [{ id: "default", name: "Default" }], model: "default", modes: [], efforts: [] });
  return app;
}
const notes = (app) => app.$("messages").querySelectorAll(".image-error");

describe("rejected image", () => {
  it("explains a whole-message API error and offers to compact", () => {
    const app = booted();
    app.pushMessage("assistant", { content: [{ type: "text", text: CLI_ERROR }] });
    assert.strictEqual(notes(app).length, 1);
    assert.ok(notes(app)[0].textContent.includes("Compact now"), notes(app)[0].textContent);
    notes(app)[0].querySelector("#imgCompact").click();
    assert.strictEqual(app.sent("compact").length, 1);
    assert.strictEqual(notes(app).length, 0, "the note goes once acted on");
  });

  it("catches the error when it streams in, and says it once", () => {
    const app = booted();
    for (let i = 0; i < 2; i++) {
      app.pushMessage("assistantStart", {});
      app.pushMessage("assistantDelta", { text: CLI_ERROR.slice(0, 30) });
      app.pushMessage("assistantDelta", { text: CLI_ERROR.slice(30) });
      app.pushMessage("assistantEnd", {});
    }
    assert.strictEqual(notes(app).length, 1);
  });

  it("speaks up again after a compaction", () => {
    const app = booted();
    app.pushMessage("assistant", { content: [{ type: "text", text: CLI_ERROR }] });
    app.pushMessage("compacted", { trigger: "manual", preTokens: 100000, postTokens: 5000 });
    app.pushMessage("assistant", { content: [{ type: "text", text: CLI_ERROR }] });
    assert.strictEqual(notes(app).length, 2);
  });

  it("stays quiet for ordinary replies", () => {
    const app = booted();
    app.pushMessage("assistant", { content: [{ type: "text", text: "Here is the image description." }] });
    assert.strictEqual(notes(app).length, 0);
  });
});
