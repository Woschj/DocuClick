using System.IO;

namespace DocuClick.Services;

/// <summary>
/// Recognizes an Obsidian vault around a session folder. Inside a vault a
/// recording is saved as a diagram note (<c>.md</c>, see <see cref="DiagramNote"/>)
/// — one file the DocuClick Diagrams plugin opens as diagram tab and
/// Obsidian searches and links like any note — instead of a standalone
/// <c>.html</c> Ablauf, so the plugin stays the one place a flow is edited
/// and the apps only record into it.
/// </summary>
public static class ObsidianVault
{
    /// <summary>Diagram note: the format for new recordings in a vault.</summary>
    public const string NoteExtension = ".md";

    /// <summary>Plugin diagrams before diagram notes (still read and written).</summary>
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

    /// <summary>File extension for a new session in <paramref name="folder"/>: <c>.md</c> (diagram note) in a vault, else <c>.html</c>.</summary>
    public static string OutputExtensionFor(string? folder) =>
        FindRoot(folder) is null ? SessionManager.OutputExtension : NoteExtension;

    /// <summary>Line under the session-start dialog's folder field: what gets created there.</summary>
    public static string TargetHint(string? folder) => FindRoot(folder) is null
        ? "Die .html-Datei und ihr Attachments-Unterordner werden direkt in diesem Ordner angelegt."
        : "Obsidian-Vault erkannt: Der Ablauf wird als Diagramm-Notiz (.md) angelegt und öffnet sich mit dem Plugin „DocuClick Diagrams“ als Diagramm (auch schon während der Aufnahme). Screenshots landen im Attachments-Unterordner.";

    /// <summary>True for a plugin file: a diagram note (any .md target) or a .docuclick diagram.</summary>
    public static bool IsDiagramFile(string? path) => IsNote(path)
        || (path is not null && string.Equals(Path.GetExtension(path), DiagramExtension, StringComparison.OrdinalIgnoreCase));

    /// <summary>True for a diagram note target (.md).</summary>
    public static bool IsNote(string? path) =>
        path is not null && string.Equals(Path.GetExtension(path), NoteExtension, StringComparison.OrdinalIgnoreCase);
}
