using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DocuClick.Services;

/// <summary>
/// Reads and writes a diagram of the DocuClick Diagrams plugin for Obsidian
/// (<c>obsidian/src/document.js</c>) — a diagram note (<c>.md</c>, the JSON
/// below inside, see <see cref="DiagramNote"/>) or a plain <c>.docuclick</c>
/// file — so the apps can record straight into a diagram the plugin has open:
/// <code>
/// { "format": "docuclick-diagram", "version": 1,
///   "canvas": { nodes, edges },          // same document as an .html Ablauf
///   "flow":   { nodes, edges },          // rendered graph, as HtmlViewerBuilder.FlowData
///   "images": { textNodeId: "vault/relative/path.png" } }
/// </code>
/// A step's screenshot is either a vault file (listed in <c>images</c>) or a
/// data URI on its flow node (<c>data.imageUrl</c>, the plugin's default
/// "In der Datei" storage). Screenshots recorded by the apps are always
/// files (<c>Attachments/&lt;Session&gt;/…</c> next to the diagram), so the
/// diagram stays small and each click only rewrites a few kilobytes.
/// Canvas file nodes carry no <c>file</c> here: the flow node's image is
/// the single source, as in the plugin's own storage form.
/// </summary>
public static class DocuClickDiagramIo
{
    public const string Format = "docuclick-diagram";
    public const int Version = 1;

    // Same limit as the plugin (MAX_BYTES in document.js).
    private const long MaxBytes = 64 * 1024 * 1024;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Plain JSON file, never embedded in HTML: keep umlauts readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Folder that the <c>images</c> paths are relative to: the vault root, or the diagram's own folder outside a vault.</summary>
    public static string RootFor(string diagramPath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(diagramPath))!;
        return ObsidianVault.FindRoot(folder) ?? folder;
    }

    /// <summary>
    /// Loads a diagram as the apps' <see cref="CanvasDocument"/>: each
    /// step's screenshot is put back on its canvas file node as a path
    /// relative to the diagram's folder (may start with "../" when the
    /// plugin keeps images in a vault folder elsewhere, but never leaves the
    /// vault) or as a data URI. A missing file is an empty document.
    /// </summary>
    public static CanvasDocument Load(string path)
    {
        if (!File.Exists(path))
        {
            return new CanvasDocument();
        }

        string text;
        try
        {
            if (new FileInfo(path).Length > MaxBytes)
            {
                throw new InvalidOperationException("Datei ist größer als 64 MB.");
            }

            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Datei \"{Path.GetFileName(path)}\" konnte nicht gelesen werden: {ex.Message}", ex);
        }

        return Parse(text, path);
    }

    /// <param name="text">File content: a diagram note (for a .md path) or the diagram JSON.</param>
    public static CanvasDocument Parse(string text, string path)
    {
        var name = Path.GetFileName(path);
        if (ObsidianVault.IsNote(path))
        {
            var data = DiagramNote.ExtractData(text);
            if (data is null)
            {
                // An empty note may become a diagram; any other note is left alone.
                return string.IsNullOrWhiteSpace(text)
                    ? new CanvasDocument()
                    : throw new InvalidOperationException($"\"{name}\" ist keine DocuClick-Diagramm-Notiz (keine Diagrammdaten). Bitte eine andere Datei wählen oder eine neue anlegen.");
            }

            text = data;
        }

        JsonObject root;
        CanvasDocument doc;
        try
        {
            root = JsonNode.Parse(text) as JsonObject ?? throw new InvalidOperationException("kein JSON-Objekt");
            if ((string?)root["format"] != Format || (int?)root["version"] != Version)
            {
                throw new InvalidOperationException("kein DocuClick-Diagramm (format/version)");
            }

            doc = root["canvas"]?.Deserialize<CanvasDocument>() ?? throw new InvalidOperationException("\"canvas\" fehlt");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new InvalidOperationException($"Datei \"{name}\" ist beschädigt und konnte nicht gelesen werden: {ex.Message}", ex);
        }

        doc.Nodes.RemoveAll(n => n is null);
        doc.Edges.RemoveAll(e => e is null);

        var diagramFolder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var imageRoot = RootFor(path);

        // Canvas file nodes may still carry an embedded image (older plugin
        // files); anything that is not a data URI or a safe relative path is
        // dropped, same defense as CanvasDocumentIo's SanitizeFilePaths.
        foreach (var node in doc.Nodes.Where(n => n.File is not null))
        {
            if (!IsImageDataUri(node.File!) && ToDiagramRelative(node.File!, diagramFolder, diagramFolder) is null)
            {
                node.File = null;
            }
        }

        var images = root["images"] as JsonObject;
        var textNodes = doc.Nodes.Where(n => n.Type == "text").ToDictionary(n => n.Id);
        foreach (var flowNode in (root["flow"]?["nodes"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            var id = (string?)flowNode["data"]?["id"];
            if (id is null || !textNodes.TryGetValue(id, out var textNode))
            {
                continue;
            }

            string? image = null;
            if (images?[id] is JsonValue vaultPath && vaultPath.TryGetValue<string>(out var vaultRelative))
            {
                image = ToDiagramRelative(vaultRelative, imageRoot, diagramFolder);
                if (image is null)
                {
                    LogService.Log($"Unsicherer Bild-Pfad \"{vaultRelative}\" in \"{name}\" ignoriert.");
                }
            }
            else if (flowNode["data"]?["imageUrl"] is JsonValue url && url.TryGetValue<string>(out var dataUri) && IsImageDataUri(dataUri))
            {
                image = dataUri;
            }

            if (image is null)
            {
                continue;
            }

            var sibling = FindImageSibling(doc, textNode);
            if (sibling is null)
            {
                sibling = new CanvasNode
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Type = "file",
                    X = textNode.X,
                    Y = textNode.Y + CanvasFlowWriter.TextNodeHeight + CanvasFlowWriter.TextToImageGap,
                    Width = 380,
                    Height = 270,
                };
                doc.Nodes.Add(sibling);
            }

            sibling.File = image;
        }

        return doc;
    }

    /// <summary>
    /// The file text for <paramref name="doc"/>: for a diagram note,
    /// <paramref name="currentText"/> (the note on disk) with steps and data
    /// replaced, own text kept; else the diagram JSON. <paramref name="nodes"/>
    /// are the rendered steps with <see cref="HtmlViewerBuilder.NodeSpec.ImageSrc"/>
    /// set to the canvas file node's raw value (relative path or data URI);
    /// <paramref name="embed"/> turns a path outside the image root into a
    /// data URI so no screenshot is lost.
    /// </summary>
    public static string Serialize(
        string path,
        CanvasDocument doc,
        IReadOnlyList<HtmlViewerBuilder.NodeSpec> nodes,
        IReadOnlyList<HtmlViewerBuilder.EdgeSpec> edges,
        Func<string, string?> embed,
        string? currentText = null)
    {
        var diagramFolder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var imageRoot = RootFor(path);
        var images = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var flowNodes = new List<HtmlViewerBuilder.NodeSpec>(nodes.Count);
        foreach (var node in nodes)
        {
            var image = node.ImageSrc;
            if (image is not null && !IsImageDataUri(image))
            {
                var vaultPath = ToRootRelative(image, diagramFolder, imageRoot);
                if (vaultPath is not null)
                {
                    images[node.Id] = vaultPath;
                    image = null;
                }
                else
                {
                    image = embed(image);
                }
            }

            flowNodes.Add(node with { ImageSrc = image });
        }

        var canvas = new
        {
            nodes = doc.Nodes.Select(n => n.Type == "file" ? new CanvasNode
            {
                Id = n.Id, Type = n.Type, X = n.X, Y = n.Y, Width = n.Width, Height = n.Height, Color = n.Color,
            } : n),
            edges = doc.Edges,
        };

        var json = JsonSerializer.Serialize(new
        {
            format = Format,
            version = Version,
            canvas,
            flow = HtmlViewerBuilder.FlowData(flowNodes, edges),
            images = images.Count > 0 ? images : null,
        }, WriteOptions);

        return ObsidianVault.IsNote(path)
            ? DiagramNote.Compose(currentText, DiagramNote.EscapeForNote(json), DiagramNote.Steps(doc))
            : json;
    }

    internal static bool IsImageDataUri(string value) =>
        value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && value.Contains(";base64,", StringComparison.OrdinalIgnoreCase);

    /// <summary>Root-relative (forward slashes) → diagram-relative, or null if it is absolute, uses "..", or leaves the root.</summary>
    private static string? ToDiagramRelative(string rootRelative, string root, string diagramFolder)
    {
        if (string.IsNullOrWhiteSpace(rootRelative) || Path.IsPathRooted(rootRelative)
            || rootRelative.Contains(':') || rootRelative.Split('/', '\\').Any(segment => segment == ".."))
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(root, rootRelative));
        return IsInside(full, root) ? Path.GetRelativePath(diagramFolder, full).Replace('\\', '/') : null;
    }

    /// <summary>Diagram-relative → root-relative (forward slashes), or null if it resolves outside the root.</summary>
    private static string? ToRootRelative(string diagramRelative, string diagramFolder, string root)
    {
        if (Path.IsPathRooted(diagramRelative))
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(diagramFolder, diagramRelative));
        return IsInside(full, root) ? Path.GetRelativePath(root, full).Replace('\\', '/') : null;
    }

    private static bool IsInside(string fullPath, string root)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return fullPath.StartsWith(prefix, comparison);
    }

    private static CanvasNode? FindImageSibling(CanvasDocument doc, CanvasNode textNode) => doc.Nodes.FirstOrDefault(n =>
        n.Type == "file"
        && Math.Abs(n.X - textNode.X) < 0.5
        && Math.Abs(n.Y - (textNode.Y + CanvasFlowWriter.TextNodeHeight + CanvasFlowWriter.TextToImageGap)) < 0.5);
}
