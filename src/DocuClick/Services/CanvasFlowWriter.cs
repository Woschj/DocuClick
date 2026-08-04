using System.Drawing;
using System.IO;
using System.Text.Json;

namespace DocuClick.Services;

/// <summary>Result of a branch-related action, for user-facing feedback.</summary>
public readonly record struct BranchActionResult(bool Success, int Depth, string? AnchorLabel);

/// <summary>An existing canvas node offered as a resume point for the next session.</summary>
public sealed record ResumableNode(string Id, string Label, double X, double Y);

/// <summary>
/// Writes clicks as connected nodes into an Obsidian .canvas file (plain
/// JSON — no plugin needed) instead of a linear note. Each click becomes a
/// text node embedding the screenshot, linked from the previous node.
///
/// Layout is vertical: the main line runs top-to-bottom in one column.
///
/// Branching: <see cref="MarkBranchAnchor"/> bookmarks the current node on a
/// stack; <see cref="JumpToLastAnchor"/> rewinds the cursor to the top of
/// that stack (without popping it, so the same point can be branched from
/// more than once) and starts a new column to the right so the new branch
/// doesn't overlap the existing flow.
/// </summary>
public sealed class CanvasFlowWriter : IFlowWriter
{
    private const double NodeWidth = 380;
    private const double NodeHeight = 340;
    private const double SequentialSpacing = 60; // gap between consecutive nodes along the main (vertical) flow
    private const double BranchColumnSpacing = 80; // gap between branch columns

    private readonly AppConfig _config;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private string? _canvasPath;
    private CanvasDocument _doc = new();
    private string? _cursorNodeId;
    private double _cursorX;
    private double _cursorY;
    private double _nextColumnX;
    private readonly Stack<(string NodeId, double X, double Y)> _branchAnchors = new();
    private (string NodeId, double X, double Y)? _pendingResumeAnchor;

    public CanvasFlowWriter(AppConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Lists every node currently in <paramref name="canvasFileName"/> so the
    /// UI can offer one as a resume point for the next session — without
    /// starting a session or touching any writer state.
    /// </summary>
    public List<ResumableNode> ListNodesForResume(string canvasFileName)
    {
        if (string.IsNullOrWhiteSpace(_config.VaultPath))
        {
            return new List<ResumableNode>();
        }

        var path = Path.Combine(_config.VaultPath, canvasFileName);
        var doc = LoadOrCreate(path);

        return doc.Nodes
            .OrderBy(n => n.Y).ThenBy(n => n.X)
            .Select(n => new ResumableNode(n.Id, BuildLabel(n.Text), n.X, n.Y))
            .ToList();
    }

    /// <summary>
    /// Queues an existing node as the starting point for the *next* call to
    /// <see cref="StartSession"/> — new clicks connect from it instead of
    /// starting a disconnected subgraph. Consumed once.
    /// </summary>
    public void SetResumeAnchor(ResumableNode node) => _pendingResumeAnchor = (node.Id, node.X, node.Y);

    public void ClearResumeAnchor() => _pendingResumeAnchor = null;

    public void StartSession(string canvasFileName)
    {
        if (string.IsNullOrWhiteSpace(_config.VaultPath))
        {
            throw new InvalidOperationException("Kein Obsidian-Vault-Pfad konfiguriert.");
        }

        _canvasPath = Path.Combine(_config.VaultPath, canvasFileName);
        _doc = LoadOrCreate(_canvasPath);
        _nextColumnX = _doc.Nodes.Count > 0 ? _doc.Nodes.Max(n => n.X) + NodeWidth + BranchColumnSpacing : 0;
        _branchAnchors.Clear();

        if (_pendingResumeAnchor is { } resume && _doc.Nodes.Any(n => n.Id == resume.NodeId))
        {
            // Continue from a previously recorded node as a new branch
            // column, so it never collides with whatever is already below it.
            _cursorNodeId = resume.NodeId;
            _cursorX = _nextColumnX;
            _cursorY = resume.Y;
        }
        else
        {
            // Start a fresh, disconnected subgraph to the right of whatever
            // is already in the file so re-opened/append sessions never
            // overlap old content.
            _cursorNodeId = null;
            _cursorX = _nextColumnX;
            _cursorY = 0;
        }

        _pendingResumeAnchor = null;
    }

    public void Stop()
    {
        _cursorNodeId = null;
        _branchAnchors.Clear();
    }

    public int BranchDepth => _branchAnchors.Count;

    /// <summary>Short preview of the node the next click would connect from, if any.</summary>
    public string? CurrentNodeLabel => _cursorNodeId is null ? null : GetNodeLabel(_cursorNodeId);

    public void AddClickNode(string description, Bitmap screenshot, DateTime timestamp)
    {
        if (_canvasPath is null)
        {
            throw new InvalidOperationException("Canvas-Session wurde nicht gestartet.");
        }

        var imageFileName = AttachmentSaver.SaveScreenshot(_config, screenshot, timestamp);

        var newY = _cursorNodeId is null ? _cursorY : _cursorY + NodeHeight + SequentialSpacing;
        var node = new CanvasNode
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = "text",
            Text = $"{description}\n\n![[{imageFileName}]]",
            X = _cursorX,
            Y = newY,
            Width = NodeWidth,
            Height = NodeHeight
        };
        _doc.Nodes.Add(node);

        if (_cursorNodeId is not null)
        {
            _doc.Edges.Add(new CanvasEdge
            {
                Id = Guid.NewGuid().ToString("N"),
                FromNode = _cursorNodeId,
                ToNode = node.Id
            });
        }

        _cursorNodeId = node.Id;
        _cursorY = newY;

        Save();
    }

    /// <summary>Bookmarks the current node as a branch point.</summary>
    public BranchActionResult MarkBranchAnchor()
    {
        if (_cursorNodeId is null)
        {
            return new BranchActionResult(false, _branchAnchors.Count, null);
        }

        _branchAnchors.Push((_cursorNodeId, _cursorX, _cursorY));

        var node = _doc.Nodes.FirstOrDefault(n => n.Id == _cursorNodeId);
        if (node is not null)
        {
            node.Color = "6"; // Obsidian canvas preset color slot, just to stand out visually
            Save();
        }

        return new BranchActionResult(true, _branchAnchors.Count, GetNodeLabel(_cursorNodeId));
    }

    /// <summary>Rewinds the cursor to the top of the branch-anchor stack (without popping it).</summary>
    public BranchActionResult JumpToLastAnchor()
    {
        if (_branchAnchors.Count == 0)
        {
            return new BranchActionResult(false, 0, null);
        }

        var anchor = _branchAnchors.Peek();
        _nextColumnX += NodeWidth + BranchColumnSpacing;
        _cursorNodeId = anchor.NodeId;
        _cursorX = _nextColumnX;
        _cursorY = anchor.Y;

        return new BranchActionResult(true, _branchAnchors.Count, GetNodeLabel(anchor.NodeId));
    }

    private string? GetNodeLabel(string nodeId) =>
        BuildLabel(_doc.Nodes.FirstOrDefault(n => n.Id == nodeId)?.Text);

    private static string BuildLabel(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "(ohne Beschreibung)";
        }

        var firstLine = text.Split('\n', 2)[0];
        return firstLine.Length > 70 ? firstLine[..70] + "…" : firstLine;
    }

    private CanvasDocument LoadOrCreate(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var doc = JsonSerializer.Deserialize<CanvasDocument>(json);
                if (doc is not null)
                {
                    return doc;
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Log($"Canvas-Datei konnte nicht gelesen werden, beginne neu: {ex.Message}");
        }

        return new CanvasDocument();
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_doc, _jsonOptions);
        File.WriteAllText(_canvasPath!, json);
    }
}
