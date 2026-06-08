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
    popover: $("popover"), cpop: $("cpop"),
  };

  let running = false, currentAssistant = null;
  const toolCards = new Map();
  let attachments = [];
  let slashCommands = [];
  let models = [], modes = [], efforts = [];
  let cur = { model: "default", mode: "bypassPermissions", effort: "none" };
  const totals = { costUsd: 0, inputTokens: 0, outputTokens: 0, turns: 0 };
  const ctx = { used: 0, window: 200000, model: "", baseline: 0, system: 0 };
  let topOpen = null, cOpen = null;
  let acct = null;

  function post(type, payload) { if (api) api.postMessage({ type: type, payload: payload || {} }); }

  // ---- inbound ----
  function onHostMessage(ev) {
    let m = ev.data;
    if (typeof m === "string") { try { m = JSON.parse(m); } catch (e) { return; } }
    if (!m || !m.type) return;
    (handlers[m.type] || function () {})(m.payload || {});
  }
  if (api && api.addEventListener) api.addEventListener("message", onHostMessage);
  window.addEventListener("message", onHostMessage);

  function scrollDown() { els.messages.scrollTop = els.messages.scrollHeight; }
  function addMsg(role) {
    const w = document.createElement("div"); w.className = "msg " + role;
    const r = document.createElement("div"); r.className = "role"; r.textContent = role === "user" ? "You" : "Claude";
    const b = document.createElement("div"); b.className = "bubble";
    w.appendChild(r); w.appendChild(b); els.messages.appendChild(w); scrollDown(); return b;
  }
  function removeThinking() { const t = els.messages.querySelector(".thinking-row"); if (t) t.remove(); }
  function showThinking(label) {
    removeThinking();
    const row = document.createElement("div"); row.className = "msg assistant thinking-row";
    row.innerHTML = '<div class="thinking"><span>' + (label || "Working") + '</span><span class="dot"></span><span class="dot"></span><span class="dot"></span></div>';
    els.messages.appendChild(row); scrollDown();
  }
  function renderBlocks(b, blocks) {
    for (const x of blocks) {
      if (x.type === "text") { const d = document.createElement("div"); d.innerHTML = window.md.render(x.text || ""); b.appendChild(d); }
      else if (x.type === "tool_use") renderToolUse({ id: x.id, name: x.name, input: x.input });
    }
  }
  function summarize(name, input) {
    try { if (!input) return ""; return input.command || input.file_path || input.path || input.pattern || input.url || (input.prompt ? String(input.prompt).slice(0, 80) : JSON.stringify(input).slice(0, 80)); }
    catch (e) { return ""; }
  }
  function renderToolUse(t) {
    const c = document.createElement("div"); c.className = "tool";
    const h = document.createElement("div"); h.className = "tool-head";
    h.innerHTML = '<span class="tname">' + window.md.esc(t.name || "tool") + '</span><span class="tsummary">' + window.md.esc(summarize(t.name, t.input)) + '</span><span class="chev">▶</span>';
    const bd = document.createElement("div"); bd.className = "tool-body";
    bd.innerHTML = "<pre>" + window.md.esc(JSON.stringify(t.input || {}, null, 2)) + "</pre>";
    h.addEventListener("click", () => c.classList.toggle("open"));
    c.appendChild(h); c.appendChild(bd); els.messages.appendChild(c);
    if (t.id) toolCards.set(t.id, bd); scrollDown();
  }
  function renderToolResult(r) {
    const bd = r.id && toolCards.get(r.id);
    const text = typeof r.content === "string" ? r.content : JSON.stringify(r.content, null, 2);
    const pre = document.createElement("pre"); pre.style.marginTop = "8px";
    if (r.isError) pre.style.color = "var(--red)";
    pre.textContent = (text || "").slice(0, 4000);
    if (bd) { const l = document.createElement("div"); l.style.cssText = "color:var(--fg-dim);font-size:11px"; l.textContent = r.isError ? "result (error)" : "result"; bd.appendChild(l); bd.appendChild(pre); }
  }
  function renderPermission(p) {
    const c = document.createElement("div"); c.className = "perm"; c.dataset.id = p.id;
    c.innerHTML = '<div class="ptitle">Allow ' + window.md.esc(p.tool || "tool") + '?</div><div class="pbody"><pre>' + window.md.esc(JSON.stringify(p.input || {}, null, 2)) + '</pre></div><div class="pactions"><button class="allow" data-b="allow">Allow</button><button class="deny" data-b="deny">Deny</button></div>';
    c.querySelectorAll("button").forEach((btn) => btn.addEventListener("click", () => {
      post("permissionResponse", { id: p.id, behavior: btn.dataset.b });
      c.classList.add("resolved"); c.querySelector(".pactions").innerHTML = "<em>" + (btn.dataset.b === "deny" ? "Denied" : "Allowed") + "</em>";
    }));
    els.messages.appendChild(c); scrollDown();
  }

  const handlers = {
    init: (p) => {
      if (p.theme) applyTheme(p.theme);
      if (p.models) models = p.models;
      if (p.modes) modes = p.modes;
      if (p.efforts) efforts = p.efforts;
      if (p.model) cur.model = p.model;
      if (p.permissionMode) cur.mode = p.permissionMode;
      if (p.effort) cur.effort = p.effort;
      updateModeLabel();
    },
    commands: (p) => { slashCommands = p.commands || []; },
    context: (p) => mergeContextIde(p),
    theme: (p) => applyTheme(p),
    status: (p) => {
      running = p.state === "thinking" || p.state === "running";
      els.statusText.textContent = p.text || cap(p.state || "Ready");
      els.sendBtn.classList.toggle("hidden", running);
      els.stopBtn.classList.toggle("hidden", !running);
      if (!running) removeThinking();
    },
    assistantStart: () => { removeThinking(); currentAssistant = { el: addMsg("assistant"), buf: "" }; },
    assistantDelta: (p) => {
      if (!currentAssistant) currentAssistant = { el: addMsg("assistant"), buf: "" };
      currentAssistant.buf += p.text || ""; currentAssistant.el.innerHTML = window.md.render(currentAssistant.buf); scrollDown();
    },
    assistantEnd: () => { currentAssistant = null; },
    assistant: (p) => { removeThinking(); currentAssistant = null; renderBlocks(addMsg("assistant"), p.content || []); scrollDown(); },
    thinking: (p) => showThinking(p.label),
    toolUse: (p) => { removeThinking(); renderToolUse(p); },
    toolResult: (p) => renderToolResult(p),
    permission: (p) => { removeThinking(); renderPermission(p); },
    result: (p) => {
      const parts = [];
      if (p.costUsd != null) parts.push("$" + Number(p.costUsd).toFixed(4));
      if (p.inputTokens != null) parts.push(p.inputTokens + " in");
      if (p.outputTokens != null) parts.push(p.outputTokens + " out");
      if (p.durationMs != null) parts.push((p.durationMs / 1000).toFixed(1) + "s");
      els.usage.textContent = parts.join(" · ");
      totals.costUsd += +(p.costUsd || 0); totals.inputTokens += +(p.inputTokens || 0);
      totals.outputTokens += +(p.outputTokens || 0); totals.turns += 1;
      // context window usage
      ctx.used = (+(p.inputTokens || 0)) + (+(p.cacheReadTokens || 0)) + (+(p.cacheCreationTokens || 0));
      if (p.contextWindow) ctx.window = +p.contextWindow;
      if (p.model) ctx.model = p.model;
      if (!ctx.baseline && p.cacheCreationTokens) ctx.baseline = +p.cacheCreationTokens;
      ctx.system = Math.min(ctx.baseline || 0, ctx.used);
      updateRing();
      if (topOpen === "usage") renderUsage();
      if (topOpen === "context") renderContext();
    },
    error: (p) => { removeThinking(); const b = addMsg("assistant"); b.innerHTML = '<span style="color:var(--red)">⚠ ' + window.md.esc(p.message || "Error") + "</span>"; },
    system: (p) => { if (p.subtype === "init" && p.model) ctx.model = p.model; },
    clear: () => { els.messages.innerHTML = ""; els.usage.textContent = ""; currentAssistant = null; toolCards.clear(); },
    accountData: (p) => { acct = p; if (topOpen === "usage") renderUsage(); },
    compacted: (p) => { const n = document.createElement("div"); n.className = "note"; n.textContent = "✓ Context compacted into a summary."; els.messages.appendChild(n); ctx.used = 0; ctx.baseline = 0; updateRing(); },
    attachImage: (p) => { attachments.push({ mediaType: p.mediaType, data: p.data, name: p.name }); renderAttachments(); },
    insertText: (p) => { els.input.value += (els.input.value && !els.input.value.endsWith(" ") ? " " : "") + (p.text || ""); els.input.focus(); autoGrow(); },
  };

  function applyTheme(t) { const r = document.documentElement.style; Object.keys(t).forEach((k) => { if (k.startsWith("--")) r.setProperty(k, t[k]); }); }
  function cap(s) { return s ? s.charAt(0).toUpperCase() + s.slice(1) : s; }
  function fmt(n) { n = +n || 0; if (n >= 1e6) return (n / 1e6).toFixed(1) + "M"; if (n >= 1e3) return (n / 1e3).toFixed(1) + "k"; return String(n); }
  function modeName(id) { const m = modes.find((x) => x.id === id); return m ? m.name : id; }
  function updateModeLabel() { els.modeLabel.textContent = modeName(cur.mode).replace(/ mode$/i, "").replace("Edit automatically", "Auto-edit").replace("Ask before edits", "Ask"); }

  // ---- context ring ----
  function updateRing() {
    const C = 94.2; const frac = ctx.window ? Math.min(1, ctx.used / ctx.window) : 0;
    els.ringFg.style.strokeDashoffset = (C * (1 - frac)).toFixed(1);
    const remain = Math.round((1 - frac) * 100);
    els.ringBtn.title = remain + "% of context remaining until auto-compact. Click to compact now.";
  }

  // ---- composer ----
  function autoGrow() { els.input.style.height = "auto"; els.input.style.height = Math.min(els.input.scrollHeight, 200) + "px"; }
  function send() {
    const text = els.input.value.trim();
    if (!text && attachments.length === 0) return;
    addMsg("user").innerHTML = window.md.render(text);
    post("send", { text: text, images: attachments });
    els.input.value = ""; attachments = []; renderAttachments(); autoGrow(); showThinking("Working");
  }
  function renderAttachments() {
    els.attachments.innerHTML = "";
    attachments.forEach((a, i) => {
      const c = document.createElement("span"); c.className = "chip";
      c.innerHTML = '<img src="data:' + a.mediaType + ";base64," + a.data + '" /><span>' + window.md.esc(a.name || "image") + '</span><button data-i="' + i + '" style="background:none;border:none;color:inherit;cursor:pointer">×</button>';
      c.querySelector("button").addEventListener("click", () => { attachments.splice(i, 1); renderAttachments(); });
      els.attachments.appendChild(c);
    });
  }
  els.input.addEventListener("input", () => { autoGrow(); if (els.input.value.startsWith("/") && cOpen !== "slash") openSlash(); });
  els.input.addEventListener("keydown", (e) => {
    if (cOpen === "slash") { if (paletteKey(e)) return; }
    if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); send(); }
  });
  els.input.addEventListener("paste", (e) => {
    const items = e.clipboardData && e.clipboardData.items; if (!items) return;
    for (const it of items) if (it.type && it.type.startsWith("image/")) {
      const f = it.getAsFile(); const rd = new FileReader();
      rd.onload = () => { attachments.push({ mediaType: it.type, data: String(rd.result).split(",")[1], name: "pasted." + it.type.split("/")[1] }); renderAttachments(); };
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
    if (cOpen && !els.cpop.contains(e.target) && !e.target.closest("#plusBtn,#slashBtn,#modeBtn")) closeC();
  });
  document.addEventListener("keydown", (e) => { if (e.key === "Escape") closeAll(); });

  // ---- top popovers ----
  function resetTotals() { totals.costUsd = 0; totals.inputTokens = 0; totals.outputTokens = 0; totals.turns = 0; els.usage.textContent = ""; }
  function activeTop() { [["model", els.modelBtn], ["context", els.contextBtn], ["usage", els.usageBtn]].forEach(([k, b]) => b.classList.toggle("active", topOpen === k)); }
  function closeTop() { topOpen = null; els.popover.classList.add("hidden"); els.popover.innerHTML = ""; activeTop(); }
  function openTop(which) { closeC(); topOpen = which; activeTop(); if (which === "model") renderModel(); else if (which === "context") { post("getContext"); renderContext(); } else if (which === "usage") { post("getUsage"); renderUsage(); } }
  function toggleTop(w) { if (topOpen === w) closeTop(); else openTop(w); }
  function showTop(html) { els.popover.innerHTML = html; els.popover.classList.remove("hidden"); const x = els.popover.querySelector(".close-x"); if (x) x.addEventListener("click", closeTop); }

  function renderModel() {
    let h = '<h3>Select a model <button class="close-x">×</button></h3>';
    models.forEach((m) => {
      h += '<div class="opt' + (m.id === cur.model ? " sel" : "") + '" data-id="' + m.id + '"><div class="obody"><div class="oname">' + window.md.esc(m.name) + '</div><div class="odesc">' + window.md.esc(m.desc || "") + '</div></div>' + (m.id === cur.model ? '<div class="ochk">✓</div>' : "") + "</div>";
    });
    showTop(h);
    els.popover.querySelectorAll(".opt").forEach((o) => o.addEventListener("click", () => { cur.model = o.dataset.id; post("setModel", { model: cur.model }); closeTop(); }));
  }
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
          h += '<div class="uitem">';
          h += '<div class="utitle"><span>' + window.md.esc(l.name || "") + '</span><span>' + Math.round(pct) + "%</span></div>";
          h += '<div class="ubar"><div class="ufill" style="width:' + pct + '%"></div></div>';
          if (l.resetsIn) h += '<div class="ureset">Resets in ' + window.md.esc(l.resetsIn) + '</div>';
          h += '</div>';
        });
      }

      h += '<div style="margin-top:12px"><a class="ulink" href="#" data-url="' + window.md.esc(acct.manageUrl || "https://claude.ai") + '">Manage usage on claude.ai</a></div>';
    }

    h += '<div class="sec" style="margin-top:10px">Session tokens</div>';
    h += kv("Total cost", "$" + totals.costUsd.toFixed(4));
    h += kv("Turns", totals.turns);
    h += kv("Input tokens", totals.inputTokens.toLocaleString());
    h += kv("Output tokens", totals.outputTokens.toLocaleString());
    h += kv("Total tokens", (totals.inputTokens + totals.outputTokens).toLocaleString());

    showTop(h);

    const link = els.popover.querySelector(".ulink");
    if (link) link.addEventListener("click", function(e) { e.preventDefault(); post("openExternal", { url: link.dataset.url }); });
  }
  let lastIde = null;
  function mergeContextIde(p) { lastIde = p; if (topOpen === "context") renderContext(); }
  function renderContext() {
    const used = ctx.used, win = ctx.window || 200000, pct = Math.round((used / win) * 100);
    const sys = ctx.system || 0, msgs = Math.max(0, used - sys), free = Math.max(0, win - used);
    const seg = (v, c) => '<span style="width:' + (win ? (v / win * 100) : 0) + '%;background:' + c + '"></span>';
    let h = '<h3>Context usage <button class="close-x">×</button></h3>';
    h += '<div class="ctxhead">' + window.md.esc(ctx.model || cur.model) + '</div>';
    h += '<div class="ctxsub">' + fmt(used) + " / " + fmt(win) + " tokens (" + pct + "%)</div>";
    h += '<div class="ctxbar">' + seg(sys, "#cc7a3b") + seg(msgs, "#b07cff") + seg(free, "transparent") + "</div>";
    h += '<div class="brk">';
    h += row("#cc7a3b", "System & tools (cached prefix)", sys, win);
    h += row("#b07cff", "Messages", msgs, win);
    h += row("transparent", "Free space", free, win);
    h += "</div>";
    h += '<div class="note" style="margin-top:6px">Per-category split (system prompt vs tools vs skills) isn\'t exposed by the headless CLI; values are derived from real token usage.</div>';
    if (lastIde) {
      h += '<div class="sec">IDE context</div>';
      h += kv("Working dir", lastIde.cwd) + kv("Active file", lastIde.activeFile || "—");
      if (lastIde.hasSelection) h += kv("Selection", "lines " + lastIde.selStart + "–" + lastIde.selEnd);
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

  function renderMode() {
    let h = '<div class="sec">Permission mode</div>';
    modes.forEach((m) => {
      h += '<div class="opt' + (m.id === cur.mode ? " sel" : "") + '" data-id="' + m.id + '"><div class="oicon">' + (m.icon || "") + '</div><div class="obody"><div class="oname">' + window.md.esc(m.name) + '</div><div class="odesc">' + window.md.esc(m.desc || "") + '</div></div>' + (m.id === cur.mode ? '<div class="ochk">✓</div>' : "") + "</div>";
    });
    h += '<div class="effort-row"><div class="elabel">Effort <small>(thinking budget)</small></div><div class="dots" id="effdots"></div></div>';
    showC(h);
    els.cpop.querySelectorAll(".opt").forEach((o) => o.addEventListener("click", () => { cur.mode = o.dataset.id; post("setPermissionMode", { mode: cur.mode }); updateModeLabel(); renderMode(); }));
    const dots = els.cpop.querySelector("#effdots");
    efforts.forEach((e) => {
      const d = document.createElement("span"); d.className = "dot" + (e.id === cur.effort ? " on" : ""); d.title = e.name;
      d.addEventListener("click", () => { cur.effort = e.id; post("setEffort", { effort: cur.effort }); renderMode(); });
      dots.appendChild(d);
    });
    const lbl = document.createElement("small"); lbl.style.cssText = "margin-left:8px;color:var(--fg-dim)";
    lbl.textContent = (efforts.find((x) => x.id === cur.effort) || {}).name || ""; dots.appendChild(lbl);
  }

  // ---- slash palette ----
  let palIndex = 0, palItems = [];
  function extCmds() {
    return [
      { name: "context", desc: "Show context window usage", run: () => openTop("context") },
      { name: "usage", desc: "Show session usage & cost", run: () => openTop("usage") },
      { name: "model", desc: "Select model", run: () => openTop("model") },
      { name: "compact", desc: "Compact the conversation", run: () => { post("compact"); showThinking("Compacting"); } },
      { name: "clear", desc: "Clear the chat", run: () => handlers.clear() },
      { name: "new", desc: "Start a new session", run: () => { resetTotals(); post("newSession"); } },
    ];
  }
  function openSlash() {
    closeTop(); cOpen = "slash"; activeC();
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
    const cli = slashCommands.map((c) => ({ name: c, desc: "Insert /" + c, kind: "cli" }));
    palItems = ext.concat(cli).filter((c) => c.name.toLowerCase().includes(q));
    palIndex = 0; renderPalette();
  }
  function renderPalette() {
    const list = els.cpop.querySelector("#pallist"); if (!list) return;
    if (!palItems.length) { list.innerHTML = '<div class="note" style="padding:8px">No matching commands</div>'; return; }
    let h = "";
    palItems.forEach((c, i) => {
      h += '<button class="menu-item' + (i === palIndex ? " sel" : "") + '" data-i="' + i + '"><span class="mi-icon">/</span><span><span class="cmd-name">/' + window.md.esc(c.name) + '</span> <span class="mi-desc">' + window.md.esc(c.desc || "") + "</span></span></button>";
    });
    list.innerHTML = h;
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

  function closeAll() { closeTop(); closeC(); }

  updateRing();
  post("ready");
})();
