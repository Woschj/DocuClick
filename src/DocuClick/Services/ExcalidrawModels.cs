using System.Text.Json.Serialization;

namespace DocuClick.Services;

/// <summary>
/// Minimal mirror of the .excalidraw scene JSON format. One flat element
/// class covers every element type (rectangle/text/image/arrow) — fields
/// that don't apply to a given type are simply left null and omitted from
/// output, which the Excalidraw loader tolerates fine.
/// </summary>
public sealed class ExcalidrawElement
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("width")] public double Width { get; set; }
    [JsonPropertyName("height")] public double Height { get; set; }
    [JsonPropertyName("angle")] public double Angle { get; set; }
    [JsonPropertyName("strokeColor")] public string StrokeColor { get; set; } = "#1e1e1e";
    [JsonPropertyName("backgroundColor")] public string BackgroundColor { get; set; } = "transparent";
    [JsonPropertyName("fillStyle")] public string FillStyle { get; set; } = "solid";
    [JsonPropertyName("strokeWidth")] public double StrokeWidth { get; set; } = 1;
    [JsonPropertyName("strokeStyle")] public string StrokeStyle { get; set; } = "solid";
    [JsonPropertyName("roughness")] public double Roughness { get; set; } = 1;
    [JsonPropertyName("opacity")] public double Opacity { get; set; } = 100;
    [JsonPropertyName("groupIds")] public List<string> GroupIds { get; set; } = new();
    [JsonPropertyName("frameId")] public string? FrameId { get; set; }
    [JsonPropertyName("roundness")] public ExcalidrawRoundness? Roundness { get; set; }
    [JsonPropertyName("seed")] public long Seed { get; set; }
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("versionNonce")] public long VersionNonce { get; set; }
    [JsonPropertyName("isDeleted")] public bool IsDeleted { get; set; }
    [JsonPropertyName("boundElements")] public List<ExcalidrawBoundElementRef>? BoundElements { get; set; }
    [JsonPropertyName("updated")] public long Updated { get; set; }
    [JsonPropertyName("link")] public string? Link { get; set; }
    [JsonPropertyName("locked")] public bool Locked { get; set; }

    // type == "text"
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("originalText")] public string? OriginalText { get; set; }
    [JsonPropertyName("fontSize")] public double? FontSize { get; set; }
    // 1 = hand-drawn "Virgil", 2 = clean sans-serif "Normal", 3 = monospace "Code".
    [JsonPropertyName("fontFamily")] public int? FontFamily { get; set; }
    [JsonPropertyName("textAlign")] public string? TextAlign { get; set; }
    [JsonPropertyName("verticalAlign")] public string? VerticalAlign { get; set; }
    [JsonPropertyName("containerId")] public string? ContainerId { get; set; }
    [JsonPropertyName("lineHeight")] public double? LineHeight { get; set; }

    // type == "image"
    [JsonPropertyName("fileId")] public string? FileId { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("scale")] public double[]? Scale { get; set; }

    // type == "arrow"
    [JsonPropertyName("points")] public List<double[]>? Points { get; set; }
    [JsonPropertyName("startBinding")] public ExcalidrawBinding? StartBinding { get; set; }
    [JsonPropertyName("endBinding")] public ExcalidrawBinding? EndBinding { get; set; }
    [JsonPropertyName("startArrowhead")] public string? StartArrowhead { get; set; }
    [JsonPropertyName("endArrowhead")] public string? EndArrowhead { get; set; }
}

public sealed class ExcalidrawRoundness
{
    [JsonPropertyName("type")] public int Type { get; set; }
}

public sealed class ExcalidrawBoundElementRef
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
}

public sealed class ExcalidrawBinding
{
    [JsonPropertyName("elementId")] public string ElementId { get; set; } = "";
    [JsonPropertyName("focus")] public double Focus { get; set; }
    [JsonPropertyName("gap")] public double Gap { get; set; } = 4;
}

public sealed class ExcalidrawFile
{
    [JsonPropertyName("mimeType")] public string MimeType { get; set; } = "image/png";
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("dataURL")] public string DataUrl { get; set; } = "";
    [JsonPropertyName("created")] public long Created { get; set; }
}

public sealed class ExcalidrawAppState
{
    [JsonPropertyName("gridSize")] public int? GridSize { get; set; }
    [JsonPropertyName("viewBackgroundColor")] public string ViewBackgroundColor { get; set; } = "#ffffff";
}

public sealed class ExcalidrawDocument
{
    [JsonPropertyName("type")] public string Type { get; set; } = "excalidraw";
    [JsonPropertyName("version")] public int Version { get; set; } = 2;
    [JsonPropertyName("source")] public string Source { get; set; } = "https://github.com/Woschj/DocuClick";
    [JsonPropertyName("elements")] public List<ExcalidrawElement> Elements { get; set; } = new();
    [JsonPropertyName("appState")] public ExcalidrawAppState AppState { get; set; } = new();
    [JsonPropertyName("files")] public Dictionary<string, ExcalidrawFile> Files { get; set; } = new();
}
