// Real Chromium renderer + sandbox iframe, simulated public Obsidian API.
// No user's Obsidian profile or vault is touched.
const fs = require("node:fs");
const path = require("node:path");
const os = require("node:os");
const http = require("node:http");
const { spawn } = require("node:child_process");
const assert = require("node:assert/strict");
const D = require("../src/document.js");
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
const files = new Map(), folders = new Map(), notices = [];
class TFolder { constructor(path) { this.path = path; } }
class TFile { constructor(path) { this.path = path; this.basename = path.split('/').pop().replace(/\\.[^.]+$/, ''); this.extension = path.split('.').pop(); this.parent = {path: path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : '/'}; this.stat = {size: 0}; } }
const handlers = [];
window.emitModify = file => handlers.forEach(handler => handler(file));
const vault = {
  on: (name, handler) => { if (name === 'modify') handlers.push(handler); return {}; },
  read: async file => files.get(file.path).text,
  cachedRead: async file => files.get(file.path).text,
  process: async (file, fn) => { if (window.writeDelay) await new Promise(resolve => setTimeout(resolve, window.writeDelay)); const entry = files.get(file.path); entry.text = fn(entry.text); },
  createBinary: async (path, bytes) => { if (files.has(path)) throw Error('exists'); const file = new TFile(path); file.stat.size = bytes.byteLength; files.set(path, {file, bytes}); return file; },
  readBinary: async file => files.get(file.path).bytes,
  create: async (path, text) => { if (files.has(path)) throw Error('exists'); const file = new TFile(path); files.set(path, {file, text}); return file; },
  getAbstractFileByPath: path => files.get(path)?.file || folders.get(path),
  createFolder: async path => { if (files.has(path) || folders.has(path)) throw Error('exists'); const folder = new TFolder(path); folders.set(path, folder); return folder; },
  getAllLoadedFiles: () => [...folders.values()],
  configDir: '.obsidian',
  adapter: { read: async path => { if (!files.has(path)) throw Error('missing'); return files.get(path).text; }, getFullPath: path => '/vault/' + path },
  getFiles: () => [...files.values()].map(entry => entry.file),
  getMarkdownFiles: () => [...files.values()].map(entry => entry.file).filter(file => file.extension === 'md')
};
// Frontmatter of notes, parsed on demand (enough for a "docuclick" property).
const frontmatter = path => { const text = files.get(path)?.text || ''; const m = /^---\\n([\\s\\S]*?)\\n---/.exec(text); if (!m) return undefined; const result = {}; for (const line of m[1].split('\\n')) { const kv = /^(\\w+):\\s*"?(.*?)"?$/.exec(line); if (kv) result[kv[1]] = kv[2]; } return result; };
const metadataCache = {
  getCache: path => ({frontmatter: frontmatter(path)}), getFileCache: file => ({frontmatter: frontmatter(file.path)}),
  getFirstLinkpathDest: path => files.get(path)?.file || null, on: () => ({})
};
class WorkspaceLeaf { async setViewState(state) { this.state = state; } }
class MarkdownRenderChild { constructor(containerEl) { this.containerEl = containerEl; } registerEvent() {} }
const app = {vault, metadataCache, workspace: {on: () => () => {}, getActiveFile: () => null, getLeaf: () => ({openFile: async () => {}, setViewState: async () => {}}), getLeavesOfType: () => window.testHost ? [{view: window.testHost.view}] : []}};
class Plugin {
  constructor() { this.app = app; }
  registerView(type, factory) { this.factory = factory; }
  registerMarkdownCodeBlockProcessor(language, processor) { window.codeBlocks = {...window.codeBlocks, [language]: processor}; }
  addStatusBarItem() { const el = document.body.createDiv(); window.statusBar = el; return el; } registerInterval() {}
  register() {} registerExtensions() {} addRibbonIcon() {} addCommand() {} registerEvent() {} addSettingTab() {}
  async loadData() { return window.pluginData || null; } async saveData(data) { window.pluginData = JSON.parse(JSON.stringify(data)); }
}
class FileView { constructor() { this.app = app; this.contentEl = document.body.createDiv(); this.contentEl.style = 'height:95vh;display:flex;flex-direction:column'; } }
window.module = {exports: {}};
window.require = name => { if (name !== 'obsidian') throw Error(name); return {Plugin, PluginSettingTab: class {}, AbstractInputSuggest: class {}, TFolder, FileView, MarkdownRenderChild, WorkspaceLeaf, requestUrl: async request => { window.requests = [...(window.requests || []), request]; return window.fakeApp(JSON.parse(request.body)); }, Modal: class {}, Setting: class {}, Notice: class {constructor(text) {notices.push(text);}}, TFile, normalizePath: path => path}; };
window.start = async () => {
  const plugin = new module.exports(); await plugin.onload();
  const file = await vault.create('Test.docuclick', JSON.stringify({format:'docuclick-diagram', version:1, canvas:{nodes:[],edges:[]}, flow:{nodes:[],edges:[]}}));
  const view = plugin.factory({}); view.file = file; await view.onOpen(); await view.onLoadFile(file);
  window.testHost = {plugin, view, file, files, folders, notices, vault};
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
  browser = spawn(browserPath, ["--headless=new", "--disable-gpu", "--disable-site-isolation-trials", "--no-first-run", "--no-default-browser-check", "--disable-background-networking", "--remote-debugging-port=0", `--user-data-dir=${profile}`, "about:blank"], {stdio: ["ignore", "ignore", "pipe"]});
  let browserLog = "", exitCode = null;
  browser.stderr.on("data", chunk => { browserLog = (browserLog + chunk).slice(-4000); });
  browser.on("exit", code => { exitCode = code; });
  // A cold runner can take well over 10 s for Chromium's first start.
  const portFile = path.join(profile, "DevToolsActivePort");
  for (let i = 0; i < 600 && exitCode === null && !fs.existsSync(portFile); i++) await delay(100);
  assert.ok(fs.existsSync(portFile), `Chromium did not start (exit code ${exitCode}):\n${browserLog}`);
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
  assert.equal(await evaluate("testHost.files.get('Test.docuclick').text.split('data:image/png').length - 1"), 1, "screenshot stored more than once");
  // Redaction: areas are stored on the screenshot (the original stays, so
  // they can be changed later) and every view shows the redacted version.
  const savedDoc = "JSON.parse(testHost.files.get('Test.docuclick').text)";
  const pixel = (src) => `(async () => { const c = document.createElement('canvas'); const i = new Image(); i.src = await (${src}); return i.decode().then(() => { c.width = i.naturalWidth; c.height = i.naturalHeight; const x = c.getContext('2d'); x.drawImage(i, 0, 0); return Array.from(x.getImageData(2, 2, 1, 1).data.slice(0, 3)).join(); }); })()`;
  await inFrame(`(() => {
    const big = document.createElement('canvas'); big.width = 400; big.height = 200; const g = big.getContext('2d'); g.fillStyle = '#fff'; g.fillRect(0, 0, 400, 200);
    for (let x = 0; x < 400; x += 4) { g.fillStyle = x % 8 ? '#000' : '#fff'; g.fillRect(x, 0, 4, 200); }
    window.bigUrl = big.toDataURL('image/png');
    cy.getElementById(imageId).data('imageUrl', bigUrl); imageNodes.find(n => n.data.id === imageId).data.imageUrl = bigUrl;
    docuclickSetRedactions(imageId, [{ x: 0, y: 0, w: 1, h: 1, mode: 'black' }]);
  })()`);
  await until(`${savedDoc}.canvas.nodes.find(n => n.type === 'file').docuClickRedactions?.length === 1`);
  assert.equal(await evaluate(`${savedDoc}.flow.nodes.find(n => n.data.imageUrl).data.imageUrl`), await inFrame("bigUrl"), "original must be kept in the file");
  assert.equal(await inFrame(pixel("bigUrl")), "255,255,255");
  await until("overlayImgs.get(imageId).src.startsWith('blob:')", frameContext);
  assert.equal(await inFrame(pixel("overlayImgs.get(imageId).src")), "0,0,0", "overview shows the original");
  assert.equal(await inFrame("cy.getElementById(imageId).data('imageUrl') === bigUrl"), true, "original must stay");
  // The editor in the image view: preview, add, select, change, delete.
  await inFrame("docuclickSetRedactions(imageId, []); openLightboxByNodeId(imageId); lightboxImg.decode()");
  await inFrame("document.getElementById('lightbox-redact-btn').click()");
  await until("!document.getElementById('redact-stage').hidden && document.getElementById('redact-canvas').width === 400", frameContext);
  const drag = (from, to) => inFrame(`(() => {
    const layer = document.getElementById('redact-boxes'), box = layer.getBoundingClientRect();
    const at = (type, fx, fy) => (type === 'pointerdown' ? document.elementFromPoint(box.left + fx * box.width, box.top + fy * box.height) : layer)
      .dispatchEvent(new PointerEvent(type, { clientX: box.left + fx * box.width, clientY: box.top + fy * box.height, bubbles: true, button: 0, pointerId: 1 }));
    at('pointerdown', ${from}); at('pointermove', ${to}); at('pointerup', ${to});
  })()`);
  await inFrame("document.getElementById('redact-tool-blur').click()");
  await drag("0.1, 0.1", "0.6, 0.9");
  await delay(100);
  assert.equal(await inFrame("document.querySelectorAll('.redact-box.selected .redact-handle').length"), 4);
  // Live preview: the blurred stripes turn grey before anything is saved.
  assert.notEqual(await inFrame("Array.from(document.getElementById('redact-canvas').getContext('2d').getImageData(100, 100, 1, 1).data.slice(0, 3)).join()"), "0,0,0");
  assert.equal(await evaluate(`${savedDoc}.canvas.nodes.find(n => n.type === 'file').docuClickRedactions`), undefined);
  // Move it, resize it from a corner, switch it to black.
  await drag("0.3, 0.5", "0.4, 0.5");
  await delay(50);
  await inFrame("(() => { const h = document.querySelector('.redact-handle.se'), r = h.getBoundingClientRect(), layer = document.getElementById('redact-boxes'), box = layer.getBoundingClientRect(); h.dispatchEvent(new PointerEvent('pointerdown', { clientX: r.left + 6, clientY: r.top + 6, bubbles: true, button: 0, pointerId: 1 })); for (const t of ['pointermove', 'pointerup']) layer.dispatchEvent(new PointerEvent(t, { clientX: box.left + box.width, clientY: box.top + box.height, bubbles: true, pointerId: 1 })); })()");
  await inFrame("document.getElementById('redact-tool-black').click(); document.getElementById('redact-done-btn').click()");
  await until(`${savedDoc}.canvas.nodes.find(n => n.type === 'file').docuClickRedactions?.length === 1`);
  const area = JSON.parse(await evaluate(`JSON.stringify(${savedDoc}.canvas.nodes.find(n => n.type === 'file').docuClickRedactions[0])`));
  assert.equal(area.mode, "black");
  assert.ok(Math.abs(area.x - 0.2) < 0.02 && Math.abs(area.y - 0.1) < 0.02 && area.x + area.w > 0.99 && area.y + area.h > 0.99, JSON.stringify(area));
  // Later: open again, select the area and remove it; Esc discards, "Fertig" stores.
  await inFrame("document.getElementById('lightbox-redact-btn').click()");
  await until("!document.getElementById('redact-stage').hidden", frameContext);
  await drag("0.5, 0.5", "0.5, 0.5");
  await inFrame("document.getElementById('redact-delete-btn').click(); document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))");
  assert.equal(await inFrame("lightbox.hidden + ':' + document.getElementById('redact-stage').hidden + ':' + redactionsOf(imageId).length"), "false:true:1");
  await inFrame("document.getElementById('lightbox-redact-btn').click()");
  await until("!document.getElementById('redact-stage').hidden", frameContext);
  await drag("0.5, 0.5", "0.5, 0.5");
  await inFrame("document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Delete', bubbles: true })); document.getElementById('redact-done-btn').click()");
  await until(`!${savedDoc}.canvas.nodes.find(n => n.type === 'file').docuClickRedactions`);
  // Undo brings the area back.
  await inFrame("restoreHistory(-1)");
  assert.equal(await inFrame("redactionsOf(imageId).length"), 1);
  await inFrame("closeLightbox(); docuclickSetRedactions(imageId, [{ x: 0, y: 0, w: 1, h: 1, mode: 'blur' }])");
  // Files that leave the editor carry the areas burnt in, never the original.
  assert.equal(await inFrame("docuclickBakedSnapshot().then(s => { const n = s.flow.nodes.find(n => n.data.id === imageId); return (n.data.imageUrl !== bigUrl) + ':' + n.data.redactionsBaked + ':' + !JSON.stringify(s).includes(bigUrl); })"), "true:1:true");
  const blurred = (await inFrame(pixel("docuclickBakedSnapshot().then(s => s.flow.nodes.find(n => n.data.id === imageId).data.imageUrl)"))).split(",").map(Number);
  assert.ok(blurred.every(v => v > 60 && v < 200), `blurred stripes should be grey: ${blurred}`);
  await inFrame("deleteNode(imageId)");
  await inFrame("restoreHistory(-1)");
  assert.equal(await inFrame("snapshot().flow.nodes.length"), 2);
  assert.equal(await inFrame("snapshot().canvas.edges.length"), 1);
  await evaluate("testHost.view.flush()");
  // One edit is validated once, although the editor sends both "change" and "save".
  await evaluate("window.validations = 0; const original = DocuClickDocument.validateDocument; DocuClickDocument.validateDocument = (...args) => { validations++; return original(...args); }");
  await inFrame("addManualElement(0, 500, 'rectangle', '#3b82f6', 'Einmal validiert')");
  await until("JSON.parse(testHost.files.get('Test.docuclick').text).canvas.nodes.some(n => n.text === 'Einmal validiert')");
  assert.equal(await evaluate("validations"), 1);
  await evaluate("DocuClickDocument.validateDocument = original");
  await inFrame("deleteNode(snapshot().flow.nodes.find(n => n.data.label === 'Einmal validiert').data.id)");
  await evaluate("testHost.view.flush()");
  // Default folder: nested folders are created; unsafe paths are rejected.
  assert.equal(await evaluate("testHost.plugin.targetFolder()"), "");
  await evaluate("testHost.plugin.settings.defaultFolder = 'Prozesse/Buchhaltung'; testHost.plugin.ensureFolder(testHost.plugin.targetFolder())");
  assert.equal(await evaluate("[...testHost.folders.keys()].join(',')"), "Prozesse,Prozesse/Buchhaltung");
  assert.equal(await evaluate("testHost.plugin.createUnique(testHost.plugin.targetFolder(), 'Neu', 'docuclick', '{}').then(f => f.path)"), "Prozesse/Buchhaltung/Neu.docuclick");
  assert.equal(await evaluate("JSON.stringify(['../x', 'a/../b', 'a:b', '/Prozesse/', ' '].map(cleanFolder))"), '[null,null,null,"Prozesse",""]');
  await evaluate("testHost.plugin.settings.defaultFolder = ''");
  // Colour settings restyle the open editor live (CSS and graph labels).
  await evaluate("testHost.plugin.settings.themeMode = 'custom'; testHost.plugin.settings.background = '#ffffff'; testHost.plugin.settings.accent = '#e11d48'; testHost.plugin.saveSettings()");
  for (let i = 0; i < 40 && await inFrame("getComputedStyle(document.documentElement).getPropertyValue('--bg-canvas').trim()") !== "#ffffff"; i++) await delay(50);
  assert.equal(await inFrame("getComputedStyle(document.documentElement).getPropertyValue('--accent').trim()"), "#e11d48");
  // Labels above screenshot cards follow the theme (plain shapes keep white text on their fill).
  assert.equal(await inFrame("cy.nodes('[imageUrl]').first().style('color')"), "rgb(15,23,42)");
  assert.equal(await evaluate("JSON.stringify(window.pluginData.themeMode)"), '"custom"');
  // Only the editor's own dock remains. Export is a separate viewer-only HTML.
  assert.equal(await evaluate("testHost.view.contentEl.querySelectorAll('button, .docuclick-toolbar').length"), 0);
  await inFrame("document.getElementById('export-readonly-btn').click()");
  await until("testHost.files.has('Test – Ansicht.html')");
  assert.equal(await evaluate("DocuClickDocument.importHtml(testHost.files.get('Test – Ansicht.html').text).flow.nodes.length"), 2);
  // The export has the blurred screenshot burnt in, not the original.
  assert.equal(await evaluate("DocuClickDocument.importHtml(testHost.files.get('Test – Ansicht.html').text).flow.nodes.find(n => n.data.imageUrl).data.redactionsBaked"), 1);
  assert.equal(await evaluate(`testHost.files.get('Test – Ansicht.html').text.includes(${JSON.stringify(await inFrame("bigUrl"))})`), false, "export contains the unredacted screenshot");
  assert.ok(await evaluate("testHost.files.get('Test – Ansicht.html').text.includes('--accent: #e11d48;')"), "Viewer export ignores the colour settings");
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
  assert.equal(await inViewer("[...document.querySelectorAll('.node-image-overlay')].every(i => i.src.startsWith('data:image/'))"), true, "viewer must show the baked image as is");
  const before = await inViewer("JSON.stringify(cy.elements().jsons())");
  await inViewer("cy.nodes().first().emit('cxttap'); document.dispatchEvent(new KeyboardEvent('keydown', {key:'Delete'})); document.dispatchEvent(new KeyboardEvent('keydown', {key:'z',ctrlKey:true})); document.dispatchEvent(new Event('paste')); document.dispatchEvent(new Event('drop'))");
  assert.equal(await inViewer("JSON.stringify(cy.elements().jsons())"), before);
  // Print / PDF of the guide: one block per step, then the browser's print dialog.
  assert.equal(await inViewer("typeof docuclickRedactImage + ':' + !!document.getElementById('lightbox-redact-btn')"), "undefined:false", "redaction must not be in the read-only view");
  await inViewer("window.printed = 0; window.print = () => { printed++; }; document.getElementById('guide-print-btn').click()");
  for (let i = 0; i < 40 && !(await inViewer("printed")); i++) await delay(50);
  assert.equal(await inViewer("printed"), 1);
  assert.equal(await inViewer("document.querySelectorAll('#print-guide .print-step').length"), 2);
  assert.equal(await inViewer("document.querySelectorAll('#print-guide .print-step img').length"), 1);
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
  // External change without local edits: the view reloads silently; own writes never do.
  const empty = JSON.stringify({format:'docuclick-diagram', version:1, canvas:{nodes:[],edges:[]}, flow:{nodes:[],edges:[]}});
  await evaluate(`(async () => {
    window.reloadFile = await testHost.vault.create('Reload.docuclick', ${JSON.stringify("")} + ${JSON.stringify("")} + '${empty}');
    window.reloadView = testHost.plugin.factory({}); reloadView.file = reloadFile; await reloadView.onOpen(); await reloadView.onLoadFile(reloadFile);
    window.leaves = [{view: reloadView}]; testHost.plugin.app.workspace.getLeavesOfType = () => leaves;
  })()`);
  const oldState = await evaluate("!!reloadView.state");
  assert.ok(oldState);
  await evaluate("window.firstState = reloadView.state; emitModify(reloadFile); new Promise(r => setTimeout(r, 100))");
  assert.equal(await evaluate("reloadView.state === firstState"), true, "own/unchanged file must not reload");
  const external = JSON.stringify({format:'docuclick-diagram', version:1, canvas:{nodes:[{id:'x',type:'text',text:'Extern',x:0,y:0,width:100,height:50}],edges:[]}, flow:{nodes:[{data:{id:'x',label:'Extern'},position:{x:0,y:0}}],edges:[]}});
  await evaluate(`window.firstFrame = reloadView.frame; testHost.files.get('Reload.docuclick').text = ${JSON.stringify(external)}; emitModify(reloadFile)`);
  await until("reloadView.state && reloadView.state.doc.canvas.nodes.length === 1");
  // Shown in the running editor (same frame: zoom and position stay), which now holds the new document.
  assert.equal(await evaluate("reloadView.frame === firstFrame"), true, "external change reloaded the editor");
  await evaluate("reloadView.flush()");
  assert.equal(await evaluate("reloadView.state.doc.flow.nodes.map(n => n.data.label).join()"), "Extern");
  assert.equal(await evaluate("testHost.files.get('Reload.docuclick').text"), external, "showing an external change must not rewrite the file");
  // With local changes at risk: a banner with a reload button instead of a silent reload.
  await evaluate("reloadView.state.blocked = true; window.blockedState = reloadView.state; testHost.files.get('Reload.docuclick').text = " + JSON.stringify(empty) + "; emitModify(reloadFile)");
  await until("!!reloadView.contentEl.querySelector('.docuclick-banner')");
  assert.equal(await evaluate("reloadView.state === blockedState"), true);
  await evaluate("reloadView.contentEl.querySelector('.docuclick-banner button').click()");
  await until("reloadView.state !== blockedState && reloadView.state.doc.canvas.nodes.length === 0 && !reloadView.contentEl.querySelector('.docuclick-banner')");
  // A view that changed nothing does not rewrite the file (here: unusual formatting stays as is).
  await evaluate("reloadView.flush()");
  assert.equal(await evaluate("testHost.files.get('Reload.docuclick').text"), empty);
  // Screenshots as vault files (optional): stored once by checksum, restored on load, hostile paths ignored.
  const shot = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==";
  await evaluate(`(async () => {
    const plugin = testHost.plugin; plugin.settings.imageStorage = 'attachments'; plugin.settings.imageFolder = 'Bilder/DocuClick';
    const doc = DocuClickDocument.emptyDocument();
    for (const id of ['a', 'b']) {
      doc.canvas.nodes.push({id, type:'text', text:id, x:id === 'a' ? 0 : 300, y:0, width:200, height:60}, {id:'f'+id, type:'file', x:id === 'a' ? 0 : 300, y:70, width:200, height:100});
      doc.flow.nodes.push({data:{id, label:id, imageUrl:'${shot}'}, position:{x:id === 'a' ? 0 : 300, y:0}});
    }
    window.attachDoc = DocuClickDocument.validateDocument(doc);
    window.attachText = JSON.stringify(await plugin.storageDoc(null, DocuClickDocument.compactForStorage(attachDoc)), null, 2);
    window.attachLoaded = await plugin.loadDocument(attachText);
  })()`);
  assert.equal(await evaluate("attachText.includes('data:image')"), false, "image data left in the diagram file");
  assert.equal(await evaluate("Object.keys(JSON.parse(attachText).images).length"), 2);
  assert.equal(await evaluate("[...testHost.files.keys()].filter(p => p.startsWith('Bilder/DocuClick/')).length"), 1, "identical images must be stored once");
  assert.equal(await evaluate("JSON.stringify(attachLoaded) === JSON.stringify(attachDoc)"), true, "round trip changed the document");
  // A live view saves through the same path.
  await evaluate("reloadView.state.pristineKey = null; reloadView.enqueueSave(reloadView.state, DocuClickDocument.compactForStorage(attachDoc))");
  assert.equal(await evaluate("JSON.parse(testHost.files.get('Reload.docuclick').text).images.b.startsWith('Bilder/DocuClick/')"), true);
  // Without an own image folder, screenshots follow Obsidian's attachment setting (same rule as the apps).
  await evaluate("testHost.vault.getConfig = key => key === 'attachmentFolderPath' ? window.attachSetting : undefined; window.noteFile = new (testHost.vault.getAbstractFileByPath('Test.docuclick').constructor)('Prozesse/IT/Ablauf X.md')");
  for (const [setting, expected] of [[undefined, "Ablauf X"], ["/", "Ablauf X"], ["./", "Prozesse/IT/Ablauf X"], ["./Attachments", "Prozesse/IT/Attachments/Ablauf X"], ["Anhänge", "Anhänge/Ablauf X"], ["../raus", "Ablauf X"]]) {
    assert.equal(await evaluate(`window.attachSetting = ${JSON.stringify(setting)}; testHost.plugin.attachmentFolderFor(noteFile)`), expected, `attachment setting ${setting}`);
  }
  // Moving/renaming screenshots in Obsidian updates every diagram that uses them (open ones via their save queue).
  await evaluate(`(async () => {
    testHost.files.set('Moved.docuclick', {file: new (testHost.vault.getAbstractFileByPath('Test.docuclick').constructor)('Moved.docuclick'), text: attachText});
    window.untouched = testHost.files.get('Test.docuclick').text;
    window.leavesOfType = testHost.plugin.app.workspace.getLeavesOfType;
    testHost.plugin.app.workspace.getLeavesOfType = () => [{view: reloadView}];
    testHost.notices.length = 0;
    const { TFolder, TFile } = window.require('obsidian');
    await testHost.plugin.followRename(new TFolder('Bilder/Screens'), 'Bilder/DocuClick');
    window.afterFolder = { closed: Object.values(JSON.parse(testHost.files.get('Moved.docuclick').text).images), open: Object.values(JSON.parse(testHost.files.get('Reload.docuclick').text).images) };
    const old = afterFolder.closed[0];
    await testHost.plugin.followRename(new TFile('Bilder/Screens/neu.png'), old);
    window.afterFile = Object.values(JSON.parse(testHost.files.get('Moved.docuclick').text).images);
    testHost.plugin.app.workspace.getLeavesOfType = leavesOfType;
  })()`);
  assert.equal(await evaluate("afterFolder.closed.every(p => p.startsWith('Bilder/Screens/')) && afterFolder.closed.length === 2"), true, "closed diagram not updated");
  assert.equal(await evaluate("afterFolder.open.every(p => p.startsWith('Bilder/Screens/'))"), true, "open diagram not updated");
  assert.equal(await evaluate("afterFile.every(p => p === 'Bilder/Screens/neu.png')"), true, "renamed image not followed");
  assert.equal(await evaluate("testHost.files.get('Test.docuclick').text === untouched"), true, "unrelated diagram rewritten");
  assert.ok(await evaluate("testHost.notices.some(n => n.includes('Bildpfade in 2 Diagrammen'))"));
  // Missing files and paths outside the image rules are dropped with a notice, never read.
  await evaluate("testHost.notices.length = 0; testHost.files.set('Geheim.txt', {file: new (testHost.vault.getAbstractFileByPath('Test.docuclick').constructor)('Geheim.txt'), text: 'x'})");
  const hostile = await evaluate(`(async () => { const raw = JSON.parse(attachText); raw.images.a = '../Geheim.txt'; raw.images.b = 'Geheim.txt'; return (await testHost.plugin.loadDocument(JSON.stringify(raw))).flow.nodes.map(n => !!n.data.imageUrl).join(); })()`);
  assert.equal(hostile, "false,false");
  assert.ok(await evaluate("testHost.notices.some(n => n.includes('Bilddatei'))"));
  await evaluate("testHost.plugin.settings.imageStorage = 'embedded'");
  // A diagram note recorded by the DocuClick apps: one Markdown file; screenshots stay vault
  // files (also with "In der Datei"); own text in the note survives every save.
  const recording = fs.readFileSync(path.join(__dirname, "fixtures/app-recording.md"), "utf8");
  await evaluate(`(async () => {
    const raw = ${JSON.stringify(recording)};
    const bytes = Uint8Array.from(atob('${shot.split(",")[1]}'), c => c.charCodeAt(0));
    for (const file of Object.values(JSON.parse(DocuClickDocument.noteData(raw)).images)) await testHost.vault.createBinary(file, bytes.buffer);
    window.appFile = await testHost.vault.create('Prozesse/Ablauf.md', raw);
    window.appView = testHost.plugin.factory({}); appView.file = appFile; await appView.onOpen(); await appView.onLoadFile(appFile);
    window.leaves = [{view: appView}];
  })()`);
  assert.equal(await evaluate("appView.state.doc.flow.nodes.filter(n => n.data.imageUrl).length"), 2, "recorded screenshots not loaded");
  // Typing in the note's text (other pane) does not reload the diagram tab.
  await evaluate("window.appState = appView.state; testHost.files.get('Prozesse/Ablauf.md').text = testHost.files.get('Prozesse/Ablauf.md').text.replace('## Schritte', 'Eigene Notiz.\\n\\n## Schritte'); emitModify(appFile)");
  await delay(150);
  assert.equal(await evaluate("appView.state === appState"), true, "text edit reloaded the diagram");
  // An edit in the diagram: steps and data replaced, own text kept.
  await evaluate(`(() => {
    const doc = structuredClone(appView.state.doc);
    const first = doc.canvas.nodes.find(n => n.type === 'text'); first.text = 'Umbenannt im Diagramm';
    doc.flow.nodes.find(n => n.data.id === first.id).data.label = first.text;
    // As drainInbox does for an edit from the editor frame.
    appView.state.pristineKey = null; appView.state.doc = DocuClickDocument.validateDocument(doc);
    return appView.enqueueSave(appView.state, DocuClickDocument.compactForStorage(appView.state.doc));
  })()`);
  const saved = await evaluate("testHost.files.get('Prozesse/Ablauf.md').text");
  assert.ok(D.isDiagramNote(saved) && saved.includes("Eigene Notiz.") && saved.includes("1. Umbenannt im Diagramm\n2. Abzweigung:"), saved.slice(0, 600));
  assert.equal(saved.includes("data:image"), false, "recorded screenshots were embedded into the note");
  assert.deepEqual(JSON.parse(D.noteData(saved)).images, JSON.parse(D.noteData(recording)).images);
  assert.equal(saved.split(D.DATA_START).length, 2);
  // The app records another step (diagram data changes): the open tab shows it.
  const nextData = JSON.parse(D.noteData(saved)); nextData.canvas.nodes.find(n => n.type === "text").text = "Nächster Klick";
  const nextNote = D.composeNote(saved, D.noteJson(nextData), D.stepsMarkdown(nextData));
  await evaluate(`window.appState = appView.state; testHost.files.get('Prozesse/Ablauf.md').text = ${JSON.stringify(nextNote)}; emitModify(appFile)`);
  await until("appView.state && appView.state.doc.canvas.nodes.some(n => n.text === 'Nächster Klick')");
  // Opening a diagram note shows the full diagram tab; a tab switched to text stays text.
  await evaluate(`(async () => {
    const leaf = view => ({ view, setViewState: async state => { leaf.last = state; } });
    const markdown = file => { const l = { view: { file, getViewType: () => 'markdown', addAction() { l.actions = (l.actions || 0) + 1; } }, setViewState: async state => { l.state = state; } }; return l; };
    window.plain = await testHost.vault.create('Notiz.md', '# Nur eine Notiz');
    window.mdLeaf = markdown(appFile); window.plainLeaf = markdown(plain);
    testHost.plugin.app.workspace.getLeavesOfType = type => type === 'markdown' ? [mdLeaf, plainLeaf] : leaves;
    await testHost.plugin.showDiagramNotes();
  })()`);
  assert.equal(await evaluate("mdLeaf.state?.type + ':' + mdLeaf.state?.state.file"), "docuclick-diagram:Prozesse/Ablauf.md");
  assert.equal(await evaluate("plainLeaf.state"), undefined, "an ordinary note must stay a note");
  await evaluate("mdLeaf.state = undefined; testHost.plugin.textLeaves.set(mdLeaf, appFile.path); testHost.plugin.showDiagramNotes()");
  assert.equal(await evaluate("mdLeaf.state === undefined && mdLeaf.actions === 1"), true, "tab switched to text must stay text, with a way back");
  await evaluate("testHost.plugin.app.workspace.getLeavesOfType = type => type === 'markdown' ? [] : leaves");
  // Without a flash of text: a leaf asked to show a (indexed) diagram note as Markdown shows the diagram right away.
  assert.equal(await evaluate("(async () => { const leaf = new (require('obsidian').WorkspaceLeaf)(); await leaf.setViewState({type: 'markdown', state: {file: 'Prozesse/Ablauf.md'}}); return leaf.state.type; })()"), "docuclick-diagram");
  assert.equal(await evaluate("(async () => { const leaf = new (require('obsidian').WorkspaceLeaf)(); await leaf.setViewState({type: 'markdown', state: {file: 'Notiz.md'}}); return leaf.state.type; })()"), "markdown");
  // "Als Notiz anzeigen": text in reading mode, and the patch lets that through.
  assert.equal(await evaluate("(async () => { const leaf = new (require('obsidian').WorkspaceLeaf)(); leaf.view = {getViewType: () => 'docuclick-diagram', addAction() {}}; await testHost.plugin.toggleView(leaf, appFile); return leaf.state.type + ':' + leaf.state.state.mode; })()"), "markdown:preview");
  // Embedding a diagram note in another note: read-only viewer, follows changes.
  await evaluate("window.embedEl = document.body.createDiv(); codeBlocks.docuclick('[[Prozesse/Ablauf.md]]', embedEl, {sourcePath: 'Uebersicht.md', addChild: child => child.onload()})");
  for (let i = 0; i < 40 && !await evaluate("!!embedEl.querySelector('iframe.docuclick-embed-frame')"); i++) await delay(50);
  assert.ok(await evaluate("!!embedEl.querySelector('iframe.docuclick-embed-frame')"), await evaluate("embedEl.innerText"));
  const embedded = await evaluate("embedEl.querySelector('iframe').srcdoc");
  assert.ok(embedded.includes("Nächster Klick") && embedded.includes("Content-Security-Policy"));
  assert.ok(!embedded.includes('id="add-element-btn"'), "embedded diagram must be read-only");
  assert.equal(await evaluate("embedEl.querySelector('.docuclick-embed-header span').textContent"), "Ablauf");
  await evaluate("window.missingEl = document.body.createDiv(); codeBlocks.docuclick('Notiz.md', missingEl, {sourcePath: 'X.md', addChild: child => child.onload()})");
  await until("!!missingEl.querySelector('.docuclick-error')");
  await evaluate("embedEl.remove(); missingEl.remove()");
  // New and imported flows are diagram notes; an old .docuclick file is converted, the old file trashed.
  assert.ok(D.isDiagramNote(await evaluate("testHost.plugin.fileText({extension: 'md'}, null, DocuClickDocument.emptyDocument())")));
  await evaluate("testHost.plugin.importText(testHost.files.get('Test – Ansicht.html').text, 'Import', '')");
  assert.equal(await evaluate("JSON.parse(DocuClickDocument.noteData(testHost.files.get('Import.md').text)).flow.nodes.length"), 2);
  await evaluate("testHost.plugin.app.fileManager = { trashFile: async file => testHost.files.delete(file.path) }; testHost.plugin.convertToNote(testHost.vault.getAbstractFileByPath('Reload.docuclick'))");
  assert.equal(await evaluate("testHost.files.has('Reload.docuclick')"), false);
  assert.equal(await evaluate("DocuClickDocument.isDiagramNote(testHost.files.get('Reload.md').text) && JSON.parse(DocuClickDocument.noteData(testHost.files.get('Reload.md').text)).images.b.startsWith('Bilder/DocuClick/')"), true);
  // Screenshots of deleted steps: found (only in DocuClick's folders), linked images are kept.
  await evaluate(`(async () => {
    const bytes = new Uint8Array([137, 80, 78, 71]).buffer;
    for (const path of ['Prozesse/Attachments/Ablauf/999999_000.png', 'Fotos/Urlaub.png', 'Prozesse/Attachments/Ablauf/verlinkt.png']) await testHost.vault.createBinary(path, bytes);
    testHost.plugin.app.metadataCache.resolvedLinks = { 'Notiz.md': { 'Prozesse/Attachments/Ablauf/verlinkt.png': 1 } };
  })()`);
  assert.equal(await evaluate("testHost.plugin.findUnusedImages().then(list => list.map(f => f.path).join())"), "Prozesse/Attachments/Ablauf/999999_000.png");
  // Recording with the DocuClick app: not paired, paired, app not running.
  assert.match(await evaluate("testHost.plugin.startRecording(appFile).then(() => 'ok', e => e.message)"), /einmal eine Aufnahme in diesem Vault starten/);
  await evaluate(`testHost.files.set('.obsidian/plugins/docuclick-diagrams/app-link.json', { file: null, text: JSON.stringify({ port: 47811, token: 'geheim' }) });
    window.fakeApp = body => ({ status: 200, json: { ok: true, message: 'Aufnahme läuft.', recording: body.action !== 'pause', paused: body.action === 'pause', file: body.file || '/vault/Prozesse/Ablauf.md' } });`);
  await evaluate("testHost.plugin.startRecording(appFile)");
  assert.deepEqual(JSON.parse(await evaluate("requests.at(-1).body")), { token: "geheim", action: "start", file: "/vault/Prozesse/Ablauf.md" });
  assert.equal(await evaluate("requests.at(-1).url + ' ' + requests.at(-1).contentType"), "http://127.0.0.1:47811/control application/json");
  assert.match(await evaluate("statusBar.textContent"), /DocuClick nimmt auf: Ablauf/);
  await evaluate("testHost.plugin.remote('branch', { name: 'Fehlerfall' })");
  assert.equal(await evaluate("JSON.parse(requests.at(-1).body).name"), "Fehlerfall");
  await evaluate("testHost.plugin.toggleRecording(appFile)"); // same file recording -> pause
  assert.equal(await evaluate("JSON.parse(requests.at(-1).body).action"), "pause");
  assert.match(await evaluate("statusBar.textContent"), /pausiert/);
  await evaluate("window.fakeApp = () => { throw new Error('ECONNREFUSED'); }");
  assert.match(await evaluate("testHost.plugin.remote('status').then(() => 'ok', e => e.message)"), /läuft nicht/);
  assert.equal(await evaluate("statusBar.textContent"), "");
  assert.deepEqual(errors, [], "Uncaught browser errors");
  console.log("PASS: sandbox, palette, default folder, attachment folder + rename following, colour themes, rename, undo/redo, image move, connect/delete/restore, Vault save, read-only HTML export/search/guide/lightbox, forged message rejection, conflict recovery, close flush, app recording in the vault, diagram notes (open as tab, own text kept, embed, convert)");
})().catch(error => { console.error(error); process.exitCode=1; }).finally(async () => {
  socket?.close(); browser?.kill(); server.close();
  await delay(300);
  fs.rmSync(profile, {recursive:true, force:true});
});
