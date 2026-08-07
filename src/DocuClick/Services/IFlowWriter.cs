using System.Drawing;

namespace DocuClick.Services;

/// <summary>
/// One node in a flow, for the tree-preview overlay's minimap rendering.
/// <see cref="PathId"/>/<see cref="PathName"/> are filled in after the fact
/// by <see cref="FlowPreviewBranching.TagBranches"/>, which propagates them
/// forward from whichever <see cref="IsPathStart"/> node reaches a given
/// node first — not by the individual writers.
/// </summary>
public sealed record PreviewNode(
    string Id, string Label, double X, double Y, double Width, double Height,
    bool IsCurrent, bool IsDecisionPoint, bool IsPathStart,
    string? PathId = null, string? PathName = null);

/// <summary>One connector line between two nodes, for the tree-preview overlay.</summary>
public sealed record PreviewEdge(string FromId, string ToId);

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
        var forward = preview.Edges
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
}

/// <summary>Result of a branch-related action, for user-facing feedback.</summary>
public readonly record struct BranchActionResult(bool Success);

/// <summary>One path forking from a decision point — for the Ablauf-Übersicht's "bestehenden Pfad fortsetzen" popup.</summary>
public readonly record struct PathInfo(string PathStartNodeId, string Name, int StepCount);

/// <summary>
/// Common contract for the branching output modes (Obsidian Canvas, draw.io)
/// so SessionManager doesn't need to know which one is active.
/// The plain note mode (ObsidianWriter) has no branching concept and
/// deliberately does not implement this.
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
    /// Re-parents a node: moves its single incoming edge from its current
    /// parent to <paramref name="newParentId"/>, for the Ablauf-Übersicht's
    /// drag-to-rewire. Only ordinary content nodes qualify, on both ends —
    /// a decision point's/path-start's role as a branch hub or a path's own
    /// identity would break if either could be dragged or dropped onto.
    /// Refuses (returns failure) if <paramref name="newParentId"/> is
    /// <paramref name="nodeId"/> itself or one of its own descendants, which
    /// would create a cycle.
    /// </summary>
    BranchActionResult ReparentNode(string nodeId, string newParentId);

    /// <summary>
    /// Manually connects two existing nodes with a new edge — for the
    /// Ablauf-Übersicht's "Verbinden" toolbar gesture, when the recorded
    /// flow itself doesn't already capture some real transition (e.g. a
    /// step that loops back to an earlier one). Additive, unlike
    /// <see cref="ReparentNode"/>: no existing edge is removed, so
    /// <paramref name="toNodeId"/> can end up with more than one incoming
    /// edge — a genuine merge point, not a bug (the Ablauf-Übersicht's
    /// row/column layout just picks whichever parent it reaches <paramref name="toNodeId"/>
    /// from first). Only ordinary content nodes qualify, on both ends —
    /// same reasoning as <see cref="ReparentNode"/>: a decision point's/
    /// path-start's role as a branch hub or a path's own identity would
    /// break if either could be connected into or out of arbitrarily.
    /// Refuses (returns failure) if <paramref name="toNodeId"/> can already
    /// reach <paramref name="fromNodeId"/>, which would create a cycle.
    /// </summary>
    BranchActionResult ConnectNodes(string fromNodeId, string toNodeId);

    /// <summary>
    /// Removes an existing edge between two ordinary content nodes — the
    /// undo counterpart to <see cref="ConnectNodes"/>, for the Ablauf-
    /// Übersicht's right-click-an-edge gesture. Same marker restriction as
    /// <see cref="ConnectNodes"/>/<see cref="ReparentNode"/>: a decision
    /// point's/path-start's structural edges (into it, or its own fork out
    /// of a decision point) can't be removed this way, since that would
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
}
