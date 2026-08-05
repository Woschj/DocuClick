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
/// text node (the description) with a sibling "file" node (the screenshot,
/// Canvas's native embed type) directly beneath it; the text nodes form the
/// linked spine, linked from the previous click's text node.
///
/// Layout is vertical: the main line runs top-to-bottom in one column.
///
/// Branching: <see cref="MarkBranchAnchor"/> adds a small, visible marker
/// node ("Branch: &lt;name&gt;") connected from the current node — an
/// explicit waypoint object in the canvas itself, not hidden metadata.
/// <see cref="JumpToAnchor"/> moves the cursor to that marker (can be
/// re-visited any number of times) and starts a new column so the new
/// branch doesn't overlap the existing flow. Because the marker is a real,
/// recognizable node in the file, <see cref="StartSession"/> rebuilds the
/// branch list by scanning for it — branches survive a Stop()/Start()
/// cycle instead of only living in memory for one run.
/// </summary>
public sealed class CanvasFlowWriter : IFlowWriter
{
    private const double NodeWidth = 380;
    private const double NodeHeight = 340;
    private const double TextNodeHeight = 60;
    private const double TextToImageGap = 10;
    private const double ImageNodeHeight = NodeHeight - TextNodeHeight - TextToImageGap;
    private const double MarkerHeight = 60;
    private const double SequentialSpacing = 60; // gap between consecutive nodes along the main (vertical) flow
    private const double BranchColumnSpacing = 80; // gap between branch columns
    private const string BranchMarkerPrefix = "Branch: ";
    private const string BranchMarkerColor = "6"; // Obsidian canvas preset color slot ("purple"), just to stand out

    private sealed record BranchAnchor(string Name, string NodeId, double X, double Y);

    private readonly AppConfig _config;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private string? _canvasPath;
    private string _sessionName = "Session";
    private CanvasDocument _doc = new();
    private string? _cursorNodeId;
    private double _cursorX;
    private double _cursorY;
    private double _nextColumnX;
    private string? _currentBranchName;
    private readonly List<BranchAnchor> _branchAnchors = new();
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

        // Only offer the description/text nodes as resume points — not
        // their sibling image nodes (no Text to show) or branch markers.
        return doc.Nodes
            .Where(n => n.Type == "text" && !(n.Text?.StartsWith(BranchMarkerPrefix, StringComparison.Ordinal) ?? false))
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
        _sessionName = Path.GetFileNameWithoutExtension(canvasFileName);
        _doc = LoadOrCreate(_canvasPath);
        _nextColumnX = _doc.Nodes.Count > 0 ? _doc.Nodes.Max(n => n.X) + NodeWidth + BranchColumnSpacing : 0;
        _currentBranchName = null;

        // Rebuild branch anchors by scanning for their marker nodes (see
        // MarkBranchAnchor) instead of relying on in-memory state, so a
        // Stop()/Start() cycle on the same file doesn't lose them.
        _branchAnchors.Clear();
        foreach (var n in _doc.Nodes
            .Where(n => n.Text is not null && n.Text.StartsWith(BranchMarkerPrefix, StringComparison.Ordinal))
            .OrderBy(n => n.Y).ThenBy(n => n.X))
        {
            var branchName = n.Text![BranchMarkerPrefix.Length..].Trim();
            if (branchName.Length == 0)
            {
                continue;
            }

            AddOrReplaceAnchor(new BranchAnchor(branchName, n.Id, n.X, n.Y));
        }

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
        _currentBranchName = null;
    }

    public int BranchDepth => _branchAnchors.Count;

    public string? CurrentBranchName => _currentBranchName;

    /// <summary>Short preview of the node the next click would connect from, if any.</summary>
    public string? CurrentNodeLabel => _cursorNodeId is null ? null : GetNodeLabel(_cursorNodeId);

    public void AddClickNode(string description, Bitmap screenshot, DateTime timestamp)
    {
        if (_canvasPath is null)
        {
            throw new InvalidOperationException("Canvas-Session wurde nicht gestartet.");
        }

        // Screenshots land in Attachments/<session>/ instead of flat in
        // Attachments/.
        var imageRelativeToAttachments = AttachmentSaver.SaveScreenshot(_config, screenshot, timestamp, _sessionName);
        var imageVaultPath = Path.Combine(_config.AttachmentsFolder, imageRelativeToAttachments).Replace('\\', '/');

        var newY = _cursorNodeId is null ? _cursorY : _cursorY + NodeHeight + SequentialSpacing;

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
            File = imageVaultPath,
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
    /// Adds a small, visible "Branch: &lt;name&gt;" marker node connected
    /// from the current node — an explicit waypoint object rather than a
    /// hidden field, so it shows up in the canvas itself and survives a
    /// Stop()/Start() cycle (see StartSession). Doesn't move the cursor;
    /// the ongoing flow keeps recording from where it was — only
    /// <see cref="JumpToAnchor"/> actually jumps to a marker.
    /// Re-marking an existing name adds a fresh marker (the newest one
    /// wins on the next reload, same as in-memory re-marking).
    /// </summary>
    public BranchActionResult MarkBranchAnchor(string branchName)
    {
        if (_cursorNodeId is null)
        {
            return new BranchActionResult(false, _branchAnchors.Count, null);
        }

        var markerY = _cursorY + NodeHeight + SequentialSpacing;
        var marker = new CanvasNode
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = "text",
            Text = $"{BranchMarkerPrefix}{branchName}",
            X = _cursorX,
            Y = markerY,
            Width = NodeWidth,
            Height = MarkerHeight,
            Color = BranchMarkerColor
        };
        _doc.Nodes.Add(marker);
        _doc.Edges.Add(new CanvasEdge
        {
            Id = Guid.NewGuid().ToString("N"),
            FromNode = _cursorNodeId,
            ToNode = marker.Id
        });

        AddOrReplaceAnchor(new BranchAnchor(branchName, marker.Id, marker.X, marker.Y));
        Save();

        return new BranchActionResult(true, _branchAnchors.Count, branchName);
    }

    public List<string> ListBranchAnchorNames() => _branchAnchors.Select(a => a.Name).ToList();

    /// <summary>Moves the cursor to the named anchor and opens a new column so the branch doesn't overlap the existing flow.</summary>
    public BranchActionResult JumpToAnchor(string branchName)
    {
        var anchor = _branchAnchors.FirstOrDefault(a => a.Name == branchName);
        if (anchor is null)
        {
            return new BranchActionResult(false, _branchAnchors.Count, null);
        }

        _nextColumnX += NodeWidth + BranchColumnSpacing;
        _cursorNodeId = anchor.NodeId;
        _cursorX = _nextColumnX;
        _cursorY = anchor.Y;
        _currentBranchName = branchName;

        return new BranchActionResult(true, _branchAnchors.Count, branchName);
    }

    public FlowPreview GetPreview()
    {
        var nodes = _doc.Nodes
            .Where(n => n.Type == "text")
            .Select(n => new PreviewNode(
                n.Id,
                BuildLabel(n.Text),
                n.X, n.Y, n.Width, n.Height,
                n.Id == _cursorNodeId,
                n.Text?.StartsWith(BranchMarkerPrefix, StringComparison.Ordinal) ?? false))
            .ToList();
        var edges = _doc.Edges.Select(e => new PreviewEdge(e.FromNode, e.ToNode)).ToList();
        return FlowPreviewBranching.TagBranches(new FlowPreview(nodes, edges));
    }

    /// <summary>Jumps the cursor to an arbitrary existing text/marker node, opening a new column so the new content doesn't overlap the existing flow — same mechanics as <see cref="JumpToAnchor"/>, just not limited to named branch markers.</summary>
    public BranchActionResult JumpToNode(string nodeId)
    {
        var node = _doc.Nodes.FirstOrDefault(n => n.Id == nodeId && n.Type == "text");
        if (node is null)
        {
            return new BranchActionResult(false, _branchAnchors.Count, null);
        }

        _nextColumnX += NodeWidth + BranchColumnSpacing;
        _cursorNodeId = node.Id;
        _cursorX = _nextColumnX;
        _cursorY = node.Y;
        _currentBranchName = _branchAnchors.FirstOrDefault(a => a.NodeId == node.Id)?.Name;

        return new BranchActionResult(true, _branchAnchors.Count, _currentBranchName);
    }

    private void AddOrReplaceAnchor(BranchAnchor anchor)
    {
        var existingIndex = _branchAnchors.FindIndex(a => a.Name == anchor.Name);
        if (existingIndex >= 0)
        {
            _branchAnchors[existingIndex] = anchor;
        }
        else
        {
            _branchAnchors.Add(anchor);
        }
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
