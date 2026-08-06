using System.Drawing;
using System.IO;
using System.Text.Json;

namespace DocuClick.Services;

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
/// Branching (see <see cref="IFlowWriter"/> for the full model):
/// <see cref="MarkDecisionPoint"/> adds a small "◆ Abzweigung" diamond
/// connected from the current node and moves the cursor onto it inline —
/// clicking normally afterward just continues straight through it. From
/// there, <see cref="StartNewPath"/> forks a new "↳ Pfad: &lt;name&gt;"
/// column, or <see cref="ContinuePath"/> resumes one started earlier.
/// Nothing about a path/decision point is cached in memory — every lookup
/// walks the actual node/edge graph, so a Stop()/Start() cycle can never
/// forget or desync from what's really in the file.
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
    private const double BranchColumnSpacing = 80; // gap between path columns

    // Leading icons give both kinds of marker a distinct at-a-glance look
    // in Obsidian Canvas, which — unlike draw.io's rhombus shape — has no
    // concept of node shapes at all, only plain text/file/link/group nodes.
    private const string DecisionPointLabel = "◆ Abzweigung";
    private const string PathStartPrefix = "↳ Pfad: ";
    private const string DecisionPointColor = "6"; // Obsidian canvas preset color slot ("purple")
    private const string PathStartColor = "4"; // preset "green" — visually distinct from the decision point itself

    private readonly AppConfig _config;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private string? _canvasPath;
    private string _sessionName = "Session";
    private CanvasDocument _doc = new();
    private string? _cursorNodeId;
    private double _cursorX;
    private double _cursorY;
    private double _nextColumnX;
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

        // Only offer real content nodes as resume points — not their
        // sibling image nodes (no Text to show), decision points, or path
        // starts (neither has a screenshot to resume "at").
        return doc.Nodes
            .Where(n => n.Type == "text" && !IsDecisionPointNode(n) && !IsPathStartNode(n))
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

        if (_pendingResumeAnchor is { } resume && _doc.Nodes.Any(n => n.Id == resume.NodeId))
        {
            // Continue from a previously recorded node as a new column, so
            // it never collides with whatever is already below it.
            _cursorNodeId = resume.NodeId;
            _cursorX = _nextColumnX;
            _cursorY = resume.Y;
        }
        else
        {
            // No explicit resume point chosen ("Bestehende Datei
            // fortsetzen" without picking a node): still resume the main
            // flow's actual current tip rather than leaving the cursor
            // null. A null cursor meant every node-relative action
            // (MarkDecisionPoint included) failed with "kein Klick
            // vorhanden" until a throwaway click created *some* node first
            // — confusing right after deliberately resuming a file that
            // already has content. Still placed in a fresh column so it
            // never visually collides with whatever's already in the file.
            var targetIds = _doc.Edges.Select(e => e.ToNode).ToHashSet();
            var root = _doc.Nodes
                .Where(n => n.Type == "text" && !targetIds.Contains(n.Id))
                .OrderBy(n => n.Y).ThenBy(n => n.X)
                .FirstOrDefault();

            if (root is not null)
            {
                var tip = FindBranchTip(root);
                _cursorNodeId = tip.Id;
                _cursorX = _nextColumnX;
                _cursorY = tip.Y;
            }
            else
            {
                // Truly empty file — nothing yet to attach to.
                _cursorNodeId = null;
                _cursorX = _nextColumnX;
                _cursorY = 0;
            }
        }

        _pendingResumeAnchor = null;
    }

    public void Stop() => _cursorNodeId = null;

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
    /// Adds a small "◆ Abzweigung" diamond connected from the current node
    /// — an explicit, visible waypoint rather than hidden state — and moves
    /// the cursor onto it inline, so the next regular click still just
    /// continues straight through it in the same column. Forking an actual
    /// new path only happens via <see cref="StartNewPath"/>, chosen later
    /// by clicking this diamond in the Ablauf-Übersicht; nothing here asks
    /// for a name upfront, since a decision point can end up with any
    /// number of differently-named paths over time.
    /// </summary>
    public BranchActionResult MarkDecisionPoint()
    {
        if (_cursorNodeId is null)
        {
            return new BranchActionResult(false);
        }

        var markerY = _cursorY + NodeHeight + SequentialSpacing;
        var marker = new CanvasNode
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = "text",
            Text = DecisionPointLabel,
            X = _cursorX,
            Y = markerY,
            Width = NodeWidth,
            Height = MarkerHeight,
            Color = DecisionPointColor
        };
        _doc.Nodes.Add(marker);
        _doc.Edges.Add(new CanvasEdge
        {
            Id = Guid.NewGuid().ToString("N"),
            FromNode = _cursorNodeId,
            ToNode = marker.Id
        });

        _cursorNodeId = marker.Id;
        _cursorY = markerY;

        Save();
        return new BranchActionResult(true);
    }

    /// <summary>Every path already forking from <paramref name="decisionPointId"/>, resolved fresh from the graph (never cached) — see <see cref="ListPaths"/> on <see cref="IFlowWriter"/>.</summary>
    public List<PathInfo> ListPaths(string decisionPointId)
    {
        var childIds = _doc.Edges.Where(e => e.FromNode == decisionPointId).Select(e => e.ToNode).ToHashSet();
        return _doc.Nodes
            .Where(n => childIds.Contains(n.Id) && IsPathStartNode(n))
            .Select(n => new PathInfo(n.Id, ExtractPathName(n), FindBranchTip(n).Steps))
            .ToList();
    }

    /// <summary>Forks a brand-new named path from an existing decision point into its own column, and jumps the cursor onto it.</summary>
    public BranchActionResult StartNewPath(string decisionPointId, string pathName)
    {
        var decisionNode = _doc.Nodes.FirstOrDefault(n => n.Id == decisionPointId && IsDecisionPointNode(n));
        if (decisionNode is null)
        {
            return new BranchActionResult(false);
        }

        _nextColumnX += NodeWidth + BranchColumnSpacing;
        var pathStart = new CanvasNode
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = "text",
            Text = $"{PathStartPrefix}{pathName}",
            X = _nextColumnX,
            Y = decisionNode.Y,
            Width = NodeWidth,
            Height = MarkerHeight,
            Color = PathStartColor
        };
        _doc.Nodes.Add(pathStart);
        _doc.Edges.Add(new CanvasEdge
        {
            Id = Guid.NewGuid().ToString("N"),
            FromNode = decisionNode.Id,
            ToNode = pathStart.Id
        });

        _cursorNodeId = pathStart.Id;
        _cursorX = pathStart.X;
        _cursorY = pathStart.Y;

        Save();
        return new BranchActionResult(true);
    }

    /// <summary>Resumes an existing path at wherever it currently ends (walked fresh from the graph — see <see cref="FindBranchTip"/>), in its own already-established column.</summary>
    public BranchActionResult ContinuePath(string pathStartNodeId)
    {
        var pathStart = _doc.Nodes.FirstOrDefault(n => n.Id == pathStartNodeId && IsPathStartNode(n));
        if (pathStart is null)
        {
            return new BranchActionResult(false);
        }

        var tip = FindBranchTip(pathStart);
        _cursorNodeId = tip.Id;
        _cursorX = tip.X;
        _cursorY = tip.Y;

        return new BranchActionResult(true);
    }

    public FlowPreview GetPreview()
    {
        var nodes = _doc.Nodes
            .Where(n => n.Type == "text")
            .Select(n =>
            {
                var isDecisionPoint = IsDecisionPointNode(n);
                var isPathStart = IsPathStartNode(n);
                var label = isDecisionPoint ? "Abzweigung" : isPathStart ? $"↳ {ExtractPathName(n)}" : BuildLabel(n.Text);
                return new PreviewNode(
                    n.Id, label, n.X, n.Y, n.Width, n.Height,
                    n.Id == _cursorNodeId, isDecisionPoint, isPathStart,
                    PathName: isPathStart ? ExtractPathName(n) : null);
            })
            .ToList();
        var edges = _doc.Edges.Select(e => new PreviewEdge(e.FromNode, e.ToNode)).ToList();
        return FlowPreviewBranching.TagBranches(new FlowPreview(nodes, edges));
    }

    /// <summary>Jumps the cursor to an arbitrary existing node, opening a new column so the new content doesn't overlap the existing flow — for regular nodes and path-start nodes; decision points are handled separately by the Ablauf-Übersicht's path popup (<see cref="StartNewPath"/>/<see cref="ContinuePath"/>).</summary>
    public BranchActionResult JumpToNode(string nodeId)
    {
        var node = _doc.Nodes.FirstOrDefault(n => n.Id == nodeId && n.Type == "text");
        if (node is null)
        {
            return new BranchActionResult(false);
        }

        _nextColumnX += NodeWidth + BranchColumnSpacing;
        _cursorNodeId = node.Id;
        _cursorX = _nextColumnX;
        _cursorY = node.Y;

        return new BranchActionResult(true);
    }

    private static bool IsDecisionPointNode(CanvasNode n) => n.Type == "text" && n.Text == DecisionPointLabel;

    private static bool IsPathStartNode(CanvasNode n) =>
        n.Type == "text" && (n.Text?.StartsWith(PathStartPrefix, StringComparison.Ordinal) ?? false);

    private static string ExtractPathName(CanvasNode n) => n.Text![PathStartPrefix.Length..].Trim();

    /// <summary>Follows the single-child edge chain from <paramref name="start"/> as far as it goes, resolving a path's current tip fresh from the graph every time (never cached, so it can never desync from what's actually in the file).</summary>
    private (string Id, double X, double Y, int Steps) FindBranchTip(CanvasNode start)
    {
        var current = start;
        var steps = 0;
        while (true)
        {
            var nextEdge = _doc.Edges.FirstOrDefault(e => e.FromNode == current.Id);
            var nextNode = nextEdge is null ? null : _doc.Nodes.FirstOrDefault(n => n.Id == nextEdge.ToNode);
            if (nextNode is null)
            {
                return (current.Id, current.X, current.Y, steps);
            }

            current = nextNode;
            steps++;
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
