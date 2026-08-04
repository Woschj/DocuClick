using System.Drawing;

namespace DocuClick.Services;

/// <summary>
/// Common contract for the branching output modes (Obsidian Canvas, Word)
/// so SessionManager doesn't need to know which one is active.
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
