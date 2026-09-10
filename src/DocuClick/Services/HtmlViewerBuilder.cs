using System.IO;
using System.Text.Json;

namespace DocuClick.Services;

/// <summary>
/// Builds the actual interactive HTML page (Cytoscape.js graph, pan/zoom,
/// click-to-enlarge screenshot) shared by two different callers with two
/// different needs: <see cref="CanvasFlowWriter"/>'s live, on-disk session
/// file (relative image paths, embeds the raw node/edge JSON so it can be
/// read back and edited again — see <see cref="CanvasDocumentIo"/> — and,
/// only when that data block is present, ships its own drag-to-move/
/// shift-drag-to-connect editing and a File System Access API save-back, so
/// the page can edit and persist itself with no DocuClick or Obsidian
/// running) and <see cref="HtmlFlowExporter"/>'s one-shot, fully
/// self-contained share export (base64-embedded images, no embedded data
/// block since it's never read back or edited). Keeping one shared template
/// means a rendering fix (like the row-spacing bug already found and fixed
/// once) only ever needs making in one place.
/// </summary>
public static class HtmlViewerBuilder
{
    // Lazily read once and cached for the process's lifetime — this file is
    // ~365 KB, and BuildPage runs on *every single click* while a live
    // session is recording (CanvasFlowWriter.Save() calls it every time);
    // re-reading it from disk that often was a real, measured per-click
    // cost completely independent of session size. The vendor file never
    // changes at runtime, so there's nothing to invalidate this cache for.
    private static readonly Lazy<string> CytoscapeJs = new(() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "WebAssets", "vendor", "cytoscape.min.js")));

    public sealed record NodeSpec(string Id, string Label, double X, double Y, string Color, string Shape, string? ImageSrc);

    public sealed record EdgeSpec(string Source, string Target, string Color, bool Manual);

    /// <param name="embeddedDataJson">
    /// When non-null, embedded verbatim in a `&lt;script id="docuclick-data"
    /// type="application/json"&gt;` block for <see cref="CanvasDocumentIo.Load"/>
    /// to read back later — omitted entirely for a read-only export that's
    /// never re-opened for editing.
    /// </param>
    public static string BuildPage(string title, IReadOnlyList<NodeSpec> nodes, IReadOnlyList<EdgeSpec> edges, string? embeddedDataJson = null)
    {
        var jsNodes = nodes.Select((n, idx) => new
        {
            data = new { id = n.Id, label = n.Label, color = n.Color, shape = n.Shape, imageUrl = n.ImageSrc, stepIndex = idx + 1 },
            position = new { x = n.X, y = n.Y },
        });
        var jsEdges = edges.Select(e => new
        {
            data = new
            {
                id = $"{(e.Manual ? "manual-" : "")}{e.Source}->{e.Target}",
                source = e.Source,
                target = e.Target,
                color = e.Color,
                manual = e.Manual,
            },
        });
        // WhenWritingNull: a node without a screenshot (decision points,
        // path-start markers) must omit "imageUrl" entirely rather than
        // serialize it as null — Cytoscape's `[imageUrl]` selector matches a
        // data field that's merely *present*, null value and all, which
        // would wrongly create an image overlay (src "null") for it too.
        var jsonOptions = new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
        var dataJson = JsonSerializer.Serialize(new { nodes = jsNodes, edges = jsEdges }, jsonOptions);
        var cytoscapeJs = CytoscapeJs.Value;
        // type="application/json" is never executed by the browser — purely
        // inert data storage, safe to embed regardless of where it sits.
        // System.Text.Json's default encoder already escapes '<'/'>' inside
        // string values (e.g. a description containing literal "</script>"),
        // so this can never prematurely close its own tag.
        var embeddedBlock = embeddedDataJson is null
            ? ""
            : $"\n<script id=\"docuclick-data\" type=\"application/json\">\n{embeddedDataJson}\n</script>";

        var nodeCount = nodes.Count;
        var edgeCount = edges.Count;

        return $$"""
            <!DOCTYPE html>
            <html lang="de">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <title>{{System.Net.WebUtility.HtmlEncode(title)}} – Ablauf</title>
            <style>
              :root {
                --bg-canvas: #090d16;
                --surface-glass: rgba(15, 23, 42, 0.78);
                --surface-glass-hover: rgba(30, 41, 59, 0.9);
                --surface-card: #141b2d;
                --border-glass: rgba(255, 255, 255, 0.12);
                --border-subtle: rgba(255, 255, 255, 0.06);
                --accent: #3b82f6;
                --accent-hover: #2563eb;
                --accent-glow: rgba(59, 130, 246, 0.4);
                --text-main: #f8fafc;
                --text-sub: #94a3b8;
                --text-muted: #64748b;
                --success: #10b981;
                --warning: #f59e0b;
                --radius-sm: 6px;
                --radius-md: 10px;
                --radius-lg: 14px;
                --radius-full: 9999px;
                --shadow-dock: 0 10px 30px -5px rgba(0, 0, 0, 0.5), 0 0 0 1px rgba(255, 255, 255, 0.1);
                --shadow-card: 0 8px 24px -4px rgba(0, 0, 0, 0.4);
                --font-stack: system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI Variable Text", "Segoe UI", Inter, Roboto, sans-serif;
              }

              *, *::before, *::after { box-sizing: border-box; }

              html, body {
                margin: 0; padding: 0; width: 100%; height: 100%;
                background: var(--bg-canvas);
                font-family: var(--font-stack);
                overflow: hidden; user-select: none; color: var(--text-main);
              }

              #cy {
                width: 100%; height: 100%;
                background-color: var(--bg-canvas);
                background-image: radial-gradient(rgba(255, 255, 255, 0.08) 1.2px, transparent 1.2px);
                background-size: 24px 24px;
              }

              #image-overlays { position: fixed; inset: 0; pointer-events: none; overflow: hidden; z-index: 2; }
              .node-image-overlay {
                position: absolute; object-fit: cover; border-radius: 8px;
                pointer-events: none; box-shadow: 0 4px 14px rgba(0, 0, 0, 0.4);
              }

              #node-handles { position: fixed; inset: 0; pointer-events: none; overflow: hidden; z-index: 1000; }
              .node-handle {
                position: absolute; width: 14px; height: 14px; margin: -7px;
                border-radius: 50%; background: var(--success); border: 2px solid #ffffff;
                cursor: crosshair; pointer-events: auto;
                box-shadow: 0 0 10px rgba(16, 185, 129, 0.6), 0 2px 4px rgba(0, 0, 0, 0.4);
                opacity: 0; transition: opacity 0.15s, transform 0.15s;
              }
              .node-handle.visible { opacity: 1; }
              .node-handle:hover { transform: scale(1.3); background: #059669; }

              #connect-line { position: fixed; inset: 0; width: 100%; height: 100%; pointer-events: none; z-index: 999; }
              #connect-line line { stroke: var(--success); stroke-width: 2.5; stroke-dasharray: 6 4; }

              /* Top Brand & Stats Bar */
              .brand-bar {
                position: fixed; top: 16px; left: 16px; z-index: 50;
                display: flex; align-items: center; gap: 10px;
                padding: 7px 14px;
                background: var(--surface-glass);
                backdrop-filter: blur(16px);
                -webkit-backdrop-filter: blur(16px);
                border: 1px solid var(--border-glass);
                border-radius: var(--radius-md);
                box-shadow: var(--shadow-dock);
                pointer-events: auto;
              }
              .brand-badge {
                background: linear-gradient(135deg, #3b82f6, #6366f1);
                color: #fff; font-size: 10px; font-weight: 800;
                letter-spacing: 0.05em; text-transform: uppercase;
                padding: 2px 7px; border-radius: var(--radius-sm);
              }
              .brand-title {
                font-weight: 600; font-size: 13px; color: var(--text-main);
                max-width: 280px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
              }
              .brand-meta {
                display: flex; align-items: center; gap: 6px; margin-left: 4px;
              }
              .meta-chip {
                background: rgba(255, 255, 255, 0.07);
                padding: 2px 8px; border-radius: var(--radius-full);
                font-size: 11px; font-weight: 500; color: var(--text-sub);
              }

              /* Floating Action Dock */
              .floating-dock {
                position: fixed; bottom: 20px; left: 50%; transform: translateX(-50%); z-index: 60;
                display: flex; align-items: center; gap: 6px;
                padding: 6px 10px;
                background: var(--surface-glass);
                backdrop-filter: blur(20px);
                -webkit-backdrop-filter: blur(20px);
                border: 1px solid var(--border-glass);
                border-radius: var(--radius-full);
                box-shadow: var(--shadow-dock);
              }
              .dock-btn {
                display: flex; align-items: center; justify-content: center; gap: 6px;
                height: 32px; padding: 0 10px;
                background: transparent; color: var(--text-main);
                border: none; border-radius: var(--radius-full);
                font-family: inherit; font-size: 12px; font-weight: 600;
                cursor: pointer; transition: background 0.15s, transform 0.1s;
              }
              .dock-btn:hover { background: rgba(255, 255, 255, 0.12); }
              .dock-btn:active { transform: scale(0.96); }
              .dock-btn-primary { background: var(--accent); }
              .dock-btn-primary:hover { background: var(--accent-hover); }
              .dock-btn-highlight { background: rgba(59, 130, 246, 0.18); color: #60a5fa; }
              .dock-btn-highlight:hover { background: rgba(59, 130, 246, 0.3); }
              .dock-sep { width: 1px; height: 18px; background: var(--border-glass); margin: 0 3px; }

              .search-wrapper {
                display: flex; align-items: center; gap: 6px;
                padding: 0 8px; background: rgba(0, 0, 0, 0.35);
                border-radius: var(--radius-full); border: 1px solid var(--border-subtle);
              }
              .search-input {
                background: transparent; border: none; outline: none;
                color: var(--text-main); font-family: inherit; font-size: 12px;
                width: 130px; transition: width 0.2s; padding: 4px 0;
              }
              .search-input:focus { width: 180px; }
              .search-input::placeholder { color: var(--text-muted); }
              .search-badge {
                font-size: 10px; font-weight: 700; color: #38bdf8;
                background: rgba(56, 189, 248, 0.15); padding: 1px 6px; border-radius: var(--radius-full);
              }

              .dock-save-group { display: flex; align-items: center; gap: 6px; }
              .save-status { font-size: 11px; color: var(--text-sub); margin: 0 4px; white-space: nowrap; }

              /* SOP Guide Drawer */
              .guide-drawer {
                position: fixed; top: 0; right: 0; bottom: 0; width: 360px; max-width: 85vw;
                background: rgba(11, 15, 25, 0.9);
                backdrop-filter: blur(24px);
                -webkit-backdrop-filter: blur(24px);
                border-left: 1px solid var(--border-glass);
                box-shadow: -10px 0 40px rgba(0, 0, 0, 0.6);
                z-index: 80; display: flex; flex-direction: column;
                transform: translateX(100%); transition: transform 0.3s cubic-bezier(0.16, 1, 0.3, 1);
              }
              .guide-drawer.open { transform: translateX(0); }
              .guide-header {
                padding: 16px 18px; display: flex; align-items: center; justify-content: space-between;
                border-bottom: 1px solid var(--border-subtle);
              }
              .guide-title-box { display: flex; align-items: center; gap: 8px; }
              .guide-badge {
                background: var(--accent); color: #fff; font-size: 10px; font-weight: 800;
                padding: 2px 6px; border-radius: var(--radius-sm);
              }
              .guide-title { font-size: 14px; font-weight: 700; margin: 0; color: var(--text-main); }
              .icon-btn {
                width: 28px; height: 28px; border-radius: 50%; border: none;
                background: rgba(255, 255, 255, 0.08); color: var(--text-sub);
                cursor: pointer; display: flex; align-items: center; justify-content: center;
                transition: background 0.15s, color 0.15s;
              }
              .icon-btn:hover { background: rgba(255, 255, 255, 0.18); color: #fff; }
              .guide-list {
                flex: 1; overflow-y: auto; padding: 12px 14px; display: flex; flex-direction: column; gap: 8px;
              }
              .guide-card {
                display: flex; gap: 10px; padding: 8px 10px; border-radius: var(--radius-md);
                background: rgba(255, 255, 255, 0.03); border: 1px solid var(--border-subtle);
                cursor: pointer; transition: background 0.15s, border-color 0.15s, transform 0.1s;
              }
              .guide-card:hover {
                background: rgba(255, 255, 255, 0.08); border-color: var(--border-glass);
                transform: translateX(-2px);
              }
              .guide-card-head { display: flex; gap: 8px; flex: 1; min-width: 0; align-items: flex-start; }
              .guide-card-num {
                display: flex; align-items: center; justify-content: center;
                width: 22px; height: 22px; border-radius: 50%;
                background: var(--accent); font-size: 10px; font-weight: 800; flex-shrink: 0;
              }
              .guide-card-text {
                font-size: 11.5px; font-weight: 600; line-height: 1.35;
                color: var(--text-main); overflow: hidden; text-overflow: ellipsis;
                display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical;
              }
              .guide-card-thumb {
                width: 58px; height: 42px; border-radius: 6px; object-fit: cover;
                background: #000; flex-shrink: 0; border: 1px solid var(--border-subtle);
              }

              /* Lightbox Modal */
              #lightbox {
                position: fixed; inset: 0; z-index: 1000;
                background: rgba(8, 12, 20, 0.88);
                backdrop-filter: blur(18px);
                -webkit-backdrop-filter: blur(18px);
                display: flex; flex-direction: column; align-items: center; justify-content: center;
              }
              .lightbox-frame {
                position: relative; max-width: 92vw; max-height: 84vh;
                display: flex; flex-direction: column; align-items: center;
              }
              #lightbox-img {
                max-width: 92vw; max-height: 76vh; object-fit: contain;
                border-radius: var(--radius-md); box-shadow: 0 20px 50px rgba(0, 0, 0, 0.8);
                border: 1px solid var(--border-glass);
              }
              .lightbox-nav {
                position: fixed; top: 50%; transform: translateY(-50%);
                width: 44px; height: 44px; border-radius: 50%;
                background: var(--surface-glass); border: 1px solid var(--border-glass);
                color: #fff; font-size: 24px; font-weight: bold;
                display: flex; align-items: center; justify-content: center;
                cursor: pointer; transition: background 0.15s, transform 0.1s;
                user-select: none;
              }
              .lightbox-nav:hover:not(:disabled) { background: var(--accent); transform: translateY(-50%) scale(1.08); }
              .lightbox-nav:disabled { opacity: 0.3; cursor: default; }
              .lightbox-nav.prev { left: 24px; }
              .lightbox-nav.next { right: 24px; }
              .lightbox-bar {
                margin-top: 14px; display: flex; align-items: center; gap: 12px;
                padding: 7px 16px; border-radius: var(--radius-full);
                background: var(--surface-glass); border: 1px solid var(--border-glass);
                font-size: 12px; color: var(--text-sub);
              }
              .lightbox-step-badge {
                background: var(--accent); color: #fff; font-weight: 700;
                padding: 2px 8px; border-radius: var(--radius-full); font-size: 11px;
              }
              .lightbox-title { font-weight: 600; color: var(--text-main); }
              .lightbox-close-btn {
                position: fixed; top: 20px; right: 24px; width: 36px; height: 36px;
                border-radius: 50%; background: var(--surface-glass); border: 1px solid var(--border-glass);
                color: #fff; font-size: 16px; cursor: pointer; display: flex; align-items: center; justify-content: center;
                transition: background 0.15s;
              }
              .lightbox-close-btn:hover { background: rgba(255, 255, 255, 0.2); }

              [hidden] { display: none !important; }
            </style>
            </head>
            <body>
            <div id="cy"></div>
            <div id="image-overlays"></div>
            <div id="node-handles"></div>
            <svg id="connect-line" hidden><line x1="0" y1="0" x2="0" y2="0"/></svg>

            <!-- Brand / Stats Bar -->
            <header class="brand-bar">
              <div class="brand-badge">DocuClick</div>
              <div class="brand-title">{{System.Net.WebUtility.HtmlEncode(title)}}</div>
              <div class="brand-meta">
                <span class="meta-chip">{{nodeCount}} Schritte</span>
                <span class="meta-chip">{{edgeCount}} Verbindungen</span>
              </div>
            </header>

            <!-- Floating Action Dock -->
            <nav class="floating-dock">
              <div class="search-wrapper">
                <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2"><circle cx="11" cy="11" r="8"/><line x1="21" y1="21" x2="16.65" y2="16.65"/></svg>
                <input id="search-input" class="search-input" type="search" placeholder="Ablauf durchsuchen..." autocomplete="off">
                <span id="search-count" class="search-badge" hidden>0/0</span>
              </div>
              <div class="dock-sep"></div>
              <button id="zoom-in-btn" class="dock-btn" title="Vergrößern">+</button>
              <button id="zoom-out-btn" class="dock-btn" title="Verkleinern">−</button>
              <button id="fit-btn" class="dock-btn" title="Ansicht einpassen (Fit)">⛶</button>
              <div class="dock-sep"></div>
              <button id="guide-toggle-btn" class="dock-btn dock-btn-highlight" title="Schritt-für-Schritt-Guide ein-/ausblenden">
                <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2"><path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20"/><path d="M6.5 2H20v20H6.5A2.5 2.5 0 0 1 4 19.5v-15A2.5 2.5 0 0 1 6.5 2z"/></svg>
                <span>Anleitung</span>
              </button>
              <div id="dock-save-group" class="dock-save-group" hidden>
                <div class="dock-sep"></div>
                <span id="save-status" class="save-status">nicht verbunden</span>
                <button id="connect-btn" class="dock-btn dock-btn-primary" type="button">Mit Datei verbinden</button>
                <button id="download-btn" class="dock-btn" type="button" title="Aktuelle Änderungen herunterladen">Herunterladen</button>
              </div>
            </nav>

            <!-- SOP Guide Drawer -->
            <aside id="guide-drawer" class="guide-drawer">
              <div class="guide-header">
                <div class="guide-title-box">
                  <span class="guide-badge">SOP Guide</span>
                  <h3 class="guide-title">Schritt-für-Schritt</h3>
                </div>
                <button id="guide-close-btn" class="icon-btn" title="Schließen">✕</button>
              </div>
              <div id="guide-list" class="guide-list"></div>
            </aside>

            <!-- Lightbox Modal -->
            <div id="lightbox" hidden>
              <button id="lightbox-close-btn" class="lightbox-close-btn" title="Schließen (Esc)">✕</button>
              <button id="lightbox-prev-btn" class="lightbox-nav prev" title="Vorheriger Schritt (←)">‹</button>
              <button id="lightbox-next-btn" class="lightbox-nav next" title="Nächster Schritt (→)">›</button>
              <div class="lightbox-frame">
                <img id="lightbox-img" alt="">
                <div class="lightbox-bar">
                  <span id="lightbox-step-badge" class="lightbox-step-badge">Schritt 1</span>
                  <span id="lightbox-title" class="lightbox-title"></span>
                </div>
              </div>
            </div>

            {{embeddedBlock}}
            <script>
            {{cytoscapeJs}}
            </script>
            <script>
            // Captured before anything below touches the DOM (Cytoscape's own
            // canvas, the image overlays, live class toggles) — at this exact
            // point it's byte-equivalent to what's saved on disk, so the
            // save-back logic near the bottom can patch just the embedded
            // data block into *this* string and write the result out
            // unchanged otherwise (the same self-modifying-page technique
            // single-file wiki tools have long used).
            const originalHtmlText = "<!DOCTYPE html>\n" + document.documentElement.outerHTML;

            const flowData = {{dataJson}};
            const cy = cytoscape({
              container: document.getElementById("cy"),
              elements: [
                ...flowData.nodes.map((n) => ({ group: "nodes", data: n.data, position: n.position, grabbable: false })),
                ...flowData.edges.map((e) => ({ group: "edges", data: e.data })),
              ],
              style: [
                {
                  selector: "node",
                  style: {
                    "background-color": "data(color)",
                    shape: "data(shape)",
                    width: 230,
                    height: 175,
                    "border-color": "rgba(255,255,255,0.18)",
                    "border-width": 1.5,
                    label: "data(label)",
                    color: "#f8fafc",
                    "font-size": 11,
                    "font-weight": "600",
                    "font-family": "system-ui, -apple-system, sans-serif",
                    "text-valign": "top",
                    "text-halign": "center",
                    "text-margin-y": -26,
                    "text-background-color": "#0f172a",
                    "text-background-opacity": 0.9,
                    "text-background-padding": 5,
                    "text-background-shape": "roundrectangle",
                    "text-border-color": "rgba(255,255,255,0.16)",
                    "text-border-width": 1,
                    "text-border-opacity": 1,
                    "text-wrap": "wrap",
                    "text-max-width": 210,
                  },
                },
                {
                  selector: "node[shape='diamond']",
                  style: { width: 170, height: 60, "background-color": "#d97706", "border-color": "#fbbf24", "border-width": 2 },
                },
                {
                  selector: "node[shape='round-rectangle'][!imageUrl]",
                  style: { width: 170, height: 54 },
                },
                {
                  selector: "edge",
                  style: {
                    width: 2,
                    "line-color": "data(color)",
                    "curve-style": "straight",
                    "target-arrow-shape": "triangle",
                    "target-arrow-color": "data(color)",
                    "arrow-scale": 1,
                  },
                },
                {
                  selector: "edge[?manual]",
                  style: { "line-style": "dashed", "line-dash-pattern": [6, 4] },
                },
                {
                  selector: "node.connect-from, node.drop-target",
                  style: { "border-color": "#10b981", "border-width": 3 },
                },
                {
                  selector: "node.search-hit",
                  style: { "border-color": "#38bdf8", "border-width": 4 },
                },
                {
                  selector: "node.search-dimmed",
                  style: { opacity: 0.2 },
                },
              ],
              layout: { name: "preset" },
              minZoom: 0.1,
              maxZoom: 3,
              boxSelectionEnabled: false,
            });
            cy.fit(undefined, 40);

            // Overlay images
            const overlayContainer = document.getElementById("image-overlays");
            const overlayImgs = new Map();
            cy.nodes("[imageUrl]").filter((node) => !!node.data("imageUrl")).forEach((node) => {
              const img = document.createElement("img");
              img.className = "node-image-overlay";
              img.alt = "";
              img.src = node.data("imageUrl");
              overlayContainer.appendChild(img);
              overlayImgs.set(node.id(), img);
            });

            function updateOverlays() {
              overlayImgs.forEach((img, id) => {
                const box = cy.getElementById(id).renderedBoundingBox({ includeLabels: false });
                img.style.left = `${box.x1}px`;
                img.style.top = `${box.y1}px`;
                img.style.width = `${box.x2 - box.x1}px`;
                img.style.height = `${box.y2 - box.y1}px`;
              });
            }
            cy.on("pan zoom position", updateOverlays);
            updateOverlays();
            window.addEventListener("resize", () => { cy.resize(); updateOverlays(); });

            const cyContainer = document.getElementById("cy");
            cy.on("mouseover", "node[imageUrl]", () => { cyContainer.style.cursor = "zoom-in"; });
            cy.on("mouseout", "node[imageUrl]", () => { cyContainer.style.cursor = ""; });

            // Lightbox Modal
            const lightbox = document.getElementById("lightbox");
            const lightboxImg = document.getElementById("lightbox-img");
            const lightboxTitle = document.getElementById("lightbox-title");
            const lightboxStepBadge = document.getElementById("lightbox-step-badge");
            const lightboxPrevBtn = document.getElementById("lightbox-prev-btn");
            const lightboxNextBtn = document.getElementById("lightbox-next-btn");
            const lightboxCloseBtn = document.getElementById("lightbox-close-btn");

            const imageNodes = flowData.nodes.filter((n) => !!n.data.imageUrl);
            let currentLightboxIdx = -1;

            function openLightbox(idx) {
              if (idx < 0 || idx >= imageNodes.length) return;
              currentLightboxIdx = idx;
              const node = imageNodes[idx];
              lightboxImg.src = node.data.imageUrl;
              lightboxStepBadge.textContent = `Schritt ${idx + 1} von ${imageNodes.length}`;
              lightboxTitle.textContent = node.data.label || "";
              lightboxPrevBtn.disabled = (idx === 0);
              lightboxNextBtn.disabled = (idx === imageNodes.length - 1);
              lightbox.hidden = false;
            }

            function closeLightbox() {
              lightbox.hidden = true;
              currentLightboxIdx = -1;
            }

            lightboxCloseBtn.addEventListener("click", closeLightbox);
            lightboxPrevBtn.addEventListener("click", (e) => { e.stopPropagation(); openLightbox(currentLightboxIdx - 1); });
            lightboxNextBtn.addEventListener("click", (e) => { e.stopPropagation(); openLightbox(currentLightboxIdx + 1); });
            lightbox.addEventListener("click", (e) => {
              if (e.target === lightbox) closeLightbox();
            });

            document.addEventListener("keydown", (e) => {
              if (!lightbox.hidden) {
                if (e.key === "Escape") closeLightbox();
                else if (e.key === "ArrowLeft") openLightbox(currentLightboxIdx - 1);
                else if (e.key === "ArrowRight") openLightbox(currentLightboxIdx + 1);
              }
            });

            cy.on("tap", "node[imageUrl]", (evt) => {
              const clickedId = evt.target.id();
              const idx = imageNodes.findIndex((n) => n.data.id === clickedId);
              if (idx >= 0) openLightbox(idx);
            });

            // Double click on canvas to fit view
            cy.on("dbltap", (evt) => {
              if (evt.target === cy) {
                cy.animate({ fit: { eles: cy.elements(), padding: 40 }, duration: 250 });
              }
            });

            // Dock Controls: Zoom and Fit
            document.getElementById("zoom-in-btn").addEventListener("click", () => {
              cy.zoom({ level: cy.zoom() * 1.25, renderedPosition: { x: cy.width() / 2, y: cy.height() / 2 } });
            });
            document.getElementById("zoom-out-btn").addEventListener("click", () => {
              cy.zoom({ level: cy.zoom() * 0.8, renderedPosition: { x: cy.width() / 2, y: cy.height() / 2 } });
            });
            document.getElementById("fit-btn").addEventListener("click", () => {
              cy.animate({ fit: { eles: cy.elements(), padding: 40 }, duration: 250 });
            });

            // Search & Filter
            const searchInput = document.getElementById("search-input");
            const searchCount = document.getElementById("search-count");
            searchInput.addEventListener("input", () => {
              const q = searchInput.value.trim().toLowerCase();
              if (!q) {
                cy.nodes().removeClass("search-hit search-dimmed");
                searchCount.hidden = true;
                return;
              }
              let hits = 0;
              cy.nodes().forEach((node) => {
                const label = (node.data("label") || "").toLowerCase();
                const match = label.includes(q);
                if (match) hits++;
                node.toggleClass("search-hit", match);
                node.toggleClass("search-dimmed", !match);
              });
              searchCount.hidden = false;
              searchCount.textContent = `${hits}/${cy.nodes().length}`;
              const firstHit = cy.nodes(".search-hit")[0];
              if (firstHit) {
                cy.animate({ center: { eles: firstHit }, duration: 200 });
              }
            });

            // SOP Guide Drawer
            const guideDrawer = document.getElementById("guide-drawer");
            const guideToggleBtn = document.getElementById("guide-toggle-btn");
            const guideCloseBtn = document.getElementById("guide-close-btn");
            const guideList = document.getElementById("guide-list");

            function toggleGuide(show) {
              const willShow = show !== undefined ? show : !guideDrawer.classList.contains("open");
              guideDrawer.classList.toggle("open", willShow);
            }
            guideToggleBtn.addEventListener("click", () => toggleGuide());
            guideCloseBtn.addEventListener("click", () => toggleGuide(false));

            function escapeHtml(str) {
              const div = document.createElement("div");
              div.textContent = str;
              return div.innerHTML;
            }

            function buildGuideList() {
              guideList.innerHTML = "";
              imageNodes.forEach((node, idx) => {
                const card = document.createElement("div");
                card.className = "guide-card";
                card.innerHTML = `
                  <div class="guide-card-head">
                    <span class="guide-card-num">${idx + 1}</span>
                    <div class="guide-card-text">${escapeHtml(node.data.label || "Schritt " + (idx + 1))}</div>
                  </div>
                  ${node.data.imageUrl ? `<img class="guide-card-thumb" src="${node.data.imageUrl}" alt="" loading="lazy">` : ""}
                `;
                card.addEventListener("click", () => {
                  const cyNode = cy.getElementById(node.data.id);
                  if (!cyNode.empty()) {
                    cy.animate({ center: { eles: cyNode }, zoom: Math.max(cy.zoom(), 0.7), duration: 250 });
                  }
                });
                guideList.appendChild(card);
              });
            }
            buildGuideList();

            // ---- Live format editing (move / connect / save) ----------------
            const dataScriptEl = document.getElementById("docuclick-data");
            if (dataScriptEl) {
              document.getElementById("dock-save-group").hidden = false;

              const canvasDoc = JSON.parse(dataScriptEl.textContent);
              const textNodesById = new Map(canvasDoc.nodes.filter((n) => n.type === "text").map((n) => [n.id, n]));

              // Kept in sync with CanvasFlowWriter.cs's own constants: the
              // text/file/group node triplet behind a single card is matched
              // purely by relative position there (no shared id between
              // them), so moving a card here must shift all three by the
              // same delta to keep that match intact when the app re-reads
              // this file later.
              const TEXT_NODE_HEIGHT = 60;
              const TEXT_TO_IMAGE_GAP = 10;
              const GROUP_PADDING = 8;
              const DECISION_POINT_LABEL = "◆ Abzweigung";
              const PATH_START_PREFIX = "↳ Pfad: ";

              const isMarkerText = (textNode) => textNode.text === DECISION_POINT_LABEL || (textNode.text || "").startsWith(PATH_START_PREFIX);
              const findImageSibling = (textNode) => canvasDoc.nodes.find((n) => n.type === "file" && Math.abs(n.x - textNode.x) < 0.5 && Math.abs(n.y - (textNode.y + TEXT_NODE_HEIGHT + TEXT_TO_IMAGE_GAP)) < 0.5);
              const findGroupSibling = (textNode) => canvasDoc.nodes.find((n) => n.type === "group" && Math.abs(n.x - (textNode.x - GROUP_PADDING)) < 0.5 && Math.abs(n.y - (textNode.y - GROUP_PADDING)) < 0.5);
              const randomId = () => (crypto.randomUUID ? crypto.randomUUID() : `${Date.now().toString(36)}${Math.random().toString(36).slice(2)}`).replace(/-/g, "");

              let fileHandle = null;
              let dirty = false;
              let saving = false;
              let saveTimer = null;
              const saveStatus = document.getElementById("save-status");
              const setStatus = (text) => { saveStatus.textContent = text; };
              const connectBtn = document.getElementById("connect-btn");

              if (!window.showOpenFilePicker) {
                connectBtn.disabled = true;
                setStatus("Speichern hier nicht unterstützt (Browser ohne File System Access API)");
              }

              connectBtn.addEventListener("click", async () => {
                try {
                  [fileHandle] = await window.showOpenFilePicker({
                    types: [{ description: "DocuClick-Ablauf", accept: { "text/html": [".html"] } }],
                  });
                  setStatus(`verbunden: ${fileHandle.name}`);
                  if (dirty) scheduleSave();
                } catch (err) {
                  // Show the actual browser error instead of a generic
                  // message — the only way to tell "API unsupported here",
                  // "permission denied", and other causes apart without
                  // needing the F12 console.
                  if (err.name !== "AbortError") setStatus(`Fehler: ${err.name}: ${err.message}`);
                }
              });

              function buildPatchedHtml() {
                const startMarker = "<script id=\"docuclick-data\" type=\"application/json\">";
                const startIdx = originalHtmlText.indexOf(startMarker);
                const endIdx = originalHtmlText.indexOf("<\/script>", startIdx);
                return `${originalHtmlText.slice(0, startIdx + startMarker.length)}\n${JSON.stringify(canvasDoc, null, 2)}\n${originalHtmlText.slice(endIdx)}`;
              }

              // Always available regardless of browser support for the File
              // System Access API above (Brave, in particular, ships with it
              // restricted by default) — downloads a fresh copy of this same
              // file with the current edits baked in. Doesn't overwrite the
              // original by itself; replacing it with the download is a
              // manual step, but it's the one save path that works
              // everywhere without any special permission dance.
              document.getElementById("download-btn").addEventListener("click", () => {
                const blob = new Blob([buildPatchedHtml()], { type: "text/html" });
                const url = URL.createObjectURL(blob);
                const a = document.createElement("a");
                a.href = url;
                a.download = document.title.replace(/ – Ablauf$/, "") + ".html";
                document.body.appendChild(a);
                a.click();
                a.remove();
                URL.revokeObjectURL(url);
                dirty = false;
                setStatus(`heruntergeladen ${new Date().toLocaleTimeString("de-DE")}`);
              });

              function scheduleSave() {
                dirty = true;
                if (!fileHandle) {
                  setStatus("nicht gespeichert (nicht verbunden) — oder „Herunterladen“ nutzen");
                  return;
                }
                clearTimeout(saveTimer);
                saveTimer = setTimeout(saveNow, 400);
              }

              async function saveNow() {
                if (!fileHandle || saving) return;
                saving = true;
                setStatus("speichere...");
                try {
                  const patched = buildPatchedHtml();
                  const writable = await fileHandle.createWritable();
                  await writable.write(patched);
                  await writable.close();
                  dirty = false;
                  setStatus(`gespeichert ${new Date().toLocaleTimeString("de-DE")}`);
                } catch (err) {
                  setStatus("Speichern fehlgeschlagen");
                } finally {
                  saving = false;
                }
              }

              // ---- Move and shift+connect: both hand-rolled, neither uses --
              // Cytoscape's own built-in node dragging (nodes stay
              // grabbable: false, see the elements() mapping above). Tried
              // grabbable: true plus a mousedown handler that ungrabify()'d
              // the node for a shift-held connect gesture first, but
              // Cytoscape's own native drag-start already claims the
              // gesture *before* that delegated "mousedown" handler ever
              // runs (confirmed: the node moved and no gesture armed even
              // though shiftKey was set on the event) — exactly the
              // conflict the app's own live overlay (flow.js) already
              // avoids by keeping its nodes permanently non-grabbable and
              // tracking the whole mousedown/mousemove/mouseup sequence
              // itself. Doing the same for *both* gestures here sidesteps
              // it: plain mousedown starts a move (position() is set
              // programmatically regardless of grabbable), shift-held
              // mousedown starts the connect-line gesture instead.
              const DRAG_THRESHOLD = 6;
              const cyRect = () => cyContainer.getBoundingClientRect();
              function modelPositionFromClient(clientX, clientY) {
                const rect = cyRect();
                const pan = cy.pan();
                const zoom = cy.zoom();
                return { x: (clientX - rect.left - pan.x) / zoom, y: (clientY - rect.top - pan.y) / zoom };
              }

              let moveNodeId = null;
              let moveStartClient = null;
              let moveStartPos = null;

              function finalizeMove(nodeId) {
                const textNode = textNodesById.get(nodeId);
                if (!textNode) return;
                const pos = cy.getElementById(nodeId).position();
                textNode.x = pos.x;
                textNode.y = pos.y;
                const image = findImageSibling(textNode);
                if (image) { image.x = pos.x; image.y = pos.y + TEXT_NODE_HEIGHT + TEXT_TO_IMAGE_GAP; }
                const group = findGroupSibling(textNode);
                if (group) { group.x = pos.x - GROUP_PADDING; group.y = pos.y - GROUP_PADDING; }
                scheduleSave();
              }

              let connectFromId = null;
              let connectStartClient = null;
              let connectArmed = false;
              const connectLine = document.getElementById("connect-line");
              const connectLineEl = connectLine.querySelector("line");

              // ---- Connection handles: a small dot on each of the 4 sides
              // of every connectable card — a modifier-key drag (shift+drag,
              // still wired below as a fallback) turned out to be unreliable
              // for real mouse input in ways synthetic testing never caught,
              // so this is now the primary, discoverable way to start a
              // connection: press a handle and drag to another card.
              const handlesContainer = document.getElementById("node-handles");
              const handlesByNode = new Map();
              cy.nodes().forEach((node) => {
                const textNode = textNodesById.get(node.id());
                if (!textNode || isMarkerText(textNode)) return;
                const dots = ["top", "right", "bottom", "left"].map(() => {
                  const dot = document.createElement("div");
                  dot.className = "node-handle";
                  handlesContainer.appendChild(dot);
                  dot.addEventListener("mousedown", (e) => {
                    e.preventDefault();
                    e.stopPropagation();
                    connectFromId = node.id();
                    connectStartClient = { x: e.clientX, y: e.clientY };
                    connectArmed = true;
                    cy.getElementById(connectFromId).addClass("connect-from");
                    connectLine.removeAttribute("hidden");
                    connectLineEl.setAttribute("x1", e.clientX);
                    connectLineEl.setAttribute("y1", e.clientY);
                    connectLineEl.setAttribute("x2", e.clientX);
                    connectLineEl.setAttribute("y2", e.clientY);
                  });
                  return dot;
                });
                handlesByNode.set(node.id(), dots);
              });

              function updateHandles() {
                handlesByNode.forEach((dots, id) => {
                  const box = cy.getElementById(id).renderedBoundingBox({ includeLabels: false });
                  const midX = (box.x1 + box.x2) / 2;
                  const midY = (box.y1 + box.y2) / 2;
                  const points = [
                    { x: midX, y: box.y1 },
                    { x: box.x2, y: midY },
                    { x: midX, y: box.y2 },
                    { x: box.x1, y: midY },
                  ];
                  dots.forEach((dot, i) => {
                    dot.style.left = `${points[i].x}px`;
                    dot.style.top = `${points[i].y}px`;
                  });
                });
              }
              cy.on("pan zoom position", updateHandles);
              updateHandles();
              window.addEventListener("resize", updateHandles);

              // Handles only reveal themselves near their own card — a
              // permanently-visible ring of dots around every single card
              // looked cluttered. Checked against each card's own bounding
              // box (padded a bit) rather than Cytoscape's node
              // mouseover/mouseout, which only hit-tests the card's own
              // shape and would hide a handle again the instant the cursor
              // crosses onto it (half of every handle sits *outside* its
              // card by design, so it's reachable at all).
              const HANDLE_REVEAL_PADDING = 24;
              document.addEventListener("mousemove", (e) => {
                if (moveNodeId !== null || connectFromId !== null) return;
                const pos = modelPositionFromClient(e.clientX, e.clientY);
                const pad = HANDLE_REVEAL_PADDING / cy.zoom();
                handlesByNode.forEach((dots, id) => {
                  const box = cy.getElementById(id).boundingBox({ includeLabels: false });
                  const near = pos.x >= box.x1 - pad && pos.x <= box.x2 + pad && pos.y >= box.y1 - pad && pos.y <= box.y2 + pad;
                  dots.forEach((d) => d.classList.toggle("visible", near));
                });
              });

              cy.on("mousedown", "node", (evt) => {
                const textNode = textNodesById.get(evt.target.id());
                if (!textNode) return;
                // Without this, a real (trusted) mousedown+drag starting
                // anywhere near a text element on the page — the #hint line,
                // the toolbar — can fall through to the browser's own native
                // text-selection drag, which then competes with (and can
                // silently swallow) the custom gesture below. Synthetic
                // events used while developing this don't trigger that
                // native behavior, which is why it wasn't caught until real
                // mouse input was tested.
                evt.originalEvent.preventDefault();
                if (evt.originalEvent.shiftKey) {
                  if (isMarkerText(textNode)) return;
                  connectFromId = evt.target.id();
                  connectStartClient = { x: evt.originalEvent.clientX, y: evt.originalEvent.clientY };
                  connectArmed = false;
                  return;
                }
                moveNodeId = evt.target.id();
                moveStartClient = { x: evt.originalEvent.clientX, y: evt.originalEvent.clientY };
                moveStartPos = { ...evt.target.position() };
              });

              // ---- Right-click a card to rename its description ----------
              cy.on("cxttap", "node", (evt) => {
                const textNode = textNodesById.get(evt.target.id());
                if (!textNode || textNode.text === DECISION_POINT_LABEL) return;
                const isPathStart = (textNode.text || "").startsWith(PATH_START_PREFIX);
                const currentValue = isPathStart ? textNode.text.slice(PATH_START_PREFIX.length) : (textNode.text || "");
                const next = prompt("Beschreibung bearbeiten:", currentValue);
                if (next === null || next === currentValue) return;
                textNode.text = isPathStart ? `${PATH_START_PREFIX}${next}` : next;
                evt.target.data("label", isPathStart ? textNode.text : buildLabel(next));
                scheduleSave();
              });

              function buildLabel(text) {
                if (!text) return "(ohne Beschreibung)";
                const firstLine = text.split("\n")[0];
                return firstLine.length > 70 ? `${firstLine.slice(0, 70)}…` : firstLine;
              }

              document.addEventListener("mousemove", (e) => {
                if (moveNodeId !== null) {
                  const zoom = cy.zoom();
                  const dx = (e.clientX - moveStartClient.x) / zoom;
                  const dy = (e.clientY - moveStartClient.y) / zoom;
                  cy.getElementById(moveNodeId).position({ x: moveStartPos.x + dx, y: moveStartPos.y + dy });
                  return;
                }
                if (connectFromId === null) return;
                if (!connectArmed) {
                  if (Math.hypot(e.clientX - connectStartClient.x, e.clientY - connectStartClient.y) < DRAG_THRESHOLD) return;
                  connectArmed = true;
                  cy.getElementById(connectFromId).addClass("connect-from");
                  connectLine.removeAttribute("hidden");
                }
                connectLineEl.setAttribute("x1", connectStartClient.x);
                connectLineEl.setAttribute("y1", connectStartClient.y);
                connectLineEl.setAttribute("x2", e.clientX);
                connectLineEl.setAttribute("y2", e.clientY);
                cy.nodes(".drop-target").removeClass("drop-target");
                const target = findConnectTarget(connectFromId, modelPositionFromClient(e.clientX, e.clientY));
                if (target) target.addClass("drop-target");
              });

              document.addEventListener("mouseup", (e) => {
                if (moveNodeId !== null) {
                  finalizeMove(moveNodeId);
                  moveNodeId = null;
                  moveStartClient = null;
                  moveStartPos = null;
                  return;
                }
                endConnectGesture(e.clientX, e.clientY);
              });
              document.addEventListener("mouseleave", () => { moveNodeId = null; endConnectGesture(null, null); });
              window.addEventListener("blur", () => { moveNodeId = null; endConnectGesture(null, null); });

              function endConnectGesture(clientX, clientY) {
                if (connectFromId === null) return;
                if (connectArmed && clientX !== null) {
                  const target = findConnectTarget(connectFromId, modelPositionFromClient(clientX, clientY));
                  if (target) tryConnect(connectFromId, target.id());
                }
                const fromNode = cy.getElementById(connectFromId);
                fromNode.removeClass("connect-from");
                cy.nodes(".drop-target").removeClass("drop-target");
                connectLine.setAttribute("hidden", "");
                connectLineEl.setAttribute("x1", "0");
                connectLineEl.setAttribute("y1", "0");
                connectLineEl.setAttribute("x2", "0");
                connectLineEl.setAttribute("y2", "0");
                void connectLine.offsetHeight;
                connectFromId = null;
                connectStartClient = null;
                connectArmed = false;
              }

              function findConnectTarget(fromId, modelPos) {
                const invalidTargets = ancestorsOf(fromId);
                const candidates = cy.nodes().filter((n) => {
                  if (n.id() === fromId || invalidTargets.has(n.id())) return false;
                  const textNode = textNodesById.get(n.id());
                  return !!textNode && !isMarkerText(textNode);
                });
                return candidates.filter((n) => {
                  const box = n.boundingBox();
                  return modelPos.x >= box.x1 && modelPos.x <= box.x2 && modelPos.y >= box.y1 && modelPos.y <= box.y2;
                })[0] || null;
              }

              // Every node reachable *backward* via structural (non-manual)
              // edges — connecting into one of fromId's own ancestors would
              // close a cycle. Matches CanvasFlowWriter.ConnectNodes' own
              // guard exactly, including skipping manual edges: those are
              // additive references, never real structural relationships, so
              // they can't actually close a cycle.
              function ancestorsOf(nodeId) {
                const result = new Set();
                const queue = [nodeId];
                while (queue.length > 0) {
                  const id = queue.shift();
                  cy.getElementById(id).incomers("edge").filter((e) => !e.data("manual")).forEach((e) => {
                    const sourceId = e.source().id();
                    if (!result.has(sourceId)) { result.add(sourceId); queue.push(sourceId); }
                  });
                }
                return result;
              }

              function tryConnect(fromId, toId) {
                if (fromId === toId) return;
                if (cy.edges().some((e) => e.data("source") === fromId && e.data("target") === toId)) return;
                cy.add({ group: "edges", data: { id: `manual-${fromId}->${toId}`, source: fromId, target: toId, color: "#6B7280", manual: true } });
                canvasDoc.edges.push({ id: randomId(), fromNode: fromId, toNode: toId, fromSide: "bottom", toSide: "top", docuClickManual: true });
                scheduleSave();
              }

              // ---- Right-click a manual edge to remove it ------------------
              cy.on("cxttap", "edge[?manual]", (evt) => {
                const edge = evt.target;
                if (!confirm("Diese Verbindung entfernen?")) return;
                const fromId = edge.data("source");
                const toId = edge.data("target");
                canvasDoc.edges = canvasDoc.edges.filter((e) => !(e.fromNode === fromId && e.toNode === toId && e.docuClickManual));
                edge.remove();
                scheduleSave();
              });
            }
            </script>
            </body>
            </html>
            """;
    }
}
