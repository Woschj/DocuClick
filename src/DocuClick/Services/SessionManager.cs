using System.Drawing;
using System.IO;

namespace DocuClick.Services;

/// <summary>
/// Glues the mouse/keyboard hooks to the capture pipeline (UI Automation
/// lookup -> screenshot -> highlight -> write) and owns the current
/// session's target file. Writes either a linear note (ObsidianWriter) or
/// a branching flow (<see cref="IFlowWriter"/>: Obsidian Canvas or Word),
/// depending on <see cref="AppConfig.OutputMode"/>.
/// </summary>
public sealed class SessionManager : IDisposable
{
    private readonly MouseHookService _mouseHook = new();
    private readonly KeyboardHookService _keyboardHook = new();
    private readonly AppConfig _config;
    private readonly ObsidianWriter _noteWriter;
    private readonly CanvasFlowWriter _canvasWriter;
    private readonly WordFlowWriter _wordWriter;
    private string _currentTargetFileName = string.Empty;
    private bool _isRunning;

    public event Action<string>? ErrorOccurred;
    public event Action<string>? InfoOccurred;
    public event Action<int>? BranchDepthChanged;
    public event Action<string?>? CanvasStatusChanged;

    public bool IsRunning => _isRunning;

    /// <summary>Whether the active output mode supports branching (Canvas/Word vs. plain Note).</summary>
    public bool SupportsBranching => ActiveFlowWriter is not null;

    private IFlowWriter? ActiveFlowWriter => _config.OutputMode switch
    {
        "Canvas" => _canvasWriter,
        "Word" => _wordWriter,
        _ => null
    };

    public SessionManager(AppConfig config)
    {
        _config = config;
        _noteWriter = new ObsidianWriter(config);
        _canvasWriter = new CanvasFlowWriter(config);
        _wordWriter = new WordFlowWriter(config);
        _mouseHook.LeftButtonDown += OnLeftButtonDown;
        _keyboardHook.EnterPressed += OnEnterPressed;
    }

    /// <summary>Extension for a given <see cref="AppConfig.OutputMode"/> value.</summary>
    public static string ExtensionForOutputMode(string outputMode) => outputMode switch
    {
        "Canvas" => ".canvas",
        "Word" => ".docx",
        _ => ".md"
    };

    /// <summary>
    /// Existing files for the active output mode in the configured
    /// vault/target folder, newest first — used by the session-start file
    /// picker and the "Ablauf fortsetzen" tray action. Every session now
    /// requires an explicit file (chosen or newly named) instead of an
    /// auto-generated name, so callers always have something to list.
    /// </summary>
    public List<string> ListExistingFiles()
    {
        if (string.IsNullOrWhiteSpace(_config.VaultPath) || !Directory.Exists(_config.VaultPath))
        {
            return new List<string>();
        }

        var extension = ExtensionForOutputMode(_config.OutputMode);
        return Directory.GetFiles(_config.VaultPath, "*" + extension)
            .Select(f => Path.GetFileName(f) ?? f)
            .OrderByDescending(f => File.GetLastWriteTimeUtc(Path.Combine(_config.VaultPath, f)))
            .ToList();
    }

    /// <summary>Starts a session against an explicit target file name (with extension) — see <see cref="ListExistingFiles"/>.</summary>
    public void Start(string targetFileName)
    {
        _currentTargetFileName = targetFileName;

        ActiveFlowWriter?.StartSession(_currentTargetFileName);

        _mouseHook.Start();
        if (_config.CaptureOnEnter)
        {
            _keyboardHook.Start();
        }

        _isRunning = true;
        if (ActiveFlowWriter is not null)
        {
            CanvasStatusChanged?.Invoke(BuildStatusText());
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
        var depth = writer.BranchDepth;
        var branchText = depth > 0 ? $"Branch-Tiefe {depth}" : "Hauptablauf";
        var label = writer.CurrentNodeLabel ?? "(noch kein Klick)";
        return $"{_config.OutputMode}: {branchText}\nZuletzt: {label}";
    }

    /// <summary>Hotkey action: bookmark the current node as a branch point.</summary>
    public void MarkBranchAnchor()
    {
        if (!_isRunning || ActiveFlowWriter is not { } writer)
        {
            InfoOccurred?.Invoke("Hotkey ignoriert: aktueller Ausgabemodus unterstützt keine Abzweigungen.");
            return;
        }

        var result = writer.MarkBranchAnchor();
        if (result.Success)
        {
            BranchDepthChanged?.Invoke(result.Depth);
            CanvasStatusChanged?.Invoke(BuildStatusText());
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
        if (!_isRunning || ActiveFlowWriter is not { } writer)
        {
            InfoOccurred?.Invoke("Hotkey ignoriert: aktueller Ausgabemodus unterstützt keine Abzweigungen.");
            return;
        }

        var result = writer.JumpToLastAnchor();
        if (result.Success)
        {
            BranchDepthChanged?.Invoke(result.Depth);
            CanvasStatusChanged?.Invoke(BuildStatusText());
            InfoOccurred?.Invoke($"Neue Abzweigung von Punkt #{result.Depth} ({result.AnchorLabel}) — nächster Klick startet die neue Spalte.");
        }
        else
        {
            InfoOccurred?.Invoke("Kein Abzweigungspunkt gesetzt (erst mit dem Mark-Hotkey einen setzen).");
        }
    }

    /// <summary>For the "Ablauf fortsetzen ab Punkt..." picker: nodes already in <paramref name="fileName"/>.</summary>
    public List<ResumableNode> ListResumableCanvasNodes(string fileName) =>
        ActiveFlowWriter?.ListNodesForResume(fileName) ?? new List<ResumableNode>();

    /// <summary>Queues a chosen node as the starting point of the next Start() call.</summary>
    public void SetResumeAnchor(ResumableNode node) => ActiveFlowWriter?.SetResumeAnchor(node);

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

            if (ActiveFlowWriter is { } writer)
            {
                writer.AddClickNode(description, screenshot, timestamp);
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
