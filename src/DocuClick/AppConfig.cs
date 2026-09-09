using System.Text.Json.Serialization;

namespace DocuClick;

/// <summary>Persisted user configuration, serialized as-is to config.json.</summary>
public sealed class AppConfig
{
    /// <summary>
    /// Root output folder everything gets written under. Property renamed
    /// from "VaultPath" (the app used to require an Obsidian vault here;
    /// the live output is now a self-contained HTML file, no Obsidian
    /// needed) — the JSON attribute keeps the on-disk key unchanged so an
    /// existing config.json keeps loading with no migration step.
    /// </summary>
    [JsonPropertyName("VaultPath")]
    public string OutputPath { get; set; } = string.Empty;

    public string AttachmentsFolder { get; set; } = "Attachments";

    private const int MaxRecentOutputPaths = 8;

    /// <summary>
    /// Most-recently-used output folders, most-recent-first, deduplicated
    /// case-insensitively, capped at <see cref="MaxRecentOutputPaths"/>
    /// entries. Populated via <see cref="RememberRecentOutputPath"/>
    /// whenever a folder is deliberately chosen (Settings' Browse button)
    /// or actually put to use (a session starts successfully) — lets
    /// switching between a few regularly-used output locations (e.g.
    /// different clients/projects) happen from a dropdown instead of
    /// retyping or re-browsing the full path every time.
    /// </summary>
    public List<string> RecentOutputPaths { get; set; } = new();

    /// <summary>
    /// Records <paramref name="path"/> as the most-recently-used output
    /// folder: moves it to the front if already present (case-insensitive —
    /// Windows paths), inserts it otherwise, then trims the list back down
    /// to <see cref="MaxRecentOutputPaths"/>. No-op for a blank path.
    /// </summary>
    public void RememberRecentOutputPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        RecentOutputPaths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentOutputPaths.Insert(0, path);
        if (RecentOutputPaths.Count > MaxRecentOutputPaths)
        {
            RecentOutputPaths.RemoveRange(MaxRecentOutputPaths, RecentOutputPaths.Count - MaxRecentOutputPaths);
        }
    }

    public bool UseUiAutomation { get; set; } = true;

    /// <summary>
    /// Held-down modifier that suppresses recording for a single click.
    /// One of "None", "Shift", "Control", "Alt".
    /// </summary>
    public string SkipRecordingModifier { get; set; } = "Control";

    public string HighlightColorHex { get; set; } = "#E63946";
    public int HighlightRadius { get; set; } = 24;
    public int HighlightThickness { get; set; } = 4;

    /// <summary>
    /// Global hotkey: marks the current node as a decision point. Starting
    /// a new path from it, or resuming one, then happens by clicking the
    /// decision point's diamond in the Ablauf-Übersicht — there's no
    /// second hotkey for that anymore (property name kept as "BranchMark"
    /// for config-file compatibility with earlier versions).
    /// </summary>
    public string BranchMarkModifiers { get; set; } = "";
    public string BranchMarkKey { get; set; } = "F9";

    /// <summary>Global hotkey: toggle recording on/off (same as clicking the tray icon).</summary>
    public string StartStopModifiers { get; set; } = "Control+Alt";
    public string StartStopKey { get; set; } = "R";

    /// <summary>
    /// Global hotkey: toggle "Zoom-auf-Cursor" on/off. While active, a
    /// captured click crops tightly around the cursor (see
    /// <see cref="ZoomToCursorRadius"/>) instead of grabbing the whole
    /// clicked window — useful for zooming in on small UI details.
    /// </summary>
    public string ZoomToCursorModifiers { get; set; } = "";
    public string ZoomToCursorKey { get; set; } = "F11";

    /// <summary>Half-width/height in pixels of the square cropped around the cursor when "Zoom-auf-Cursor" is active.</summary>
    public int ZoomToCursorRadius { get; set; } = 200;

    /// <summary>Short system sound on every successfully captured click.</summary>
    public bool EnableClickSound { get; set; } = true;

    /// <summary>
    /// Also capture on Enter key presses (active window + focused element),
    /// not just left clicks. The underlying hook only ever recognizes the
    /// Enter key itself — it never inspects or records any other keystroke.
    /// </summary>
    public bool CaptureOnEnter { get; set; } = true;

    /// <summary>Also capture on right-clicks, not just left-clicks (e.g. context-menu triggers).</summary>
    public bool CaptureOnRightClick { get; set; } = true;

    /// <summary>
    /// Target file name (with extension, possibly subfolder-prefixed) most
    /// recently used to start a session — persisted so a plain "Start"
    /// (tray/hotkey/top-bar) can resume it directly without prompting.
    /// "Neue Session" always prompts regardless of this.
    /// </summary>
    public string? LastSessionFileName { get; set; }
}
