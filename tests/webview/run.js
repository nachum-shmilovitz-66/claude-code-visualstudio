"use strict";
// Entry point: `node tests/webview/run.js`. Also invoked by WebViewUiTests so the JS suite shows up
// in Test Explorer and in any `dotnet test` run alongside the C# tests.

const fs = require("fs");
const path = require("path");
const { run } = require("./runner");

const dir = path.join(__dirname, "tests");
const files = fs.readdirSync(dir).filter((f) => f.endsWith(".test.js")).sort();
if (!files.length) {
  console.error("no test files found in " + dir);
  process.exit(1);
}
for (const f of files) require(path.join(dir, f));

run().then((ok) => process.exit(ok ? 0 : 1), (err) => {
  console.error(err && err.stack ? err.stack : err);
  process.exit(1);
});
