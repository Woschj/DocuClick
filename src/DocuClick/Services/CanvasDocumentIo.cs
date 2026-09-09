using System.IO;
using System.Text.Json;

namespace DocuClick.Services;

/// <summary>
/// Shared on-disk format for a Canvas-mode session: a single, real .html
/// file whose &lt;script id="docuclick-data"&gt; tag embeds the same node/
/// edge JSON CanvasFlowWriter always worked with — opening the file
/// directly in a plain browser shows the interactive diagram, and (see
/// <see cref="HtmlViewerBuilder"/>) that page can now move/connect nodes
/// and save the result straight back into this same file itself via the
/// File System Access API, no DocuClick or Obsidian needed. Centralized
/// here since CanvasFlowWriter, DrawIoConverter, and HtmlFlowExporter all
/// need to read this same format, and a session recorded before this
/// format switch is still a bare-JSON .canvas file — <see cref="Load"/>
/// falls back to parsing the whole file as JSON directly when no
/// embedded-data marker is found, so an old file doesn't just silently
/// look empty.
/// </summary>
public static class CanvasDocumentIo
{
    internal const string DataMarkerStart = "<script id=\"docuclick-data\" type=\"application/json\">";
    internal const string DataMarkerEnd = "</script>";

    public static CanvasDocument Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var content = File.ReadAllText(path);
                var json = ExtractEmbeddedJson(content) ?? content;
                var doc = JsonSerializer.Deserialize<CanvasDocument>(json);
                if (doc is not null)
                {
                    return doc;
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Log($"Datei konnte nicht gelesen werden, beginne neu: {ex.Message}");
        }

        return new CanvasDocument();
    }

    private static string? ExtractEmbeddedJson(string htmlContent)
    {
        var startIdx = htmlContent.IndexOf(DataMarkerStart, StringComparison.Ordinal);
        if (startIdx < 0)
        {
            return null;
        }

        startIdx += DataMarkerStart.Length;
        var endIdx = htmlContent.IndexOf(DataMarkerEnd, startIdx, StringComparison.Ordinal);
        return endIdx > startIdx ? htmlContent[startIdx..endIdx].Trim() : null;
    }
}
