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
    /// (Obsidian .canvas flow diagram), or "Word" (.docx, one heading +
    /// screenshot per click, appended sequentially — handles long flows
    /// better than a fixed canvas, and stays fully editable in
    /// Word/SharePoint). Canvas and Word both support branching via the
    /// hotkeys below.
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
}
