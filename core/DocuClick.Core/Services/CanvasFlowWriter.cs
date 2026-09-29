using System.IO;
using System.Text.Json;

namespace DocuClick.Services;

/// <summary>
/// Writes clicks as connected nodes into a single, self-contained HTML flow
/// file (see <see cref="HtmlViewerBuilder"/>/<see cref="CanvasDocumentIo"/>)
/// — no Obsidian or any plugin needed to view or edit it. The underlying
/// node/edge JSON still mirrors Obsidian Canvas's own schema (kept for
/// compatibility with sessions recorded before this format existed). Each
/// click becomes a text node (the description) with a sibling "file" node
/// (the screenshot) directly beneath it; the text nodes form the linked
/// spine, linked from the previous click's text node.
///
/// Layout is vertical: the main line runs top-to-bottom in one column.
///
/// Branching: <see cref="MarkDecisionPoint"/> adds a small "◆ Abzweigung"
/// diamond connected from the current node and immediately forks its first
/// named "↳ Pfad: &lt;name&gt;" column, jumping the cursor onto it. Later,
/// <see cref="StartNewPath"/> forks another new column from the same
/// diamond, or <see cref="ContinuePath"/> resumes one started earlier —
/// mirroring a UML activity diagram's decision nodes and their outgoing
/// flows, screenshots instead of activity labels. Nothing about a path/
/// decision point is cached in memory — every lookup walks the actual
/// node/edge graph, so a Stop()/Start() cycle can never forget or desync
/// from what's really in the file. This is the sole flow-writing
/// implementation — a separate, non-branching plain-note writer existed
/// once and was removed once the HTML flow format no longer needed
/// Obsidian; draw.io is not a live-recording target either (see
/// <see cref="DrawIoConverter"/>) — it converts an existing session into a
/// .drawio file in one pass instead.
/// </summary>
public sealed class CanvasFlowWriter
{
    private const double NodeWidth = 380;
    private const double NodeHeight = 340;
    // internal: shared with DrawIoConverter, which needs the same
    // text-node/image-sibling geometry to locate a card's screenshot file
    // when converting an existing .canvas session into a .drawio export.
    internal const double TextNodeHeight = 60;
    internal const double TextToImageGap = 10;
    private const double ImageNodeHeight = NodeHeight - TextNodeHeight - TextToImageGap;
    private const double MarkerHeight = 60;
    private const double GroupPadding = 8; // margin between a card's group-node border and its text+image children
    private const double SequentialSpacing = 60; // gap between consecutive nodes along the main (vertical) flow
    private const double BranchColumnSpacing = 80; // gap between path columns

    // Leading icons give both kinds of marker a distinct at-a-glance look
    // in Obsidian Canvas, which — unlike draw.io's rhombus shape — has no
    // concept of node shapes at all, only plain text/file/link/group nodes.
    // internal: DrawIoConverter matches on the same text to recognize a
    // decision-point/path-start node when converting.
    internal const string DecisionPointLabel = "◆ Abzweigung";
    internal const string PathStartPrefix = "↳ Pfad: ";
    private const string DecisionPointColor = "6"; // Obsidian canvas preset color slot ("purple")
    private const string PathStartColor = "4"; // preset "green" — visually distinct from the decision point itself

    private readonly AppConfig _config;
    // Not WriteIndented: this JSON is embedded in a <script> block nothing
    // ever reads as pretty-printed text, and it gets rewritten on every
    // single click — the extra whitespace bytes are pure waste multiplied
    // by every save for the life of the session.
    private readonly JsonSerializerOptions _jsonOptions = new();

    /// <summary>
    /// Set by <see cref="SessionManager"/> to hand a callback back onto its
    /// own serial writer thread — <see cref="ScheduleBackgroundSave"/>'s
    /// debounced save must run there too, not on a bare ThreadPool thread,
    /// since every other read/mutation of <see cref="_doc"/> already runs
    /// exclusively on that thread via SessionManager.RunOnWriterQueue. When
    /// unset (e.g. a test harness using this class directly, with no
    /// SessionManager and hence no other thread that could race it), the
    /// save still runs, just on the bare ThreadPool thread as before.
    /// </summary>
    public Action<Action>? RunOnWriterThread { get; set; }

    private string? _canvasPath;
    private string _sessionName = "Session";
    private CanvasDocument _doc = new();
    private string? _cursorNodeId;
    private double _cursorX;
    private double _cursorY;
    private double _nextColumnX;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _base64Cache = new();

    public CanvasFlowWriter(AppConfig config)
    {
        _config = config;
    }

    /// <param name="canvasFilePath">Absolute path to the target .html file — there's no configured "output root" any more (see AppConfig's own history), so this is used exactly as given, not combined with anything.</param>
    public void StartSession(string canvasFilePath)
    {
        if (string.IsNullOrWhiteSpace(canvasFilePath) || !Path.IsPathRooted(canvasFilePath))
        {
            throw new InvalidOperationException("Kein gültiger Ablauf-Dateipfad angegeben.");
        }

        // Commit any still-pending drag/add-node save for whatever was
        // previously loaded before possibly switching _canvasPath/_doc out
        // from under it — the debounced continuation reads both at fire
        // time, so an unflushed edit here would otherwise get silently
        // lost (or, worse, written under the wrong path).
        FlushPendingSave();

        var fullPath = canvasFilePath;
        if (_canvasPath == fullPath && _doc is not null && _doc.Nodes.Count > 0)
        {
            // Session already open in memory: keep the current cursor if
            // it's still valid, otherwise fall through to the same
            // tip-resolution logic as a fresh load below.
            if (_cursorNodeId is not null && _doc.Nodes.Any(n => n.Id == _cursorNodeId))
            {
                return;
            }

            var openTargetIds = _doc.Edges.Where(e => !e.Manual).Select(e => e.ToNode).ToHashSet();
            var openRoot = _doc.Nodes
                .Where(n => n.Type == "text" && !openTargetIds.Contains(n.Id))
                .OrderBy(n => n.Y).ThenBy(n => n.X)
                .FirstOrDefault();

            if (openRoot is not null)
            {
                var openTip = FindBranchTip(openRoot);
                _cursorNodeId = openTip.Id;
                _cursorX = openTip.X;
                _cursorY = openTip.Y;
            }
            return;
        }

        // Loaded into a local first and only committed to instance state
        // once it succeeds — a corrupt-file exception from LoadOrCreate
        // (see CanvasDocumentIo.Load) must leave whatever session was
        // previously open untouched rather than half-switching to a
        // now-unusable target file.
        var loadedDoc = LoadOrCreate(fullPath);
        _canvasPath = fullPath;
        _sessionName = Path.GetFileNameWithoutExtension(canvasFilePath);
        _doc = loadedDoc;
        RememberDiskState(File.Exists(fullPath) && ObsidianVault.IsDiagramFile(fullPath) ? File.ReadAllText(fullPath) : null);
        _nextColumnX = _doc.Nodes.Count > 0 ? _doc.Nodes.Max(n => n.X) + NodeWidth + BranchColumnSpacing : 0;

        // An Ablauf from before browser saving existed has no save token
        // yet: write one right away, so the file can save its own edits
        // through DocuClick from then on (see LocalSaveService).
        if (_doc.SaveToken is null && _doc.Nodes.Count > 0 && CanvasDocumentIo.IsAblaufFile(fullPath))
        {
            Save();
        }

        // Resume the main flow's actual current tip rather than leaving the
        // cursor null. A null cursor meant every node-relative action
        // (MarkDecisionPoint included) failed with "kein Klick vorhanden"
        // until a throwaway click created *some* node first — confusing
        // right after deliberately resuming a file that already has
        // content. Placed in a fresh column so it never visually collides
        // with whatever's already in the file.
        var targetIds = _doc.Edges.Where(e => !e.Manual).Select(e => e.ToNode).ToHashSet();
        var root = _doc.Nodes
            .Where(n => n.Type == "text" && !targetIds.Contains(n.Id))
            .OrderBy(n => n.Y).ThenBy(n => n.X)
            .FirstOrDefault();

        if (root is not null)
        {
            var tip = FindBranchTip(root);
            _cursorNodeId = tip.Id;
            _cursorX = _nextColumnX;
            _cursorY = tip.Y;
        }
        else
        {
            // Truly empty file — nothing yet to attach to.
            _cursorNodeId = null;
            _cursorX = _nextColumnX;
            _cursorY = 0;
        }
    }

    public void Pause()
    {
        if (_canvasPath is not null)
        {
            Save();
        }
    }

    public void Stop()
    {
        if (_canvasPath is not null)
        {
            Relayout();
            Save();
        }

        _cursorNodeId = null;
        _canvasPath = null;
    }

    /// <summary>
    /// Recomputes every node's position into the same clean grid the
    /// Ablauf-Übersicht's minimap already shows — BFS row + alternating
    /// branch column, via the shared
    /// <see cref="FlowPreviewBranching.ComputeGridLayout"/> — instead of
    /// leaving the positions accumulated live during recording. Those
    /// accumulated positions drift out of row-alignment between columns
    /// whenever branches are recorded at different paces (each column's Y
    /// only ever grows by its own nodes' actual heights), which is exactly
    /// what turned a manual cross-connect between two columns into a
    /// visibly crossing diagonal line in Obsidian instead of a clean short
    /// connector — confirmed as a real complaint: the persisted .canvas
    /// file's layout didn't match the tidy minimap at all, forcing manual
    /// rework. Runs at <see cref="Stop"/> and also right after
    /// <see cref="ConnectNodes"/>/<see cref="DisconnectNodes"/> (the actions
    /// whose whole point is "does this line look clean"), rather than only
    /// once at the very end — see those methods' own comments.
    /// </summary>
    private void Relayout()
    {
        var preview = GetPreview();
        if (preview.Nodes.Count == 0)
        {
            return;
        }

        var slotOf = FlowPreviewBranching.ComputeGridLayout(preview);

        // Snapshot each content node's sibling image/group before any
        // position changes — FindImageSibling/FindGroupSibling match by
        // *current* relative position, which would misfire once earlier
        // nodes have already moved to their new slot. Indexed lookup (see
        // BuildSiblingIndex) rather than the single-node linear-scan
        // overload — this runs once per node in the whole document, so the
        // O(n) scan-per-node version made this O(n²).
        var textNodesById = _doc.Nodes.Where(n => n.Type == "text").ToDictionary(n => n.Id);
        var (imageIndex, groupIndex) = BuildSiblingIndex();
        var siblingsOf = textNodesById.Values.ToDictionary(
            n => n.Id, n => (Image: FindImageSibling(n, imageIndex), Group: FindGroupSibling(n, groupIndex)));

        var rowHeight = new Dictionary<int, double>();
        foreach (var node in preview.Nodes)
        {
            var (row, _) = slotOf[node.Id];
            var height = node.IsDecisionPoint || node.IsPathStart ? MarkerHeight : NodeHeight;
            rowHeight[row] = Math.Max(rowHeight.GetValueOrDefault(row), height);
        }

        var rowY = new Dictionary<int, double>();
        var y = 0.0;
        foreach (var row in rowHeight.Keys.OrderBy(r => r))
        {
            rowY[row] = y;
            y += rowHeight[row] + SequentialSpacing;
        }

        foreach (var node in preview.Nodes)
        {
            var (row, column) = slotOf[node.Id];
            var newX = column * (NodeWidth + BranchColumnSpacing);
            var newY = rowY[row];

            var textNode = textNodesById[node.Id];
            textNode.X = newX;
            textNode.Y = newY;

            var (imageSibling, groupSibling) = siblingsOf[node.Id];
            if (imageSibling is not null)
            {
                imageSibling.X = newX;
                imageSibling.Y = newY + TextNodeHeight + TextToImageGap;
            }

            if (groupSibling is not null)
            {
                groupSibling.X = newX - GroupPadding;
                groupSibling.Y = newY - GroupPadding;
            }
        }

        // Now called mid-session (not just right before Stop() nulls the
        // cursor anyway) — the live cursor position and the "where does the
        // next brand-new path column go" pointer must stay consistent with
        // whatever this just moved everything to. Without this, the very
        // next click or StartNewPath would be placed using stale
        // pre-relayout coordinates, immediately reintroducing the exact
        // misalignment this method exists to fix.
        if (_cursorNodeId is not null && slotOf.TryGetValue(_cursorNodeId, out var cursorSlot))
        {
            _cursorX = cursorSlot.Column * (NodeWidth + BranchColumnSpacing);
            _cursorY = rowY[cursorSlot.Row];
        }

        _nextColumnX = textNodesById.Values.Max(n => n.X) + NodeWidth + BranchColumnSpacing;
    }

    /// <summary>Short preview of the node the next click would connect from, if any.</summary>
    public string? CurrentNodeLabel => _cursorNodeId is null ? null : GetNodeLabel(_cursorNodeId);

    public void AddClickNode(string description, ScreenshotImage screenshot, DateTime timestamp)
    {
        if (_canvasPath is null)
        {
            throw new InvalidOperationException("Canvas-Session wurde nicht gestartet.");
        }

        SyncWithDisk();

        // Screenshots land in Attachments/<session>/ instead of flat in
        // Attachments/, inside the session's own folder (wherever its .html
        // file itself lives — there's no separate configured output root).
        var (imageRelativeToAttachments, imageBytes) = AttachmentSaver.SaveScreenshot(Path.GetDirectoryName(_canvasPath)!, _config.AttachmentsFolder, screenshot, timestamp, _sessionName);
        var imageOutputRelativePath = Path.Combine(_config.AttachmentsFolder, imageRelativeToAttachments).Replace('\\', '/');

        // Immediately cache the screenshot in memory as base64 data URI (zero disk re-read cost later)
        try
        {
            _base64Cache[imageOutputRelativePath] = "data:image/png;base64," + Convert.ToBase64String(imageBytes);
        }
        catch
        {
            // Ignore - fallback file read will load it if needed
        }

        var newY = _cursorNodeId is null ? _cursorY : _cursorY + NodeHeight + SequentialSpacing;

        // A visible bounding group behind the text+image pair — Canvas
        // edges only ever connect text nodes (see FindImageSibling's doc
        // comment), so without this the screenshot reads as a disconnected
        // element floating below the description instead of clearly
        // belonging with it. Dragging the group in Obsidian also moves both
        // children together, same idea as draw.io mode's container=1 card.
        // Added first so it renders behind its text/image children.
        var groupNode = new CanvasNode
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = "group",
            X = _cursorX - GroupPadding,
            Y = newY - GroupPadding,
            Width = NodeWidth + GroupPadding * 2,
            Height = TextNodeHeight + TextToImageGap + ImageNodeHeight + GroupPadding * 2
        };
        _doc.Nodes.Add(groupNode);

        var textNode = new CanvasNode
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = "text",
            Text = description,
            X = _cursorX,
            Y = newY,
            Width = NodeWidth,
            Height = TextNodeHeight
        };
        _doc.Nodes.Add(textNode);

        // A dedicated "file" node instead of a "![[filename]]" wikilink
        // buried in the text node — see the File property's doc comment
        // in CanvasModels.cs for why.
        var imageNode = new CanvasNode
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = "file",
            File = imageOutputRelativePath,
            X = _cursorX,
            Y = newY + TextNodeHeight + TextToImageGap,
            Width = NodeWidth,
            Height = ImageNodeHeight
        };
        _doc.Nodes.Add(imageNode);

        if (_cursorNodeId is not null)
        {
            _doc.Edges.Add(new CanvasEdge
            {
                Id = Guid.NewGuid().ToString("N"),
                FromNode = _cursorNodeId,
                ToNode = textNode.Id
            });
        }

        _cursorNodeId = textNode.Id;
        _cursorY = newY;

        Save();
    }

    /// <summary>
    /// Adds a small "◆ Abzweigung" diamond connected from the current node
    /// — an explicit, visible waypoint rather than hidden state — then
    /// immediately forks <paramref name="firstPathName"/> off it via
    /// <see cref="StartNewPath"/> and jumps the cursor onto that path.
    /// There's deliberately no bare, unnamed "just continue" state: every
    /// path leaving a decision point is a real, named node from the start,
    /// so it always shows up in <see cref="ListPaths"/> and can be resumed
    /// later — an implicit default continuation could never be listed
    /// there, making it permanently unreachable once you moved on.
    /// </summary>
    public BranchActionResult MarkDecisionPoint(string firstPathName)
    {
        if (_cursorNodeId is null)
        {
            return new BranchActionResult(false);
        }

        var markerY = _cursorY + NodeHeight + SequentialSpacing;
        var marker = new CanvasNode
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = "text",
            Text = DecisionPointLabel,
            X = _cursorX,
            Y = markerY,
            Width = NodeWidth,
            Height = MarkerHeight,
            Color = DecisionPointColor
        };
        _doc.Nodes.Add(marker);
        _doc.Edges.Add(new CanvasEdge
        {
            Id = Guid.NewGuid().ToString("N"),
            FromNode = _cursorNodeId,
            ToNode = marker.Id
        });

        // StartNewPath saves the whole document (marker included) once
        // it's done — no need to save here too.
        return StartNewPath(marker.Id, firstPathName);
    }

    /// <summary>Every path already forking from <paramref name="originNodeId"/>, for the Ablauf-Übersicht's per-node popup — resolved fresh from the graph, never cached.</summary>
    public List<PathInfo> ListPaths(string originNodeId)
    {
        var childIds = _doc.Edges.Where(e => e.FromNode == originNodeId).Select(e => e.ToNode).ToHashSet();
        return _doc.Nodes
            .Where(n => childIds.Contains(n.Id) && IsPathStartNode(n))
            .Select(n => new PathInfo(n.Id, ExtractPathName(n), FindBranchTip(n).Steps))
            .ToList();
    }

    /// <summary>
    /// Forks a brand-new named path from an existing node into its own
    /// column, and jumps the cursor onto it. The origin no longer has to be
    /// a decision-point diamond — any existing node can be the start of a
    /// retroactive alternate branch (see <see cref="JumpToNode"/> for why
    /// that's now required instead of silently forking).
    /// </summary>
    public BranchActionResult StartNewPath(string originNodeId, string pathName)
    {
        var originNode = _doc.Nodes.FirstOrDefault(n => n.Id == originNodeId && n.Type == "text");
        if (originNode is null)
        {
            return new BranchActionResult(false);
        }

        _nextColumnX += NodeWidth + BranchColumnSpacing;
        var pathStart = new CanvasNode
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = "text",
            Text = $"{PathStartPrefix}{pathName}",
            X = _nextColumnX,
            Y = originNode.Y,
            Width = NodeWidth,
            Height = MarkerHeight,
            Color = PathStartColor
        };
        _doc.Nodes.Add(pathStart);
        _doc.Edges.Add(new CanvasEdge
        {
            Id = Guid.NewGuid().ToString("N"),
            FromNode = originNode.Id,
            ToNode = pathStart.Id
        });

        _cursorNodeId = pathStart.Id;
        _cursorX = pathStart.X;
        _cursorY = pathStart.Y;

        Save();
        return new BranchActionResult(true);
    }

    /// <summary>Resumes an existing path at wherever it currently ends (walked fresh from the graph — see <see cref="FindBranchTip"/>), in its own already-established column.</summary>
    public BranchActionResult ContinuePath(string pathStartNodeId)
    {
        var pathStart = _doc.Nodes.FirstOrDefault(n => n.Id == pathStartNodeId && IsPathStartNode(n));
        if (pathStart is null)
        {
            return new BranchActionResult(false);
        }

        var tip = FindBranchTip(pathStart);
        _cursorNodeId = tip.Id;
        _cursorX = tip.X;
        _cursorY = tip.Y;

        return new BranchActionResult(true);
    }

    /// <summary>Snapshot of every node currently in the flow (position + size + which one the cursor is on) for the live tree-preview overlay.</summary>
    public FlowPreview GetPreview()
    {
        var (imageIndex, _) = BuildSiblingIndex();
        var nodes = _doc.Nodes
            .Where(n => n.Type == "text")
            .Select(n =>
            {
                var isDecisionPoint = IsDecisionPointNode(n);
                var isPathStart = IsPathStartNode(n);
                var label = isDecisionPoint ? "Abzweigung" : isPathStart ? $"↳ {ExtractPathName(n)}" : BuildLabel(n.Text);
                // Markers never get an image sibling (see AddClickNode); a
                // content node's is found by the same fixed relative-position
                // lookup DeleteNode/Relayout already use to move it alongside
                // its text node.
                var imagePath = isDecisionPoint || isPathStart ? null : FindImageSibling(n, imageIndex)?.File;
                return new PreviewNode(
                    n.Id, label, n.X, n.Y, n.Width, n.Height,
                    n.Id == _cursorNodeId, isDecisionPoint, isPathStart,
                    PathName: isPathStart ? ExtractPathName(n) : null,
                    ImagePath: imagePath,
                    Shape: n.Shape,
                    Color: n.Color);
            })
            .ToList();
        var edges = _doc.Edges.Select(e => new PreviewEdge(e.FromNode, e.ToNode, e.Manual, e.Color, e.LineStyle)).ToList();
        return FlowPreviewBranching.TagBranches(new FlowPreview(nodes, edges));
    }

    /// <summary>
    /// Jumps the cursor to an arbitrary existing node — always resolved
    /// forward to that branch's current tip (via <see cref="FindBranchTip"/>)
    /// and resumed exactly there, in the same column. Never opens a new
    /// column and never adds a second outgoing edge from a node that
    /// already has one: clicking a node that already has downstream
    /// content is "continue where this branch left off", not "silently
    /// fork a second, untracked branch from here" — that used to create a
    /// second edge with no <see cref="PreviewNode.PathId"/> of its own,
    /// which the Ablauf-Übersicht's graph-based layout then collapsed onto
    /// the same grid cell as whatever else followed that node, drawing two
    /// unrelated nodes on top of each other. A deliberate new branch from a
    /// non-tip node now goes through <see cref="StartNewPath"/> instead
    /// (see the Ablauf-Übersicht's per-node popup), which gives it a real
    /// name and its own <see cref="PreviewNode.PathId"/>/column. The Ablauf-
    /// Übersicht uses this for click-to-navigate on a node that's already a
    /// tip (no downstream content); for a node with existing children it
    /// instead shows a popup offering this ("→ Weiter") alongside
    /// <see cref="StartNewPath"/> ("+ Neuer Pfad ab hier").
    /// </summary>
    public BranchActionResult JumpToNode(string nodeId)
    {
        var node = _doc.Nodes.FirstOrDefault(n => n.Id == nodeId && n.Type == "text");
        if (node is null)
        {
            return new BranchActionResult(false);
        }

        var tip = FindBranchTip(node);
        _cursorNodeId = tip.Id;
        _cursorX = tip.X;
        _cursorY = tip.Y;

        return new BranchActionResult(true);
    }

    /// <summary>
    /// Renames a node's label (or, for a path-start marker, its path name —
    /// the "↳ Pfad: " prefix is kept). Decision-point diamonds can't be
    /// renamed — their fixed "◆ Abzweigung" text is how one is recognized
    /// as a decision point in the first place.
    /// </summary>
    public BranchActionResult RenameNode(string nodeId, string newLabel)
    {
        var node = _doc.Nodes.FirstOrDefault(n => n.Id == nodeId && n.Type == "text");
        if (node is null || IsDecisionPointNode(node))
        {
            return new BranchActionResult(false);
        }

        node.Text = IsPathStartNode(node) ? $"{PathStartPrefix}{newLabel}" : newLabel;
        Save();
        return new BranchActionResult(true);
    }

    /// <summary>
    /// Exactly one outgoing *structural* edge: the parent is reconnected
    /// straight to that child so the branch below isn't orphaned. More than
    /// one (a decision point, or any node a path was forked from): the
    /// whole downstream subtree goes with it — the caller (the Ablauf-
    /// Übersicht) is responsible for confirming that with the user first,
    /// since there is no single "the" continuation to stitch to here.
    /// Manual cross-connect edges (<see cref="CanvasEdge.Manual"/>, added
    /// via <see cref="ConnectNodes"/>) never count toward this and are
    /// never cascaded through — they're an additive reference to some other,
    /// independently-anchored part of the flow, not a fork this node owns.
    /// Deleting this node still drops any manual edge touching it (nothing
    /// left to connect from/to), it just doesn't take the far end's subtree
    /// down with it. If the deleted node (or one of its cascaded
    /// descendants) was the current cursor, the cursor moves to the
    /// parent's branch tip (or null, if the deleted node was a root).
    /// </summary>
    public BranchActionResult DeleteNode(string nodeId)
    {
        var node = _doc.Nodes.FirstOrDefault(n => n.Id == nodeId && n.Type == "text");
        if (node is null)
        {
            return new BranchActionResult(false);
        }

        var childEdges = _doc.Edges.Where(e => e.FromNode == nodeId && !e.Manual).ToList();
        var parentEdge = _doc.Edges.FirstOrDefault(e => e.ToNode == nodeId && !e.Manual);

        // A path-start node's single child is never itself tagged with the
        // path's identity (only the path-start node is — see
        // FlowPreviewBranching.TagBranches) — stitching parent straight to
        // that child like an ordinary 1-child node would silently erase
        // which path it belonged to, collapsing it back onto whatever the
        // path forked from. That's the exact same "second, untracked branch
        // with no PathId" shape JumpToNode used to create by accident (see
        // its own doc comment) — so a path-start with a child must cascade
        // just like a >1-child node does, even though it only has the one.
        var toRemove = new HashSet<string> { nodeId };
        if (childEdges.Count > 1 || (childEdges.Count == 1 && IsPathStartNode(node)))
        {
            var queue = new Queue<string>(childEdges.Select(e => e.ToNode));
            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                if (!toRemove.Add(id))
                {
                    continue;
                }

                foreach (var e in _doc.Edges.Where(e => e.FromNode == id && !e.Manual))
                {
                    queue.Enqueue(e.ToNode);
                }
            }
        }

        foreach (var id in toRemove)
        {
            var n = _doc.Nodes.FirstOrDefault(x => x.Id == id);
            if (n is null)
            {
                continue;
            }

            var imageSibling = FindImageSibling(n);
            if (imageSibling is not null)
            {
                _doc.Nodes.Remove(imageSibling);
            }

            var groupSibling = FindGroupSibling(n);
            if (groupSibling is not null)
            {
                _doc.Nodes.Remove(groupSibling);
            }

            _doc.Nodes.Remove(n);
        }

        _doc.Edges.RemoveAll(e => toRemove.Contains(e.FromNode) || toRemove.Contains(e.ToNode));

        if (childEdges.Count == 1 && parentEdge is not null && !IsPathStartNode(node))
        {
            _doc.Edges.Add(new CanvasEdge
            {
                Id = Guid.NewGuid().ToString("N"),
                FromNode = parentEdge.FromNode,
                ToNode = childEdges[0].ToNode
            });
        }

        if (_cursorNodeId is not null && toRemove.Contains(_cursorNodeId))
        {
            if (parentEdge is not null)
            {
                var parentNode = _doc.Nodes.First(n => n.Id == parentEdge.FromNode);
                var tip = FindBranchTip(parentNode);
                _cursorNodeId = tip.Id;
                _cursorX = tip.X;
                _cursorY = tip.Y;
            }
            else
            {
                _cursorNodeId = null;
            }
        }

        Save();
        return new BranchActionResult(true);
    }

    /// <summary>
    /// Manually connects two existing nodes with a new edge — for the
    /// Ablauf-Übersicht's drag-to-connect gesture, when the recorded flow
    /// itself doesn't already capture some real transition (e.g. a step
    /// that loops back to an earlier one). Additive: no existing edge is
    /// removed, so <paramref name="toNodeId"/> can end up with more than one
    /// incoming edge — a genuine merge point, not a bug (the Ablauf-
    /// Übersicht's row/column layout just picks whichever parent it reaches
    /// <paramref name="toNodeId"/> from first). Only ordinary content nodes
    /// qualify, on both ends — a decision point's/path-start's role as a
    /// branch hub or a path's own identity would break if either could be
    /// connected into or out of arbitrarily. Refuses (returns failure) if
    /// <paramref name="toNodeId"/> can already reach <paramref name="fromNodeId"/>,
    /// which would create a cycle.
    /// </summary>
    public BranchActionResult ConnectNodes(string fromNodeId, string toNodeId)
    {
        if (fromNodeId == toNodeId)
        {
            return new BranchActionResult(false);
        }

        var from = _doc.Nodes.FirstOrDefault(n => n.Id == fromNodeId && n.Type == "text");
        var to = _doc.Nodes.FirstOrDefault(n => n.Id == toNodeId && n.Type == "text");
        if (from is null || to is null
            || IsDecisionPointNode(from) || IsPathStartNode(from)
            || IsDecisionPointNode(to) || IsPathStartNode(to))
        {
            return new BranchActionResult(false);
        }

        if (_doc.Edges.Any(e => e.FromNode == fromNodeId && e.ToNode == toNodeId))
        {
            return new BranchActionResult(false); // already connected, nothing to do
        }

        // Cycle guard: toNodeId must not already be able to *structurally*
        // reach fromNodeId. Deliberately walks only structural edges, not
        // other manual cross-connects — those are non-structural references
        // FindBranchTip/DeleteNode never traverse either, so a real
        // structural cycle can't actually form through one. Without this
        // exclusion, an earlier unrelated manual connection could make a
        // perfectly fine new one look like a false cycle and get rejected
        // (confirmed as a real bug — some cross-connects became impossible
        // to add once other, unrelated ones already existed).
        var reachableFromTo = new HashSet<string>();
        var queue = new Queue<string>(_doc.Edges.Where(e => e.FromNode == toNodeId && !e.Manual).Select(e => e.ToNode));
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!reachableFromTo.Add(id))
            {
                continue;
            }

            foreach (var e in _doc.Edges.Where(e => e.FromNode == id && !e.Manual))
            {
                queue.Enqueue(e.ToNode);
            }
        }

        if (reachableFromTo.Contains(fromNodeId))
        {
            return new BranchActionResult(false);
        }

        var fromNode = _doc.Nodes.FirstOrDefault(n => n.Id == fromNodeId);
        _doc.Edges.Add(new CanvasEdge
        {
            Id = Guid.NewGuid().ToString("N"),
            FromNode = fromNodeId,
            ToNode = toNodeId,
            Manual = true,
            Color = fromNode?.Color,
            LineStyle = "solid"
        });

        // Manual connection added: skip Relayout() so manually arranged node positions are preserved.
        Save();
        return new BranchActionResult(true);
    }

    /// <summary>
    /// Removes an existing edge between two ordinary content nodes — the
    /// undo counterpart to <see cref="ConnectNodes"/>, for the Ablauf-
    /// Übersicht's right-click-an-edge gesture. Same marker restriction as
    /// <see cref="ConnectNodes"/>: a decision point's/path-start's
    /// structural edges (into it, or its own fork out of a decision point)
    /// can't be removed this way, since that would silently detach a whole
    /// path from <see cref="ListPaths"/> while leaving its nodes behind,
    /// unreachable but not deleted — a confusing half-state. Deliberately
    /// does *not* refuse just because a node would end up with no remaining
    /// edges at all (fully isolated) — the caller doesn't have to reconnect
    /// it to anything else; the Ablauf-Übersicht's layout places an isolated
    /// node in its own row/column rather than overlapping it onto whatever
    /// else happens to sit at the origin. Removes any edge between the two
    /// nodes, structural or manual, and preserves manually arranged
    /// positions by skipping Relayout(). Refuses (returns failure) if no
    /// such edge exists.
    /// </summary>
    public BranchActionResult DisconnectNodes(string fromNodeId, string toNodeId)
    {
        var from = _doc.Nodes.FirstOrDefault(n => n.Id == fromNodeId && n.Type == "text");
        var to = _doc.Nodes.FirstOrDefault(n => n.Id == toNodeId && n.Type == "text");
        if (from is null || to is null
            || IsDecisionPointNode(from) || IsPathStartNode(from)
            || IsDecisionPointNode(to) || IsPathStartNode(to))
        {
            return new BranchActionResult(false);
        }

        var edge = _doc.Edges.FirstOrDefault(e => e.FromNode == fromNodeId && e.ToNode == toNodeId);
        if (edge is null)
        {
            return new BranchActionResult(false);
        }

        _doc.Edges.Remove(edge);
        Save();
        return new BranchActionResult(true);
    }

    /// <summary>Updates color and line style of an edge.</summary>
    public BranchActionResult SetEdgeStyle(string fromNodeId, string toNodeId, string? color, string? lineStyle)
    {
        var edge = _doc.Edges.FirstOrDefault(e => e.FromNode == fromNodeId && e.ToNode == toNodeId);
        if (edge is null)
        {
            return new BranchActionResult(false);
        }

        edge.Color = color;
        edge.LineStyle = lineStyle;
        Save();
        return new BranchActionResult(true);
    }

    /// <summary>Reverses direction of an edge.</summary>
    public BranchActionResult ReverseEdge(string fromNodeId, string toNodeId)
    {
        var edge = _doc.Edges.FirstOrDefault(e => e.FromNode == fromNodeId && e.ToNode == toNodeId);
        if (edge is null)
        {
            return new BranchActionResult(false);
        }

        edge.FromNode = toNodeId;
        edge.ToNode = fromNodeId;
        edge.Manual = true;
        Save();
        return new BranchActionResult(true);
    }

    /// <summary>
    /// Moves a node (and its screenshot/group siblings, kept aligned by the
    /// same fixed relative-position match every other sibling lookup here
    /// uses) to an explicit new position — the Ablauf-Übersicht's drag-to-
    /// move gesture. Unlike every other mutating method here, this
    /// deliberately does *not* re-run the auto-layout: the whole point is
    /// letting the user override the computed grid arrangement, and
    /// snapping it straight back would defeat the drag that just happened.
    /// That also means it's not a *permanent* override — there's no
    /// "pinned position" concept in the file format, so the next action
    /// that does re-run the auto-layout (a new click, Connect/Disconnect, a
    /// new decision point/path) discards it again.
    /// </summary>
    public BranchActionResult MoveNode(string nodeId, double x, double y)
    {
        var node = _doc.Nodes.FirstOrDefault(n => n.Id == nodeId && n.Type == "text");
        if (node is null)
        {
            return new BranchActionResult(false);
        }

        // Found using the *old* position, same as Relayout's own snapshot-
        // before-moving comment explains — looking these up after node.X/Y
        // below already changed would misfire.
        var image = FindImageSibling(node);
        var group = FindGroupSibling(node);
        var dx = x - node.X;
        var dy = y - node.Y;

        node.X = x;
        node.Y = y;
        if (image is not null)
        {
            image.X += dx;
            image.Y += dy;
        }

        if (group is not null)
        {
            group.X += dx;
            group.Y += dy;
        }

        // If this is the live cursor node, the next AddClickNode must
        // attach relative to *this* new position, not the stale one —
        // otherwise the very next screenshot lands where this card used to
        // be, as if the move never happened (confirmed as a real bug).
        // Mirrors Relayout()'s own comment on why it re-syncs these too.
        if (_cursorNodeId == nodeId)
        {
            _cursorX = x;
            _cursorY = y;
        }

        ScheduleBackgroundSave();
        return new BranchActionResult(true);
    }

    /// <summary>Batch move for multiple nodes dragged together — performs all position updates in memory and schedules a single background save.</summary>
    public BranchActionResult MoveNodes(IReadOnlyList<(string NodeId, double X, double Y)> moves)
    {
        bool anyMoved = false;
        foreach (var (nodeId, x, y) in moves)
        {
            var node = _doc.Nodes.FirstOrDefault(n => n.Id == nodeId && n.Type == "text");
            if (node is null) continue;

            var image = FindImageSibling(node);
            var group = FindGroupSibling(node);
            var dx = x - node.X;
            var dy = y - node.Y;

            node.X = x;
            node.Y = y;
            if (image is not null)
            {
                image.X += dx;
                image.Y += dy;
            }

            if (group is not null)
            {
                group.X += dx;
                group.Y += dy;
            }

            if (_cursorNodeId == nodeId)
            {
                _cursorX = x;
                _cursorY = y;
            }

            anyMoved = true;
        }

        if (anyMoved)
        {
            ScheduleBackgroundSave();
        }

        return new BranchActionResult(anyMoved);
    }

    /// <summary>
    /// Creates a brand-new, isolated content node at an explicit position —
    /// the Ablauf-Übersicht's UML-style "+ Neuer Knoten hier" gesture on the
    /// empty canvas background. No screenshot and no edges: it starts out
    /// exactly like a node that's been <see cref="DisconnectNodes"/>'d from
    /// everything, ready to be connected via <see cref="ConnectNodes"/> or
    /// moved via <see cref="MoveNode"/> like any other. Like MoveNode, does
    /// *not* re-run the auto-layout — the whole point is placing it exactly
    /// where the user right-clicked.
    /// </summary>
    public BranchActionResult AddManualNode(string label, double x, double y, string? shape = null, string? color = null)
    {
        var node = new CanvasNode
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = "text",
            Text = label,
            X = x,
            Y = y,
            Width = shape switch
            {
                "diamond" => 180,
                "ellipse" => 160,
                "rhomboid" => 200,
                "tag" => 200,
                "rectangle" => 220,
                _ => NodeWidth
            },
            Height = shape switch
            {
                "diamond" => 90,
                "ellipse" => 80,
                "rhomboid" => 80,
                "tag" => 100,
                "rectangle" => 140,
                _ => TextNodeHeight
            },
            Shape = shape,
            Color = color
        };
        _doc.Nodes.Add(node);

        ScheduleBackgroundSave();
        return new BranchActionResult(true);
    }

    /// <summary>Creates a brand-new node with an attached external image at an explicit position.</summary>
    public BranchActionResult AddManualImageNode(string label, string imageSourcePath, double x, double y)
    {
        if (_canvasPath is null)
        {
            throw new InvalidOperationException("Canvas-Session wurde nicht gestartet.");
        }

        var (imageRelativeToAttachments, imageBytes) = AttachmentSaver.SaveImage(Path.GetDirectoryName(_canvasPath)!, _config.AttachmentsFolder, imageSourcePath, _sessionName);
        var imageOutputRelativePath = Path.Combine(_config.AttachmentsFolder, imageRelativeToAttachments).Replace('\\', '/');

        try
        {
            _base64Cache[imageOutputRelativePath] = "data:image/png;base64," + Convert.ToBase64String(imageBytes);
        }
        catch
        {
            // Ignore - fallback file read will load it if needed
        }

        var groupNode = new CanvasNode
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = "group",
            X = x - GroupPadding,
            Y = y - GroupPadding,
            Width = NodeWidth + GroupPadding * 2,
            Height = TextNodeHeight + TextToImageGap + ImageNodeHeight + GroupPadding * 2
        };
        _doc.Nodes.Add(groupNode);

        var textNode = new CanvasNode
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = "text",
            Text = label,
            X = x,
            Y = y,
            Width = NodeWidth,
            Height = TextNodeHeight
        };
        _doc.Nodes.Add(textNode);

        var imageNode = new CanvasNode
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = "file",
            File = imageOutputRelativePath,
            X = x,
            Y = y + TextNodeHeight + TextToImageGap,
            Width = NodeWidth,
            Height = ImageNodeHeight
        };
        _doc.Nodes.Add(imageNode);

        ScheduleBackgroundSave();
        return new BranchActionResult(true);
    }

    /// <summary>The sibling "file" (image) node created alongside a content node in <see cref="AddClickNode"/> — identified by its fixed position relative to the text node, since the two are never edge-linked to each other. A single-node lookup (linear scan) — fine for the handful of one-off call sites (MoveNode, DeleteNode); a loop over every node in the document should use the indexed overload below instead (see <see cref="BuildSiblingIndex"/>'s own doc comment for why).</summary>
    private CanvasNode? FindImageSibling(CanvasNode textNode) => _doc.Nodes.FirstOrDefault(n =>
        n.Type == "file" && Math.Abs(n.X - textNode.X) < 0.5 && Math.Abs(n.Y - (textNode.Y + TextNodeHeight + TextToImageGap)) < 0.5);

    /// <summary>The sibling "group" node wrapping a content node and its image, created alongside both in <see cref="AddClickNode"/> — same fixed-position lookup as <see cref="FindImageSibling"/>. Marker nodes (decision points/path starts) never get one. Single-node lookup — see that method's own doc comment on when to use the indexed overload instead.</summary>
    private CanvasNode? FindGroupSibling(CanvasNode textNode) => _doc.Nodes.FirstOrDefault(n =>
        n.Type == "group" && Math.Abs(n.X - (textNode.X - GroupPadding)) < 0.5 && Math.Abs(n.Y - (textNode.Y - GroupPadding)) < 0.5);

    /// <summary>
    /// Precomputed once per pass, indexed variants of
    /// <see cref="FindImageSibling(CanvasNode)"/>/<see cref="FindGroupSibling(CanvasNode)"/>
    /// for the three call sites (<see cref="GetPreview"/>, <see cref="BuildLiveHtml"/>,
    /// <see cref="Relayout"/>) that look up a sibling for *every* node in the
    /// document in a loop — each single-node lookup above is itself a linear
    /// scan over every node, so calling it once per node made a whole pass
    /// O(n²) (confirmed as a real, worsening-with-session-length slowdown:
    /// GetPreview() alone runs twice per recorded click). Positions here are
    /// always the result of this same class's own deterministic arithmetic
    /// (fixed-size constants added/subtracted, or MoveNode's dx/dy applied
    /// identically to both siblings), never independently computed, so
    /// rounding to the nearest whole pixel for the dictionary key can never
    /// merge two genuinely different siblings (the smallest real gap between
    /// node types is <see cref="GroupPadding"/>, 8 units) or miss a real
    /// match (which is always exactly, not approximately, equal pre-rounding).
    /// </summary>
    private (Dictionary<(double X, double Y), CanvasNode> Images, Dictionary<(double X, double Y), CanvasNode> Groups) BuildSiblingIndex()
    {
        var images = new Dictionary<(double, double), CanvasNode>();
        var groups = new Dictionary<(double, double), CanvasNode>();
        foreach (var n in _doc.Nodes)
        {
            if (n.Type == "file")
            {
                images[(Math.Round(n.X), Math.Round(n.Y))] = n;
            }
            else if (n.Type == "group")
            {
                groups[(Math.Round(n.X), Math.Round(n.Y))] = n;
            }
        }

        return (images, groups);
    }

    private static CanvasNode? FindImageSibling(CanvasNode textNode, Dictionary<(double X, double Y), CanvasNode> imageIndex) =>
        imageIndex.TryGetValue((Math.Round(textNode.X), Math.Round(textNode.Y + TextNodeHeight + TextToImageGap)), out var n) ? n : null;

    private static CanvasNode? FindGroupSibling(CanvasNode textNode, Dictionary<(double X, double Y), CanvasNode> groupIndex) =>
        groupIndex.TryGetValue((Math.Round(textNode.X - GroupPadding), Math.Round(textNode.Y - GroupPadding)), out var n) ? n : null;

    private static bool IsDecisionPointNode(CanvasNode n) => n.Type == "text" && n.Text == DecisionPointLabel;

    private static bool IsPathStartNode(CanvasNode n) =>
        n.Type == "text" && (n.Text?.StartsWith(PathStartPrefix, StringComparison.Ordinal) ?? false);

    private static string ExtractPathName(CanvasNode n) => n.Text![PathStartPrefix.Length..].Trim();

    /// <summary>
    /// Follows the single-child *structural* edge chain from
    /// <paramref name="start"/> as far as it goes, resolving a path's
    /// current tip fresh from the graph every time (never cached, so it can
    /// never desync from what's actually in the file). Manual cross-connect
    /// edges are never followed here — a node whose only outgoing edge
    /// happens to be a manual one is still a real tip; following it into
    /// whatever it was cross-connected to used to silently walk the cursor
    /// into a completely different, unrelated tree (confirmed as a real
    /// bug: the next click would then graft new content onto that other
    /// tree instead, and deleting things later cascaded into it).
    /// </summary>
    private (string Id, double X, double Y, int Steps) FindBranchTip(CanvasNode start)
    {
        var current = start;
        var steps = 0;
        while (true)
        {
            var nextEdge = _doc.Edges.FirstOrDefault(e => e.FromNode == current.Id && !e.Manual);
            var nextNode = nextEdge is null ? null : _doc.Nodes.FirstOrDefault(n => n.Id == nextEdge.ToNode);
            if (nextNode is null)
            {
                return (current.Id, current.X, current.Y, steps);
            }

            current = nextNode;
            steps++;
        }
    }

    private string? GetNodeLabel(string nodeId) =>
        BuildLabel(_doc.Nodes.FirstOrDefault(n => n.Id == nodeId)?.Text);

    private static string BuildLabel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(ohne Beschreibung)";
        }

        return text.Trim();
    }

    private CanvasDocument LoadOrCreate(string path) => CanvasDocumentIo.Load(path);

    private readonly object _saveLock = new();
    private CancellationTokenSource? _saveCts;

    /// <summary>
    /// Commits an outstanding debounced save (from <see cref="MoveNode"/>/
    /// <see cref="AddManualNode"/>) synchronously right now, instead of
    /// waiting for its timer. Needed in two places a bare 150ms delay can
    /// otherwise silently lose the edit: <see cref="StartSession"/> before
    /// switching to a different (or the same) file — the debounced
    /// continuation reads <see cref="_canvasPath"/>/<see cref="_doc"/> at
    /// fire time, not schedule time, so a file switch in that window would
    /// either write the wrong file's content under the pending save's old
    /// path or drop the edit entirely — and <see cref="SessionManager.Dispose"/>
    /// before the process can exit out from under a still-pending save.
    /// </summary>
    /// <summary>
    /// Replaces the loaded document with one edited outside DocuClick (the
    /// Ablauf page in a browser, via <see cref="LocalSaveService"/>) and saves
    /// it right away. The file's save token is kept; the cursor stays where
    /// it was if that node still exists, else moves to the main flow's tip.
    /// </summary>
    public void ReplaceDocument(CanvasDocument document)
    {
        if (_canvasPath is null)
        {
            throw new InvalidOperationException("Kein Ablauf geladen.");
        }

        document.SaveToken = _doc.SaveToken ?? document.SaveToken;
        AdoptDocument(document);
        Save();
    }

    /// <summary>Makes <paramref name="document"/> the loaded one; keeps the cursor if its node still exists, else moves it to the main flow's tip.</summary>
    private void AdoptDocument(CanvasDocument document)
    {
        _doc = document;
        _nextColumnX = _doc.Nodes.Count > 0 ? _doc.Nodes.Max(n => n.X) + NodeWidth + BranchColumnSpacing : 0;

        if (_cursorNodeId is null || _doc.Nodes.All(n => n.Id != _cursorNodeId))
        {
            var targetIds = _doc.Edges.Where(e => !e.Manual).Select(e => e.ToNode).ToHashSet();
            var root = _doc.Nodes.Where(n => n.Type == "text" && !targetIds.Contains(n.Id)).OrderBy(n => n.Y).ThenBy(n => n.X).FirstOrDefault();
            if (root is null)
            {
                _cursorNodeId = null;
                _cursorX = _cursorY = 0;
            }
            else
            {
                var tip = FindBranchTip(root);
                (_cursorNodeId, _cursorX, _cursorY) = (tip.Id, tip.X, tip.Y);
            }
        }
    }

    public void FlushPendingSave()
    {
        bool hasPending;
        lock (_saveLock)
        {
            hasPending = _saveCts is not null;
        }

        if (hasPending && _canvasPath is not null)
        {
            Save();
        }
    }

    private void ScheduleBackgroundSave()
    {
        lock (_saveLock)
        {
            _saveCts?.Cancel();
            var cts = new CancellationTokenSource();
            _saveCts = cts;
            var token = cts.Token;

            void RunSave()
            {
                // Only clear _saveCts if it's still *this* run's token — a
                // newer edit could have already replaced it with its own
                // CancellationTokenSource by the time this actually runs
                // (this one having lost the race and been cancelled), and
                // clearing the field in that case would wrongly mark the
                // newer, still-pending save as already handled.
                lock (_saveLock)
                {
                    if (ReferenceEquals(_saveCts, cts))
                    {
                        _saveCts = null;
                    }
                }

                if (token.IsCancellationRequested || _canvasPath is null)
                {
                    return;
                }

                try
                {
                    WriteFile();
                }
                catch (Exception ex)
                {
                    LogService.Log($"CanvasFlowWriter background save failed: {ex.Message}");
                }
            }

            // The actual write is handed off to RunOnWriterThread (see its
            // own doc comment) so it never races a mutation happening
            // concurrently on that same thread — Task.Delay here is purely
            // the debounce timer, not where the write itself runs.
            Task.Delay(150, token).ContinueWith(t =>
            {
                if (t.IsCanceled)
                {
                    return;
                }

                if (RunOnWriterThread is { } runOnWriterThread)
                {
                    runOnWriterThread(RunSave);
                }
                else
                {
                    RunSave();
                }
            }, TaskScheduler.Default);
        }
    }

    private void Save()
    {
        lock (_saveLock)
        {
            _saveCts?.Cancel();
            _saveCts = null;
        }

        WriteFile();
    }

    /// <summary>Writes the loaded document in its file's format: a .docuclick diagram or an .html Ablauf.</summary>
    private void WriteFile()
    {
        var path = _canvasPath!;
        var text = ObsidianVault.IsDiagramFile(path) ? BuildDiagramText() : BuildLiveHtml();
        FileSaveRetry.Save(path, () => File.WriteAllText(path, text));
        RememberDiskState(text);
    }

    // What this writer last wrote to (or read from) a .docuclick file, to
    // notice when the Obsidian plugin (or sync) changed it in between.
    private string? _diskText;
    private (DateTime WriteTime, long Length) _diskStamp;

    private void RememberDiskState(string? text)
    {
        if (_canvasPath is null || !ObsidianVault.IsDiagramFile(_canvasPath))
        {
            _diskText = null;
            return;
        }

        _diskText = text;
        _diskStamp = ReadStamp(_canvasPath);
    }

    private static (DateTime, long) ReadStamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? (info.LastWriteTimeUtc, info.Length) : (default, -1);
    }

    /// <summary>
    /// A .docuclick diagram can be edited in Obsidian while DocuClick has it
    /// loaded (the plugin saves every edit right away). Before the next
    /// change here, adopt the file's current content instead of silently
    /// writing the stale in-memory copy over the plugin's edit. The cursor
    /// stays where it was if that node still exists. No-op for .html files
    /// and when nothing changed (cheap: timestamp and length first).
    /// </summary>
    /// <returns>True if the document was reloaded from disk.</returns>
    public bool SyncWithDisk()
    {
        if (_canvasPath is null || _diskText is null || !ObsidianVault.IsDiagramFile(_canvasPath))
        {
            return false;
        }

        var stamp = ReadStamp(_canvasPath);
        if (stamp == _diskStamp || stamp.Item2 < 0)
        {
            return false;
        }

        string text;
        try
        {
            text = File.ReadAllText(_canvasPath);
        }
        catch (IOException ex)
        {
            LogService.Log($"Diagramm konnte nicht neu eingelesen werden: {ex.Message}");
            return false;
        }

        _diskStamp = stamp;
        if (text == _diskText)
        {
            return false;
        }

        CanvasDocument document;
        try
        {
            document = DocuClickDiagramIo.Parse(text, _canvasPath);
        }
        catch (InvalidOperationException ex)
        {
            // Half-written by a sync tool, say: keep working on the loaded
            // copy; the next change here writes a complete file again.
            LogService.Log($"Extern geändertes Diagramm nicht lesbar, behalte geladenen Stand: {ex.Message}");
            return false;
        }

        lock (_saveLock)
        {
            _saveCts?.Cancel();
            _saveCts = null;
        }

        _diskText = text;
        AdoptDocument(document);
        LogService.Log($"Diagramm wurde extern geändert (z. B. in Obsidian) und neu eingelesen: {_canvasPath}");
        return true;
    }

    // Hex colors for the live Ablauf-Übersicht-in-a-browser viewer's own
    // Cytoscape rendering — distinct from DecisionPointColor/PathStartColor
    // above, which are Obsidian Canvas's own small integer color-preset
    // slots ("6"="purple" etc.), meaningless to a plain <input>/CSS color.
    // Kept in sync with DrawIoConverter's own palette so a session looks
    // the same whether viewed live or exported to draw.io.
    private const string HtmlDecisionPointColor = "#6B7280";
    private const string HtmlMainColor = "#2563EB";
    private static readonly string[] HtmlBranchColors =
    {
        "#D97706", "#059669", "#DB2777", "#7C3AED", "#DC2626", "#0891B2"
    };

    /// <summary>
    /// Builds the actual file this session is saved as: a real, interactive
    /// HTML page (see <see cref="HtmlViewerBuilder"/>) with the session's
    /// raw JSON embedded for <see cref="CanvasDocumentIo.Load"/> to read
    /// back on the next Start()/OpenForEditing() — opening the file directly
    /// in a plain browser shows the same diagram the Ablauf-Übersicht does,
    /// no DocuClick or Obsidian needed just to look at it. Screenshots are
    /// embedded as base64 (<see cref="ResolveImageSrc"/>) rather than
    /// referenced by relative path, so the file is fully self-contained and
    /// works when embedded directly in an Obsidian note — the trade-off
    /// (noted here, not yet acted on) is that this runs on every single
    /// click, so a long session with many/large screenshots means every one
    /// of those clicks re-serializes and rewrites *all* of them to disk, not
    /// just the newest one.
    /// </summary>
    private string BuildLiveHtml()
    {
        _doc.SaveToken ??= CanvasDocumentIo.NewSaveToken();
        var dataJson = JsonSerializer.Serialize(_doc, _jsonOptions);
        var (nodeSpecs, edgeSpecs) = BuildSpecs(ResolveImageSrc);
        return HtmlViewerBuilder.BuildPage(_sessionName, nodeSpecs, edgeSpecs, dataJson);
    }

    /// <summary>
    /// The diagram note / .docuclick text (see <see cref="DocuClickDiagramIo"/>):
    /// same rendered steps as the .html Ablauf, but screenshots stay files
    /// referenced by vault path instead of being embedded on every click.
    /// </summary>
    private string BuildDiagramText()
    {
        var (nodeSpecs, edgeSpecs) = BuildSpecs(file => file);
        // A diagram note: compose over the note as it is on disk right now,
        // so text typed in Obsidian a moment ago is kept.
        var current = ObsidianVault.IsNote(_canvasPath) && File.Exists(_canvasPath) ? File.ReadAllText(_canvasPath!) : null;
        return DocuClickDiagramIo.Serialize(_canvasPath!, _doc, nodeSpecs, edgeSpecs, ResolveImageSrc, current);
    }

    /// <summary>Rendered nodes/edges (colors, shapes, labels) shared by both file formats; <paramref name="image"/> maps a file node's value to the node's image.</summary>
    private (List<HtmlViewerBuilder.NodeSpec> Nodes, List<HtmlViewerBuilder.EdgeSpec> Edges) BuildSpecs(Func<string, string?> image)
    {

        // Reuses GetPreview()'s own PathId tagging for column-based accent
        // colors, matching DrawIoConverter's palette —
        // this is purely a coloring lookup, the actual rendered *position*
        // of every node still comes from this document's own X/Y below
        // (already meaningful/clean via Relayout()), not recomputed here.
        var preview = GetPreview();
        var slotOf = FlowPreviewBranching.ComputeGridLayout(preview);
        var textNodesById = _doc.Nodes.Where(n => n.Type == "text").ToDictionary(n => n.Id);
        var previewNodesById = preview.Nodes.ToDictionary(n => n.Id);
        var (imageIndex, _) = BuildSiblingIndex();

        string AccentFor(int column) => column == 0 ? HtmlMainColor : HtmlBranchColors[FlowPreviewBranching.StableColumnHash(column) % HtmlBranchColors.Length];

        var nodeSpecs = new List<HtmlViewerBuilder.NodeSpec>();
        foreach (var previewNode in preview.Nodes)
        {
            var canvasNode = textNodesById[previewNode.Id];
            var (_, column) = slotOf[previewNode.Id];
            var accent = AccentFor(column);

            string color;
            string shape = "round-rectangle";
            string label;
            string? imageSrc = null;

            if (previewNode.IsDecisionPoint)
            {
                color = HtmlDecisionPointColor;
                shape = "diamond";
                label = "◆ Abzweigung";
            }
            else if (previewNode.IsPathStart)
            {
                color = accent;
                label = canvasNode.Text ?? "";
            }
            else
            {
                color = !string.IsNullOrEmpty(canvasNode.Color) ? canvasNode.Color : accent;
                shape = !string.IsNullOrEmpty(canvasNode.Shape) ? canvasNode.Shape : "round-rectangle";
                label = BuildLabel(canvasNode.Text);
                if (FindImageSibling(canvasNode, imageIndex)?.File is { } relativeToOutput)
                {
                    imageSrc = image(relativeToOutput);
                }
            }

            nodeSpecs.Add(new HtmlViewerBuilder.NodeSpec(previewNode.Id, label, canvasNode.X, canvasNode.Y, color, shape, imageSrc));
        }

        var edgeSpecs = new List<HtmlViewerBuilder.EdgeSpec>();
        var canvasEdgesByFromTo = _doc.Edges
            .GroupBy(e => $"{e.FromNode}->{e.ToNode}")
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var edge in preview.Edges)
        {
            var key = $"{edge.FromId}->{edge.ToId}";
            canvasEdgesByFromTo.TryGetValue(key, out var canvasEdge);
            var customColor = canvasEdge?.Color;
            var lineStyle = !string.IsNullOrEmpty(canvasEdge?.LineStyle) ? canvasEdge.LineStyle : "solid";

            if (edge.Manual)
            {
                var manualColor = !string.IsNullOrEmpty(customColor) ? customColor : HtmlMainColor;
                edgeSpecs.Add(new HtmlViewerBuilder.EdgeSpec(edge.FromId, edge.ToId, manualColor, Manual: true, LineStyle: lineStyle));
                continue;
            }

            var targetIsDecisionPoint = previewNodesById[edge.ToId].IsDecisionPoint;
            var (_, targetColumn) = slotOf[edge.ToId];
            var defaultColor = targetIsDecisionPoint ? HtmlDecisionPointColor : AccentFor(targetColumn);
            var edgeColor = !string.IsNullOrEmpty(customColor) ? customColor : defaultColor;
            edgeSpecs.Add(new HtmlViewerBuilder.EdgeSpec(edge.FromId, edge.ToId, edgeColor, Manual: false, LineStyle: lineStyle));
        }

        return (nodeSpecs, edgeSpecs);
    }

    /// <summary>
    /// Resolves a screenshot into a 100% self-contained data:image/png;base64 URL,
    /// using the in-memory cache for instant zero-I/O performance. This guarantees that
    /// the generated .html file can be embedded in Obsidian notes (iframes, local-html-embed,
    /// embed-html) without broken relative image paths.
    /// </summary>
    private string? ResolveImageSrc(string relativeToOutput)
    {
        if (string.IsNullOrWhiteSpace(relativeToOutput))
        {
            return null;
        }

        if (relativeToOutput.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return relativeToOutput;
        }

        if (_base64Cache.TryGetValue(relativeToOutput, out var cachedDataUri))
        {
            return cachedDataUri;
        }

        var sessionDir = Path.GetDirectoryName(_canvasPath!) ?? "";
        var fullImagePath = Path.Combine(sessionDir, relativeToOutput);
        if (File.Exists(fullImagePath))
        {
            try
            {
                var bytes = File.ReadAllBytes(fullImagePath);
                var dataUri = "data:image/png;base64," + Convert.ToBase64String(bytes);
                _base64Cache[relativeToOutput] = dataUri;
                return dataUri;
            }
            catch
            {
                // Fallback to relative URL if file read fails
            }
        }

        return ToRelativeUrl(sessionDir, fullImagePath);
    }

    /// <summary>
    /// A relative filesystem path, safe to use as a URL — session/folder
    /// names routinely contain spaces or parentheses (e.g. "IT-Support
    /// 2026-08-04 (1)"), which a raw path breaks as soon as something tries
    /// to actually resolve it as a URL (confirmed as a real bug: an
    /// unescaped space in the path silently failed to load the image at
    /// all in a real browser, leaving only the plain background color
    /// visible). Each path segment is escaped separately, not the whole
    /// string, so the "/" separators themselves stay intact.
    /// </summary>
    private static string ToRelativeUrl(string baseDir, string fullPath) =>
        string.Join("/", Path.GetRelativePath(baseDir, fullPath).Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
}
