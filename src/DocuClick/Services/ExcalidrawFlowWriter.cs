using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;

namespace DocuClick.Services;

/// <summary>
/// EXPERIMENTAL: writes clicks as connected nodes into an Obsidian
/// .excalidraw scene (plain JSON) — requires the free community
/// "Excalidraw" plugin to open/edit, unlike Canvas which is an Obsidian
/// core feature. Chosen for its freeform sketch-style visuals as an
/// alternative to Canvas's fixed boxes.
///
/// Each click becomes a rounded rectangle "card" (bound text label above
/// an embedded screenshot image), connected to the previous card with an
/// arrow. Layout/branching mirrors <see cref="CanvasFlowWriter"/> — see
/// <see cref="IFlowWriter"/> for the full decision-point/path model.
///
/// Text uses fontFamily=2 (Excalidraw's built-in clean "Normal"/sans-serif
/// family) instead of the default hand-drawn "Virgil" family, for a more
/// professional look. A scene file cannot embed an actual custom font —
/// rendering depends on fonts the Excalidraw plugin has locally — so this
/// is the practical equivalent achievable per-file.
/// </summary>
public sealed class ExcalidrawFlowWriter : IFlowWriter
{
    private const double NodeWidth = 380;
    private const double LabelHeight = 50;
    private const double LabelGap = 8;
    private const double SequentialSpacing = 50;
    private const double BranchColumnSpacing = 80;
    private const int FontFamilyNormal = 2;

    // Leading icons give both kinds of marker a distinct at-a-glance look
    // — Excalidraw markers are plain text elements, with no built-in shape
    // of their own the way draw.io's rhombus has.
    private const string DecisionPointLabel = "◆ Abzweigung";
    private const string PathStartPrefix = "↳ Pfad: ";
    private const string DecisionPointColor = "#6B7280"; // neutral gray — decision points aren't tied to any one path's color
    private static readonly string[] PathColors =
    {
        "#D97706", "#059669", "#DB2777", "#7C3AED", "#DC2626", "#0891B2"
    };

    private readonly AppConfig _config;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private string? _filePath;
    private ExcalidrawDocument _doc = new();
    private string? _cursorNodeId;
    private double _cursorX;
    private double _cursorY;
    private double _nextColumnX;
    private (string NodeId, double X, double Y)? _pendingResumeAnchor;

    public ExcalidrawFlowWriter(AppConfig config)
    {
        _config = config;
    }

    public string? CurrentNodeLabel => _cursorNodeId is null ? null : GetNodeLabel(_cursorNodeId);

    public List<ResumableNode> ListNodesForResume(string fileName)
    {
        if (string.IsNullOrWhiteSpace(_config.VaultPath))
        {
            return new List<ResumableNode>();
        }

        var path = Path.Combine(_config.VaultPath, fileName);
        var doc = LoadOrCreate(path);

        return doc.Elements
            .Where(e => e.Type == "rectangle")
            .OrderBy(e => e.Y).ThenBy(e => e.X)
            .Select(e => new ResumableNode(e.Id, GetNodeLabel(doc, e.Id), e.X, e.Y))
            .ToList();
    }

    public void SetResumeAnchor(ResumableNode node) => _pendingResumeAnchor = (node.Id, node.X, node.Y);

    public void StartSession(string fileName)
    {
        if (string.IsNullOrWhiteSpace(_config.VaultPath))
        {
            throw new InvalidOperationException("Kein Zielordner konfiguriert.");
        }

        _filePath = Path.Combine(_config.VaultPath, fileName);
        _doc = LoadOrCreate(_filePath);

        var rectangles = _doc.Elements.Where(e => e.Type == "rectangle").ToList();
        _nextColumnX = rectangles.Count > 0 ? rectangles.Max(e => e.X) + NodeWidth + BranchColumnSpacing : 0;

        if (_pendingResumeAnchor is { } resume && rectangles.Any(e => e.Id == resume.NodeId))
        {
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
            // vorhanden" until a throwaway click created *some* rectangle
            // first — confusing right after deliberately resuming a file
            // that already has content. Still placed in a fresh column so
            // it never visually collides with whatever's already in the file.
            var targetIds = _doc.Elements
                .Where(e => e.Type == "arrow" && e.EndBinding is not null)
                .Select(e => e.EndBinding!.ElementId)
                .ToHashSet();

            var root = rectangles
                .Where(r => !targetIds.Contains(r.Id))
                .OrderBy(r => r.Y).ThenBy(r => r.X)
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

    public void AddClickNode(string description, Bitmap screenshot, DateTime timestamp)
    {
        if (_filePath is null)
        {
            throw new InvalidOperationException("Excalidraw-Session wurde nicht gestartet.");
        }

        var imageHeight = screenshot.Height * (NodeWidth / screenshot.Width);
        var newY = _cursorNodeId is null
            ? _cursorY
            : _cursorY + LabelHeight + LabelGap + ImageHeightOf(_cursorNodeId) + SequentialSpacing;

        var rectangleId = "r" + Guid.NewGuid().ToString("N");
        var textId = "t" + Guid.NewGuid().ToString("N");
        var imageId = "img" + Guid.NewGuid().ToString("N");
        var fileId = "f" + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var rectangle = NewElement("rectangle", rectangleId, _cursorX, newY, NodeWidth, LabelHeight);
        rectangle.BackgroundColor = "#f5f5f5";
        rectangle.Roundness = new ExcalidrawRoundness { Type = 3 };
        rectangle.BoundElements = new List<ExcalidrawBoundElementRef> { new() { Id = textId, Type = "text" } };

        var text = NewElement("text", textId, _cursorX + 8, newY + 8, NodeWidth - 16, LabelHeight - 16);
        text.StrokeColor = "#1e1e1e";
        text.Text = TruncateLabel(description);
        text.OriginalText = description;
        text.FontSize = 16;
        text.FontFamily = FontFamilyNormal;
        text.TextAlign = "left";
        text.VerticalAlign = "top";
        text.ContainerId = rectangleId;
        text.LineHeight = 1.25;

        var imageFileEntry = new ExcalidrawFile
        {
            Id = fileId,
            MimeType = "image/png",
            DataUrl = "data:image/png;base64," + ToBase64Png(screenshot),
            Created = now
        };

        var image = NewElement("image", imageId, _cursorX, newY + LabelHeight + LabelGap, NodeWidth, imageHeight);
        image.FileId = fileId;
        image.Status = "saved";
        image.Scale = new double[] { 1, 1 };

        _doc.Elements.Add(rectangle);
        _doc.Elements.Add(text);
        _doc.Elements.Add(image);
        _doc.Files[fileId] = imageFileEntry;

        if (_cursorNodeId is not null)
        {
            _doc.Elements.Add(BuildArrow(_cursorNodeId, rectangleId));
        }

        _cursorNodeId = rectangleId;
        _cursorY = newY;

        Save();
    }

    /// <summary>
    /// Adds a small, visible "◆ Abzweigung" text marker connected from the
    /// current node with an arrow — an explicit waypoint rather than
    /// hidden state — then immediately forks <paramref name="firstPathName"/>
    /// off it via <see cref="StartNewPath"/> and jumps the cursor onto that
    /// path. There's deliberately no bare, unnamed "just continue" state:
    /// every path leaving a decision point is a real, named node from the
    /// start, so it always shows up in <see cref="ListPaths"/> and can be
    /// resumed later — an implicit default continuation could never be
    /// listed there, making it permanently unreachable once you moved on.
    /// </summary>
    public BranchActionResult MarkDecisionPoint(string firstPathName)
    {
        if (_cursorNodeId is null)
        {
            return new BranchActionResult(false);
        }

        var markerId = "decision" + Guid.NewGuid().ToString("N");
        var markerY = _cursorY + LabelHeight + LabelGap + ImageHeightOf(_cursorNodeId) + SequentialSpacing;

        var marker = NewElement("text", markerId, _cursorX, markerY, NodeWidth, LabelHeight);
        marker.StrokeColor = DecisionPointColor;
        marker.Text = DecisionPointLabel;
        marker.OriginalText = marker.Text;
        marker.FontSize = 16;
        marker.FontFamily = FontFamilyNormal;
        marker.TextAlign = "left";
        marker.VerticalAlign = "top";

        _doc.Elements.Add(marker);
        _doc.Elements.Add(BuildArrow(_cursorNodeId, markerId));

        // StartNewPath saves the whole document (marker included) once
        // it's done — no need to save here too.
        return StartNewPath(markerId, firstPathName);
    }

    /// <summary>Every path already forking from <paramref name="decisionPointId"/>, resolved fresh from the graph (never cached) — see <see cref="ListPaths"/> on <see cref="IFlowWriter"/>.</summary>
    public List<PathInfo> ListPaths(string decisionPointId)
    {
        var childIds = _doc.Elements
            .Where(e => e.Type == "arrow" && e.StartBinding?.ElementId == decisionPointId && e.EndBinding is not null)
            .Select(e => e.EndBinding!.ElementId)
            .ToHashSet();

        return _doc.Elements
            .Where(e => childIds.Contains(e.Id) && IsPathStart(e))
            .Select(e => new PathInfo(e.Id, ExtractPathName(e), FindBranchTip(e).Steps))
            .ToList();
    }

    /// <summary>Forks a brand-new named path from an existing decision point into its own column, and jumps the cursor onto it.</summary>
    public BranchActionResult StartNewPath(string decisionPointId, string pathName)
    {
        var decisionElement = _doc.Elements.FirstOrDefault(e => e.Id == decisionPointId && IsDecisionPoint(e));
        if (decisionElement is null)
        {
            return new BranchActionResult(false);
        }

        _nextColumnX += NodeWidth + BranchColumnSpacing;
        var pathStartId = "pathstart" + Guid.NewGuid().ToString("N");
        var color = PathColors[Math.Abs(pathStartId.GetHashCode()) % PathColors.Length];

        var pathStart = NewElement("text", pathStartId, _nextColumnX, decisionElement.Y, NodeWidth, LabelHeight);
        pathStart.StrokeColor = color;
        pathStart.Text = $"{PathStartPrefix}{pathName}";
        pathStart.OriginalText = pathStart.Text;
        pathStart.FontSize = 16;
        pathStart.FontFamily = FontFamilyNormal;
        pathStart.TextAlign = "left";
        pathStart.VerticalAlign = "top";

        _doc.Elements.Add(pathStart);
        _doc.Elements.Add(BuildArrow(decisionPointId, pathStartId));

        _cursorNodeId = pathStartId;
        _cursorX = pathStart.X;
        _cursorY = pathStart.Y;

        Save();
        return new BranchActionResult(true);
    }

    /// <summary>Resumes an existing path at wherever it currently ends (walked fresh from the graph — see <see cref="FindBranchTip"/>), in its own already-established column.</summary>
    public BranchActionResult ContinuePath(string pathStartNodeId)
    {
        var pathStart = _doc.Elements.FirstOrDefault(e => e.Id == pathStartNodeId && IsPathStart(e));
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
        var nodes = new List<PreviewNode>();
        foreach (var el in _doc.Elements.Where(e => e.Type == "rectangle"))
        {
            nodes.Add(new PreviewNode(el.Id, GetNodeLabel(_doc, el.Id), el.X, el.Y, el.Width, el.Height, el.Id == _cursorNodeId, false, false));
        }

        foreach (var el in _doc.Elements.Where(e => e.Type == "text" && (IsDecisionPoint(e) || IsPathStart(e))))
        {
            var isDecisionPoint = IsDecisionPoint(el);
            var pathName = isDecisionPoint ? null : ExtractPathName(el);
            var label = isDecisionPoint ? "Abzweigung" : $"↳ {pathName}";
            nodes.Add(new PreviewNode(el.Id, label, el.X, el.Y, el.Width, el.Height, el.Id == _cursorNodeId, isDecisionPoint, !isDecisionPoint, PathName: pathName));
        }

        var edges = _doc.Elements
            .Where(e => e.Type == "arrow" && e.StartBinding is not null && e.EndBinding is not null)
            .Select(e => new PreviewEdge(e.StartBinding!.ElementId, e.EndBinding!.ElementId))
            .ToList();

        return FlowPreviewBranching.TagBranches(new FlowPreview(nodes, edges));
    }

    /// <summary>Jumps the cursor to an arbitrary existing rectangle/decision-point/path-start element, opening a new column — for regular nodes; decision points are handled separately by the Ablauf-Übersicht's path popup (<see cref="StartNewPath"/>/<see cref="ContinuePath"/>).</summary>
    public BranchActionResult JumpToNode(string nodeId)
    {
        var target = _doc.Elements.FirstOrDefault(e =>
            e.Id == nodeId && (e.Type == "rectangle" || IsDecisionPoint(e) || IsPathStart(e)));
        if (target is null)
        {
            return new BranchActionResult(false);
        }

        _nextColumnX += NodeWidth + BranchColumnSpacing;
        _cursorNodeId = nodeId;
        _cursorX = _nextColumnX;
        _cursorY = target.Y;

        return new BranchActionResult(true);
    }

    private ExcalidrawElement BuildArrow(string fromRectangleId, string toRectangleId)
    {
        var fromRect = _doc.Elements.First(e => e.Id == fromRectangleId);
        var toRect = _doc.Elements.First(e => e.Id == toRectangleId);

        var startX = fromRect.X + fromRect.Width / 2;
        var startY = fromRect.Y + fromRect.Height;
        var endX = toRect.X + toRect.Width / 2;
        var endY = toRect.Y;

        var arrowId = "a" + Guid.NewGuid().ToString("N");
        var arrow = NewElement("arrow", arrowId,
            Math.Min(startX, endX), Math.Min(startY, endY),
            Math.Max(Math.Abs(endX - startX), 1), Math.Max(Math.Abs(endY - startY), 1));
        arrow.Points = new List<double[]> { new[] { startX - arrow.X, startY - arrow.Y }, new[] { endX - arrow.X, endY - arrow.Y } };
        arrow.StartBinding = new ExcalidrawBinding { ElementId = fromRectangleId, Focus = 0, Gap = 4 };
        arrow.EndBinding = new ExcalidrawBinding { ElementId = toRectangleId, Focus = 0, Gap = 4 };
        arrow.EndArrowhead = "arrow";

        (fromRect.BoundElements ??= new List<ExcalidrawBoundElementRef>()).Add(new ExcalidrawBoundElementRef { Id = arrowId, Type = "arrow" });
        (toRect.BoundElements ??= new List<ExcalidrawBoundElementRef>()).Add(new ExcalidrawBoundElementRef { Id = arrowId, Type = "arrow" });

        return arrow;
    }

    private static bool IsDecisionPoint(ExcalidrawElement e) => e.Type == "text" && e.OriginalText == DecisionPointLabel;

    private static bool IsPathStart(ExcalidrawElement e) =>
        e.Type == "text" && (e.OriginalText?.StartsWith(PathStartPrefix, StringComparison.Ordinal) ?? false);

    private static string ExtractPathName(ExcalidrawElement e) => e.OriginalText![PathStartPrefix.Length..].Trim();

    /// <summary>Follows the single-child arrow-binding chain from <paramref name="start"/> as far as it goes, resolving a path's current tip fresh from the graph every time (never cached, so it can never desync from what's actually in the file).</summary>
    private (string Id, double X, double Y, int Steps) FindBranchTip(ExcalidrawElement start)
    {
        var current = start;
        var steps = 0;
        while (true)
        {
            var arrow = _doc.Elements.FirstOrDefault(e => e.Type == "arrow" && e.StartBinding?.ElementId == current.Id);
            var nextId = arrow?.EndBinding?.ElementId;
            var next = nextId is null ? null : _doc.Elements.FirstOrDefault(e => e.Id == nextId);
            if (next is null)
            {
                return (current.Id, current.X, current.Y, steps);
            }

            current = next;
            steps++;
        }
    }

    private static ExcalidrawElement NewElement(string type, string id, double x, double y, double width, double height) => new()
    {
        Id = id,
        Type = type,
        X = x,
        Y = y,
        Width = width,
        Height = height,
        Seed = Random.Shared.Next(),
        VersionNonce = Random.Shared.Next(),
        Updated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    };

    /// <summary>Rendered image height for the node currently at the cursor, for vertical spacing of the next node.</summary>
    private double ImageHeightOf(string rectangleId)
    {
        var rect = _doc.Elements.FirstOrDefault(e => e.Type == "rectangle" && e.Id == rectangleId);
        if (rect is null)
        {
            return 0;
        }

        var image = _doc.Elements.FirstOrDefault(e => e.Type == "image" && Math.Abs(e.X - rect.X) < 0.5 && e.Y > rect.Y);
        return image?.Height ?? 0;
    }

    private string GetNodeLabel(string rectangleId) => GetNodeLabel(_doc, rectangleId);

    private static string GetNodeLabel(ExcalidrawDocument doc, string rectangleId)
    {
        var text = doc.Elements.FirstOrDefault(e => e.Type == "text" && e.ContainerId == rectangleId);
        return text?.OriginalText is { Length: > 0 } original ? TruncateLabel(original) : "(ohne Beschreibung)";
    }

    private static string TruncateLabel(string text)
    {
        var firstLine = text.Split('\n', 2)[0];
        return firstLine.Length > 70 ? firstLine[..70] + "…" : firstLine;
    }

    private static string ToBase64Png(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        return Convert.ToBase64String(ms.ToArray());
    }

    private ExcalidrawDocument LoadOrCreate(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var doc = JsonSerializer.Deserialize<ExcalidrawDocument>(json);
                if (doc is not null)
                {
                    return doc;
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Log($"Excalidraw-Datei konnte nicht gelesen werden, beginne neu: {ex.Message}");
        }

        return new ExcalidrawDocument();
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_doc, _jsonOptions);
        File.WriteAllText(_filePath!, json);
    }
}
