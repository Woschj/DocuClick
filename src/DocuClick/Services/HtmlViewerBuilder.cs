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

    public sealed record EdgeSpec(string Source, string Target, string Color, bool Manual, string LineStyle = "solid");

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
                lineStyle = string.IsNullOrEmpty(e.LineStyle) ? "solid" : e.LineStyle,
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

              .guide-controls {
                padding: 10px 14px; background: rgba(0, 0, 0, 0.25);
                border-bottom: 1px solid var(--border-subtle);
                display: flex; flex-direction: column; gap: 8px;
              }
              .guide-path-bar { display: flex; align-items: center; gap: 8px; }
              .guide-control-label {
                font-size: 11px; font-weight: 700; color: var(--text-sub);
                text-transform: uppercase; letter-spacing: 0.5px;
              }
              .guide-select {
                flex: 1; background: rgba(15, 23, 42, 0.85); border: 1px solid var(--border-glass);
                color: var(--text-main); border-radius: var(--radius-sm);
                padding: 5px 8px; font-size: 12px; font-family: inherit; outline: none; cursor: pointer;
              }
              .guide-select:focus { border-color: var(--accent); }
              .guide-filter-tabs {
                display: flex; gap: 4px; background: rgba(0, 0, 0, 0.35); padding: 3px; border-radius: var(--radius-sm);
              }
              .guide-tab {
                flex: 1; background: transparent; border: none; color: var(--text-sub);
                font-size: 11px; font-weight: 600; padding: 4px 6px; border-radius: 4px; cursor: pointer;
                transition: background 0.15s, color 0.15s; text-align: center;
              }
              .guide-tab:hover { color: var(--text-main); }
              .guide-tab.active { background: var(--accent); color: #fff; }

              /* Decision and Branch Cards in Guide */
              .guide-card.guide-card-decision {
                background: linear-gradient(135deg, rgba(245, 158, 11, 0.12), rgba(15, 23, 42, 0.6));
                border-color: rgba(245, 158, 11, 0.35);
              }
              .guide-card.guide-card-decision:hover {
                border-color: rgba(245, 158, 11, 0.6); background: linear-gradient(135deg, rgba(245, 158, 11, 0.2), rgba(15, 23, 42, 0.7));
              }
              .guide-badge-decision {
                background: #f59e0b !important; color: #000 !important; font-weight: 800;
              }
              .guide-badge-merge {
                display: inline-block; font-size: 9px; font-weight: 700; color: #10b981;
                background: rgba(16, 185, 129, 0.18); border: 1px solid rgba(16, 185, 129, 0.3);
                padding: 1px 5px; border-radius: 4px; margin-top: 4px;
              }
              .guide-branch-buttons {
                display: flex; flex-wrap: wrap; gap: 6px; margin-top: 8px; width: 100%;
              }
              .guide-branch-btn {
                background: rgba(255, 255, 255, 0.08); border: 1px solid var(--border-glass);
                color: var(--text-main); font-size: 11px; font-weight: 600; font-family: inherit;
                padding: 4px 8px; border-radius: var(--radius-sm); cursor: pointer;
                display: inline-flex; align-items: center; gap: 4px; transition: background 0.15s, transform 0.1s;
              }
              .guide-branch-btn:hover {
                background: var(--accent); color: #fff; transform: translateY(-1px);
              }
              .guide-path-divider {
                display: flex; align-items: center; gap: 8px; padding: 6px 10px; margin-top: 6px;
                background: rgba(255, 255, 255, 0.04); border-radius: var(--radius-sm);
                font-size: 11px; font-weight: 700; color: var(--text-sub);
              }
              .guide-path-badge {
                width: 8px; height: 8px; border-radius: 50%; display: inline-block;
              }
              .guide-shape-badge {
                width: 22px; height: 22px; border-radius: 6px; display: flex;
                align-items: center; justify-content: center; font-size: 11px; flex-shrink: 0;
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

              /* Iframe & Obsidian Embed Adaptations */
              body.embedded-in-iframe .brand-bar { top: 10px; left: 10px; padding: 4px 10px; }
              body.embedded-in-iframe .floating-dock { bottom: 12px; }
              body.embedded-in-iframe #connect-btn, body.embedded-in-iframe #download-btn { display: none !important; }
              body.embedded-in-iframe #dock-save-group { display: flex !important; }
              body.embedded-in-iframe .save-status { color: #34d399 !important; font-weight: 600; }

              /* Context Menu */
              .hud-menu {
                position: fixed; z-index: 1000;
                background: rgba(15, 23, 42, 0.94);
                backdrop-filter: blur(16px);
                -webkit-backdrop-filter: blur(16px);
                border: 1px solid var(--border-glass);
                border-radius: var(--radius-md);
                padding: 6px; min-width: 190px;
                box-shadow: var(--shadow-dock);
                font-family: var(--font-stack);
                user-select: none;
              }
              .hud-menu-header {
                font-size: 11px; font-weight: 700; color: var(--text-muted);
                text-transform: uppercase; letter-spacing: 0.05em;
                padding: 5px 8px 6px; border-bottom: 1px solid var(--border-subtle);
                margin-bottom: 4px; max-width: 240px; overflow: hidden;
                text-overflow: ellipsis; white-space: nowrap;
              }
              .hud-menu-item {
                display: flex; align-items: center; gap: 8px;
                padding: 7px 10px; font-size: 12.5px; font-weight: 500;
                color: var(--text-main); border-radius: var(--radius-sm);
                cursor: pointer; transition: background 0.12s ease, color 0.12s ease;
                white-space: nowrap;
              }
              .hud-menu-item:hover {
                background: rgba(59, 130, 246, 0.25); color: #ffffff;
              }
              .hud-menu-item.primary {
                font-weight: 600; color: #60a5fa;
              }
              .hud-menu-item.danger {
                color: #f87171;
              }
              .hud-menu-item.danger:hover {
                background: rgba(239, 68, 68, 0.25); color: #fca5a5;
              }
              .hud-menu-separator {
                height: 1px; background: var(--border-subtle); margin: 4px 0;
              }
              .hud-menu-color-row {
                display: flex; align-items: center; justify-content: space-between;
                gap: 5px; padding: 6px 8px; background: rgba(0, 0, 0, 0.25);
                border-radius: var(--radius-sm); margin: 4px 2px;
              }
              .hud-color-dot {
                width: 18px; height: 18px; border-radius: 50%;
                border: 2px solid rgba(255, 255, 255, 0.25); cursor: pointer;
                padding: 0; transition: transform 0.12s, border-color 0.12s, box-shadow 0.12s;
              }
              .hud-color-dot:hover {
                transform: scale(1.22); border-color: #ffffff;
                box-shadow: 0 0 8px rgba(255, 255, 255, 0.5);
              }
              .hud-color-dot.active {
                border-color: #ffffff; box-shadow: 0 0 0 2px var(--accent);
              }
              .menu-icon { font-size: 13px; opacity: 0.9; }

              /* Rename Modal */
              #rename-modal {
                position: fixed; inset: 0; z-index: 1050;
                background: rgba(8, 12, 20, 0.85);
                backdrop-filter: blur(16px);
                -webkit-backdrop-filter: blur(16px);
                display: flex; align-items: center; justify-content: center;
              }
              .rename-dialog {
                width: 440px; max-width: 90vw; background: var(--surface-card);
                border: 1px solid var(--border-glass); border-radius: var(--radius-lg);
                box-shadow: var(--shadow-dock); padding: 20px; color: var(--text-main);
              }
              .rename-dialog h3 {
                margin: 0 0 12px 0; font-size: 14.5px; font-weight: 700;
                display: flex; align-items: center; gap: 8px;
              }
              .rename-textarea {
                width: 100%; box-sizing: border-box; background: #090d16;
                border: 1px solid var(--border-subtle); border-radius: var(--radius-md);
                padding: 10px 12px; color: var(--text-main); font-family: inherit;
                font-size: 13px; resize: vertical; outline: none;
                transition: border-color 0.15s ease; margin-bottom: 16px;
              }
              .rename-textarea:focus { border-color: var(--accent); }
              .rename-actions { display: flex; justify-content: flex-end; gap: 8px; }

              /* Element Add/Edit Modal */
              #element-modal {
                position: fixed; inset: 0; z-index: 1060;
                background: rgba(8, 12, 20, 0.85);
                backdrop-filter: blur(16px);
                -webkit-backdrop-filter: blur(16px);
                display: flex; align-items: center; justify-content: center;
              }
              .element-dialog {
                width: 460px; max-width: 92vw; background: var(--surface-card);
                border: 1px solid var(--border-glass); border-radius: var(--radius-lg);
                box-shadow: var(--shadow-dock); padding: 22px; color: var(--text-main);
              }
              .element-dialog h3 {
                margin: 0 0 12px 0; font-size: 15px; font-weight: 700;
                display: flex; align-items: center; gap: 8px;
              }
              .modal-section-title {
                font-size: 10.5px; font-weight: 700; text-transform: uppercase;
                letter-spacing: 0.05em; color: var(--text-muted); margin-bottom: 7px; margin-top: 12px;
              }
              .shape-grid {
                display: grid; grid-template-columns: repeat(3, 1fr); gap: 8px; margin-bottom: 4px;
              }
              .shape-tile {
                display: flex; align-items: center; gap: 8px; padding: 8px 10px;
                border-radius: var(--radius-md); background: rgba(255, 255, 255, 0.04);
                border: 1.5px solid var(--border-subtle); cursor: pointer;
                transition: all 0.15s ease; font-size: 12px; font-weight: 600; color: var(--text-main);
                user-select: none;
              }
              .shape-tile:hover {
                background: rgba(255, 255, 255, 0.09); border-color: rgba(255, 255, 255, 0.22);
              }
              .shape-tile.active {
                background: rgba(59, 130, 246, 0.22); border-color: var(--accent);
                box-shadow: 0 0 10px rgba(59, 130, 246, 0.35);
              }
              .shape-icon { font-size: 14px; flex-shrink: 0; }
              .color-palette {
                display: flex; align-items: center; gap: 8px; flex-wrap: wrap; margin-bottom: 8px;
              }
              .color-swatch {
                width: 25px; height: 25px; border-radius: 50%; cursor: pointer;
                border: 2px solid transparent; transition: transform 0.12s, border-color 0.12s;
                position: relative;
              }
              .color-swatch:hover { transform: scale(1.18); }
              .color-swatch.active {
                border-color: #ffffff; transform: scale(1.15);
                box-shadow: 0 0 8px rgba(255, 255, 255, 0.6);
              }

              /* Guide list item highlight */
              .guide-card.highlight {
                border-color: var(--accent) !important;
                box-shadow: 0 0 16px var(--accent-glow) !important;
              }

              /* Dropzone and Image Preview inside Element Modal */
              .image-dropzone {
                border: 2px dashed var(--border-glass);
                border-radius: var(--radius-md);
                padding: 12px 10px;
                text-align: center;
                cursor: pointer;
                background: rgba(255, 255, 255, 0.02);
                transition: all 0.15s ease;
                margin-bottom: 12px;
                user-select: none;
              }
              .image-dropzone:hover, .image-dropzone.drag-over {
                border-color: var(--accent);
                background: rgba(59, 130, 246, 0.12);
              }
              .image-dropzone-prompt {
                font-size: 11.5px;
                color: var(--text-sub);
                display: flex;
                align-items: center;
                justify-content: center;
                gap: 8px;
              }
              .image-dropzone-prompt .drop-icon { font-size: 16px; }
              .image-preview-wrapper {
                margin-bottom: 12px;
                display: flex;
                flex-direction: column;
                gap: 6px;
              }
              .image-preview-container {
                position: relative;
                border-radius: var(--radius-md);
                overflow: hidden;
                border: 1px solid var(--border-glass);
                background: rgba(0, 0, 0, 0.4);
                max-height: 130px;
                display: flex;
                align-items: center;
                justify-content: center;
              }
              .image-preview-img {
                width: 100%;
                max-height: 130px;
                object-fit: contain;
                display: block;
              }
              .image-preview-actions {
                display: flex;
                align-items: center;
                justify-content: space-between;
                gap: 8px;
                font-size: 11px;
                color: var(--text-sub);
              }
              .image-remove-btn {
                background: rgba(239, 68, 68, 0.2);
                border: 1px solid rgba(239, 68, 68, 0.4);
                color: #fca5a5;
                padding: 3px 8px;
                border-radius: var(--radius-sm);
                cursor: pointer;
                font-size: 10.5px;
                font-family: inherit;
                transition: all 0.15s ease;
              }
              .image-remove-btn:hover {
                background: rgba(239, 68, 68, 0.4);
                color: #fff;
              }

              /* Canvas Drag & Drop Overlay Indicator */
              .canvas-drop-indicator {
                position: absolute;
                inset: 0;
                pointer-events: none;
                border: 3px dashed var(--accent);
                background: rgba(59, 130, 246, 0.15);
                z-index: 1040;
                display: flex;
                align-items: center;
                justify-content: center;
                backdrop-filter: blur(4px);
              }
              .canvas-drop-indicator-badge {
                background: var(--surface-card);
                border: 1px solid var(--border-glass);
                border-radius: var(--radius-lg);
                padding: 16px 28px;
                font-size: 15px;
                font-weight: 600;
                color: var(--text-main);
                box-shadow: var(--shadow-dock);
                display: flex;
                align-items: center;
                gap: 12px;
              }

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
              <button id="add-element-btn" class="dock-btn dock-btn-highlight" title="Neues Flowchart-Element einfügen">
                <span style="font-size: 13px;">➕</span>
                <span>Element</span>
              </button>
              <button id="guide-toggle-btn" class="dock-btn" title="Schritt-für-Schritt-Guide ein-/ausblenden">
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
              <div class="guide-controls">
                <div class="guide-path-bar">
                  <span class="guide-control-label">Ablauf:</span>
                  <select id="guide-path-select" class="guide-select" title="Zwischen Gesamtablauf und Pfaden filtern">
                    <option value="all">🌐 Gesamter Ablauf</option>
                  </select>
                </div>
                <div class="guide-filter-tabs">
                  <button type="button" id="guide-tab-all" class="guide-tab active">Alle Schritte</button>
                  <button type="button" id="guide-tab-images" class="guide-tab">Nur Screenshots</button>
                </div>
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

            <!-- Context Menu -->
            <div id="context-menu" class="hud-menu" hidden></div>

            <!-- Rename / Edit Description Modal -->
            <div id="rename-modal" hidden>
              <div class="rename-dialog">
                <h3 id="rename-dialog-title"><span>✏️</span> Beschreibung bearbeiten</h3>
                <textarea id="rename-input" rows="3" class="rename-textarea" placeholder="Beschreibung eingeben..."></textarea>
                <div class="rename-actions">
                  <button id="rename-cancel-btn" class="dock-btn" type="button">Abbrechen</button>
                  <button id="rename-save-btn" class="dock-btn dock-btn-primary" type="button">Speichern</button>
                </div>
              </div>
            </div>

            <!-- Flowchart Element Add/Edit Modal -->
            <div id="element-modal" hidden>
              <div class="element-dialog">
                <h3 id="element-modal-title"><span>➕</span> Element einfügen</h3>
                <div class="modal-section-title">Form wählen</div>
                <div class="shape-grid" id="shape-grid"></div>
                <div class="modal-section-title">Farbe wählen</div>
                <div class="color-palette" id="color-palette"></div>
                <div class="modal-section-title">Bezeichnung / Text</div>
                <textarea id="element-input" rows="2" class="rename-textarea" placeholder="Bezeichnung eingeben..."></textarea>
                
                <div class="modal-section-title">Bild anhängen (optional)</div>
                <div id="element-dropzone" class="image-dropzone">
                  <div class="image-dropzone-prompt">
                    <span class="drop-icon">📷</span>
                    <span>Bild hier ablegen, klicken oder <b>Strg+V</b></span>
                  </div>
                </div>
                <input type="file" id="element-image-file" accept="image/*" style="display:none">
                <div id="element-image-preview-wrapper" class="image-preview-wrapper" hidden>
                  <div class="image-preview-container">
                    <img id="element-image-preview" class="image-preview-img" alt="Vorschau">
                  </div>
                  <div class="image-preview-actions">
                    <span id="element-image-info"></span>
                    <button type="button" id="element-image-remove-btn" class="image-remove-btn">✕ Bild entfernen</button>
                  </div>
                </div>

                <div class="rename-actions">
                  <button id="element-cancel-btn" class="dock-btn" type="button">Abbrechen</button>
                  <button id="element-save-btn" class="dock-btn dock-btn-primary" type="button">Einfügen</button>
                </div>
              </div>
            </div>

            <!-- Canvas Drag & Drop Overlay Indicator -->
            <div id="canvas-drop-overlay" class="canvas-drop-indicator" hidden>
              <div class="canvas-drop-indicator-badge">
                <span>📷</span> Bild hier ablegen, um Schritt zu erstellen
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
            let originalHtmlText = "<!DOCTYPE html>\n" + document.documentElement.outerHTML;

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
                  selector: "node[!imageUrl]",
                  style: {
                    "text-valign": "center",
                    "text-halign": "center",
                    "text-margin-y": 0,
                    "text-background-opacity": 0,
                    "text-border-width": 0,
                    "text-wrap": "wrap",
                    "text-max-width": 160,
                    "font-size": 12,
                    "font-weight": "600",
                    color: "#ffffff",
                    "border-color": "rgba(255, 255, 255, 0.45)",
                    "border-width": 2,
                  },
                },
                {
                  selector: "node[shape='diamond']",
                  style: { width: 180, height: 90, "border-width": 2.5 },
                },
                {
                  selector: "node[shape='ellipse']",
                  style: { width: 160, height: 80 },
                },
                {
                  selector: "node[shape='rhomboid']",
                  style: { width: 200, height: 80 },
                },
                {
                  selector: "node[shape='tag']",
                  style: { width: 200, height: 90 },
                },
                {
                  selector: "node[shape='rectangle'][!imageUrl], node[shape='round-rectangle'][!imageUrl]",
                  style: { width: 200, height: 85 },
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
                  },
                },
                {
                  selector: "edge[lineStyle = 'dashed']",
                  style: { "line-style": "dashed", "line-dash-pattern": [6, 4] },
                },
                {
                  selector: "edge[lineStyle = 'dotted']",
                  style: { "line-style": "dotted" },
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
                  selector: "node.search-dimmed, node.path-dimmed, edge.path-dimmed",
                  style: { opacity: 0.18 },
                },
                {
                  selector: "node.path-highlighted",
                  style: { "border-color": "#38bdf8", "border-width": 4, opacity: 1 },
                },
                {
                  selector: "edge.path-highlighted",
                  style: { width: 4.5, opacity: 1 },
                },
              ],
              layout: { name: "preset" },
              minZoom: 0.1,
              maxZoom: 3,
              boxSelectionEnabled: false,
            });
            function ensureViewFit() {
              cy.resize();
              cy.fit(undefined, 40);
              scheduleViewerRaf();
            }

            if (window.ResizeObserver) {
              const ro = new ResizeObserver(() => {
                ensureViewFit();
              });
              ro.observe(document.getElementById("cy"));
            }

            // Retry fitting after container has computed its geometry in iframes/tabs
            setTimeout(ensureViewFit, 50);
            setTimeout(ensureViewFit, 150);
            setTimeout(ensureViewFit, 400);

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

            let viewerRafId = null;
            function scheduleViewerRaf() {
              if (viewerRafId !== null) return;
              viewerRafId = requestAnimationFrame(() => {
                viewerRafId = null;
                updateOverlays();
                if (typeof updateHandles === "function") {
                  updateHandles();
                }
              });
            }

            function updateOverlays() {
              const w = cy.width();
              const h = cy.height();
              const pad = 80;
              overlayImgs.forEach((img, id) => {
                const node = cy.getElementById(id);
                if (node.empty()) return;
                const box = node.renderedBoundingBox({ includeLabels: false });
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
            cy.on("pan zoom position", scheduleViewerRaf);
            ensureViewFit();
            window.addEventListener("resize", () => { cy.resize(); scheduleViewerRaf(); });

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
              lightboxStepBadge.textContent = `Bild ${idx + 1} von ${imageNodes.length}`;
              lightboxTitle.textContent = node.data.label || "";
              lightboxPrevBtn.disabled = (idx === 0);
              lightboxNextBtn.disabled = (idx === imageNodes.length - 1);
              lightbox.hidden = false;
            }

            function openLightboxByNodeId(nodeId) {
              const idx = imageNodes.findIndex((n) => n.data.id === nodeId);
              if (idx >= 0) openLightbox(idx);
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
              } else if (typeof elementModal !== "undefined" && elementModal && !elementModal.hidden) {
                if (e.key === "Escape") closeElementModal();
                else if (e.key === "Enter" && !e.shiftKey) {
                  e.preventDefault();
                  saveElementModal();
                }
              } else if (typeof renameModal !== "undefined" && renameModal && !renameModal.hidden) {
                if (e.key === "Escape") { renameModal.hidden = true; pendingRenameNodeId = null; }
              } else if (typeof contextMenu !== "undefined" && contextMenu && !contextMenu.hidden) {
                if (e.key === "Escape") closeContextMenu();
              }
            });

            cy.on("tap", "node[imageUrl]", (evt) => {
              openLightboxByNodeId(evt.target.id());
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
            const guidePathSelect = document.getElementById("guide-path-select");
            const guideTabAll = document.getElementById("guide-tab-all");
            const guideTabImages = document.getElementById("guide-tab-images");

            let guideCurrentPathId = "all";
            let guideShowOnlyImages = false;

            function toggleGuide(show) {
              const willShow = show !== undefined ? show : !guideDrawer.classList.contains("open");
              guideDrawer.classList.toggle("open", willShow);
            }
            guideToggleBtn?.addEventListener("click", () => toggleGuide());
            guideCloseBtn?.addEventListener("click", () => toggleGuide(false));

            function escapeHtml(str) {
              const div = document.createElement("div");
              div.textContent = str;
              return div.innerHTML;
            }

            function getGraphPaths() {
              const allNodes = cy.nodes();
              if (allNodes.empty()) return [];

              const branchTargets = new Map();
              const decisionNodes = allNodes.filter((n) => n.data("shape") === "diamond" || (n.data("label") || "").includes("Abzweigung") || n.outgoers("edge").length > 1);

              decisionNodes.forEach((dec) => {
                dec.outgoers("edge").forEach((e) => {
                  const tgt = e.target();
                  if (!branchTargets.has(tgt.id())) {
                    const rawLabel = tgt.data("label") || "Zweig";
                    const name = rawLabel.startsWith("↳ Pfad: ") ? rawLabel.slice(8) : rawLabel;
                    branchTargets.set(tgt.id(), {
                      name: name,
                      color: tgt.data("color") || e.data("color") || "#3b82f6",
                      originId: dec.id()
                    });
                  }
                });
              });

              allNodes.filter((n) => (n.data("label") || "").startsWith("↳ Pfad: ")).forEach((n) => {
                if (!branchTargets.has(n.id())) {
                  const name = n.data("label").slice(8);
                  branchTargets.set(n.id(), {
                    name: name,
                    color: n.data("color") || "#3b82f6",
                    originId: null
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
                  if (!node.empty() && node.data("shape") !== "diamond") {
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
                    startNodeId: mainRoot.id()
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
                  startNodeId: startId
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
                const isDecision = shape === "diamond" || rawLabel.includes("Abzweigung");
                const isPathStart = rawLabel.startsWith("↳ Pfad: ");
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
                  displayTitle = rawLabel.slice(8);
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
                  thumbHtml = `<img class="guide-card-thumb" src="${imageUrl}" alt="" loading="lazy" title="Klicken für Vollbild">`;
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
                    openLightboxByNodeId(nodeId);
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
            buildGuideList();

            function highlightGuideItem(nodeId) {
              toggleGuide(true);
              const card = guideList.querySelector(`[data-node-id="${nodeId}"]`);
              if (card) {
                card.scrollIntoView({ behavior: "smooth", block: "nearest" });
                card.classList.add("highlight");
                setTimeout(() => card.classList.remove("highlight"), 1600);
              }
            }

            // ---- Context Menu & Custom Interactions ----------------------
            const contextMenu = document.getElementById("context-menu");
            const renameModal = document.getElementById("rename-modal");
            const renameInput = document.getElementById("rename-input");
            const renameSaveBtn = document.getElementById("rename-save-btn");
            const renameCancelBtn = document.getElementById("rename-cancel-btn");
            let pendingRenameNodeId = null;

            const DECISION_POINT_LABEL = "◆ Abzweigung";
            const PATH_START_PREFIX = "↳ Pfad: ";

            function buildLabel(text) {
              if (!text) return "(ohne Beschreibung)";
              const firstLine = text.split("\n")[0];
              return firstLine.length > 70 ? `${firstLine.slice(0, 70)}…` : firstLine;
            }

            function showContextMenu(items, clientX, clientY) {
              contextMenu.innerHTML = "";
              items.forEach((it) => {
                if (it.header) {
                  const hdr = document.createElement("div");
                  hdr.className = "hud-menu-header";
                  hdr.textContent = it.header;
                  contextMenu.appendChild(hdr);
                } else if (it.separator) {
                  const sep = document.createElement("div");
                  sep.className = "hud-menu-separator";
                  contextMenu.appendChild(sep);
                } else if (it.colorBar) {
                  const row = document.createElement("div");
                  row.className = "hud-menu-color-row";
                  it.colors.forEach((c) => {
                    const dot = document.createElement("button");
                    dot.type = "button";
                    dot.className = "hud-color-dot" + (it.activeColor && it.activeColor.toLowerCase() === c.toLowerCase() ? " active" : "");
                    dot.style.backgroundColor = c;
                    dot.title = c;
                    dot.addEventListener("click", (e) => {
                      e.stopPropagation();
                      closeContextMenu();
                      it.onSelect(c);
                    });
                    row.appendChild(dot);
                  });
                  contextMenu.appendChild(row);
                } else {
                  const item = document.createElement("div");
                  item.className = "hud-menu-item" + (it.danger ? " danger" : "") + (it.primary ? " primary" : "");
                  item.innerHTML = (it.icon ? `<span class="menu-icon">${it.icon}</span> ` : "") + it.label;
                  item.addEventListener("click", (e) => {
                    e.stopPropagation();
                    closeContextMenu();
                    it.action();
                  });
                  contextMenu.appendChild(item);
                }
              });
              contextMenu.hidden = false;
              const rect = contextMenu.getBoundingClientRect();
              const maxX = window.innerWidth - rect.width - 10;
              const maxY = window.innerHeight - rect.height - 10;
              contextMenu.style.left = `${Math.max(10, Math.min(clientX, maxX))}px`;
              contextMenu.style.top = `${Math.max(10, Math.min(clientY, maxY))}px`;
            }

            function closeContextMenu() {
              contextMenu.hidden = true;
            }

            document.addEventListener("click", (e) => {
              if (!contextMenu.contains(e.target)) closeContextMenu();
            });
            window.addEventListener("contextmenu", (e) => {
              e.preventDefault();
            });
            cy.on("pan zoom", closeContextMenu);

            function openRenameModal(nodeId) {
              const node = cy.getElementById(nodeId);
              if (node.empty()) return;
              pendingRenameNodeId = nodeId;
              const textNode = (typeof textNodesById !== "undefined" && textNodesById) ? textNodesById.get(nodeId) : null;
              const rawText = textNode ? (textNode.text || "") : (node.data("label") || "");
              const isPathStart = rawText.startsWith(PATH_START_PREFIX);
              const val = isPathStart ? rawText.slice(PATH_START_PREFIX.length) : rawText;
              renameInput.value = val;
              renameModal.hidden = false;
              setTimeout(() => { renameInput.focus(); renameInput.select(); }, 60);
            }

            function saveRename() {
              if (!pendingRenameNodeId) return;
              const node = cy.getElementById(pendingRenameNodeId);
              const next = renameInput.value.trim();
              if (node && !node.empty()) {
                const textNode = (typeof textNodesById !== "undefined" && textNodesById) ? textNodesById.get(pendingRenameNodeId) : null;
                const rawText = textNode ? (textNode.text || "") : (node.data("label") || "");
                const isPathStart = rawText.startsWith(PATH_START_PREFIX);
                const newText = isPathStart ? `${PATH_START_PREFIX}${next}` : next;
                if (textNode) textNode.text = newText;
                node.data("label", isPathStart ? newText : buildLabel(next));
                buildGuideList();
                if (typeof scheduleSave === "function") scheduleSave();
              }
              renameModal.hidden = true;
              pendingRenameNodeId = null;
            }

            renameSaveBtn?.addEventListener("click", saveRename);
            renameCancelBtn?.addEventListener("click", () => {
              renameModal.hidden = true;
              pendingRenameNodeId = null;
            });
            renameModal?.addEventListener("click", (e) => {
              if (e.target === renameModal) {
                renameModal.hidden = true;
                pendingRenameNodeId = null;
              }
            });
            renameInput?.addEventListener("keydown", (e) => {
              if (e.key === "Enter" && !e.shiftKey) {
                e.preventDefault();
                saveRename();
              } else if (e.key === "Escape") {
                renameModal.hidden = true;
                pendingRenameNodeId = null;
              }
            });

            const isEmbedded = (window.self !== window.top);
            if (isEmbedded) {
              document.body.classList.add("embedded-in-iframe");
            }

            // ---- Live format editing (move / connect / save) ----------------
            const dataScriptEl = document.getElementById("docuclick-data");
            if (dataScriptEl) {
              const saveGroup = document.getElementById("dock-save-group");
              if (saveGroup) saveGroup.hidden = false;

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
              const setStatus = (text) => { if (saveStatus) saveStatus.textContent = text; };
              const connectBtn = document.getElementById("connect-btn");

              if (isEmbedded) {
                setStatus("✓ Vault-Sync aktiv");
              } else if (!window.showOpenFilePicker) {
                if (connectBtn) connectBtn.disabled = true;
                setStatus("Lokales Auto-Save aktiv");
              }

              if (connectBtn) {
                connectBtn.addEventListener("click", async () => {
                  try {
                    [fileHandle] = await window.showOpenFilePicker({
                      types: [{ description: "DocuClick-Ablauf", accept: { "text/html": [".html"] } }],
                    });
                    setStatus(`verbunden: ${fileHandle.name}`);
                    saveNow();
                  } catch (err) {
                    if (err.name !== "AbortError") setStatus(`Fehler: ${err.name}: ${err.message}`);
                  }
                });
              }

              function buildPatchedHtml() {
                const currentFlowData = {
                  nodes: cy.nodes().map((n) => {
                    const d = { ...n.data() };
                    if (!d.imageUrl) delete d.imageUrl;
                    return { data: d, position: n.position() };
                  }),
                  edges: cy.edges().map((e) => ({ data: e.data() }))
                };
                const flowJson = JSON.stringify(currentFlowData);

                let patched = originalHtmlText;

                // 1. Patch docuclick-data block (Obsidian Canvas document)
                const startMarker = "<script id=\"docuclick-data\" type=\"application/json\">";
                const startIdx = patched.indexOf(startMarker);
                const endIdx = patched.indexOf("<\/script>", startIdx);
                if (startIdx !== -1 && endIdx !== -1) {
                  patched = `${patched.slice(0, startIdx + startMarker.length)}\n${JSON.stringify(canvasDoc, null, 2)}\n${patched.slice(endIdx)}`;
                }

                // 2. Patch flowData variable (Cytoscape JSON data)
                const flowPattern = /const flowData = \{[\s\S]*?\};\s*const cy = cytoscape\(/;
                patched = patched.replace(flowPattern, `const flowData = ${flowJson};\n            const cy = cytoscape(`);

                originalHtmlText = patched;
                return patched;
              }

              document.getElementById("download-btn")?.addEventListener("click", () => {
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
                clearTimeout(saveTimer);
                saveTimer = setTimeout(saveNow, 350);
              }

              async function saveNow() {
                if (saving) return;
                saving = true;
                setStatus("speichere...");

                const patched = buildPatchedHtml();
                const titleClean = document.title.replace(/ – Ablauf$/, "");
                const htmlFileName = titleClean + ".html";
                const canvasJsonStr = JSON.stringify(canvasDoc, null, 2);

                // 1. LocalStorage auto-backup (instant)
                try {
                  localStorage.setItem("docuclick_cache_" + titleClean, patched);
                  localStorage.setItem("docuclick_canvas_" + titleClean, canvasJsonStr);
                } catch (e) {}

                let savedToDisk = false;

                // 2. Obsidian PostMessage Bridge (if embedded in Obsidian)
                if (isEmbedded) {
                  try {
                    window.parent.postMessage({
                      type: "docuclick-save",
                      fileName: htmlFileName,
                      htmlContent: patched,
                      canvasDoc: canvasDoc
                    }, "*");
                    savedToDisk = true;
                  } catch (e) {}
                }

                // 3. Direct Obsidian App Vault write (if same-origin Electron)
                try {
                  const parentApp = (window.parent && window.parent.app) || (window.top && window.top.app);
                  if (parentApp && parentApp.vault && parentApp.vault.adapter) {
                    const adapter = parentApp.vault.adapter;
                    const files = parentApp.vault.getFiles();
                    const targetFile = files.find((f) => f.name === htmlFileName || f.path.endsWith("/" + htmlFileName));
                    const targetPath = targetFile ? targetFile.path : htmlFileName;
                    try {
                      const leaves = parentApp.workspace.getLeavesOfType("html-view");
                      for (const leaf of leaves) {
                        if (leaf.view && leaf.view.watcher && leaf.view.watcher.noteSelfWrite) {
                          leaf.view.watcher.noteSelfWrite(patched);
                        }
                      }
                    } catch (err) {}
                    await adapter.write(targetPath, patched);
                    await adapter.write(targetPath.replace(/\.html$/i, ".canvas"), canvasJsonStr);
                    savedToDisk = true;
                  }
                } catch (e) {}

                // 4. File System Access API
                if (fileHandle) {
                  try {
                    const writable = await fileHandle.createWritable();
                    await writable.write(patched);
                    await writable.close();
                    savedToDisk = true;
                  } catch (err) {
                    console.warn("FileHandle write failed:", err);
                  }
                }

                saving = false;
                dirty = false;

                const timeStr = new Date().toLocaleTimeString("de-DE");
                if (savedToDisk) {
                  setStatus(isEmbedded ? `✓ Vault-Sync (${timeStr})` : `✓ Gespeichert (${timeStr})`);
                } else if (!isEmbedded && !fileHandle) {
                  setStatus("Lokal gesichert — 'Datei verbinden' für Festplatte");
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

              function createHandlesForNode(node) {
                if (!node || node.empty() || handlesByNode.has(node.id())) return;
                const textNode = textNodesById.get(node.id());
                if (!textNode || isMarkerText(textNode)) return;
                const dots = ["top", "right", "bottom", "left"].map(() => {
                  const dot = document.createElement("div");
                  dot.className = "node-handle";
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
                    if (e.button !== 0) return;
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
              }

              cy.nodes().forEach(createHandlesForNode);

              function updateHandles() {
                const w = cy.width();
                const h = cy.height();
                const pad = 60;
                handlesByNode.forEach((dots, id) => {
                  const node = cy.getElementById(id);
                  if (node.empty()) return;
                  const box = node.renderedBoundingBox({ includeLabels: false });
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
              updateHandles();

              // Zero-overhead event-driven handle reveal instead of 1000Hz mousemove bounding box loops
              cy.on("mouseover", "node", (evt) => {
                if (moveNodeId !== null || connectFromId !== null) return;
                showHandlesForNode(evt.target.id());
              });
              cy.on("mouseout", "node", () => {
                if (moveNodeId !== null || connectFromId !== null) return;
                hideHandlesWithDelay();
              });

              cy.on("mousedown", "node", (evt) => {
                if (evt.originalEvent.button !== 0) return;
                const textNode = textNodesById.get(evt.target.id());
                if (!textNode) return;
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

              function deleteNode(nodeId) {
                const node = cy.getElementById(nodeId);
                if (node.empty()) return;
                const textNode = textNodesById.get(nodeId);
                if (!textNode) return;

                const image = findImageSibling(textNode);
                const group = findGroupSibling(textNode);

                canvasDoc.nodes = canvasDoc.nodes.filter((n) =>
                  n.id !== nodeId && (!image || n.id !== image.id) && (!group || n.id !== group.id)
                );
                textNodesById.delete(nodeId);
                canvasDoc.edges = canvasDoc.edges.filter((e) => e.fromNode !== nodeId && e.toNode !== nodeId);

                if (overlayImgs && overlayImgs.has(nodeId)) {
                  overlayImgs.get(nodeId).remove();
                  overlayImgs.delete(nodeId);
                }
                if (handlesByNode && handlesByNode.has(nodeId)) {
                  handlesByNode.get(nodeId).forEach((d) => d.remove());
                  handlesByNode.delete(nodeId);
                }

                const imgIdx = imageNodes.findIndex((n) => n.data.id === nodeId);
                if (imgIdx >= 0) imageNodes.splice(imgIdx, 1);

                node.remove();
                buildGuideList();
                scheduleSave();
              }

              function removeEdge(edge) {
                const fromId = edge.data("source");
                const toId = edge.data("target");
                canvasDoc.edges = canvasDoc.edges.filter((e) => !(e.fromNode === fromId && e.toNode === toId));
                edge.remove();
                buildGuideList();
                scheduleSave();
              }

              // ---- Flowchart Element Palette & Modal ------------------------
              const FLOWCHART_TYPES = [
                { id: "start", label: "Start / Ende", icon: "🟢", shape: "ellipse", color: "#10b981", defaultText: "Start" },
                { id: "process", label: "Prozessschritt", icon: "🟦", shape: "round-rectangle", color: "#3b82f6", defaultText: "Prozess" },
                { id: "decision", label: "Entscheidung", icon: "🔶", shape: "diamond", color: "#f59e0b", defaultText: "Entscheidung?" },
                { id: "data", label: "Eingabe / Ausgabe", icon: "🔷", shape: "rhomboid", color: "#06b6d4", defaultText: "Daten" },
                { id: "doc", label: "Dokument / Beleg", icon: "📑", shape: "tag", color: "#0d9488", defaultText: "Dokument" },
                { id: "note", label: "Notiz / Anmerkung", icon: "📝", shape: "rectangle", color: "#eab308", defaultText: "Notiz" },
              ];

              const COLOR_PRESETS = [
                "#10b981", "#3b82f6", "#f59e0b", "#06b6d4", "#0d9488", "#eab308", "#8b5cf6", "#ef4444", "#64748b"
              ];

              const elementModal = document.getElementById("element-modal");
              const elementModalTitle = document.getElementById("element-modal-title");
              const shapeGrid = document.getElementById("shape-grid");
              const colorPalette = document.getElementById("color-palette");
              const elementInput = document.getElementById("element-input");
              const elementSaveBtn = document.getElementById("element-save-btn");
              const elementCancelBtn = document.getElementById("element-cancel-btn");
              const elementDropzone = document.getElementById("element-dropzone");
              const elementImageFile = document.getElementById("element-image-file");
              const elementImagePreviewWrapper = document.getElementById("element-image-preview-wrapper");
              const elementImagePreview = document.getElementById("element-image-preview");
              const elementImageInfo = document.getElementById("element-image-info");
              const elementImageRemoveBtn = document.getElementById("element-image-remove-btn");
              const canvasDropOverlay = document.getElementById("canvas-drop-overlay");

              let modalTargetPos = null;
              let modalEditNodeId = null;
              let selectedShape = "round-rectangle";
              let selectedColor = "#3b82f6";
              let modalImageDataUrl = null;
              let modalImageFileName = null;

              function readImageFileToDataUrl(file, callback) {
                if (!file || !file.type.startsWith("image/")) return;
                const reader = new FileReader();
                reader.onload = (e) => {
                  if (e.target && e.target.result) {
                    callback(e.target.result);
                  }
                };
                reader.readAsDataURL(file);
              }

              function setModalImage(dataUrl, fileName) {
                modalImageDataUrl = dataUrl;
                modalImageFileName = fileName || "Bild";
                if (dataUrl) {
                  if (elementImagePreview) elementImagePreview.src = dataUrl;
                  if (elementImageInfo) elementImageInfo.textContent = modalImageFileName;
                  if (elementImagePreviewWrapper) elementImagePreviewWrapper.hidden = false;
                  if (elementDropzone) elementDropzone.hidden = true;
                } else {
                  clearModalImage();
                }
              }

              function clearModalImage() {
                modalImageDataUrl = null;
                modalImageFileName = null;
                if (elementImagePreview) elementImagePreview.src = "";
                if (elementImageInfo) elementImageInfo.textContent = "";
                if (elementImagePreviewWrapper) elementImagePreviewWrapper.hidden = true;
                if (elementDropzone) elementDropzone.hidden = false;
                if (elementImageFile) elementImageFile.value = "";
              }

              elementImageRemoveBtn?.addEventListener("click", () => {
                clearModalImage();
              });

              elementDropzone?.addEventListener("click", () => {
                elementImageFile?.click();
              });

              elementImageFile?.addEventListener("change", (e) => {
                const file = e.target.files && e.target.files[0];
                if (file) {
                  readImageFileToDataUrl(file, (dataUrl) => {
                    setModalImage(dataUrl, file.name);
                    if (!elementInput.value || FLOWCHART_TYPES.some((ft) => ft.defaultText === elementInput.value)) {
                      elementInput.value = file.name.replace(/\.[^/.]+$/, "");
                    }
                  });
                }
              });

              elementDropzone?.addEventListener("dragover", (e) => {
                e.preventDefault();
                elementDropzone.classList.add("drag-over");
              });
              elementDropzone?.addEventListener("dragleave", () => {
                elementDropzone.classList.remove("drag-over");
              });
              elementDropzone?.addEventListener("drop", (e) => {
                e.preventDefault();
                elementDropzone.classList.remove("drag-over");
                const file = e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files[0];
                if (file && file.type.startsWith("image/")) {
                  readImageFileToDataUrl(file, (dataUrl) => {
                    setModalImage(dataUrl, file.name);
                    if (!elementInput.value || FLOWCHART_TYPES.some((ft) => ft.defaultText === elementInput.value)) {
                      elementInput.value = file.name.replace(/\.[^/.]+$/, "");
                    }
                  });
                }
              });

              function findNodeAt(modelPos) {
                const hit = cy.nodes().filter((n) => {
                  const box = n.boundingBox();
                  return modelPos.x >= box.x1 && modelPos.x <= box.x2 && modelPos.y >= box.y1 && modelPos.y <= box.y2;
                });
                return hit.length > 0 ? hit[0] : null;
              }

              window.addEventListener("dragover", (e) => {
                if (e.dataTransfer && Array.from(e.dataTransfer.types).includes("Files")) {
                  e.preventDefault();
                  if (canvasDropOverlay && canvasDropOverlay.hidden) {
                    canvasDropOverlay.hidden = false;
                  }
                }
              });
              window.addEventListener("dragleave", (e) => {
                if (e.relatedTarget === null || e.clientX <= 0 || e.clientY <= 0) {
                  if (canvasDropOverlay) canvasDropOverlay.hidden = true;
                }
              });
              window.addEventListener("drop", (e) => {
                if (canvasDropOverlay) canvasDropOverlay.hidden = true;
                const files = e.dataTransfer && e.dataTransfer.files;
                if (!files || files.length === 0) return;
                const file = files[0];
                if (!file.type.startsWith("image/")) return;
                e.preventDefault();

                const pos = modelPositionFromClient(e.clientX, e.clientY);
                const targetNode = findNodeAt(pos);

                readImageFileToDataUrl(file, (dataUrl) => {
                  if (targetNode) {
                    openEditElementModal(targetNode.id());
                    setModalImage(dataUrl, file.name);
                  } else {
                    openAddElementModal(pos, FLOWCHART_TYPES[1], dataUrl, file.name.replace(/\.[^/.]+$/, ""));
                  }
                });
              });

              document.addEventListener("paste", (e) => {
                const items = e.clipboardData && e.clipboardData.items;
                if (!items) return;
                for (let i = 0; i < items.length; i++) {
                  if (items[i].type.indexOf("image") !== -1) {
                    const file = items[i].getAsFile();
                    if (file) {
                      e.preventDefault();
                      readImageFileToDataUrl(file, (dataUrl) => {
                        if (elementModal && !elementModal.hidden) {
                          setModalImage(dataUrl, "Zwischenablage");
                        } else {
                          const pan = cy.pan();
                          const zoom = cy.zoom();
                          const centerPos = {
                            x: (cy.width() / 2 - pan.x) / zoom,
                            y: (cy.height() / 2 - pan.y) / zoom
                          };
                          openAddElementModal(centerPos, FLOWCHART_TYPES[1], dataUrl, "Screenshot");
                        }
                      });
                      break;
                    }
                  }
                }
              });

              function initElementModalControls() {
                if (!shapeGrid || !colorPalette) return;
                shapeGrid.innerHTML = "";
                FLOWCHART_TYPES.forEach((t) => {
                  const tile = document.createElement("div");
                  tile.className = "shape-tile" + (t.shape === selectedShape ? " active" : "");
                  tile.dataset.shape = t.shape;
                  tile.dataset.color = t.color;
                  tile.dataset.defaultText = t.defaultText;
                  tile.innerHTML = `<span class="shape-icon">${t.icon}</span><span>${t.label}</span>`;
                  tile.addEventListener("click", () => {
                    shapeGrid.querySelectorAll(".shape-tile").forEach((el) => el.classList.remove("active"));
                    tile.classList.add("active");
                    selectedShape = t.shape;
                    selectColor(t.color);
                    if (!modalEditNodeId && (!elementInput.value || FLOWCHART_TYPES.some((ft) => ft.defaultText === elementInput.value))) {
                      elementInput.value = t.defaultText;
                      elementInput.select();
                    }
                  });
                  shapeGrid.appendChild(tile);
                });

                colorPalette.innerHTML = "";
                COLOR_PRESETS.forEach((c) => {
                  const swatch = document.createElement("div");
                  swatch.className = "color-swatch" + (c.toLowerCase() === selectedColor.toLowerCase() ? " active" : "");
                  swatch.style.backgroundColor = c;
                  swatch.dataset.color = c;
                  swatch.addEventListener("click", () => selectColor(c));
                  colorPalette.appendChild(swatch);
                });
              }

              function selectColor(hex) {
                selectedColor = hex;
                if (colorPalette) {
                  colorPalette.querySelectorAll(".color-swatch").forEach((s) => {
                    s.classList.toggle("active", s.dataset.color.toLowerCase() === hex.toLowerCase());
                  });
                }
              }

              function selectShape(shapeName) {
                selectedShape = shapeName;
                if (shapeGrid) {
                  shapeGrid.querySelectorAll(".shape-tile").forEach((tile) => {
                    tile.classList.toggle("active", tile.dataset.shape === shapeName);
                  });
                }
              }

              initElementModalControls();

              function openAddElementModal(posModel, defaultType, initialImageDataUrl, initialLabel) {
                modalEditNodeId = null;
                modalTargetPos = posModel;
                const type = defaultType || FLOWCHART_TYPES[1];
                selectedShape = type.shape;
                selectedColor = type.color;
                elementModalTitle.innerHTML = `<span>➕</span> Element einfügen`;
                elementSaveBtn.textContent = "Einfügen";
                selectShape(type.shape);
                selectColor(type.color);
                elementInput.value = initialLabel || type.defaultText;
                if (initialImageDataUrl) {
                  setModalImage(initialImageDataUrl, initialLabel || "Bild");
                } else {
                  clearModalImage();
                }
                elementModal.hidden = false;
                setTimeout(() => { elementInput.focus(); elementInput.select(); }, 60);
              }

              function openEditElementModal(nodeId) {
                const node = cy.getElementById(nodeId);
                if (node.empty()) return;
                modalEditNodeId = nodeId;
                modalTargetPos = null;
                const textNode = textNodesById.get(nodeId);
                const currentShape = (textNode && textNode.shape) || node.data("shape") || "round-rectangle";
                const currentColor = (textNode && textNode.color) || node.data("color") || "#3b82f6";
                const currentText = textNode ? (textNode.text || "") : (node.data("label") || "");
                const currentImage = node.data("imageUrl");

                selectedShape = currentShape;
                selectedColor = currentColor;
                elementModalTitle.innerHTML = `<span>🎨</span> Form & Farbe anpassen`;
                elementSaveBtn.textContent = "Speichern";
                selectShape(currentShape);
                selectColor(currentColor);
                elementInput.value = currentText;
                if (currentImage) {
                  setModalImage(currentImage, "Aktuelles Bild");
                } else {
                  clearModalImage();
                }
                elementModal.hidden = false;
                setTimeout(() => { elementInput.focus(); elementInput.select(); }, 60);
              }

              function closeElementModal() {
                if (elementModal) elementModal.hidden = true;
                modalEditNodeId = null;
                modalTargetPos = null;
                clearModalImage();
              }

              function saveElementModal() {
                const text = elementInput.value.trim() || "Element";
                if (modalEditNodeId) {
                  const node = cy.getElementById(modalEditNodeId);
                  const textNode = textNodesById.get(modalEditNodeId);
                  if (node && !node.empty()) {
                    if (textNode) {
                      textNode.text = text;
                      textNode.shape = selectedShape;
                      textNode.color = selectedColor;
                    }
                    node.data("label", buildLabel(text));
                    node.data("shape", selectedShape);
                    node.data("color", selectedColor);

                    const hadImage = !!node.data("imageUrl");
                    if (modalImageDataUrl) {
                      node.data("imageUrl", modalImageDataUrl);
                      if (overlayImgs && overlayImgs.has(modalEditNodeId)) {
                        overlayImgs.get(modalEditNodeId).src = modalImageDataUrl;
                      } else {
                        const img = document.createElement("img");
                        img.className = "node-image-overlay";
                        img.alt = "";
                        img.src = modalImageDataUrl;
                        overlayContainer.appendChild(img);
                        overlayImgs.set(modalEditNodeId, img);
                      }

                      const existingImgIdx = imageNodes.findIndex((n) => n.data.id === modalEditNodeId);
                      if (existingImgIdx >= 0) {
                        imageNodes[existingImgIdx].data.imageUrl = modalImageDataUrl;
                        imageNodes[existingImgIdx].data.label = text;
                      } else {
                        imageNodes.push({ data: { id: modalEditNodeId, label: text, imageUrl: modalImageDataUrl } });
                      }

                      if (textNode) {
                        textNode.width = 380;
                        textNode.height = 60;
                        let imgSib = findImageSibling(textNode);
                        if (imgSib) {
                          imgSib.file = modalImageDataUrl;
                        } else {
                          imgSib = {
                            id: randomId(),
                            type: "file",
                            file: modalImageDataUrl,
                            x: textNode.x,
                            y: textNode.y + TEXT_NODE_HEIGHT + TEXT_TO_IMAGE_GAP,
                            width: 380,
                            height: 270
                          };
                          canvasDoc.nodes.push(imgSib);
                        }

                        let grpSib = findGroupSibling(textNode);
                        if (grpSib) {
                          grpSib.label = text;
                        } else {
                          grpSib = {
                            id: randomId(),
                            type: "group",
                            label: text,
                            x: textNode.x - GROUP_PADDING,
                            y: textNode.y - GROUP_PADDING,
                            width: 396,
                            height: 356
                          };
                          canvasDoc.nodes.push(grpSib);
                        }
                      }
                    } else if (hadImage) {
                      removeImageFromNode(modalEditNodeId, false);
                    }

                    buildGuideList();
                    scheduleViewerRaf();
                    scheduleSave();
                  }
                } else if (modalTargetPos) {
                  addManualElement(modalTargetPos.x, modalTargetPos.y, selectedShape, selectedColor, text, modalImageDataUrl);
                }
                closeElementModal();
              }

              elementSaveBtn?.addEventListener("click", saveElementModal);
              elementCancelBtn?.addEventListener("click", closeElementModal);
              elementModal?.addEventListener("click", (e) => {
                if (e.target === elementModal) closeElementModal();
              });

              // Dock "+ Element" Button
              const addElementBtn = document.getElementById("add-element-btn");
              if (addElementBtn) {
                addElementBtn.addEventListener("click", (e) => {
                  e.stopPropagation();
                  const pan = cy.pan();
                  const zoom = cy.zoom();
                  const centerPos = { x: (cy.width() / 2 - pan.x) / zoom, y: (cy.height() / 2 - pan.y) / zoom };
                  openAddElementModal(centerPos, FLOWCHART_TYPES[1]);
                });
              }

              function addManualElement(x, y, shape, color, label, imageUrl) {
                const newId = randomId();
                const nodeShape = shape || "round-rectangle";
                const nodeColor = color || "#3b82f6";
                const text = label || "Neuer Schritt";

                let width = 200;
                let height = 85;
                if (imageUrl) {
                  width = 380;
                  height = 60;
                } else if (nodeShape === "diamond") { width = 180; height = 90; }
                else if (nodeShape === "ellipse") { width = 160; height = 80; }
                else if (nodeShape === "rhomboid") { width = 200; height = 80; }
                else if (nodeShape === "tag") { width = 200; height = 90; }
                else if (nodeShape === "rectangle") { width = 220; height = 100; }

                const newCanvasNode = {
                  id: newId,
                  type: "text",
                  text: text,
                  x: x,
                  y: y,
                  width: width,
                  height: height,
                  shape: nodeShape,
                  color: nodeColor
                };
                canvasDoc.nodes.push(newCanvasNode);
                textNodesById.set(newId, newCanvasNode);

                if (imageUrl) {
                  const imageSibling = {
                    id: randomId(),
                    type: "file",
                    file: imageUrl,
                    x: x,
                    y: y + TEXT_NODE_HEIGHT + TEXT_TO_IMAGE_GAP,
                    width: 380,
                    height: 270
                  };
                  const groupSibling = {
                    id: randomId(),
                    type: "group",
                    label: text,
                    x: x - GROUP_PADDING,
                    y: y - GROUP_PADDING,
                    width: 396,
                    height: 356
                  };
                  canvasDoc.nodes.push(imageSibling, groupSibling);
                }

                const cyData = {
                  id: newId,
                  label: text,
                  color: nodeColor,
                  shape: nodeShape,
                  stepIndex: cy.nodes().length + 1
                };
                if (imageUrl) {
                  cyData.imageUrl = imageUrl;
                }

                cy.add({
                  group: "nodes",
                  data: cyData,
                  position: { x, y },
                  grabbable: false
                });

                if (imageUrl) {
                  const img = document.createElement("img");
                  img.className = "node-image-overlay";
                  img.alt = "";
                  img.src = imageUrl;
                  overlayContainer.appendChild(img);
                  overlayImgs.set(newId, img);
                  imageNodes.push({ data: cyData });
                }

                createHandlesForNode(cy.getElementById(newId));
                buildGuideList();
                scheduleViewerRaf();
                scheduleSave();
              }

              function removeImageFromNode(nodeId, save = true) {
                const node = cy.getElementById(nodeId);
                if (node.empty()) return;
                const textNode = textNodesById.get(nodeId);

                if (overlayImgs && overlayImgs.has(nodeId)) {
                  overlayImgs.get(nodeId).remove();
                  overlayImgs.delete(nodeId);
                }
                node.removeData("imageUrl");

                const imgIdx = imageNodes.findIndex((n) => n.data.id === nodeId);
                if (imgIdx >= 0) imageNodes.splice(imgIdx, 1);

                if (textNode) {
                  const imgSib = findImageSibling(textNode);
                  const grpSib = findGroupSibling(textNode);
                  canvasDoc.nodes = canvasDoc.nodes.filter(
                    (n) => (!imgSib || n.id !== imgSib.id) && (!grpSib || n.id !== grpSib.id)
                  );
                  textNode.width = 200;
                  textNode.height = 85;
                }

                buildGuideList();
                scheduleViewerRaf();
                if (save) scheduleSave();
              }

              function promptAddImageAt(pos) {
                openAddElementModal(pos, FLOWCHART_TYPES[1]);
                elementImageFile?.click();
              }

              function promptAttachImageToNode(nodeId) {
                openEditElementModal(nodeId);
                elementImageFile?.click();
              }

              // ---- Context Menu Dispatch (Node, Edge, Background) ----------
              cy.on("cxttap", (evt) => {
                const e = evt.originalEvent;
                const clientX = e ? e.clientX : 100;
                const clientY = e ? e.clientY : 100;

                if (evt.target === cy) {
                  // Canvas background right-click
                  const pos = modelPositionFromClient(clientX, clientY);
                  const items = [
                    { header: "+ Element einfügen" },
                    ...FLOWCHART_TYPES.map((t) => ({
                      icon: t.icon,
                      label: t.label,
                      action: () => openAddElementModal(pos, t)
                    })),
                    {
                      icon: "📷",
                      label: "Schritt mit Bild einfügen...",
                      action: () => promptAddImageAt(pos)
                    },
                    {
                      icon: "⚙️",
                      label: "Mehr Optionen (Form & Farbe)...",
                      action: () => openAddElementModal(pos, null)
                    },
                    { separator: true },
                    { icon: "⛶", label: "Ansicht einpassen (Fit)", action: () => ensureViewFit() },
                    { icon: "🔍", label: "Zoom 100%", action: () => { cy.zoom(1); cy.center(); scheduleViewerRaf(); } },
                    { icon: "📖", label: "Anleitung ein-/ausblenden", action: () => toggleGuide() }
                  ];
                  showContextMenu(items, clientX, clientY);
                  return;
                }

                if (evt.target.isNode && evt.target.isNode()) {
                  const node = evt.target;
                  const nodeId = node.id();
                  const nodeLabel = node.data("label") || "Knoten";
                  const hasImage = !!node.data("imageUrl");

                  const items = [];
                  items.push({ header: nodeLabel.length > 28 ? nodeLabel.slice(0, 28) + "…" : nodeLabel });

                  if (hasImage) {
                    const idx = imageNodes.findIndex((n) => n.data.id === nodeId);
                    if (idx >= 0) {
                      items.push({
                        icon: "🔍",
                        primary: true,
                        label: "Screenshot vergrößern",
                        action: () => openLightbox(idx)
                      });
                    }
                  }

                  items.push({
                    icon: "📖",
                    label: "In Anleitung anzeigen",
                    action: () => highlightGuideItem(nodeId)
                  });

                  items.push({ separator: true });
                  items.push({
                    icon: "🎨",
                    label: "Form & Farbe anpassen...",
                    action: () => openEditElementModal(nodeId)
                  });
                  items.push({
                    icon: "🖼️",
                    label: hasImage ? "Bild ändern..." : "Bild anhängen...",
                    action: () => promptAttachImageToNode(nodeId)
                  });
                  if (hasImage) {
                    items.push({
                      icon: "❌",
                      danger: true,
                      label: "Bild entfernen",
                      action: () => removeImageFromNode(nodeId)
                    });
                  }
                  items.push({
                    icon: "✏️",
                    label: "Beschreibung bearbeiten",
                    action: () => openRenameModal(nodeId)
                  });
                  items.push({
                    icon: "🔗",
                    label: "Verbindung von hier ziehen...",
                    action: () => {
                      connectFromId = nodeId;
                      connectStartClient = { x: clientX, y: clientY };
                      connectArmed = true;
                      node.addClass("connect-from");
                      connectLine.removeAttribute("hidden");
                    }
                  });
                  items.push({ separator: true });
                  items.push({
                    icon: "🗑️",
                    danger: true,
                    label: "Knoten löschen",
                    action: () => deleteNode(nodeId)
                  });

                  showContextMenu(items, clientX, clientY);
                  return;
                }

                if (evt.target.isEdge && evt.target.isEdge()) {
                  const edge = evt.target;
                  const currentColor = edge.data("color") || "#3b82f6";
                  const currentLineStyle = edge.data("lineStyle") || "solid";
                  const fromNode = cy.getElementById(edge.data("source"));
                  const toNode = cy.getElementById(edge.data("target"));
                  const fromLabel = fromNode.empty() ? "Knoten" : (fromNode.data("label") || "Knoten");
                  const toLabel = toNode.empty() ? "Knoten" : (toNode.data("label") || "Knoten");
                  const headerTitle = `Verbindung: ${fromLabel.slice(0, 10)} → ${toLabel.slice(0, 10)}`;

                  const items = [
                    { header: headerTitle },
                    { header: "🎨 Farbe wählen" },
                    {
                      colorBar: true,
                      activeColor: currentColor,
                      colors: COLOR_PRESETS,
                      onSelect: (color) => setEdgeColor(edge, color)
                    },
                    { separator: true },
                    {
                      icon: currentLineStyle === "dashed" ? "―" : "- -",
                      label: currentLineStyle === "dashed" ? "Linienstil: Durchgezogen" : "Linienstil: Gestrichelt",
                      action: () => toggleEdgeLineStyle(edge)
                    },
                    {
                      icon: "⇄",
                      label: "Richtung umkehren",
                      action: () => reverseEdge(edge)
                    },
                    { separator: true },
                    {
                      icon: "🗑️",
                      danger: true,
                      label: "Verbindung entfernen",
                      action: () => removeEdge(edge)
                    }
                  ];
                  showContextMenu(items, clientX, clientY);
                  return;
                }
              });

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

              function setEdgeColor(edge, color) {
                edge.data("color", color);
                const fromId = edge.data("source");
                const toId = edge.data("target");
                const cEdge = canvasDoc.edges.find((e) => e.fromNode === fromId && e.toNode === toId);
                if (cEdge) {
                  cEdge.color = color;
                }
                scheduleSave();
              }

              function toggleEdgeLineStyle(edge) {
                const current = edge.data("lineStyle") || "solid";
                const next = current === "dashed" ? "solid" : "dashed";
                edge.data("lineStyle", next);
                const fromId = edge.data("source");
                const toId = edge.data("target");
                const cEdge = canvasDoc.edges.find((e) => e.fromNode === fromId && e.toNode === toId);
                if (cEdge) {
                  cEdge.lineStyle = next;
                }
                scheduleSave();
              }

              function reverseEdge(edge) {
                const fromId = edge.data("source");
                const toId = edge.data("target");
                const color = edge.data("color") || "#3b82f6";
                const lineStyle = edge.data("lineStyle") || "solid";

                removeEdge(edge);

                const newId = `rev-${toId}->${fromId}-${randomId().slice(0, 4)}`;
                cy.add({
                  group: "edges",
                  data: { id: newId, source: toId, target: fromId, color: color, lineStyle: lineStyle, manual: true }
                });
                canvasDoc.edges.push({
                  id: randomId(),
                  fromNode: toId,
                  toNode: fromId,
                  fromSide: "bottom",
                  toSide: "top",
                  color: color,
                  lineStyle: lineStyle,
                  docuClickManual: true
                });
                buildGuideList();
                scheduleSave();
              }

              function tryConnect(fromId, toId) {
                if (fromId === toId) return;
                if (cy.edges().some((e) => e.data("source") === fromId && e.data("target") === toId)) return;
                const fromNode = cy.getElementById(fromId);
                const color = fromNode.data("color") || "#3b82f6";
                const edgeId = `manual-${fromId}->${toId}-${randomId().slice(0, 4)}`;
                cy.add({
                  group: "edges",
                  data: { id: edgeId, source: fromId, target: toId, color: color, manual: true, lineStyle: "solid" }
                });
                canvasDoc.edges.push({
                  id: randomId(),
                  fromNode: fromId,
                  toNode: toId,
                  fromSide: "bottom",
                  toSide: "top",
                  color: color,
                  lineStyle: "solid",
                  docuClickManual: true
                });
                buildGuideList();
                scheduleSave();
              }
            }
            </script>
            </body>
            </html>
            """;
    }
}
