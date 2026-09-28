
namespace DocuClick.Services;

/// <summary>
/// One node in a flow, for the tree-preview overlay's minimap rendering.
/// <see cref="PathId"/>/<see cref="PathName"/> are filled in after the fact
/// by <see cref="FlowPreviewBranching.TagBranches"/>, which propagates them
/// forward from whichever <see cref="IsPathStart"/> node reaches a given
/// node first — not by <see cref="CanvasFlowWriter"/> itself.
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

/// <summary>Full snapshot of a flow's nodes and connectors, as returned by <see cref="CanvasFlowWriter.GetPreview"/>.</summary>
public sealed record FlowPreview(List<PreviewNode> Nodes, List<PreviewEdge> Edges);

/// <summary>
/// Shared post-processing for <see cref="CanvasFlowWriter.GetPreview"/>:
/// propagates each path-start node's identity (its own id,
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
    /// Ablauf-Übersicht viewer, <c>DrawIoConverter</c>) instead of two
    /// separately-maintained copies of the same one-line hash.
    /// </summary>
    public static int StableColumnHash(int column) => unchecked((int)((uint)column * 2654435761u) & 0x7FFFFFFF);
}

/// <summary>Result of a branch-related action, for user-facing feedback.</summary>
public readonly record struct BranchActionResult(bool Success);

/// <summary>One path forking from a decision point — for the Ablauf-Übersicht's "bestehenden Pfad fortsetzen" popup.</summary>
public readonly record struct PathInfo(string PathStartNodeId, string Name, int StepCount);
