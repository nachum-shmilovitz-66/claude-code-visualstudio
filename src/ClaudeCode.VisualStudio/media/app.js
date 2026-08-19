(function () {
  "use strict";

  const api = window.chrome && window.chrome.webview;
  const $ = (id) => document.getElementById(id);
  const els = {
    messages: $("messages"), input: $("input"),
    sendBtn: $("sendBtn"), stopBtn: $("stopBtn"),
    modelBtn: $("modelBtn"), contextBtn: $("contextBtn"), usageBtn: $("usageBtn"),
    plusBtn: $("plusBtn"), slashBtn: $("slashBtn"), ringBtn: $("ringBtn"), ringFg: $("ringFg"),
    modeBtn: $("modeBtn"), modeLabel: $("modeLabel"),
    statusText: $("statusText"), usage: $("usage"), attachments: $("attachments"),
    popover: $("popover"), cpop: $("cpop"), setupBanner: $("setupBanner"),
  };

  let running = false, currentAssistant = null, currentTurn = null, currentThinking = null;
  const toolCards = new Map();
  let attachments = [];
  let slashCommands = [];
  let commandsLoading = false;
  let fileList = [], atQuery = "", atItems = [], atIndex = 0;
  let models = [], modes = [], efforts = [], effortsByModel = {};
  let cur = { model: "default", mode: "default", effort: "none" };
  const totals = { costUsd: 0, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0, turns: 0 };
  // `live` = ctx.used came from per-request usage (contextUsage) rather than the cumulative
  // turn totals in `result`, which overcount and must not win once real numbers are in.
  const ctx = { used: 0, window: 200000, windowReported: false, model: "", baseline: 0, system: 0, live: false };
  // Post-compaction token count, held across the compaction turn's trailing `result` event.
  let compactPin = 0;
  let topOpen = null, cOpen = null;
  // Which "latest" the user dismissed. Dismiss is a snooze, not a mute: the host re-checks
  // hourly and flags that message as periodic, which clears this — so the reminder comes back
  // about an hour later. A release that lands in the meantime is new, so it shows straight away.
  let dismissedVersion = null;
  let updateInFlight = false;  // "Update CLI" clicked; waiting for the installed version to move
  let recheckPending = false;  // user asked for a re-check — report the outcome, not silence
  let lastCliVersion = null;   // CLI version from the previous setup message, to spot the change
  let updatedTo = null;        // new version, once an in-flight update has been confirmed landed
  let updateError = null;      // a background update failed — never let a hidden process fail quietly
  let inputHistory = [], histIndex = -1, histDraft = ""; // sent-input history; histIndex === -1 = live draft
  let acct = null;
  let thinkingVisible = true;

  function post(type, payload) { if (api) api.postMessage({ type: type, payload: payload || {} }); }
  // Numbers-only breadcrumbs to the host log (%LOCALAPPDATA%\ClaudeCodeVS\session.log) — the
  // WebView has no DevTools in Release builds, so layout bugs are otherwise unobservable.
  function diag(text) { post("diag", { text: String(text) }); }

  // ---- inbound ----
  function onHostMessage(ev) {
    let m = ev.data;
    if (typeof m === "string") { try { m = JSON.parse(m); } catch (e) { return; } }
    if (!m || !m.type) return;
    (handlers[m.type] || function () {})(m.payload || {});
  }
  if (api && api.addEventListener) api.addEventListener("message", onHostMessage);
  window.addEventListener("message", onHostMessage);

  // Stick-to-bottom: only auto-scroll when user is already near the bottom.
  // If user scrolled up to read history, leave scroll position alone.
  let stickBottom = true;
  function atBottom() {
    const m = els.messages;
    return (m.scrollHeight - m.scrollTop - m.clientHeight) < 40;
  }
  // Survive tool-window tab switches. Docked beside Solution Explorer, Claude's WebView is
  // hidden whenever the other tab is fronted, and a hidden WebView is laid out at zero height —
  // which clamps #messages' scrollTop to 0 and fires a scroll event on the way. Remember where
  // the user was while the view still had a real size, and put it back when the size returns.
  //
  // Restoring is not a single assignment. Coming back, the page is re-laid out over several
  // frames, so an early write lands while scrollHeight is still short and gets clamped near the
  // top — which is exactly what a one-shot restore produced in practice. Keep re-applying until
  // the target sticks and the content height stops moving.
  let savedTop = 0;
  let ignoreScrollUntil = 0;      // scroll events before this are our own layout noise
  let settling = false;
  function maxTop() { const m = els.messages; return Math.max(0, m.scrollHeight - m.clientHeight); }
  els.messages.addEventListener("scroll", function () {
    const m = els.messages;
    if (m.clientHeight === 0 || Date.now() < ignoreScrollUntil) return;
    stickBottom = atBottom();
    savedTop = m.scrollTop;
  });
  function restoreScroll(why) {
    const m = els.messages;
    if (settling || m.clientHeight === 0) return;
    settling = true;
    let frames = 0, stable = 0, lastHeight = -1;
    (function apply() {
      const target = stickBottom ? maxTop() : Math.min(savedTop, maxTop());
      if (m.scrollTop !== target) m.scrollTop = target;
      stable = (m.scrollHeight === lastHeight) ? stable + 1 : 0;
      lastHeight = m.scrollHeight;
      ignoreScrollUntil = Date.now() + 200;
      if (stable < 3 && ++frames < 40) { requestAnimationFrame(apply); return; }
      settling = false;
      ignoreScrollUntil = 0;
      diag("restore(" + why + ") frames=" + frames + " top=" + m.scrollTop + " max=" + maxTop() +
           " h=" + m.clientHeight + " stick=" + stickBottom);
    })();
  }
  document.addEventListener("visibilitychange", function () {
    const hidden = document.visibilityState === "hidden";
    diag("visibility=" + document.visibilityState + " h=" + els.messages.clientHeight +
         " top=" + els.messages.scrollTop + " max=" + maxTop());
    // Nothing that moves while we are hidden is the user scrolling.
    if (hidden) { ignoreScrollUntil = Infinity; return; }
    restoreScroll("show");
    // An update that completed behind another tab gets its read-time now that it is on screen.
    if (updatedTo) scheduleUpdatedDismiss();
    // A login that landed while we were hidden shows up immediately, not on the next tick.
    if (loginPollTimer) post("recheckSetup");
  });
  if (window.ResizeObserver) {
    let lastHeight = els.messages.clientHeight;
    new ResizeObserver(function () {
      const h = els.messages.clientHeight;
      if (h === lastHeight) return;
      const wasCollapsed = lastHeight === 0;
      if (wasCollapsed || h === 0) {
        diag("resize " + lastHeight + " -> " + h + " top=" + els.messages.scrollTop);
      }
      lastHeight = h;
      // Any regained height re-pins the view: the dock may collapse to a non-zero sliver rather
      // than exactly 0, so this must not be limited to the 0 -> N transition.
      if (h > 0) restoreScroll(wasCollapsed ? "expand" : "resize");
    }).observe(els.messages);
  }
  function scrollDown(force) { if (force || stickBottom) els.messages.scrollTop = els.messages.scrollHeight; }
  function addMsg(role) {
    const w = document.createElement("div"); w.className = "msg " + role;
    const b = document.createElement("div"); b.className = "bubble";
    w.appendChild(b); els.messages.appendChild(w); scrollDown(); return b;
  }
  // ---- activity timeline (VS Code-style rail with dots) ----
  function ensureTurn() {
    if (!currentTurn) { currentTurn = document.createElement("div"); currentTurn.className = "turn"; els.messages.appendChild(currentTurn); }
    return currentTurn;
  }
  function endTurn() { currentTurn = null; currentAssistant = null; currentThinking = null; }
  function addNode(kind, dotKind) {
    const turn = ensureTurn();
    const node = document.createElement("div"); node.className = "node" + (kind ? " " + kind : "");
    const dot = document.createElement("span"); dot.className = "node-dot" + (dotKind ? " " + dotKind : "");
    const main = document.createElement("div"); main.className = "node-main";
    node.appendChild(dot); node.appendChild(main); turn.appendChild(node); scrollDown();
    return { node: node, dot: dot, main: main };
  }
  function removeThinking() { const t = els.messages.querySelector(".thinking-node"); if (t) t.remove(); }
  function showThinking(label) {
    removeThinking();
    const n = addNode("thinking-node", "spin");
    n.main.innerHTML = '<div class="thinking"><span>' + window.md.esc(label || "Working") + '</span><span class="dot"></span><span class="dot"></span><span class="dot"></span></div>';
  }
  // Streamed extended-thinking: a collapsible "Thinking" node on the rail.
  function appendThinking(text) {
    removeThinking();
    if (!currentThinking) {
      const nd = addNode("think-node", "spin");
      const c = document.createElement("div"); c.className = "think";
      const h = document.createElement("div"); h.className = "think-head";
      h.innerHTML = '<span class="chev">▶</span><span class="think-label">Thinking…</span>';
      const bd = document.createElement("div"); bd.className = "think-body";
      h.addEventListener("click", () => c.classList.toggle("open"));
      c.appendChild(h); c.appendChild(bd); nd.main.appendChild(c);
      currentThinking = { bd: bd, buf: "", dot: nd.dot, label: h.querySelector(".think-label") };
    }
    currentThinking.buf += text || "";
    currentThinking.bd.textContent = currentThinking.buf;
    scrollDown();
  }
  function settleThinking() {
    if (!currentThinking) return;
    currentThinking.dot.className = "node-dot text";
    if (currentThinking.label) currentThinking.label.textContent = "Thinking";
    currentThinking = null;
  }
  function renderBlocks(b, blocks) {
    for (const x of blocks) {
      if (x.type === "text") { const d = document.createElement("div"); d.innerHTML = window.md.render(x.text || ""); b.appendChild(d); }
      else if (x.type === "tool_use") renderToolUse({ id: x.id, name: x.name, input: x.input });
    }
  }
  // The header wraps rather than ellipsising, so the summary can be long — but cap it so a
  // pasted multi-hundred-char command can't turn one tool call into a wall of text.
  const SUMMARY_MAX = 400;
  function summarize(name, input) {
    try {
      if (!input) return "";
      const s = input.command || input.file_path || input.path || input.pattern || input.url ||
        (input.prompt ? String(input.prompt).slice(0, 80) : JSON.stringify(input).slice(0, 80));
      return String(s).length > SUMMARY_MAX ? String(s).slice(0, SUMMARY_MAX) + "…" : String(s);
    }
    catch (e) { return ""; }
  }
  function isEditTool(name) { return name === "Edit" || name === "Write" || name === "MultiEdit"; }
  // Render an edit as a VS Code-style unified diff: lines the edit leaves alone stay as plain
  // context, only what actually changed gets a red/green band, and within a modified line the
  // changed characters are marked. The old renderer dumped the whole of old_string as removed
  // followed by the whole of new_string as added, so a one-word change read as a wall of colour.
  // We only ever get the hunk (old_string/new_string), never the file, so gutter numbers are
  // hunk-relative for Edit/MultiEdit; Write hands us the whole file, so there they are real.
  const DIFF_MAX_ROWS = 300;        // rows per hunk before truncating, so a big Write can't flood
  const LCS_MAX_CELLS = 1200000;    // DP guard — past this, fall back to flat remove-then-add
  function splitLines(s) { return s == null || s === "" ? [] : String(s).split("\n"); }
  // Classic LCS, but only over the lines that actually differ: trimming the shared head/tail
  // first is what keeps it cheap (a one-line change inside a 200-line hunk collapses to 1x1).
  function lineOps(a, b) {
    const ops = []; let lo = 0, aHi = a.length, bHi = b.length;
    while (lo < aHi && lo < bHi && a[lo] === b[lo]) lo++;
    while (aHi > lo && bHi > lo && a[aHi - 1] === b[bHi - 1]) { aHi--; bHi--; }
    for (let i = 0; i < lo; i++) ops.push({ t: " ", a: i, b: i });
    const n = aHi - lo, m = bHi - lo;
    if (n * m > LCS_MAX_CELLS) {                                                // pathological — don't hang the WebView
      for (let i = lo; i < aHi; i++) ops.push({ t: "-", a: i, b: -1 });
      for (let j = lo; j < bHi; j++) ops.push({ t: "+", a: -1, b: j });
    } else {
      const w = m + 1, dp = new Int32Array((n + 1) * w);
      for (let i = n - 1; i >= 0; i--) for (let j = m - 1; j >= 0; j--)
        dp[i * w + j] = a[lo + i] === b[lo + j] ? dp[(i + 1) * w + j + 1] + 1 : Math.max(dp[(i + 1) * w + j], dp[i * w + j + 1]);
      let i = 0, j = 0;
      while (i < n && j < m) {
        if (a[lo + i] === b[lo + j]) { ops.push({ t: " ", a: lo + i, b: lo + j }); i++; j++; }
        else if (dp[(i + 1) * w + j] >= dp[i * w + j + 1]) { ops.push({ t: "-", a: lo + i, b: -1 }); i++; }
        else { ops.push({ t: "+", a: -1, b: lo + j }); j++; }
      }
      while (i < n) { ops.push({ t: "-", a: lo + i, b: -1 }); i++; }
      while (j < m) { ops.push({ t: "+", a: -1, b: lo + j }); j++; }
    }
    for (let k = 0; k < a.length - aHi; k++) ops.push({ t: " ", a: aHi + k, b: bHi + k });
    return ops;
  }
  // Character-level marks for one changed line: trim the shared head/tail and mark the middle.
  // Lands exactly right on the common case (one renamed identifier); on a line with several
  // scattered edits it marks the whole span between them, which still beats no marking at all.
  function charSplit(o, n) {
    const min = Math.min(o.length, n.length);
    let s = 0; while (s < min && o[s] === n[s]) s++;
    let e = 0; while (e < min - s && o[o.length - 1 - e] === n[n.length - 1 - e]) e++;
    return { s: s, e: e };
  }
  // Only mark a pair that is plausibly the same line edited — otherwise two unrelated lines that
  // happen to be adjacent in a change run light up almost end to end and the marks mean nothing.
  function pairable(o, n) { const c = charSplit(o, n); return (c.s + c.e) * 3 >= Math.max(o.length, n.length); }
  function markLine(text, c, cls) {
    const E = window.md.esc, mid = text.slice(c.s, text.length - c.e);
    return E(text.slice(0, c.s)) + (mid ? '<span class="dc ' + cls + '">' + E(mid) + "</span>" : "") + E(text.slice(text.length - c.e));
  }
  function diffHunk(oldS, newS) {
    const a = splitLines(oldS), b = splitLines(newS), ops = lineOps(a, b);
    // Pair the i-th removed line of a change run with its i-th added line, so a modified line
    // renders as one marked pair rather than two flat bands.
    const pair = new Map();
    for (let i = 0; i < ops.length;) {
      if (ops[i].t === " ") { i++; continue; }
      let j = i; while (j < ops.length && ops[j].t === "-") j++;
      let k = j; while (k < ops.length && ops[k].t === "+") k++;
      for (let d = 0; d < Math.min(j - i, k - j); d++) {
        const o = a[ops[i + d].a], n = b[ops[j + d].b];
        if (pairable(o, n)) { const c = charSplit(o, n); pair.set(i + d, c); pair.set(j + d, c); }
      }
      i = k;
    }
    let rows = "", al = 1, bl = 1, shown = 0, more = 0, adds = 0, dels = 0;
    for (let idx = 0; idx < ops.length; idx++) {
      const op = ops[idx], isDel = op.t === "-", isAdd = op.t === "+";
      if (isAdd) adds++; else if (isDel) dels++;
      const aNo = isAdd ? "" : al++, bNo = isDel ? "" : bl++;
      if (shown++ >= DIFF_MAX_ROWS) { more++; continue; }
      const text = isAdd ? b[op.b] : a[op.a], mark = pair.get(idx);
      const html = mark ? markLine(text, mark, isDel ? "dcd" : "dca") : window.md.esc(text);
      rows += '<div class="dr ' + (isDel ? "del" : isAdd ? "add" : "ctx") + '"><span class="dn">' + aNo + '</span><span class="dn">' + bNo +
        '</span><span class="ds">' + (isDel ? "-" : isAdd ? "+" : " ") + '</span><span class="dt">' + html + "</span></div>";
    }
    if (more) rows += '<div class="dr meta"><span class="dn"></span><span class="dn"></span><span class="ds"></span><span class="dt">… ' + more + " more lines</span></div>";
    return '<div class="diff"><div class="dhead"><span class="dstat add">+' + adds + '</span><span class="dstat del">−' + dels +
      '</span></div><div class="dbody">' + rows + "</div></div>";
  }
  function diffBody(input, name) {
    let body, rel = true;
    if (name === "MultiEdit" && Array.isArray(input.edits)) body = input.edits.map((e) => diffHunk(e.old_string, e.new_string)).join("");
    else if (name === "Write") { body = diffHunk(null, input.content); rel = false; }  // whole-file write → all added; native diff shows true before/after
    else body = diffHunk(input.old_string, input.new_string);                          // Edit
    return (rel ? '<div class="dnote" title="This edit is a hunk, not the whole file, so the gutter counts from the first line shown.">line numbers are relative to the edit</div>' : "") +
      body + '<button class="open-diff" title="Open the full Before/After diff in Visual Studio">Open diff</button>';
  }
  function renderToolUse(t) {
    const nd = addNode("tool-node", "pending");
    const c = document.createElement("div"); c.className = "tool";
    const h = document.createElement("div"); h.className = "tool-head";
    h.innerHTML = '<span class="tname">' + window.md.esc(t.name || "tool") + '</span><span class="tsummary">' + window.md.esc(summarize(t.name, t.input)) + '</span><span class="chev">▶</span>';
    const bd = document.createElement("div"); bd.className = "tool-body";
    const edit = isEditTool(t.name) && t.input && t.input.file_path;
    if (edit) { bd.innerHTML = diffBody(t.input, t.name); c.classList.add("open"); }  // show edit diffs expanded by default
    else bd.innerHTML = "<pre>" + window.md.esc(JSON.stringify(t.input || {}, null, 2)) + "</pre>";
    h.addEventListener("click", () => c.classList.toggle("open"));
    c.appendChild(h); c.appendChild(bd); nd.main.appendChild(c);
    if (edit) { const od = bd.querySelector(".open-diff"); if (od) od.addEventListener("click", (e) => { e.stopPropagation(); post("openDiff", { id: t.id }); }); }
    if (t.id) toolCards.set(t.id, { bd: bd, dot: nd.dot }); scrollDown();
  }
  function renderToolResult(r) {
    const entry = r.id && toolCards.get(r.id);
    const text = typeof r.content === "string" ? r.content : JSON.stringify(r.content, null, 2);
    const pre = document.createElement("pre"); pre.style.marginTop = "8px";
    if (r.isError) pre.style.color = "var(--red)";
    pre.textContent = (text || "").slice(0, 4000);
    if (entry) {
      if (entry.dot) entry.dot.className = "node-dot " + (r.isError ? "error" : "done");
      const l = document.createElement("div"); l.style.cssText = "color:var(--fg-dim);font-size:11px"; l.textContent = r.isError ? "result (error)" : "result";
      entry.bd.appendChild(l); entry.bd.appendChild(pre);
    }
  }
  function renderPermission(p) {
    const nd = addNode("perm-node", "perm");
    const c = document.createElement("div"); c.className = "perm"; c.dataset.id = p.id;
    c.innerHTML = '<div class="ptitle">Allow ' + window.md.esc(p.tool || "tool") + '?</div><div class="pbody"><pre>' + window.md.esc(JSON.stringify(p.input || {}, null, 2)) + '</pre></div><div class="pactions"><button class="allow" data-b="allow">Allow</button><button class="deny" data-b="deny">Deny</button></div>';
    c.querySelectorAll("button").forEach((btn) => btn.addEventListener("click", () => {
      post("permissionResponse", { id: p.id, behavior: btn.dataset.b });
      markPermResolved(c, btn.dataset.b);
    }));
    nd.main.appendChild(c); scrollDown();
  }
  function markPermResolved(c, behavior, note) {
    if (!c || c.classList.contains("resolved")) return;
    c.classList.add("resolved");
    const a = c.querySelector(".pactions");
    if (a) a.innerHTML = "<em>" + (behavior === "deny" ? "Denied" : "Allowed") + (note ? " " + window.md.esc(note) : "") + "</em>";
  }
  // The card was answered for us (switching to Auto mode allows whatever is still pending).
  function resolvePermById(id, behavior, note) {
    document.querySelectorAll(".perm").forEach((c) => { if (c.dataset.id === id) markPermResolved(c, behavior, note); });
  }

  const handlers = {
    init: (p) => {
      if (p.theme) applyTheme(p.theme);
      if (p.models) models = p.models;
      if (p.modes) modes = p.modes;
      if (p.efforts) efforts = p.efforts;
      if (p.effortsByModel) effortsByModel = p.effortsByModel;
      if (p.model) cur.model = p.model;
      if (p.permissionMode) cur.mode = p.permissionMode;
      if (p.effort) cur.effort = p.effort;
      if (typeof p.showThinking === "boolean") thinkingVisible = p.showThinking;
      applyEffortsForModel();
      updateModeLabel();
      applyThinkingVisibility();
    },
    commands: (p) => { slashCommands = p.commands || []; commandsLoading = false; if (cOpen === "slash") { const q = els.cpop.querySelector("#palq"); filterPalette(q ? q.value : ""); } },
    commandsLoading: (p) => { commandsLoading = !!p.on; if (cOpen === "slash") { const q = els.cpop.querySelector("#palq"); filterPalette(q ? q.value : ""); } },
    setup: (p) => renderSetupBanner(p),
    // Outcome of a background `claude update`. The host reports it directly rather than leaving
    // the page to infer it from a version change, so the confirmation can't be lost to a race
    // between this and the status message that follows.
    cliUpdate: (p) => {
      if (p.state === "running") { updateInFlight = true; updateError = null; }
      else if (p.state === "failed") { updateInFlight = false; updateError = p.detail || "The update failed."; }
      else if (p.state === "done") {
        updateInFlight = false; updateError = null;
        if (p.changed && p.version) {
          updatedTo = p.version; dismissedVersion = null;
          // The cached status predates the update, so rendering from it would flash the old
          // "update available" prompt until the refreshed status lands. The host has just told
          // us what is installed — trust it; the forced status that follows refines the rest.
          // Copy rather than edit in place: lastSetup aliases a received message payload.
          lastSetup = Object.assign({}, lastSetup, { cliVersion: p.version, cliOutdated: false });
        }
      }
      renderSetupBanner(lastSetup || {});
    },
    files: (p) => { fileList = p.files || []; if (cOpen === "at") filterAt(); },
    context: (p) => mergeContextIde(p),
    theme: (p) => applyTheme(p),
    status: (p) => {
      running = p.state === "thinking" || p.state === "running";
      els.statusText.textContent = p.text || cap(p.state || "Ready");
      els.sendBtn.classList.toggle("hidden", running);
      els.stopBtn.classList.toggle("hidden", !running);
      if (!running) removeThinking();
    },
    assistantStart: () => { removeThinking(); settleThinking(); currentAssistant = null; },
    assistantDelta: (p) => {
      if (!currentAssistant) { settleThinking(); const n = addNode("text-node", "text"); currentAssistant = { el: n.main, buf: "" }; }
      currentAssistant.buf += p.text || ""; currentAssistant.el.innerHTML = window.md.render(currentAssistant.buf); scrollDown();
    },
    assistantEnd: () => { settleThinking(); currentAssistant = null; },
    assistant: (p) => { removeThinking(); settleThinking(); const n = addNode("text-node", "text"); renderBlocks(n.main, p.content || []); currentAssistant = null; scrollDown(); },
    thinking: (p) => showThinking(p.label),
    thinkingDelta: (p) => appendThinking(p.text),
    toolUse: (p) => { removeThinking(); renderToolUse(p); },
    toolResult: (p) => renderToolResult(p),
    permission: (p) => { removeThinking(); renderPermission(p); },
    permissionResolved: (p) => resolvePermById(p.id, p.behavior, "(Auto mode)"),
    // Live context size, one per API request. Authoritative for the ring — see the `result` handler.
    contextUsage: (p) => {
      ctx.used = +(p.totalTokens || p.promptTokens || 0);
      ctx.live = true;
      // The cached prefix (system prompt + tools + skills) is written cold on the session's first
      // request — that write IS the prefix size. Only take the baseline from such a request: on a
      // resumed session the first request we see reads the whole restored conversation back from
      // cache, and counting that as "system" would swallow every message into the wrong bucket.
      if (!ctx.baseline && !(+(p.cacheReadTokens || 0))) ctx.baseline = +(p.cacheCreationTokens || 0);
      ctx.system = Math.min(ctx.baseline || 0, ctx.used);
      updateRing();
      if (topOpen === "context") renderContext();
    },
    result: (p) => {
      const parts = [];
      if (p.costUsd != null) parts.push("$" + Number(p.costUsd).toFixed(4));
      if (p.inputTokens != null) parts.push(p.inputTokens + " in");
      if (p.outputTokens != null) parts.push(p.outputTokens + " out");
      if (p.durationMs != null) parts.push((p.durationMs / 1000).toFixed(1) + "s");
      els.usage.textContent = parts.join(" · ");
      totals.costUsd += +(p.costUsd || 0); totals.inputTokens += +(p.inputTokens || 0);
      totals.outputTokens += +(p.outputTokens || 0); totals.turns += 1;
      totals.cacheReadTokens += +(p.cacheReadTokens || 0); totals.cacheCreationTokens += +(p.cacheCreationTokens || 0);
      // context window usage. A /compact turn ends with a result whose usage still describes the
      // PRE-compaction context, and it arrives after compact_boundary — so honour the post-compact
      // figure the CLI already gave us for this one turn instead of letting it be clobbered.
      //
      // Otherwise the ring is driven by `contextUsage` (per-request), NOT by these totals: this
      // event's usage is summed over every API request of the turn, so a turn with tool round-trips
      // counts the cached prefix once per request and races past 100% of the window.
      if (compactPin) { ctx.used = compactPin; ctx.live = false; compactPin = 0; }
      else if (!ctx.live) ctx.used = (+(p.inputTokens || 0)) + (+(p.cacheReadTokens || 0)) + (+(p.cacheCreationTokens || 0));
      if (p.contextWindow) { ctx.window = +p.contextWindow; ctx.windowReported = true; }
      if (p.model) ctx.model = p.model;
      ctx.system = Math.min(ctx.baseline || 0, ctx.used);
      updateRing();
      if (topOpen === "usage") renderUsage();
      if (topOpen === "context") renderContext();
    },
    error: (p) => {
      removeThinking();
      const msg = p.message || "Error";
      const low = msg.toLowerCase();
      let extra = "";
      if (/log ?in|logged in|auth|credential|unauthor|could not launch|exited \(code/.test(low))
        extra = '<div class="err-actions"><button class="sb-btn" data-act="login">Open terminal to log in</button></div>';
      else if (/credit|billing|subscription|quota|insufficient|payment|plan/.test(low))
        extra = '<div class="err-note">Claude Code needs a paid Pro/Max plan or API credits — a free account can\'t run it.</div>';
      const b = addMsg("assistant");
      b.innerHTML = '<span style="color:var(--red)">⚠ ' + window.md.esc(msg) + "</span>" + extra;
      const lb = b.querySelector('[data-act="login"]');
      if (lb) lb.addEventListener("click", () => post("openClaudeTerminal"));
      post("recheckSetup"); // refresh the onboarding banner after a failure
    },
    system: (p) => { if (p.subtype === "init" && p.model) ctx.model = p.model; },
    clear: () => { els.messages.innerHTML = ""; els.usage.textContent = ""; endTurn(); toolCards.clear(); },
    restore: (p) => {
      endTurn(); els.messages.innerHTML = ""; toolCards.clear();
      if (p.model) cur.model = p.model;
      if (p.mode) { cur.mode = p.mode; updateModeLabel(); }
      if (p.effort) cur.effort = p.effort;
      if (typeof p.showThinking === "boolean") { thinkingVisible = p.showThinking; applyThinkingVisibility(); }
      applyEffortsForModel();
      const msgs = p.messages || [];
      if (msgs.length) { const d = document.createElement("div"); d.className = "compacted-divider"; d.innerHTML = "<span>Restored previous conversation</span>"; els.messages.appendChild(d); }
      msgs.forEach((m) => {
        endTurn();
        if (m.role === "user") { addMsg("user").innerHTML = window.md.render(m.text || ""); }
        else { const n = addNode("text-node", "text"); n.main.innerHTML = window.md.render(m.text || ""); }
      });
      endTurn(); scrollDown();
    },
    accountData: (p) => { acct = p; if (topOpen === "usage") renderUsage(); },
    mcpList: (p) => { lastMcp = p.servers || []; lastMcpError = p.error || null; if (topOpen === "mcp") renderMcp(); else if (topOpen === "context") renderContext(); },
    // The CLI compacted its context in place (system/compact_boundary). It emits no assistant
    // text, so all the transcript needs is a divider — with the real before/after token counts
    // from compact_metadata rather than a guess. "auto" means the window filled and the CLI
    // compacted on its own, which is worth labelling differently from a deliberate /compact.
    compacted: (p) => {
      removeThinking(); endTurn();
      const n = document.createElement("div");
      n.className = "compacted-divider";
      const pre = +(p.preTokens || 0), post = +(p.postTokens || 0);
      let label = p.trigger === "auto" ? "Auto-compacted" : "Compacted";
      if (pre && post) label += " · " + fmt(pre) + " → " + fmt(post) + " tokens";
      n.innerHTML = "<span>" + window.md.esc(label) + "</span>";
      els.messages.appendChild(n);
      // Re-baseline the ring off what the CLI reports it actually kept, instead of zeroing and
      // waiting for the next turn's result to correct it.
      ctx.used = post || 0; ctx.baseline = 0; ctx.system = 0; ctx.live = false;
      compactPin = post || 0;   // survive this turn's trailing `result` (see the result handler)
      updateRing(); scrollDown();
      if (topOpen === "context") renderContext();
    },
    attachImage: (p) => { attachments.push({ mediaType: p.mediaType, data: p.data, name: p.name }); renderAttachments(); },
    insertText: (p) => { els.input.value += (els.input.value && !els.input.value.endsWith(" ") ? " " : "") + (p.text || ""); els.input.focus(); autoGrow(); },
    sentSelection: (p) => attachSelectionChip(p),
    debugBreak: (p) => renderDebugBreak(p),
  };

  // The VS debugger paused (breakpoint / step / exception). Drop a banner into the transcript
  // so the user can ask about the live runtime — the next send auto-attaches locals/stack.
  function renderDebugBreak(p) {
    p = p || {};
    const isExc = p.reason === "Exception" || !!p.exception;
    const where = p.function
      ? (p.function + (p.line ? " (" + fileName(p.file) + ":" + p.line + ")" : ""))
      : (p.file ? fileName(p.file) + (p.line ? ":" + p.line : "") : "");
    let head = isExc ? "⚠ Exception" : "⏸ Paused";
    if (p.exception) head += " — " + p.exception;
    else if (p.reason && p.reason !== "Break") head += " — " + p.reason;
    const div = document.createElement("div");
    div.className = "debug-break" + (isExc ? " exc" : "");
    div.innerHTML = '<div class="db-head">' + window.md.esc(head) + '</div>'
      + (where ? '<div class="db-where">' + window.md.esc(where) + '</div>' : "")
      + '<div class="db-actions"><button class="db-btn" data-act="explain">Ask Claude about this</button></div>';
    div.querySelector('[data-act="explain"]').addEventListener("click", () => {
      const q = isExc
        ? "The debugger stopped on an exception. Explain what caused it and how to fix it."
        : "The debugger is paused here. Explain the current state and what the code is doing.";
      els.input.value = q; els.input.focus(); autoGrow();
    });
    els.messages.appendChild(div); scrollDown();
  }
  function fileName(p) { return p ? String(p).split(/[\\/]/).pop() : ""; }

  // Prepend a "selection attached" chip to the most recent user bubble — shows what editor
  // selection the host captured and sent as context, click to open the file at that line.
  function attachSelectionChip(p) {
    if (!p || !p.filePath) return;
    const bubbles = els.messages.querySelectorAll(".msg.user .bubble");
    const b = bubbles[bubbles.length - 1];
    if (!b) return;
    const name = String(p.filePath).split(/[\\/]/).pop() || p.filePath;
    const rng = p.startLine === p.endLine ? ("L" + p.startLine) : ("L" + p.startLine + "–" + p.endLine);
    const wrap = document.createElement("div");
    wrap.className = "msg-selection";
    wrap.innerHTML = '<span class="sel-chip" title="' + window.md.esc(p.filePath + " (lines " + p.startLine + "–" + p.endLine + ") — click to open") + '">'
      + '<span class="sel-ico">⌗</span><span class="sel-name">' + window.md.esc(name) + '</span>'
      + '<span class="sel-range">' + window.md.esc(rng) + '</span></span>';
    wrap.querySelector(".sel-chip").addEventListener("click", () => post("openFile", { path: p.filePath, line: p.startLine }));
    b.insertBefore(wrap, b.firstChild);
  }

  function applyTheme(t) { const r = document.documentElement.style; Object.keys(t).forEach((k) => { if (k.startsWith("--")) r.setProperty(k, t[k]); }); }
  function cap(s) { return s ? s.charAt(0).toUpperCase() + s.slice(1) : s; }
  function fmt(n) { n = +n || 0; if (n >= 1e6) return (n / 1e6).toFixed(1) + "M"; if (n >= 1e3) return (n / 1e3).toFixed(1) + "k"; return String(n); }
  function modeName(id) { const m = modes.find((x) => x.id === id); return m ? m.name : id; }
  function updateModeLabel() { els.modeLabel.textContent = modeName(cur.mode).replace(/ mode$/i, "").replace("Edit automatically", "Auto-edit").replace("Ask before edits", "Ask"); }
  function applyThinkingVisibility() {
    els.messages.classList.toggle("hide-thinking", !thinkingVisible);
  }
  // Pick the effort list for the current model, falling back to the flat list.
  // Clamp cur.effort to the new list so the slider never points at an
  // unsupported level after a model switch.
  // Own-property lookup: cur.model is user-typed, so a plain obj[key] would resolve
  // Object.prototype members ("constructor", "hasOwnProperty", …) to inherited functions.
  function own(o, k) { return o && Object.prototype.hasOwnProperty.call(o, k) ? o[k] : undefined; }
  function applyEffortsForModel() {
    // Custom model ids have no per-model entry; assume the full (default/Opus) range.
    const list = own(effortsByModel, cur.model) || own(effortsByModel, "default") || efforts;
    if (list && list.length) efforts = list;
    if (!efforts.some((e) => e.id === cur.effort)) {
      cur.effort = (efforts[0] || { id: "none" }).id;
      post("setEffort", { effort: cur.effort });
    }
    if (!visibleModes().some((m) => m.id === cur.mode)) {
      cur.mode = "default";
      post("setPermissionMode", { mode: cur.mode });
      updateModeLabel();
    }
  }
  // Maps a model picker id to the wire name handed to the CLI, also shown in the "Switched to"
  // divider. All aliases, never dated ids — the CLI resolves each to the newest model in that
  // family at launch, so a new release needs no extension rebuild. Mirrors ClaudeSession.DefaultModel.
  const MODEL_WIRE = { default: "opus[1m]", fable: "fable", sonnet: "sonnet", haiku: "haiku" };
  // Quick-picks shown under the Custom-model input — click to fill + apply. Deliberately
  // excludes what the main picker already offers one-click; lists the other context/family
  // combinations plus pinned older snapshots. Still open-ended: any valid id works
  // (dated snapshots, [1m] 1M-context variants, etc). Availability depends on the CLI/account.
  // The display name is derived with prettyModel(), so these rows read the same
  // "<model> · <what it is for>" way as the main picker without repeating the name here.
  const MODEL_SUGGESTIONS = [
    { id: "opus", desc: "Latest Opus, standard 200k context" },
    { id: "sonnet[1m]", desc: "Latest Sonnet, 1M context" },
    { id: "claude-opus-4-8[1m]", desc: "Pinned — stays on 4.8 as newer models ship" },
    { id: "claude-opus-4-8", desc: "Pinned, standard 200k context" },
    { id: "claude-opus-4-7[1m]", desc: "Pinned — previous Opus generation" },
  ];
  function showModelDivider(id) {
    const d = document.createElement("div");
    d.className = "compacted-divider";
    d.innerHTML = "<span>Switched to " + window.md.esc(own(MODEL_WIRE, id) || id) + "</span>";
    els.messages.appendChild(d); scrollDown();
  }
  function effortDesc(id) {
    switch (id) {
      case "low": return "~4k tokens";
      case "medium": return "~10k tokens";
      case "high": return "~16k tokens";
      case "extrahigh": return "~24k tokens";
      case "max": return "~32k tokens";
      case "ultracode": return "xhigh + workflows";
      default: return "no extended thinking";
    }
  }
  // VS Code-style dumbbell icon for the Effort row.
  const DUMBBELL = '<svg class="eicon" width="14" height="14" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><rect x="1.5" y="9" width="3" height="6" rx="1"/><rect x="4.7" y="7" width="2.2" height="10" rx="1"/><rect x="6.9" y="10.5" width="10.2" height="3"/><rect x="17.1" y="7" width="2.2" height="10" rx="1"/><rect x="19.5" y="9" width="3" height="6" rx="1"/></svg>';

  // ---- context ring ----
  // The window the ring and the Context dialog both measure against. Until the CLI reports one
  // (it only does so on a `result`), fall back to what the *selected* model implies — a 1M model
  // measured against the 200k default made the ring read 5× the dialog's percentage.
  function ctxWindow() {
    if (ctx.windowReported && ctx.window) return ctx.window;
    const shown = ctx.model || own(MODEL_WIRE, cur.model) || cur.model;
    return /\[1m\]/i.test(shown) ? 1000000 : 200000;
  }
  // Picking a different model changes the window (1M Opus -> 200k Sonnet), but the CLI only says
  // so on the next `result`. Drop the reported value so ctxWindow() follows the new selection
  // right away instead of measuring against the old model's window for one turn.
  function onModelSwitched() { ctx.windowReported = false; ctx.model = ""; updateRing(); if (topOpen === "context") renderContext(); }
  function updateRing() {
    const win = ctxWindow();
    const C = 94.2; const frac = win ? Math.min(1, ctx.used / win) : 0;
    els.ringFg.style.strokeDashoffset = (C * (1 - frac)).toFixed(1);
    const remain = Math.round((1 - frac) * 100);
    els.ringBtn.title = remain + "% of context remaining until auto-compact. Click to compact now.";
  }

  // ---- composer ----
  function autoGrow() {
    els.input.style.height = "auto";
    const sh = els.input.scrollHeight;
    els.input.style.height = Math.min(sh, 200) + "px";
    els.input.style.overflowY = sh > 200 ? "auto" : "hidden";
  }
  // Sent-input history (Up/Down in the composer). Browses only at the edge lines so normal
  // multi-line cursor movement still works; setComposer assigns .value directly (no "input"
  // event), so browsing never re-triggers slash/@ detection or resets histIndex.
  function caretOnFirstLine() { const v = els.input.value, p = els.input.selectionStart; return v.lastIndexOf("\n", p - 1) === -1; }
  function caretOnLastLine() { const v = els.input.value, p = els.input.selectionEnd; return v.indexOf("\n", p) === -1; }
  function setComposer(v) { els.input.value = v; autoGrow(); const n = v.length; try { els.input.setSelectionRange(n, n); } catch (_) {} }
  function histPrev() {
    if (!inputHistory.length) return;
    if (histIndex === -1) { histDraft = els.input.value; histIndex = inputHistory.length - 1; }
    else if (histIndex > 0) histIndex--;
    setComposer(inputHistory[histIndex]);
  }
  function histNext() {
    if (histIndex === -1) return;
    if (histIndex < inputHistory.length - 1) setComposer(inputHistory[++histIndex]);
    else { histIndex = -1; setComposer(histDraft); }
  }
  function send() {
    const text = els.input.value.trim();
    if (!text && attachments.length === 0) return;
    endTurn();
    let html = attachments.length ? renderMsgAttachments(attachments) : "";
    if (text) html += window.md.render(text);
    stickBottom = true; // sending a new prompt re-follows the conversation
    addMsg("user").innerHTML = html;
    post("send", { text: text, images: attachments });
    if (text && inputHistory[inputHistory.length - 1] !== text) { inputHistory.push(text); if (inputHistory.length > 100) inputHistory.shift(); }
    histIndex = -1; histDraft = "";
    els.input.value = ""; attachments = []; renderAttachments(); autoGrow(); showThinking("Working");
  }
  function renderMsgAttachments(list) {
    let h = '<div class="msg-attachments">';
    list.forEach((a) => {
      const isImg = a.data && (!a.mediaType || /^image\//i.test(a.mediaType));
      const nm = window.md.esc(a.name || (isImg ? "image" : "file"));
      if (isImg) {
        const mt = (a.mediaType && /^image\//i.test(a.mediaType)) ? String(a.mediaType).replace(/[^a-z0-9/+.\-]/gi, "") : "image/png";
        const src = "data:" + mt + ";base64," + a.data;
        h += '<span class="msg-att-chip"><img class="msg-att-img" src="' + src + '" title="' + nm + ' — click to open" /><span class="msg-att-name">' + nm + "</span></span>";
      } else {
        h += '<span class="msg-att-file">📄 ' + nm + "</span>";
      }
    });
    return h + "</div>";
  }
  // Click an attachment thumbnail to open it full-size in a lightbox overlay.
  function openLightbox(src) {
    const ov = document.createElement("div"); ov.className = "lightbox";
    const img = document.createElement("img"); img.src = src; ov.appendChild(img);
    ov.addEventListener("click", () => ov.remove());
    document.getElementById("app").appendChild(ov);
  }
  els.messages.addEventListener("click", (e) => {
    const img = e.target.closest ? e.target.closest(".msg-att-img") : null;
    if (img) openLightbox(img.getAttribute("src"));
  });
  function renderAttachments() {
    els.attachments.innerHTML = "";
    attachments.forEach((a, i) => {
      const c = document.createElement("span"); c.className = "chip";
      // Sanitize the media type before it goes into the data: URI (mirrors renderMsgAttachments) so a
      // crafted value can't break out of the src attribute. data is base64 (no quotes) from the host/clipboard.
      const mt = (a.mediaType && /^image\//i.test(a.mediaType)) ? String(a.mediaType).replace(/[^a-z0-9/+.\-]/gi, "") : "image/png";
      c.innerHTML = '<img src="data:' + mt + ";base64," + a.data + '" /><span>' + window.md.esc(a.name || "image") + '</span><button data-i="' + i + '" style="background:none;border:none;color:inherit;cursor:pointer">×</button>';
      c.querySelector("button").addEventListener("click", () => { attachments.splice(i, 1); renderAttachments(); });
      els.attachments.appendChild(c);
    });
  }
  els.input.addEventListener("input", () => {
    autoGrow();
    histIndex = -1; // typing exits history browsing
    if (els.input.value.startsWith("/") && cOpen !== "slash") { openSlash(); return; }
    const m = els.input.value.match(/(?:^|\s)@(\S*)$/);
    if (m) { if (!fileList.length) post("getFiles"); openAt(m[1]); }
    else if (cOpen === "at") closeC();
  });
  els.input.addEventListener("keydown", (e) => {
    if (cOpen === "slash") { if (paletteKey(e)) return; }
    if (cOpen === "at") { if (atKey(e)) return; }
    // Browse previously-sent inputs with Up/Down (shell-style): only when no popover is open,
    // and only at the edge lines so multi-line cursor movement still works normally.
    if (!cOpen && e.key === "ArrowUp" && inputHistory.length && caretOnFirstLine()) { e.preventDefault(); histPrev(); return; }
    if (!cOpen && e.key === "ArrowDown" && histIndex !== -1 && caretOnLastLine()) { e.preventDefault(); histNext(); return; }
    if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); send(); }
  });
  els.input.addEventListener("paste", (e) => {
    const items = e.clipboardData && e.clipboardData.items; if (!items) return;
    for (const it of items) if (it.type && it.type.startsWith("image/")) {
      const type = it.type; const f = it.getAsFile(); const rd = new FileReader();
      rd.onload = () => { attachments.push({ mediaType: type, data: String(rd.result).split(",")[1], name: "pasted." + (type.split("/")[1] || "png") }); renderAttachments(); };
      rd.readAsDataURL(f);
    }
  });

  els.sendBtn.addEventListener("click", send);
  els.stopBtn.addEventListener("click", () => post("interrupt"));
  els.ringBtn.addEventListener("click", () => { closeAll(); post("compact"); showThinking("Compacting"); });

  els.modelBtn.addEventListener("click", () => toggleTop("model"));
  els.contextBtn.addEventListener("click", () => toggleTop("context"));
  els.usageBtn.addEventListener("click", () => toggleTop("usage"));
  els.plusBtn.addEventListener("click", () => toggleC("plus"));
  els.slashBtn.addEventListener("click", () => toggleC("slash"));
  els.modeBtn.addEventListener("click", () => toggleC("mode"));

  document.addEventListener("mousedown", (e) => {
    if (topOpen && !els.popover.contains(e.target) && !e.target.closest("#modelBtn,#contextBtn,#usageBtn")) closeTop();
    if (cOpen && !els.cpop.contains(e.target) && !e.target.closest("#plusBtn,#slashBtn,#modeBtn,#input")) closeC();
  });
  document.addEventListener("keydown", (e) => { if (e.key === "Escape") closeAll(); });

  // ---- top popovers ----
  function resetTotals() {
    totals.costUsd = 0; totals.inputTokens = 0; totals.outputTokens = 0; totals.cacheReadTokens = 0; totals.cacheCreationTokens = 0; totals.turns = 0; els.usage.textContent = "";
    ctx.used = 0; ctx.baseline = 0; ctx.system = 0; ctx.live = false; updateRing();
  }
  function activeTop() { [["model", els.modelBtn], ["context", els.contextBtn], ["usage", els.usageBtn]].forEach(([k, b]) => b.classList.toggle("active", topOpen === k)); }
  function closeTop() { topOpen = null; els.popover.classList.add("hidden"); els.popover.innerHTML = ""; activeTop(); }
  function openTop(which) { closeC(); topOpen = which; activeTop(); if (which === "model") renderModel(); else if (which === "context") { post("getContext"); if (!(lastIde && (lastIde.mcpServers || []).length) && lastMcp == null) post("getMcp"); renderContext(); } else if (which === "usage") { post("getUsage"); renderUsage(); } else if (which === "mcp") { lastMcp = null; post("getMcp"); renderMcp(); } }
  function toggleTop(w) { if (topOpen === w) closeTop(); else openTop(w); }
  function showTop(html) { els.popover.innerHTML = html; els.popover.classList.remove("hidden"); const x = els.popover.querySelector(".close-x"); if (x) x.addEventListener("click", closeTop); }

  // Relative per-token cost badge shown next to each model. The host sends
  // `ratio` = price vs the cheapest model (Haiku = 1×). Custom models have none.
  function ratioBadge(ratio) {
    if (typeof ratio !== "number" || !(ratio > 0)) return "";
    const r = ratio % 1 === 0 ? String(ratio) : ratio.toFixed(1);
    return '<div class="oratio" title="Token cost ≈ ' + r + '× the cheapest model (Haiku)">' + r + '×</div>';
  }

  // Turn a wire model id into the human name the VS Code panel shows:
  //   claude-opus-5[1m]           -> "Opus 5 with 1M context"
  //   claude-haiku-4-5-20251001   -> "Haiku 4.5"
  //   opus                        -> "Opus"
  // Version parts join with a dot, an 8-digit dated snapshot is dropped, and "[1m]" becomes the
  // spelled-out context note. This is what keeps the picker honest: the host ships a hardcoded
  // `label` as a first-run fallback, but once the CLI reports what an alias actually resolved to,
  // that wins — so a new model release renames the row by itself.
  function prettyModel(id) {
    if (!id) return "";
    let s = String(id).trim();
    const is1m = /\[1m\]/i.test(s);
    s = s.replace(/\[1m\]/ig, "").replace(/^claude-/i, "");
    const parts = s.split("-").filter((x) => x.length && !/^\d{8}$/.test(x));
    if (!parts.length) return "";
    const family = parts[0].charAt(0).toUpperCase() + parts[0].slice(1);
    const ver = parts.slice(1).filter((x) => /^\d+$/.test(x)).join(".");
    return family + (ver ? " " + ver : "") + (is1m ? " with 1M context" : "");
  }

  function renderModel() {
    let h = '<h3>Select a model <button class="close-x">×</button></h3>';
    models.forEach((m) => {
      // Row reads "<model> · <what it is for>", matching the VS Code panel. The model half is the
      // CLI-resolved id when we know it (the selected row, after a session has started), else the
      // host's fallback label.
      const label = (m.id === cur.model && ctx.model ? prettyModel(ctx.model) : "") || m.label || "";
      const line = label ? (m.desc ? label + " · " + m.desc : label) : (m.desc || "");
      h += '<div class="opt' + (m.id === cur.model ? " sel" : "") + '" data-id="' + m.id + '"><div class="obody"><div class="oname">' + window.md.esc(m.name) + '</div><div class="odesc">' + window.md.esc(line) + '</div></div>' + ratioBadge(m.ratio) + (m.id === cur.model ? '<div class="ochk">✓</div>' : "") + "</div>";
    });
    const isCustom = !!cur.model && !models.some((m) => m.id === cur.model);
    // A selected custom id reads the same way as the built-in rows: friendly name, then the id.
    const customLine = isCustom
      ? ((prettyModel(cur.model) ? prettyModel(cur.model) + " · " : "") + cur.model)
      : "Any model id or alias, e.g. claude-fable-5";
    h += '<div class="opt' + (isCustom ? " sel" : "") + '" data-id="__custom"><div class="obody"><div class="oname">Custom model…</div><div class="odesc">' + window.md.esc(customLine) + '</div></div>' + (isCustom ? '<div class="ochk">✓</div>' : "") + "</div>";
    const ei = Math.max(0, efforts.findIndex((x) => x.id === cur.effort));
    const curName = (efforts[ei] || {}).name || "Off";
    h += '<div class="effort-row"><div class="elabel">' + DUMBBELL + ' Effort <small id="effdesc">(' + window.md.esc(curName + " — " + effortDesc(cur.effort)) + ')</small></div><input type="range" class="effort-slider" id="effslider" min="0" max="' + (efforts.length - 1) + '" value="' + ei + '" /></div>';
    showTop(h);
    els.popover.querySelectorAll(".opt").forEach((o) => o.addEventListener("click", () => {
      if (o.dataset.id === "__custom") { renderCustomModel(); return; }
      if (o.dataset.id !== cur.model) { cur.model = o.dataset.id; post("setModel", { model: cur.model }); applyEffortsForModel(); showModelDivider(cur.model); onModelSwitched(); }
      closeTop();
    }));
    const sl = els.popover.querySelector("#effslider");
    if (sl) sl.addEventListener("input", () => {
      const e = efforts[+sl.value] || efforts[0];
      cur.effort = e.id; post("setEffort", { effort: cur.effort });
      const dd = els.popover.querySelector("#effdesc");
      if (dd) dd.textContent = "(" + e.name + " — " + effortDesc(e.id) + ")";
    });
  }

  // Mirrors InputValidation.ModelIdShape on the host (which re-validates); this copy is UX only.
  const MODEL_ID_RE = /^[A-Za-z0-9][A-Za-z0-9.\[\]-]{0,63}$/;
  function renderCustomModel() {
    let h = '<h3>Custom model <button class="close-x">×</button></h3>';
    h += '<input type="text" class="palette-input" id="customModel" placeholder="Model id or alias — Enter to apply, Esc to go back" spellcheck="false" />';
    h += '<div class="note err" id="customModelErr"></div>';
    h += '<div class="note">Type any model id or alias, or pick a known one:</div>';
    h += '<div id="customSuggest">';
    MODEL_SUGGESTIONS.forEach((s) => {
      // Same shape as the main picker: bold model name, then "<what it is for> · <wire id>".
      // The id stays visible here because this screen is about picking a specific id.
      const nm = prettyModel(s.id) || s.id;
      const line = s.desc ? s.desc + " · " + s.id : s.id;
      h += '<div class="opt" data-id="' + window.md.esc(s.id) + '"><div class="obody"><div class="oname">' + window.md.esc(nm) + '</div><div class="odesc">' + window.md.esc(line) + '</div></div></div>';
    });
    h += "</div>";
    showTop(h);
    const inp = els.popover.querySelector("#customModel");
    inp.value = models.some((m) => m.id === cur.model) ? "" : cur.model;
    inp.focus();
    // Shared apply path for both Enter and a suggestion click. Returns false (and shows the
    // error) when the id is malformed so the caller can keep the picker open.
    const apply = (raw) => {
      let v = (raw || "").trim();
      if (!MODEL_ID_RE.test(v)) {
        els.popover.querySelector("#customModelErr").textContent = "⚠ Letters, digits, dots, dashes and [] only — no spaces, max 64 chars.";
        return false;
      }
      // A typed wire name of a built-in entry normalizes to its picker id so per-model
      // gating (effort range, Auto-mode visibility) applies the same either way. An exact
      // picker id is already normal — check that first, else typing "opus[1m]" would match
      // MODEL_WIRE.default and land on Default rather than the explicit Opus row.
      if (!models.some((m) => m.id === v)) {
        for (const k in MODEL_WIRE) if (MODEL_WIRE[k] === v) { v = k; break; }
      }
      if (v !== cur.model) { cur.model = v; post("setModel", { model: v }); applyEffortsForModel(); showModelDivider(v); onModelSwitched(); }
      closeTop();
      return true;
    };
    inp.addEventListener("keydown", (e) => {
      if (e.key === "Escape") { e.stopPropagation(); renderModel(); return; }
      if (e.key === "Enter") apply(inp.value);
    });
    els.popover.querySelectorAll("#customSuggest .opt").forEach((o) =>
      o.addEventListener("click", () => apply(o.dataset.id)));
  }
  let usagePeriod = "day"; // Day/Week toggle in the "what's contributing" section
  function renderUsage() {
    let h = '<h3>Account &amp; Usage <button class="close-x">×</button></h3>';

    if (!acct) {
      h += '<div class="note" style="padding:6px 0">Loading account info…</div>';
    } else if (acct.error && !acct.email) {
      h += '<div class="note" style="padding:6px 0;color:var(--red)">⚠ ' + window.md.esc(acct.error) + '</div>';
    } else {
      h += '<div class="sec">Account</div>';
      h += kv("Auth method", acct.authMethod || "—");
      h += kv("Email", acct.email || "—");
      h += kv("Organization", acct.organization || "—");
      h += kv("Plan", acct.plan || "—");

      if (acct.limits && acct.limits.length > 0) {
        h += '<div class="sec">Usage</div>';
        acct.limits.forEach(function(l) {
          const pct = Math.min(100, Math.max(0, +(l.percent || 0)));
          const sev = l.severity === "warning" ? " warn"
            : (l.severity === "critical" || l.severity === "exceeded") ? " crit" : "";
          h += '<div class="uitem">';
          h += '<div class="utitle"><span>' + window.md.esc(l.name || "") + '</span><span>' + Math.round(pct) + "%</span></div>";
          h += '<div class="ubar"><div class="ufill' + sev + '" style="width:' + pct + '%"></div></div>';
          if (l.resetsIn) h += '<div class="ureset">Resets in ' + window.md.esc(l.resetsIn) + '</div>';
          h += '</div>';
        });
      }

      const xu = acct.extraUsage;
      if (xu && (xu.enabled || xu.usedCredits > 0)) {
        const dp = xu.decimalPlaces == null ? 2 : xu.decimalPlaces;
        const money = function(minor) {
          const v = (minor || 0) / Math.pow(10, dp);
          return (xu.currency === "USD" || !xu.currency ? "$" : xu.currency + " ") + v.toFixed(dp);
        };
        const upct = Math.min(100, Math.max(0, +(xu.utilization || 0)));
        h += '<div class="sec">Extra usage</div>';
        h += '<div class="uitem">';
        h += '<div class="utitle"><span>Credits this month</span><span>' + Math.round(upct) + "%</span></div>";
        h += '<div class="ubar"><div class="ufill" style="width:' + upct + '%"></div></div>';
        h += '<div class="ureset">' + window.md.esc(money(xu.usedCredits) + " of " + money(xu.monthlyLimit)) +
             (xu.enabled ? "" : " · off") + '</div>';
        h += '</div>';
      }

      h += '<div style="margin-top:12px"><a class="ulink" href="#" data-url="' + window.md.esc(acct.manageUrl || "https://claude.ai") + '">Manage usage on claude.ai</a></div>';

      const ins = acct.insights || {};
      const p = ins[usagePeriod] || ins.day || ins.week;
      if (p) {
        h += '<div class="sec" style="margin-top:12px">What’s contributing to your limits usage?</div>';
        h += '<div class="seg">' +
             '<button class="seg-btn' + (usagePeriod === "day" ? " on" : "") + '" data-period="day">Day</button>' +
             '<button class="seg-btn' + (usagePeriod === "week" ? " on" : "") + '" data-period="week">Week</button></div>';
        h += '<div class="note" style="margin:4px 0 6px">Approximate, based on local sessions on this machine — does not include other devices or claude.ai</div>';
        h += kv("Sessions", p.sessions);
        h += kv("Total tokens", (p.totalTokens || 0).toLocaleString());
        if (p.pctOver150k > 0) {
          h += '<div class="insight"><b>' + p.pctOver150k + '% of your usage was at &gt;150k context</b>' +
               '<div class="note">Longer sessions are more expensive even when cached. /compact mid-task, /clear when switching to new tasks.</div></div>';
        }
        if (p.pctSidechain > 0) {
          h += '<div class="insight"><b>' + p.pctSidechain + '% of your usage came from subagents</b>' +
               '<div class="note">Skills, subagents, and workflows spend tokens in the background on top of the visible chat.</div></div>';
        }
      }
    }

    h += '<div class="sec" style="margin-top:10px">Session tokens</div>';
    h += kv("Total cost", "$" + totals.costUsd.toFixed(4));
    h += kv("Turns", totals.turns);
    h += kv("Input (fresh)", totals.inputTokens.toLocaleString());
    h += kv("Cache write", totals.cacheCreationTokens.toLocaleString());
    h += kv("Cache read", totals.cacheReadTokens.toLocaleString());
    h += kv("Output", totals.outputTokens.toLocaleString());
    const totalIn = totals.inputTokens + totals.cacheCreationTokens + totals.cacheReadTokens;
    const hit = totalIn > 0 ? Math.round((totals.cacheReadTokens / totalIn) * 100) : 0;
    h += kv("Cache hit", hit + "% (cache read is ~90% cheaper)");
    h += kv("Total tokens", (totalIn + totals.outputTokens).toLocaleString());

    showTop(h);

    const link = els.popover.querySelector(".ulink");
    if (link) link.addEventListener("click", function(e) { e.preventDefault(); post("openExternal", { url: link.dataset.url }); });
    els.popover.querySelectorAll(".seg-btn").forEach(function(b) {
      b.addEventListener("click", function() {
        if (usagePeriod !== b.dataset.period) { usagePeriod = b.dataset.period; renderUsage(); }
      });
    });
  }
  let lastIde = null;
  function mergeContextIde(p) { lastIde = p; if (topOpen === "context") renderContext(); }
  // /mcp screen: configured MCP servers with live health (from `claude mcp list`),
  // grouped by scope (Project / User / claude.ai), with refresh + per-server hints/actions.
  let lastMcp = null, lastMcpError = null;
  const MCP_GROUP_ORDER = ["Project", "User", "claude.ai"];
  function renderMcp() {
    let h = '<h3>MCP servers <button class="close-x">×</button><button class="mcp-refresh" title="Re-check">↻</button></h3>';
    if (lastMcp == null) {
      h += '<div class="note" style="padding:8px 0">Checking MCP server health…</div>';
    } else {
      if (lastMcpError) h += '<div class="note mcp-err">Could not run <span class="cmd-name">claude mcp list</span>: ' + window.md.esc(lastMcpError) + '</div>';
      if (!lastMcp.length && !lastMcpError) {
        h += '<div class="note" style="padding:8px 0">No MCP servers configured.</div>';
        h += '<div class="note">Add servers in a <span class="cmd-name">.mcp.json</span> at the project root (or run <span class="cmd-name">claude mcp add</span>), then re-check.</div>';
      } else if (lastMcp.length) {
        const ok = lastMcp.filter((s) => s.ok).length;
        h += '<div class="sec">' + lastMcp.length + ' server' + (lastMcp.length === 1 ? '' : 's') + ' · ' + ok + ' connected</div>';
        const groups = {};
        lastMcp.forEach((s) => { const g = MCP_GROUP_ORDER.indexOf(s.scope) >= 0 ? s.scope : "User"; (groups[g] = groups[g] || []).push(s); });
        MCP_GROUP_ORDER.forEach((g) => {
          const list = groups[g]; if (!list || !list.length) return;
          h += '<div class="mcp-group">' + window.md.esc(g) + ' (' + list.length + ')</div>';
          list.forEach((s) => {
            const cls = s.ok ? "ok" : (/auth/i.test(s.status || "") ? "warn" : "bad");
            h += '<div class="mcp-item"><span class="mcp-dot ' + cls + '"></span>'
              + '<div class="mcp-body"><div class="mcp-name">' + window.md.esc(s.name) + '</div>'
              + '<div class="mcp-detail">' + window.md.esc(s.detail || "") + '</div>';
            if (s.missingEnv) h += '<div class="mcp-hint">⚠ env var <span class="cmd-name">' + window.md.esc(s.missingEnv) + '</span> not set — set it, then restart VS</div>';
            else if (s.envMaybeInvalid) h += '<div class="mcp-hint">⚠ token <span class="cmd-name">' + window.md.esc(s.envMaybeInvalid) + '</span> present but rejected — likely expired/invalid; update it, then restart VS</div>';
            h += '</div>';
            if (cls === "warn") h += '<button class="mcp-status warn mcp-authbtn" data-act="auth" title="Click to authenticate — opens a terminal, then run /mcp">' + window.md.esc(s.status || "") + '</button>';
            else h += '<div class="mcp-status ' + cls + '">' + window.md.esc(s.status || "") + '</div>';
            h += '</div>';
          });
        });
      }
    }
    h += '<div class="note mcp-foot"><a class="ulink" href="#" data-url="https://modelcontextprotocol.io/">Learn more about MCP</a></div>';
    showTop(h);
    const rb = els.popover.querySelector(".mcp-refresh");
    if (rb) rb.addEventListener("click", () => { lastMcp = null; lastMcpError = null; post("getMcp"); renderMcp(); });
    els.popover.querySelectorAll('[data-act="auth"]').forEach((b) => b.addEventListener("click", () => { post("mcpAuth"); b.textContent = "opening terminal…"; b.disabled = true; }));
    const lm = els.popover.querySelector(".mcp-foot .ulink");
    if (lm) lm.addEventListener("click", (e) => { e.preventDefault(); post("openExternal", { url: lm.dataset.url }); });
  }
  function renderContext() {
    // Before the first turn the CLI reports no usage, so fall back to the *selected* model
    // (resolved to its wire id) and its expected window instead of a bare "default"/200k.
    const shownModel = ctx.model || own(MODEL_WIRE, cur.model) || cur.model;
    const used = ctx.used, win = ctxWindow(), pct = Math.round((used / win) * 100);
    const sys = ctx.system || 0, msgs = Math.max(0, used - sys), free = Math.max(0, win - used);
    const seg = (v, c) => '<span style="width:' + (win ? (v / win * 100) : 0) + '%;background:' + c + '"></span>';
    let h = '<h3>Context usage <button class="close-x">×</button></h3>';
    h += '<div class="ctxhead">' + window.md.esc(shownModel) + '</div>';
    h += '<div class="ctxsub">' + fmt(used) + " / " + fmt(win) + " tokens (" + pct + "%)</div>";
    h += '<div class="ctxbar">' + seg(sys, "#cc7a3b") + seg(msgs, "#b07cff") + seg(free, "transparent") + "</div>";
    h += '<div class="ctxsub">' + Math.max(0, 100 - pct) + '% remaining until auto-compact — click the ◌ ring to compact now.</div>';
    h += '<div class="brk">';
    h += row("#cc7a3b", "System & tools (cached prefix)", sys, win);
    h += row("#b07cff", "Messages", msgs, win);
    h += row("transparent", "Free space", free, win);
    h += "</div>";
    h += '<div class="note" style="margin-top:6px">Per-category split (system prompt vs tools vs skills) isn\'t exposed by the headless CLI; values are derived from real token usage.</div>';
    if (lastIde) {
      h += '<div class="sec">IDE context</div>';
      h += kv("Working dir", lastIde.cwd || "—");
      h += kv("Active file", lastIde.activeFile || "—");
      if (lastIde.hasSelection) h += kv("Selection", "lines " + lastIde.selStart + "–" + lastIde.selEnd);

      // Live debugger / runtime state (only while a debug session is active).
      if (lastIde.dbgActive) {
        h += '<div class="sec">Runtime (debugger)</div>';
        h += kv("State", lastIde.dbgMode || "—");
        if (lastIde.dbgProcess) h += kv("Process", lastIde.dbgProcess);
        if (lastIde.dbgFunction) h += kv("Stopped in", lastIde.dbgFunction + (lastIde.dbgLine ? " :" + lastIde.dbgLine : ""));
        if (lastIde.dbgException) h += kv("Exception", lastIde.dbgException);
        if (lastIde.dbgLocals) h += kv("Locals captured", lastIde.dbgLocals);
      }

      // Project memory (CLAUDE.md the CLI auto-loads)
      h += '<div class="sec">Project memory (CLAUDE.md)</div>';
      h += kv("Project", lastIde.claudeMdProject || "none");
      h += kv("User", lastIde.claudeMdUser || "none");

      // MCP servers. The CLI only reports its list on the session's first turn, so before a
      // message has been sent there is nothing to show — and hiding the section outright read
      // as "this build has no MCP support". Fall back to the configured servers (the same
      // `claude mcp list` the /mcp screen uses) until the session reports its own.
      const mcp = lastIde.mcpServers || [];
      if (mcp.length) {
        h += '<div class="sec">MCP servers (' + mcp.length + ")</div>";
        h += '<ul class="files">' + mcp.map((x) => "<li>" + window.md.esc(x) + "</li>").join("") + "</ul>";
      } else if (lastMcp == null) {
        h += '<div class="sec">MCP servers</div>';
        h += '<div style="color:var(--fg-dim)">checking…</div>';
      } else if (lastMcp.length) {
        h += '<div class="sec">MCP servers (' + lastMcp.length + ")</div>";
        h += '<ul class="files">' + lastMcp.map((s) =>
          "<li>" + window.md.esc(s.name || "") +
          (s.status ? ' <span style="color:var(--fg-dim)">(' + window.md.esc(s.status) + ")</span>" : "") +
          "</li>").join("") + "</ul>";
        h += '<div class="note">From your MCP config — the session reports its own list after the first message.</div>';
      } else {
        h += '<div class="sec">MCP servers (0)</div>';
        h += '<div style="color:var(--fg-dim)">none configured</div>';
      }
      const tl = lastIde.tools || [];
      if (tl.length) h += kv("Tools available", tl.length);

      const f = lastIde.openFiles || [];
      h += '<div class="sec">Open files (' + f.length + ")</div>";
      h += f.length ? '<ul class="files">' + f.map((x) => "<li>" + window.md.esc(x) + "</li>").join("") + "</ul>" : '<div style="color:var(--fg-dim)">none</div>';
    }
    showTop(h);
  }
  function row(c, name, tk, win) { const pc = win ? (tk / win * 100) : 0; return '<div class="sw" style="background:' + c + (c === "transparent" ? ";border:1px solid var(--border)" : "") + '"></div><div class="nm">' + window.md.esc(name) + '</div><div class="tk">' + fmt(tk) + '</div><div class="pc">' + (pc < 0.1 && pc > 0 ? "<0.1" : pc.toFixed(1)) + "%</div>"; }
  function kv(k, v) { return '<div class="kv"><span class="k">' + window.md.esc(k) + '</span><span class="v">' + window.md.esc(v == null || v === "" ? "—" : String(v)) + "</span></div>"; }

  // ---- composer popovers ----
  function activeC() { [["plus", els.plusBtn], ["slash", els.slashBtn], ["mode", els.modeBtn]].forEach(([k, b]) => b.classList.toggle("active", cOpen === k)); }
  function closeC() { cOpen = null; els.cpop.classList.add("hidden"); els.cpop.innerHTML = ""; activeC(); }
  function toggleC(w) { if (cOpen === w) closeC(); else openC(w); }
  function openC(which) { closeTop(); cOpen = which; activeC(); if (which === "plus") renderPlus(); else if (which === "slash") openSlash(); else if (which === "mode") renderMode(); }
  function showC(html) { els.cpop.innerHTML = html; els.cpop.classList.remove("hidden"); }

  function renderPlus() {
    let h = "";
    h += '<button class="menu-item" data-a="image"><span class="mi-icon">⬆</span><span>Upload image from computer</span></button>';
    h += '<button class="menu-item" data-a="file"><span class="mi-icon">📄</span><span>Add file to context</span></button>';
    h += '<button class="menu-item" data-a="web"><span class="mi-icon">🌐</span><span>Add a web URL</span></button>';
    showC(h);
    els.cpop.querySelectorAll(".menu-item").forEach((b) => b.addEventListener("click", () => {
      const a = b.dataset.a; closeC();
      if (a === "image") post("pickImage");
      else if (a === "file") post("pickFile");
      else if (a === "web") { const u = prompt("Web URL to include:"); if (u) handlers.insertText({ text: u }); }
    }));
  }

  // Models where bypassPermissions (Auto mode) is hidden.
  const NO_AUTO_MODE_MODELS = new Set(["haiku"]);
  function visibleModes() {
    return NO_AUTO_MODE_MODELS.has(cur.model)
      ? modes.filter((m) => m.id !== "bypassPermissions")
      : modes;
  }
  function renderMode() {
    let h = '<div class="sec">Permission mode</div>';
    visibleModes().forEach((m) => {
      h += '<div class="opt' + (m.id === cur.mode ? " sel" : "") + '" data-id="' + m.id + '"><div class="oicon">' + (m.icon || "") + '</div><div class="obody"><div class="oname">' + window.md.esc(m.name) + '</div><div class="odesc">' + window.md.esc(m.desc || "") + '</div></div>' + (m.id === cur.mode ? '<div class="ochk">✓</div>' : "") + "</div>";
    });
    h += '<div class="effort-row"><div class="elabel">Show thinking <small>(stream reasoning)</small></div><button class="mini-toggle' + (thinkingVisible ? " on" : "") + '" id="thinkToggle">' + (thinkingVisible ? "On" : "Off") + '</button></div>';
    showC(h);
    els.cpop.querySelectorAll(".opt").forEach((o) => o.addEventListener("click", () => { cur.mode = o.dataset.id; post("setPermissionMode", { mode: cur.mode }); updateModeLabel(); closeC(); }));
    const tt = els.cpop.querySelector("#thinkToggle");
    if (tt) tt.addEventListener("click", () => { thinkingVisible = !thinkingVisible; post("setShowThinking", { on: thinkingVisible }); applyThinkingVisibility(); renderMode(); });
  }

  // ---- @-mention file picker ----
  function openAt(query) {
    closeTop(); cOpen = "at"; activeC();
    atQuery = query || "";
    filterAt();
  }
  function filterAt() {
    const q = (atQuery || "").toLowerCase();
    atItems = fileList.filter((f) => f.toLowerCase().includes(q)).slice(0, 50);
    atIndex = 0; renderAt();
  }
  function renderAt() {
    if (cOpen !== "at") return;
    showC('<div class="sec" style="padding:0 4px">Reference a file</div><div id="atlist"></div>');
    const list = els.cpop.querySelector("#atlist");
    if (!atItems.length) { list.innerHTML = '<div class="note" style="padding:8px">' + (fileList.length ? "No matching files" : "Loading files…") + '</div>'; return; }
    let h = "";
    atItems.forEach((f, i) => { h += '<button class="menu-item' + (i === atIndex ? " sel" : "") + '" data-i="' + i + '"><span class="mi-icon">@</span><span class="cmd-name">' + window.md.esc(f) + "</span></button>"; });
    list.innerHTML = h;
    list.querySelectorAll(".menu-item").forEach((b) => b.addEventListener("click", () => pickAt(atItems[+b.dataset.i])));
  }
  function atKey(e) {
    if (e.key === "ArrowDown") { atIndex = Math.min(atIndex + 1, atItems.length - 1); renderAt(); e.preventDefault(); return true; }
    if (e.key === "ArrowUp") { atIndex = Math.max(atIndex - 1, 0); renderAt(); e.preventDefault(); return true; }
    if (e.key === "Enter") { if (atItems[atIndex]) { pickAt(atItems[atIndex]); e.preventDefault(); return true; } }
    if (e.key === "Escape") { closeC(); return true; }
    return false;
  }
  function pickAt(f) {
    if (!f) return;
    els.input.value = els.input.value.replace(/(^|\s)@(\S*)$/, function (_, pre) { return pre + "@" + f + " "; });
    closeC(); els.input.focus(); autoGrow();
  }

  // ---- slash palette ----
  let palIndex = 0, palItems = [];
  function extCmds() {
    return [
      { name: "context", desc: "Show context window usage", run: () => openTop("context") },
      { name: "usage", desc: "Show session usage & cost", run: () => openTop("usage") },
      { name: "model", desc: "Select model", run: () => openTop("model") },
      { name: "mcp", desc: "MCP servers", run: () => openTop("mcp") },
      { name: "compact", desc: "Compact the conversation", run: () => { post("compact"); showThinking("Compacting"); } },
      { name: "clear", desc: "Clear the chat", run: () => handlers.clear() },
      { name: "new", desc: "Start a new session", run: () => { resetTotals(); post("newSession"); } },
    ];
  }
  function openSlash() {
    closeTop(); cOpen = "slash"; activeC();
    if (!slashCommands.length) { commandsLoading = true; post("getCommands"); } // lazy fallback if eager fetch hasn't landed
    const q0 = els.input.value.startsWith("/") ? els.input.value.slice(1) : "";
    showC('<input class="palette-input" id="palq" placeholder="Search commands…" value="' + window.md.esc(q0) + '" /><div id="pallist"></div>');
    const q = els.cpop.querySelector("#palq");
    q.addEventListener("input", () => filterPalette(q.value));
    q.addEventListener("keydown", (e) => paletteKey(e));
    q.focus();
    filterPalette(q0);
  }
  function filterPalette(q) {
    q = (q || "").toLowerCase().replace(/^\//, "");
    const ext = extCmds().map((c) => ({ ...c, kind: "ext" }));
    const extNames = new Set(ext.map((c) => c.name));
    // Drop MCP prompt commands (mcp__server__name) — noise in the palette.
    const cli = slashCommands
      .filter((c) => !extNames.has(c) && c.indexOf("mcp__") !== 0)
      .map((c) => ({ name: c, desc: "Insert /" + c, kind: "cli" }));
    palItems = ext.concat(cli)
      .filter((c) => c.name.toLowerCase().includes(q))
      .sort((a, b) => a.name.localeCompare(b.name));
    palIndex = 0; renderPalette();
  }
  function renderPalette() {
    const list = els.cpop.querySelector("#pallist"); if (!list) return;
    // Cold-cache: the full CLI set is still being fetched (CLI startup + SessionStart hooks).
    const loadingNote = (commandsLoading && !slashCommands.length)
      ? '<div class="note pal-loading" style="padding:8px">Loading commands…</div>' : "";
    if (!palItems.length) { list.innerHTML = loadingNote || '<div class="note" style="padding:8px">No matching commands</div>'; return; }
    let h = "";
    palItems.forEach((c, i) => {
      h += '<button class="menu-item' + (i === palIndex ? " sel" : "") + '" data-i="' + i + '"><span class="mi-icon">/</span><span><span class="cmd-name">' + window.md.esc(c.name) + '</span> <span class="mi-desc">' + window.md.esc(c.desc || "") + "</span></span></button>";
    });
    list.innerHTML = h + loadingNote;
    list.querySelectorAll(".menu-item").forEach((b) => b.addEventListener("click", () => runPalette(palItems[+b.dataset.i])));
  }
  function paletteKey(e) {
    if (e.key === "ArrowDown") { palIndex = Math.min(palIndex + 1, palItems.length - 1); renderPalette(); e.preventDefault(); return true; }
    if (e.key === "ArrowUp") { palIndex = Math.max(palIndex - 1, 0); renderPalette(); e.preventDefault(); return true; }
    if (e.key === "Enter") { if (palItems[palIndex]) { runPalette(palItems[palIndex]); e.preventDefault(); return true; } }
    if (e.key === "Escape") { closeC(); return true; }
    return false;
  }
  function runPalette(c) {
    if (!c) return;
    if (els.input.value.startsWith("/")) els.input.value = "";
    closeC(); els.input.focus(); autoGrow();
    if (c.kind === "ext" && c.run) c.run();
    else { els.input.value = "/" + c.name + " "; autoGrow(); }
  }

  // ---- first-run onboarding banner ----
  // The "updated to X" row is a confirmation, not a task, so it retires itself. The countdown only
  // runs while the page is actually on screen: the update completes asynchronously, and burning the
  // timer behind another tool-window tab would put us right back to "never saw it finish".
  const UPDATED_BANNER_MS = 30000;
  let updatedTimer = null;
  let lastSetup = null;

  // Login happens in a terminal *outside* the IDE, so nothing reports back when it finishes.
  // Without this the "not signed in" banner just sat there after a successful login and the
  // only way out was the manual re-check button. Poll while the banner is up instead.
  // Bounded: someone who has no intention of logging in shouldn't be polled at forever.
  const LOGIN_POLL_MS = 3000;
  const LOGIN_POLL_WINDOW_MS = 10 * 60 * 1000;
  let loginPollTimer = null, loginPollUntil = 0;
  function stopLoginPoll() {
    if (loginPollTimer) { clearInterval(loginPollTimer); loginPollTimer = null; }
    loginPollUntil = 0;
  }
  function startLoginPoll(extend) {
    // Clicking "Open terminal to log in" restarts the clock — the user is acting right now.
    if (extend || !loginPollUntil) loginPollUntil = Date.now() + LOGIN_POLL_WINDOW_MS;
    if (loginPollTimer) return;
    loginPollTimer = setInterval(function () {
      if (Date.now() > loginPollUntil) { stopLoginPoll(); return; }
      if (document.visibilityState === "hidden") return;   // behind another tab: nothing to update
      post("recheckSetup");
    }, LOGIN_POLL_MS);
  }
  function clearUpdatedTimer() { if (updatedTimer) { clearTimeout(updatedTimer); updatedTimer = null; } }
  function scheduleUpdatedDismiss() {
    clearUpdatedTimer();
    if (document.visibilityState === "hidden") return;   // resumes on the visibilitychange below
    updatedTimer = setTimeout(function () {
      updatedTimer = null;
      updatedTo = null;
      renderSetupBanner(lastSetup || {});   // re-render, so a newly-available update still wins
    }, UPDATED_BANNER_MS);
  }

  function renderSetupBanner(p) {
    const el = els.setupBanner; if (!el) return;
    p = p || {};
    lastSetup = p;
    clearUpdatedTimer();   // every path below re-decides whether a countdown is warranted

    // Track the reported CLI version across status messages so an update that lands can be
    // reported as *finished*. The updater runs detached in its own terminal, so without this the
    // banner either sat there unchanged or silently vanished — neither of which tells the user
    // the update worked.
    const prevVersion = lastCliVersion;
    if (p.cliVersion) lastCliVersion = p.cliVersion;
    if (updateInFlight && prevVersion && p.cliVersion && p.cliVersion !== prevVersion) {
      updateInFlight = false;
      updatedTo = p.cliVersion;
      dismissedVersion = null;   // this is news, even if the update prompt was dismissed earlier
    }
    const wasRecheck = recheckPending; recheckPending = false;

    // The host's hourly re-check is the reminder: it undoes an earlier dismiss, so an update
    // the user waved away is put back in front of them roughly an hour later.
    if (p.periodic) dismissedVersion = null;

    // Set before the branch chain so the all-clear path (which returns early) stops it too.
    if (p.cliFound && !p.loggedIn) startLoginPoll(false); else stopLoginPoll();

    let html = "";
    if (!p.cliFound) {
      const installBtn = p.npmFound
        ? '<button class="sb-btn primary" data-act="install">Install CLI</button>'
        : '<button class="sb-btn" data-act="node">Get Node.js</button>';
      const hint = p.npmFound
        ? 'Install it (runs in a terminal), then Re-check — a VS restart may be needed for PATH:'
        : 'Node.js / npm not found. Install Node.js first, then re-check:';
      html = '<div class="sb-row"><span class="sb-ico">⚠</span><div class="sb-text"><b>Claude CLI not found.</b> This extension drives your locally-installed Claude Code CLI — it does not bundle one. ' + hint + '<br><code>npm install -g @anthropic-ai/claude-code</code></div></div>'
           + '<div class="sb-actions">' + installBtn + '<button class="sb-btn" data-act="docs">Install guide</button><button class="sb-btn" data-act="recheck">Re-check</button></div>';
    } else if (!p.loggedIn) {
      html = '<div class="sb-row"><span class="sb-ico">🔑</span><div class="sb-text"><b>Not signed in to Claude.</b> Login is handled by the CLI. Open a terminal and run <code>/login</code>. Claude Code needs a paid <b>Pro/Max</b> plan or API credits — a free account can\'t run it.</div></div>'
           + '<div class="sb-actions"><button class="sb-btn primary" data-act="login">Open terminal to log in</button><button class="sb-btn" data-act="recheck">I\'ve logged in — re-check</button></div>';
    } else if (p.cliOutdated && p.latestCliVersion !== dismissedVersion) {
      // A re-check that finds nothing new must still show it ran, otherwise an identical banner
      // re-render reads as "it didn't even look".
      const note = updateError
        ? '<br><b>The update failed.</b> <code>' + window.md.esc(updateError) + '</code><br>Run it in a terminal to see the full output — some failures (a login prompt, for instance) need an interactive session.'
        : wasRecheck
          ? '<br><b>Re-checked just now — still on <code>' + window.md.esc(p.cliVersion || "?") + '</code>.</b> The update may not have replaced the binary yet; a running Claude session can hold it open.'
          : (updateInFlight ? '<br>Updating in the background — this banner reports the result when it finishes.' : '');
      html = '<div class="sb-row"><span class="sb-ico">' + (updateError ? '⚠' : '⬆') + '</span><div class="sb-text"><b>Claude CLI update available.</b> Installed <code>' + window.md.esc(p.cliVersion || "?") + '</code>, latest <code>' + window.md.esc(p.latestCliVersion || "?") + '</code>. Newer models and fixes ship in CLI updates — an older CLI may not offer the latest models (e.g. the picker can list a model your CLI cannot run yet).' + note + '</div></div>'
           + '<div class="sb-actions">' + (updateInFlight
                ? '<button class="sb-btn primary" data-act="update" disabled>updating…</button>'
                : '<button class="sb-btn primary" data-act="update">' + (updateError ? 'Try again' : 'Update CLI') + '</button>')
           + (updateError ? '<button class="sb-btn" data-act="terminal">Run in terminal</button>' : '')
           + '<button class="sb-btn" data-act="recheck">Re-check</button><button class="sb-btn" data-act="dismiss">Dismiss</button></div>';
    } else if (updatedTo) {
      html = '<div class="sb-row"><span class="sb-ico">✓</span><div class="sb-text"><b>Claude CLI updated to <code>' + window.md.esc(updatedTo) + '</code>.</b> The new version is picked up on your next message — no restart needed.</div></div>'
           + '<div class="sb-actions"><button class="sb-btn" data-act="dismissUpdated">Dismiss</button></div>';
      scheduleUpdatedDismiss();   // it has served its purpose once read; don't make it a chore
    } else if (updateError) {
      // The CLI no longer reports as outdated, but the update we ran did fail — say so rather
      // than letting a background failure vanish.
      html = '<div class="sb-row"><span class="sb-ico">⚠</span><div class="sb-text"><b>The Claude CLI update failed.</b> <code>' + window.md.esc(updateError) + '</code></div></div>'
           + '<div class="sb-actions"><button class="sb-btn primary" data-act="update">Try again</button><button class="sb-btn" data-act="terminal">Run in terminal</button><button class="sb-btn" data-act="dismissError">Dismiss</button></div>';
    } else {
      el.classList.add("hidden"); el.innerHTML = ""; return;
    }
    el.innerHTML = html;
    el.classList.remove("hidden");
    el.querySelectorAll("[data-act]").forEach((b) => b.addEventListener("click", () => {
      const a = b.dataset.act;
      if (a === "login") { post("openClaudeTerminal"); startLoginPoll(true); }
      else if (a === "install") { post("installCli"); b.textContent = "installing… (see terminal)"; b.disabled = true; }
      else if (a === "update") { updateInFlight = true; updateError = null; post("updateCli"); b.textContent = "updating…"; b.disabled = true; }
      else if (a === "terminal") { updateInFlight = true; updateError = null; post("updateCliInTerminal"); renderSetupBanner(lastSetup || {}); }
      else if (a === "dismissError") { updateError = null; el.classList.add("hidden"); el.innerHTML = ""; }
      else if (a === "dismiss") { dismissedVersion = (lastSetup && lastSetup.latestCliVersion) || "*"; el.classList.add("hidden"); el.innerHTML = ""; }
      else if (a === "dismissUpdated") { clearUpdatedTimer(); updatedTo = null; el.classList.add("hidden"); el.innerHTML = ""; }
      else if (a === "recheck") { recheckPending = true; post("recheckSetup"); b.textContent = "checking…"; b.disabled = true; }
      else if (a === "node") post("openExternal", { url: "https://nodejs.org/en/download" });
      else if (a === "docs") post("openExternal", { url: "https://docs.claude.com/en/docs/claude-code/setup" });
    }));
  }

  function closeAll() { closeTop(); closeC(); }

  updateRing();
  applyThinkingVisibility();
  post("ready");
})();
