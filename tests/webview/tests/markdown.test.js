"use strict";
// media/markdown.js — the renderer behind every assistant message. It is hand-written and
// dependency-free, which makes escaping its own responsibility: model output is untrusted text and
// the page's CSP is the only other thing standing between a crafted response and script execution.

const assert = require("assert");
const { describe, it } = require("../runner");
const { boot } = require("../harness");

const app = boot();
const md = app.context.window.md;
const render = (src) => md.render(src);

/** Render into a detached element so assertions can read text rather than markup. */
function dom(src) {
  const el = app.document.createElement("div");
  el.innerHTML = render(src);
  return el;
}

describe("markdown — block structure", () => {
  it("renders headings at their level", () => {
    assert.ok(render("# Title").includes("<h1>Title</h1>"));
    assert.ok(render("### Deep").includes("<h3>Deep</h3>"));
    assert.ok(render("####### too deep").indexOf("<h7") === -1, "there is no h7");
  });

  it("merges consecutive lines into one paragraph with line breaks", () => {
    const html = render("one\ntwo\n\nthree");
    assert.ok(html.includes("<p>one<br />two</p>"), html);
    assert.ok(html.includes("<p>three</p>"), html);
  });

  it("renders both kinds of list and closes them", () => {
    assert.strictEqual(render("- a\n- b"), "<ul><li>a</li><li>b</li></ul>");
    assert.strictEqual(render("1. a\n2. b"), "<ol><li>a</li><li>b</li></ol>");
  });

  it("closes an open list before a following block", () => {
    const html = render("- a\n\n# after");
    assert.ok(html.indexOf("</ul>") < html.indexOf("<h1>"), html);
  });

  it("renders blockquotes and rules", () => {
    assert.ok(render("> quoted").includes("<blockquote>quoted</blockquote>"));
    assert.ok(render("---").includes("<hr />"));
  });

  it("renders a table with per-column alignment", () => {
    const html = render("| a | b | c |\n|:--|:-:|--:|\n| 1 | 2 | 3 |");
    assert.ok(html.includes("<table>"), html);
    assert.ok(html.includes('<th style="text-align:left">a</th>'), html);
    assert.ok(html.includes('<th style="text-align:center">b</th>'), html);
    assert.ok(html.includes('<th style="text-align:right">c</th>'), html);
    assert.ok(html.includes('<td style="text-align:left">1</td>'), html);
  });

  it("normalises CRLF so Windows output does not render as one long line", () => {
    assert.strictEqual(render("a\r\n\r\nb"), render("a\n\nb"));
  });
});

describe("markdown — inline", () => {
  it("renders bold, italic and inline code", () => {
    assert.ok(render("**bold**").includes("<strong>bold</strong>"));
    assert.ok(render("*em*").includes("<em>em</em>"));
    assert.ok(render("_em_").includes("<em>em</em>"));
    assert.ok(render("`code`").includes("<code>code</code>"));
  });

  it("leaves markdown inside inline code alone", () => {
    // The code span is lifted out before the emphasis rules run; without that, `**x**` in a command
    // would render as bold inside a code span.
    const html = render("use `**not bold**` here");
    assert.ok(html.includes("<code>**not bold**</code>"), html);
    assert.ok(!html.includes("<strong>"), html);
  });

  it("keeps text out of the markup", () => {
    const el = dom("a < b & c > d");
    assert.strictEqual(el.textContent, "a < b & c > d");
  });
});

describe("markdown — untrusted input", () => {
  it("escapes HTML in model output instead of rendering it", () => {
    const html = render('<img src=x onerror="alert(1)">');
    assert.ok(!html.includes("<img"), html);
    assert.ok(html.includes("&lt;img"), html);
  });

  it("escapes HTML inside code blocks and code spans", () => {
    assert.ok(render("```\n<script>x</script>\n```").includes("&lt;script&gt;"));
    assert.ok(render("`<script>`").includes("<code>&lt;script&gt;</code>"));
  });

  it("keeps http, https and mailto links", () => {
    const html = render("[docs](https://docs.claude.com/x)");
    assert.ok(html.includes('href="https://docs.claude.com/x"'), html);
    assert.ok(html.includes('rel="noreferrer noopener"'), html);
    assert.ok(html.includes('target="_blank"'), html);
    assert.ok(render("[mail](mailto:a@b.c)").includes('href="mailto:a@b.c"'));
  });

  it("drops every other URL scheme, keeping the link text", () => {
    for (const url of ["javascript:alert(1)", "data:text/html;base64,x", "file:///c:/windows",
                       "vbscript:x", "JaVaScRiPt:alert(1)", "  javascript:alert(1)"]) {
      const html = render("[click](" + url.replace(/\s/g, "") + ")");
      assert.ok(!html.includes("<a "), "should not be a link: " + url + " -> " + html);
      assert.ok(html.includes("click"), "link text should survive: " + url);
    }
  });

  it("cannot be tricked into an attribute break by a quote in the URL", () => {
    // esc() runs over the whole line before the link rule, so a quote inside the URL arrives as
    // &quot; and stays inside the href value. What matters is that no second attribute appears.
    const el = dom('[x](https://a.test/")onmouseover="alert(1))');
    const a = el.querySelector("a");
    assert.ok(a, "the safe part should still be a link");
    assert.strictEqual(a.getAttribute("onmouseover"), null, "no attribute may be smuggled in");
    assert.strictEqual(a.getAttribute("href"), 'https://a.test/"');
    assert.deepStrictEqual(
      Array.from(a.attributes.keys()).sort(), ["href", "rel", "target"],
      "an anchor should carry exactly the three attributes the renderer writes");
  });

  it("strips NUL so the code-span placeholder cannot be forged", () => {
    // inline() round-trips code spans through a \u0000-delimited placeholder; a literal NUL in the
    // text could otherwise impersonate one.
    const html = render("a\u00000\u0000b `x`");
    assert.ok(!html.includes("\u0000"), JSON.stringify(html));
    assert.ok(html.includes("<code>x</code>"), html);
  });
});

describe("markdown — fenced code", () => {
  it("preserves the code verbatim through the DOM", () => {
    const code = 'if (a < b && c > d) { echo "hi"; }';
    const el = dom("```js\n" + code + "\n```");
    assert.strictEqual(el.querySelector("pre code").textContent, code);
  });

  it("tags the block with its language", () => {
    const el = dom("```powershell\nGet-Date\n```");
    assert.strictEqual(el.querySelector("pre").getAttribute("data-lang"), "powershell");
  });

  it("renders a block that is still streaming in, before its closing fence", () => {
    // assistantDelta re-renders on every chunk, so a half-arrived fence must not swallow the page.
    const el = dom("intro\n\n```sh\nnpm ci");
    assert.strictEqual(el.querySelector("pre code").textContent, "npm ci");
    assert.ok(el.textContent.includes("intro"));
  });
});
