"use strict";
// A 60-line test runner. Node on this machine is v16, which predates `node --test`, and the repo
// deliberately carries no npm dependencies — so the runner is part of the repo.

const suites = [];
let current = null;

function describe(name, fn) {
  current = { name, tests: [] };
  suites.push(current);
  fn();
  current = null;
}

function it(name, fn) {
  if (!current) throw new Error("it() outside describe()");
  current.tests.push({ name, fn });
}

async function run() {
  let passed = 0;
  const failures = [];

  for (const suite of suites) {
    console.log("\n  " + suite.name);
    for (const test of suite.tests) {
      try {
        await test.fn();
        passed++;
        console.log("    ✓ " + test.name);
      } catch (err) {
        failures.push({ suite: suite.name, test: test.name, err });
        console.log("    ✗ " + test.name);
      }
    }
  }

  console.log("");
  if (failures.length) {
    console.log("Failures:\n");
    for (const f of failures) {
      console.log("  " + f.suite + " > " + f.test);
      const msg = (f.err && f.err.message) || String(f.err);
      console.log("    " + msg.split("\n").join("\n    "));
      if (f.err && f.err.stack) {
        const frame = f.err.stack.split("\n").find((l) => l.includes(".test.js"));
        if (frame) console.log("   " + frame.trim());
      }
      console.log("");
    }
  }
  console.log(passed + " passed, " + failures.length + " failed");
  return failures.length === 0;
}

module.exports = { describe, it, run };
