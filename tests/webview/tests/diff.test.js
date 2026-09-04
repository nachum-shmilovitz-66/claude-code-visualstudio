"use strict";
// Edit/MultiEdit/Write tool cards render as a real diff (v1.0.10), with colour bands that read as
// added and removed (v1.0.11, which took five attempts to get right). Nothing here was covered by a
// test at the time — the bands were checked by eye against a screenshot.

const assert = require("assert");
const { describe, it } = require("../runner");
const { boot } = require("../harness");

function toolCard(input, name) {
  const app = boot();
  app.pushMessage("toolUse", { id: "t1", name: name || "Edit", input });
  const card = app.$("messages").querySelector(".tool");
  assert.ok(card, "expected a tool card");
  return { app, card };
}

const rows = (card) => card.querySelectorAll(".dr");
const kinds = (card) => rows(card).map((r) => (r.classList.contains("add") ? "+"
  : r.classList.contains("del") ? "-" : r.classList.contains("meta") ? "…" : " "));
const textOf = (row) => row.querySelector(".dt").textContent;

describe("diff — Edit", () => {
  it("shows removed and added lines as their own banded rows", () => {
    const { card } = toolCard({ file_path: "a.cs", old_string: "int a = 1;", new_string: "int a = 2;" });

    assert.deepStrictEqual(kinds(card), ["-", "+"]);
    assert.strictEqual(textOf(rows(card)[0]), "int a = 1;");
    assert.strictEqual(textOf(rows(card)[1]), "int a = 2;");
  });

  it("keeps unchanged context lines unbanded", () => {
    const { card } = toolCard({
      file_path: "a.cs",
      old_string: "one\ntwo\nthree",
      new_string: "one\nTWO\nthree",
    });

    assert.deepStrictEqual(kinds(card), [" ", "-", "+", " "]);
  });

  it("counts the change in the header", () => {
    const { card } = toolCard({ file_path: "a.cs", old_string: "a\nb", new_string: "a\nb\nc\nd" });

    assert.strictEqual(card.querySelector(".dstat.add").textContent, "+2");
    assert.strictEqual(card.querySelector(".dstat.del").textContent, "−0");
  });

  it("numbers both sides, leaving the gutter blank on the side that has no line", () => {
    const { card } = toolCard({ file_path: "a.cs", old_string: "one\ntwo", new_string: "one\ntwo\nthree" });
    const added = rows(card).find((r) => r.classList.contains("add"));
    const gutters = added.querySelectorAll(".dn").map((g) => g.textContent);

    assert.strictEqual(gutters[0], "", "an added line has no line number on the old side");
    assert.strictEqual(gutters[1], "3");
  });

  it("marks the characters that changed inside a modified line", () => {
    const { card } = toolCard({ file_path: "a.cs", old_string: "int a = 1;", new_string: "int a = 2;" });
    const del = rows(card).find((r) => r.classList.contains("del"));
    const add = rows(card).find((r) => r.classList.contains("add"));

    assert.ok(del.querySelector(".dcd"), "the removed line should mark what went");
    assert.ok(add.querySelector(".dca"), "the added line should mark what arrived");
    assert.strictEqual(del.querySelector(".dcd").textContent, "1");
    assert.strictEqual(add.querySelector(".dca").textContent, "2");
    // The whole line still reads correctly with the marks in place.
    assert.strictEqual(textOf(del), "int a = 1;");
  });

  it("escapes code in the diff instead of rendering it", () => {
    const { card } = toolCard({
      file_path: "a.html",
      old_string: "<b>x</b>",
      new_string: '<img src=x onerror="alert(1)">',
    });

    assert.strictEqual(card.querySelector(".diff").querySelectorAll("img").length, 0);
    assert.strictEqual(textOf(rows(card)[0]), "<b>x</b>");
  });

  it("says the line numbers are relative, because a hunk is not the whole file", () => {
    const { card } = toolCard({ file_path: "a.cs", old_string: "a", new_string: "b" });
    assert.ok(card.querySelector(".dnote"), "an Edit hunk needs the relative-numbering note");
  });

  it("offers to open the real VS diff", () => {
    const { app, card } = toolCard({ file_path: "a.cs", old_string: "a", new_string: "b" });
    card.querySelector(".open-diff").click();

    const sent = app.sent("openDiff");
    assert.strictEqual(sent.length, 1);
    assert.strictEqual(sent[0].payload.id, "t1");
  });

  it("opens expanded, because a diff nobody clicks is a diff nobody reads", () => {
    const { card } = toolCard({ file_path: "a.cs", old_string: "a", new_string: "b" });
    assert.ok(card.classList.contains("open"));
  });
});

describe("diff — MultiEdit and Write", () => {
  it("renders one hunk per edit", () => {
    const { card } = toolCard({
      file_path: "a.cs",
      edits: [
        { old_string: "one", new_string: "ONE" },
        { old_string: "two", new_string: "TWO" },
      ],
    }, "MultiEdit");

    assert.strictEqual(card.querySelectorAll(".diff").length, 2);
    assert.deepStrictEqual(kinds(card), ["-", "+", "-", "+"]);
  });

  it("shows a Write as all-added, with absolute line numbers", () => {
    const { card } = toolCard({ file_path: "new.cs", content: "line one\nline two" }, "Write");

    assert.deepStrictEqual(kinds(card), ["+", "+"]);
    assert.strictEqual(card.querySelector(".dstat.add").textContent, "+2");
    assert.strictEqual(card.querySelector(".dnote"), null,
      "a whole-file write is not a hunk, so its numbering is not relative");
  });
});

describe("diff — large edits", () => {
  it("truncates a huge diff and says how much it held back", () => {
    const before = Array.from({ length: 500 }, (_, i) => "line " + i).join("\n");
    const after = Array.from({ length: 500 }, (_, i) => "LINE " + i).join("\n");
    const { card } = toolCard({ file_path: "big.cs", old_string: before, new_string: after });

    const meta = rows(card).filter((r) => r.classList.contains("meta"));
    assert.strictEqual(meta.length, 1, "expected one truncation row");
    assert.ok(/\d+ more lines/.test(textOf(meta[0])), textOf(meta[0]));
    assert.ok(rows(card).length < 1000, "the panel must not render a thousand rows");
  });
});
