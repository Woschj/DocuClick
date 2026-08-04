using System.Drawing;
using System.IO;

namespace DocuClick.Services;

/// <summary>
/// Glues the mouse hook to the capture pipeline (UI Automation lookup ->
/// screenshot -> highlight -> Obsidian write) and owns the current
/// session's target file. Writes either a linear note or a branching
/// .canvas flow diagram, depending on <see cref="AppConfig.UseCanvas"/>.
/// </summary>
public sealed class SessionManager : IDisposable
{
    private readonly MouseHookService _mouseHook = new();
    private readonly AppConfig _config;
    private readonly ObsidianWriter _noteWriter;
    private readonly CanvasFlowWriter _canvasWriter;
    private string _currentTargetFileName = string.Empty;
    private bool _isRunning;

    public event Action<string>? ErrorOccurred;
    public event Action<string>? InfoOccurred;
    public event Action<int>? BranchDepthChanged;

    public bool IsRunning => _isRunning;

    public SessionManager(AppConfig config)
    {
        _config = config;
        _noteWriter = new ObsidianWriter(config);
        _canvasWriter = new CanvasFlowWriter(config);
        _mouseHook.LeftButtonDown += OnLeftButtonDown;
    }

    public void Start()
    {
        _currentTargetFileName = ResolveTargetFileName();

        if (_config.UseCanvas)
        {
            _canvasWriter.StartSession(_currentTargetFileName);
        }

        _mouseHook.Start();
        _isRunning = true;
        LogService.Log($"Session gestartet. Ziel: {_currentTargetFileName} (Canvas: {_config.UseCanvas}), Vault: '{_config.VaultPath}'");
    }

    public void Stop()
    {
        _mouseHook.Stop();
        _canvasWriter.Stop();
        _isRunning = false;
        BranchDepthChanged?.Invoke(0);
        LogService.Log("Session gestoppt.");
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
        if (IsSkipModifierDown(e))
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

    private bool IsSkipModifierDown(MouseClickEventArgs e) => _config.SkipRecordingModifier switch
    {
        "Shift" => e.ShiftDown,
        "Control" => e.ControlDown,
        "Alt" => e.AltDown,
        _ => false
    };

    private void ProcessClick(Point point, DateTime timestamp, string targetFileName)
    {
        try
        {
            var element = _config.UseUiAutomation ? UiAutomationService.GetElementAt(point) : null;
            var fallbackWindowTitle = element?.WindowTitle ?? ForegroundWindowService.GetTitle();
            var description = DescriptionGenerator.Describe(element, fallbackWindowTitle, timestamp);

            using var screenshot = ScreenshotService.CaptureMonitorAt(point);
            var highlightColor = ColorTranslator.FromHtml(_config.HighlightColorHex);

            if (element?.BoundingRectangle is { } rect)
            {
                var localRect = ScreenshotService.ToMonitorLocal(rect, point);
                HighlightRenderer.DrawBoundingBox(screenshot, localRect, highlightColor, _config.HighlightThickness);
            }
            else
            {
                var localPoint = ScreenshotService.ToMonitorLocal(point);
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
            LogService.Log($"Klick-Verarbeitung fehlgeschlagen: {ex}");
            if (_config.EnableClickSound)
            {
                ClickFeedbackService.PlayError();
            }
            ErrorOccurred?.Invoke(ex.Message);
        }
    }

    public void Dispose() => _mouseHook.Dispose();
}
