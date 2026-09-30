/* Pure data boundary: imported HTML is parsed as text, never executed. */
const FORMAT = "docuclick-diagram";
const MAX_BYTES = 64 * 1024 * 1024;
const SHAPES = new Set(["round-rectangle", "rectangle", "ellipse", "diamond", "rhomboid", "tag", "round-tag", "cut-rectangle"]);
const imageUri = value => typeof value === "string" && /^data:image\/(png|jpeg|jpg|webp|gif|bmp);base64,[a-z0-9+/=\s]+$/i.test(value);
function check(condition, message) { if (!condition) throw new Error(message); }
function finite(value) { check(Number.isFinite(value) && Math.abs(value) <= 1e8, "Ungültige Diagrammkoordinate."); return value; }
function label(value) { check(typeof value === "string" && value.length <= 100000, "Ungültiger Text."); return value; }
function id(value) { check(typeof value === "string" && /^[a-zA-Z0-9_-]{1,160}$/.test(value), "Ungültige Knoten-ID."); return value; }
/** Blacked-out / blurred areas of a screenshot (fractions of the image), see the template. */
function redactions(value) {
  if (!Array.isArray(value)) return [];
  const unit = v => Number.isFinite(v) ? Math.min(1, Math.max(0, v)) : 0;
  return value.slice(0, 500).filter(a => a && typeof a === "object")
    .map(a => ({ x: unit(a.x), y: unit(a.y), w: unit(a.w), h: unit(a.h), mode: a.mode === "blur" ? "blur" : "black" }))
    .filter(a => a.w > 0 && a.h > 0);
}
function color(value) { return typeof value === "string" && /^#[0-9a-f]{6}$/i.test(value) ? value : "#3b82f6"; }
function safeJson(value) { return JSON.stringify(value).replace(/</g, "\\u003c").replace(/>/g, "\\u003e").replace(/&/g, "\\u0026"); }
function escapeHtml(value) { return String(value).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]); }

// File nodes bucketed by rounded position; the .5 tolerance is checked on candidates.
function fileIndex(nodes) {
  const at = new Map();
  for (const x of nodes) if (x.type === "file") { const key = `${Math.round(x.x)},${Math.round(x.y)}`; (at.get(key) || at.set(key, []).get(key)).push(x); }
  // The screenshot card of a step sits 70 units below its text node.
  return (x, y) => {
    for (const dx of [-1, 0, 1]) for (const dy of [-1, 0, 1]) {
      const hit = at.get(`${Math.round(x) + dx},${Math.round(y + 70) + dy}`)?.find(f => Math.abs(f.x - x) < .5 && Math.abs(f.y - y - 70) < .5);
      if (hit) return hit;
    }
  };
}
function validateDocument(input) {
  check(input?.format === FORMAT && input.version === 1, "Unbekanntes Diagrammformat oder neuere Version.");
  const canvas = input.canvas, flow = input.flow;
  check(Array.isArray(canvas?.nodes) && Array.isArray(canvas.edges) && Array.isArray(flow?.nodes) && Array.isArray(flow.edges), "Knoten oder Verbindungen fehlen.");
  check(canvas.nodes.length <= 30000 && canvas.edges.length <= 50000 && flow.nodes.length <= 10000 && flow.edges.length <= 50000, "Diagramm ist zu groß.");
  const canvasIds = new Set(), textIds = new Set();
  const nodes = canvas.nodes.map(n => {
    const nodeId = id(n.id);
    check(!canvasIds.has(nodeId), "Doppelte Knoten-ID."); canvasIds.add(nodeId);
    check(["text", "file", "group"].includes(n.type), "Unbekannter Knotentyp.");
    if (n.type === "text") textIds.add(nodeId);
    const result = { id: nodeId, type: n.type, x: finite(n.x), y: finite(n.y), width: finite(n.width), height: finite(n.height) };
    check(result.width >= 0 && result.height >= 0, "Negative Knotengröße.");
    if (n.text != null) result.text = label(n.text);
    if (n.label != null) result.label = label(n.label);
    if (n.color) result.color = color(n.color);
    if (n.shape) result.shape = SHAPES.has(n.shape) ? n.shape : "round-rectangle";
    // The plugin never reads a path supplied by imported content.
    if (imageUri(n.file)) result.file = n.file;
    const areas = n.type === "file" ? redactions(n.docuClickRedactions) : [];
    if (areas.length) result.docuClickRedactions = areas;
    return result;
  });
  const renderedIds = new Set();
  const rendered = flow.nodes.map((n, index) => {
    const nodeId = id(n.data?.id);
    check(textIds.has(nodeId) && !renderedIds.has(nodeId), "Inkonsistente Diagrammknoten."); renderedIds.add(nodeId);
    const data = { id: nodeId, label: label(n.data.label || ""), color: color(n.data.color), shape: SHAPES.has(n.data.shape) ? n.data.shape : "round-rectangle", stepIndex: index + 1 };
    if (n.data.imageUrl) { check(imageUri(n.data.imageUrl), "Bilder müssen in der HTML-Datei eingebettet sein (PNG/JPEG/WebP/GIF/BMP)."); data.imageUrl = n.data.imageUrl; }
    // How many of the screenshot's areas imageUrl already contains (shared/exported files).
    if (data.imageUrl && Number.isInteger(n.data.redactionsBaked) && n.data.redactionsBaked > 0) data.redactionsBaked = Math.min(n.data.redactionsBaked, 500);
    return { data, position: { x: finite(n.position?.x), y: finite(n.position?.y) } };
  });
  check(renderedIds.size === textIds.size, "Die Diagrammansicht enthält nicht alle Textknoten.");
  const pairs = new Set();
  const flowEdgeColors = new Map();
  for (const x of flow.edges) { const key = `${x.data?.source}->${x.data?.target}`; if (x.data && !flowEdgeColors.has(key)) flowEdgeColors.set(key, x.data.color); }
  const edges = canvas.edges.map((e, index) => {
    check(textIds.has(e.fromNode) && textIds.has(e.toNode), "Verbindung verweist auf fehlenden Knoten.");
    const pair = `${e.fromNode}->${e.toNode}`;
    check(!pairs.has(pair), "Doppelte Verbindung."); pairs.add(pair);
    return { id: `edge-${index}`, fromNode: e.fromNode, toNode: e.toNode, fromSide: "bottom", toSide: "top", docuClickManual: !!e.docuClickManual, color: color(e.color || flowEdgeColors.get(pair)), lineStyle: ["solid", "dashed", "dotted"].includes(e.lineStyle) ? e.lineStyle : "solid" };
  });
  // Rebuild edges from the document, never trust a second conflicting graph.
  const renderedEdges = edges.map(e => ({ data: { id: `${e.docuClickManual ? "manual-" : ""}${e.fromNode}->${e.toNode}`, source: e.fromNode, target: e.toNode, color: e.color, manual: e.docuClickManual, lineStyle: e.lineStyle } }));
  // Preserve imported screenshots even after the original attachment folder is gone.
  const nodeById = new Map(nodes.map(x => [x.id, x]));
  const siblingOf = fileIndex(nodes);
  for (const n of rendered) {
    const text = nodeById.get(n.data.id);
    text.x = n.position.x; text.y = n.position.y;
    if (n.data.imageUrl) {
      const sibling = siblingOf(text.x, text.y);
      if (sibling) sibling.file = n.data.imageUrl;
    }
  }
  return { format: FORMAT, version: 1, canvas: { nodes, edges }, flow: { nodes: rendered, edges: renderedEdges } };
}
/**
 * Storage form: a screenshot is kept once (flow imageUrl); the duplicate on the
 * canvas file node is left out when validateDocument can rebuild it from the
 * flow node next to it (same position rule), so nothing is lost on reload.
 */
function compactForStorage(doc) {
  const siblingOf = fileIndex(doc.canvas.nodes), rebuilt = new Set();
  for (const n of doc.flow.nodes) {
    const sibling = n.data.imageUrl && siblingOf(n.position.x, n.position.y);
    if (sibling && sibling.file === n.data.imageUrl) rebuilt.add(sibling);
  }
  if (!rebuilt.size) return doc;
  return { ...doc, canvas: { ...doc.canvas, nodes: doc.canvas.nodes.map(x => { if (!rebuilt.has(x)) return x; const { file, ...rest } = x; return rest; }) } };
}
function parseDocument(text) {
  check(typeof text === "string" && text.length <= MAX_BYTES, "Datei ist zu groß.");
  return validateDocument(JSON.parse(text));
}
function importHtml(html) {
  check(typeof html === "string" && html.length <= MAX_BYTES, "HTML-Datei ist zu groß.");
  const data = html.match(/<script\s+id="docuclick-data"\s+type="application\/json">([\s\S]*?)<\/script>/i);
  // flowData is single-line JSON (older files: directly followed by the
  // cytoscape() call; newer ones may have code in between).
  const graph = html.match(/const flowData = (\{.*?\});\r?\n/) || html.match(/const flowData = (\{[\s\S]*?\});\s*const cy = cytoscape\(/);
  check(data && graph, "Keine unterstützte DocuClick-HTML-Datei. Bitte mit einer aktuellen DocuClick-Version speichern.");
  return validateDocument({ format: FORMAT, version: 1, canvas: JSON.parse(data[1]), flow: JSON.parse(graph[1]) });
}
function emptyDocument() { return { format: FORMAT, version: 1, canvas: { nodes: [], edges: [] }, flow: { nodes: [], edges: [] } }; }
// ---- Colour themes -------------------------------------------------------
// Derives every theme variable of the shared template (see its :root) from
// two colours, so a light background automatically gets dark text etc.
const HEX = /^#[0-9a-f]{6}$/i;
const rgb = hex => { const n = parseInt(hex.slice(1), 16); return [n >> 16 & 255, n >> 8 & 255, n & 255]; };
const toHex = c => "#" + c.map(v => Math.round(Math.max(0, Math.min(255, v))).toString(16).padStart(2, "0")).join("");
const mix = (a, b, t) => a.map((v, i) => v + (b[i] - v) * t);
const triplet = c => c.map(v => Math.round(v)).join(", ");
function luminance(c) {
  const [r, g, b] = c.map(v => { v /= 255; return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4; });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}
function themeCss(theme) {
  if (!theme) return "";
  check(HEX.test(theme.background) && HEX.test(theme.accent), "Ungültige Theme-Farbe.");
  const bg = rgb(theme.background), accent = rgb(theme.accent), black = [0, 0, 0], white = [255, 255, 255];
  const light = luminance(bg) > 0.4;
  const text = rgb(light ? "#0f172a" : "#f8fafc");
  const tint = light ? "15, 23, 42" : "255, 255, 255";
  const panel = mix(bg, text, 0.04), panelAlt = mix(bg, text, 0.08);
  const vars = {
    "color-scheme": light ? "light" : "dark",
    "--bg-canvas": toHex(bg),
    "--surface-glass": `rgba(${triplet(panelAlt)}, 0.82)`,
    "--surface-glass-hover": `rgba(${triplet(mix(bg, text, 0.14))}, 0.9)`,
    "--surface-card": toHex(mix(bg, text, 0.06)),
    "--border-glass": `rgba(${tint}, ${light ? 0.16 : 0.12})`,
    "--border-subtle": `rgba(${tint}, ${light ? 0.09 : 0.06})`,
    "--accent": toHex(accent),
    "--accent-hover": toHex(mix(accent, black, 0.15)),
    "--accent-glow": `rgba(${triplet(accent)}, 0.4)`,
    "--accent-rgb": triplet(accent),
    "--accent-text": toHex(light ? mix(accent, black, 0.25) : mix(accent, white, 0.3)),
    "--on-accent": luminance(accent) > 0.45 ? "#0f172a" : "#ffffff",
    "--brand-end": toHex(mix(accent, black, 0.2)),
    "--text-main": toHex(text),
    "--text-sub": light ? "#475569" : "#94a3b8",
    "--text-muted": "#64748b",
    "--shadow-dock": light ? "0 10px 30px -5px rgba(15, 23, 42, 0.18), 0 0 0 1px rgba(15, 23, 42, 0.08)" : "0 10px 30px -5px rgba(0, 0, 0, 0.5), 0 0 0 1px rgba(255, 255, 255, 0.1)",
    "--shadow-card": light ? "0 8px 24px -4px rgba(15, 23, 42, 0.15)" : "0 8px 24px -4px rgba(0, 0, 0, 0.4)",
    "--tint": tint,
    "--shade": light ? "255, 255, 255" : "0, 0, 0",
    "--panel": triplet(panel),
    "--panel-drawer": triplet(panel),
    "--text-strong": toHex(text),
    "--primary-btn-text": luminance(accent) > 0.45 ? "#0f172a" : "#ffffff",
    "--panel-alt": triplet(panelAlt),
    "--input-bg": toHex(light ? mix(bg, white, 0.6) : mix(bg, black, 0.2)),
    "--swatch-ring": toHex(text),
    "--graph-label-text": toHex(text),
    "--graph-label-bg": toHex(panelAlt),
    "--graph-label-border": `rgba(${tint}, 0.16)`,
    "--graph-node-border": `rgba(${tint}, 0.18)`,
  };
  return `:root {\n${Object.entries(vars).map(([key, value]) => `  ${key}: ${value};`).join("\n")}\n}\n`;
}
function buildHtml(template, cytoscape, document, title, { readOnly = false, theme = null } = {}) {
  const doc = validateDocument(document);
  if (readOnly) {
    // Strip the actual editor before inserting any user data. Viewer exports
    // contain no mutation handlers or save bridge, not just hidden buttons.
    template = template
      .replace(/<!-- DOCUCLICK-EDITOR-START -->[\s\S]*?<!-- DOCUCLICK-EDITOR-END -->/g, "")
      .replace(/\/\/ DOCUCLICK-EDITOR-START\r?\n[\s\S]*?\/\/ DOCUCLICK-EDITOR-END/g, "");
  }
  // Theme overrides go last in <head>, after the template's own :root.
  const css = themeCss(theme);
  if (css) template = template.replace("</head>", () => `<style id="docuclick-theme">\n${css}</style>\n</head>`);
  const values = { TITLE: escapeHtml(title), NODE_COUNT: String(doc.flow.nodes.length), EDGE_COUNT: String(doc.flow.edges.length), DOCUMENT: `<script id="docuclick-data" type="application/json">${safeJson(doc.canvas)}</script>`, CYTOSCAPE: cytoscape, FLOW: safeJson(doc.flow), SAVE_PORT: "47811" };
  return template.replace(/@@([A-Z_]+)@@/g, (_, key) => { check(key in values, `Unbekannter Vorlagenwert: ${key}`); return values[key]; });
}
// ---- Step list for Markdown notes -----------------------------------------
// A note about a flow embeds the diagram and carries the steps as plain text,
// so Obsidian's search, links and backlinks find a flow by what happens in it.
const DECISION_POINT = "◆ Abzweigung", PATH_START = "↳ Pfad: ";
const STEPS_START = "%% DocuClick: Schritte werden aus dem Diagramm erzeugt, Änderungen hier gehen verloren. %%";
const STEPS_END = "%% DocuClick: Ende der Schritte %%";
/** Step text safe for a Markdown list item: one line, no tags, links or formatting. */
function markdownText(value) {
  return String(value || "").replace(/\s+/g, " ").trim().replace(/[\\`*_[\]#<>|~=$^%]/g, c => "\\" + c) || "(ohne Beschreibung)";
}
/**
 * The flow as nested Markdown lists: the main line numbered, each path of a
 * decision point as its own sub-list. Manual cross-connections are left out
 * (they are references, not the order of steps).
 * Keep in sync with DiagramNote.Steps (C#, core/DocuClick.Core/Services/
 * DiagramNote.cs): the apps write the same list. tests/fixtures/app-recording.md
 * is an app recording; document.test.js checks both produce identical text.
 */
function stepsMarkdown(document) {
  const doc = validateDocument(document);
  const texts = new Map(doc.canvas.nodes.filter(n => n.type === "text").map(n => [n.id, n]));
  const children = new Map(), hasParent = new Set();
  for (const e of doc.canvas.edges) {
    if (e.docuClickManual) continue;
    (children.get(e.fromNode) || children.set(e.fromNode, []).get(e.fromNode)).push(e.toNode);
    hasParent.add(e.toNode);
  }
  const byPosition = (a, b) => texts.get(a).x - texts.get(b).x || texts.get(a).y - texts.get(b).y;
  for (const list of children.values()) list.sort(byPosition);
  const lines = [], seen = new Set();
  // Next steps after a node: one = same list, several = one sub-list per branch.
  const continueWith = (next, depth) => {
    if (next.length === 1) { chain(next[0], depth); return; }
    for (const branch of next) {
      if ((texts.get(branch).text || "").startsWith(PATH_START)) chain(branch, depth);
      else if (!seen.has(branch)) { lines.push(`${"\t".repeat(depth)}- **Weiter mit:**`); chain(branch, depth + 1); }
    }
  };
  const chain = (start, depth) => {
    let number = 1, id = start;
    while (id && !seen.has(id)) {
      seen.add(id);
      const text = texts.get(id).text || "", next = children.get(id) || [], indent = "\t".repeat(depth);
      if (text.startsWith(PATH_START)) {
        lines.push(`${indent}- **Pfad: ${markdownText(text.slice(PATH_START.length))}**`);
        continueWith(next, depth + 1);
        return;
      }
      if (text === DECISION_POINT || next.length > 1) {
        lines.push(`${indent}${number++}. ${text === DECISION_POINT ? "Abzweigung:" : `${markdownText(text)}, dann je nach Fall:`}`);
        continueWith(next, depth + 1);
        return;
      }
      lines.push(`${indent}${number++}. ${markdownText(text)}`);
      id = next[0];
    }
  };
  const roots = [...texts.keys()].filter(id => !hasParent.has(id)).sort((a, b) => texts.get(a).y - texts.get(b).y || texts.get(a).x - texts.get(b).x);
  roots.forEach((root, index) => { if (index) lines.push(""); chain(root, 0); });
  // Anything only reachable through a cycle.
  for (const id of texts.keys()) if (!seen.has(id)) { lines.push(""); chain(id, 0); }
  return lines.length ? lines.join("\n") : "_Noch keine Schritte._";
}
/** Replaces the generated section of a note; null if the note has none (then it is left alone). */
function replaceSteps(note, steps) {
  const start = note.indexOf(STEPS_START), end = note.indexOf(STEPS_END, start + 1);
  if (start < 0 || end < 0) return null;
  return `${note.slice(0, start)}${STEPS_START}\n${steps}\n${note.slice(end)}`;
}
// ---- Diagram notes -------------------------------------------------------
// A flow is one Markdown note: own text, the generated step list and the
// diagram data in a hidden %% comment. The plugin opens it as a diagram tab.
const NOTE_PROPERTY = "diagramm";
const DATA_START = "%% DocuClick-Diagrammdaten (nicht von Hand ändern)";
/** True if the note's properties mark it as a DocuClick diagram (`docuclick: diagramm`). */
function isDiagramNote(text) {
  const front = /^---\r?\n([\s\S]*?)\r?\n---/.exec(String(text || ""));
  return !!front && /^docuclick:\s*["']?diagramm["']?\s*$/m.test(front[1]);
}
/** The diagram JSON inside a note, or null. */
function noteData(text) {
  const start = text.indexOf(DATA_START);
  if (start < 0) return null;
  const end = text.indexOf("\n%%", start + DATA_START.length);
  return end < 0 ? null : text.slice(start + DATA_START.length, end).trim();
}
/** JSON for the data block; "%" escaped so it can never close the comment early. */
function noteJson(doc) { return JSON.stringify(doc, null, 2).replace(/%/g, "\\u0025"); }
/**
 * The note text with this diagram: a new note, or `previous` with only the
 * step list and the data block replaced (own text and properties stay).
 */
function composeNote(previous, dataJson, steps) {
  const data = `${DATA_START}\n${dataJson}\n%%`;
  if (!previous || !previous.trim()) {
    return `---\ndocuclick: ${NOTE_PROPERTY}\n---\n\n## Schritte\n\n${STEPS_START}\n${steps}\n${STEPS_END}\n\n${data}\n`;
  }
  let text = replaceSteps(previous, steps) ?? previous;
  const start = text.indexOf(DATA_START), end = start < 0 ? -1 : text.indexOf("\n%%", start + DATA_START.length);
  return end < 0 ? `${text.replace(/\s*$/, "")}\n\n${data}\n` : `${text.slice(0, start)}${data}${text.slice(end + 3)}`;
}
module.exports = { stepsMarkdown, replaceSteps, isDiagramNote, noteData, noteJson, composeNote, DATA_START, STEPS_START, STEPS_END, compactForStorage, FORMAT, MAX_BYTES, validateDocument, parseDocument, importHtml, emptyDocument, buildHtml, safeJson, themeCss };
