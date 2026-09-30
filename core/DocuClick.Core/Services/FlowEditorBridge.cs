using System.Text.Json;

namespace DocuClick.Services;

/// <summary>
/// What <see cref="FlowEditorBridge"/> needs from the window hosting the
/// Ablauf-Übersicht's web view (WPF + WebView2 on Windows, Avalonia +
/// WebKit on macOS): small native dialogs, and a way to push JSON into the
/// page. Prompts are async so both a blocking WPF ShowDialog and an awaited
/// Avalonia dialog fit.
/// </summary>
public interface IFlowEditorHost
{
    /// <summary>The name/label prompt (BranchNameWindow). Null title/label/initial value = the dialog's defaults ("Pfad benennen").</summary>
    Task<string?> PromptTextAsync(string? title = null, string? label = null, string? initialValue = null);

    /// <summary>Picks an image file for a manual image node; null if cancelled.</summary>
    Task<string?> PickImageFileAsync();

    /// <summary>Yes/No warning (cascade delete).</summary>
    Task<bool> ConfirmAsync(string message);

    /// <summary>Delivers one JSON message to flow.js.</summary>
    void PostToWeb(string json);
}

/// <summary>
/// The C# half of the Ablauf-Übersicht's message protocol (the other half is
/// WebAssets/flow.js): turns gestures reported by the page into
/// <see cref="SessionManager"/>-level requests, and a <see cref="FlowPreview"/>
/// into the JSON the page renders. Shared by the Windows and macOS overlays,
/// which only differ in how the web view is hosted — every decision about
/// what a gesture *means* (jump vs. fork vs. rename vs. cascade-delete)
/// lives here, once.
/// </summary>
public sealed class FlowEditorBridge
{
    // Same-size cards as the exported/live HTML's own Cytoscape rendering
    // (220x170), so a screenshot thumbnail is as recognizable here as it is
    // in the browser instead of shrunk down to a barely-there smudge.
    private const double NodeWidth = 200;
    private const double NodeHeight = 150;
    private const double CurrentNodeWidth = 220;
    private const double CurrentNodeHeight = 170;

    // Same accent palette as the draw.io export's branch colors. A stable
    // (non-randomized) hash of the path id picks the color deterministically,
    // so it never flickers between redraws or picks up .NET's per-process
    // string-hash randomization.
    private static readonly string[] BranchPalette = { "#D97706", "#059669", "#DB2776", "#7C3AED", "#DC2626", "#0891B2" };

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IFlowEditorHost _host;
    // Screenshots the page already has (by full path): each is sent once and
    // cached there — resending every image with every preview was ~19 MB per
    // click after 100 clicks. The page asks again ("needImages") if it lost
    // them (reload).
    private readonly HashSet<string> _sentImages = new(StringComparer.Ordinal);

    public FlowEditorBridge(IFlowEditorHost host) => _host = host;

    /// <summary>The loaded session's folder — screenshots are resolved relative to it.</summary>
    public string? CurrentSessionFolder
    {
        get => _currentSessionFolder;
        set
        {
            if (_currentSessionFolder != value)
            {
                _sentImages.Clear();
            }

            _currentSessionFolder = value;
        }
    }

    private string? _currentSessionFolder;

    /// <summary>The preview most recently rendered, for prompts that need a node's current label.</summary>
    public FlowPreview LastPreview { get; private set; } = new(new List<PreviewNode>(), new List<PreviewEdge>());

    public event Action<string>? NodeClicked;
    /// <summary>(decisionPointId, pathName) — after the user named the new path.</summary>
    public event Action<string, string>? NewPathRequested;
    public event Action<string>? ContinuePathRequested;
    public event Action<string, string>? RenameRequested;
    public event Action<string>? DeleteRequested;
    public event Action<string, string>? ConnectRequested;
    public event Action<string, string>? DisconnectRequested;
    public event Action<string, string, string?, string?>? SetEdgeStyleRequested;
    public event Action<string, string>? ReverseEdgeRequested;
    public event Action<string, double, double>? MoveRequested;
    public event Action<IReadOnlyList<(string NodeId, double X, double Y)>>? BatchMoveRequested;
    /// <summary>(label, x, y, shape, color)</summary>
    public event Action<string, double, double, string?, string?>? AddNodeRequested;
    /// <summary>(label, imagePath, x, y)</summary>
    public event Action<string, string, double, double>? AddImageNodeRequested;

    /// <summary>Supplies the existing paths for a decision point, queried fresh when its popup opens.</summary>
    public Func<string, List<PathInfo>>? PathsProvider { get; set; }

    public static string FitViewMessage => JsonSerializer.Serialize(new { type = "fitView" }, JsonOptions);

    /// <summary>Handles one message from flow.js. Must run on the host's UI thread (it may open dialogs).</summary>
    public async Task HandleMessageAsync(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        switch (root.GetProperty("type").GetString())
        {
            case "needImages":
                // The page lost its image cache (reload) or missed a message.
                foreach (var key in root.GetProperty("keys").EnumerateArray())
                {
                    _sentImages.Remove(key.GetString() ?? "");
                }

                _host.PostToWeb(BuildPreviewMessage(LastPreview));
                break;

            case "nodeClick":
                NodeClicked?.Invoke(root.GetProperty("nodeId").GetString()!);
                break;

            case "requestPaths":
            {
                var nodeId = root.GetProperty("nodeId").GetString()!;
                var paths = PathsProvider?.Invoke(nodeId) ?? new List<PathInfo>();
                _host.PostToWeb(JsonSerializer.Serialize(new
                {
                    type = "pathsResult",
                    nodeId,
                    paths = paths.Select(p => new { pathStartNodeId = p.PathStartNodeId, name = p.Name, stepCount = p.StepCount })
                }, JsonOptions));
                break;
            }

            case "newPath":
            {
                var nodeId = root.GetProperty("nodeId").GetString()!;
                if (await _host.PromptTextAsync() is { } name)
                {
                    NewPathRequested?.Invoke(nodeId, name);
                }
                break;
            }

            case "continuePath":
                ContinuePathRequested?.Invoke(root.GetProperty("pathStartNodeId").GetString()!);
                break;

            case "rename":
            {
                var nodeId = root.GetProperty("nodeId").GetString()!;
                // Inline edit in the page already supplies the new text.
                if (root.TryGetProperty("newLabel", out var newLabelProp) && newLabelProp.GetString() is { } directLabel)
                {
                    RenameRequested?.Invoke(nodeId, directLabel);
                    break;
                }

                var node = LastPreview.Nodes.FirstOrDefault(n => n.Id == nodeId);
                var initialValue = node?.PathName ?? node?.Label ?? "";
                if (await _host.PromptTextAsync("DocuClick - Umbenennen", "Bezeichnung", initialValue) is { } newLabel)
                {
                    RenameRequested?.Invoke(nodeId, newLabel);
                }
                break;
            }

            case "delete":
                await RequestDeleteAsync(root.GetProperty("nodeId").GetString()!);
                break;

            case "connect":
                ConnectRequested?.Invoke(root.GetProperty("fromId").GetString()!, root.GetProperty("toId").GetString()!);
                break;

            case "disconnect":
                DisconnectRequested?.Invoke(root.GetProperty("fromId").GetString()!, root.GetProperty("toId").GetString()!);
                break;

            case "setEdgeStyle":
            {
                var color = root.TryGetProperty("color", out var colorProp) ? colorProp.GetString() : null;
                var lineStyle = root.TryGetProperty("lineStyle", out var lineStyleProp) ? lineStyleProp.GetString() : null;
                SetEdgeStyleRequested?.Invoke(root.GetProperty("fromId").GetString()!, root.GetProperty("toId").GetString()!, color, lineStyle);
                break;
            }

            case "reverseEdge":
                ReverseEdgeRequested?.Invoke(root.GetProperty("fromId").GetString()!, root.GetProperty("toId").GetString()!);
                break;

            case "move":
                MoveRequested?.Invoke(root.GetProperty("nodeId").GetString()!, root.GetProperty("x").GetDouble(), root.GetProperty("y").GetDouble());
                break;

            case "moveBatch":
            {
                var moves = new List<(string NodeId, double X, double Y)>();
                if (root.TryGetProperty("moves", out var movesElement) && movesElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var move in movesElement.EnumerateArray())
                    {
                        moves.Add((move.GetProperty("nodeId").GetString()!, move.GetProperty("x").GetDouble(), move.GetProperty("y").GetDouble()));
                    }
                }

                if (moves.Count > 0)
                {
                    BatchMoveRequested?.Invoke(moves);
                }
                break;
            }

            case "addNode":
            {
                var x = root.GetProperty("x").GetDouble();
                var y = root.GetProperty("y").GetDouble();
                var shape = root.TryGetProperty("shape", out var shapeProp) ? shapeProp.GetString() : null;
                var color = root.TryGetProperty("color", out var colorProp) ? colorProp.GetString() : null;
                var defaultName = root.TryGetProperty("label", out var labelProp) && !string.IsNullOrEmpty(labelProp.GetString()) ? labelProp.GetString()! : "";
                if (await _host.PromptTextAsync("DocuClick - Neuer Knoten", "Bezeichnung", defaultName) is { } label)
                {
                    AddNodeRequested?.Invoke(label, x, y, shape, color);
                }
                break;
            }

            case "addImageNode":
            {
                var x = root.GetProperty("x").GetDouble();
                var y = root.GetProperty("y").GetDouble();
                if (await _host.PickImageFileAsync() is { } imagePath && File.Exists(imagePath)
                    && await _host.PromptTextAsync("DocuClick - Neues Bild-Element", "Bezeichnung", Path.GetFileNameWithoutExtension(imagePath)) is { } label)
                {
                    AddImageNodeRequested?.Invoke(label, imagePath, x, y);
                }
                break;
            }

            case "imageLoadError":
                LogService.Log($"Ablauf-Übersicht: Bild konnte nicht geladen werden: {root.GetProperty("url").GetString()}");
                break;
        }
    }

    /// <summary>
    /// Deleting a node that forks (more than one outgoing edge, or a path
    /// start with its path behind it) takes its whole subtree with it — ask
    /// first, naming how many nodes would go.
    /// </summary>
    private async Task RequestDeleteAsync(string nodeId)
    {
        var node = LastPreview.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node is null)
        {
            return;
        }

        var childCount = LastPreview.Edges.Count(e => e.FromId == nodeId);
        if (childCount > 1 || (childCount == 1 && node.IsPathStart))
        {
            var subtreeSize = DescendantsOf(nodeId).Count;
            var confirmed = await _host.ConfirmAsync(
                $"„{node.Label}“ hat {childCount} abzweigende Fortsetzungen — beim Löschen werden auch alle {subtreeSize} nachfolgenden Knoten gelöscht. Fortfahren?");
            if (!confirmed)
            {
                return;
            }
        }

        DeleteRequested?.Invoke(nodeId);
    }

    private HashSet<string> DescendantsOf(string nodeId)
    {
        var forward = LastPreview.Edges.GroupBy(e => e.FromId).ToDictionary(g => g.Key, g => g.Select(e => e.ToId).ToList());
        var result = new HashSet<string>();
        var queue = new Queue<string>(forward.GetValueOrDefault(nodeId) ?? new List<string>());
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (result.Add(current) && forward.TryGetValue(current, out var children))
            {
                foreach (var child in children)
                {
                    queue.Enqueue(child);
                }
            }
        }

        return result;
    }

    /// <summary>The "preview" message flow.js renders; also remembers the preview for later prompts.</summary>
    public string BuildPreviewMessage(FlowPreview preview, bool isRecordedClick = false)
    {
        LastPreview = preview;
        return JsonSerializer.Serialize(BuildPreviewPayload(preview, isRecordedClick), JsonOptions);
    }

    private PreviewPayload BuildPreviewPayload(FlowPreview preview, bool isRecordedClick)
    {
        if (preview.Nodes.Count == 0)
        {
            return new PreviewPayload("preview", true, new List<NodePayload>(), new List<EdgePayload>(), false);
        }

        var forward = preview.Edges
            .Where(e => !e.Manual)
            .GroupBy(e => e.FromId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ToId).ToList());

        var nodes = preview.Nodes.Select(n =>
        {
            var isMarker = n.IsDecisionPoint || n.IsPathStart;
            // Manually added flowchart shapes and image-less nodes keep their
            // own stored size; recorded screenshot cards use the fixed card size.
            var isFlowchartElement = !string.IsNullOrEmpty(n.Shape) || (n.ImagePath is null && !n.IsCurrent);
            var width = isFlowchartElement && n.Width > 0 ? n.Width : (n.IsCurrent ? CurrentNodeWidth : NodeWidth);
            var height = isFlowchartElement && n.Height > 0 ? n.Height : (n.IsCurrent ? CurrentNodeHeight : NodeHeight);
            var permLabel = n.IsDecisionPoint
                ? "◆ Abzweigung"
                : n.IsPathStart && n.PathName is { } pathName
                    ? $"↳ {pathName}"
                    : n.IsCurrent
                        ? "● hier"
                        : "";
            var displayLabel = isMarker ? permLabel : n.IsCurrent ? $"● {n.Label}" : n.Label;

            return new NodePayload(
                n.Id, n.Label, permLabel, displayLabel, n.X, n.Y, width, height, NodeColorCss(n),
                isMarker, n.IsDecisionPoint, n.IsPathStart, n.IsCurrent, forward.ContainsKey(n.Id), n.PathName,
                ImageOnce(n.ImagePath), n.Shape, ImageKey(n.ImagePath));
        }).ToList();

        var nodeColors = nodes.ToDictionary(n => n.Id, n => n.Color);
        var edges = preview.Edges.Select(e => new EdgePayload(
            e.FromId, e.ToId, e.Manual,
            !string.IsNullOrEmpty(e.Color) ? e.Color : nodeColors.GetValueOrDefault(e.FromId) is { Length: > 0 } sourceColor ? sourceColor : "#3b82f6",
            string.IsNullOrEmpty(e.LineStyle) ? "solid" : e.LineStyle)).ToList();

        return new PreviewPayload("preview", true, nodes, edges, isRecordedClick);
    }

    /// <summary>The page's cache key for a screenshot: its full path (unique across sessions).</summary>
    private string? ImageKey(string? outputRelativePath) =>
        outputRelativePath is null || CurrentSessionFolder is null ? null : Path.GetFullPath(Path.Combine(CurrentSessionFolder, outputRelativePath));

    /// <summary>
    /// The screenshot as data: URI the first time the page needs it, null
    /// afterwards (the page keeps it by <see cref="ImageKey"/>). Data URIs,
    /// because the page is loaded from the app's own WebAssets folder and has
    /// no file access to wherever the session happens to live.
    /// </summary>
    private string? ImageOnce(string? outputRelativePath)
    {
        if (ImageKey(outputRelativePath) is not { } key || !_sentImages.Add(key))
        {
            return null;
        }

        try
        {
            return ImageData.DataUri(File.ReadAllBytes(key));
        }
        catch (Exception ex)
        {
            _sentImages.Remove(key);
            LogService.Log($"Ablauf-Übersicht: Screenshot konnte nicht eingebettet werden ({key}): {ex.Message}");
            return null;
        }
    }

    private static string NodeColorCss(PreviewNode node)
    {
        if (node.IsCurrent)
        {
            return "#E63946";
        }

        if (!string.IsNullOrWhiteSpace(node.Color))
        {
            return node.Color;
        }

        if (node.IsDecisionPoint)
        {
            return "#6B7280";
        }

        return node.PathId is { } pathId
            ? BranchPalette[StableHash(pathId) % BranchPalette.Length]
            : "rgba(76,175,232,0.902)";
    }

    private static int StableHash(string value)
    {
        unchecked
        {
            var hash = 17;
            foreach (var c in value)
            {
                hash = hash * 31 + c;
            }

            return hash & 0x7FFFFFFF;
        }
    }

    private sealed record NodePayload(
        string Id, string Label, string PermLabel, string DisplayLabel, double X, double Y, double Width, double Height, string Color,
        bool IsMarker, bool IsDecisionPoint, bool IsPathStart, bool IsCurrent, bool HasChildren, string? PathName,
        string? ImageUrl, string? Shape, string? ImageKey);

    private sealed record EdgePayload(string Source, string Target, bool Manual, string? Color = null, string? LineStyle = "solid");

    private sealed record PreviewPayload(string Type, bool Large, List<NodePayload> Nodes, List<EdgePayload> Edges, bool IsRecordedClick);
}
