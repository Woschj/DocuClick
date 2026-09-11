using System.Drawing;

namespace DocuClick.Services;

/// <summary>
/// One node in a flow, for the tree-preview overlay's minimap rendering.
/// <see cref="PathId"/>/<see cref="PathName"/> are filled in after the fact
/// by <see cref="FlowPreviewBranching.TagBranches"/>, which propagates them
/// forward from whichever <see cref="IsPathStart"/> node reaches a given
/// node first — not by the individual writers.
/// </summary>
/// <param name="ImagePath">
/// Output-relative path to this node's screenshot (forward slashes), or null
/// for a marker node — lets the Ablauf-Übersicht and the HTML export
/// actually show the captured image, not just a label.
/// </param>
public sealed record PreviewNode(
    string Id, string Label, double X, double Y, double Width, double Height,
    bool IsCurrent, bool IsDecisionPoint, bool IsPathStart,
    string? PathId = null, string? PathName = null, string? ImagePath = null,
    string? Shape = null, string? Color = null);

/// <summary>One connector line between two nodes, for the tree-preview overlay.</summary>
public sealed record PreviewEdge(string FromId, string ToId, bool Manual = false, string? Color = null, string? LineStyle = "solid");

/// <summary>Full snapshot of a flow's nodes and connectors, as returned by <see cref="IFlowWriter.GetPreview"/>.</summary>
public sealed record FlowPreview(List<PreviewNode> Nodes, List<PreviewEdge> Edges);

/// <summary>
/// Shared post-processing for every <see cref="IFlowWriter.GetPreview"/>
/// implementation: propagates each path-start node's identity (its own id,
/// used as <see cref="PreviewNode.PathId"/>) and display name forward
/// through everything reachable from it, so the tree-preview overlay's
/// minimap can give each path its own color/label instead of only
/// distinguishing "is a path start" from "isn't". Decision points
/// themselves are ordinary pass-through nodes here — they belong to
/// whichever path led into them; only their own path-start *children* mark
/// the start of a new, distinctly colored path.
/// </summary>
public static class FlowPreviewBranching
{
    public static FlowPreview TagBranches(FlowPreview preview)
    {
        // Structural edges only — a manual cross-connect (see
        // CanvasEdge.Manual) is an additive reference, not a real fork this
        // path owns. Propagating a path's identity through one used to let
        // an unrelated node (and its whole real subtree) get relabeled with
        // the wrong path's id/color the moment someone cross-connected into
        // or out of it — the same class of bug already fixed for
        // CanvasFlowWriter's own topology walks (FindBranchTip etc.).
        var forward = preview.Edges
            .Where(e => !e.Manual)
            .GroupBy(e => e.FromId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ToId).ToList());
        var pathStartIds = preview.Nodes.Where(n => n.IsPathStart).Select(n => n.Id).ToHashSet();

        var pathIdOf = new Dictionary<string, string>();
        var pathNameOf = new Dictionary<string, string>();

        // Pass 1: every path-start owns its own identity (its own node id)
        // and display name, set directly by the writer that created it —
        // this must happen before any propagation below, so a nested path
        // starting *within* another path never has its own identity
        // clobbered by the outer path's identity reaching it first.
        foreach (var start in preview.Nodes.Where(n => n.IsPathStart))
        {
            pathIdOf[start.Id] = start.Id;
            if (start.PathName is { } name)
            {
                pathNameOf[start.Id] = name;
            }
        }

        // Pass 2: propagate each path's identity forward through its
        // descendants (decision points included — they're just pass-
        // through here), but stop at any *other* path-start — that's
        // where a different (possibly nested) path begins, and its own
        // identity from pass 1 already stands there.
        foreach (var start in preview.Nodes.Where(n => n.IsPathStart))
        {
            var queue = new Queue<string>();
            queue.Enqueue(start.Id);
            var visited = new HashSet<string> { start.Id };
            while (queue.Count > 0)
            {
                var id = queue.Dequeue();

                if (!forward.TryGetValue(id, out var children))
                {
                    continue;
                }

                foreach (var child in children)
                {
                    if (!visited.Add(child))
                    {
                        continue;
                    }

                    if (pathStartIds.Contains(child) && child != start.Id)
                    {
                        continue; // boundary: a different (nested) path starts here
                    }

                    pathIdOf.TryAdd(child, start.Id);
                    if (pathNameOf.TryGetValue(start.Id, out var name))
                    {
                        pathNameOf.TryAdd(child, name);
                    }

                    queue.Enqueue(child);
                }
            }
        }

        if (pathIdOf.Count == 0)
        {
            return preview;
        }

        var taggedNodes = preview.Nodes
            .Select(n => pathIdOf.TryGetValue(n.Id, out var pathId)
                ? n with { PathId = pathId, PathName = pathNameOf.GetValueOrDefault(pathId) }
                : n)
            .ToList();

        return new FlowPreview(taggedNodes, preview.Edges);
    }

    /// <summary>
    /// Schematic (row, column) grid slot for every node, from graph topology
    /// alone — BFS depth from each structural root is the row; each branch
    /// (grouped by <see cref="PreviewNode.PathId"/>, or by its own component
    /// root for an untagged fragment) gets its own column, placed in the
    /// nearest free column to wherever it actually forks from — never from a
    /// single global left/right sequence unaware of *where* in the diagram a
    /// fork happens. That global sequence used to hand two unrelated
    /// "+ Neuer Pfad ab hier" forks off the very same node one column right
    /// next to their origin and one clear across the whole diagram (whichever
    /// the sequence happened to be on next), producing a connector line that
    /// visually cut straight through an entire unrelated branch — confirmed
    /// as a real complaint ("cards lying on top of each other"), even though
    /// no two node boxes ever actually intersected. Only ever follows
    /// structural edges — a manual cross-connect must never shift where a
    /// node "really" belongs in this schematic. Shared by the Ablauf-
    /// Übersicht's own minimap (<c>FlowPreviewOverlay.BuildPreviewPayload</c>)
    /// and <c>CanvasFlowWriter</c>'s relayout, so both always agree on the
    /// same "clean" arrangement instead of risking two independently-evolving
    /// layout algorithms drifting apart.
    /// </summary>
    public static Dictionary<string, (int Row, int Column)> ComputeGridLayout(FlowPreview preview)
    {
        var result = new Dictionary<string, (int Row, int Column)>();
        if (preview.Nodes.Count == 0)
        {
            return result;
        }

        var forward = preview.Edges
            .Where(e => !e.Manual)
            .GroupBy(e => e.FromId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ToId).ToList());
        var hasInbound = preview.Edges.Where(e => !e.Manual).Select(e => e.ToId).ToHashSet();

        var rowOf = new Dictionary<string, int>();
        var componentRootOf = new Dictionary<string, string>();
        var bfsQueue = new Queue<string>();
        foreach (var node in preview.Nodes.OrderBy(n => n.Y).ThenBy(n => n.X))
        {
            if (!hasInbound.Contains(node.Id))
            {
                rowOf[node.Id] = 0;
                componentRootOf[node.Id] = node.Id;
                bfsQueue.Enqueue(node.Id);
            }
        }

        while (bfsQueue.Count > 0)
        {
            var id = bfsQueue.Dequeue();
            if (!forward.TryGetValue(id, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                if (rowOf.ContainsKey(child))
                {
                    continue;
                }

                rowOf[child] = rowOf[id] + 1;
                componentRootOf[child] = componentRootOf[id];
                bfsQueue.Enqueue(child);
            }
        }

        // Defensive: a stray node the BFS never reached lands at row 0, its
        // own singleton component (shouldn't happen for a tree-shaped flow).
        foreach (var node in preview.Nodes)
        {
            rowOf.TryAdd(node.Id, 0);
            componentRootOf.TryAdd(node.Id, node.Id);
        }

        // Structural parent of every node (a proper tree — at most one) so a
        // brand-new path's column can be anchored to wherever its own
        // origin node ended up, instead of a position-unaware global
        // sequence.
        var parentOf = new Dictionary<string, string>();
        foreach (var (parentId, children) in forward)
        {
            foreach (var child in children)
            {
                parentOf[child] = parentId;
            }
        }

        var columnOf = new Dictionary<string, int>();
        var columnKeyToColumn = new Dictionary<string, int>();
        var usedColumns = new HashSet<int> { 0 }; // column 0 is exclusively the main flow's
        string? mainRootId = null;

        int NearestFreeColumn(int anchor)
        {
            var offset = 1;
            while (true)
            {
                if (usedColumns.Add(anchor + offset))
                {
                    return anchor + offset;
                }

                if (usedColumns.Add(anchor - offset))
                {
                    return anchor - offset;
                }

                offset++;
            }
        }

        // Row order (parents before children) — a fork's anchor column must
        // already be resolved before the fork itself is placed.
        foreach (var node in preview.Nodes.OrderBy(n => rowOf[n.Id]))
        {
            string columnKey;
            bool isMainRoot;
            int anchorColumn;
            if (node.PathId is { } pathId)
            {
                columnKey = "path:" + pathId;
                isMainRoot = false;
                // pathId is the path-start node's own id (see TagBranches) —
                // its structural parent is exactly the node this path forks
                // from, wherever that ended up.
                anchorColumn = parentOf.TryGetValue(pathId, out var originId) && columnOf.TryGetValue(originId, out var originColumn)
                    ? originColumn
                    : 0;
            }
            else
            {
                var root = componentRootOf[node.Id];
                mainRootId ??= root; // first non-path root seen keeps the original column-0 behavior
                isMainRoot = root == mainRootId;
                columnKey = "root:" + root;
                anchorColumn = 0; // an independent secondary root/fragment has no real origin to anchor to
            }

            int column;
            if (isMainRoot)
            {
                column = 0;
            }
            else if (!columnKeyToColumn.TryGetValue(columnKey, out column))
            {
                column = NearestFreeColumn(anchorColumn);
                columnKeyToColumn[columnKey] = column;
            }

            columnOf[node.Id] = column;
            result[node.Id] = (rowOf[node.Id], column);
        }

        return result;
    }

    /// <summary>
    /// Spreads column values (small integers, positive and negative) across
    /// a fixed-size accent-color palette — a raw Math.Abs(column) reduction
    /// would collide column and -column onto the same palette slot, giving
    /// two clearly different branches the same accent color. Shared by
    /// every renderer that colors a node/edge by its column (the live
    /// Ablauf-Übersicht viewer, <c>DrawIoConverter</c>,
    /// <c>HtmlFlowExporter</c>) instead of three separately-maintained
    /// copies of the same one-line hash.
    /// </summary>
    public static int StableColumnHash(int column) => unchecked((int)((uint)column * 2654435761u) & 0x7FFFFFFF);
}

/// <summary>Result of a branch-related action, for user-facing feedback.</summary>
public readonly record struct BranchActionResult(bool Success);

/// <summary>One path forking from a decision point — for the Ablauf-Übersicht's "bestehenden Pfad fortsetzen" popup.</summary>
public readonly record struct PathInfo(string PathStartNodeId, string Name, int StepCount);

/// <summary>
/// Common contract for the branching flow writer, implemented solely by
/// CanvasFlowWriter — there is no other output mode (a separate, non-
/// branching plain-note writer existed once and was removed once the HTML
/// flow format no longer needed Obsidian). draw.io is not a live-recording
/// target either (see DrawIoConverter) — it converts an existing session
/// into a .drawio file in one pass instead of implementing this interface.
///
/// Branching model: <see cref="MarkDecisionPoint"/> turns the current node
/// into a small diamond — a decision point — and immediately forks the
/// first named path from it, jumping the cursor onto that path (there is
/// deliberately no unnamed/implicit "default continuation": every path
/// leaving a decision point is a real, selectable, named node from the
/// moment it exists — otherwise it could never appear in
/// <see cref="ListPaths"/>, making it impossible to ever resume). From a
/// decision point (found by clicking its diamond in the Ablauf-Übersicht),
/// the user picks <see cref="StartNewPath"/> to fork another new named
/// path, or <see cref="ContinuePath"/> to resume one started earlier —
/// mirroring a UML activity diagram's decision nodes and their outgoing
/// flows, screenshots instead of activity labels.
/// </summary>
public interface IFlowWriter
{
    void StartSession(string fileName);
    void Pause();
    void Stop();
    void AddClickNode(string description, Bitmap screenshot, DateTime timestamp);

    /// <summary>Marks the current node as a decision point (a small diamond marker) and immediately forks+jumps onto its first named path — see the type's own doc comment for why there's no unnamed default continuation.</summary>
    BranchActionResult MarkDecisionPoint(string firstPathName);

    /// <summary>Every path already forking directly from a given node (decision point or otherwise), for the Ablauf-Übersicht's per-node popup.</summary>
    List<PathInfo> ListPaths(string originNodeId);

    /// <summary>
    /// Starts a brand-new named path from an existing node, in its own
    /// column, and jumps the cursor onto it. The origin doesn't have to be
    /// a decision point — any node can be the retroactive start of an
    /// alternate branch (see <see cref="JumpToNode"/>'s doc comment for why
    /// this is the only way to branch from a node that already has
    /// downstream content).
    /// </summary>
    BranchActionResult StartNewPath(string originNodeId, string pathName);

    /// <summary>Resumes an existing path at wherever it currently ends (not necessarily where it started).</summary>
    BranchActionResult ContinuePath(string pathStartNodeId);

    /// <summary>Snapshot of every node currently in the flow (position + size + which one the cursor is on) for the live tree-preview overlay.</summary>
    FlowPreview GetPreview();

    /// <summary>
    /// Moves the cursor to an arbitrary existing node — always resolved
    /// forward to that node's branch's current tip and resumed exactly
    /// there (never opens a new column, never risks a second, untracked
    /// outgoing edge from a node that already has one). The Ablauf-
    /// Übersicht's click-to-navigate for a node that's already a tip (no
    /// downstream content); for a node with existing children it instead
    /// shows a popup offering this ("→ Weiter") alongside
    /// <see cref="StartNewPath"/> ("+ Neuer Pfad ab hier").
    /// </summary>
    BranchActionResult JumpToNode(string nodeId);

    List<ResumableNode> ListNodesForResume(string fileName);
    void SetResumeAnchor(ResumableNode node);

    string? CurrentNodeLabel { get; }

    /// <summary>
    /// Renames a node's label (or, for a path-start marker, its path name —
    /// the "↳ Pfad: " prefix is kept). Decision-point diamonds can't be
    /// renamed — their fixed "◆ Abzweigung" text is how every writer
    /// recognizes one as a decision point in the first place.
    /// </summary>
    BranchActionResult RenameNode(string nodeId, string newLabel);

    /// <summary>
    /// Deletes a node. Exactly one outgoing edge: the gap is stitched shut
    /// (the node's own parent connects directly to its former child)
    /// instead of leaving that branch orphaned. More than one outgoing edge
    /// (a decision point, or any node a path was forked from): the whole
    /// downstream subtree is deleted with it — the UI must confirm this
    /// with the user first, since there's no single "the" continuation to
    /// stitch to. If the deleted node (or one of its cascaded descendants)
    /// was the current cursor, the cursor moves to the parent's branch tip
    /// (or null, if the deleted node was a root).
    /// </summary>
    BranchActionResult DeleteNode(string nodeId);

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
    BranchActionResult ConnectNodes(string fromNodeId, string toNodeId);

    /// <summary>
    /// Removes an existing edge between two ordinary content nodes — the
    /// undo counterpart to <see cref="ConnectNodes"/>, for the Ablauf-
    /// Übersicht's right-click-an-edge gesture. Same marker restriction as
    /// <see cref="ConnectNodes"/>: a decision point's/path-start's
    /// structural edges (into it, or its own fork out of a decision point)
    /// can't be removed this way, since that would
    /// silently detach a whole path from <see cref="ListPaths"/> while
    /// leaving its nodes behind, unreachable but not deleted — a confusing
    /// half-state. Deliberately does *not* refuse just because a node would
    /// end up with no remaining edges at all (fully isolated) — the caller
    /// doesn't have to reconnect it to anything else; the Ablauf-Übersicht's
    /// layout places an isolated node in its own row/column rather than
    /// overlapping it onto whatever else happens to sit at the origin.
    /// Refuses (returns failure) if no such edge exists.
    /// </summary>
    BranchActionResult DisconnectNodes(string fromNodeId, string toNodeId);

    /// <summary>
    /// Moves a node (and its screenshot/group siblings, kept aligned by the
    /// same fixed relative-position match every other sibling lookup here
    /// uses) to an explicit new position — the Ablauf-Übersicht's drag-to-
    /// move gesture. Unlike every other mutating action on this interface,
    /// this deliberately does *not* re-run the auto-layout: the whole point
    /// is letting the user override the computed grid arrangement, and
    /// snapping it straight back would defeat the drag that just happened.
    /// That also means it's not a *permanent* override — there's no
    /// "pinned position" concept in the file format, so the next action
    /// that does re-run the auto-layout (a new click, Connect/Disconnect, a
    /// new decision point/path) discards it again.
    /// </summary>
    BranchActionResult MoveNode(string nodeId, double x, double y);

    /// <summary>Batch move for multiple nodes dragged together.</summary>
    BranchActionResult MoveNodes(IReadOnlyList<(string NodeId, double X, double Y)> moves);

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
    BranchActionResult AddManualNode(string label, double x, double y, string? shape = null, string? color = null);

    /// <summary>
    /// Creates a brand-new node with an attached external image at an explicit position.
    /// </summary>
    BranchActionResult AddManualImageNode(string label, string imageSourcePath, double x, double y);

    /// <summary>
    /// Updates the color and line style of an existing edge between two nodes.
    /// </summary>
    BranchActionResult SetEdgeStyle(string fromNodeId, string toNodeId, string? color, string? lineStyle);

    /// <summary>
    /// Reverses the direction of an existing edge between two nodes.
    /// </summary>
    BranchActionResult ReverseEdge(string fromNodeId, string toNodeId);
}
