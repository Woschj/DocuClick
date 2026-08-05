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
/// moves label and screenshot together as one unit. Branches get their
/// own accent color (cycled from a fixed palette, assigned in the order
/// they're first marked) so separate paths read apart at a glance, and
/// edges have real arrowheads colored to match the column they lead into.
///
/// Layout/branching semantics mirror <see cref="CanvasFlowWriter"/>
/// (vertical main flow, branches as new columns via a named anchor that
/// can be revisited any number of times) so behavior is consistent across
/// both graph-style output modes.
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
    private const string BranchMarkerPrefix = "Branch: ";
    private const string CardIdPrefix = "card_";
    private const string MarkerIdPrefix = "marker_";

    private const string MainColor = "#2563EB";
    private static readonly string[] BranchColors =
    {
        "#D97706", "#059669", "#DB2777", "#7C3AED", "#DC2626", "#0891B2"
    };

    private sealed record BranchAnchor(string Name, string NodeId, double X, double Y, string Color);

    private readonly AppConfig _config;

    private string? _filePath;
    private XDocument _doc;
    private XElement _root;

    private string? _cursorNodeId;
    private double _cursorX;
    private double _cursorY;
    private double _nextColumnX;
    private string? _currentBranchName;
    private int _stepCounter;

    // Tracks the most recently built card's actual height (varies with
    // caption length) so the *next* card's Y position never overlaps it —
    // a fixed per-card height would either waste space or clip long
    // captions.
    private double _lastCardHeight = ImageAreaHeight + MinHeaderHeight + CardMargin;

    private readonly Dictionary<string, string> _labels = new();
    private readonly List<BranchAnchor> _branchAnchors = new();
    private (string NodeId, double X, double Y)? _pendingResumeAnchor;

    public DrawIoFlowWriter(AppConfig config)
    {
        _config = config;
        (_doc, _root) = NewEmptyDocument();
    }

    public int BranchDepth => _branchAnchors.Count;

    public string? CurrentBranchName => _currentBranchName;

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
        _branchAnchors.Clear();
        _currentBranchName = null;
        _stepCounter = 0;

        foreach (var cell in _root.Elements("mxCell"))
        {
            var id = (string?)cell.Attribute("id");
            if (id is null || !IsCardCell(cell))
            {
                continue;
            }

            _stepCounter++;
            var labelCell = _root.Elements("mxCell").FirstOrDefault(c => (string?)c.Attribute("id") == id + "_label");
            var label = (string?)labelCell?.Attribute("value");
            if (!string.IsNullOrEmpty(label))
            {
                _labels[id] = label;
            }
        }

        // Rebuild branch anchors by scanning for their marker cells so a
        // Stop()/Start() cycle on the same file doesn't lose them. Colors
        // are re-derived from scan order (not stored), which is stable
        // because markers are always encountered in the same relative
        // order across scans.
        var branchOrder = 0;
        foreach (var cell in _root.Elements("mxCell")
            .Where(c => ((string?)c.Attribute("id"))?.StartsWith(MarkerIdPrefix, StringComparison.Ordinal) ?? false))
        {
            var id = (string)cell.Attribute("id")!;
            var value = (string?)cell.Attribute("value") ?? "";
            if (!value.StartsWith(BranchMarkerPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var branchName = value[BranchMarkerPrefix.Length..].Trim();
            if (branchName.Length == 0)
            {
                continue;
            }

            var geometry = cell.Element("mxGeometry");
            var x = ParseDouble(geometry?.Attribute("x"));
            var y = ParseDouble(geometry?.Attribute("y"));
            var color = BranchColors[branchOrder % BranchColors.Length];
            branchOrder++;

            _labels[id] = value;
            AddOrReplaceAnchor(new BranchAnchor(branchName, id, x, y, color));
        }

        _nextColumnX = _root.Elements("mxCell")
            .Select(c => (double?)c.Element("mxGeometry")?.Attribute("x"))
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .DefaultIfEmpty(-CardWidth - BranchColumnSpacing)
            .Max() + CardWidth + BranchColumnSpacing;

        if (_pendingResumeAnchor is { } resume && _root.Elements("mxCell").Any(c => (string?)c.Attribute("id") == resume.NodeId))
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
            throw new InvalidOperationException("draw.io-Session wurde nicht gestartet.");
        }

        var newY = _cursorNodeId is null ? _cursorY : _cursorY + _lastCardHeight + SequentialSpacing;
        var cardId = CardIdPrefix + Guid.NewGuid().ToString("N");
        var accent = GetAccentColor(_currentBranchName);

        var cardHeight = BuildCard(cardId, _cursorX, newY, ++_stepCounter, description, screenshot, accent);
        _lastCardHeight = cardHeight;

        if (_cursorNodeId is not null)
        {
            AddEdge(_cursorNodeId, cardId, accent);
        }

        _labels[cardId] = description;
        _cursorNodeId = cardId;
        _cursorY = newY;

        Save();
    }

    /// <summary>
    /// Adds a small, visible "Branch: &lt;name&gt;" diamond marker
    /// connected from the current card — a real, recognizable node in the
    /// file (not hidden metadata), so it survives a Stop()/Start() cycle
    /// (see StartSession). Doesn't move the cursor; only
    /// <see cref="JumpToAnchor"/> actually jumps to a marker. Re-marking an
    /// existing name adds a fresh marker (the newest one wins on reload).
    /// </summary>
    public BranchActionResult MarkBranchAnchor(string branchName)
    {
        if (_cursorNodeId is null)
        {
            return new BranchActionResult(false, _branchAnchors.Count, null);
        }

        var markerY = _cursorY + _lastCardHeight + SequentialSpacing;
        var markerId = MarkerIdPrefix + Guid.NewGuid().ToString("N");
        var color = BranchColors[_branchAnchors.Count % BranchColors.Length];

        var marker = new XElement("mxCell",
            new XAttribute("id", markerId),
            new XAttribute("value", $"{BranchMarkerPrefix}{branchName}"),
            new XAttribute("style",
                $"rhombus;whiteSpace=wrap;html=1;fillColor=#F5F3FF;strokeColor={color};strokeWidth=2;" +
                $"fontColor=#3B0764;fontStyle=1;fontSize=12;arcSize=4;"),
            new XAttribute("vertex", "1"),
            new XAttribute("parent", "1"),
            new XElement("mxGeometry",
                new XAttribute("x", Fmt(_cursorX + (CardWidth - MarkerWidth) / 2)),
                new XAttribute("y", Fmt(markerY)),
                new XAttribute("width", Fmt(MarkerWidth)),
                new XAttribute("height", Fmt(MarkerHeight)),
                new XAttribute("as", "geometry")));
        _root.Add(marker);

        AddEdge(_cursorNodeId, markerId, GetAccentColor(_currentBranchName));

        _labels[markerId] = $"{BranchMarkerPrefix}{branchName}";
        AddOrReplaceAnchor(new BranchAnchor(branchName, markerId, _cursorX + (CardWidth - MarkerWidth) / 2, markerY, color));
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

        _nextColumnX += CardWidth + BranchColumnSpacing;
        _cursorNodeId = anchor.NodeId;
        _cursorX = _nextColumnX;
        _cursorY = anchor.Y;
        _currentBranchName = branchName;
        _lastCardHeight = MarkerHeight;

        return new BranchActionResult(true, _branchAnchors.Count, branchName);
    }

    private string GetAccentColor(string? branchName)
    {
        if (branchName is null)
        {
            return MainColor;
        }

        var anchor = _branchAnchors.FirstOrDefault(a => a.Name == branchName);
        return anchor?.Color ?? BranchColors[_branchAnchors.Count % BranchColors.Length];
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
        // still the original full resolution, so wrapping the image cell
        // in a UserObject with a "link" pointing at that same data (as a
        // *proper* "data:image/png;base64,..." URI this time — unlike the
        // comma form used in the style attribute above, this one is parsed
        // by the browser/OS as a real URL, not mxGraph's style splitter)
        // lets a click open it at full native resolution in a new tab/
        // viewer — "zoom in" without bloating the diagram's default view.
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
            new XAttribute("tooltip", "Klicken zum Vergrößern (Screenshot in Originalgröße)"),
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
