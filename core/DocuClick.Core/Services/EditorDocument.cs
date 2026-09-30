namespace DocuClick.Services;

/// <summary>
/// A snapshot of the loaded flow for an editor page the app hosts (see
/// <see cref="EditorPageHost"/>): <see cref="CanvasJson"/> is the stored
/// document (what the page saves back), <see cref="FlowJson"/> the rendered
/// graph, <see cref="CurrentNodeId"/> where the next recorded click attaches.
/// </summary>
public sealed record EditorDocument(string FilePath, string Title, string CanvasJson, string FlowJson, string? CurrentNodeId, Func<string> BuildPage);
