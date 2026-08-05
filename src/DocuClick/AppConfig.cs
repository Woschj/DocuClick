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
    /// (Obsidian .canvas flow diagram), "Word" (.docx, one heading +
    /// screenshot per click, appended sequentially — handles long flows
    /// better than a fixed canvas, and stays fully editable in
    /// Word/SharePoint), "PowerPoint" (.pptx, a real spatial flowchart —
    /// one slide per branch column, boxes/images/connector lines with
    /// actual coordinates, branch navigation via slide-jump hyperlinks),
    /// or "Excalidraw" (.excalidraw sketch-style diagram, experimental —
    /// needs the free Excalidraw Obsidian plugin). Canvas, Word,
    /// PowerPoint, and Excalidraw all support branching via the hotkeys
    /// below.
    /// </summary>
    public string OutputMode { get; set; } = "Note";

    /// <summary>Global hotkey: bookmark the current node as a branch point.</summary>
    public string BranchMarkModifiers { get; set; } = "";
    public string BranchMarkKey { get; set; } = "F9";

    /// <summary>Global hotkey: rewind the cursor to the last bookmarked branch point.</summary>
    public string BranchJumpModifiers { get; set; } = "";
    public string BranchJumpKey { get; set; } = "F10";

    /// <summary>Global hotkey: toggle recording on/off (same as clicking the tray icon).</summary>
    public string StartStopModifiers { get; set; } = "Control+Alt";
    public string StartStopKey { get; set; } = "R";

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
    /// switching from Canvas to Word).
    /// </summary>
    public string? LastSessionFileName { get; set; }
}
