// Minimal, dependency-free markdown -> HTML renderer with HTML escaping.
// Good enough for chat: fenced code, inline code, bold/italic, headings, lists, links, hr, blockquote.
(function (global) {
  function esc(s) {
    return s
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  function inline(s) {
    // inline code first, protect from other rules
    const codes = [];
    s = s.replace(/`([^`]+)`/g, function (_, c) {
      codes.push("<code>" + esc(c) + "</code>");
      return "\u0000" + (codes.length - 1) + "\u0000";
    });
    s = esc(s);
    // links [text](url) — only http(s)/mailto; drop unsafe schemes (javascript:, data:, file:…)
    s = s.replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, function (_, text, url) {
      if (!/^(https?:|mailto:)/i.test(url)) return text;
      var safe = url.replace(/"/g, "%22");
      return '<a href="' + safe + '" target="_blank" rel="noreferrer noopener">' + text + "</a>";
    });
    // bold then italic
    s = s.replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>");
    s = s.replace(/(^|[^*])\*([^*]+)\*/g, "$1<em>$2</em>");
    s = s.replace(/_([^_]+)_/g, "<em>$1</em>");
    // restore inline code
    s = s.replace(/\u0000(\d+)\u0000/g, function (_, i) { return codes[+i]; });
    return s;
  }

  // A fenced block renders as a header bar (language on the left, a copy button on the right)
  // sitting on top of the <pre>. The button is always visible rather than hover-only: the point
  // of it is that a suggested command can be taken without hand-selecting the text, and on a
  // narrow tool window a control that only appears on hover is one nobody finds. app.js owns the
  // click, delegated -- streaming rewrites this markup on every chunk, so a listener bound here
  // would end up on a node that is already gone.
  const COPY_ICON =
    '<svg class="cico" viewBox="0 0 16 16" width="12" height="12" aria-hidden="true">' +
    '<path d="M5.5 3.5h7a1 1 0 0 1 1 1v7" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round" />' +
    '<rect x="2.5" y="5.5" width="9" height="9" rx="1.5" fill="none" stroke="currentColor" stroke-width="1.2" /></svg>';

  function codeBlock(lang, code) {
    return '<div class="cblock"><div class="cbar"><span class="clang">' + esc(lang) + '</span>' +
      '<button type="button" class="ccopy" title="Copy code">' + COPY_ICON +
      '<span class="clabel">Copy</span></button></div>' +
      '<pre data-lang="' + esc(lang) + '"><code>' + esc(code) + '</code></pre></div>';
  }

  function render(md) {
    // Security: inline() round-trips code spans through a \u0000-delimited placeholder, and esc()
    // does not escape \u0000 — so text arriving with a literal NUL (JSON carries it fine) could
    // forge a placeholder. Harmless while codes[] only ever holds escaped content, but that is a
    // fragile invariant to rest the escaping on. Drop NULs at the door instead.
    md = (md || "").replace(/\r\n/g, "\n").replace(/\u0000/g, "");
    const lines = md.split("\n");
    let html = "";
    let i = 0;
    let listType = null;

    function closeList() {
      if (listType) { html += "</" + listType + ">"; listType = null; }
    }

    while (i < lines.length) {
      let line = lines[i];

      // fenced code block
      const fence = line.match(/^```(\w*)\s*$/);
      if (fence) {
        closeList();
        const lang = fence[1] || "";
        const buf = [];
        i++;
        while (i < lines.length && !/^```\s*$/.test(lines[i])) { buf.push(lines[i]); i++; }
        i++; // skip closing fence
        html += codeBlock(lang, buf.join("\n"));
        continue;
      }

      // heading
      const h = line.match(/^(#{1,6})\s+(.*)$/);
      if (h) { closeList(); html += "<h" + h[1].length + ">" + inline(h[2]) + "</h" + h[1].length + ">"; i++; continue; }

      // hr
      if (/^(\*\*\*|---|___)\s*$/.test(line)) { closeList(); html += "<hr />"; i++; continue; }

      // blockquote
      if (/^>\s?/.test(line)) { closeList(); html += "<blockquote>" + inline(line.replace(/^>\s?/, "")) + "</blockquote>"; i++; continue; }

      // unordered list
      const ul = line.match(/^[-*+]\s+(.*)$/);
      if (ul) {
        if (listType !== "ul") { closeList(); html += "<ul>"; listType = "ul"; }
        html += "<li>" + inline(ul[1]) + "</li>"; i++; continue;
      }
      // ordered list
      const ol = line.match(/^\d+\.\s+(.*)$/);
      if (ol) {
        if (listType !== "ol") { closeList(); html += "<ol>"; listType = "ol"; }
        html += "<li>" + inline(ol[1]) + "</li>"; i++; continue;
      }

      // table: header row followed by separator row (|---|---|)
      if (/\|/.test(line) && i + 1 < lines.length && /^\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$/.test(lines[i + 1])) {
        closeList();
        var parseRow = function (r) { return r.replace(/^\||\|$/g, "").split("|").map(function (c) { return c.trim(); }); };
        var heads = parseRow(line);
        var aligns = parseRow(lines[i + 1]).map(function (c) {
          if (/^:-+:$/.test(c)) return "center";
          if (/^-+:$/.test(c)) return "right";
          return "left";
        });
        html += "<table><thead><tr>";
        heads.forEach(function (c, ci) { html += '<th style="text-align:' + (aligns[ci] || "left") + '">' + inline(c) + "</th>"; });
        html += "</tr></thead><tbody>";
        i += 2;
        while (i < lines.length && /\|/.test(lines[i])) {
          var cells = parseRow(lines[i]);
          html += "<tr>";
          cells.forEach(function (c, ci) { html += '<td style="text-align:' + (aligns[ci] || "left") + '">' + inline(c) + "</td>"; });
          html += "</tr>";
          i++;
        }
        html += "</tbody></table>";
        continue;
      }

      // blank
      if (/^\s*$/.test(line)) { closeList(); i++; continue; }

      // paragraph (merge consecutive non-blank, non-special lines)
      closeList();
      const para = [line];
      i++;
      while (i < lines.length && !/^\s*$/.test(lines[i]) && !/^(#{1,6}\s|```|>|[-*+]\s|\d+\.\s|\|)/.test(lines[i])) {
        para.push(lines[i]); i++;
      }
      html += "<p>" + para.map(inline).join("<br />") + "</p>";
    }
    closeList();
    return html;
  }

  global.md = { render: render, esc: esc };
})(window);
