namespace DocuClick;

/// <summary>Persisted user configuration, serialized as-is to config.json.</summary>
public sealed class AppConfig
{
    public string VaultPath { get; set; } = string.Empty;
    public string AttachmentsFolder { get; set; } = "Attachments";

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
    /// Where clicks get written to: "Note" (linear Markdown), "Canvas"
    /// (Obsidian .canvas flow diagram), "Excalidraw" (.excalidraw
    /// sketch-style diagram, experimental — needs the free Excalidraw
    /// Obsidian plugin), or "DrawIo" (.drawio, a real editable flowchart:
    /// card-shaped nodes with numbered badges, per-branch accent colors,
    /// and arrowed connectors — opens in the free draw.io/diagrams.net
    /// app, no Obsidian needed). Canvas, Excalidraw, and DrawIo all
    /// support branching via the decision-point hotkey below.
    /// </summary>
    public string OutputMode { get; set; } = "Note";

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
    /// "Neue Session" always prompts regardless of this. Ignored if its
    /// extension no longer matches the current OutputMode (e.g. after
    /// switching from Canvas to DrawIo).
    /// </summary>
    public string? LastSessionFileName { get; set; }
}
