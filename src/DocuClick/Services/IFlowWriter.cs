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
/// Common contract for the branching output modes (Obsidian Canvas, draw.io,
/// Excalidraw) so SessionManager doesn't need to know which one is active.
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

    /// <summary>Every path already forking from a given decision point, for the Ablauf-Übersicht's "bestehenden Pfad fortsetzen" popup.</summary>
    List<PathInfo> ListPaths(string decisionPointId);

    /// <summary>Starts a brand-new named path from an existing decision point, in its own column, and jumps the cursor onto it.</summary>
    BranchActionResult StartNewPath(string decisionPointId, string pathName);

    /// <summary>Resumes an existing path at wherever it currently ends (not necessarily where it started).</summary>
    BranchActionResult ContinuePath(string pathStartNodeId);

    /// <summary>Snapshot of every node currently in the flow (position + size + which one the cursor is on) for the live tree-preview overlay.</summary>
    FlowPreview GetPreview();

    /// <summary>Moves the cursor to an arbitrary existing node (not a decision point/path action) — the tree-preview overlay's click-to-navigate for regular nodes.</summary>
    BranchActionResult JumpToNode(string nodeId);

    List<ResumableNode> ListNodesForResume(string fileName);
    void SetResumeAnchor(ResumableNode node);

    string? CurrentNodeLabel { get; }
}
