using System.Text.Json.Serialization;

namespace DocuClick.Services;

public sealed class CanvasNode
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "text";
    [JsonPropertyName("text")] public string? Text { get; set; }
    // Vault-relative path for type="file" nodes — Obsidian Canvas's native
    // embed mechanism. Third-party tools that read/export .canvas files
    // generally honor this documented field but don't replicate Obsidian's
    // own "![[wikilink]]" resolution inside a text node's markdown, which
    // is why images went missing on export before this was added.
    [JsonPropertyName("file")] public string? File { get; set; }
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("width")] public double Width { get; set; }
    [JsonPropertyName("height")] public double Height { get; set; }
    [JsonPropertyName("color")] public string? Color { get; set; }
}

public sealed class CanvasEdge
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("fromNode")] public string FromNode { get; set; } = "";
    [JsonPropertyName("toNode")] public string ToNode { get; set; } = "";
    // Vertical flow: main line connects bottom-to-top by default.
    [JsonPropertyName("fromSide")] public string FromSide { get; set; } = "bottom";
    [JsonPropertyName("toSide")] public string ToSide { get; set; } = "top";
}

/// <summary>Mirrors the plain-JSON shape of an Obsidian .canvas file.</summary>
public sealed class CanvasDocument
{
    [JsonPropertyName("nodes")] public List<CanvasNode> Nodes { get; set; } = new();
    [JsonPropertyName("edges")] public List<CanvasEdge> Edges { get; set; } = new();
}
