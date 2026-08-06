using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace DocuClick.Services;

/// <summary>
/// Writes clicks as a draw.io / diagrams.net (.drawio) flowchart — plain
/// mxGraph XML, no plugin or draw.io installation needed to produce it,
/// just the free draw.io desktop/web app (or the VS Code extension) to
/// open it. Screenshots are embedded directly as base64 PNG data in each
/// card's image cell, so the file is fully self-contained.
///
/// Each click is a real mxGraph *container* cell (a "card": rounded
/// border, shadow, a numbered badge, a caption, and the screenshot as a
/// child shape) rather than a bare floating image — dragging the card
/// moves label and screenshot together as one unit. Paths get their own
/// accent color (hashed from the path's own node id, so it's stable
/// without needing a registry) so separate paths read apart at a glance,
/// and edges have real arrowheads colored to match the path they lead into.
///
/// Branching (see <see cref="IFlowWriter"/> for the full model):
/// <see cref="MarkDecisionPoint"/> adds a gray rhombus connected from the
/// current card and moves the cursor onto it inline — clicking normally
/// afterward just continues straight through it. From there,
/// <see cref="StartNewPath"/> forks a new, colored "↳ Pfad: &lt;name&gt;"
/// column, or <see cref="ContinuePath"/> resumes one started earlier.
/// Nothing about a path/decision point is cached in memory — every lookup
/// walks the actual mxCell graph, so a Stop()/Start() cycle can never
/// forget or desync from what's really in the file.
/// </summary>
public sealed class DrawIoFlowWriter : IFlowWriter
{
    private const double CardWidth = 380;
    private const double CardMargin = 14;
    private const double BadgeSize = 26;
    private const double ImageAreaHeight = 240;
    private const double CharsPerLine = 44;
    private const double LineHeight = 18;
    private const double MinHeaderHeight = 40;
    private const double SequentialSpacing = 50;
    private const double BranchColumnSpacing = 90;
    private const double MarkerWidth = 200;
    private const double MarkerHeight = 80;

    private const string DecisionPointLabel = "◆ Abzweigung";
    private const string PathStartPrefix = "↳ Pfad: ";
    private const string DecisionPointColor = "#6B7280"; // neutral gray — decision points aren't tied to any one path's color
    private const string CardIdPrefix = "card_";
    private const string DecisionPointIdPrefix = "decision_";
    private const string PathStartIdPrefix = "pathstart_";

    private const string MainColor = "#2563EB";
    private static readonly string[] BranchColors =
    {
        "#D97706", "#059669", "#DB2777", "#7C3AED", "#DC2626", "#0891B2"
    };

    private readonly AppConfig _config;

    private string? _filePath;
    private XDocument _doc;
    private XElement _root;

    private string? _cursorNodeId;
    private double _cursorX;
    private double _cursorY;
    private double _nextColumnX;
    private int _stepCounter;

    // Which path (if any) the cursor is currently inside — re-derived by
    // SetCursor every time the cursor moves (walking backward through
    // edges to the nearest path-start ancestor) rather than tracked ad
    // hoc, purely so AddClickNode's cards/edges get that path's accent
    // color. Never persisted; a fresh StartSession simply has no notion of
    // it until the cursor moves somewhere.
    private string? _currentPathStartId;

    // Tracks the most recently built card's actual height (varies with
    // caption length) so the *next* card's Y position never overlaps it —
    // a fixed per-card height would either waste space or clip long
    // captions.
    private double _lastCardHeight = ImageAreaHeight + MinHeaderHeight + CardMargin;

    private readonly Dictionary<string, string> _labels = new();
    private (string NodeId, double X, double Y)? _pendingResumeAnchor;

    public DrawIoFlowWriter(AppConfig config)
    {
        _config = config;
        (_doc, _root) = NewEmptyDocument();
    }

    public string? CurrentNodeLabel => _cursorNodeId is null ? null : _labels.GetValueOrDefault(_cursorNodeId);

    public List<ResumableNode> ListNodesForResume(string fileName)
    {
        if (string.IsNullOrWhiteSpace(_config.VaultPath))
        {
            return new List<ResumableNode>();
        }

        var path = Path.Combine(_config.VaultPath, fileName);
        var (_, root) = LoadOrCreate(path);

        var result = new List<ResumableNode>();
        foreach (var cell in root.Elements("mxCell"))
        {
            var id = (string?)cell.Attribute("id");
            if (id is null || !IsCardCell(cell))
            {
                continue;
            }

            var labelCell = root.Elements("mxCell").FirstOrDefault(c => (string?)c.Attribute("id") == id + "_label");
            var label = (string?)labelCell?.Attribute("value");

            var geometry = cell.Element("mxGeometry");
            var x = ParseDouble(geometry?.Attribute("x"));
            var y = ParseDouble(geometry?.Attribute("y"));

            result.Add(new ResumableNode(id, string.IsNullOrEmpty(label) ? "(ohne Beschreibung)" : label, x, y));
        }

        return result.OrderBy(n => n.Y).ThenBy(n => n.X).ToList();
    }

    public void SetResumeAnchor(ResumableNode node) => _pendingResumeAnchor = (node.Id, node.X, node.Y);

    public void StartSession(string fileName)
    {
        if (string.IsNullOrWhiteSpace(_config.VaultPath))
        {
            throw new InvalidOperationException("Kein Zielordner konfiguriert.");
        }

        _filePath = Path.Combine(_config.VaultPath, fileName);
        (_doc, _root) = LoadOrCreate(_filePath);

        _labels.Clear();
        _stepCounter = 0;

        foreach (var cell in _root.Elements("mxCell"))
        {
            var id = (string?)cell.Attribute("id");
            if (id is null)
            {
                continue;
            }

            if (IsCardCell(cell))
            {
                _stepCounter++;
                var labelCell = _root.Elements("mxCell").FirstOrDefault(c => (string?)c.Attribute("id") == id + "_label");
                var label = (string?)labelCell?.Attribute("value");
                if (!string.IsNullOrEmpty(label))
                {
                    _labels[id] = label;
                }
            }
            else if (IsDecisionPointId(id) || IsPathStartId(id))
            {
                var value = (string?)cell.Attribute("value");
                if (!string.IsNullOrEmpty(value))
                {
                    _labels[id] = value;
                }
            }
        }

        _nextColumnX = _root.Elements("mxCell")
            .Select(c => (double?)c.Element("mxGeometry")?.Attribute("x"))
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .DefaultIfEmpty(-CardWidth - BranchColumnSpacing)
            .Max() + CardWidth + BranchColumnSpacing;

        if (_pendingResumeAnchor is { } resume && _root.Elements("mxCell").Any(c => (string?)c.Attribute("id") == resume.NodeId))
        {
            SetCursor(resume.NodeId, _nextColumnX, resume.Y, ImageAreaHeight + MinHeaderHeight + CardMargin);
        }
        else
        {
            // No explicit resume point chosen ("Bestehende Datei
            // fortsetzen" without picking a node): still resume the main
            // flow's actual current tip rather than leaving the cursor
            // null. A null cursor meant every node-relative action
            // (MarkDecisionPoint included) failed with "kein Klick
            // vorhanden" until a throwaway click created *some* card first
            // — confusing right after deliberately resuming a file that
            // already has content. Still placed in a fresh column so it
            // never visually collides with whatever's already in the file.
            var targetIds = _root.Elements("mxCell")
                .Where(c => (string?)c.Attribute("edge") == "1")
                .Select(c => (string?)c.Attribute("target"))
                .Where(id => id is not null)
                .Select(id => id!)
                .ToHashSet();

            var rootCell = _root.Elements("mxCell")
                .Where(c => IsCardCell(c) && !targetIds.Contains((string)c.Attribute("id")!))
                .OrderBy(c => ParseDouble(c.Element("mxGeometry")?.Attribute("y")))
                .ThenBy(c => ParseDouble(c.Element("mxGeometry")?.Attribute("x")))
                .FirstOrDefault();

            if (rootCell is not null)
            {
                var rootId = (string)rootCell.Attribute("id")!;
                var geometry = rootCell.Element("mxGeometry");
                var tip = FindBranchTip(rootId, ParseDouble(geometry?.Attribute("x")), ParseDouble(geometry?.Attribute("y")), ParseDouble(geometry?.Attribute("height")));
                SetCursor(tip.Id, _nextColumnX, tip.Y, tip.Height);
            }
            else
            {
                // Truly empty file — nothing yet to attach to.
                _cursorNodeId = null;
                _cursorX = _nextColumnX;
                _cursorY = 0;
                _currentPathStartId = null;
            }
        }

        _pendingResumeAnchor = null;
    }

    public void Stop()
    {
        _cursorNodeId = null;
        _currentPathStartId = null;
    }

    public void AddClickNode(string description, Bitmap screenshot, DateTime timestamp)
    {
        if (_filePath is null)
        {
            throw new InvalidOperationException("draw.io-Session wurde nicht gestartet.");
        }

        var newY = _cursorNodeId is null ? _cursorY : _cursorY + _lastCardHeight + SequentialSpacing;
        var cardId = CardIdPrefix + Guid.NewGuid().ToString("N");
        var accent = GetAccentColor();

        var cardHeight = BuildCard(cardId, _cursorX, newY, ++_stepCounter, description, screenshot, accent);

        if (_cursorNodeId is not null)
        {
            AddEdge(_cursorNodeId, cardId, accent);
        }

        _labels[cardId] = description;
        SetCursor(cardId, _cursorX, newY, cardHeight);

        Save();
    }

    /// <summary>
    /// Adds a gray "◆ Abzweigung" rhombus connected from the current card
    /// — an explicit, visible waypoint rather than hidden state — and
    /// moves the cursor onto it inline, so the next regular click still
    /// just continues straight through it in the same column. Forking an
    /// actual new path only happens via <see cref="StartNewPath"/>, chosen
    /// later by clicking this rhombus in the Ablauf-Übersicht; nothing
    /// here asks for a name upfront, since a decision point can end up
    /// with any number of differently-named paths over time.
    /// </summary>
    public BranchActionResult MarkDecisionPoint()
    {
        if (_cursorNodeId is null)
        {
            return new BranchActionResult(false);
        }

        var markerY = _cursorY + _lastCardHeight + SequentialSpacing;
        var markerId = DecisionPointIdPrefix + Guid.NewGuid().ToString("N");
        var markerX = _cursorX + (CardWidth - MarkerWidth) / 2;

        var marker = new XElement("mxCell",
            new XAttribute("id", markerId),
            new XAttribute("value", DecisionPointLabel),
            new XAttribute("style",
                $"rhombus;whiteSpace=wrap;html=1;fillColor=#F3F4F6;strokeColor={DecisionPointColor};strokeWidth=2;" +
                $"fontColor=#1F2937;fontStyle=1;fontSize=12;arcSize=4;"),
            new XAttribute("vertex", "1"),
            new XAttribute("parent", "1"),
            new XElement("mxGeometry",
                new XAttribute("x", Fmt(markerX)),
                new XAttribute("y", Fmt(markerY)),
                new XAttribute("width", Fmt(MarkerWidth)),
                new XAttribute("height", Fmt(MarkerHeight)),
                new XAttribute("as", "geometry")));
        _root.Add(marker);

        AddEdge(_cursorNodeId, markerId, DecisionPointColor);
        _labels[markerId] = DecisionPointLabel;

        SetCursor(markerId, markerX, markerY, MarkerHeight);
        Save();
        return new BranchActionResult(true);
    }

    /// <summary>Every path already forking from <paramref name="decisionPointId"/>, resolved fresh from the graph (never cached) — see <see cref="ListPaths"/> on <see cref="IFlowWriter"/>.</summary>
    public List<PathInfo> ListPaths(string decisionPointId)
    {
        var childIds = _root.Elements("mxCell")
            .Where(c => (string?)c.Attribute("edge") == "1" && (string?)c.Attribute("source") == decisionPointId)
            .Select(c => (string?)c.Attribute("target"))
            .Where(id => id is not null)
            .Select(id => id!);

        var result = new List<PathInfo>();
        foreach (var id in childIds.Where(IsPathStartId))
        {
            var cell = _root.Elements("mxCell").FirstOrDefault(c => (string?)c.Attribute("id") == id);
            var value = (string?)cell?.Attribute("value") ?? "";
            if (cell is null || !value.StartsWith(PathStartPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var geometry = cell.Element("mxGeometry");
            var tip = FindBranchTip(id, ParseDouble(geometry?.Attribute("x")), ParseDouble(geometry?.Attribute("y")), ParseDouble(geometry?.Attribute("height")));
            result.Add(new PathInfo(id, value[PathStartPrefix.Length..].Trim(), tip.Steps));
        }

        return result;
    }

    /// <summary>Forks a brand-new named path from an existing decision point into its own column, and jumps the cursor onto it.</summary>
    public BranchActionResult StartNewPath(string decisionPointId, string pathName)
    {
        var decisionCell = _root.Elements("mxCell").FirstOrDefault(c => (string?)c.Attribute("id") == decisionPointId);
        if (decisionCell is null || !IsDecisionPointId(decisionPointId))
        {
            return new BranchActionResult(false);
        }

        var decisionY = ParseDouble(decisionCell.Element("mxGeometry")?.Attribute("y"));

        _nextColumnX += CardWidth + BranchColumnSpacing;
        var pathStartId = PathStartIdPrefix + Guid.NewGuid().ToString("N");
        var color = BranchColors[Math.Abs(pathStartId.GetHashCode()) % BranchColors.Length];
        var pathStartX = _nextColumnX + (CardWidth - MarkerWidth) / 2;

        var pathStart = new XElement("mxCell",
            new XAttribute("id", pathStartId),
            new XAttribute("value", $"{PathStartPrefix}{pathName}"),
            new XAttribute("style",
                $"rounded=1;arcSize=30;whiteSpace=wrap;html=1;fillColor=#F0FDF4;strokeColor={color};strokeWidth=2;" +
                $"fontColor=#14532D;fontStyle=1;fontSize=12;"),
            new XAttribute("vertex", "1"),
            new XAttribute("parent", "1"),
            new XElement("mxGeometry",
                new XAttribute("x", Fmt(pathStartX)),
                new XAttribute("y", Fmt(decisionY)),
                new XAttribute("width", Fmt(MarkerWidth)),
                new XAttribute("height", Fmt(MarkerHeight)),
                new XAttribute("as", "geometry")));
        _root.Add(pathStart);

        AddEdge(decisionPointId, pathStartId, color);
        _labels[pathStartId] = $"{PathStartPrefix}{pathName}";

        SetCursor(pathStartId, pathStartX, decisionY, MarkerHeight);
        Save();
        return new BranchActionResult(true);
    }

    /// <summary>Resumes an existing path at wherever it currently ends (walked fresh from the graph — see <see cref="FindBranchTip"/>), in its own already-established column.</summary>
    public BranchActionResult ContinuePath(string pathStartNodeId)
    {
        if (!IsPathStartId(pathStartNodeId))
        {
            return new BranchActionResult(false);
        }

        var startCell = _root.Elements("mxCell").FirstOrDefault(c => (string?)c.Attribute("id") == pathStartNodeId);
        if (startCell is null)
        {
            return new BranchActionResult(false);
        }

        var geometry = startCell.Element("mxGeometry");
        var tip = FindBranchTip(pathStartNodeId, ParseDouble(geometry?.Attribute("x")), ParseDouble(geometry?.Attribute("y")), ParseDouble(geometry?.Attribute("height")));
        SetCursor(tip.Id, tip.X, tip.Y, tip.Height);

        return new BranchActionResult(true);
    }

    public FlowPreview GetPreview()
    {
        var nodes = new List<PreviewNode>();
        foreach (var cell in _root.Elements("mxCell"))
        {
            var id = (string?)cell.Attribute("id");
            if (id is null)
            {
                continue;
            }

            var isDecisionPoint = IsDecisionPointId(id);
            var isPathStart = IsPathStartId(id);
            var isCard = IsCardCell(cell);
            if (!isDecisionPoint && !isPathStart && !isCard)
            {
                continue;
            }

            var geometry = cell.Element("mxGeometry");
            var x = ParseDouble(geometry?.Attribute("x"));
            var y = ParseDouble(geometry?.Attribute("y"));
            var w = ParseDouble(geometry?.Attribute("width"));
            var h = ParseDouble(geometry?.Attribute("height"));
            var rawLabel = _labels.GetValueOrDefault(id) ?? "(ohne Beschreibung)";
            var pathName = isPathStart ? rawLabel[PathStartPrefix.Length..].Trim() : null;
            var label = isDecisionPoint ? "Abzweigung" : isPathStart ? $"↳ {pathName}" : rawLabel;

            nodes.Add(new PreviewNode(id, label, x, y, w, h, id == _cursorNodeId, isDecisionPoint, isPathStart, PathName: pathName));
        }

        var edges = _root.Elements("mxCell")
            .Where(c => (string?)c.Attribute("edge") == "1")
            .Select(c => new PreviewEdge((string?)c.Attribute("source") ?? "", (string?)c.Attribute("target") ?? ""))
            .Where(e => e.FromId.Length > 0 && e.ToId.Length > 0)
            .ToList();

        return FlowPreviewBranching.TagBranches(new FlowPreview(nodes, edges));
    }

    /// <summary>Jumps the cursor to an arbitrary existing card/decision-point/path-start cell, opening a new column — for regular nodes; decision points are handled separately by the Ablauf-Übersicht's path popup (<see cref="StartNewPath"/>/<see cref="ContinuePath"/>).</summary>
    public BranchActionResult JumpToNode(string nodeId)
    {
        var cell = _root.Elements("mxCell").FirstOrDefault(c => (string?)c.Attribute("id") == nodeId);
        var isMarkerLike = IsDecisionPointId(nodeId) || IsPathStartId(nodeId);
        if (cell is null || !(isMarkerLike || IsCardCell(cell)))
        {
            return new BranchActionResult(false);
        }

        var geometry = cell.Element("mxGeometry");
        var y = ParseDouble(geometry?.Attribute("y"));
        var height = ParseDouble(geometry?.Attribute("height"));

        _nextColumnX += CardWidth + BranchColumnSpacing;
        SetCursor(nodeId, _nextColumnX, y, isMarkerLike ? MarkerHeight : height);

        return new BranchActionResult(true);
    }

    /// <summary>Moves the cursor and re-derives which path (if any) it's now inside, by walking backward through edges to the nearest path-start ancestor — see <see cref="_currentPathStartId"/>.</summary>
    private void SetCursor(string nodeId, double x, double y, double height)
    {
        _cursorNodeId = nodeId;
        _cursorX = x;
        _cursorY = y;
        _lastCardHeight = height;
        _currentPathStartId = FindOwningPathStart(nodeId);
    }

    private string? FindOwningPathStart(string nodeId)
    {
        var currentId = nodeId;
        var guard = 0;
        while (guard++ < 10_000) // defensive: a real flow never has cycles, but never hang if one somehow existed
        {
            if (IsPathStartId(currentId))
            {
                return currentId;
            }

            var inboundEdge = _root.Elements("mxCell")
                .FirstOrDefault(c => (string?)c.Attribute("edge") == "1" && (string?)c.Attribute("target") == currentId);
            var sourceId = (string?)inboundEdge?.Attribute("source");
            if (sourceId is null)
            {
                return null;
            }

            currentId = sourceId;
        }

        return null;
    }

    private string GetAccentColor() => _currentPathStartId is { } id
        ? BranchColors[Math.Abs(id.GetHashCode()) % BranchColors.Length]
        : MainColor;

    /// <summary>
    /// Builds one "card": a rounded, shadowed container with a numbered
    /// badge, a caption, and the screenshot (scaled to fit, aspect
    /// preserved, never distorted) as child cells — a real mxGraph group,
    /// so dragging the container in draw.io moves all three together
    /// instead of leaving the caption behind. Returns the card's total
    /// height, since it grows with caption length.
    /// </summary>
    private double BuildCard(string cardId, double x, double y, int stepNumber, string description, Bitmap screenshot, string accent)
    {
        var lines = Math.Max(1, Math.Ceiling(description.Length / CharsPerLine));
        var headerHeight = Math.Max(MinHeaderHeight, 16 + lines * LineHeight);
        var cardHeight = headerHeight + CardMargin + ImageAreaHeight + CardMargin;

        var container = new XElement("mxCell",
            new XAttribute("id", cardId),
            new XAttribute("value", ""),
            new XAttribute("style",
                $"rounded=1;arcSize=6;whiteSpace=wrap;html=1;fillColor=#FFFFFF;strokeColor={accent};" +
                $"strokeWidth=2;shadow=1;container=1;collapsible=0;connectable=1;"),
            new XAttribute("vertex", "1"),
            new XAttribute("parent", "1"),
            new XElement("mxGeometry",
                new XAttribute("x", Fmt(x)), new XAttribute("y", Fmt(y)),
                new XAttribute("width", Fmt(CardWidth)), new XAttribute("height", Fmt(cardHeight)),
                new XAttribute("as", "geometry")));
        _root.Add(container);

        var badge = new XElement("mxCell",
            new XAttribute("id", cardId + "_badge"),
            new XAttribute("value", stepNumber.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("style",
                $"ellipse;whiteSpace=wrap;html=1;fillColor={accent};strokeColor=none;" +
                $"fontColor=#FFFFFF;fontStyle=1;fontSize=13;align=center;verticalAlign=middle;"),
            new XAttribute("vertex", "1"),
            new XAttribute("parent", cardId),
            new XElement("mxGeometry",
                new XAttribute("x", Fmt(CardMargin)), new XAttribute("y", Fmt((headerHeight - BadgeSize) / 2)),
                new XAttribute("width", Fmt(BadgeSize)), new XAttribute("height", Fmt(BadgeSize)),
                new XAttribute("as", "geometry")));
        _root.Add(badge);

        var labelCell = new XElement("mxCell",
            new XAttribute("id", cardId + "_label"),
            new XAttribute("value", description),
            new XAttribute("style",
                "text;html=1;strokeColor=none;fillColor=none;align=left;verticalAlign=middle;" +
                "whiteSpace=wrap;fontSize=13;fontStyle=1;fontColor=#111827;spacingLeft=4;"),
            new XAttribute("vertex", "1"),
            new XAttribute("parent", cardId),
            new XElement("mxGeometry",
                new XAttribute("x", Fmt(CardMargin * 2 + BadgeSize)), new XAttribute("y", Fmt(4)),
                new XAttribute("width", Fmt(CardWidth - CardMargin * 3 - BadgeSize)), new XAttribute("height", Fmt(headerHeight - 8)),
                new XAttribute("as", "geometry")));
        _root.Add(labelCell);

        var contentWidth = CardWidth - CardMargin * 2;
        var scale = Math.Min(contentWidth / screenshot.Width, ImageAreaHeight / screenshot.Height);
        var imgWidth = screenshot.Width * scale;
        var imgHeight = screenshot.Height * scale;
        var imgX = (CardWidth - imgWidth) / 2;
        var imgY = headerHeight + CardMargin + (ImageAreaHeight - imgHeight) / 2;

        var base64 = ToBase64Png(screenshot);

        // The card only ever shows the screenshot shrunk to fit — far too
        // small to read text/UI details in. The embedded image data is
        // still the original full resolution. A "link" (see below) needs a
        // non-obvious click on a small hover icon to follow in draw.io, so
        // the *primary* zoom mechanism is a plain hover: draw.io renders a
        // cell's "tooltip" attribute as HTML, so an <img> tag in there
        // shows a much larger rendition immediately on mouseover — no
        // click, nothing to discover. The link is kept as a secondary path
        // to the truly full-resolution image in a new tab, for the rare
        // case even the tooltip-sized preview isn't big enough.
        var tooltipImgWidth = Math.Min(screenshot.Width, 640);
        var tooltipHtml = $"<img src=\"data:image/png;base64,{base64}\" width=\"{Fmt(tooltipImgWidth)}\">";

        var imageMxCell = new XElement("mxCell",
            new XAttribute("style", $"shape=image;imageAspect=1;image=data:image/png,{base64};"),
            new XAttribute("vertex", "1"),
            new XAttribute("parent", cardId),
            new XElement("mxGeometry",
                new XAttribute("x", Fmt(imgX)), new XAttribute("y", Fmt(imgY)),
                new XAttribute("width", Fmt(imgWidth)), new XAttribute("height", Fmt(imgHeight)),
                new XAttribute("as", "geometry")));

        var imageCell = new XElement("UserObject",
            new XAttribute("id", cardId + "_img"),
            new XAttribute("label", ""),
            new XAttribute("link", $"data:image/png;base64,{base64}"),
            new XAttribute("tooltip", tooltipHtml),
            imageMxCell);
        _root.Add(imageCell);

        return cardHeight;
    }

    private void AddEdge(string sourceId, string targetId, string accent)
    {
        var edge = new XElement("mxCell",
            new XAttribute("id", "edge_" + Guid.NewGuid().ToString("N")),
            new XAttribute("style",
                $"edgeStyle=orthogonalEdgeStyle;rounded=1;arcSize=6;html=1;strokeColor={accent};" +
                $"strokeWidth=2;endArrow=blockThin;endFill=1;startArrow=none;jettySize=auto;"),
            new XAttribute("edge", "1"),
            new XAttribute("parent", "1"),
            new XAttribute("source", sourceId),
            new XAttribute("target", targetId),
            new XElement("mxGeometry", new XAttribute("relative", "1"), new XAttribute("as", "geometry")));
        _root.Add(edge);
    }

    // A card's own id starts with CardIdPrefix, but so do its badge/label/
    // image children's ids ("card_<guid>_badge" etc., since they're
    // siblings at the XML root rather than nested under the container) —
    // an id-prefix check alone would treat those children as cards too.
    // The container style (unique to the outer card cell) disambiguates.
    private static bool IsCardCell(XElement cell) =>
        ((string?)cell.Attribute("id"))?.StartsWith(CardIdPrefix, StringComparison.Ordinal) == true
        && (((string?)cell.Attribute("style"))?.Contains("container=1") ?? false);

    private static bool IsDecisionPointId(string id) => id.StartsWith(DecisionPointIdPrefix, StringComparison.Ordinal);

    private static bool IsPathStartId(string id) => id.StartsWith(PathStartIdPrefix, StringComparison.Ordinal);

    private static string ToBase64Png(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        return Convert.ToBase64String(ms.ToArray());
    }

    // draw.io's XML must use invariant "." decimals regardless of the
    // machine's locale (e.g. de-DE uses "," by default), or the geometry
    // fails to parse when the file is opened.
    private static string Fmt(double value) => value.ToString(CultureInfo.InvariantCulture);

    private static double ParseDouble(XAttribute? attribute) =>
        attribute is not null && double.TryParse((string)attribute, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    /// <summary>Follows the single-child edge chain from <paramref name="startId"/> as far as it goes, resolving a path's current tip fresh from the graph every time (never cached, so it can never desync from what's actually in the file).</summary>
    private (string Id, double X, double Y, double Height, int Steps) FindBranchTip(string startId, double startX, double startY, double startHeight)
    {
        var currentId = startId;
        var currentX = startX;
        var currentY = startY;
        var currentHeight = startHeight;
        var steps = 0;
        while (true)
        {
            var edge = _root.Elements("mxCell")
                .FirstOrDefault(c => (string?)c.Attribute("edge") == "1" && (string?)c.Attribute("source") == currentId);
            var targetId = (string?)edge?.Attribute("target");
            var targetCell = targetId is null ? null : _root.Elements("mxCell").FirstOrDefault(c => (string?)c.Attribute("id") == targetId);
            if (targetCell is null)
            {
                return (currentId, currentX, currentY, currentHeight, steps);
            }

            var geometry = targetCell.Element("mxGeometry");
            currentId = targetId!;
            currentX = ParseDouble(geometry?.Attribute("x"));
            currentY = ParseDouble(geometry?.Attribute("y"));
            currentHeight = ParseDouble(geometry?.Attribute("height"));
            steps++;
        }
    }

    private static (XDocument Doc, XElement Root) LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                var doc = XDocument.Load(path);
                var root = doc.Root?.Element("diagram")?.Element("mxGraphModel")?.Element("root");
                if (root is not null)
                {
                    return (doc, root);
                }
            }
            catch (Exception ex)
            {
                LogService.Log($"drawio-Datei konnte nicht gelesen werden, beginne neu: {ex.Message}");
            }
        }

        return NewEmptyDocument();
    }

    private static (XDocument Doc, XElement Root) NewEmptyDocument()
    {
        var root = new XElement("root",
            new XElement("mxCell", new XAttribute("id", "0")),
            new XElement("mxCell", new XAttribute("id", "1"), new XAttribute("parent", "0")));

        var model = new XElement("mxGraphModel",
            new XAttribute("dx", "800"), new XAttribute("dy", "600"),
            new XAttribute("grid", "1"), new XAttribute("gridSize", "10"),
            new XAttribute("guides", "1"), new XAttribute("tooltips", "1"),
            new XAttribute("connect", "1"), new XAttribute("arrows", "1"),
            new XAttribute("fold", "1"), new XAttribute("page", "1"),
            new XAttribute("pageScale", "1"), new XAttribute("pageWidth", "850"),
            new XAttribute("pageHeight", "1100"), new XAttribute("math", "0"),
            new XAttribute("shadow", "0"),
            root);

        var diagram = new XElement("diagram",
            new XAttribute("id", Guid.NewGuid().ToString("N")),
            new XAttribute("name", "DocuClick"),
            model);

        var mxfile = new XElement("mxfile", new XAttribute("host", "app.diagrams.net"), diagram);
        return (new XDocument(mxfile), root);
    }

    // XDocument.Save(path) defaults to UTF-8 *with* a BOM. draw.io's file
    // loader apparently doesn't strip a leading BOM before checking for
    // "<mxfile"/"<mxGraphModel", so a BOM-prefixed file fails to open with
    // "Invalid file data" even though the XML itself is perfectly
    // well-formed — write UTF-8 without a BOM explicitly instead.
    private static readonly XmlWriterSettings SaveSettings = new()
    {
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        Indent = false
    };

    private void Save()
    {
        using var writer = XmlWriter.Create(_filePath!, SaveSettings);
        _doc.Save(writer);
    }
}
