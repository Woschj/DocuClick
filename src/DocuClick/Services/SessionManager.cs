using System.Drawing;
using System.IO;

namespace DocuClick.Services;

/// <summary>
/// Glues the mouse/keyboard hooks to the capture pipeline (UI Automation
/// lookup -> screenshot -> highlight -> Obsidian write) and owns the
/// current session's target file. Writes either a linear note or a
/// branching .canvas flow diagram, depending on
/// <see cref="AppConfig.UseCanvas"/>.
/// </summary>
public sealed class SessionManager : IDisposable
{
    private readonly MouseHookService _mouseHook = new();
    private readonly KeyboardHookService _keyboardHook = new();
    private readonly AppConfig _config;
    private readonly ObsidianWriter _noteWriter;
    private readonly CanvasFlowWriter _canvasWriter;
    private string _currentTargetFileName = string.Empty;
    private bool _isRunning;

    public event Action<string>? ErrorOccurred;
    public event Action<string>? InfoOccurred;
    public event Action<int>? BranchDepthChanged;
    public event Action<string?>? CanvasStatusChanged;

    public bool IsRunning => _isRunning;

    public SessionManager(AppConfig config)
    {
        _config = config;
        _noteWriter = new ObsidianWriter(config);
        _canvasWriter = new CanvasFlowWriter(config);
        _mouseHook.LeftButtonDown += OnLeftButtonDown;
        _keyboardHook.EnterPressed += OnEnterPressed;
    }

    public void Start()
    {
        _currentTargetFileName = ResolveTargetFileName();

        if (_config.UseCanvas)
        {
            _canvasWriter.StartSession(_currentTargetFileName);
        }

        _mouseHook.Start();
        if (_config.CaptureOnEnter)
        {
            _keyboardHook.Start();
        }

        _isRunning = true;
        if (_config.UseCanvas)
        {
            CanvasStatusChanged?.Invoke(BuildCanvasStatusText());
        }
        LogService.Log($"Session gestartet. Ziel: {_currentTargetFileName} (Canvas: {_config.UseCanvas}), Vault: '{_config.VaultPath}'");
    }

    public void Stop()
    {
        _mouseHook.Stop();
        _keyboardHook.Stop();
        _canvasWriter.Stop();
        _isRunning = false;
        BranchDepthChanged?.Invoke(0);
        CanvasStatusChanged?.Invoke(null);
        LogService.Log("Session gestoppt.");
    }

    private string BuildCanvasStatusText()
    {
        var depth = _canvasWriter.BranchDepth;
        var branchText = depth > 0 ? $"Branch-Tiefe {depth}" : "Hauptablauf";
        var label = _canvasWriter.CurrentNodeLabel ?? "(noch kein Klick)";
        return $"Canvas: {branchText}\nZuletzt: {label}";
    }

    /// <summary>Hotkey action: bookmark the current node as a branch point.</summary>
    public void MarkBranchAnchor()
    {
        if (!_isRunning || !_config.UseCanvas)
        {
            InfoOccurred?.Invoke("Hotkey ignoriert: keine laufende Canvas-Session.");
            return;
        }

        var result = _canvasWriter.MarkBranchAnchor();
        if (result.Success)
        {
            BranchDepthChanged?.Invoke(result.Depth);
            CanvasStatusChanged?.Invoke(BuildCanvasStatusText());
            InfoOccurred?.Invoke($"Abzweigungspunkt #{result.Depth} gesetzt bei: {result.AnchorLabel}");
        }
        else
        {
            InfoOccurred?.Invoke("Noch kein Klick vorhanden, der als Abzweigungspunkt markiert werden könnte.");
        }
    }

    /// <summary>Hotkey action: rewind the cursor to the last bookmarked branch point.</summary>
    public void JumpToLastAnchor()
    {
        if (!_isRunning || !_config.UseCanvas)
        {
            InfoOccurred?.Invoke("Hotkey ignoriert: keine laufende Canvas-Session.");
            return;
        }

        var result = _canvasWriter.JumpToLastAnchor();
        if (result.Success)
        {
            BranchDepthChanged?.Invoke(result.Depth);
            CanvasStatusChanged?.Invoke(BuildCanvasStatusText());
            InfoOccurred?.Invoke($"Neue Abzweigung von Punkt #{result.Depth} ({result.AnchorLabel}) — nächster Klick startet die neue Spalte.");
        }
        else
        {
            InfoOccurred?.Invoke("Kein Abzweigungspunkt gesetzt (erst mit dem Mark-Hotkey einen setzen).");
        }
    }

    /// <summary>For the "Ablauf fortsetzen ab Punkt..." picker: nodes already in the target canvas file.</summary>
    public List<ResumableNode> ListResumableCanvasNodes() =>
        _canvasWriter.ListNodesForResume(ResolveTargetFileName());

    /// <summary>Queues a chosen node as the starting point of the next Start() call.</summary>
    public void SetResumeAnchor(ResumableNode node) => _canvasWriter.SetResumeAnchor(node);

    private string ResolveTargetFileName()
    {
        var extension = _config.UseCanvas ? ".canvas" : ".md";

        if (!_config.NewNotePerSession && !string.IsNullOrWhiteSpace(_config.FixedNoteName))
        {
            return Path.GetFileNameWithoutExtension(_config.FixedNoteName) + extension;
        }

        return $"Screenshots {DateTime.Now:yyyy-MM-dd}{extension}";
    }

    private void OnLeftButtonDown(object? sender, MouseClickEventArgs e)
    {
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

        LogService.Log($"Klick erkannt bei ({point.X}, {point.Y}).");
        Task.Run(() => ProcessClick(point, timestamp, targetFileName));
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

    private void ProcessClick(Point point, DateTime timestamp, string targetFileName)
    {
        var element = _config.UseUiAutomation ? UiAutomationService.GetElementAt(point) : null;
        var fallbackWindowTitle = element?.WindowTitle ?? ForegroundWindowService.GetTitle();
        var description = DescriptionGenerator.Describe(element, fallbackWindowTitle, timestamp, InputAction.Click);

        FinalizeCapture(description, timestamp, () => ScreenshotService.CaptureWindowAt(point), element, point, targetFileName);
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

            if (_config.UseCanvas)
            {
                _canvasWriter.AddClickNode(description, screenshot, timestamp);
            }
            else
            {
                _noteWriter.AppendEntry(targetFileName, description, screenshot, timestamp);
            }

            LogService.Log($"Eintrag geschrieben: \"{description}\" -> {targetFileName}");

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
