"use strict";
// A DOM small enough to read in one sitting and real enough to boot media/app.js under Node's
// `vm`. Release builds ship with AreDevToolsEnabled = false, so a bug in the chat UI is invisible
// from inside VS — this is the only place webview behaviour can be asserted automatically.
//
// Scope on purpose: it implements what media/app.js actually touches (see the selector list in
// matchCompound) and nothing else. If app.js starts using a DOM feature that is missing, the boot
// throws with a clear TypeError rather than silently doing the wrong thing — extend it then.

const VOID_TAGS = new Set([
  "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta",
  "param", "source", "track", "wbr",
  // SVG shapes we emit self-closed
  "path", "rect", "circle", "line", "polygon", "polyline", "ellipse", "stop", "use",
]);

const ENTITIES = { amp: "&", lt: "<", gt: ">", quot: '"', apos: "'", "#39": "'", nbsp: " " };

function decodeEntities(s) {
  return s.replace(/&(amp|lt|gt|quot|apos|nbsp|#39);/g, (_, name) => ENTITIES[name]);
}
function encodeText(s) {
  return String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
}
function encodeAttr(s) {
  return String(s).replace(/&/g, "&amp;").replace(/"/g, "&quot;");
}

// ---------------------------------------------------------------- class list / style

class ClassList {
  constructor(el) { this._el = el; }
  get _list() { return (this._el.getAttribute("class") || "").split(/\s+/).filter(Boolean); }
  _write(list) { this._el.setAttribute("class", list.join(" ")); }
  add(...names) {
    const l = this._list;
    for (const n of names) if (n && l.indexOf(n) === -1) l.push(n);
    this._write(l);
  }
  remove(...names) { this._write(this._list.filter((c) => names.indexOf(c) === -1)); }
  contains(n) { return this._list.indexOf(n) !== -1; }
  toggle(n, force) {
    const want = force === undefined ? !this.contains(n) : !!force;
    if (want) this.add(n); else this.remove(n);
    return want;
  }
  get length() { return this._list.length; }
  toString() { return this._list.join(" "); }
}

class Style {
  constructor() { this._props = {}; }
  get cssText() {
    return Object.keys(this._props).map((k) => k + ":" + this._props[k]).join(";");
  }
  set cssText(v) {
    this._props = {};
    String(v).split(";").forEach((decl) => {
      const i = decl.indexOf(":");
      if (i > 0) this._props[decl.slice(0, i).trim()] = decl.slice(i + 1).trim();
    });
  }
}
// Every other style property is a plain assignment (el.style.height = "10px"), which a bare object
// already handles — Style only exists so cssText round-trips.

// ---------------------------------------------------------------- events

class DomEvent {
  constructor(type, init) {
    init = init || {};
    this.type = type;
    this.bubbles = init.bubbles !== false;
    this.defaultPrevented = false;
    this.target = null;
    this.currentTarget = null;
    this._stopped = false;
    this._stopImmediate = false;
    Object.assign(this, init.props || {});
  }
  preventDefault() { this.defaultPrevented = true; }
  stopPropagation() { this._stopped = true; }
  stopImmediatePropagation() { this._stopped = true; this._stopImmediate = true; }
}

const EventTarget = {
  addEventListener(type, fn) {
    if (typeof fn !== "function") return;
    if (!this._listeners.has(type)) this._listeners.set(type, []);
    this._listeners.get(type).push(fn);
  },
  removeEventListener(type, fn) {
    const l = this._listeners.get(type);
    if (!l) return;
    const i = l.indexOf(fn);
    if (i !== -1) l.splice(i, 1);
  },
  _fire(ev) {
    const l = this._listeners.get(ev.type);
    if (!l || !l.length) return;
    ev.currentTarget = this;
    for (const fn of l.slice()) {
      fn.call(this, ev);
      if (ev._stopImmediate) break;
    }
  },
};

// Elements bubble up their parents, then the document, then the window — app.js listens at all
// three levels (#messages for the copy button, document for outside-click, window for host
// messages), so the order here is load-bearing.
function propagate(start, ev) {
  ev.target = ev.target || start;
  let node = start;
  while (node) {
    node._fire(ev);
    if (ev._stopped || !ev.bubbles) return !ev.defaultPrevented;
    node = node.parentNode;
  }
  const doc = start.ownerDocument;
  if (doc) {
    doc._fire(ev);
    if (!ev._stopped && doc.defaultView) doc.defaultView._fire(ev);
  }
  return !ev.defaultPrevented;
}

// ---------------------------------------------------------------- nodes

class TextNode {
  constructor(doc, data) {
    this.ownerDocument = doc;
    this.nodeType = 3;
    this.data = String(data);
    this.parentNode = null;
    this.childNodes = [];
  }
  get textContent() { return this.data; }
  set textContent(v) { this.data = String(v); }
  get outerHTML() { return encodeText(this.data); }
  remove() { if (this.parentNode) this.parentNode.removeChild(this); }
}

class Element {
  constructor(doc, tagName) {
    this.ownerDocument = doc;
    this.nodeType = 1;
    this.tagName = String(tagName).toUpperCase();
    this.attributes = new Map();
    this.childNodes = [];
    this.parentNode = null;
    this.classList = new ClassList(this);
    this.style = new Style();
    this._listeners = new Map();
    // Layout numbers the UI reads. Real layout does not exist here; a test that cares assigns them.
    this.scrollTop = 0;
    this.scrollHeight = 0;
    this.clientHeight = 0;
    // Form-ish state.
    this.value = "";
    this.selectionStart = 0;
    this.selectionEnd = 0;
    this.checked = false;
    this.disabled = false;
  }

  get localName() { return this.tagName.toLowerCase(); }
  get id() { return this.getAttribute("id") || ""; }
  set id(v) { this.setAttribute("id", v); }
  get className() { return this.getAttribute("class") || ""; }
  set className(v) { this.setAttribute("class", v); }
  get title() { return this.getAttribute("title") || ""; }
  set title(v) { this.setAttribute("title", v); }

  setAttribute(name, value) {
    name = String(name).toLowerCase();
    this.attributes.set(name, String(value));
    if (name === "value") this.value = String(value);
  }
  getAttribute(name) {
    name = String(name).toLowerCase();
    return this.attributes.has(name) ? this.attributes.get(name) : null;
  }
  hasAttribute(name) { return this.attributes.has(String(name).toLowerCase()); }
  removeAttribute(name) { this.attributes.delete(String(name).toLowerCase()); }
  get dataset() {
    const out = {};
    for (const [k, v] of this.attributes) if (k.startsWith("data-")) out[k.slice(5)] = v;
    return out;
  }

  get children() { return this.childNodes.filter((n) => n.nodeType === 1); }
  get firstChild() { return this.childNodes[0] || null; }
  get lastChild() { return this.childNodes[this.childNodes.length - 1] || null; }

  appendChild(node) {
    if (node.parentNode) node.parentNode.removeChild(node);
    node.parentNode = this;
    this.childNodes.push(node);
    return node;
  }
  insertBefore(node, ref) {
    if (!ref) return this.appendChild(node);
    const i = this.childNodes.indexOf(ref);
    if (i === -1) return this.appendChild(node);
    if (node.parentNode) node.parentNode.removeChild(node);
    node.parentNode = this;
    this.childNodes.splice(i, 0, node);
    return node;
  }
  removeChild(node) {
    const i = this.childNodes.indexOf(node);
    if (i !== -1) { this.childNodes.splice(i, 1); node.parentNode = null; }
    return node;
  }
  remove() { if (this.parentNode) this.parentNode.removeChild(this); }
  contains(node) {
    for (let n = node; n; n = n.parentNode) if (n === this) return true;
    return false;
  }

  get textContent() {
    return this.childNodes.map((n) => n.textContent).join("");
  }
  set textContent(v) {
    this.childNodes.forEach((n) => { n.parentNode = null; });
    this.childNodes = [];
    if (v !== "" && v != null) this.appendChild(new TextNode(this.ownerDocument, v));
  }

  get innerHTML() { return this.childNodes.map((n) => n.outerHTML).join(""); }
  set innerHTML(html) {
    this.childNodes.forEach((n) => { n.parentNode = null; });
    this.childNodes = [];
    parseInto(this, String(html), this.ownerDocument);
  }
  get outerHTML() {
    let attrs = "";
    for (const [k, v] of this.attributes) attrs += " " + k + '="' + encodeAttr(v) + '"';
    const tag = this.localName;
    if (VOID_TAGS.has(tag) && !this.childNodes.length) return "<" + tag + attrs + " />";
    return "<" + tag + attrs + ">" + this.innerHTML + "</" + tag + ">";
  }

  matches(selector) { return matchesSelector(this, selector); }
  closest(selector) {
    for (let n = this; n && n.nodeType === 1; n = n.parentNode) if (matchesSelector(n, selector)) return n;
    return null;
  }
  querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
  querySelectorAll(selector) {
    const out = [];
    walk(this, (el) => { if (el !== this && matchesSelector(el, selector)) out.push(el); });
    return out;
  }

  focus() { if (this.ownerDocument) this.ownerDocument.activeElement = this; }
  blur() { if (this.ownerDocument && this.ownerDocument.activeElement === this) this.ownerDocument.activeElement = null; }
  select() {
    this.selectionStart = 0;
    this.selectionEnd = String(this.value).length;
    // execCommand("copy") copies the selection, so the document has to know what was selected for
    // the clipboard fallback to be assertable.
    if (this.ownerDocument) this.ownerDocument._selection = this;
  }
  scrollIntoView() {}

  dispatchEvent(ev) { return propagate(this, ev); }
  click(props) { return this.dispatchEvent(new DomEvent("click", { bubbles: true, props: props })); }
}
Object.assign(Element.prototype, EventTarget);

function walk(root, fn) {
  fn(root);
  for (const child of root.childNodes) if (child.nodeType === 1) walk(child, fn);
}

// ---------------------------------------------------------------- selectors
// Supports what app.js uses: tag, #id, .class, [attr], [attr="value"], compound selectors,
// descendant combinators, and comma-separated groups. No child/sibling combinators, no pseudos.

const selectorCache = new Map();

function parseSelector(selector) {
  if (selectorCache.has(selector)) return selectorCache.get(selector);
  const groups = String(selector).split(",").map((group) =>
    group.trim().split(/\s+/).filter(Boolean).map(parseCompound));
  selectorCache.set(selector, groups);
  return groups;
}

function parseCompound(text) {
  const c = { tag: null, id: null, classes: [], attrs: [] };
  const re = /^([A-Za-z][\w-]*)|#([\w-]+)|\.([\w-]+)|\[([\w:.-]+)(?:\s*=\s*"([^"]*)")?\]/g;
  let m, consumed = 0;
  while ((m = re.exec(text)) !== null) {
    consumed = re.lastIndex;
    if (m[1]) c.tag = m[1].toUpperCase();
    else if (m[2]) c.id = m[2];
    else if (m[3]) c.classes.push(m[3]);
    else if (m[4]) c.attrs.push({ name: m[4].toLowerCase(), value: m[5] });
    if (m.index !== 0 && re.lastIndex === m.index) break;
  }
  if (consumed !== text.length) throw new Error("dom-stub: unsupported selector fragment: " + text);
  return c;
}

function matchCompound(el, c) {
  if (c.tag && el.tagName !== c.tag) return false;
  if (c.id && el.getAttribute("id") !== c.id) return false;
  for (const cls of c.classes) if (!el.classList.contains(cls)) return false;
  for (const a of c.attrs) {
    if (!el.hasAttribute(a.name)) return false;
    if (a.value !== undefined && el.getAttribute(a.name) !== a.value) return false;
  }
  return true;
}

function matchesSelector(el, selector) {
  if (el.nodeType !== 1) return false;
  return parseSelector(selector).some((chain) => {
    if (!chain.length) return false;
    if (!matchCompound(el, chain[chain.length - 1])) return false;
    let i = chain.length - 2;
    let node = el.parentNode;
    while (i >= 0) {
      if (!node || node.nodeType !== 1) return false;
      if (matchCompound(node, chain[i])) i--;
      node = node.parentNode;
    }
    return true;
  });
}

// ---------------------------------------------------------------- HTML parsing

function findTagEnd(html, start) {
  let quote = null;
  for (let i = start; i < html.length; i++) {
    const ch = html[i];
    if (quote) { if (ch === quote) quote = null; continue; }
    if (ch === '"' || ch === "'") { quote = ch; continue; }
    if (ch === ">") return i;
  }
  return html.length;
}

const ATTR_RE = /([\w:.-]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'>]+)))?/g;

function parseInto(root, html, doc) {
  const stack = [root];
  const top = () => stack[stack.length - 1];
  let i = 0;

  const pushText = (text) => {
    if (!text) return;
    top().appendChild(new TextNode(doc, decodeEntities(text)));
  };

  while (i < html.length) {
    const lt = html.indexOf("<", i);
    if (lt === -1) { pushText(html.slice(i)); break; }
    if (lt > i) pushText(html.slice(i, lt));

    if (html.startsWith("<!--", lt)) {
      const end = html.indexOf("-->", lt);
      i = end === -1 ? html.length : end + 3;
      continue;
    }
    if (html.startsWith("<!", lt)) {           // <!DOCTYPE …>
      i = findTagEnd(html, lt) + 1;
      continue;
    }

    const gt = findTagEnd(html, lt);
    const raw = html.slice(lt + 1, gt);
    i = gt + 1;

    if (raw[0] === "/") {                       // closing tag
      const name = raw.slice(1).trim().toLowerCase();
      for (let s = stack.length - 1; s > 0; s--) {
        if (stack[s].localName === name) { stack.length = s; break; }
      }
      continue;
    }

    const selfClosed = raw.trimEnd().endsWith("/");
    const body = selfClosed ? raw.trimEnd().slice(0, -1) : raw;
    const nameMatch = body.match(/^([A-Za-z][\w:-]*)/);
    if (!nameMatch) continue;
    const tag = nameMatch[1];
    const el = doc.createElement(tag);

    ATTR_RE.lastIndex = nameMatch[0].length;
    let a;
    while ((a = ATTR_RE.exec(body)) !== null) {
      const value = a[2] !== undefined ? a[2] : a[3] !== undefined ? a[3] : a[4] !== undefined ? a[4] : "";
      el.setAttribute(a[1], decodeEntities(value));
    }

    top().appendChild(el);
    if (!selfClosed && !VOID_TAGS.has(el.localName)) stack.push(el);
  }
}

// ---------------------------------------------------------------- document & window

class Document {
  constructor() {
    this.nodeType = 9;
    this._listeners = new Map();
    this.visibilityState = "visible";
    this.activeElement = null;
    this.defaultView = null;
    this.documentElement = null;
    // Recorded so a test can assert the execCommand fallback ran and what it copied, and forced to
    // fail when it should exercise the "copy did not work" path.
    this.execCommands = [];
    this.execCommandResult = true;
    this._selection = null;
  }
  createElement(tag) { return new Element(this, tag); }
  createTextNode(text) { return new TextNode(this, text); }
  execCommand(name) {
    this.execCommands.push({ name, text: this._selection ? String(this._selection.value) : null });
    return this.execCommandResult;
  }

  get body() { return this.documentElement ? this.documentElement.querySelector("body") : null; }
  get head() { return this.documentElement ? this.documentElement.querySelector("head") : null; }

  getElementById(id) {
    if (!this.documentElement) return null;
    let found = null;
    walk(this.documentElement, (el) => { if (!found && el.getAttribute("id") === id) found = el; });
    return found;
  }
  querySelector(sel) { return this.documentElement ? this.documentElement.querySelector(sel) : null; }
  querySelectorAll(sel) { return this.documentElement ? this.documentElement.querySelectorAll(sel) : []; }
  dispatchEvent(ev) {
    ev.target = ev.target || this;
    this._fire(ev);
    if (!ev._stopped && this.defaultView) this.defaultView._fire(ev);
    return !ev.defaultPrevented;
  }
}
Object.assign(Document.prototype, EventTarget);

class Window {
  constructor(doc) {
    this._listeners = new Map();
    this.document = doc;
    this.navigator = {};
    this.location = { href: "https://claudecode.local/index.html" };
    this.setTimeout = setTimeout;
    this.clearTimeout = clearTimeout;
    this.setInterval = setInterval;
    this.clearInterval = clearInterval;
    // Left undefined by default: app.js guards on `window.ResizeObserver`, so the harness runs the
    // no-observer path unless a test opts in.
    this.requestAnimationFrame = (fn) => setTimeout(() => fn(Date.now()), 0);
  }
  dispatchEvent(ev) {
    ev.target = ev.target || this;
    this._fire(ev);
    return !ev.defaultPrevented;
  }
}
Object.assign(Window.prototype, EventTarget);

/** Build a document/window pair from an HTML source string (normally media/index.html). */
function createDom(html) {
  const document = new Document();
  const root = document.createElement("html");
  root.ownerDocument = document;
  document.documentElement = root;
  parseInto(root, String(html).replace(/<!DOCTYPE[^>]*>/i, ""), document);
  // The parsed source has its own <html> wrapper; unwrap it so documentElement is that element.
  const inner = root.children.find((c) => c.localName === "html");
  if (inner) {
    inner.parentNode = null;
    document.documentElement = inner;
  }
  const window = new Window(document);
  document.defaultView = window;
  window.window = window;
  return { document, window };
}

module.exports = {
  createDom, parseInto, DomEvent, Element, TextNode, Document, Window,
  decodeEntities, encodeText, walk,
};
