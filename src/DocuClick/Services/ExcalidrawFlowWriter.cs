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
/// arrow. Layout/branching mirrors <see cref="CanvasFlowWriter"/> exactly
/// (vertical main flow, named branch anchors, new column per branch).
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
    private const string BranchMarkerColor = "#7C3AED";
    private const string BranchMarkerPrefix = "Branch: ";

    private sealed record BranchAnchor(string Name, string NodeId, double X, double Y);

    private readonly AppConfig _config;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private string? _filePath;
    private ExcalidrawDocument _doc = new();
    private string? _cursorNodeId;
    private double _cursorX;
    private double _cursorY;
    private double _nextColumnX;
    private string? _currentBranchName;
    private readonly List<BranchAnchor> _branchAnchors = new();
    private (string NodeId, double X, double Y)? _pendingResumeAnchor;

    public ExcalidrawFlowWriter(AppConfig config)
    {
        _config = config;
    }

    public int BranchDepth => _branchAnchors.Count;

    public string? CurrentBranchName => _currentBranchName;

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

        _currentBranchName = null;

        // Rebuild branch anchors by scanning for their marker text elements
        // (see MarkBranchAnchor) instead of relying on in-memory state, so
        // a Stop()/Start() cycle on the same file doesn't lose them.
        _branchAnchors.Clear();
        foreach (var el in _doc.Elements
            .Where(e => e.Type == "text" && e.OriginalText is not null && e.OriginalText.StartsWith(BranchMarkerPrefix, StringComparison.Ordinal))
            .OrderBy(e => e.Y).ThenBy(e => e.X))
        {
            var branchName = el.OriginalText![BranchMarkerPrefix.Length..].Trim();
            if (branchName.Length == 0)
            {
                continue;
            }

            AddOrReplaceAnchor(new BranchAnchor(branchName, el.Id, el.X, el.Y));
        }

        if (_pendingResumeAnchor is { } resume && rectangles.Any(e => e.Id == resume.NodeId))
        {
            _cursorNodeId = resume.NodeId;
            _cursorX = _nextColumnX;
            _cursorY = resume.Y;
        }
        else
        {
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
    /// Adds a small, visible "Branch: &lt;name&gt;" text marker connected
    /// from the current node with an arrow — an explicit waypoint object
    /// rather than hidden state, so it shows up in the scene itself and
    /// survives a Stop()/Start() cycle (see StartSession). Doesn't move
    /// the cursor; only <see cref="JumpToAnchor"/> actually jumps to a
    /// marker. Re-marking an existing name adds a fresh marker (the
    /// newest one wins on the next reload, same as in-memory re-marking).
    /// </summary>
    public BranchActionResult MarkBranchAnchor(string branchName)
    {
        if (_cursorNodeId is null)
        {
            return new BranchActionResult(false, _branchAnchors.Count, null);
        }

        var markerId = "branch" + Guid.NewGuid().ToString("N");
        var markerY = _cursorY + LabelHeight + LabelGap + ImageHeightOf(_cursorNodeId) + SequentialSpacing;

        var marker = NewElement("text", markerId, _cursorX, markerY, NodeWidth, LabelHeight);
        marker.StrokeColor = BranchMarkerColor;
        marker.Text = $"{BranchMarkerPrefix}{branchName}";
        marker.OriginalText = marker.Text;
        marker.FontSize = 16;
        marker.FontFamily = FontFamilyNormal;
        marker.TextAlign = "left";
        marker.VerticalAlign = "top";

        _doc.Elements.Add(marker);
        _doc.Elements.Add(BuildArrow(_cursorNodeId, markerId));

        AddOrReplaceAnchor(new BranchAnchor(branchName, markerId, marker.X, marker.Y));
        Save();

        return new BranchActionResult(true, _branchAnchors.Count, branchName);
    }

    public List<string> ListBranchAnchorNames() => _branchAnchors.Select(a => a.Name).ToList();

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
        var nodes = new List<PreviewNode>();
        foreach (var el in _doc.Elements.Where(e => e.Type == "rectangle"))
        {
            nodes.Add(new PreviewNode(el.Id, GetNodeLabel(_doc, el.Id), el.X, el.Y, el.Width, el.Height, el.Id == _cursorNodeId, false));
        }

        foreach (var el in _doc.Elements.Where(e =>
            e.Type == "text" && e.OriginalText is not null && e.OriginalText.StartsWith(BranchMarkerPrefix, StringComparison.Ordinal)))
        {
            nodes.Add(new PreviewNode(el.Id, el.OriginalText!, el.X, el.Y, el.Width, el.Height, el.Id == _cursorNodeId, true));
        }

        var edges = _doc.Elements
            .Where(e => e.Type == "arrow" && e.StartBinding is not null && e.EndBinding is not null)
            .Select(e => new PreviewEdge(e.StartBinding!.ElementId, e.EndBinding!.ElementId))
            .ToList();

        return new FlowPreview(nodes, edges);
    }

    /// <summary>Jumps the cursor to an arbitrary existing rectangle/marker node, opening a new column — same mechanics as <see cref="JumpToAnchor"/>, just not limited to named branch markers.</summary>
    public BranchActionResult JumpToNode(string nodeId)
    {
        var target = _doc.Elements.FirstOrDefault(e =>
            e.Id == nodeId && (e.Type == "rectangle" ||
                (e.Type == "text" && e.OriginalText is not null && e.OriginalText.StartsWith(BranchMarkerPrefix, StringComparison.Ordinal))));
        if (target is null)
        {
            return new BranchActionResult(false, _branchAnchors.Count, null);
        }

        _nextColumnX += NodeWidth + BranchColumnSpacing;
        _cursorNodeId = nodeId;
        _cursorX = _nextColumnX;
        _cursorY = target.Y;
        _currentBranchName = _branchAnchors.FirstOrDefault(a => a.NodeId == nodeId)?.Name;

        return new BranchActionResult(true, _branchAnchors.Count, _currentBranchName);
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
