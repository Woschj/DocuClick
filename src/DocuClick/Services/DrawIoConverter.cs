using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace DocuClick.Services;

/// <summary>
/// One-shot export: reads an already-recorded Canvas-mode session and produces an
/// equivalent .drawio flowchart — cards, decision points, path columns and
/// all — using the exact same visual building blocks draw.io mode used to
/// build live (rounded card containers with a numbered badge/caption/
/// embedded screenshot, gray rhombus decision points, colored path-start
/// markers, accent-colored edges per path).
///
/// draw.io is no longer a *live* recording mode (see IFlowWriter's
/// implementers: Canvas and the plain Note writer only) — draw.io mode
/// embeds every screenshot as inline base64 and rewrites the whole file on
/// every single click, which measurably slows down (confirmed via harness:
/// ~127ms/click at a 47MB session) as a recording grows, and that per-click
/// cost is also what any branch action blocks the UI thread on. Canvas mode
/// has none of this — screenshots save as separate files, so its own file
/// stays tiny and every action stays fast regardless of session length.
/// Recording only ever happens in Canvas now; a draw.io export is always
/// available afterward (from the tray menu), built in one pass instead of
/// paying a growing per-click cost throughout the whole session.
/// </summary>
public static class DrawIoConverter
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

    private const string DecisionPointColor = "#6B7280"; // neutral gray — decision points aren't tied to any one path's color
    private const string MainColor = "#2563EB";
    private static readonly string[] BranchColors =
    {
        "#D97706", "#059669", "#DB2777", "#7C3AED", "#DC2626", "#0891B2"
    };

    /// <summary>
    /// Converts <paramref name="canvasFilePath"/> into a new .drawio file at
    /// <paramref name="drawioFilePath"/> (overwritten if it already exists).
    /// <paramref name="vaultPath"/> resolves each card's screenshot — Canvas
    /// mode stores those as separate files, referenced by vault-relative
    /// path from the node's own "file" property.
    /// </summary>
    public static void Convert(string canvasFilePath, string vaultPath, string drawioFilePath)
    {
        var canvas = CanvasDocumentIo.Load(canvasFilePath);

        var (doc, root) = NewEmptyDocument();

        var textNodes = canvas.Nodes.Where(n => n.Type == "text").ToDictionary(n => n.Id);
        var structuralEdges = canvas.Edges.Where(e => !e.Manual && textNodes.ContainsKey(e.FromNode) && textNodes.ContainsKey(e.ToNode)).ToList();
        var manualEdges = canvas.Edges.Where(e => e.Manual && textNodes.ContainsKey(e.FromNode) && textNodes.ContainsKey(e.ToNode)).ToList();

        // Same grouping IFlowWriter.GetPreview() already uses for the
        // Ablauf-Übersicht minimap — propagates each path-start's identity
        // forward through everything reachable from it, so this converter
        // doesn't need its own separate branch-walking logic.
        var previewNodes = textNodes.Values.Select(n => new PreviewNode(
            n.Id, n.Text ?? "", n.X, n.Y, n.Width, n.Height,
            IsCurrent: false,
            IsDecisionPoint: n.Text == CanvasFlowWriter.DecisionPointLabel,
            IsPathStart: n.Text?.StartsWith(CanvasFlowWriter.PathStartPrefix, StringComparison.Ordinal) ?? false)).ToList();
        var previewEdges = structuralEdges.Select(e => new PreviewEdge(e.FromNode, e.ToNode)).ToList();
        var tagged = FlowPreviewBranching.TagBranches(new FlowPreview(previewNodes, previewEdges));

        // Same (row, column) grid the Ablauf-Übersicht's minimap and
        // CanvasFlowWriter's own relayout use — this converter used to have
        // its own separate, simpler column-assignment pass (plain "next slot
        // in a global sequence"), which was vulnerable to the exact same
        // "two forks off the same node land on opposite sides of the
        // diagram, with a connector line cutting straight through an
        // unrelated branch" bug already found and fixed for the minimap/
        // Canvas file — it just hadn't shown up here yet. Reusing the one
        // shared, origin-anchored algorithm fixes that for the draw.io
        // export too, instead of every consumer needing its own separately
        // maintained (and separately buggy) copy.
        var slotOf = FlowPreviewBranching.ComputeGridLayout(tagged);

        var rowHeight = new Dictionary<int, double>();
        foreach (var node in tagged.Nodes)
        {
            var (row, _) = slotOf[node.Id];
            var height = node.IsDecisionPoint || node.IsPathStart ? MarkerHeight : CardHeightFor(node.Label);
            rowHeight[row] = Math.Max(rowHeight.GetValueOrDefault(row), height);
        }

        var rowY = new Dictionary<int, double>();
        var y = 0.0;
        foreach (var row in rowHeight.Keys.OrderBy(r => r))
        {
            rowY[row] = y;
            y += rowHeight[row] + SequentialSpacing;
        }

        var idMap = new Dictionary<string, string>(); // canvas node id -> new drawio cell id
        var stepCounter = 0;

        // Every node's position is already fully determined by its own
        // (row, column) — no walk/queue bookkeeping needed to place cells
        // anymore, just one pass over every node.
        foreach (var node in tagged.Nodes)
        {
            var (row, column) = slotOf[node.Id];
            var x = column * (CardWidth + BranchColumnSpacing);
            var cellY = rowY[row];
            var canvasNode = textNodes[node.Id];
            var accent = AccentFor(column);

            string cellId;
            if (node.IsDecisionPoint)
            {
                cellId = BuildDecisionMarker(root, x, cellY, canvasNode.Text!);
            }
            else if (node.IsPathStart)
            {
                cellId = BuildPathStartMarker(root, x, cellY, canvasNode.Text!, accent);
            }
            else
            {
                var screenshotPath = FindScreenshotPath(canvas, canvasNode, vaultPath);
                using var screenshot = screenshotPath is not null && File.Exists(screenshotPath)
                    ? new Bitmap(screenshotPath)
                    : new Bitmap(1, 1); // missing/moved attachment — still export the card, just without a real image
                cellId = BuildCard(root, x, cellY, ++stepCounter, canvasNode.Text ?? "", screenshot, accent);
            }

            idMap[node.Id] = cellId;
        }

        // Gray into a decision point (matches MarkDecisionPoint live); the
        // target's own path color everywhere else, including a decision
        // point's own outgoing edges to each forked path (matches
        // StartNewPath live — the *new* path's color, not the decision
        // point's gray).
        foreach (var edge in structuralEdges)
        {
            var targetIsDecisionPoint = textNodes[edge.ToNode].Text == CanvasFlowWriter.DecisionPointLabel;
            var (_, targetColumn) = slotOf[edge.ToNode];
            AddEdge(root, idMap[edge.FromNode], idMap[edge.ToNode], targetIsDecisionPoint ? DecisionPointColor : AccentFor(targetColumn));
        }

        // Manual cross-connects last, once every card/marker has a cell —
        // same neutral gray IFlowWriter.ConnectNodes already uses live, to
        // visually read as "not part of the recorded sequence" the same way.
        foreach (var edge in manualEdges)
        {
            if (idMap.TryGetValue(edge.FromNode, out var fromCellId) && idMap.TryGetValue(edge.ToNode, out var toCellId))
            {
                AddEdge(root, fromCellId, toCellId, DecisionPointColor, manual: true);
            }
        }

        Save(doc, drawioFilePath);
    }

    private static string AccentFor(int column) =>
        column == 0 ? MainColor : BranchColors[FlowPreviewBranching.StableColumnHash(column) % BranchColors.Length];

    private static string? FindScreenshotPath(CanvasDocument canvas, CanvasNode textNode, string vaultPath)
    {
        var imageSibling = canvas.Nodes.FirstOrDefault(n =>
            n.Type == "file" && Math.Abs(n.X - textNode.X) < 0.5 && Math.Abs(n.Y - (textNode.Y + CanvasFlowWriter.TextNodeHeight + CanvasFlowWriter.TextToImageGap)) < 0.5);
        return imageSibling?.File is { } relativePath ? Path.Combine(vaultPath, relativePath) : null;
    }

    private static string BuildDecisionMarker(XElement root, double x, double y, string label)
    {
        var id = "decision_" + Guid.NewGuid().ToString("N");
        var markerX = x + (CardWidth - MarkerWidth) / 2;
        root.Add(new XElement("mxCell",
            new XAttribute("id", id),
            new XAttribute("value", label),
            new XAttribute("style",
                $"rhombus;whiteSpace=wrap;html=1;fillColor=#F3F4F6;strokeColor={DecisionPointColor};strokeWidth=2;" +
                $"fontColor=#1F2937;fontStyle=1;fontSize=12;arcSize=4;"),
            new XAttribute("vertex", "1"),
            new XAttribute("parent", "1"),
            new XElement("mxGeometry",
                new XAttribute("x", Fmt(markerX)), new XAttribute("y", Fmt(y)),
                new XAttribute("width", Fmt(MarkerWidth)), new XAttribute("height", Fmt(MarkerHeight)),
                new XAttribute("as", "geometry"))));
        return id;
    }

    private static string BuildPathStartMarker(XElement root, double x, double y, string label, string color)
    {
        var id = "pathstart_" + Guid.NewGuid().ToString("N");
        var markerX = x + (CardWidth - MarkerWidth) / 2;
        root.Add(new XElement("mxCell",
            new XAttribute("id", id),
            new XAttribute("value", label),
            new XAttribute("style",
                $"rounded=1;arcSize=30;whiteSpace=wrap;html=1;fillColor=#F0FDF4;strokeColor={color};strokeWidth=2;" +
                $"fontColor=#14532D;fontStyle=1;fontSize=12;"),
            new XAttribute("vertex", "1"),
            new XAttribute("parent", "1"),
            new XElement("mxGeometry",
                new XAttribute("x", Fmt(markerX)), new XAttribute("y", Fmt(y)),
                new XAttribute("width", Fmt(MarkerWidth)), new XAttribute("height", Fmt(MarkerHeight)),
                new XAttribute("as", "geometry"))));
        return id;
    }

    /// <summary>Header strip height for a card's description text — split out so the row-height precompute in Convert() and BuildCard's own layout always agree on the same number.</summary>
    private static double HeaderHeightFor(string description)
    {
        var lines = Math.Max(1, Math.Ceiling(description.Length / CharsPerLine));
        return Math.Max(MinHeaderHeight, 16 + lines * LineHeight);
    }

    private static double CardHeightFor(string description) =>
        HeaderHeightFor(description) + CardMargin + ImageAreaHeight + CardMargin;

    /// <summary>Builds one card — same shape as the live draw.io writer used to (rounded container + numbered badge + caption + embedded screenshot).</summary>
    private static string BuildCard(XElement root, double x, double y, int stepNumber, string description, Bitmap screenshot, string accent)
    {
        var cardId = "card_" + Guid.NewGuid().ToString("N");
        var headerHeight = HeaderHeightFor(description);
        var cardHeight = headerHeight + CardMargin + ImageAreaHeight + CardMargin;

        root.Add(new XElement("mxCell",
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
                new XAttribute("as", "geometry"))));

        root.Add(new XElement("mxCell",
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
                new XAttribute("as", "geometry"))));

        root.Add(new XElement("mxCell",
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
                new XAttribute("as", "geometry"))));

        var contentWidth = CardWidth - CardMargin * 2;
        var scale = Math.Min(contentWidth / screenshot.Width, ImageAreaHeight / screenshot.Height);
        var imgWidth = screenshot.Width * scale;
        var imgHeight = screenshot.Height * scale;
        var imgX = (CardWidth - imgWidth) / 2;
        var imgY = headerHeight + CardMargin + (ImageAreaHeight - imgHeight) / 2;

        var base64 = ToBase64Png(screenshot);
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

        root.Add(new XElement("UserObject",
            new XAttribute("id", cardId + "_img"),
            new XAttribute("label", ""),
            new XAttribute("link", $"data:image/png;base64,{base64}"),
            new XAttribute("tooltip", tooltipHtml),
            imageMxCell));

        return cardId;
    }

    private static void AddEdge(XElement root, string sourceId, string targetId, string accent, bool manual = false)
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
        if (manual)
        {
            edge.SetAttributeValue("docuClickManual", "1");
        }
        root.Add(edge);
    }

    private static string ToBase64Png(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        return System.Convert.ToBase64String(ms.ToArray());
    }

    // draw.io's XML must use invariant "." decimals regardless of the
    // machine's locale (e.g. de-DE uses "," by default), or the geometry
    // fails to parse when the file is opened.
    private static string Fmt(double value) => value.ToString(CultureInfo.InvariantCulture);

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

    private static void Save(XDocument doc, string path)
    {
        using var writer = XmlWriter.Create(path, SaveSettings);
        doc.Save(writer);
    }
}
