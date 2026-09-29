const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const D = require("../src/document.js");
const template = fs.readFileSync(path.join(__dirname, "../../core/DocuClick.Core/WebAssets/viewer.template.html"), "utf8");
const png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==";
function fixture() {
  const doc = D.emptyDocument();
  doc.canvas.nodes.push({ id: "a", type: "text", text: "Schritt", x: 10, y: 20, width: 200, height: 60 }, { id: "image", type: "file", file: "Attachments/Test.png", x: 10, y: 90, width: 200, height: 100 });
  doc.flow.nodes.push({ data: { id: "a", label: "Schritt", imageUrl: png, color: "#123456", shape: "rectangle" }, position: { x: 10, y: 20 } });
  return doc;
}
test("HTML round trip preserves screenshots, full text, and literal script delimiters", () => {
  const doc = fixture();
  doc.canvas.nodes[0].text = '</script><script>throw new Error("injected")</script>\n@@FLOW@@';
  doc.flow.nodes[0].data.label = doc.canvas.nodes[0].text;
  const html = D.buildHtml(template, "/* bundled vendor */", doc, "A < B @@FLOW@@");
  const imported = D.importHtml(html);
  assert.equal(imported.canvas.nodes[0].text, doc.canvas.nodes[0].text);
  assert.equal(imported.flow.nodes[0].data.imageUrl, png);
  assert.equal(imported.canvas.nodes[1].file, png);
  assert.ok(html.includes("A &lt; B @@FLOW@@"));
  assert.ok(!html.includes('<script>throw new Error("injected")'));
});
test("unknown versions, duplicate nodes and dangling edges are rejected", () => {
  assert.throws(() => D.parseDocument(JSON.stringify({ ...fixture(), version: 2 })), /Version/);
  const duplicate = fixture(); duplicate.canvas.nodes.push(duplicate.canvas.nodes[0]);
  assert.throws(() => D.validateDocument(duplicate), /Doppelte/);
  const dangling = fixture(); dangling.canvas.edges.push({ fromNode: "a", toNode: "missing" });
  assert.throws(() => D.validateDocument(dangling), /fehlenden/);
});
test("import strips save tokens and filesystem paths and refuses remote/SVG images", () => {
  const doc = fixture(); doc.canvas.docuClickSaveToken = "old-secret";
  assert.equal(D.validateDocument(doc).canvas.docuClickSaveToken, undefined);
  doc.canvas.nodes[1].file = "../../private.txt"; delete doc.flow.nodes[0].data.imageUrl;
  assert.equal(D.validateDocument(doc).canvas.nodes[1].file, undefined);
  for (const url of ["https://example.com/pixel", "file:///etc/passwd", "data:image/svg+xml;base64,AAAA"]) {
    doc.flow.nodes[0].data.imageUrl = url;
    assert.throws(() => D.validateDocument(doc), /eingebettet/);
  }
});
test("import parses data without executing surrounding HTML", () => {
  const html = D.buildHtml(template, "throw new Error('never execute')", fixture(), "Test");
  assert.equal(D.importHtml(html).flow.nodes.length, 1);
  assert.throws(() => D.importHtml("<html>Not a diagram</html>"), /unterstützte/);
});
test("exported editor scripts parse as JavaScript and all template keys resolve", () => {
  const html = D.buildHtml(template, "/* vendor */", D.emptyDocument(), "Test");
  assert.ok(!/@@[A-Z_]+@@/.test(html));
  for (const script of html.matchAll(/<script>([\s\S]*?)<\/script>/g)) new vm.Script(script[1]);
  // The template's own "Kopie herunterladen" patches flowData with this
  // pattern; freshly built files must still match it (and be importable).
  assert.match(html, /const flowData = \{[\s\S]*?\};\s*const cy = cytoscape\(/);
  assert.equal(D.importHtml(html).flow.nodes.length, 0);
});
test("edge styles survive import/export and graph endpoints come from the document", () => {
  const doc = fixture();
  doc.canvas.nodes.push({ id: "b", type: "text", text: "Ende", x: 300, y: 0, width: 100, height: 80 });
  doc.flow.nodes.push({ data: { id: "b", label: "Ende" }, position: { x: 300, y: 0 } });
  doc.canvas.edges.push({ fromNode: "a", toNode: "b", color: "#ff0000", lineStyle: "dashed", docuClickManual: true });
  const result = D.importHtml(D.buildHtml(template, "", doc, "Test"));
  assert.equal(result.flow.edges[0].data.lineStyle, "dashed");
  assert.equal(result.flow.edges[0].data.color, "#ff0000");
  assert.equal(result.flow.edges[0].data.target, "b");
});
module.exports = { fixture };

test("viewer export omits editor code and UI but preserves data, search, guide and images", () => {
  const doc = fixture();
  const html = D.buildHtml(template, "/* vendor */", doc, "Ansicht", { readOnly: true });
  for (const absent of ['id="add-element-btn"', 'id="dock-save-group"', 'id="rename-modal"', 'id="element-modal"', 'id="context-menu"', 'id="node-handles"', 'id="canvas-drop-overlay"', "function scheduleSave", "function addManualElement", "function deleteNode", "function restoreHistory", "127.0.0.1", "docuclickEditorHost"]) {
    assert.ok(!html.includes(absent), `Viewer still contains ${absent}`);
  }
  for (const present of ['id="search-input"', 'id="guide-toggle-btn"', 'id="zoom-in-btn"', 'id="lightbox"', png]) assert.ok(html.includes(present));
  for (const script of html.matchAll(/<script>([\s\S]*?)<\/script>/g)) new vm.Script(script[1]);
  assert.equal(D.importHtml(html).flow.nodes[0].data.imageUrl, png);
  // The normal editor still has its tools after generating a viewer.
  assert.ok(D.buildHtml(template, "", doc, "Editor").includes("function addManualElement"));
});

test("themes derive readable colours and are injected after the template's own styles", () => {
  const dark = D.themeCss({ background: "#1e1e1e", accent: "#7c3aed" });
  assert.match(dark, /--text-main: #f8fafc;/);
  assert.match(dark, /color-scheme: dark;/);
  const light = D.themeCss({ background: "#ffffff", accent: "#e11d48" });
  assert.match(light, /--text-main: #0f172a;/);
  assert.match(light, /--graph-label-text: #0f172a;/);
  assert.match(light, /--bg-canvas: #ffffff;/);
  assert.equal(D.themeCss(null), "");
  for (const bad of [{ background: "red", accent: "#000000" }, { background: "#000000", accent: "#000;}body{x:1" }]) {
    assert.throws(() => D.themeCss(bad), /Theme-Farbe/);
  }
  const html = D.buildHtml(template, "", fixture(), "Test", { theme: { background: "#ffffff", accent: "#e11d48" } });
  assert.ok(html.indexOf('<style id="docuclick-theme">') > html.indexOf("--bg-canvas: #090d16"));
  assert.ok(html.indexOf('<style id="docuclick-theme">') < html.indexOf("</head>"));
  assert.ok(!D.buildHtml(template, "", fixture(), "Test").includes("docuclick-theme"));
  // Viewer exports keep the theme too.
  assert.ok(D.buildHtml(template, "", fixture(), "Test", { readOnly: true, theme: { background: "#ffffff", accent: "#e11d48" } }).includes("--accent: #e11d48;"));
});

test("edge colour falls back to the first matching flow edge and screenshots stay attached", () => {
  const doc = fixture();
  doc.canvas.nodes.push({ id: "b", type: "text", text: "Ende", x: 300, y: 0, width: 100, height: 80 });
  doc.flow.nodes.push({ data: { id: "b", label: "Ende" }, position: { x: 300, y: 0 } });
  doc.canvas.edges.push({ fromNode: "a", toNode: "b" });
  doc.flow.edges.push({ data: { source: "a", target: "b", color: "#00ff00" } }, { data: { source: "a", target: "b", color: "#ff0000" } });
  delete doc.canvas.nodes[1].file;
  const result = D.validateDocument(doc);
  assert.equal(result.flow.edges[0].data.color, "#00ff00");
  assert.equal(result.canvas.nodes[1].file, png);
});

test("large diagrams validate quickly (no quadratic lookups)", () => {
  const doc = D.emptyDocument(), steps = 5000;
  for (let i = 0; i < steps; i++) {
    doc.canvas.nodes.push({ id: `n${i}`, type: "text", text: `S${i}`, x: i * 10, y: i * 10, width: 200, height: 60 }, { id: `f${i}`, type: "file", x: i * 10, y: i * 10 + 70, width: 200, height: 100 });
    doc.flow.nodes.push({ data: { id: `n${i}`, label: `S${i}`, imageUrl: png }, position: { x: i * 10, y: i * 10 } });
    if (i) doc.canvas.edges.push({ fromNode: `n${i - 1}`, toNode: `n${i}` });
  }
  const start = Date.now(), result = D.validateDocument(doc);
  assert.ok(Date.now() - start < 2000, "validation too slow");
  assert.equal(result.canvas.nodes.filter(n => n.file === png).length, steps);
});

test("storage form keeps each screenshot once and restores it on load", () => {
  const doc = D.validateDocument(fixture());
  doc.canvas.nodes.find(n => n.type === "file").y = 90;
  const stored = D.compactForStorage(D.validateDocument(doc));
  assert.equal(stored.canvas.nodes.find(n => n.type === "file").file, undefined);
  assert.equal(stored.flow.nodes[0].data.imageUrl, png);
  assert.ok(JSON.stringify(stored).length < JSON.stringify(doc).length - png.length + 10);
  assert.deepEqual(D.parseDocument(JSON.stringify(stored)), D.validateDocument(doc));
});

test("storage form leaves screenshots alone that cannot be rebuilt", () => {
  const doc = fixture();
  doc.canvas.nodes[1].file = png; doc.canvas.nodes[1].y = 500;
  const valid = D.validateDocument(doc);
  const stored = D.compactForStorage(valid);
  assert.equal(stored, valid);
});
test("a diagram recorded by the DocuClick apps is a valid plugin document", () => {
  // Written by the C# writer (DocuClickDiagramTests regenerates it), so both sides share one format.
  const raw = JSON.parse(fs.readFileSync(path.join(__dirname, "fixtures/app-recording.docuclick"), "utf8"));
  const doc = D.validateDocument(raw);
  assert.equal(doc.flow.nodes.length, 4);
  assert.equal(doc.flow.edges.length, 3);
  // Screenshots are vault files the plugin loads via "images", keyed by step id.
  const ids = new Set(doc.flow.nodes.map(n => n.data.id));
  assert.equal(Object.keys(raw.images).length, 2);
  for (const [id, file] of Object.entries(raw.images)) {
    assert.ok(ids.has(id));
    assert.match(file, /^Prozesse\/Attachments\/Ablauf\/\d{6}_\d{3}\.png$/);
  }
  assert.ok(!JSON.stringify(raw).includes("base64"));
  assert.ok(D.buildHtml(template, "/* vendor */", doc, "Aufnahme").includes("Linksklick auf „Anmelden“"));
});
