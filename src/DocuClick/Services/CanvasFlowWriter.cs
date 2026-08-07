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
/// connected from the current node and immediately forks its first named
/// "↳ Pfad: &lt;name&gt;" column, jumping the cursor onto it. Later,
/// <see cref="StartNewPath"/> forks another new column from the same
/// diamond, or <see cref="ContinuePath"/> resumes one started earlier.
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
    private const double GroupPadding = 8; // margin between a card's group-node border and its text+image children
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

        // A visible bounding group behind the text+image pair — Canvas
        // edges only ever connect text nodes (see FindImageSibling's doc
        // comment), so without this the screenshot reads as a disconnected
        // element floating below the description instead of clearly
        // belonging with it. Dragging the group in Obsidian also moves both
        // children together, same idea as draw.io mode's container=1 card.
        // Added first so it renders behind its text/image children.
        var groupNode = new CanvasNode
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = "group",
            X = _cursorX - GroupPadding,
            Y = newY - GroupPadding,
            Width = NodeWidth + GroupPadding * 2,
            Height = TextNodeHeight + TextToImageGap + ImageNodeHeight + GroupPadding * 2
        };
        _doc.Nodes.Add(groupNode);

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
    /// — an explicit, visible waypoint rather than hidden state — then
    /// immediately forks <paramref name="firstPathName"/> off it via
    /// <see cref="StartNewPath"/> and jumps the cursor onto that path.
    /// There's deliberately no bare, unnamed "just continue" state: every
    /// path leaving a decision point is a real, named node from the start,
    /// so it always shows up in <see cref="ListPaths"/> and can be resumed
    /// later — an implicit default continuation could never be listed
    /// there, making it permanently unreachable once you moved on.
    /// </summary>
    public BranchActionResult MarkDecisionPoint(string firstPathName)
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

        // StartNewPath saves the whole document (marker included) once
        // it's done — no need to save here too.
        return StartNewPath(marker.Id, firstPathName);
    }

    /// <summary>Every path already forking from <paramref name="originNodeId"/>, resolved fresh from the graph (never cached) — see <see cref="ListPaths"/> on <see cref="IFlowWriter"/>.</summary>
    public List<PathInfo> ListPaths(string originNodeId)
    {
        var childIds = _doc.Edges.Where(e => e.FromNode == originNodeId).Select(e => e.ToNode).ToHashSet();
        return _doc.Nodes
            .Where(n => childIds.Contains(n.Id) && IsPathStartNode(n))
            .Select(n => new PathInfo(n.Id, ExtractPathName(n), FindBranchTip(n).Steps))
            .ToList();
    }

    /// <summary>
    /// Forks a brand-new named path from an existing node into its own
    /// column, and jumps the cursor onto it. The origin no longer has to be
    /// a decision-point diamond — any existing node can be the start of a
    /// retroactive alternate branch (see <see cref="JumpToNode"/> for why
    /// that's now required instead of silently forking).
    /// </summary>
    public BranchActionResult StartNewPath(string originNodeId, string pathName)
    {
        var originNode = _doc.Nodes.FirstOrDefault(n => n.Id == originNodeId && n.Type == "text");
        if (originNode is null)
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
            Y = originNode.Y,
            Width = NodeWidth,
            Height = MarkerHeight,
            Color = PathStartColor
        };
        _doc.Nodes.Add(pathStart);
        _doc.Edges.Add(new CanvasEdge
        {
            Id = Guid.NewGuid().ToString("N"),
            FromNode = originNode.Id,
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

    /// <summary>
    /// Jumps the cursor to an arbitrary existing node — always resolved
    /// forward to that branch's current tip (via <see cref="FindBranchTip"/>)
    /// and resumed exactly there, in the same column. Never opens a new
    /// column and never adds a second outgoing edge from a node that
    /// already has one: clicking a node that already has downstream
    /// content is "continue where this branch left off", not "silently
    /// fork a second, untracked branch from here" — that used to create a
    /// second edge with no <see cref="PreviewNode.PathId"/> of its own,
    /// which the Ablauf-Übersicht's graph-based layout then collapsed onto
    /// the same grid cell as whatever else followed that node, drawing two
    /// unrelated nodes on top of each other. A deliberate new branch from a
    /// non-tip node now goes through <see cref="StartNewPath"/> instead
    /// (see the Ablauf-Übersicht's per-node popup), which gives it a real
    /// name and its own <see cref="PreviewNode.PathId"/>/column.
    /// </summary>
    public BranchActionResult JumpToNode(string nodeId)
    {
        var node = _doc.Nodes.FirstOrDefault(n => n.Id == nodeId && n.Type == "text");
        if (node is null)
        {
            return new BranchActionResult(false);
        }

        var tip = FindBranchTip(node);
        _cursorNodeId = tip.Id;
        _cursorX = tip.X;
        _cursorY = tip.Y;

        return new BranchActionResult(true);
    }

    public BranchActionResult RenameNode(string nodeId, string newLabel)
    {
        var node = _doc.Nodes.FirstOrDefault(n => n.Id == nodeId && n.Type == "text");
        if (node is null || IsDecisionPointNode(node))
        {
            return new BranchActionResult(false);
        }

        node.Text = IsPathStartNode(node) ? $"{PathStartPrefix}{newLabel}" : newLabel;
        Save();
        return new BranchActionResult(true);
    }

    /// <summary>
    /// Exactly one outgoing edge: the parent is reconnected straight to
    /// that child so the branch below isn't orphaned. More than one (a
    /// decision point, or any node a path was forked from): the whole
    /// downstream subtree goes with it — the caller (the Ablauf-Übersicht)
    /// is responsible for confirming that with the user first, since there
    /// is no single "the" continuation to stitch to here.
    /// </summary>
    public BranchActionResult DeleteNode(string nodeId)
    {
        var node = _doc.Nodes.FirstOrDefault(n => n.Id == nodeId && n.Type == "text");
        if (node is null)
        {
            return new BranchActionResult(false);
        }

        var childEdges = _doc.Edges.Where(e => e.FromNode == nodeId).ToList();
        var parentEdge = _doc.Edges.FirstOrDefault(e => e.ToNode == nodeId);

        // A path-start node's single child is never itself tagged with the
        // path's identity (only the path-start node is — see
        // FlowPreviewBranching.TagBranches) — stitching parent straight to
        // that child like an ordinary 1-child node would silently erase
        // which path it belonged to, collapsing it back onto whatever the
        // path forked from. That's the exact same "second, untracked branch
        // with no PathId" shape JumpToNode used to create by accident (see
        // its own doc comment) — so a path-start with a child must cascade
        // just like a >1-child node does, even though it only has the one.
        var toRemove = new HashSet<string> { nodeId };
        if (childEdges.Count > 1 || (childEdges.Count == 1 && IsPathStartNode(node)))
        {
            var queue = new Queue<string>(childEdges.Select(e => e.ToNode));
            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                if (!toRemove.Add(id))
                {
                    continue;
                }

                foreach (var e in _doc.Edges.Where(e => e.FromNode == id))
                {
                    queue.Enqueue(e.ToNode);
                }
            }
        }

        foreach (var id in toRemove)
        {
            var n = _doc.Nodes.FirstOrDefault(x => x.Id == id);
            if (n is null)
            {
                continue;
            }

            var imageSibling = FindImageSibling(n);
            if (imageSibling is not null)
            {
                _doc.Nodes.Remove(imageSibling);
            }

            var groupSibling = FindGroupSibling(n);
            if (groupSibling is not null)
            {
                _doc.Nodes.Remove(groupSibling);
            }

            _doc.Nodes.Remove(n);
        }

        _doc.Edges.RemoveAll(e => toRemove.Contains(e.FromNode) || toRemove.Contains(e.ToNode));

        if (childEdges.Count == 1 && parentEdge is not null && !IsPathStartNode(node))
        {
            _doc.Edges.Add(new CanvasEdge
            {
                Id = Guid.NewGuid().ToString("N"),
                FromNode = parentEdge.FromNode,
                ToNode = childEdges[0].ToNode
            });
        }

        if (_cursorNodeId is not null && toRemove.Contains(_cursorNodeId))
        {
            if (parentEdge is not null)
            {
                var parentNode = _doc.Nodes.First(n => n.Id == parentEdge.FromNode);
                var tip = FindBranchTip(parentNode);
                _cursorNodeId = tip.Id;
                _cursorX = tip.X;
                _cursorY = tip.Y;
            }
            else
            {
                _cursorNodeId = null;
            }
        }

        Save();
        return new BranchActionResult(true);
    }

    /// <summary>Only ordinary content nodes qualify, on both ends — see <see cref="IFlowWriter.ReparentNode"/>.</summary>
    public BranchActionResult ReparentNode(string nodeId, string newParentId)
    {
        if (nodeId == newParentId)
        {
            return new BranchActionResult(false);
        }

        var node = _doc.Nodes.FirstOrDefault(n => n.Id == nodeId && n.Type == "text");
        var newParent = _doc.Nodes.FirstOrDefault(n => n.Id == newParentId && n.Type == "text");
        if (node is null || newParent is null
            || IsDecisionPointNode(node) || IsPathStartNode(node)
            || IsDecisionPointNode(newParent) || IsPathStartNode(newParent))
        {
            return new BranchActionResult(false);
        }

        // Cycle guard: newParentId must not be a descendant of nodeId.
        var descendants = new HashSet<string>();
        var descendantQueue = new Queue<string>(_doc.Edges.Where(e => e.FromNode == nodeId).Select(e => e.ToNode));
        while (descendantQueue.Count > 0)
        {
            var id = descendantQueue.Dequeue();
            if (!descendants.Add(id))
            {
                continue;
            }

            foreach (var e in _doc.Edges.Where(e => e.FromNode == id))
            {
                descendantQueue.Enqueue(e.ToNode);
            }
        }

        if (descendants.Contains(newParentId))
        {
            return new BranchActionResult(false);
        }

        var oldParentEdge = _doc.Edges.FirstOrDefault(e => e.ToNode == nodeId);
        if (oldParentEdge is not null)
        {
            _doc.Edges.Remove(oldParentEdge);
        }

        _doc.Edges.Add(new CanvasEdge { Id = Guid.NewGuid().ToString("N"), FromNode = newParentId, ToNode = nodeId });

        Save();
        return new BranchActionResult(true);
    }

    /// <summary>Only ordinary content nodes qualify, on both ends — see <see cref="IFlowWriter.ConnectNodes"/>.</summary>
    public BranchActionResult ConnectNodes(string fromNodeId, string toNodeId)
    {
        if (fromNodeId == toNodeId)
        {
            return new BranchActionResult(false);
        }

        var from = _doc.Nodes.FirstOrDefault(n => n.Id == fromNodeId && n.Type == "text");
        var to = _doc.Nodes.FirstOrDefault(n => n.Id == toNodeId && n.Type == "text");
        if (from is null || to is null
            || IsDecisionPointNode(from) || IsPathStartNode(from)
            || IsDecisionPointNode(to) || IsPathStartNode(to))
        {
            return new BranchActionResult(false);
        }

        if (_doc.Edges.Any(e => e.FromNode == fromNodeId && e.ToNode == toNodeId))
        {
            return new BranchActionResult(false); // already connected, nothing to do
        }

        // Cycle guard: toNodeId must not already be able to reach fromNodeId.
        var reachableFromTo = new HashSet<string>();
        var queue = new Queue<string>(_doc.Edges.Where(e => e.FromNode == toNodeId).Select(e => e.ToNode));
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!reachableFromTo.Add(id))
            {
                continue;
            }

            foreach (var e in _doc.Edges.Where(e => e.FromNode == id))
            {
                queue.Enqueue(e.ToNode);
            }
        }

        if (reachableFromTo.Contains(fromNodeId))
        {
            return new BranchActionResult(false);
        }

        _doc.Edges.Add(new CanvasEdge { Id = Guid.NewGuid().ToString("N"), FromNode = fromNodeId, ToNode = toNodeId });

        Save();
        return new BranchActionResult(true);
    }

    /// <summary>Only ordinary content nodes qualify, on both ends — see <see cref="IFlowWriter.DisconnectNodes"/>.</summary>
    public BranchActionResult DisconnectNodes(string fromNodeId, string toNodeId)
    {
        var from = _doc.Nodes.FirstOrDefault(n => n.Id == fromNodeId && n.Type == "text");
        var to = _doc.Nodes.FirstOrDefault(n => n.Id == toNodeId && n.Type == "text");
        if (from is null || to is null
            || IsDecisionPointNode(from) || IsPathStartNode(from)
            || IsDecisionPointNode(to) || IsPathStartNode(to))
        {
            return new BranchActionResult(false);
        }

        var edge = _doc.Edges.FirstOrDefault(e => e.FromNode == fromNodeId && e.ToNode == toNodeId);
        if (edge is null)
        {
            return new BranchActionResult(false);
        }

        _doc.Edges.Remove(edge);

        Save();
        return new BranchActionResult(true);
    }

    /// <summary>The sibling "file" (image) node created alongside a content node in <see cref="AddClickNode"/> — identified by its fixed position relative to the text node, since the two are never edge-linked to each other.</summary>
    private CanvasNode? FindImageSibling(CanvasNode textNode) => _doc.Nodes.FirstOrDefault(n =>
        n.Type == "file" && Math.Abs(n.X - textNode.X) < 0.5 && Math.Abs(n.Y - (textNode.Y + TextNodeHeight + TextToImageGap)) < 0.5);

    /// <summary>The sibling "group" node wrapping a content node and its image, created alongside both in <see cref="AddClickNode"/> — same fixed-position lookup as <see cref="FindImageSibling"/>. Marker nodes (decision points/path starts) never get one.</summary>
    private CanvasNode? FindGroupSibling(CanvasNode textNode) => _doc.Nodes.FirstOrDefault(n =>
        n.Type == "group" && Math.Abs(n.X - (textNode.X - GroupPadding)) < 0.5 && Math.Abs(n.Y - (textNode.Y - GroupPadding)) < 0.5);

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
        FileSaveRetry.Save(_canvasPath!, () => File.WriteAllText(_canvasPath!, json));
    }
}
