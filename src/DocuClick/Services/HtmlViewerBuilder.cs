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
        var jsNodes = nodes.Select(n => new
        {
            data = new { id = n.Id, label = n.Label, color = n.Color, shape = n.Shape, imageUrl = n.ImageSrc },
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
        var cytoscapeJs = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "WebAssets", "vendor", "cytoscape.min.js"));
        // type="application/json" is never executed by the browser — purely
        // inert data storage, safe to embed regardless of where it sits.
        // System.Text.Json's default encoder already escapes '<'/'>' inside
        // string values (e.g. a description containing literal "</script>"),
        // so this can never prematurely close its own tag.
        var embeddedBlock = embeddedDataJson is null
            ? ""
            : $"\n<script id=\"docuclick-data\" type=\"application/json\">\n{embeddedDataJson}\n</script>";

        return $$"""
            <!DOCTYPE html>
            <html lang="de">
            <head>
            <meta charset="utf-8">
            <title>{{System.Net.WebUtility.HtmlEncode(title)}} – Ablauf</title>
            <style>
              html, body { margin: 0; padding: 0; width: 100%; height: 100%; background: #1a1a1e; font-family: "Segoe UI", sans-serif; overflow: hidden; user-select: none; }
              #cy { width: 100%; height: 100%; }
              /* Real <img> elements overlaid on top of Cytoscape's own canvas,
                 positioned to track each node — NOT Cytoscape's own
                 background-image (which loads via fetch() internally and gets
                 flatly refused by Chromium when this page is opened via
                 file:// with a "null" origin: fetching a *sibling* file:// URL
                 is cross-origin there and file: isn't an allowed scheme for
                 it, confirmed via a real browser's console). A plain <img src>
                 uses the browser's native image loader instead, which has no
                 such restriction — the same reason the lightbox preview below
                 already worked even when the in-node thumbnail didn't. */
              #image-overlays { position: fixed; inset: 0; pointer-events: none; overflow: hidden; }
              .node-image-overlay { position: absolute; object-fit: cover; border-radius: 8px; pointer-events: none; }
              #node-handles { position: fixed; inset: 0; pointer-events: none; overflow: hidden; z-index: 1000; }
              .node-handle { position: absolute; width: 16px; height: 16px; margin: -8px; border-radius: 50%; background: #22C55E; border: 2px solid #ffffff; cursor: crosshair; pointer-events: auto; box-shadow: 0 1px 4px rgba(0,0,0,0.5); opacity: 0; transition: opacity 0.1s; }
              .node-handle.visible { opacity: 1; }
              .node-handle:hover { background: #16A34A; }
              #hint { position: fixed; top: 10px; left: 10px; color: #ffffff; opacity: 0.55; font-size: 12px; pointer-events: none; }
              #lightbox { position: fixed; inset: 0; background: rgba(0,0,0,0.82); display: flex; align-items: center; justify-content: center; cursor: zoom-out; }
              #lightbox img { max-width: 92vw; max-height: 92vh; border-radius: 6px; box-shadow: 0 8px 32px rgba(0,0,0,0.6); }
              #connect-line { position: fixed; inset: 0; width: 100%; height: 100%; pointer-events: none; z-index: 999; }
              #connect-line line { stroke: #22C55E; stroke-width: 2; stroke-dasharray: 6 4; }
              #toolbar { position: fixed; top: 10px; right: 10px; display: flex; align-items: center; gap: 8px; background: rgba(0,0,0,0.6); color: #fff; padding: 6px 10px; border-radius: 6px; font-size: 12px; }
              #toolbar button { font: inherit; cursor: pointer; background: #2563EB; color: #fff; border: none; border-radius: 4px; padding: 4px 10px; }
              #toolbar button:hover { background: #1d4ed8; }
              [hidden] { display: none !important; }
            </style>
            </head>
            <body>
            <div id="cy"></div>
            <div id="image-overlays"></div>
            <div id="node-handles"></div>
            <div id="hint">Ziehen zum Verschieben, Mausrad zum Zoomen, Karte anklicken für Screenshot in voller Größe.</div>
            <div id="lightbox" hidden><img id="lightbox-img" alt=""></div>
            <svg id="connect-line" hidden><line x1="0" y1="0" x2="0" y2="0"/></svg>
            <div id="toolbar" hidden>
              <span id="save-status">nicht verbunden</span>
              <button id="connect-btn" type="button">Mit Datei verbinden</button>
              <button id="download-btn" type="button">Herunterladen</button>
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
                    width: 220,
                    height: 170,
                    "border-color": "#ffffff",
                    "border-width": 1,
                    label: "data(label)",
                    color: "#ffffff",
                    "font-size": 11,
                    "font-weight": "bold",
                    "text-valign": "top",
                    "text-halign": "center",
                    "text-margin-y": -28,
                    "text-background-color": "rgba(0,0,0,0.78)",
                    "text-background-opacity": 1,
                    "text-background-padding": 3,
                    "text-wrap": "wrap",
                    "text-max-width": 200,
                  },
                },
                {
                  selector: "node[shape='diamond'], node[shape='round-rectangle'][!imageUrl]",
                  style: { width: 160, height: 50 },
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
                  style: { "line-style": "dashed" },
                },
                {
                  selector: "node.connect-from, node.drop-target",
                  style: { "border-color": "#22C55E", "border-width": 3 },
                },
              ],
              layout: { name: "preset" },
              minZoom: 0.1,
              maxZoom: 3,
              // Cytoscape's own built-in gesture for this (rubber-band
              // multi-select on the background) uses the exact same
              // shift+drag chord as the connect gesture below — confirmed
              // as a real conflict (shift+drag drew Cytoscape's own
              // selection rectangle instead of a connection whenever the
              // gesture started a pixel off the card). Disabled outright:
              // nothing here ever uses Cytoscape's selection state anyway.
              boxSelectionEnabled: false,
            });
            cy.fit(undefined, 40);

            // Real <img> elements tracking each image node's on-screen box —
            // see the #image-overlays comment above for why these exist
            // instead of a Cytoscape background-image.
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
                // includeLabels: false — the label sits externally above the
                // node (text-valign: top), so the default bounding box (which
                // includes it) is taller than the card itself and would let
                // the image cover the label text and bleed into whatever
                // sits above the card.
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

            const lightbox = document.getElementById("lightbox");
            const lightboxImg = document.getElementById("lightbox-img");
            cy.on("tap", "node[imageUrl]", (evt) => {
              lightboxImg.src = evt.target.data("imageUrl");
              lightbox.hidden = false;
            });
            lightbox.addEventListener("click", () => { lightbox.hidden = true; });

            // ---- Editing (move / connect / save) — live format only -------
            // Only present when this file still carries its round-trippable
            // node/edge JSON (see CanvasDocumentIo) — HtmlFlowExporter's
            // one-shot share export has no such block and stays purely
            // read-only, exactly as before.
            const dataScriptEl = document.getElementById("docuclick-data");
            if (dataScriptEl) {
              document.getElementById("hint").textContent =
                "Ziehen zum Verschieben, grünen Punkt am Kartenrand zu einer anderen Karte ziehen für neue Verbindung, Rechtsklick auf eine Karte zum Umbenennen (auf eine Verbindung zum Entfernen), Mausrad zum Zoomen, Karte anklicken für Screenshot in voller Größe.";
              document.getElementById("toolbar").hidden = false;

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
