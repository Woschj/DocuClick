// Real Chromium renderer + sandbox iframe, simulated public Obsidian API.
// No user's Obsidian profile or vault is touched.
const fs = require("node:fs");
const path = require("node:path");
const os = require("node:os");
const http = require("node:http");
const { spawn } = require("node:child_process");
const assert = require("node:assert/strict");
const root = path.resolve(__dirname, "../..");
const css = fs.readFileSync(path.join(root, "obsidian/styles.css"), "utf8");
const bundle = fs.readFileSync(path.join(root, "dist/obsidian-docuclick/docuclick-diagrams/main.js"), "utf8");
const browserPath = process.argv[2];
if (!browserPath) throw new Error("Usage: node browser.cjs /path/to/chromium");
const profile = fs.mkdtempSync(path.join(os.tmpdir(), "docuclick-browser-"));
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const mock = `
(() => {
HTMLElement.prototype.empty = function() { this.replaceChildren(); };
HTMLElement.prototype.addClass = function(name) { this.classList.add(name); };
HTMLElement.prototype.setText = function(text) { this.textContent = text; };
HTMLElement.prototype.createEl = function(tag, options = {}) {
  const el = document.createElement(tag);
  if (options.cls) el.className = options.cls;
  if (options.text) el.textContent = options.text;
  for (const [key, value] of Object.entries(options.attr || {})) el.setAttribute(key, value);
  this.appendChild(el); return el;
};
HTMLElement.prototype.createDiv = function(options) { return this.createEl('div', options); };
HTMLElement.prototype.createSpan = function(options) { return this.createEl('span', options); };
const files = new Map(), notices = [];
class TFile { constructor(path) { this.path = path; this.basename = path.replace(/\\.[^.]+$/, ''); this.extension = path.split('.').pop(); this.parent = {path: '/'}; } }
const vault = {
  read: async file => files.get(file.path).text,
  process: async (file, fn) => { if (window.writeDelay) await new Promise(resolve => setTimeout(resolve, window.writeDelay)); const entry = files.get(file.path); entry.text = fn(entry.text); },
  create: async (path, text) => { if (files.has(path)) throw Error('exists'); const file = new TFile(path); files.set(path, {file, text}); return file; },
  getAbstractFileByPath: path => files.get(path)?.file
};
const app = {vault, workspace: {on: () => () => {}, getActiveFile: () => null, getLeaf: () => ({openFile: async () => {}})}};
class Plugin {
  constructor() { this.app = app; }
  registerView(type, factory) { this.factory = factory; }
  registerExtensions() {} addRibbonIcon() {} addCommand() {} registerEvent() {}
}
class FileView { constructor() { this.app = app; this.contentEl = document.body.createDiv(); this.contentEl.style = 'height:95vh;display:flex;flex-direction:column'; } }
window.module = {exports: {}};
window.require = name => { if (name !== 'obsidian') throw Error(name); return {Plugin, FileView, Modal: class {}, Setting: class {}, Notice: class {constructor(text) {notices.push(text);}}, TFile, normalizePath: path => path}; };
window.start = async () => {
  const plugin = new module.exports(); await plugin.onload();
  const file = await vault.create('Test.docuclick', JSON.stringify({format:'docuclick-diagram', version:1, canvas:{nodes:[],edges:[]}, flow:{nodes:[],edges:[]}}));
  const view = plugin.factory({}); view.file = file; await view.onOpen(); await view.onLoadFile(file);
  window.testHost = {plugin, view, file, files, notices, vault};
};
})();
`;
const server = http.createServer((request, response) => {
  response.setHeader("Content-Type", request.url.endsWith(".js") ? "text/javascript; charset=utf-8" : request.url.endsWith(".css") ? "text/css; charset=utf-8" : "text/html; charset=utf-8");
  response.end(request.url === "/styles.css" ? css : request.url === "/main.js" ? bundle : request.url === "/mock.js" ? mock : '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="/styles.css"></head><body><script src="/mock.js"></script><script src="/main.js"></script><script>start().catch(e => window.bootError = e.stack)</script></body></html>');
});
let browser, socket;
(async () => {
  await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
  browser = spawn(browserPath, ["--headless=new", "--disable-gpu", "--disable-site-isolation-trials", "--no-first-run", "--no-default-browser-check", "--disable-background-networking", "--remote-debugging-port=0", `--user-data-dir=${profile}`, "about:blank"], {stdio: "ignore"});
  const portFile = path.join(profile, "DevToolsActivePort");
  for (let i = 0; i < 100 && !fs.existsSync(portFile); i++) await delay(100);
  assert.ok(fs.existsSync(portFile), "Chromium did not start");
  const port = fs.readFileSync(portFile, "utf8").split("\n")[0];
  const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
  socket = new WebSocket(targets.find(x => x.type === "page").webSocketDebuggerUrl);
  await new Promise(resolve => socket.addEventListener("open", resolve, {once:true}));
  let seq = 0; const calls = new Map(), contexts = new Map(), errors = [];
  socket.addEventListener("message", event => {
    const message = JSON.parse(event.data);
    if (message.id) { const entry = calls.get(message.id); calls.delete(message.id); message.error ? entry.reject(Error(JSON.stringify(message.error))) : entry.resolve(message.result); }
    if (message.method === "Runtime.executionContextCreated") contexts.set(message.params.context.id, message.params.context);
    if (message.method === "Runtime.executionContextDestroyed") contexts.delete(message.params.executionContextId);
    if (message.method === "Runtime.exceptionThrown") errors.push(message.params.exceptionDetails.exception?.description || message.params.exceptionDetails.text);
  });
  const send = (method, params = {}) => new Promise((resolve,reject) => {const id = ++seq; calls.set(id,{resolve,reject}); socket.send(JSON.stringify({id,method,params}));});
  const evaluate = async (expression, contextId) => {
    const result = await send("Runtime.evaluate", {expression, contextId, awaitPromise:true, returnByValue:true});
    if (result.exceptionDetails) throw Error(result.exceptionDetails.exception?.description || result.exceptionDetails.text);
    return result.result.value;
  };
  const until = async (expression, contextId) => {
    for (let i=0;i<100;i++) { if (await evaluate(expression, contextId)) return; await delay(50); }
    throw Error(`Timed out: ${expression}\n${errors.join("\n")}\nHost: ${await evaluate("JSON.stringify({files:Array.from(testHost.files.keys()), notices:testHost.notices, error:testHost.view.state.error})")}`);
  };
  await send("Runtime.enable"); await send("Page.enable");
  await send("Emulation.setDeviceMetricsOverride", {width:1440,height:900,deviceScaleFactor:1,mobile:false});
  await send("Page.navigate", {url:`http://127.0.0.1:${server.address().port}/`});
  await until("!!window.testHost || !!window.bootError");
  assert.equal(await evaluate("window.bootError"), undefined);
  let frameContext;
  for (let i=0;i<100 && !frameContext;i++) {
    for (const context of contexts.values()) {
      if (context.auxData?.isDefault && await evaluate("!!window.docuclickEditorHost && typeof snapshot === 'function'", context.id).catch(() => false)) frameContext = context.id;
    }
    if (!frameContext) await delay(50);
  }
  assert.ok(frameContext, `Editor did not initialize: ${errors.join("\n")} contexts=${JSON.stringify([...contexts.values()])} host=${await evaluate("testHost.view.contentEl.innerText")}`);
  const inFrame = expr => evaluate(expr, frameContext);
  assert.equal(await inFrame("(() => {try {return !!parent.document} catch {return false}})()"), false, "iframe can access parent DOM");
  // Exercise actual palette UI, not only the internal data model.
  await inFrame("document.getElementById('add-element-btn').click()");
  await inFrame("document.getElementById('element-input').value = 'Erster Schritt'; document.getElementById('element-save-btn').click()");
  await until("JSON.parse(testHost.files.get('Test.docuclick').text).canvas.nodes.length === 1");
  assert.equal(await inFrame("snapshot().canvas.nodes[0].text"), "Erster Schritt");
  // Fix regression: rename must update the document, not only Cytoscape's label.
  await inFrame("openRenameModal(snapshot().canvas.nodes[0].id); document.getElementById('rename-input').value = 'Umbenannt'; document.getElementById('rename-save-btn').click()");
  await until("JSON.parse(testHost.files.get('Test.docuclick').text).canvas.nodes[0].text === 'Umbenannt'");
  await inFrame("restoreHistory(-1)");
  await until("JSON.parse(testHost.files.get('Test.docuclick').text).canvas.nodes[0].text === 'Erster Schritt'");
  await inFrame("restoreHistory(1)");
  await until("JSON.parse(testHost.files.get('Test.docuclick').text).canvas.nodes[0].text === 'Umbenannt'");
  const png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==";
  await inFrame(`addManualElement(300, 100, 'rectangle', '#123456', 'Bild', ${JSON.stringify(png)})`);
  await inFrame("window.imageId = snapshot().flow.nodes[1].data.id; cy.getElementById(imageId).position({x:700,y:400}); finalizeMove(imageId)");
  assert.equal(await inFrame("snapshot().canvas.nodes.find(n => n.type === 'file').x"), 700);
  assert.equal(await inFrame("snapshot().canvas.nodes.find(n => n.type === 'file').y"), 470);
  await inFrame("tryConnect(snapshot().flow.nodes[0].data.id, imageId)");
  await until("JSON.parse(testHost.files.get('Test.docuclick').text).canvas.edges.length === 1");
  await inFrame("deleteNode(imageId)");
  await inFrame("restoreHistory(-1)");
  assert.equal(await inFrame("snapshot().flow.nodes.length"), 2);
  assert.equal(await inFrame("snapshot().canvas.edges.length"), 1);
  await evaluate("testHost.view.flush()");
  // Only the editor's own dock remains. Export is a separate viewer-only HTML.
  assert.equal(await evaluate("testHost.view.contentEl.querySelectorAll('button, .docuclick-toolbar').length"), 0);
  await inFrame("document.getElementById('export-readonly-btn').click()");
  await until("testHost.files.has('Test – Ansicht.html')");
  assert.equal(await evaluate("DocuClickDocument.importHtml(testHost.files.get('Test – Ansicht.html').text).flow.nodes.length"), 2);
  await evaluate("window.viewerFrame = document.body.createEl('iframe', {attr:{sandbox:'allow-scripts'}}); viewerFrame.srcdoc = testHost.files.get('Test – Ansicht.html').text");
  let viewerContext;
  for (let i=0; i<100 && !viewerContext; i++) {
    for (const context of contexts.values()) {
      if (context.auxData?.isDefault && context.id !== frameContext && await evaluate("typeof cy !== 'undefined' && !window.docuclickEditorHost", context.id).catch(() => false)) viewerContext = context.id;
    }
    if (!viewerContext) await delay(50);
  }
  assert.ok(viewerContext, "Exported HTML did not load");
  const inViewer = expression => evaluate(expression, viewerContext);
  assert.equal(await inViewer("typeof addManualElement + ':' + typeof scheduleSave"), "undefined:undefined");
  assert.equal(await inViewer("document.querySelectorAll('#add-element-btn, #export-readonly-btn, #download-btn, #rename-modal, #element-modal, #node-handles').length"), 0);
  assert.equal(await inViewer("cy.nodes().every(n => !n.grabbable())"), true);
  const before = await inViewer("JSON.stringify(cy.elements().jsons())");
  await inViewer("cy.nodes().first().emit('cxttap'); document.dispatchEvent(new KeyboardEvent('keydown', {key:'Delete'})); document.dispatchEvent(new KeyboardEvent('keydown', {key:'z',ctrlKey:true})); document.dispatchEvent(new Event('paste')); document.dispatchEvent(new Event('drop'))");
  assert.equal(await inViewer("JSON.stringify(cy.elements().jsons())"), before);
  await inViewer("document.getElementById('guide-toggle-btn').click()");
  assert.equal(await inViewer("document.getElementById('guide-drawer').classList.contains('open')"), true);
  await inViewer("searchInput.value = 'Umbenannt'; searchInput.dispatchEvent(new Event('input'))");
  assert.equal(await inViewer("cy.nodes('.search-hit').length"), 1);
  await inViewer("void cy.nodes('[imageUrl]').first().emit('tap')");
  assert.equal(await inViewer("document.getElementById('lightbox').hidden"), false);
  await inViewer("document.getElementById('lightbox-close-btn').click()");
  const zoom = await inViewer("cy.zoom()");
  await inViewer("document.getElementById('zoom-in-btn').click()");
  assert.ok(await inViewer("cy.zoom()") > zoom);
  await evaluate("viewerFrame.remove()");
  const screenshot = await send("Page.captureScreenshot", {format:"png"});
  fs.writeFileSync(path.join(root, "dist/obsidian-docuclick/editor-test.png"), Buffer.from(screenshot.data,"base64"));
  // A forged message from the outer window must not alter this view's file.
  await evaluate("window.postMessage({channel:testHost.view.channel, kind:'save', snapshot:{canvas:{nodes:[],edges:[]},flow:{nodes:[],edges:[]}}}, '*')");
  await delay(100);
  assert.equal(await evaluate("JSON.parse(testHost.files.get('Test.docuclick').text).flow.nodes.length"), 2);
  // Multiple changes while a write is in flight must all reach the vault.
  await evaluate("window.writeDelay = 30");
  await inFrame("openRenameModal(snapshot().flow.nodes[0].data.id); document.getElementById('rename-input').value = 'Schnell 1'; saveRename(); openRenameModal(snapshot().flow.nodes[0].data.id); document.getElementById('rename-input').value = 'Schnell 2'; saveRename()");
  await evaluate("testHost.view.flush()");
  assert.equal(await evaluate("JSON.parse(testHost.files.get('Test.docuclick').text).canvas.nodes[0].text"), "Schnell 2");
  await evaluate("window.writeDelay = 0");
  // An external edit must survive; local edits go into a recovery document.
  await evaluate("testHost.files.get('Test.docuclick').text = 'external-content'");
  await inFrame("addManualElement(0, 700, 'ellipse', '#10b981', 'Lokale Änderung')");
  await until("Array.from(testHost.files.keys()).some(p => p.includes('lokale Änderungen'))");
  assert.equal(await evaluate("testHost.files.get('Test.docuclick').text"), "external-content");
  await evaluate("testHost.view.flush()");
  assert.equal(await evaluate("JSON.parse(testHost.files.get(testHost.view.state.recovery.path).text).flow.nodes.length"), 3);
  // Close immediately after one more edit: flush must capture the latest state.
  await inFrame("addManualElement(0, 1000, 'rectangle', '#3b82f6', 'Beim Schließen')");
  await evaluate("window.recoveryPath = testHost.view.state.recovery.path; testHost.view.onUnloadFile()");
  assert.equal(await evaluate("JSON.parse(testHost.files.get(recoveryPath).text).flow.nodes.length"), 4);
  assert.equal(await evaluate("document.querySelectorAll('iframe').length"), 0);
  assert.deepEqual(errors, [], "Uncaught browser errors");
  console.log("PASS: sandbox, palette, rename, undo/redo, image move, connect/delete/restore, Vault save, read-only HTML export/search/guide/lightbox, forged message rejection, conflict recovery, close flush");
})().catch(error => { console.error(error); process.exitCode=1; }).finally(async () => {
  socket?.close(); browser?.kill(); server.close();
  await delay(300);
  fs.rmSync(profile, {recursive:true, force:true});
});
