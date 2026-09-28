/* Pure data boundary: imported HTML is parsed as text, never executed. */
const FORMAT = "docuclick-diagram";
const MAX_BYTES = 64 * 1024 * 1024;
const SHAPES = new Set(["round-rectangle", "rectangle", "ellipse", "diamond", "rhomboid", "tag", "round-tag", "cut-rectangle"]);
const imageUri = value => typeof value === "string" && /^data:image\/(png|jpeg|jpg|webp|gif|bmp);base64,[a-z0-9+/=\s]+$/i.test(value);
function check(condition, message) { if (!condition) throw new Error(message); }
function finite(value) { check(Number.isFinite(value) && Math.abs(value) <= 1e8, "Ungültige Diagrammkoordinate."); return value; }
function label(value) { check(typeof value === "string" && value.length <= 100000, "Ungültiger Text."); return value; }
function id(value) { check(typeof value === "string" && /^[a-zA-Z0-9_-]{1,160}$/.test(value), "Ungültige Knoten-ID."); return value; }
function color(value) { return typeof value === "string" && /^#[0-9a-f]{6}$/i.test(value) ? value : "#3b82f6"; }
function safeJson(value) { return JSON.stringify(value).replace(/</g, "\\u003c").replace(/>/g, "\\u003e").replace(/&/g, "\\u0026"); }
function escapeHtml(value) { return String(value).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]); }

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
    return result;
  });
  const renderedIds = new Set();
  const rendered = flow.nodes.map((n, index) => {
    const nodeId = id(n.data?.id);
    check(textIds.has(nodeId) && !renderedIds.has(nodeId), "Inkonsistente Diagrammknoten."); renderedIds.add(nodeId);
    const data = { id: nodeId, label: label(n.data.label || ""), color: color(n.data.color), shape: SHAPES.has(n.data.shape) ? n.data.shape : "round-rectangle", stepIndex: index + 1 };
    if (n.data.imageUrl) { check(imageUri(n.data.imageUrl), "Bilder müssen in der HTML-Datei eingebettet sein (PNG/JPEG/WebP/GIF/BMP)."); data.imageUrl = n.data.imageUrl; }
    return { data, position: { x: finite(n.position?.x), y: finite(n.position?.y) } };
  });
  check(renderedIds.size === textIds.size, "Die Diagrammansicht enthält nicht alle Textknoten.");
  const pairs = new Set();
  const edges = canvas.edges.map((e, index) => {
    check(textIds.has(e.fromNode) && textIds.has(e.toNode), "Verbindung verweist auf fehlenden Knoten.");
    const pair = `${e.fromNode}->${e.toNode}`;
    check(!pairs.has(pair), "Doppelte Verbindung."); pairs.add(pair);
    return { id: `edge-${index}`, fromNode: e.fromNode, toNode: e.toNode, fromSide: "bottom", toSide: "top", docuClickManual: !!e.docuClickManual, color: color(e.color || flow.edges.find(x => x.data?.source === e.fromNode && x.data?.target === e.toNode)?.data?.color), lineStyle: ["solid", "dashed", "dotted"].includes(e.lineStyle) ? e.lineStyle : "solid" };
  });
  // Rebuild edges from the document, never trust a second conflicting graph.
  const renderedEdges = edges.map(e => ({ data: { id: `${e.docuClickManual ? "manual-" : ""}${e.fromNode}->${e.toNode}`, source: e.fromNode, target: e.toNode, color: e.color, manual: e.docuClickManual, lineStyle: e.lineStyle } }));
  // Preserve imported screenshots even after the original attachment folder is gone.
  for (const n of rendered) {
    const text = nodes.find(x => x.id === n.data.id);
    text.x = n.position.x; text.y = n.position.y;
    if (n.data.imageUrl) {
      const sibling = nodes.find(x => x.type === "file" && Math.abs(x.x - text.x) < .5 && Math.abs(x.y - text.y - 70) < .5);
      if (sibling) sibling.file = n.data.imageUrl;
    }
  }
  return { format: FORMAT, version: 1, canvas: { nodes, edges }, flow: { nodes: rendered, edges: renderedEdges } };
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
module.exports = { FORMAT, MAX_BYTES, validateDocument, parseDocument, importHtml, emptyDocument, buildHtml, safeJson, themeCss };
