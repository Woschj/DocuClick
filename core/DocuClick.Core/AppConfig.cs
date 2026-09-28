namespace DocuClick;

/// <summary>Persisted user configuration, serialized as-is to config.json.</summary>
public sealed class AppConfig
{
    /// <summary>
    /// Defaults for a first start on the current OS. The property
    /// initializers below are the Windows defaults (and fill in anything
    /// missing from an older config file). On macOS, Ctrl+click already
    /// means right-click (so Option skips instead), F9/F11 need fn on Mac
    /// keyboards and F11 shows the desktop, and the pre-click capture
    /// stream is available.
    /// </summary>
    public static AppConfig CreateDefault()
    {
        var config = new AppConfig();
        if (OperatingSystem.IsMacOS())
        {
            config.SkipRecordingModifier = "Alt";
            config.BranchMarkModifiers = "Control+Alt";
            config.BranchMarkKey = "D";
            config.ZoomToCursorModifiers = "Control+Alt";
            config.ZoomToCursorKey = "Z";
            config.CaptureTiming = "BeforeClick";
        }

        return config;
    }

    /// <summary>
    /// "AfterClick": capture once the click has been seen (the clicked app
    /// may already show its reaction — an opened menu, a pressed button).
    /// "BeforeClick": use the frame from right before the click, from a
    /// continuous capture stream that runs while recording. Only macOS
    /// supports "BeforeClick"; elsewhere it silently behaves like "AfterClick".
    /// </summary>
    public string CaptureTiming { get; set; } = "AfterClick";

    /// <summary>
    /// Scale HiDPI/Retina captures down to 1 pixel per screen point before
    /// saving — otherwise every image (and every .html file embedding them)
    /// is 4× the size for little visible gain.
    /// </summary>
    public bool DownscaleHiDpiScreenshots { get; set; } = true;

    // Historically a single, globally-configured "output root" everything
    // had to live under (JSON key "VaultPath" from when the app required an
    // Obsidian vault there). Replaced by a free-choice folder picker per
    // session (see SessionStartWindow) — there is no longer one root at
    // all, so this setting and its Settings-menu UI are gone; an existing
    // config.json's "VaultPath" value is simply ignored from here on
    // (System.Text.Json skips unknown properties), same as any other
    // deliberately dropped legacy field.

    public string AttachmentsFolder { get; set; } = "Attachments";

    private const int MaxRecentOutputPaths = 8;

    /// <summary>
    /// Most-recently-used session folders, most-recent-first, deduplicated
    /// case-insensitively, capped at <see cref="MaxRecentOutputPaths"/>
    /// entries. Populated via <see cref="RememberRecentOutputPath"/>
    /// whenever a session actually starts successfully — used only to seed
    /// SessionStartWindow's folder picker with the last-used location as a
    /// starting point, not to constrain where a new session can go.
    /// </summary>
    public List<string> RecentOutputPaths { get; set; } = new();

    /// <summary>
    /// Records <paramref name="path"/> as the most-recently-used session
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
    /// Absolute path to the target file most recently used to start a
    /// session — persisted so a plain "Start" (tray/hotkey/top-bar) can
    /// resume it directly without prompting. "Neue Session" always prompts
    /// regardless of this.
    /// </summary>
    public string? LastSessionFileName { get; set; }
}
