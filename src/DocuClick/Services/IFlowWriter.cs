using System.Drawing;

namespace DocuClick.Services;

/// <summary>One node in a flow, for the tree-preview overlay's minimap rendering. <see cref="BranchName"/> is filled in after the fact by <see cref="FlowPreviewBranching.TagBranches"/>, not by the individual writers.</summary>
public sealed record PreviewNode(string Id, string Label, double X, double Y, double Width, double Height, bool IsCurrent, bool IsBranchMarker, string? BranchName = null);

/// <summary>One connector line between two nodes, for the tree-preview overlay.</summary>
public sealed record PreviewEdge(string FromId, string ToId);

/// <summary>Full snapshot of a flow's nodes and connectors, as returned by <see cref="IFlowWriter.GetPreview"/>.</summary>
public sealed record FlowPreview(List<PreviewNode> Nodes, List<PreviewEdge> Edges);

/// <summary>
/// Shared post-processing for every <see cref="IFlowWriter.GetPreview"/>
/// implementation: tags every node reachable (via edges, forward only)
/// from a branch-marker node with that branch's name, so the tree-preview
/// overlay's minimap can give each branch its own color instead of only
/// distinguishing "is a marker" from "isn't". A node reachable from more
/// than one branch keeps whichever branch reaches it first (markers are
/// visited in the order they appear in <see cref="FlowPreview.Nodes"/>,
/// which every writer already produces in position order).
/// </summary>
public static class FlowPreviewBranching
{
    // Must stay byte-for-byte identical to every writer's own copy of this
    // same constant (CanvasFlowWriter/DrawIoFlowWriter/ExcalidrawFlowWriter)
    // — this one strips it back off whichever writer's marker label is
    // being parsed here, so a mismatch would silently break branch-name
    // extraction (and therefore per-branch minimap coloring) for that
    // writer's output.
    private const string BranchMarkerPrefix = "◆ Branch: ";

    public static FlowPreview TagBranches(FlowPreview preview)
    {
        var forward = preview.Edges
            .GroupBy(e => e.FromId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ToId).ToList());
        var markerIds = preview.Nodes.Where(n => n.IsBranchMarker).Select(n => n.Id).ToHashSet();

        var branchNameOf = new Dictionary<string, string>();

        // Pass 1: every marker owns its own name, taken directly from its
        // own label. This must happen before any propagation below —
        // branching off from *within* another branch (a nested branch
        // marker sitting downstream of an earlier one) is a normal thing
        // to do, and the nested marker's own identity must never be
        // clobbered by the outer branch's name reaching it first.
        foreach (var marker in preview.Nodes.Where(n => n.IsBranchMarker))
        {
            var branchName = ExtractBranchName(marker.Label);
            if (branchName is not null)
            {
                branchNameOf[marker.Id] = branchName;
            }
        }

        // Pass 2: propagate each branch's name forward through its
        // descendants, but stop at any *other* marker — that's where a
        // different (possibly nested) branch starts, and its own name
        // from pass 1 already stands there.
        foreach (var marker in preview.Nodes.Where(n => n.IsBranchMarker))
        {
            if (!branchNameOf.TryGetValue(marker.Id, out var branchName))
            {
                continue;
            }

            var queue = new Queue<string>();
            queue.Enqueue(marker.Id);
            var visited = new HashSet<string> { marker.Id };
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

                    if (markerIds.Contains(child) && child != marker.Id)
                    {
                        continue; // boundary: a different branch starts here
                    }

                    branchNameOf.TryAdd(child, branchName);
                    queue.Enqueue(child);
                }
            }
        }

        if (branchNameOf.Count == 0)
        {
            return preview;
        }

        var taggedNodes = preview.Nodes
            .Select(n => branchNameOf.TryGetValue(n.Id, out var name) ? n with { BranchName = name } : n)
            .ToList();

        return new FlowPreview(taggedNodes, preview.Edges);
    }

    private static string? ExtractBranchName(string label)
    {
        if (!label.StartsWith(BranchMarkerPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var name = label[BranchMarkerPrefix.Length..].Trim();
        return name.Length == 0 ? null : name;
    }
}

/// <summary>
/// Common contract for the branching output modes (Obsidian Canvas, draw.io,
/// Excalidraw) so SessionManager doesn't need to know which one is active.
/// The plain note mode (ObsidianWriter) has no branching concept and
/// deliberately does not implement this.
/// </summary>
public interface IFlowWriter
{
    void StartSession(string fileName);
    void Stop();
    void AddClickNode(string description, Bitmap screenshot, DateTime timestamp);

    /// <summary>Bookmarks the current node under a user-chosen name (re-marking an existing name replaces its target).</summary>
    BranchActionResult MarkBranchAnchor(string branchName);

    /// <summary>Moves the cursor to a previously named anchor.</summary>
    BranchActionResult JumpToAnchor(string branchName);

    /// <summary>Snapshot of every node currently in the flow (position + size + which one the cursor is on) for the live tree-preview overlay.</summary>
    FlowPreview GetPreview();

    /// <summary>Moves the cursor to an arbitrary existing node (not just a named branch anchor) — the tree-preview overlay's click-to-navigate.</summary>
    BranchActionResult JumpToNode(string nodeId);

    /// <summary>All currently defined branch names, in the order they were first marked.</summary>
    List<string> ListBranchAnchorNames();

    List<ResumableNode> ListNodesForResume(string fileName);
    void SetResumeAnchor(ResumableNode node);

    /// <summary>How many named branch anchors are currently defined.</summary>
    int BranchDepth { get; }

    /// <summary>Name of the branch the cursor is currently positioned in, or null for the main flow.</summary>
    string? CurrentBranchName { get; }

    string? CurrentNodeLabel { get; }
}
