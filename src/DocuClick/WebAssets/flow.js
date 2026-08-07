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
          "border-color": "#ffffff",
          "border-width": "data(borderWidth)",
          label: "data(permLabel)",
          color: "#ffffff",
          "font-size": 10,
          "font-weight": "bold",
          "text-valign": "top",
          "text-halign": "right",
          "text-margin-x": 4,
          "text-background-color": "rgba(0,0,0,0.75)",
          "text-background-opacity": 1,
          "text-background-padding": 3,
          "text-wrap": "none",
        },
      },
      {
        selector: "edge",
        style: {
          width: 1,
          "line-color": "rgba(255,255,255,0.51)",
          "curve-style": "straight",
          "target-arrow-shape": "none",
          "mid-target-arrow-shape": "triangle",
          "mid-target-arrow-color": "rgba(255,255,255,0.51)",
          "arrow-scale": 0.9,
        },
      },
      {
        // Drag-to-connect feedback: the fixed source node while the line is
        // being dragged...
        selector: "node.connect-from",
        style: { "border-color": "#22C55E", "border-width": 3 },
      },
      {
        // ...and whichever valid node is currently under the cursor.
        selector: "node.drop-target",
        style: { "border-color": "#4CAFE8", "border-width": 3 },
      },
      {
        // Cytoscape's built-in selection state — tap selects one node
        // (deselecting others), shift+drag box-selects several at once for
        // bulk delete (see the Delete-key handler below). Purely visual on
        // its own; every action still lives in the right-click menu or the
        // drag-to-connect gesture.
        selector: "node:selected",
        style: { "border-color": "#F5A623", "border-width": 3 },
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

  function render(preview) {
    const nodes = preview.nodes || [];
    const edges = preview.edges || [];

    emptyHint.hidden = nodes.length > 0;
    if (nodes.length === 0) {
      cy.elements().remove();
      hasFitted = false; // next session's first render should fit fresh
      return;
    }

    const elements = [
      ...nodes.map((n) => ({
        group: "nodes",
        data: {
          id: n.id,
          label: n.label,
          permLabel: n.permLabel || "",
          color: n.color,
          shape: n.isMarker ? "ellipse" : "round-rectangle",
          width: n.width,
          height: n.height,
          borderWidth: n.isCurrent ? 2 : 1,
          hasChildren: n.hasChildren,
          isMarker: n.isMarker,
          isDecisionPoint: n.isDecisionPoint,
          isPathStart: n.isPathStart,
          isCurrent: n.isCurrent,
          tooltip: n.pathName ? `${n.label} · Pfad: ${n.pathName}` : n.label,
        },
        position: { x: n.x, y: n.y },
        // Never grabbable — nodes stay fixed in their schematic slot even
        // during the drag-to-connect gesture below, which draws a line to
        // the cursor instead of moving the node itself.
        grabbable: false,
      })),
      ...edges.map((e) => ({
        group: "edges",
        data: { id: `${e.source}->${e.target}`, source: e.source, target: e.target },
      })),
    ];

    cy.elements().remove();
    cy.add(elements);
    cy.layout({ name: "preset", fit: !hasFitted }).run();
    hasFitted = true;

    // Keep the current (just-added) node in view after every redraw, same
    // as the old ScrollViewer.BringIntoView()-on-current-node behavior —
    // pan only, zoom stays exactly as the layout above left it.
    const current = cy.nodes('[?isCurrent]');
    if (current.length > 0 && !isInViewport(current[0])) {
      cy.animate({ center: { eles: current }, duration: 150 });
    }
  }

  function isInViewport(node) {
    const box = node.renderedBoundingBox();
    const w = cy.width();
    const h = cy.height();
    return box.x1 >= 0 && box.y1 >= 0 && box.x2 <= w && box.y2 <= h;
  }

  // ---- Tooltip (hover) for regular nodes -------------------------------
  const tooltip = document.getElementById("tooltip");
  cy.on("mouseover", "node", (evt) => {
    const node = evt.target;
    tooltip.textContent = node.data("tooltip");
    positionNear(tooltip, evt.renderedPosition || screenPositionOf(node));
    tooltip.hidden = false;
  });
  cy.on("mouseout", "node", () => {
    tooltip.hidden = true;
  });
  cy.on("pan zoom", () => {
    tooltip.hidden = true;
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

  // ---- Double-click: rename ---------------------------------------------
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

  // ---- Right-click on a connector: delete it ------------------------
  // Structural edges (into/out of a decision point or path-start) are left
  // alone here — removing one would silently detach a whole named path
  // from its decision point while leaving the path's nodes behind,
  // unreachable but not deleted (see IFlowWriter.DisconnectNodes).
  cy.on("cxttap", "edge", (evt) => {
    const edge = evt.target;
    const source = edge.source();
    const target = edge.target();
    if (source.data("isMarker") || target.data("isMarker")) {
      return;
    }

    pendingMenuNodeId = null; // a late pathsResult for an earlier node-menu request must not clobber this
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
  document.addEventListener("keydown", (e) => {
    if (e.key !== "Delete") {
      return;
    }
    const selected = cy.nodes(":selected");
    if (selected.length === 0) {
      return;
    }
    e.preventDefault();
    selected.forEach((n) => sendToHost({ type: "delete", nodeId: n.id() }));
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

  cy.on("mousedown", "node", (evt) => {
    const data = evt.target.data();
    if (data.isMarker) {
      return; // decision points/path starts are never valid connect endpoints
    }

    connectFromId = data.id;
    connectStartClient = { x: evt.originalEvent.clientX, y: evt.originalEvent.clientY };
    connectArmed = false;
  });

  document.addEventListener("mousemove", (e) => {
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
    connectLineEl.setAttribute("x2", e.clientX);
    connectLineEl.setAttribute("y2", e.clientY);

    cy.nodes(".drop-target").removeClass("drop-target");
    const target = findConnectTarget(connectFromId, modelPositionFromClient(e.clientX, e.clientY));
    if (target) {
      target.addClass("drop-target");
    }
  });

  document.addEventListener("mouseup", (e) => endConnectGesture(e.clientX, e.clientY));

  // Safety nets for the ways a release can happen where no mouseup ever
  // reaches this page at all: the cursor crossing out of the document
  // entirely (e.g. into the WPF resize-grip corner deliberately excluded
  // from the WebView2 control's own bounds — see FlowPreviewOverlay's
  // ResizeGripSize margin) — a real button-up there fires *outside*
  // Chromium's world and this page never learns about it — or the whole
  // window losing focus mid-drag (alt-tab, a native dialog stealing it).
  // Both just cancel rather than try to finalize: once the cursor is gone,
  // there's no reliable "what's under it" to connect to anyway.
  document.addEventListener("mouseleave", () => endConnectGesture(null, null));
  window.addEventListener("blur", () => endConnectGesture(null, null));

  function endConnectGesture(clientX, clientY) {
    if (connectFromId === null) {
      return;
    }

    if (connectArmed && clientX !== null) {
      const target = findConnectTarget(connectFromId, modelPositionFromClient(clientX, clientY));
      if (target) {
        sendToHost({ type: "connect", fromId: connectFromId, toId: target.id() });
      }
    }

    cy.getElementById(connectFromId).removeClass("connect-from");
    cy.nodes(".drop-target").removeClass("drop-target");
    connectLine.hidden = true;
    // Belt-and-suspenders: `hidden` alone left a stale painted line on
    // screen sometimes in the real WebView2 host (confirmed via logging
    // that this code was in fact running every time — a Chromium repaint
    // gap, not a logic bug). Collapsing the line to a zero-length segment
    // plus a forced reflow makes it invisible independent of whether that
    // repaint actually happens.
    connectLineEl.setAttribute("x1", "0");
    connectLineEl.setAttribute("y1", "0");
    connectLineEl.setAttribute("x2", "0");
    connectLineEl.setAttribute("y2", "0");
    void connectLine.offsetHeight; // force a synchronous reflow
    connectFromId = null;
    connectStartClient = null;
    connectArmed = false;
  }

  function findConnectTarget(fromId, modelPos) {
    // A candidate that can already reach fromId (walking forward from the
    // candidate) would close a cycle once fromId->candidate is added —
    // exclude those, not fromId's own descendants (that direction would
    // reject perfectly valid ancestor->distant-descendant merges instead).
    const invalidTargets = ancestorsOf(fromId);
    const candidates = cy.nodes().filter((n) => {
      const d = n.data();
      return n.id() !== fromId && !d.isMarker && !invalidTargets.has(n.id());
    });

    return candidates.filter((n) => {
      const box = n.boundingBox();
      return modelPos.x >= box.x1 && modelPos.x <= box.x2 && modelPos.y >= box.y1 && modelPos.y <= box.y2;
    })[0] || null;
  }

  /// Every node that can reach nodeId via existing forward edges (walked backward via incoming edges).
  function ancestorsOf(nodeId) {
    const result = new Set();
    const queue = [nodeId];
    while (queue.length > 0) {
      const id = queue.shift();
      cy.getElementById(id)
        .incomers("edge")
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

  // ---- Host message dispatch ---------------------------------------
  function dispatch(message) {
    switch (message.type) {
      case "preview":
        render(message);
        break;
      case "pathsResult":
        renderContextMenu(message.nodeId, message.paths || []);
        break;
      default:
        console.warn("Unknown message from host:", message);
    }
  }

  onHostMessage(dispatch);
})();
