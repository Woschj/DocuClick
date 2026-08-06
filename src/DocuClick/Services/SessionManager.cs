using System.Drawing;
using System.IO;

namespace DocuClick.Services;

/// <summary>
/// Glues the mouse/keyboard hooks to the capture pipeline (UI Automation
/// lookup -> screenshot -> highlight -> write) and owns the current
/// session's target file. Writes either a linear note (ObsidianWriter) or
/// a branching flow (<see cref="IFlowWriter"/>: Obsidian Canvas, draw.io,
/// or the experimental Excalidraw mode), depending on
/// <see cref="AppConfig.OutputMode"/>.
/// </summary>
public sealed class SessionManager : IDisposable
{
    private readonly MouseHookService _mouseHook = new();
    private readonly KeyboardHookService _keyboardHook = new();
    private readonly AppConfig _config;
    private readonly ObsidianWriter _noteWriter;
    private readonly CanvasFlowWriter _canvasWriter;
    private readonly ExcalidrawFlowWriter _excalidrawWriter;
    private readonly DrawIoFlowWriter _drawIoWriter;
    private string _currentTargetFileName = string.Empty;
    private bool _isRunning;
    private bool _zoomToCursorActive;

    public event Action<string>? ErrorOccurred;
    public event Action<string>? InfoOccurred;
    public event Action<int>? BranchDepthChanged;
    public event Action<string?>? CanvasStatusChanged;
    public event Action<bool>? ZoomToCursorChanged;

    /// <summary>Fired whenever the flow's nodes/current-position change (session start/stop, every click, branch actions, node jumps) — for the tree-preview overlay. Null means "hide the overlay" (stopped, or the active mode doesn't support branching).</summary>
    public event Action<FlowPreview?>? FlowPreviewChanged;

    /// <summary>PNG-encoded bytes of the most recently captured screenshot, fired after a successful capture — for the status overlay's thumbnail preview.</summary>
    public event Action<byte[]>? LastScreenshotCaptured;

    /// <summary>
    /// Set once by App.xaml.cs to answer "is this screen point over
    /// DocuClick's own UI (top bar / tray icon)?" — the global mouse hook
    /// otherwise has no way to distinguish those from actual content
    /// clicks, since WH_MOUSE_LL sees every click on screen regardless of
    /// which window it lands on.
    /// </summary>
    public Func<Point, bool>? IsPointOnOwnUi { get; set; }

    public bool IsRunning => _isRunning;

    /// <summary>Whether "Zoom-auf-Cursor" is currently active (see <see cref="ToggleZoomToCursor"/>).</summary>
    public bool IsZoomToCursorActive => _zoomToCursorActive;

    /// <summary>Hotkey action: toggles "Zoom-auf-Cursor" on/off — crops the next captures tightly around the cursor instead of the whole clicked window.</summary>
    public void ToggleZoomToCursor()
    {
        _zoomToCursorActive = !_zoomToCursorActive;
        ZoomToCursorChanged?.Invoke(_zoomToCursorActive);
        InfoOccurred?.Invoke(_zoomToCursorActive
            ? $"Zoom-auf-Cursor aktiviert (Radius {_config.ZoomToCursorRadius}px) — nächste Screenshots erfassen nur den Bereich um den Mauszeiger."
            : "Zoom-auf-Cursor deaktiviert — Screenshots erfassen wieder das ganze Fenster.");
    }

    /// <summary>Whether the active output mode supports branching (Canvas/Excalidraw/DrawIo vs. plain Note).</summary>
    public bool SupportsBranching => ActiveFlowWriter is not null;

    private IFlowWriter? ActiveFlowWriter => _config.OutputMode switch
    {
        "Canvas" => _canvasWriter,
        "Excalidraw" => _excalidrawWriter,
        "DrawIo" => _drawIoWriter,
        _ => null
    };

    public SessionManager(AppConfig config)
    {
        _config = config;
        _noteWriter = new ObsidianWriter(config);
        _canvasWriter = new CanvasFlowWriter(config);
        _excalidrawWriter = new ExcalidrawFlowWriter(config);
        _drawIoWriter = new DrawIoFlowWriter(config);
        _mouseHook.LeftButtonDown += OnLeftButtonDown;
        _mouseHook.RightButtonDown += OnRightButtonDown;
        _keyboardHook.EnterPressed += OnEnterPressed;
    }

    /// <summary>Extension for a given <see cref="AppConfig.OutputMode"/> value.</summary>
    public static string ExtensionForOutputMode(string outputMode) => outputMode switch
    {
        "Canvas" => ".canvas",
        "Excalidraw" => ".excalidraw",
        "DrawIo" => ".drawio",
        _ => ".md"
    };

    /// <summary>
    /// Existing files for the active output mode anywhere under the
    /// configured vault/target folder (as paths relative to it, so files
    /// filed into subfolders stay distinguishable), newest first — used by
    /// the session-start file picker and the "Ablauf fortsetzen" tray
    /// action. Every session now requires an explicit file (chosen or
    /// newly named) instead of an auto-generated name, so callers always
    /// have something to list.
    /// </summary>
    public List<string> ListExistingFiles()
    {
        if (string.IsNullOrWhiteSpace(_config.VaultPath) || !Directory.Exists(_config.VaultPath))
        {
            return new List<string>();
        }

        var extension = ExtensionForOutputMode(_config.OutputMode);
        return Directory.GetFiles(_config.VaultPath, "*" + extension, SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_config.VaultPath, f))
            .OrderByDescending(f => File.GetLastWriteTimeUtc(Path.Combine(_config.VaultPath, f)))
            .ToList();
    }

    /// <summary>
    /// Starts a session against an explicit target file name (with
    /// extension, optionally prefixed with a subfolder path) — see
    /// <see cref="ListExistingFiles"/>. The target subfolder is created if
    /// it doesn't exist yet, so a freshly typed folder name in the
    /// session-start dialog works immediately.
    /// </summary>
    public void Start(string targetFileName)
    {
        _currentTargetFileName = targetFileName;

        var targetDirectory = Path.GetDirectoryName(Path.Combine(_config.VaultPath, targetFileName));
        if (!string.IsNullOrEmpty(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        ActiveFlowWriter?.StartSession(_currentTargetFileName);

        _mouseHook.Start();
        if (_config.CaptureOnEnter)
        {
            _keyboardHook.Start();
        }

        _isRunning = true;
        if (ActiveFlowWriter is { } writer)
        {
            CanvasStatusChanged?.Invoke(BuildStatusText());
            FlowPreviewChanged?.Invoke(writer.GetPreview());
        }
        LogService.Log($"Session gestartet. Ziel: {_currentTargetFileName} (Modus: {_config.OutputMode}), Vault: '{_config.VaultPath}'");
    }

    public void Stop()
    {
        _mouseHook.Stop();
        _keyboardHook.Stop();
        ActiveFlowWriter?.Stop();
        _isRunning = false;
        BranchDepthChanged?.Invoke(0);
        CanvasStatusChanged?.Invoke(null);
        // Deliberately NOT FlowPreviewChanged?.Invoke(null) — that would
        // hide the Ablauf-Übersicht minimap on every Stop. Leaving it
        // showing its last state lets it double as a reference while
        // reviewing/planning the next session; it only actually closes via
        // JumpToNode's own !_isRunning guard already making clicks in it a
        // no-op, so nothing breaks by it staying open.
        LogService.Log("Session gestoppt.");
    }

    /// <summary>"Neue Session"-Aktion: closes out the current target file (a normal Stop) and immediately starts a fresh, explicitly-named one.</summary>
    public void StartNewSession(string targetFileName)
    {
        Stop();
        Start(targetFileName);
    }

    private string BuildStatusText()
    {
        var writer = ActiveFlowWriter!;
        var branches = writer.ListBranchAnchorNames();
        var currentBranch = writer.CurrentBranchName ?? "Hauptablauf";
        var branchesInfo = branches.Count > 0 ? $"Branches: {string.Join(", ", branches)}" : "Keine Branches gesetzt";
        var label = writer.CurrentNodeLabel ?? "(noch kein Klick)";
        return $"{_config.OutputMode}: {currentBranch}\n{branchesInfo}\nZuletzt: {label}";
    }

    /// <summary>Names of all currently defined branch anchors, for the "Branch auswählen" picker.</summary>
    public List<string> ListBranchAnchorNames() => ActiveFlowWriter?.ListBranchAnchorNames() ?? new List<string>();

    /// <summary>Name of the branch the cursor is currently positioned in, or null for the main flow.</summary>
    public string? CurrentBranchName => ActiveFlowWriter?.CurrentBranchName;

    /// <summary>Hotkey/button action: turn the current node into a named branch point and jump the cursor onto it — the next click attaches under it in a new column.</summary>
    public void MarkBranchAnchor(string branchName)
    {
        if (!_isRunning || ActiveFlowWriter is not { } writer)
        {
            InfoOccurred?.Invoke("Aktion ignoriert: aktueller Ausgabemodus unterstützt keine Abzweigungen.");
            return;
        }

        var result = writer.MarkBranchAnchor(branchName);
        if (result.Success)
        {
            BranchDepthChanged?.Invoke(result.Depth);
            CanvasStatusChanged?.Invoke(BuildStatusText());
            FlowPreviewChanged?.Invoke(writer.GetPreview());
            InfoOccurred?.Invoke($"Branch \"{branchName}\" gesetzt — nächster Klick beginnt dort eine neue Spalte/einen neuen Abschnitt.");
        }
        else
        {
            InfoOccurred?.Invoke("Noch kein Klick vorhanden, der als Branch-Punkt markiert werden könnte.");
        }
    }

    /// <summary>Hotkey/button action: move the cursor to a previously named branch anchor.</summary>
    public void JumpToAnchor(string branchName)
    {
        if (!_isRunning || ActiveFlowWriter is not { } writer)
        {
            InfoOccurred?.Invoke("Aktion ignoriert: aktueller Ausgabemodus unterstützt keine Abzweigungen.");
            return;
        }

        var result = writer.JumpToAnchor(branchName);
        if (result.Success)
        {
            BranchDepthChanged?.Invoke(result.Depth);
            CanvasStatusChanged?.Invoke(BuildStatusText());
            FlowPreviewChanged?.Invoke(writer.GetPreview());
            InfoOccurred?.Invoke($"Zu Branch \"{branchName}\" gesprungen — nächster Klick beginnt dort eine neue Spalte/einen neuen Abschnitt.");
        }
        else
        {
            InfoOccurred?.Invoke($"Branch \"{branchName}\" nicht gefunden.");
        }
    }

    /// <summary>Tree-preview overlay's click-to-navigate: jumps the cursor to an arbitrary existing node (not just a named branch anchor).</summary>
    public void JumpToNode(string nodeId)
    {
        if (!_isRunning || ActiveFlowWriter is not { } writer)
        {
            InfoOccurred?.Invoke("Aktion ignoriert: aktueller Ausgabemodus unterstützt keine Navigation.");
            return;
        }

        var result = writer.JumpToNode(nodeId);
        if (result.Success)
        {
            BranchDepthChanged?.Invoke(result.Depth);
            CanvasStatusChanged?.Invoke(BuildStatusText());
            FlowPreviewChanged?.Invoke(writer.GetPreview());
            InfoOccurred?.Invoke($"Zu \"{writer.CurrentNodeLabel ?? "(ohne Beschreibung)"}\" gesprungen — nächster Klick knüpft hier an.");
        }
        else
        {
            InfoOccurred?.Invoke("Knoten nicht gefunden.");
        }
    }

    /// <summary>For the "Ablauf fortsetzen ab Punkt..." picker: nodes already in <paramref name="fileName"/>.</summary>
    public List<ResumableNode> ListResumableCanvasNodes(string fileName) =>
        ActiveFlowWriter?.ListNodesForResume(fileName) ?? new List<ResumableNode>();

    /// <summary>Queues a chosen node as the starting point of the next Start() call.</summary>
    public void SetResumeAnchor(ResumableNode node) => ActiveFlowWriter?.SetResumeAnchor(node);

    private void OnLeftButtonDown(object? sender, MouseClickEventArgs e) => HandleMouseButtonDown(e, isRightClick: false);

    private void OnRightButtonDown(object? sender, MouseClickEventArgs e)
    {
        if (!_config.CaptureOnRightClick)
        {
            return;
        }

        HandleMouseButtonDown(e, isRightClick: true);
    }

    private void HandleMouseButtonDown(MouseClickEventArgs e, bool isRightClick)
    {
        if (IsPointOnOwnUi?.Invoke(e.Point) == true)
        {
            // Never counts as a "skipped" click either (no sound, no
            // balloon) — this isn't a deliberate skip, it's not a content
            // click at all, just interaction with DocuClick's own UI.
            LogService.Log($"Klick bei ({e.Point.X}, {e.Point.Y}) ignoriert (DocuClick-eigene UI).");
            return;
        }

        if (IsSkipModifierDown(e.ShiftDown, e.ControlDown, e.AltDown))
        {
            LogService.Log($"Klick bei ({e.Point.X}, {e.Point.Y}) übersprungen ({_config.SkipRecordingModifier}-Taste gedrückt).");
            if (_config.EnableClickSound)
            {
                ClickFeedbackService.PlaySkipped();
            }
            return;
        }

        // Copy everything the hook thread needs to hand off, then return
        // immediately — see the warning in MouseHookService about hooks
        // that block the message queue for too long.
        var point = e.Point;
        var timestamp = e.Timestamp;
        var targetFileName = _currentTargetFileName;

        LogService.Log($"{(isRightClick ? "Rechtsklick" : "Klick")} erkannt bei ({point.X}, {point.Y}).");
        Task.Run(() => ProcessClick(point, timestamp, targetFileName, isRightClick));
    }

    private void OnEnterPressed(object? sender, EnterKeyEventArgs e)
    {
        if (IsSkipModifierDown(e.ShiftDown, e.ControlDown, e.AltDown))
        {
            LogService.Log($"Enter-Erfassung übersprungen ({_config.SkipRecordingModifier}-Taste gedrückt).");
            if (_config.EnableClickSound)
            {
                ClickFeedbackService.PlaySkipped();
            }
            return;
        }

        var timestamp = e.Timestamp;
        var targetFileName = _currentTargetFileName;

        LogService.Log("Enter-Taste erkannt.");
        Task.Run(() => ProcessEnterPress(timestamp, targetFileName));
    }

    private bool IsSkipModifierDown(bool shiftDown, bool controlDown, bool altDown) => _config.SkipRecordingModifier switch
    {
        "Shift" => shiftDown,
        "Control" => controlDown,
        "Alt" => altDown,
        _ => false
    };

    private void ProcessClick(Point point, DateTime timestamp, string targetFileName, bool isRightClick)
    {
        var element = _config.UseUiAutomation ? UiAutomationService.GetElementAt(point) : null;
        var fallbackWindowTitle = element?.WindowTitle ?? ForegroundWindowService.GetTitle();
        var action = isRightClick ? InputAction.RightClick : InputAction.Click;
        var description = DescriptionGenerator.Describe(element, fallbackWindowTitle, timestamp, action);

        Func<CapturedWindow> captureFunc = _zoomToCursorActive
            ? () => ScreenshotService.CaptureAroundPoint(point, _config.ZoomToCursorRadius)
            : () => ScreenshotService.CaptureWindowAt(point);

        FinalizeCapture(description, timestamp, captureFunc, element, point, targetFileName);
    }

    private void ProcessEnterPress(DateTime timestamp, string targetFileName)
    {
        var element = _config.UseUiAutomation ? UiAutomationService.GetFocusedElement() : null;
        var fallbackWindowTitle = element?.WindowTitle ?? ForegroundWindowService.GetTitle();
        var description = DescriptionGenerator.Describe(element, fallbackWindowTitle, timestamp, InputAction.EnterKey);

        // No click point exists for a key press; the highlight (if any)
        // comes purely from the focused element's bounding rect.
        FinalizeCapture(description, timestamp, ScreenshotService.CaptureForegroundWindow, element, null, targetFileName);
    }

    private void FinalizeCapture(
        string description,
        DateTime timestamp,
        Func<CapturedWindow> captureFunc,
        ElementInfo? element,
        Point? clickPoint,
        string targetFileName)
    {
        try
        {
            var captured = captureFunc();
            using var screenshot = captured.Bitmap;
            var highlightColor = ColorTranslator.FromHtml(_config.HighlightColorHex);

            // A UIA element occasionally reports its parent pane/window as
            // its own bounding rect (poor automation trees, clicks on window
            // chrome, ...). Drawing that as a "highlight" would paint most
            // of the screenshot red, so only trust boxes that are clearly
            // smaller than the captured window itself; anything bigger
            // falls back to a plain click-circle (or no mark at all for the
            // Enter trigger, which has no click point).
            var rect = element?.BoundingRectangle;
            var useBoundingBox = rect is { Width: > 0, Height: > 0 } r
                && r.Width <= captured.Bounds.Width * 0.9
                && r.Height <= captured.Bounds.Height * 0.9;

            if (useBoundingBox)
            {
                var localRect = ScreenshotService.ToLocal(rect!.Value, captured.Bounds);
                HighlightRenderer.DrawBoundingBox(screenshot, localRect, highlightColor, _config.HighlightThickness);
            }
            else if (clickPoint is { } point)
            {
                var localPoint = ScreenshotService.ToLocal(point, captured.Bounds);
                HighlightRenderer.DrawClickCircle(screenshot, localPoint, highlightColor, _config.HighlightRadius, _config.HighlightThickness);
            }

            if (ActiveFlowWriter is { } writer)
            {
                writer.AddClickNode(description, screenshot, timestamp);
                FlowPreviewChanged?.Invoke(writer.GetPreview());
            }
            else
            {
                _noteWriter.AppendEntry(targetFileName, description, screenshot, timestamp);
            }

            LogService.Log($"Eintrag geschrieben: \"{description}\" -> {targetFileName}");

            if (LastScreenshotCaptured is not null)
            {
                using var thumbnailStream = new MemoryStream();
                screenshot.Save(thumbnailStream, System.Drawing.Imaging.ImageFormat.Png);
                LastScreenshotCaptured.Invoke(thumbnailStream.ToArray());
            }

            if (_config.EnableClickSound)
            {
                ClickFeedbackService.PlayCaptured();
            }
        }
        catch (Exception ex)
        {
            // One failed capture (missing vault path, UIA hiccup, locked
            // file, ...) must not tear down the running session.
            LogService.Log($"Erfassung fehlgeschlagen: {ex}");
            if (_config.EnableClickSound)
            {
                ClickFeedbackService.PlayError();
            }
            ErrorOccurred?.Invoke(ex.Message);
        }
    }

    public void Dispose()
    {
        _mouseHook.Dispose();
        _keyboardHook.Dispose();
    }
}
