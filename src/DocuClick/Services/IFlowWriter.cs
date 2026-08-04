using System.Drawing;

namespace DocuClick.Services;

/// <summary>
/// Common contract for the graph-based output modes (Obsidian Canvas,
/// draw.io) so SessionManager doesn't need to know which one is active.
/// The plain note mode (ObsidianWriter) has no branching concept and
/// deliberately does not implement this.
/// </summary>
public interface IFlowWriter
{
    void StartSession(string fileName);
    void Stop();
    void AddClickNode(string description, Bitmap screenshot, DateTime timestamp);
    BranchActionResult MarkBranchAnchor();
    BranchActionResult JumpToLastAnchor();
    List<ResumableNode> ListNodesForResume(string fileName);
    void SetResumeAnchor(ResumableNode node);
    int BranchDepth { get; }
    string? CurrentNodeLabel { get; }
}
