using System.IO;

namespace DocuClick.Services;

/// <summary>
/// One-shot export: reads an already-recorded Canvas-mode session and
/// produces a single, fully self-contained .html file — Cytoscape.js and
/// every screenshot embedded inline as base64, so it opens and pans/zooms
/// in any browser with no DocuClick, Obsidian, or draw.io installed, and no
/// dependency on a sibling Attachments folder (unlike the live session file
/// itself, which references screenshots by relative path to stay fast to
/// save — see CanvasFlowWriter.Save). Read-only (click a card to see its
/// screenshot full-size); editing still happens in the Ablauf-Übersicht
/// itself (see SessionManager.OpenForEditing) — this is purely for sharing
/// a finished result with someone who has none of this app's dependencies.
///
/// Reuses the exact same origin-anchored (row, column) layout as the
/// Ablauf-Übersicht's minimap and <see cref="DrawIoConverter"/>
/// (<see cref="FlowPreviewBranching.ComputeGridLayout"/>) rather than a
/// third, separately-maintained layout algorithm, and the same page
/// template as the live session file (<see cref="HtmlViewerBuilder"/>).
/// </summary>
public static class HtmlFlowExporter
{
    private const double CardWidth = 240;
    private const double CardHeight = 190;
    private const double MarkerWidth = 160;
    private const double MarkerHeight = 50;
    private const double SequentialSpacing = 70;
    private const double BranchColumnSpacing = 60;

    // Cytoscape renders a node's label *above* it (text-valign: top in
    // HtmlViewerBuilder's stylesheet), not inside its bounding box — that
    // space was never reserved in the row-height math below, so a long
    // (wrapped, multi-line) description visibly overlapped whatever sat in
    // the row above it (confirmed as a real bug from a live export). Labels
    // are truncated to bound this to at most ~2 lines, and this allowance
    // reserves room for exactly that, for every node uniformly.
    private const int MaxLabelChars = 70;
    private const double LabelAllowance = 46;

    private const string DecisionPointColor = "#6B7280";
    private const string MainColor = "#2563EB";
    private static readonly string[] BranchColors =
    {
        "#D97706", "#059669", "#DB2777", "#7C3AED", "#DC2626", "#0891B2"
    };

    public static void Convert(string sourceFilePath, string outputPath, string htmlFilePath)
    {
        var canvas = CanvasDocumentIo.Load(sourceFilePath);

        var textNodes = canvas.Nodes.Where(n => n.Type == "text").ToDictionary(n => n.Id);
        var structuralEdges = canvas.Edges.Where(e => !e.Manual && textNodes.ContainsKey(e.FromNode) && textNodes.ContainsKey(e.ToNode)).ToList();
        var manualEdges = canvas.Edges.Where(e => e.Manual && textNodes.ContainsKey(e.FromNode) && textNodes.ContainsKey(e.ToNode)).ToList();

        var previewNodes = textNodes.Values.Select(n => new PreviewNode(
            n.Id, n.Text ?? "", n.X, n.Y, n.Width, n.Height,
            IsCurrent: false,
            IsDecisionPoint: n.Text == CanvasFlowWriter.DecisionPointLabel,
            IsPathStart: n.Text?.StartsWith(CanvasFlowWriter.PathStartPrefix, StringComparison.Ordinal) ?? false)).ToList();
        var previewEdges = structuralEdges.Select(e => new PreviewEdge(e.FromNode, e.ToNode)).ToList();
        var tagged = FlowPreviewBranching.TagBranches(new FlowPreview(previewNodes, previewEdges));
        var slotOf = FlowPreviewBranching.ComputeGridLayout(tagged);

        var rowHeight = new Dictionary<int, double>();
        foreach (var node in tagged.Nodes)
        {
            var (row, _) = slotOf[node.Id];
            var height = (node.IsDecisionPoint || node.IsPathStart ? MarkerHeight : CardHeight) + LabelAllowance;
            rowHeight[row] = Math.Max(rowHeight.GetValueOrDefault(row), height);
        }

        var rowY = new Dictionary<int, double>();
        var y = 0.0;
        foreach (var row in rowHeight.Keys.OrderBy(r => r))
        {
            rowY[row] = y;
            y += rowHeight[row] + SequentialSpacing;
        }

        string AccentFor(int column) => column == 0 ? MainColor : BranchColors[FlowPreviewBranching.StableColumnHash(column) % BranchColors.Length];

        var nodeSpecs = new List<HtmlViewerBuilder.NodeSpec>();
        var stepCounter = 0;
        foreach (var node in tagged.Nodes)
        {
            var (row, column) = slotOf[node.Id];
            var x = column * (CardWidth + BranchColumnSpacing);
            var cellY = rowY[row];
            var canvasNode = textNodes[node.Id];
            var accent = AccentFor(column);

            if (node.IsDecisionPoint)
            {
                nodeSpecs.Add(new HtmlViewerBuilder.NodeSpec(
                    node.Id, "◆ Abzweigung", x + (CardWidth - MarkerWidth) / 2, cellY, DecisionPointColor, "diamond", null));
            }
            else if (node.IsPathStart)
            {
                nodeSpecs.Add(new HtmlViewerBuilder.NodeSpec(
                    node.Id, TruncateLabel(canvasNode.Text ?? ""), x + (CardWidth - MarkerWidth) / 2, cellY, accent, "round-rectangle", null));
            }
            else
            {
                stepCounter++;
                var screenshotPath = FindScreenshotPath(canvas, canvasNode, outputPath);
                string? imageDataUrl = null;
                if (screenshotPath is not null && File.Exists(screenshotPath))
                {
                    var bytes = File.ReadAllBytes(screenshotPath);
                    imageDataUrl = "data:image/png;base64," + System.Convert.ToBase64String(bytes);
                }

                nodeSpecs.Add(new HtmlViewerBuilder.NodeSpec(
                    node.Id, $"{stepCounter}. {TruncateLabel(canvasNode.Text ?? "")}", x, cellY, accent, "round-rectangle", imageDataUrl));
            }
        }

        var edgeSpecs = new List<HtmlViewerBuilder.EdgeSpec>();
        foreach (var edge in structuralEdges)
        {
            var targetIsDecisionPoint = textNodes[edge.ToNode].Text == CanvasFlowWriter.DecisionPointLabel;
            var (_, targetColumn) = slotOf[edge.ToNode];
            edgeSpecs.Add(new HtmlViewerBuilder.EdgeSpec(
                edge.FromNode, edge.ToNode, targetIsDecisionPoint ? DecisionPointColor : AccentFor(targetColumn), Manual: false));
        }

        // Manual cross-connects last, same neutral gray IFlowWriter.ConnectNodes
        // already uses live, to visually read as "not part of the recorded
        // sequence" the same way as in the Ablauf-Übersicht/draw.io export.
        foreach (var edge in manualEdges)
        {
            var customColor = !string.IsNullOrEmpty(edge.Color) ? edge.Color : "#2563EB";
            var lineStyle = !string.IsNullOrEmpty(edge.LineStyle) ? edge.LineStyle : "solid";
            edgeSpecs.Add(new HtmlViewerBuilder.EdgeSpec(edge.FromNode, edge.ToNode, customColor, Manual: true, LineStyle: lineStyle));
        }

        var title = Path.GetFileNameWithoutExtension(sourceFilePath);
        File.WriteAllText(htmlFilePath, HtmlViewerBuilder.BuildPage(title, nodeSpecs, edgeSpecs));
    }

    /// <summary>Bounds a label to roughly two wrapped lines at this page's font/width so <see cref="LabelAllowance"/> stays a valid, uniform reservation for every node — the full text is never lost, just not shown inline (visible again by opening the actual session file if ever needed).</summary>
    private static string TruncateLabel(string text) =>
        text.Length <= MaxLabelChars ? text : text[..MaxLabelChars].TrimEnd() + "…";

    private static string? FindScreenshotPath(CanvasDocument canvas, CanvasNode textNode, string outputPath)
    {
        var imageSibling = canvas.Nodes.FirstOrDefault(n =>
            n.Type == "file" && Math.Abs(n.X - textNode.X) < 0.5 && Math.Abs(n.Y - (textNode.Y + CanvasFlowWriter.TextNodeHeight + CanvasFlowWriter.TextToImageGap)) < 0.5);
        return imageSibling?.File is { } relativePath ? Path.Combine(outputPath, relativePath) : null;
    }
}
