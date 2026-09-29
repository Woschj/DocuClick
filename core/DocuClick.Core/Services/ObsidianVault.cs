using System.IO;

namespace DocuClick.Services;

/// <summary>
/// Recognizes an Obsidian vault around a session folder. Inside a vault a
/// recording is saved as a <c>.docuclick</c> diagram (see
/// <see cref="DocuClickDiagramIo"/>) — the file the DocuClick Diagrams
/// plugin opens and edits — instead of a standalone <c>.html</c> Ablauf,
/// so the plugin stays the one place a flow is edited and the apps only
/// record into it.
/// </summary>
public static class ObsidianVault
{
    public const string DiagramExtension = ".docuclick";

    /// <summary>
    /// The vault's root folder: the nearest folder at or above
    /// <paramref name="folder"/> that contains Obsidian's own <c>.obsidian</c>
    /// settings folder. Null outside a vault.
    /// </summary>
    public static string? FindRoot(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        try
        {
            for (var current = new DirectoryInfo(Path.GetFullPath(folder)); current is not null; current = current.Parent)
            {
                if (Directory.Exists(Path.Combine(current.FullName, ".obsidian")))
                {
                    return current.FullName;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // An unreadable parent just means "no vault found".
        }

        return null;
    }

    /// <summary>File extension for a new session in <paramref name="folder"/>: <c>.docuclick</c> in a vault, else <c>.html</c>.</summary>
    public static string OutputExtensionFor(string? folder) =>
        FindRoot(folder) is null ? SessionManager.OutputExtension : DiagramExtension;

    /// <summary>Line under the session-start dialog's folder field: what gets created there.</summary>
    public static string TargetHint(string? folder) => FindRoot(folder) is null
        ? "Die .html-Datei und ihr Attachments-Unterordner werden direkt in diesem Ordner angelegt."
        : "Obsidian-Vault erkannt: Der Ablauf wird als .docuclick-Diagramm angelegt und öffnet sich im Plugin „DocuClick Diagrams“ (auch schon während der Aufnahme). Screenshots landen im Attachments-Unterordner.";

    /// <summary>True for a DocuClick Diagrams file (by extension).</summary>
    public static bool IsDiagramFile(string? path) =>
        path is not null && string.Equals(Path.GetExtension(path), DiagramExtension, StringComparison.OrdinalIgnoreCase);
}
