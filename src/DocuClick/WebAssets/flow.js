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
          "line-color": "data(color)",
          "curve-style": "straight",
          "target-arrow-shape": "triangle",
          "target-arrow-color": "data(color)",
          "arrow-scale": 1.1,
          "line-style": "solid",
          "underlay-color": "#38bdf8",
          "underlay-padding": 8,
          "underlay-opacity": 0,
        },
      },
      {
        selector: "edge[lineStyle = 'dashed']",
        style: {
          "line-style": "dashed",
          "line-dash-pattern": [6, 4],
        },
      },
      {
        selector: "edge[lineStyle = 'dotted']",
        style: {
          "line-style": "dotted",
        },
      },
      {
        selector: "edge:selected",
        style: {
          width: 4.5,
          "line-color": "#38bdf8",
          "target-arrow-color": "#38bdf8",
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
        selector: "node.search-dimmed, node.path-dimmed, edge.path-dimmed",
        style: {
          opacity: 0.18,
        },
      },
      {
        selector: "node.path-highlighted",
        style: {
          "border-color": "#38bdf8",
          "border-width": 4,
          opacity: 1,
        },
      },
      {
        selector: "edge.path-highlighted",
        style: {
          width: 4.5,
          opacity: 1,
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

      // Add or update edges
      for (let i = 0; i < edges.length; i++) {
        const e = edges[i];
        const edgeId = `${e.source}->${e.target}`;
        const cyEdge = cy.getElementById(edgeId);
        const color = e.color || "#3b82f6";
        const lineStyle = e.lineStyle || "solid";
        if (cyEdge.length === 0) {
          toAdd.push({
            group: "edges",
            data: { id: edgeId, source: e.source, target: e.target, manual: e.manual, color: color, lineStyle: lineStyle },
          });
        } else {
          cyEdge.data("color", color);
          cyEdge.data("lineStyle", lineStyle);
          cyEdge.data("manual", e.manual);
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

    if (typeof buildGuideList === "function") {
      buildGuideList();
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
      pathName: n.pathName || undefined,
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
  }

  // Centralized requestAnimationFrame scheduler to batch all DOM overlay updates
  // strictly to the screen refresh rate (60/120/144 FPS) without DOM thrashing
  let rafId = null;
  function scheduleRafUpdate() {
    if (rafId !== null) return;
    rafId = requestAnimationFrame(() => {
      rafId = null;
      updateImageOverlays();
      updateNodeLabels();
      updateConnectHandles();
      updateCurrentBadge();
    });
  }

  function updateImageOverlays() {
    const w = cy.width();
    const h = cy.height();
    const pad = 120;
    imageOverlays.forEach((img, id) => {
      const node = cy.getElementById(id);
      if (node.empty()) {
        return;
      }
      const box = node.renderedBoundingBox({ includeLabels: false });
      // Viewport culling: offscreen images are hidden and skipped
      if (box.x2 < -pad || box.x1 > w + pad || box.y2 < -pad || box.y1 > h + pad) {
        img.style.display = "none";
        return;
      }
      img.style.display = "";
      img.style.left = `${box.x1}px`;
      img.style.top = `${box.y1}px`;
      img.style.width = `${box.x2 - box.x1}px`;
      img.style.height = `${box.y2 - box.y1}px`;
    });
  }
  cy.on("pan zoom position", scheduleRafUpdate);

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

    labelEl.addEventListener("mouseenter", () => {
      if (handleHoverTimeout) {
        clearTimeout(handleHoverTimeout);
        handleHoverTimeout = null;
      }
      showHandlesForNode(data.id);
    });
    labelEl.addEventListener("mouseleave", () => {
      hideHandlesWithDelay();
    });

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

  // ---- Current-node badge -------------------------------------------
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

  // ---- Connection handles (large/editing mode only) ----------------------
  const handlesContainer = document.getElementById("node-handles");
  let handlesByNode = new Map();
  let activeHoverNodeId = null;
  let handleHoverTimeout = null;

  function showHandlesForNode(id) {
    if (handleHoverTimeout) {
      clearTimeout(handleHoverTimeout);
      handleHoverTimeout = null;
    }
    if (activeHoverNodeId === id) return;
    if (activeHoverNodeId && handlesByNode.has(activeHoverNodeId)) {
      handlesByNode.get(activeHoverNodeId).forEach((d) => d.classList.remove("visible"));
    }
    activeHoverNodeId = id;
    if (id && handlesByNode.has(id)) {
      handlesByNode.get(id).forEach((d) => d.classList.add("visible"));
    }
  }

  function hideHandlesWithDelay() {
    if (handleHoverTimeout) clearTimeout(handleHoverTimeout);
    handleHoverTimeout = setTimeout(() => {
      if (activeHoverNodeId && handlesByNode.has(activeHoverNodeId)) {
        handlesByNode.get(activeHoverNodeId).forEach((d) => d.classList.remove("visible"));
      }
      activeHoverNodeId = null;
      handleHoverTimeout = null;
    }, 200);
  }

  function createConnectHandleDots(node) {
    const sides = ["top", "right", "bottom", "left"];
    return sides.map((side) => {
      const dot = document.createElement("div");
      dot.className = "hud-node-handle";
      dot.dataset.side = side;
      handlesContainer.appendChild(dot);

      dot.addEventListener("mouseenter", () => {
        if (handleHoverTimeout) {
          clearTimeout(handleHoverTimeout);
          handleHoverTimeout = null;
        }
        showHandlesForNode(node.id());
      });
      dot.addEventListener("mouseleave", () => {
        hideHandlesWithDelay();
      });

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
    const w = cy.width();
    const h = cy.height();
    const pad = 80;
    handlesByNode.forEach((dots, id) => {
      const node = cy.getElementById(id);
      if (node.empty()) {
        return;
      }
      const box = node.renderedBoundingBox({ includeLabels: false });
      // Viewport culling: offscreen handles are hidden and skipped
      if (box.x2 < -pad || box.x1 > w + pad || box.y2 < -pad || box.y1 > h + pad) {
        dots.forEach((dot) => { dot.style.display = "none"; });
        return;
      }
      const midX = (box.x1 + box.x2) / 2;
      const midY = (box.y1 + box.y2) / 2;
      const points = [
        { x: midX, y: box.y1 },
        { x: box.x2, y: midY },
        { x: midX, y: box.y2 },
        { x: box.x1, y: midY },
      ];
      dots.forEach((dot, i) => {
        dot.style.display = "";
        dot.style.left = `${points[i].x}px`;
        dot.style.top = `${points[i].y}px`;
      });
    });
  }

  window.addEventListener("resize", () => {
    cy.resize();
    scheduleRafUpdate();
  });

  // Zero-overhead event-driven handle reveal instead of 1000Hz mousemove bounding box loops
  cy.on("mouseover", "node", (evt) => {
    if (moveNodeId !== null || connectFromId !== null) return;
    showHandlesForNode(evt.target.id());
  });

  cy.on("mouseout", "node", () => {
    if (moveNodeId !== null || connectFromId !== null) return;
    hideHandlesWithDelay();
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

  const COLOR_PRESETS = [
    "#10b981", "#3b82f6", "#f59e0b", "#06b6d4", "#0d9488", "#eab308", "#8b5cf6", "#ef4444", "#64748b"
  ];

  // ---- Right-click on a connector: edit style / reverse / delete ----
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

    const currentColor = edge.data("color") || "#3b82f6";
    const currentLineStyle = edge.data("lineStyle") || "solid";
    const fromLabel = source.data("label") || "Knoten";
    const toLabel = target.data("label") || "Knoten";
    const headerTitle = `Verbindung: ${fromLabel.slice(0, 10)} → ${toLabel.slice(0, 10)}`;

    menu.innerHTML = "";

    const hdr = document.createElement("div");
    hdr.className = "hud-menu-header";
    hdr.textContent = headerTitle;
    menu.appendChild(hdr);

    const colorHdr = document.createElement("div");
    colorHdr.className = "hud-menu-header";
    colorHdr.textContent = "🎨 Farbe wählen";
    menu.appendChild(colorHdr);

    const colorRow = document.createElement("div");
    colorRow.className = "hud-menu-color-row";
    COLOR_PRESETS.forEach((c) => {
      const dot = document.createElement("button");
      dot.type = "button";
      dot.className = "hud-color-dot" + (currentColor.toLowerCase() === c.toLowerCase() ? " active" : "");
      dot.style.backgroundColor = c;
      dot.title = c;
      dot.addEventListener("click", (e) => {
        e.stopPropagation();
        closeMenu();
        edge.data("color", c);
        sendToHost({
          type: "setEdgeStyle",
          fromId: source.id(),
          toId: target.id(),
          color: c,
          lineStyle: edge.data("lineStyle") || "solid"
        });
      });
      colorRow.appendChild(dot);
    });
    menu.appendChild(colorRow);

    const sep1 = document.createElement("div");
    sep1.className = "hud-menu-separator";
    menu.appendChild(sep1);

    menu.appendChild(
      menuItem(currentLineStyle === "dashed" ? "Linienstil: Durchgezogen" : "Linienstil: Gestrichelt", false, () => {
        closeMenu();
        const nextStyle = currentLineStyle === "dashed" ? "solid" : "dashed";
        edge.data("lineStyle", nextStyle);
        sendToHost({
          type: "setEdgeStyle",
          fromId: source.id(),
          toId: target.id(),
          color: edge.data("color") || "#3b82f6",
          lineStyle: nextStyle
        });
      })
    );

    menu.appendChild(
      menuItem("⇄ Richtung umkehren", false, () => {
        closeMenu();
        sendToHost({ type: "reverseEdge", fromId: source.id(), toId: target.id() });
      })
    );

    const sep2 = document.createElement("div");
    sep2.className = "hud-menu-separator";
    menu.appendChild(sep2);

    menu.appendChild(
      menuItem("Verbindung löschen", false, () => {
        closeMenu();
        sendToHost({ type: "disconnect", fromId: source.id(), toId: target.id() });
      }, true)
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

    menu.appendChild(
      menuItem("📷 Bild einfügen...", false, () => {
        closeMenu();
        sendToHost({
          type: "addImageNode",
          x: posModel.x,
          y: posModel.y,
        });
      })
    );

    const bgSep = document.createElement("div");
    bgSep.className = "hud-menu-separator";
    menu.appendChild(bgSep);

    menu.appendChild(
      menuItem("📖 Anleitung ein-/ausblenden", false, () => {
        closeMenu();
        toggleGuide();
      })
    );

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
      menuItem("📖 In Anleitung anzeigen", false, () => {
        closeMenu();
        highlightGuideItem(nodeId);
      })
    );

    menu.appendChild(
      menuItem("Löschen", false, () => {
        closeMenu();
        sendToHost({ type: "delete", nodeId });
      }, true)
    );

    positionNear(menu, pendingMenuPos || screenPositionOf(node));
    menu.hidden = false;
  }

  function closeMenu() {
    menu.hidden = true;
  }

  function menuItem(text, primary, onClick, danger = false) {
    const div = document.createElement("div");
    div.className = "hud-menu-item" + (primary ? " primary" : "") + (danger ? " danger" : "");
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

      cy.batch(() => {
        for (const item of moveMultiNodes) {
          cy.getElementById(item.id).position({ x: item.startPos.x + dx, y: item.startPos.y + dy });
        }
      });

      scheduleRafUpdate();
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
        if (moveMultiNodes.length > 1) {
          const moves = moveMultiNodes.map((item) => {
            const pos = cy.getElementById(item.id).position();
            return { nodeId: item.id, x: pos.x, y: pos.y };
          });
          sendToHost({ type: "moveBatch", moves });
        } else {
          const pos = cy.getElementById(moveNodeId).position();
          sendToHost({ type: "move", nodeId: moveNodeId, x: pos.x, y: pos.y });
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

  // ---- SOP Guide Drawer ---------------------------------------------
  const guideDrawer = document.getElementById("guide-drawer");
  const guideToggleBtn = document.getElementById("hud-btn-guide");
  const guideCloseBtn = document.getElementById("guide-close-btn");
  const guideList = document.getElementById("guide-list");
  const guidePathSelect = document.getElementById("guide-path-select");
  const guideTabAll = document.getElementById("guide-tab-all");
  const guideTabImages = document.getElementById("guide-tab-images");

  let guideCurrentPathId = "all";
  let guideShowOnlyImages = false;

  function toggleGuide(show) {
    if (!guideDrawer) return;
    const willShow = show !== undefined ? show : !guideDrawer.classList.contains("open");
    guideDrawer.classList.toggle("open", willShow);
  }
  guideToggleBtn?.addEventListener("click", () => toggleGuide());
  guideCloseBtn?.addEventListener("click", () => toggleGuide(false));

  function escapeHtml(str) {
    if (!str) return "";
    const div = document.createElement("div");
    div.textContent = str;
    return div.innerHTML;
  }

  function getGraphPaths() {
    const allNodes = cy.nodes();
    if (allNodes.empty()) return [];

    const branchTargets = new Map();
    const decisionNodes = allNodes.filter(
      (n) => n.data("shape") === "diamond" ||
             (n.data("label") || "").includes("Abzweigung") ||
             !!n.data("isDecisionPoint") ||
             n.outgoers("edge").length > 1
    );

    decisionNodes.forEach((dec) => {
      dec.outgoers("edge").forEach((e) => {
        const tgt = e.target();
        if (!branchTargets.has(tgt.id())) {
          const rawLabel = tgt.data("label") || "Zweig";
          const name = rawLabel.startsWith("↳ Pfad: ") ? rawLabel.slice(8) : (tgt.data("pathName") || rawLabel);
          branchTargets.set(tgt.id(), {
            name: name,
            color: tgt.data("color") || e.data("color") || "#3b82f6",
            originId: dec.id(),
          });
        }
      });
    });

    allNodes
      .filter((n) => (n.data("label") || "").startsWith("↳ Pfad: ") || !!n.data("isPathStart"))
      .forEach((n) => {
        if (!branchTargets.has(n.id())) {
          const rawLabel = n.data("label") || "";
          const name = rawLabel.startsWith("↳ Pfad: ") ? rawLabel.slice(8) : (n.data("pathName") || rawLabel || "Pfad");
          branchTargets.set(n.id(), {
            name: name,
            color: n.data("color") || "#3b82f6",
            originId: null,
          });
        }
      });

    function tracePath(startNodeId) {
      const nodeSet = new Set();
      const queue = [startNodeId];
      while (queue.length > 0) {
        const id = queue.shift();
        if (nodeSet.has(id)) continue;
        nodeSet.add(id);
        const node = cy.getElementById(id);
        if (!node.empty()) {
          node.outgoers("edge").forEach((edge) => {
            const tgt = edge.target();
            if (!nodeSet.has(tgt.id())) {
              queue.push(tgt.id());
            }
          });
        }
      }
      return nodeSet;
    }

    const paths = [];

    let roots = allNodes.filter((n) => n.incomers("edge").filter((e) => !e.data("manual")).length === 0);
    if (roots.empty()) roots = allNodes.filter((n) => n.incomers("edge").length === 0);
    if (!roots.empty()) {
      const mainRoot = roots.toArray().sort((a, b) => (a.position().y - b.position().y) || (a.position().x - b.position().x))[0];
      const mainNodeSet = new Set();
      const queue = [mainRoot.id()];
      while (queue.length > 0) {
        const id = queue.shift();
        if (mainNodeSet.has(id)) continue;
        mainNodeSet.add(id);
        const node = cy.getElementById(id);
        if (!node.empty() && node.data("shape") !== "diamond" && !node.data("isDecisionPoint")) {
          node.outgoers("edge").filter((e) => !e.data("manual")).forEach((edge) => {
            const tgt = edge.target();
            if (!mainNodeSet.has(tgt.id()) && !branchTargets.has(tgt.id())) {
              queue.push(tgt.id());
            }
          });
        }
      }
      if (branchTargets.size > 0) {
        paths.push({
          id: "main",
          name: "Hauptablauf",
          color: "#2563eb",
          nodeIds: mainNodeSet,
          startNodeId: mainRoot.id(),
        });
      }
    }

    branchTargets.forEach((info, startId) => {
      const nodeIds = tracePath(startId);
      paths.push({
        id: startId,
        name: info.name,
        color: info.color,
        nodeIds: nodeIds,
        startNodeId: startId,
      });
    });

    return paths;
  }

  function updatePathSelectOptions(paths) {
    if (!guidePathSelect) return;
    const currentVal = guidePathSelect.value;
    guidePathSelect.innerHTML = `<option value="all">🌐 Gesamter Ablauf (${cy.nodes().length} Schritte)</option>`;
    paths.forEach((p) => {
      const opt = document.createElement("option");
      opt.value = p.id;
      opt.textContent = `↳ ${p.name} (${p.nodeIds.size} Schritte)`;
      guidePathSelect.appendChild(opt);
    });
    if (paths.some((p) => p.id === currentVal)) {
      guidePathSelect.value = currentVal;
    } else {
      guidePathSelect.value = "all";
      guideCurrentPathId = "all";
    }
  }

  guidePathSelect?.addEventListener("change", () => {
    guideCurrentPathId = guidePathSelect.value;
    const paths = getGraphPaths();
    const selectedPath = paths.find((p) => p.id === guideCurrentPathId);

    if (guideCurrentPathId === "all" || !selectedPath) {
      cy.elements().removeClass("path-highlighted path-dimmed");
      cy.animate({ fit: { eles: cy.elements(), padding: 40 }, duration: 250 });
    } else {
      cy.elements().removeClass("path-highlighted").addClass("path-dimmed");
      const activeNodes = cy.collection(Array.from(selectedPath.nodeIds).map((id) => cy.getElementById(id)));
      const activeEdges = activeNodes.edgesWith(activeNodes);
      activeNodes.removeClass("path-dimmed").addClass("path-highlighted");
      activeEdges.removeClass("path-dimmed").addClass("path-highlighted");
      if (!activeNodes.empty()) {
        cy.animate({ fit: { eles: activeNodes, padding: 50 }, duration: 250 });
      }
    }
    buildGuideList();
  });

  guideTabAll?.addEventListener("click", () => {
    guideShowOnlyImages = false;
    guideTabAll.classList.add("active");
    guideTabImages?.classList.remove("active");
    buildGuideList();
  });

  guideTabImages?.addEventListener("click", () => {
    guideShowOnlyImages = true;
    guideTabImages.classList.add("active");
    guideTabAll?.classList.remove("active");
    buildGuideList();
  });

  function buildGuideList() {
    if (!guideList) return;
    guideList.innerHTML = "";

    const paths = getGraphPaths();
    updatePathSelectOptions(paths);

    const selectedPath = paths.find((p) => p.id === guideCurrentPathId);
    const allowedNodeIds = (selectedPath && guideCurrentPathId !== "all") ? selectedPath.nodeIds : null;

    const allNodes = cy.nodes().toArray().sort((a, b) => {
      const posA = a.position();
      const posB = b.position();
      const rowDiff = Math.round(posA.y / 50) - Math.round(posB.y / 50);
      if (rowDiff !== 0) return rowDiff;
      return posA.x - posB.x;
    });

    const filteredNodes = allNodes.filter((node) => {
      if (allowedNodeIds && !allowedNodeIds.has(node.id())) return false;
      if (guideShowOnlyImages && !node.data("imageUrl")) return false;
      return true;
    });

    if (filteredNodes.length === 0) {
      const emptyMsg = document.createElement("div");
      emptyMsg.style.cssText = "padding: 24px; text-align: center; color: var(--text-muted); font-size: 12px;";
      emptyMsg.textContent = guideShowOnlyImages ? "Keine Screenshots in dieser Ansicht vorhanden." : "Keine Schritte vorhanden.";
      guideList.appendChild(emptyMsg);
      return;
    }

    let stepCounter = 1;
    filteredNodes.forEach((node) => {
      const nodeId = node.id();
      const rawLabel = node.data("label") || "";
      const shape = node.data("shape") || "round-rectangle";
      const color = node.data("color") || "#3b82f6";
      const imageUrl = node.data("imageUrl");
      const isDecision = shape === "diamond" || rawLabel.includes("Abzweigung") || !!node.data("isDecisionPoint");
      const isPathStart = rawLabel.startsWith("↳ Pfad: ") || !!node.data("isPathStart");
      const incomers = node.incomers("edge");
      const isMerge = incomers.length > 1;

      if (isPathStart && guideCurrentPathId === "all") {
        const divider = document.createElement("div");
        divider.className = "guide-path-divider";
        divider.style.borderLeft = `3px solid ${color}`;
        divider.innerHTML = `<span class="guide-path-badge" style="background:${color};"></span><span>${escapeHtml(rawLabel)}</span>`;
        guideList.appendChild(divider);
      }

      const card = document.createElement("div");
      card.className = "guide-card" + (isDecision ? " guide-card-decision" : "");
      card.setAttribute("data-node-id", nodeId);

      const badgeHtml = isDecision
        ? `<span class="guide-card-num guide-badge-decision" title="Entscheidung / Verzweigung">◆</span>`
        : `<span class="guide-card-num" style="background:${color};">${stepCounter++}</span>`;

      let displayTitle = rawLabel;
      if (isDecision) {
        displayTitle = rawLabel || "Entscheidung treffen";
      } else if (isPathStart) {
        displayTitle = rawLabel.startsWith("↳ Pfad: ") ? rawLabel.slice(8) : (node.data("pathName") || rawLabel);
      } else if (!displayTitle) {
        displayTitle = `Schritt ${stepCounter}`;
      }

      const mergeHtml = isMerge ? `<span class="guide-badge-merge" title="Mehrere Abläufe laufen hier zusammen">⇄ Zusammenführung</span>` : "";

      let branchButtonsHtml = "";
      if (isDecision) {
        const outEdges = node.outgoers("edge");
        if (outEdges.length > 0) {
          branchButtonsHtml = `
            <div class="guide-branch-buttons">
              ${outEdges.toArray().map((e) => {
                const tgt = e.target();
                const tgtLabel = tgt.data("label") || "Folgeschritt";
                const clean = tgtLabel.startsWith("↳ Pfad: ") ? tgtLabel.slice(8) : tgtLabel;
                return `<button type="button" class="guide-branch-btn" data-target-id="${tgt.id()}">↳ ${escapeHtml(clean)}</button>`;
              }).join("")}
            </div>
          `;
        }
      }

      let thumbHtml = "";
      if (imageUrl) {
        thumbHtml = `<img class="guide-card-thumb" src="${imageUrl}" alt="" loading="lazy" title="Klicken für Fokus">`;
      } else if (!isDecision) {
        const shapeIcon = shape === "ellipse" ? "🟢" : shape === "rhomboid" ? "🔷" : shape === "tag" ? "📑" : shape === "rectangle" ? "📝" : "🟦";
        thumbHtml = `<div class="guide-shape-badge" style="background:${color}22; border: 1px solid ${color}44; color:${color};" title="${shape}">${shapeIcon}</div>`;
      }

      card.innerHTML = `
        <div class="guide-card-head">
          ${badgeHtml}
          <div style="flex:1; min-width:0;">
            <div class="guide-card-text">${escapeHtml(displayTitle)}</div>
            ${mergeHtml}
            ${branchButtonsHtml}
          </div>
        </div>
        ${thumbHtml}
      `;

      const thumbEl = card.querySelector(".guide-card-thumb");
      if (thumbEl) {
        thumbEl.addEventListener("click", (e) => {
          e.stopPropagation();
          const cyNode = cy.getElementById(nodeId);
          if (!cyNode.empty()) {
            cy.animate({ center: { eles: cyNode }, zoom: Math.max(cy.zoom(), 0.8), duration: 250 });
          }
        });
      }

      card.querySelectorAll(".guide-branch-btn").forEach((btn) => {
        btn.addEventListener("click", (e) => {
          e.stopPropagation();
          const targetId = btn.getAttribute("data-target-id");
          if (targetId) {
            const matchingPath = paths.find((p) => p.id === targetId || p.startNodeId === targetId);
            if (matchingPath && guidePathSelect) {
              guidePathSelect.value = matchingPath.id;
              guidePathSelect.dispatchEvent(new Event("change"));
            }
            const tgtNode = cy.getElementById(targetId);
            if (!tgtNode.empty()) {
              cy.animate({ center: { eles: tgtNode }, zoom: Math.max(cy.zoom(), 0.75), duration: 250 });
            }
            highlightGuideItem(targetId);
          }
        });
      });

      card.addEventListener("click", () => {
        const cyNode = cy.getElementById(nodeId);
        if (!cyNode.empty()) {
          cy.animate({ center: { eles: cyNode }, zoom: Math.max(cy.zoom(), 0.75), duration: 250 });
        }
      });

      guideList.appendChild(card);
    });
  }

  function highlightGuideItem(nodeId) {
    toggleGuide(true);
    const card = guideList?.querySelector(`[data-node-id="${nodeId}"]`);
    if (card) {
      card.scrollIntoView({ behavior: "smooth", block: "nearest" });
      card.classList.add("highlight");
      setTimeout(() => card.classList.remove("highlight"), 1600);
    }
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
