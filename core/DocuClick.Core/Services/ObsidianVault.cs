using System.IO;
using System.Text.Json;

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

    /// <summary>
    /// The folder Obsidian puts new attachments in for a note in
    /// <paramref name="noteFolder"/> — the vault's setting "Dateien und Links →
    /// Standardordner für neue Anhänge" (<c>attachmentFolderPath</c> in
    /// <c>.obsidian/app.json</c>):
    /// <list type="bullet">
    /// <item>not set or "/": the vault's root folder (Obsidian's default),</item>
    /// <item>"./": the note's own folder; "./Name": a subfolder there,</item>
    /// <item>"Ordner/Pfad": that folder in the vault.</item>
    /// </list>
    /// A value that would lead outside the vault falls back to the vault root.
    /// </summary>
    public static string AttachmentFolder(string vaultRoot, string noteFolder)
    {
        var root = Path.GetFullPath(vaultRoot);
        var setting = ReadAttachmentSetting(root)?.Trim().Replace('\\', '/') ?? "";
        string folder;
        if (setting is "" or "/")
        {
            folder = root;
        }
        else if (setting == "." || setting.StartsWith("./", StringComparison.Ordinal))
        {
            folder = Path.Combine(Path.GetFullPath(noteFolder), setting.Length > 2 ? setting[2..] : "");
        }
        else
        {
            folder = Path.Combine(root, setting.TrimStart('/'));
        }

        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var inside = string.Equals(folder, Path.TrimEndingDirectorySeparator(root), comparison)
            || folder.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, comparison);
        return inside ? folder : root;
    }

    private static string? ReadAttachmentSetting(string vaultRoot)
    {
        try
        {
            var path = Path.Combine(vaultRoot, ".obsidian", "app.json");
            if (!File.Exists(path))
            {
                return null;
            }

            using var json = JsonDocument.Parse(File.ReadAllText(path));
            return json.RootElement.TryGetProperty("attachmentFolderPath", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LogService.Log($"Obsidian-Einstellung für Anhänge nicht lesbar ({ex.Message}) – Vault-Hauptordner wird verwendet.");
            return null;
        }
    }

    /// <summary>File extension for a new session in <paramref name="folder"/>: <c>.md</c> (diagram note) in a vault, else <c>.html</c>.</summary>
    public static string OutputExtensionFor(string? folder) =>
        FindRoot(folder) is null ? SessionManager.OutputExtension : NoteExtension;

    /// <summary>Line under the session-start dialog's folder field: what gets created there.</summary>
    public static string TargetHint(string? folder) => FindRoot(folder) is null
        ? "Die .html-Datei und ihr Attachments-Unterordner werden direkt in diesem Ordner angelegt."
        : "Obsidian-Vault erkannt: Der Ablauf wird als Diagramm-Notiz (.md) angelegt und öffnet sich mit dem Plugin „DocuClick Diagrams“ als Diagramm (auch schon während der Aufnahme). Das Plugin wird dafür bei Bedarf automatisch im Vault installiert. Screenshots landen dort, wo Obsidian Anhänge ablegt (Einstellungen → Dateien und Links), in einem Unterordner pro Ablauf.";

    /// <summary>True for a plugin file: a diagram note (any .md target) or a .docuclick diagram.</summary>
    public static bool IsDiagramFile(string? path) => IsNote(path)
        || (path is not null && string.Equals(Path.GetExtension(path), DiagramExtension, StringComparison.OrdinalIgnoreCase));

    /// <summary>True for a diagram note target (.md).</summary>
    public static bool IsNote(string? path) =>
        path is not null && string.Equals(Path.GetExtension(path), NoteExtension, StringComparison.OrdinalIgnoreCase);
}
