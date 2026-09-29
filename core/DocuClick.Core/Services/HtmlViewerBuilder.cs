using System.IO;
using System.Text.Json;

namespace DocuClick.Services;

/// <summary>
/// Builds the actual interactive HTML page (Cytoscape.js graph, pan/zoom,
/// click-to-enlarge screenshot) for <see cref="CanvasFlowWriter"/>'s live,
/// on-disk session file — embeds the raw node/edge JSON so it can be read
/// back and edited again (see <see cref="CanvasDocumentIo"/>), and ships
/// its own editor. Browser edits save through DocuClick's local service or
/// as a downloaded copy; the Obsidian plugin supplies its own save host.
/// </summary>
public static class HtmlViewerBuilder
{
    // Lazily read once and cached for the process's lifetime — this file is
    // ~365 KB, and BuildPage runs on *every single click* while a live
    // session is recording (CanvasFlowWriter.Save() calls it every time);
    // re-reading it from disk that often was a real, measured per-click
    // cost completely independent of session size. The vendor file never
    // changes at runtime, so there's nothing to invalidate this cache for.
    private static readonly Lazy<string> CytoscapeJs = new(() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "WebAssets", "vendor", "cytoscape.min.js")));

    private static readonly Lazy<string> ViewerTemplate = new(() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "WebAssets", "viewer.template.html")));

    public sealed record NodeSpec(string Id, string Label, double X, double Y, string Color, string Shape, string? ImageSrc);

    public sealed record EdgeSpec(string Source, string Target, string Color, bool Manual, string LineStyle = "solid");

    /// <summary>
    /// The rendered graph (Cytoscape elements) — the page's <c>flowData</c>
    /// and the <c>flow</c> part of a <c>.docuclick</c> diagram
    /// (<see cref="DocuClickDiagramIo"/>). Serialize with null values
    /// omitted (see <see cref="BuildPage"/>).
    /// </summary>
    public static object FlowData(IReadOnlyList<NodeSpec> nodes, IReadOnlyList<EdgeSpec> edges) => new
    {
        nodes = nodes.Select((n, idx) => new
        {
            data = new { id = n.Id, label = n.Label, color = n.Color, shape = n.Shape, imageUrl = n.ImageSrc, stepIndex = idx + 1 },
            position = new { x = n.X, y = n.Y },
        }),
        edges = edges.Select(e => new
        {
            data = new
            {
                id = $"{(e.Manual ? "manual-" : "")}{e.Source}->{e.Target}",
                source = e.Source,
                target = e.Target,
                color = e.Color,
                manual = e.Manual,
                lineStyle = string.IsNullOrEmpty(e.LineStyle) ? "solid" : e.LineStyle,
            },
        }),
    };

    /// <param name="embeddedDataJson">
    /// Embedded verbatim in a `&lt;script id="docuclick-data"
    /// type="application/json"&gt;` block for <see cref="CanvasDocumentIo.Load"/>
    /// to read back later.
    /// </param>
    public static string BuildPage(string title, IReadOnlyList<NodeSpec> nodes, IReadOnlyList<EdgeSpec> edges, string embeddedDataJson)
    {
        // WhenWritingNull: a node without a screenshot (decision points,
        // path-start markers) must omit "imageUrl" entirely rather than
        // serialize it as null — Cytoscape's `[imageUrl]` selector matches a
        // data field that's merely *present*, null value and all, which
        // would wrongly create an image overlay (src "null") for it too.
        var jsonOptions = new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
        var dataJson = JsonSerializer.Serialize(FlowData(nodes, edges), jsonOptions);
        var cytoscapeJs = CytoscapeJs.Value;
        var saveServicePort = LocalSaveService.Port;
        // type="application/json" is never executed by the browser — purely
        // inert data storage, safe to embed regardless of where it sits.
        // System.Text.Json's default encoder already escapes '<'/'>' inside
        // string values (e.g. a description containing literal "</script>"),
        // so this can never prematurely close its own tag.
        var embeddedBlock = $"\n<script id=\"docuclick-data\" type=\"application/json\">\n{embeddedDataJson}\n</script>";

        var nodeCount = nodes.Count;
        var edgeCount = edges.Count;

        // Shared with the Obsidian plugin. Replace in one pass so user text
        // containing a placeholder can never be interpreted as template code.
        var values = new Dictionary<string, string>
        {
            ["TITLE"] = System.Net.WebUtility.HtmlEncode(title),
            ["NODE_COUNT"] = nodeCount.ToString(),
            ["EDGE_COUNT"] = edgeCount.ToString(),
            ["DOCUMENT"] = embeddedBlock,
            ["CYTOSCAPE"] = cytoscapeJs,
            ["FLOW"] = dataJson,
            ["SAVE_PORT"] = saveServicePort.ToString(),
        };
        return System.Text.RegularExpressions.Regex.Replace(ViewerTemplate.Value,
            @"@@([A-Z_]+)@@", match => values[match.Groups[1].Value]);
    }
}
