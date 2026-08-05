using System.Drawing;

namespace DocuClick.Services;

/// <summary>One node in a flow, for the tree-preview overlay's minimap rendering.</summary>
public sealed record PreviewNode(string Id, string Label, double X, double Y, double Width, double Height, bool IsCurrent, bool IsBranchMarker);

/// <summary>One connector line between two nodes, for the tree-preview overlay.</summary>
public sealed record PreviewEdge(string FromId, string ToId);

/// <summary>Full snapshot of a flow's nodes and connectors, as returned by <see cref="IFlowWriter.GetPreview"/>.</summary>
public sealed record FlowPreview(List<PreviewNode> Nodes, List<PreviewEdge> Edges);

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
