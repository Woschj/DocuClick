using System.IO;
using System.Linq;
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
/// here since CanvasFlowWriter and DrawIoConverter both
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

    /// <summary>
    /// A missing file (first-ever session on this name) is a normal, silent
    /// "start empty" case. A file that *exists* but fails to read/parse is
    /// not — silently falling back to an empty document there used to mean
    /// the very next Save() would overwrite the original, still-mostly-
    /// intact file with that empty state and whatever gets recorded after
    /// it, destroying it for good with no visible error anywhere. Callers
    /// (see <see cref="CanvasFlowWriter.StartSession"/>) already run inside
    /// paths that surface an exception here as a real error message instead
    /// of swallowing it.
    /// </summary>
    public static CanvasDocument Load(string path)
    {
        // A .docuclick diagram (recorded into an Obsidian vault) has its own
        // JSON layout; every reader (writer, draw.io export) goes through here.
        if (ObsidianVault.IsDiagramFile(path))
        {
            return DocuClickDiagramIo.Load(path);
        }

        if (!File.Exists(path))
        {
            return new CanvasDocument();
        }

        string content;
        try
        {
            content = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Datei \"{Path.GetFileName(path)}\" konnte nicht gelesen werden: {ex.Message}", ex);
        }

        return Parse(ExtractEmbeddedJson(content) ?? content, path);
    }

    /// <summary>True if the file is a DocuClick Ablauf (HTML with the embedded data block).</summary>
    public static bool IsAblaufFile(string path) =>
        File.Exists(path) && File.ReadAllText(path).Contains(DataMarkerStart, StringComparison.Ordinal);

    /// <summary>
    /// Parses an Ablauf document's JSON (from a file, or sent back by the
    /// page's own editor) with the same path sanitizing as <see cref="Load"/>.
    /// </summary>
    public static CanvasDocument Parse(string json, string sourcePath)
    {
        CanvasDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<CanvasDocument>(json);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Datei \"{Path.GetFileName(sourcePath)}\" ist beschädigt und konnte nicht gelesen werden: {ex.Message}", ex);
        }

        if (doc is null)
        {
            throw new InvalidOperationException($"Datei \"{Path.GetFileName(sourcePath)}\" enthält kein gültiges Ablauf-Dokument.");
        }

        SanitizeFilePaths(doc, sourcePath);
        return doc;
    }

    /// <summary>A fresh random <see cref="CanvasDocument.SaveToken"/>.</summary>
    public static string NewSaveToken() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    /// <summary>
    /// Defense against a crafted/shared session file: <see cref="CanvasNode.File"/>
    /// (a screenshot's on-disk location) is attacker-controlled data once a
    /// `.html` "Ablauf" could have come from anyone — every consumer
    /// resolves it via <c>Path.Combine(outputPath, relativePath)</c>, which
    /// in .NET silently discards the output path entirely if
    /// the second argument is rooted (an absolute path or a UNC share), and
    /// a relative value with ".." segments walks out of it just as normally.
    /// Either way that turns "open a shared Ablauf" / "export it" into
    /// reading and re-embedding an arbitrary local file. Rejecting anything
    /// rooted or containing ".." here — the same rule
    /// <see cref="SessionStartWindow"/>'s own folder sanitizer already
    /// applies to user-typed paths — means every downstream consumer is
    /// safe without needing its own copy of this check.
    /// </summary>
    private static void SanitizeFilePaths(CanvasDocument doc, string sourcePath)
    {
        foreach (var node in doc.Nodes)
        {
            if (node.File is not { Length: > 0 } file)
            {
                continue;
            }

            var isSafe = !Path.IsPathRooted(file)
                && !file.Split('/', '\\').Any(segment => segment == "..");
            if (!isSafe)
            {
                LogService.Log($"Unsicherer Bild-Pfad \"{file}\" in \"{Path.GetFileName(sourcePath)}\" ignoriert (absoluter Pfad oder \"..\" verweist außerhalb des Ausgabeordners).");
                node.File = null;
            }
        }
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
