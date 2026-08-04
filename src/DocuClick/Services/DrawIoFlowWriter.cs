using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Xml.Linq;

namespace DocuClick.Services;

/// <summary>
/// Writes clicks as a draw.io / diagrams.net (.drawio) flowchart — plain
/// mxGraph XML, no plugin or draw.io installation needed to produce it.
/// Screenshots are embedded directly as base64 PNG data URIs in each
/// image cell's style, so the file is fully self-contained (no separate
/// attachments folder for this mode).
///
/// draw.io can re-export a .drawio file to real Visio (.vsdx) via
/// File → Export as → VSDX, which is the recommended path if a Visio
/// file is actually needed — hand-rolling VSDX here would be far riskier.
///
/// Layout/branching semantics mirror <see cref="CanvasFlowWriter"/>
/// exactly (vertical main flow, branches as new columns via a one-shot
/// anchor stack) so behavior is consistent across both graph modes.
/// </summary>
public sealed class DrawIoFlowWriter : IFlowWriter
{
    private const double NodeWidth = 380;
    private const double LabelHeight = 30;
    private const double ImageHeight = 300;
    private const double SequentialSpacing = 40;
    private const double BranchColumnSpacing = 80;

    private readonly AppConfig _config;

    private string? _filePath;
    private XDocument _doc;
    private XElement _root;

    private string? _cursorNodeId;
    private double _cursorX;
    private double _cursorY;
    private double _nextColumnX;
    private readonly Dictionary<string, string> _labels = new();
    private readonly Stack<(string NodeId, double X, double Y)> _branchAnchors = new();
    private (string NodeId, double X, double Y)? _pendingResumeAnchor;

    public DrawIoFlowWriter(AppConfig config)
    {
        _config = config;
        (_doc, _root) = NewEmptyDocument();
    }

    public int BranchDepth => _branchAnchors.Count;

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
            var style = (string?)cell.Attribute("style") ?? "";
            if (id is null || !style.StartsWith("shape=image", StringComparison.Ordinal))
            {
                continue;
            }

            var labelCell = root.Elements("mxCell").FirstOrDefault(c => (string?)c.Attribute("id") == id + "_label");
            var label = labelCell is not null
                ? WebUtility.HtmlDecode((string?)labelCell.Attribute("value") ?? "")
                : "(ohne Beschreibung)";

            var geometry = cell.Element("mxGeometry");
            var x = ParseDouble(geometry?.Attribute("x"));
            var y = ParseDouble(geometry?.Attribute("y")) - LabelHeight;

            result.Add(new ResumableNode(id, label, x, y));
        }

        return result.OrderBy(n => n.Y).ThenBy(n => n.X).ToList();
    }

    public void SetResumeAnchor(ResumableNode node) => _pendingResumeAnchor = (node.Id, node.X, node.Y);

    public void StartSession(string fileName)
    {
        if (string.IsNullOrWhiteSpace(_config.VaultPath))
        {
            throw new InvalidOperationException("Kein Obsidian-Vault-Pfad konfiguriert.");
        }

        _filePath = Path.Combine(_config.VaultPath, fileName);
        (_doc, _root) = LoadOrCreate(_filePath);

        _labels.Clear();
        foreach (var cell in _root.Elements("mxCell"))
        {
            var id = (string?)cell.Attribute("id");
            var value = (string?)cell.Attribute("value");
            if (id is not null && !string.IsNullOrEmpty(value))
            {
                _labels[id] = WebUtility.HtmlDecode(value);
            }
        }

        _nextColumnX = _root.Elements("mxCell")
            .Select(c => (double?)c.Element("mxGeometry")?.Attribute("x"))
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .DefaultIfEmpty(-NodeWidth - BranchColumnSpacing)
            .Max() + NodeWidth + BranchColumnSpacing;

        _branchAnchors.Clear();

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
    }

    public void AddClickNode(string description, Bitmap screenshot, DateTime timestamp)
    {
        if (_filePath is null)
        {
            throw new InvalidOperationException("draw.io-Session wurde nicht gestartet.");
        }

        var imageBase64 = ToBase64Png(screenshot);
        var newY = _cursorNodeId is null ? _cursorY : _cursorY + LabelHeight + ImageHeight + SequentialSpacing;
        var nodeId = "n" + Guid.NewGuid().ToString("N");

        AddNodeAndEdge(nodeId, _cursorX, newY, description, imageBase64, connectFromNodeId: _cursorNodeId);

        _cursorNodeId = nodeId;
        _cursorY = newY;
        Save();
    }

    public BranchActionResult MarkBranchAnchor()
    {
        if (_cursorNodeId is null)
        {
            return new BranchActionResult(false, _branchAnchors.Count, null);
        }

        _branchAnchors.Push((_cursorNodeId, _cursorX, _cursorY));

        var imageCell = _root.Elements("mxCell").FirstOrDefault(c => (string?)c.Attribute("id") == _cursorNodeId);
        if (imageCell is not null)
        {
            var style = (string?)imageCell.Attribute("style") ?? "";
            if (!style.Contains("strokeColor=", StringComparison.Ordinal))
            {
                imageCell.SetAttributeValue("style", style + "strokeColor=#7C3AED;strokeWidth=3;");
                Save();
            }
        }

        return new BranchActionResult(true, _branchAnchors.Count, CurrentNodeLabel ?? "(ohne Beschreibung)");
    }

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

        return new BranchActionResult(true, _branchAnchors.Count, _labels.GetValueOrDefault(anchor.NodeId, "(ohne Beschreibung)"));
    }

    private void AddNodeAndEdge(string nodeId, double x, double y, string label, string imageBase64, string? connectFromNodeId)
    {
        var labelCell = new XElement("mxCell",
            new XAttribute("id", nodeId + "_label"),
            new XAttribute("value", WebUtility.HtmlEncode(label)),
            new XAttribute("style", "text;html=1;strokeColor=none;fillColor=none;align=left;verticalAlign=top;whiteSpace=wrap;spacingLeft=2;"),
            new XAttribute("vertex", "1"),
            new XAttribute("parent", "1"),
            new XElement("mxGeometry",
                new XAttribute("x", Fmt(x)), new XAttribute("y", Fmt(y)),
                new XAttribute("width", Fmt(NodeWidth)), new XAttribute("height", Fmt(LabelHeight)),
                new XAttribute("as", "geometry")));

        var imageCell = new XElement("mxCell",
            new XAttribute("id", nodeId),
            new XAttribute("value", ""),
            new XAttribute("style", $"shape=image;imageAspect=0;image=data:image/png;base64,{imageBase64};"),
            new XAttribute("vertex", "1"),
            new XAttribute("parent", "1"),
            new XElement("mxGeometry",
                new XAttribute("x", Fmt(x)), new XAttribute("y", Fmt(y + LabelHeight)),
                new XAttribute("width", Fmt(NodeWidth)), new XAttribute("height", Fmt(ImageHeight)),
                new XAttribute("as", "geometry")));

        _root.Add(labelCell);
        _root.Add(imageCell);
        _labels[nodeId] = label;

        if (connectFromNodeId is not null)
        {
            var edge = new XElement("mxCell",
                new XAttribute("id", nodeId + "_edge"),
                new XAttribute("style", "edgeStyle=orthogonalEdgeStyle;rounded=0;"),
                new XAttribute("edge", "1"),
                new XAttribute("parent", "1"),
                new XAttribute("source", connectFromNodeId),
                new XAttribute("target", nodeId + "_label"),
                new XElement("mxGeometry", new XAttribute("relative", "1"), new XAttribute("as", "geometry")));
            _root.Add(edge);
        }
    }

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

    private void Save() => _doc.Save(_filePath!);
}
