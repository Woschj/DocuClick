// Renders the Ablauf-Übersicht flow graph inside the WPF host's WebView2
// control. Talks to the C# side (FlowPreviewOverlay.cs) via a small JSON
// message protocol instead of duplicating any branching/business logic
// here — this file only renders and reports gestures; every decision about
// what a click *means* (jump vs. fork vs. rename vs. cascade-delete) still
// happens in C#, exactly as before the WebView2 rewrite. See the plan doc
// for the full protocol.

(() => {
  "use strict";

  // ---- Host bridge -------------------------------------------------
  // Works two ways: inside the real WebView2 host (window.chrome.webview
  // exists), or standalone in a plain browser tab for UI development/
  // testing — in that case, mock-preview.json stands in for the host's
  // "preview" pushes, and outgoing messages just log to the console
  // instead of being lost on an undefined API.
  const inHost = typeof window.chrome !== "undefined" && !!window.chrome.webview;

  function sendToHost(message) {
    if (inHost) {
      window.chrome.webview.postMessage(message);
    } else {
      console.log("[dev] -> host:", message);
    }
  }

  function onHostMessage(handler) {
    if (inHost) {
      window.chrome.webview.addEventListener("message", (e) => handler(e.data));
    } else {
      fetch("mock-preview.json")
        .then((r) => r.json())
        .then((data) => handler({ type: "preview", ...data }))
        .catch((err) => console.error("[dev] failed to load mock-preview.json", err));
    }
  }

  // ---- Cytoscape setup ----------------------------------------------
  const cy = cytoscape({
    container: document.getElementById("cy"),
    // Native browser pan (drag background) / zoom (wheel) — this is the
    // entire point of the WebView2 rewrite: Chromium's pointer-event
    // handling is what's battle-tested here, not custom WPF routed-event
    // plumbing fighting window activation and ScrollViewer's own
    // click-to-focus handling.
    userPanningEnabled: true,
    userZoomingEnabled: true,
    // Shift+drag on the background box-selects (Cytoscape's built-in
    // gesture split: plain drag pans, shift+drag selects — both can be
    // enabled together without fighting each other). Needed for
    // multi-node delete; autounselectify must be off for selection to do
    // anything at all.
    boxSelectionEnabled: true,
    autounselectify: false,
    minZoom: 0.3,
    maxZoom: 2.5,
    style: [
      {
        selector: "node",
        style: {
          "background-color": "data(color)",
          shape: "data(shape)",
          width: "data(width)",
          height: "data(height)",
          "border-color": "rgba(255, 255, 255, 0.45)",
          "border-width": "data(borderWidth)",
        },
      },
      {
        selector: "edge",
        style: {
          width: 2.5,
          "line-color": "rgba(148, 163, 184, 0.65)",
          "curve-style": "bezier",
          "target-arrow-shape": "none",
          "mid-target-arrow-shape": "triangle",
          "mid-target-arrow-color": "rgba(148, 163, 184, 0.85)",
          "arrow-scale": 1.15,
          "underlay-color": "#38bdf8",
          "underlay-padding": 8,
          "underlay-opacity": 0,
        },
      },
      {
        selector: "edge[?manual]",
        style: {
          width: 2.5,
          "line-color": "#38bdf8",
          "line-style": "dashed",
          "line-dash-pattern": [6, 4],
          "mid-target-arrow-color": "#38bdf8",
          "underlay-color": "#38bdf8",
          "underlay-padding": 8,
          "underlay-opacity": 0,
        },
      },
      {
        selector: "edge:selected",
        style: {
          width: 4.5,
          "line-color": "#38bdf8",
          "mid-target-arrow-color": "#38bdf8",
          opacity: 1,
          "z-index": 999,
          "underlay-opacity": 0.15,
        },
      },
      {
        selector: "node.connect-from",
        style: { "border-color": "#10b981", "border-width": 3.5 },
      },
      {
        selector: "node.drop-target",
        style: { "border-color": "#38bdf8", "border-width": 3.5 },
      },
      {
        selector: "node:selected",
        style: { "border-color": "#3b82f6", "border-width": 3.5 },
      },
      {
        selector: "node.search-hit",
        style: {
          "border-color": "#60a5fa",
          "border-width": 4,
          opacity: 1,
        },
      },
      {
        selector: "node.search-dimmed",
        style: {
          opacity: 0.2,
        },
      },
      {
        selector: "edge.search-dimmed",
        style: {
          opacity: 0.12,
        },
      },
    ],
    elements: [],
    layout: { name: "preset" },
  });

  // ---- Rendering ------------------------------------------------------
  const emptyHint = document.getElementById("empty-hint");
  // Cytoscape layouts default to fit:true (recompute zoom+pan to frame
  // every element). render() runs on every single click during a
  // recording, so leaving that on meant the view zoomed out a bit further
  // on every new node, forever fighting whatever zoom the user had just
  // set manually. Fit once per session (first non-empty render), then
  // leave zoom alone and only pan to the newest node below.
  let hasFitted = false;

  // Module-wide (unlike the node-level "large" datum above, which only
  // exists per-node once rendered): read by the background-right-click
  // "+ Neuer Knoten hier" handler below, which fires outside render()'s own
  // scope and needs to know the current mode without a node to check.
  let isLargeMode = false;

  function render(preview) {
    const nodes = preview.nodes || [];
    const edges = preview.edges || [];
    const large = !!preview.large;
    const isRecordedClick = !!preview.isRecordedClick;
    isLargeMode = large;

    emptyHint.hidden = nodes.length > 0;
    if (nodes.length === 0) {
      cy.elements().remove();
      rebuildImageOverlays();
      rebuildNodeLabels();
      rebuildConnectHandles();
      updateCurrentBadge();
      hasFitted = false; // next session's first render should fit fresh
      return;
    }

    const newNodeIds = new Set(nodes.map((n) => n.id));
    const newEdgeIds = new Set(edges.map((e) => `${e.source}->${e.target}`));

    cy.batch(() => {
      // Remove nodes not in new payload
      cy.nodes().filter((n) => !newNodeIds.has(n.id())).remove();

      // Remove edges not in new payload
      cy.edges().filter((e) => !newEdgeIds.has(e.id())).remove();

      const toAdd = [];

      // Update existing or add new nodes
      for (let i = 0; i < nodes.length; i++) {
        const n = nodes[i];
        const data = buildNodeData(n, large);
        const cyNode = cy.getElementById(n.id);
        if (cyNode.length > 0) {
          const curPos = cyNode.position();
          if (curPos.x !== n.x || curPos.y !== n.y) {
            cyNode.position({ x: n.x, y: n.y });
          }
          cyNode.data(data);
        } else {
          toAdd.push({
            group: "nodes",
            data: data,
            position: { x: n.x, y: n.y },
            grabbable: false,
          });
        }
      }

      // Add missing edges
      for (let i = 0; i < edges.length; i++) {
        const e = edges[i];
        const edgeId = `${e.source}->${e.target}`;
        if (cy.getElementById(edgeId).length === 0) {
          toAdd.push({
            group: "edges",
            data: { id: edgeId, source: e.source, target: e.target, manual: e.manual },
          });
        }
      }

      if (toAdd.length > 0) {
        cy.add(toAdd);
      }
    });

    if (!hasFitted) {
      cy.layout({ name: "preset", fit: true }).run();
      hasFitted = true;
    }

    syncImageOverlays();
    syncNodeLabels();
    syncConnectHandles();
    updateCurrentBadge();

    const statsPill = document.getElementById("hud-stats-pill");
    if (statsPill) {
      const count = nodes.length;
      statsPill.textContent = count === 1 ? "1 Schritt" : `${count} Schritte`;
    }

    if (searchInput && searchInput.value.trim().length > 0) {
      applySearch(searchInput.value);
    }

    // Keep the current (just-added) node in view ONLY when a NEW screenshot was actually captured in live recording mode!
    // Never jump when adding manual elements, moving, connecting, branching, or editing!
    if (isRecordedClick) {
      const current = cy.nodes('[?isCurrent]');
      if (current.length > 0 && !isInViewport(current[0])) {
        cy.animate({ center: { eles: current }, duration: 150 });
      }
    }
  }

  function buildNodeData(n, large) {
    return {
      id: n.id,
      label: n.label,
      permLabel: n.permLabel || "",
      displayLabel: n.displayLabel || "",
      large: large || undefined,
      color: n.color || "#3b82f6",
      shape: n.isDecisionPoint ? "diamond" : n.isMarker ? "ellipse" : (n.shape || "round-rectangle"),
      width: n.width,
      height: n.height,
      borderWidth: n.isCurrent ? 2.5 : 1.5,
      hasChildren: n.hasChildren,
      isMarker: n.isMarker,
      isDecisionPoint: n.isDecisionPoint,
      isPathStart: n.isPathStart,
      isCurrent: n.isCurrent,
      tooltip: n.pathName ? `${n.label} · Pfad: ${n.pathName}` : n.label,
      imageUrl: n.imageUrl || undefined,
    };
  }

  function isInViewport(node) {
    const box = node.renderedBoundingBox();
    const w = cy.width();
    const h = cy.height();
    return box.x1 >= 0 && box.y1 >= 0 && box.x2 <= w && box.y2 <= h;
  }

  // ---- Screenshot thumbnails: real <img> overlays -----------------------
  const imageOverlayContainer = document.getElementById("image-overlays");
  let imageOverlays = new Map();

  function syncImageOverlays() {
    if (!imageOverlayContainer) return;
    const validIds = new Set();
    cy.nodes("[imageUrl]").forEach((node) => {
      const id = node.id();
      validIds.add(id);
      const url = node.data("imageUrl");
      if (imageOverlays.has(id)) {
        const img = imageOverlays.get(id);
        if (img.src !== url) {
          img.src = url;
        }
      } else {
        const img = document.createElement("img");
        img.className = "hud-node-image";
        img.alt = "";
        img.onerror = () => sendToHost({ type: "imageLoadError", url: img.src });
        img.src = url;
        imageOverlayContainer.appendChild(img);
        imageOverlays.set(id, img);
      }
    });

    imageOverlays.forEach((img, id) => {
      if (!validIds.has(id)) {
        img.remove();
        imageOverlays.delete(id);
      }
    });

    updateImageOverlays();
  }

  function rebuildImageOverlays() {
    if (!imageOverlayContainer) return;
    imageOverlayContainer.innerHTML = "";
    imageOverlays.clear();
    syncImageOverlays();
  }

  function updateImageOverlays() {
    imageOverlays.forEach((img, id) => {
      const node = cy.getElementById(id);
      if (node.empty()) {
        return;
      }
      // includeLabels: false — text-valign:"top" draws the label *above*
      // (external to) the node, so the default bounding box is taller than
      // the card and would let the image cover the label and bleed into
      // whatever sits above it (the exact bug already found and fixed once
      // for the exported/live HTML's own version of this same overlay).
      const box = node.renderedBoundingBox({ includeLabels: false });
      img.style.left = `${box.x1}px`;
      img.style.top = `${box.y1}px`;
      img.style.width = `${box.x2 - box.x1}px`;
      img.style.height = `${box.y2 - box.y1}px`;
    });
  }
  cy.on("pan zoom position", updateImageOverlays);

  // ---- Node Text Box Overlays (crisp HTML typography, word-wrap, click-to-edit) ----
  // Rendered as real DOM elements in #node-labels instead of Cytoscape
  // canvas text so typography remains razor-sharp at any zoom, wraps
  // cleanly to full readability without being truncated, and allows
  // direct single-click editing right in the text box.
  const nodeLabelsContainer = document.getElementById("node-labels");
  let nodeLabels = new Map();
  let editingNodeId = null;

  function createNodeLabelElement(node) {
    const data = node.data();
    const text = data.displayLabel || data.label || "";

    const labelEl = document.createElement("div");
    labelEl.className = "hud-node-label";
    if (data.isDecisionPoint) {
      labelEl.classList.add("decision-point");
    } else if (data.isPathStart) {
      labelEl.classList.add("path-start");
    }
    if (data.shape) {
      labelEl.classList.add(`shape-${data.shape}`);
    }

    labelEl.textContent = text;
    labelEl.title = data.isDecisionPoint ? text : "Klicken zum Bearbeiten (oder Rechtsklick für Menü)";

    // Right-click opens context menu just like clicking the node
    labelEl.addEventListener("contextmenu", (e) => {
      e.preventDefault();
      e.stopPropagation();
      pendingMenuNodeId = data.id;
      pendingMenuPos = { x: e.clientX, y: e.clientY };
      sendToHost({ type: "requestPaths", nodeId: data.id });
    });

    // Click: select node and start inline editing (if not a decision point marker)
    labelEl.addEventListener("click", (e) => {
      e.stopPropagation();
      cy.nodes().unselect();
      node.select();

      if (data.isDecisionPoint) {
        return;
      }

      if (editingNodeId === data.id) {
        return;
      }

      startInlineLabelEdit(node, labelEl);
    });

    return labelEl;
  }

  function syncNodeLabels() {
    if (!nodeLabelsContainer) return;
    const validIds = new Set();

    cy.nodes().forEach((node) => {
      const data = node.data();
      const id = node.id();
      const text = data.displayLabel || data.label || "";
      if (!text) {
        return;
      }
      validIds.add(id);

      if (nodeLabels.has(id)) {
        if (editingNodeId !== id) {
          const labelEl = nodeLabels.get(id);
          if (labelEl.textContent !== text) {
            labelEl.textContent = text;
          }
          labelEl.classList.toggle("decision-point", !!data.isDecisionPoint);
          labelEl.classList.toggle("path-start", !!data.isPathStart);
        }
      } else {
        const labelEl = createNodeLabelElement(node);
        nodeLabelsContainer.appendChild(labelEl);
        nodeLabels.set(id, labelEl);
      }
    });

    nodeLabels.forEach((labelEl, id) => {
      if (!validIds.has(id)) {
        labelEl.remove();
        nodeLabels.delete(id);
      }
    });

    updateNodeLabels();
  }

  function rebuildNodeLabels() {
    if (!nodeLabelsContainer) return;
    nodeLabelsContainer.innerHTML = "";
    nodeLabels.clear();
    editingNodeId = null;
    syncNodeLabels();
  }

  function startInlineLabelEdit(node, labelEl) {
    const data = node.data();
    editingNodeId = data.id;

    // Use pure label or path name without "↳ " or "● " prefixes
    const initialText = data.pathName || data.label || "";
    labelEl.innerHTML = "";
    labelEl.classList.add("editing");

    const textarea = document.createElement("textarea");
    textarea.className = "hud-node-label-textarea";
    textarea.value = initialText;
    textarea.rows = 1;
    textarea.spellcheck = false;
    textarea.autocomplete = "off";

    function autoFitHeight() {
      textarea.style.height = "auto";
      textarea.style.height = `${Math.max(textarea.scrollHeight, 24)}px`;
    }

    labelEl.appendChild(textarea);
    autoFitHeight();
    updateNodeLabels();
    textarea.focus();
    textarea.select();

    let committed = false;

    function commit() {
      if (committed) return;
      committed = true;
      const newText = textarea.value.trim();
      labelEl.classList.remove("editing");
      editingNodeId = null;

      if (newText.length > 0 && newText !== initialText) {
        const display = data.isPathStart ? `↳ ${newText}` : data.isCurrent ? `● ${newText}` : newText;
        labelEl.textContent = display;
        sendToHost({ type: "rename", nodeId: data.id, newLabel: newText });
      } else {
        labelEl.textContent = data.displayLabel || data.label || "";
      }
      updateNodeLabels();
    }

    function cancel() {
      if (committed) return;
      committed = true;
      labelEl.classList.remove("editing");
      editingNodeId = null;
      labelEl.textContent = data.displayLabel || data.label || "";
      updateNodeLabels();
    }

    textarea.addEventListener("keydown", (e) => {
      e.stopPropagation();
      if (e.key === "Enter" && !e.shiftKey) {
        e.preventDefault();
        commit();
      } else if (e.key === "Enter" && e.shiftKey) {
        setTimeout(autoFitHeight, 0);
      } else if (e.key === "Escape") {
        e.preventDefault();
        cancel();
      }
    });

    textarea.addEventListener("input", autoFitHeight);

    textarea.addEventListener("blur", () => {
      commit();
    });

    textarea.addEventListener("click", (e) => {
      e.stopPropagation();
    });
  }

  function updateNodeLabels() {
    const zoom = cy.zoom();
    nodeLabels.forEach((labelEl, id) => {
      const node = cy.getElementById(id);
      if (node.empty()) {
        labelEl.remove();
        nodeLabels.delete(id);
        return;
      }
      const box = node.renderedBoundingBox({ includeLabels: false });
      if (box.x2 < -120 || box.x1 > cy.width() + 120 || box.y2 < -120 || box.y1 > cy.height() + 120) {
        labelEl.style.display = "none";
        return;
      }
      labelEl.style.display = "";
      const centerX = (box.x1 + box.x2) / 2;

      const isEditing = editingNodeId === id;
      const scale = isEditing ? Math.max(zoom, 0.95) : zoom;

      const hasImage = !!node.data("imageUrl");
      if (hasImage) {
        const topY = box.y1 - 4 * zoom;
        labelEl.style.left = `${centerX}px`;
        labelEl.style.top = `${topY}px`;
        labelEl.style.transform = `translate(-50%, -100%) scale(${scale})`;
        labelEl.classList.remove("center-inside");
      } else {
        const centerY = (box.y1 + box.y2) / 2;
        labelEl.style.left = `${centerX}px`;
        labelEl.style.top = `${centerY}px`;
        labelEl.style.transform = `translate(-50%, -50%) scale(${scale})`;
        labelEl.classList.add("center-inside");
      }

      const modelWidth = node.width();
      labelEl.style.width = `${Math.max(modelWidth - (hasImage ? 0 : 16), 120)}px`;
      labelEl.style.maxWidth = `${Math.max(modelWidth * 1.5, 260)}px`;
    });
  }
  cy.on("pan zoom position", updateNodeLabels);

  // ---- Current-node badge -------------------------------------------
  // A small floating tag pointing at the writer's cursor node — where the
  // *next* screenshot attaches (PreviewNode.IsCurrent) — positioned as a
  // plain DOM element rather than a Cytoscape style so its size stays fixed
  // and legible regardless of zoom (a border/color on the node itself
  // shrinks into an unnoticeable sliver once a longer flow is zoomed out to
  // fit; see the CSS pulse animation for why this alone is easy to miss on
  // a stationary badge too).
  const currentBadge = document.getElementById("current-badge");

  function updateCurrentBadge() {
    const current = cy.nodes('[?isCurrent]');
    if (current.length === 0) {
      currentBadge.hidden = true;
      return;
    }

    const box = current[0].renderedBoundingBox({ includeLabels: false });
    // Viewport check: if the current node is scrolled completely out of view, hide the badge
    if (box.x2 < 0 || box.x1 > cy.width() || box.y2 < 0 || box.y1 > cy.height()) {
      currentBadge.hidden = true;
      return;
    }

    currentBadge.hidden = false;
    const badgeWidth = currentBadge.offsetWidth || 120;
    const placeLeft = (box.x1 - 6 - badgeWidth) >= 4;

    currentBadge.classList.toggle("flip-right", !placeLeft);
    currentBadge.textContent = placeLeft ? "Nächster Klick hier ▶" : "◀ Nächster Klick hier";
    currentBadge.style.left = placeLeft ? `${box.x1 - 6}px` : `${box.x2 + 6}px`;
    currentBadge.style.top = `${(box.y1 + box.y2) / 2}px`;
  }
  cy.on("pan zoom position", updateCurrentBadge);

  // ---- Connection handles (large/editing mode only) ----------------------
  // A small dot on each of the 4 sides of every connectable card, dragged
  // to another card to connect them — replaces compact mode's plain-drag-
  // to-connect gesture in large mode now that plain drag moves the card
  // instead (see the mousedown handler above). Hidden until the cursor is
  // actually near their card (a permanent ring of dots around every single
  // card read as visual clutter) — tracked by cursor proximity rather than
  // Cytoscape's own node mouseover/mouseout, which only hit-tests the
  // card's own shape and would hide a handle the instant the cursor
  // reaches it (half of every handle sits *outside* its card by design, so
  // it's reachable at all).
  const handlesContainer = document.getElementById("node-handles");
  let handlesByNode = new Map();

  function createConnectHandleDots(node) {
    const sides = ["top", "right", "bottom", "left"];
    return sides.map((side) => {
      const dot = document.createElement("div");
      dot.className = "hud-node-handle";
      dot.dataset.side = side;
      handlesContainer.appendChild(dot);
      dot.addEventListener("mousedown", (e) => {
        e.preventDefault();
        e.stopPropagation();
        closeMenu();
        connectFromId = node.id();
        connectStartClient = { x: e.clientX, y: e.clientY };
        connectArmed = true;
        cy.getElementById(connectFromId).addClass("connect-from");
        connectLine.hidden = false;
        connectLineEl.setAttribute("x1", e.clientX);
        connectLineEl.setAttribute("y1", e.clientY);
        connectLineEl.setAttribute("x2", e.clientX);
        connectLineEl.setAttribute("y2", e.clientY);
      });
      return dot;
    });
  }

  function syncConnectHandles() {
    if (!handlesContainer) return;
    const validIds = new Set();
    cy.nodes().forEach((node) => {
      const data = node.data();
      if (!data.large || data.isMarker) {
        return;
      }
      validIds.add(node.id());
      if (!handlesByNode.has(node.id())) {
        const dots = createConnectHandleDots(node);
        handlesByNode.set(node.id(), dots);
      }
    });

    handlesByNode.forEach((dots, id) => {
      if (!validIds.has(id)) {
        dots.forEach((d) => d.remove());
        handlesByNode.delete(id);
      }
    });

    updateConnectHandles();
  }

  function rebuildConnectHandles() {
    if (!handlesContainer) return;
    handlesContainer.innerHTML = "";
    handlesByNode.clear();
    syncConnectHandles();
  }

  function updateConnectHandles() {
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
  cy.on("pan zoom position", updateConnectHandles);

  window.addEventListener("resize", () => {
    cy.resize();
    updateImageOverlays();
    updateConnectHandles();
    updateCurrentBadge();
  });

  const HANDLE_REVEAL_PADDING = 24;
  document.addEventListener("mousemove", (e) => {
    if (moveNodeId !== null || connectFromId !== null || handlesByNode.size === 0) {
      return;
    }
    const pos = modelPositionFromClient(e.clientX, e.clientY);
    const pad = HANDLE_REVEAL_PADDING / cy.zoom();
    handlesByNode.forEach((dots, id) => {
      const box = cy.getElementById(id).boundingBox({ includeLabels: false });
      const near = pos.x >= box.x1 - pad && pos.x <= box.x2 + pad && pos.y >= box.y1 - pad && pos.y <= box.y2 + pad;
      dots.forEach((d) => d.classList.toggle("visible", near));
    });
  });

  // ---- Tooltip (hover) for regular nodes -------------------------------
  const tooltip = document.getElementById("tooltip");
  const imagePreview = document.getElementById("image-preview");
  cy.on("mouseover", "node", (evt) => {
    const node = evt.target;
    const pos = evt.renderedPosition || screenPositionOf(node);
    tooltip.textContent = node.data("tooltip");
    positionNear(tooltip, pos);
    tooltip.hidden = false;

    const imageUrl = node.data("imageUrl");
    if (imageUrl) {
      imagePreview.src = imageUrl;
      // Below the tooltip, not on top of it — both are shown together.
      positionNear(imagePreview, { x: pos.x, y: pos.y + 20 });
      imagePreview.hidden = false;
    }
  });
  cy.on("mouseout", "node", () => {
    tooltip.hidden = true;
    imagePreview.hidden = true;
  });
  cy.on("pan zoom", () => {
    tooltip.hidden = true;
    imagePreview.hidden = true;
  });

  function screenPositionOf(node) {
    const p = node.renderedPosition();
    return { x: p.x, y: p.y };
  }

  function positionNear(el, pos) {
    el.style.left = `${pos.x + 10}px`;
    el.style.top = `${pos.y + 10}px`;
  }

  // ---- Click: select only -------------------------------------------
  // Every action that used to fire directly from a plain left-click (jump,
  // fork picker) now lives in the right-click context menu instead — a
  // plain tap here only selects the node (Cytoscape's built-in tap-to-
  // select / background-tap-to-deselect / shift-drag-to-box-select
  // behavior, enabled via boxSelectionEnabled+autounselectify above —
  // nothing to wire up manually), nothing else. See renderContextMenu for
  // where those actions moved to, and the Delete-key handler below for
  // what a multi-node selection is actually for.

  // ---- Double-click: rename (on node) or fit-view (on empty canvas) ----
  function fitView() {
    if (cy.elements().length === 0) {
      return;
    }
    cy.animate({
      fit: { eles: cy.elements(), padding: 30 },
      duration: 250,
      complete: () => {
        updateImageOverlays();
        updateNodeLabels();
        updateConnectHandles();
        updateCurrentBadge();
      },
    });
    setTimeout(() => {
      updateImageOverlays();
      updateNodeLabels();
      updateConnectHandles();
      updateCurrentBadge();
    }, 270);
  }

  cy.on("dbltap", (evt) => {
    if (evt.target === cy) {
      fitView();
    }
  });

  cy.on("dbltap", "node", (evt) => {
    const data = evt.target.data();
    if (!data.isDecisionPoint) {
      sendToHost({ type: "rename", nodeId: data.id });
    }
  });

  // ---- Right-click: unified context menu ---------------------------------
  // Everything that used to be split between the left-click fork/continue
  // popup and the right-click Umbenennen/Löschen menu now lives here in one
  // place — see the plan behind this: left-click is "select", drag is
  // "connect", right-click is "everything else".
  const menu = document.getElementById("menu");
  let pendingMenuNodeId = null;
  let pendingMenuPos = null;

  cy.on("cxttap", "node", (evt) => {
    const data = evt.target.data();
    pendingMenuNodeId = data.id;
    pendingMenuPos = evt.renderedPosition || screenPositionOf(evt.target);
    sendToHost({ type: "requestPaths", nodeId: data.id });
  });

  // ---- Edge Selection (Draw.io / Visio Style) -----------------------
  cy.on("tap", "edge", (evt) => {
    const edge = evt.target;
    const source = edge.source();
    const target = edge.target();
    if (source.data("isMarker") || target.data("isMarker")) {
      return;
    }
    evt.originalEvent?.stopPropagation();
    cy.nodes().unselect();
    cy.edges().unselect();
    edge.select();
  });

  // ---- Right-click on a connector: delete it ------------------------
  cy.on("cxttap", "edge", (evt) => {
    const edge = evt.target;
    const source = edge.source();
    const target = edge.target();
    if (source.data("isMarker") || target.data("isMarker")) {
      return;
    }

    pendingMenuNodeId = null;
    cy.edges().unselect();
    edge.select();

    menu.innerHTML = "";
    menu.appendChild(
      menuItem("Verbindung löschen", false, () => {
        closeMenu();
        sendToHost({ type: "disconnect", fromId: source.id(), toId: target.id() });
      })
    );
    positionNear(menu, evt.renderedPosition || renderedFromModel(edge.midpoint()));
    menu.hidden = false;
  });

  // ---- Flowchart Element Palette ------------------------------------
  const FLOWCHART_NODE_TYPES = [
    { label: "🟢 Start / Ende", shape: "ellipse", color: "#10b981", defaultText: "Start" },
    { label: "🟦 Prozessschritt", shape: "round-rectangle", color: "#3b82f6", defaultText: "Prozess" },
    { label: "🔶 Entscheidung", shape: "diamond", color: "#f59e0b", defaultText: "Entscheidung?" },
    { label: "🔷 Eingabe / Ausgabe", shape: "rhomboid", color: "#06b6d4", defaultText: "Daten" },
    { label: "📑 Dokument / Beleg", shape: "tag", color: "#0d9488", defaultText: "Dokument" },
    { label: "📝 Notiz / Anmerkung", shape: "rectangle", color: "#eab308", defaultText: "Notiz" },
  ];

  function showAddNodeMenu(posModel, posScreen) {
    pendingMenuNodeId = null;
    menu.innerHTML = "";

    const header = document.createElement("div");
    header.className = "hud-menu-header";
    header.textContent = "+ Element einfügen";
    menu.appendChild(header);

    FLOWCHART_NODE_TYPES.forEach((t) => {
      menu.appendChild(
        menuItem(t.label, false, () => {
          closeMenu();
          sendToHost({
            type: "addNode",
            x: posModel.x,
            y: posModel.y,
            shape: t.shape,
            color: t.color,
            label: t.defaultText,
          });
        })
      );
    });

    positionNear(menu, posScreen);
    menu.hidden = false;
  }

  // ---- Right-click on the empty canvas: add a new, isolated node -----
  cy.on("cxttap", (evt) => {
    if (evt.target !== cy || !isLargeMode) {
      return;
    }
    showAddNodeMenu(evt.position, evt.renderedPosition);
  });

  cy.on("tap pan zoom", () => closeMenu());
  document.addEventListener("click", (e) => {
    if (!menu.contains(e.target)) closeMenu();
  });

  function renderContextMenu(nodeId, paths) {
    if (nodeId !== pendingMenuNodeId) {
      return; // a redraw or a different right-click superseded this request
    }

    const node = cy.getElementById(nodeId);
    if (node.empty()) {
      return;
    }

    const data = node.data();
    menu.innerHTML = "";

    if (!data.isDecisionPoint) {
      menu.appendChild(
        menuItem("→ Weiter", true, () => {
          closeMenu();
          sendToHost({ type: "nodeClick", nodeId });
        })
      );
    }

    menu.appendChild(
      menuItem(
        data.isDecisionPoint ? "+ Neuer Pfad" : "+ Neuer Pfad ab hier",
        data.isDecisionPoint,
        () => {
          closeMenu();
          sendToHost({ type: "newPath", nodeId });
        }
      )
    );

    for (const path of paths) {
      const stepLabel = path.stepCount === 1 ? "1 Schritt" : `${path.stepCount} Schritte`;
      menu.appendChild(
        menuItem(`→ ${path.name} (${stepLabel})`, false, () => {
          closeMenu();
          sendToHost({ type: "continuePath", pathStartNodeId: path.pathStartNodeId });
        })
      );
    }

    const separator = document.createElement("div");
    separator.className = "hud-menu-separator";
    menu.appendChild(separator);

    if (!data.isDecisionPoint) {
      menu.appendChild(
        menuItem("Umbenennen", false, () => {
          closeMenu();
          sendToHost({ type: "rename", nodeId });
        })
      );
    }
    menu.appendChild(
      menuItem("Löschen", false, () => {
        closeMenu();
        sendToHost({ type: "delete", nodeId });
      })
    );

    positionNear(menu, pendingMenuPos || screenPositionOf(node));
    menu.hidden = false;
  }

  function closeMenu() {
    menu.hidden = true;
  }

  function menuItem(text, primary, onClick) {
    const div = document.createElement("div");
    div.className = "hud-menu-item" + (primary ? " primary" : "");
    div.textContent = text;
    div.addEventListener("click", onClick);
    return div;
  }

  // ---- Delete key: bulk-delete the current box-selection -----------------
  // Shift+drag (see boxSelectionEnabled above) selects several nodes at
  // once; Delete sends one "delete" per selected node through the exact
  // same host message a single right-click Löschen already uses, so C#'s
  // existing cascade-confirmation logic runs unchanged for each one.
  // ---- Delete/Backspace key: delete selected edges or nodes ---------
  document.addEventListener("keydown", (e) => {
    if (e.key !== "Delete" && e.key !== "Backspace") {
      return;
    }
    // Don't delete diagram elements if user is typing in a textarea or search input
    if (editingNodeId !== null || (searchInput && document.activeElement === searchInput)) {
      return;
    }

    const selectedEdges = cy.edges(":selected");
    if (selectedEdges.length > 0) {
      e.preventDefault();
      selectedEdges.forEach((edge) => {
        sendToHost({ type: "disconnect", fromId: edge.source().id(), toId: edge.target().id() });
      });
      return;
    }

    const selectedNodes = cy.nodes(":selected");
    if (selectedNodes.length > 0) {
      e.preventDefault();
      selectedNodes.forEach((n) => sendToHost({ type: "delete", nodeId: n.id() }));
    }
  });

  // ---- Drag-to-connect (line-based; the source node never moves) --------
  // Nodes are permanently non-grabbable (see the elements() mapping above)
  // — physically dragging the whole node onto its target turned out to be
  // an awkward gesture, so this instead tracks a raw mousedown-on-node ->
  // mousemove -> mouseup sequence itself and draws a plain line from the
  // fixed source node to the cursor, snapping onto whichever valid node is
  // currently under it. Finalizes via the exact same "connect" host message
  // (see IFlowWriter.ConnectNodes: additive, never removes an existing
  // edge, restricted to non-marker nodes on both ends). The
  // reachability guard below (ancestorsOf the source node) prevents
  // picking a target that can already reach the source — connecting into
  // one of its own ancestors would close a cycle (matches the C# rule in
  // IFlowWriter.ConnectNodes: reject if toId already reaches fromId).
  //
  // mousemove/mouseup are bound on the *document*, not on cy — this panel
  // is small, and releasing over the toolbar, tooltip, or context menu
  // (separate DOM elements stacked over the canvas) never reached a
  // cy-scoped mouseup at all, leaving the gesture permanently "armed": the
  // line stuck on screen forever with no further click able to clear it.
  // A document-level listener fires no matter what's under the cursor.
  const CONNECT_DRAG_THRESHOLD = 6; // px of movement before the gesture "arms" — below this, it's just a click
  let connectFromId = null;
  let connectStartClient = null;
  let connectArmed = false;

  const cyContainer = document.getElementById("cy");
  const connectLine = document.getElementById("connect-line");
  const connectLineEl = connectLine.querySelector("line");

  // #connect-line covers the full viewport (see flow.css), so raw
  // clientX/Y already are its coordinate space — no conversion needed for
  // drawing. Hit-testing against node bounding boxes needs *model* space
  // though, which does need the cy container's own offset + current
  // pan/zoom factored in.
  function modelPositionFromClient(clientX, clientY) {
    const rect = cyContainer.getBoundingClientRect();
    const pan = cy.pan();
    const zoom = cy.zoom();
    return { x: (clientX - rect.left - pan.x) / zoom, y: (clientY - rect.top - pan.y) / zoom };
  }

  // The inverse: model space (e.g. an edge's midpoint(), which has no
  // renderedPosition of its own) back to on-screen coordinates, relative to
  // the cy container — matches what evt.renderedPosition would give for a
  // node tap.
  function renderedFromModel(modelPos) {
    const pan = cy.pan();
    const zoom = cy.zoom();
    return { x: modelPos.x * zoom + pan.x, y: modelPos.y * zoom + pan.y };
  }

  // ---- Drag-to-move (large/editing mode only) ----------------------------
  // Compact (live-recording minimap) mode never moves nodes — its schematic
  // grid layout (see FlowPreviewOverlay.BuildPreviewPayload) is recomputed
  // on every redraw regardless of what's on screen, so a drag there would
  // just snap back the instant anything else changed. Large mode positions
  // nodes from their real, persisted coordinates instead specifically so a
  // drag here (see IFlowWriter.MoveNode — deliberately skips the
  // auto-layout) actually sticks.
  const MOVE_DRAG_THRESHOLD = 5; // px of movement before this counts as a drag
  let moveNodeId = null;
  let moveStartClient = null;
  let moveStartPos = null;
  let moveArmed = false;
  let moveMultiNodes = []; // Tracks all selected nodes for multi-node drag

  cy.on("mousedown", "node", (evt) => {
    const data = evt.target.data();
    if (data.large) {
      closeMenu();
      moveNodeId = data.id;
      moveStartClient = { x: evt.originalEvent.clientX, y: evt.originalEvent.clientY };
      moveStartPos = { ...evt.target.position() };
      moveArmed = false;

      // If this node is part of a multi-selection, drag all selected nodes together (Draw.io style)
      const selected = cy.nodes(":selected");
      if (selected.length > 1 && selected.contains(evt.target)) {
        moveMultiNodes = selected.map((n) => ({ id: n.id(), startPos: { ...n.position() } }));
      } else {
        moveMultiNodes = [{ id: data.id, startPos: { ...evt.target.position() } }];
      }
      return;
    }

    // Compact mode: plain drag on a node connects it to whatever's under the cursor on release.
    if (data.isMarker) {
      return; // decision points/path starts are never valid connect endpoints
    }

    closeMenu();
    connectFromId = data.id;
    connectStartClient = { x: evt.originalEvent.clientX, y: evt.originalEvent.clientY };
    connectArmed = false;
  });

  document.addEventListener("mousemove", (e) => {
    if (moveNodeId !== null) {
      if (!moveArmed) {
        if (Math.hypot(e.clientX - moveStartClient.x, e.clientY - moveStartClient.y) < MOVE_DRAG_THRESHOLD) {
          return;
        }
        moveArmed = true;
      }
      const zoom = cy.zoom();
      const dx = (e.clientX - moveStartClient.x) / zoom;
      const dy = (e.clientY - moveStartClient.y) / zoom;

      for (const item of moveMultiNodes) {
        cy.getElementById(item.id).position({ x: item.startPos.x + dx, y: item.startPos.y + dy });
      }

      updateImageOverlays();
      updateNodeLabels(); // Keep text boxes locked to moving nodes!
      updateConnectHandles();
      updateCurrentBadge();
      return;
    }

    if (connectFromId === null) {
      return;
    }

    if (!connectArmed) {
      if (Math.hypot(e.clientX - connectStartClient.x, e.clientY - connectStartClient.y) < CONNECT_DRAG_THRESHOLD) {
        return;
      }
      connectArmed = true;
      cy.getElementById(connectFromId).addClass("connect-from");
      connectLine.hidden = false;
    }

    connectLineEl.setAttribute("x1", connectStartClient.x);
    connectLineEl.setAttribute("y1", connectStartClient.y);

    cy.nodes(".drop-target").removeClass("drop-target");
    document.querySelectorAll(".hud-node-handle.snapped").forEach((el) => el.classList.remove("snapped"));

    const target = findConnectTarget(connectFromId, modelPositionFromClient(e.clientX, e.clientY));
    if (target) {
      target.node.addClass("drop-target");
      const snappedClient = renderedFromModel(target.port);
      connectLineEl.setAttribute("x2", snappedClient.x);
      connectLineEl.setAttribute("y2", snappedClient.y);

      const targetDots = handlesByNode.get(target.node.id());
      if (targetDots) {
        const portDot = targetDots.find((d) => d.dataset.side === target.port.side);
        if (portDot) {
          portDot.classList.add("snapped", "visible");
        }
      }
    } else {
      connectLineEl.setAttribute("x2", e.clientX);
      connectLineEl.setAttribute("y2", e.clientY);
    }
  });

  document.addEventListener("mouseup", (e) => {
    if (moveNodeId !== null) {
      if (moveArmed) {
        for (const item of moveMultiNodes) {
          const pos = cy.getElementById(item.id).position();
          sendToHost({ type: "move", nodeId: item.id, x: pos.x, y: pos.y });
        }
      }
      moveNodeId = null;
      moveStartClient = null;
      moveStartPos = null;
      moveArmed = false;
      moveMultiNodes = [];
      return;
    }
    endConnectGesture(e.clientX, e.clientY);
  });

  document.addEventListener("mouseleave", () => {
    moveNodeId = null;
    moveArmed = false;
    moveMultiNodes = [];
    endConnectGesture(null, null);
  });

  window.addEventListener("blur", () => {
    moveNodeId = null;
    moveArmed = false;
    moveMultiNodes = [];
    endConnectGesture(null, null);
  });

  function endConnectGesture(clientX, clientY) {
    if (connectFromId === null) {
      return;
    }

    if (connectArmed && clientX !== null) {
      const target = findConnectTarget(connectFromId, modelPositionFromClient(clientX, clientY));
      if (target) {
        sendToHost({ type: "connect", fromId: connectFromId, toId: target.node.id() });
      }
    }

    cy.getElementById(connectFromId).removeClass("connect-from");
    cy.nodes(".drop-target").removeClass("drop-target");
    document.querySelectorAll(".hud-node-handle.snapped").forEach((el) => el.classList.remove("snapped"));
    connectLine.hidden = true;
    connectLineEl.setAttribute("x1", "0");
    connectLineEl.setAttribute("y1", "0");
    connectLineEl.setAttribute("x2", "0");
    connectLineEl.setAttribute("y2", "0");
    void connectLine.offsetHeight;
    connectFromId = null;
    connectStartClient = null;
    connectArmed = false;
  }

  function distanceToBox(box, p) {
    const dx = Math.max(box.x1 - p.x, 0, p.x - box.x2);
    const dy = Math.max(box.y1 - p.y, 0, p.y - box.y2);
    return Math.hypot(dx, dy);
  }

  function getClosestPort(box, p) {
    const midX = (box.x1 + box.x2) / 2;
    const midY = (box.y1 + box.y2) / 2;
    const ports = [
      { side: "top", x: midX, y: box.y1 },
      { side: "right", x: box.x2, y: midY },
      { side: "bottom", x: midX, y: box.y2 },
      { side: "left", x: box.x1, y: midY },
    ];
    let best = ports[0];
    let minDist = Math.hypot(ports[0].x - p.x, ports[0].y - p.y);
    for (let i = 1; i < ports.length; i++) {
      const d = Math.hypot(ports[i].x - p.x, ports[i].y - p.y);
      if (d < minDist) {
        minDist = d;
        best = ports[i];
      }
    }
    return best;
  }

  function findConnectTarget(fromId, modelPos) {
    const invalidTargets = ancestorsOf(fromId);
    const candidates = cy.nodes().filter((n) => {
      const d = n.data();
      return n.id() !== fromId && !d.isMarker && !invalidTargets.has(n.id());
    });

    const SNAP_RADIUS = 35; // Model distance threshold for magnetic snapping
    let bestTarget = null;
    let minDistance = Infinity;

    for (let i = 0; i < candidates.length; i++) {
      const n = candidates[i];
      const box = n.boundingBox();
      const dist = distanceToBox(box, modelPos);
      if (dist === 0) {
        return { node: n, port: getClosestPort(box, modelPos), distance: 0 };
      }
      if (dist <= SNAP_RADIUS && dist < minDistance) {
        minDistance = dist;
        bestTarget = { node: n, port: getClosestPort(box, modelPos), distance: dist };
      }
    }
    return bestTarget;
  }

  /// Every node that can reach nodeId via existing *structural* forward
  /// edges (walked backward via incoming edges). Manual cross-connects are
  /// deliberately skipped here — same reasoning as the C# backend's own
  /// ConnectNodes cycle guard (see CanvasFlowWriter.ConnectNodes): they're
  /// additive references, not real structural relationships, so they can
  /// never actually close a cycle. Without this filter, connecting B->D
  /// once made D->B look like it would close a cycle through that same
  /// manual edge and silently blocked the reverse direction — confirmed as
  /// a real bug (one direction of a cross-connect worked, the other
  /// didn't), and it would have made true bidirectional arrows impossible.
  function ancestorsOf(nodeId) {
    const result = new Set();
    const queue = [nodeId];
    while (queue.length > 0) {
      const id = queue.shift();
      cy.getElementById(id)
        .incomers("edge")
        .filter((e) => !e.data("manual"))
        .forEach((e) => {
          const sourceId = e.source().id();
          if (!result.has(sourceId)) {
            result.add(sourceId);
            queue.push(sourceId);
          }
        });
    }
    return result;
  }

  // ---- Modern HUD Dock & Search ------------------------------------
  const searchInput = document.getElementById("hud-search");
  const searchClear = document.getElementById("hud-search-clear");
  const btnZoomIn = document.getElementById("hud-btn-zoom-in");
  const btnZoomOut = document.getElementById("hud-btn-zoom-out");
  const btnFit = document.getElementById("hud-btn-fit");

  function applySearch(query) {
    const q = (query || "").trim().toLowerCase();
    if (searchClear) {
      searchClear.hidden = q.length === 0;
    }

    if (!q) {
      cy.elements().removeClass("search-hit search-dimmed");
      nodeLabels.forEach((lbl) => lbl.classList.remove("search-hit", "search-dimmed"));
      return;
    }

    const hits = [];
    cy.batch(() => {
      cy.nodes().forEach((n) => {
        const d = n.data();
        const text = `${d.label || ""} ${d.permLabel || ""} ${d.displayLabel || ""} ${d.tooltip || ""}`.toLowerCase();
        const isHit = text.includes(q);
        if (isHit) {
          n.removeClass("search-dimmed").addClass("search-hit");
          hits.push(n);
        } else {
          n.removeClass("search-hit").addClass("search-dimmed");
        }
        const lbl = nodeLabels.get(n.id());
        if (lbl) {
          lbl.classList.toggle("search-hit", isHit);
          lbl.classList.toggle("search-dimmed", !isHit);
        }
      });
      cy.edges().forEach((e) => {
        const sHit = e.source().hasClass("search-hit");
        const tHit = e.target().hasClass("search-hit");
        if (sHit && tHit) {
          e.removeClass("search-dimmed");
        } else {
          e.addClass("search-dimmed");
        }
      });
    });

    if (hits.length > 0) {
      cy.animate({ center: { eles: hits[0] }, duration: 200 });
    }
  }

  if (searchInput) {
    searchInput.addEventListener("input", (e) => applySearch(e.target.value));
    searchInput.addEventListener("keydown", (e) => {
      if (e.key === "Escape") {
        searchInput.value = "";
        applySearch("");
        searchInput.blur();
      }
    });
  }

  if (searchClear) {
    searchClear.addEventListener("click", () => {
      if (searchInput) {
        searchInput.value = "";
        applySearch("");
        searchInput.focus();
      }
    });
  }

  if (btnZoomIn) {
    btnZoomIn.addEventListener("click", () => {
      cy.animate({ zoom: Math.min(cy.zoom() * 1.3, cy.maxZoom()), duration: 150 });
    });
  }

  if (btnZoomOut) {
    btnZoomOut.addEventListener("click", () => {
      cy.animate({ zoom: Math.max(cy.zoom() / 1.3, cy.minZoom()), duration: 150 });
    });
  }

  if (btnFit) {
    btnFit.addEventListener("click", () => fitView());
  }

  const btnAddElement = document.getElementById("hud-btn-add-element");
  if (btnAddElement) {
    btnAddElement.addEventListener("click", (e) => {
      e.stopPropagation();
      const pan = cy.pan();
      const zoom = cy.zoom();
      const centerX = (cy.width() / 2 - pan.x) / zoom;
      const centerY = (cy.height() / 2 - pan.y) / zoom;
      const rect = btnAddElement.getBoundingClientRect();
      showAddNodeMenu({ x: centerX, y: centerY }, { x: rect.left, y: rect.bottom + 6 });
    });
  }

  // ---- Host message dispatch ---------------------------------------
  function dispatch(message) {
    switch (message.type) {
      case "preview":
        render(message);
        break;
      case "pathsResult":
        renderContextMenu(message.nodeId, message.paths || []);
        break;
      case "fitView":
        fitView();
        break;
      default:
        console.warn("Unknown message from host:", message);
    }
  }

  onHostMessage(dispatch);
})();
