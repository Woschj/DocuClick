using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Threading;

namespace DocuClick.Services;

/// <summary>
/// Glues the mouse/keyboard hooks to the capture pipeline (UI Automation
/// lookup -> screenshot -> highlight -> write) and owns the current
/// session's target file. Writes a branching flow via
/// <see cref="CanvasFlowWriter"/> (the sole <see cref="IFlowWriter"/>
/// implementation — a separate, non-branching plain-note writer existed
/// once and was removed once the HTML flow format no longer needed
/// Obsidian). A draw.io export is always available afterward instead of
/// recording into it directly — see <see cref="DrawIoConverter"/>.
/// </summary>
public sealed class SessionManager : IDisposable
{
    private readonly MouseHookService _mouseHook = new();
    private readonly KeyboardHookService _keyboardHook = new();
    private readonly AppConfig _config;
    private readonly CanvasFlowWriter _writer;
    private string _currentTargetFileName = string.Empty;
    private bool _isRunning;
    private bool _zoomToCursorActive;

    // Every touch of a writer's mutable cursor/anchor state — a captured
    // click, Start/Stop, a branch mark/jump — funnels through this single
    // background thread instead of running wherever it happens to be
    // called from. Without this, clicks were dispatched via independent
    // Task.Run calls with no ordering or mutual exclusion at all: two
    // clicks (or a click racing a branch jump) could interleave their
    // reads/writes of _cursorNodeId/_cursorX/_cursorY, corrupting node
    // positions/edges — the exact "screenshots overwritten" / "minimap
    // doesn't match the actual file" symptoms this fixes. A single serial
    // worker guarantees both mutual exclusion AND that actions execute in
    // the exact order they were captured, which a lock alone would not.
    private readonly BlockingCollection<Action> _writerQueue = new();
    private readonly Thread _writerThread;

    public event Action<string>? ErrorOccurred;
    public event Action<string>? InfoOccurred;
    public event Action<string?>? CanvasStatusChanged;
    public event Action<bool>? ZoomToCursorChanged;

    /// <summary>Fired whenever the flow's nodes/current-position change (session start/stop, every click, branch actions, node jumps) — for the tree-preview overlay. Second arg is true ONLY when a live screenshot click was recorded.</summary>
    public event Action<FlowPreview?, bool>? FlowPreviewChanged;

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

    /// <summary>
    /// "Does one of DocuClick's own windows currently have focus?" — for
    /// the global Enter-key hook, which (unlike a mouse click) has no
    /// point to check against <see cref="IsPointOnOwnUi"/>. Without this,
    /// confirming a modal dialog with Enter (e.g. naming a new path in
    /// BranchNameWindow) was itself captured as a content click — the
    /// keyboard hook sees every Enter press system-wide regardless of
    /// which window has focus, exactly the same blind spot the mouse hook
    /// already has a check for (confirmed as a real bug: naming a path
    /// saved a screenshot of the naming dialog itself as a new step).
    /// </summary>
    public Func<bool>? IsOwnUiFocused { get; set; }

    public bool IsRunning => _isRunning;

    /// <summary>
    /// The target file of the current (or, once stopped, the most recent)
    /// session — null before any session has ever run. Lets the
    /// Ablauf-Übersicht overlay resolve a clicked node back to "which file
    /// is this actually in" for the resume-from-point flow (see
    /// <see cref="ListResumableCanvasNodes"/>/<see cref="SetResumeAnchor"/>)
    /// without a separate file picker — the overlay already only ever
    /// shows this file's content.
    /// </summary>
    public string? CurrentTargetFileName => string.IsNullOrEmpty(_currentTargetFileName) ? null : _currentTargetFileName;

    /// <summary>Whether a session target file is loaded and active (running or paused).</summary>
    public bool HasActiveSession => !string.IsNullOrEmpty(_currentTargetFileName);

    /// <summary>Whether a session is currently loaded but recording is paused.</summary>
    public bool IsPaused => HasActiveSession && !_isRunning;

    /// <summary>Retrieves the current flow preview snapshot from the writer thread.</summary>
    public FlowPreview? GetPreview() => RunOnWriterQueue(() => _writer.GetPreview());

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

    /// <summary>Always true now that CanvasFlowWriter is the only writer — kept so App.xaml.cs's branch-button gating doesn't need its own special case.</summary>
    public bool SupportsBranching => true;

    public SessionManager(AppConfig config)
    {
        _config = config;
        _writer = new CanvasFlowWriter(config);
        _mouseHook.LeftButtonDown += OnLeftButtonDown;
        _mouseHook.RightButtonDown += OnRightButtonDown;
        _keyboardHook.EnterPressed += OnEnterPressed;

        _writerThread = new Thread(RunWriterQueue) { IsBackground = true, Name = "DocuClick-Writer" };
        _writerThread.Start();
    }

    private void RunWriterQueue()
    {
        foreach (var action in _writerQueue.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                // Queued actions are expected to catch their own errors
                // (FinalizeCapture does; RunOnWriterQueue<T> forwards them
                // to its caller via the TaskCompletionSource) — this is
                // only a last-resort backstop so a genuinely unexpected
                // exception can never kill this thread. If it did, every
                // click and branch action for the rest of the session
                // would silently do nothing from then on.
                LogService.Log($"Unerwarteter Fehler in der Writer-Queue: {ex}");
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> exclusively on the writer thread and
    /// blocks the caller until it completes, returning its result — the
    /// caller's own thread (UI thread for a branch action, this same
    /// writer thread for a queued click) is otherwise free to continue
    /// immediately afterward. <paramref name="work"/> must only touch
    /// writer state and build plain data to return (a preview snapshot,
    /// a status string, ...) — it must NOT raise any event that a
    /// subscriber marshals back to the UI thread via a *blocking*
    /// dispatcher call while still running here, or a caller blocked
    /// waiting on this same queue would deadlock against it. Callers fire
    /// their events themselves, afterward, using the returned result.
    /// </summary>
    private T RunOnWriterQueue<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _writerQueue.Add(() =>
        {
            try
            {
                tcs.SetResult(work());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private void RunOnWriterQueue(Action work) => RunOnWriterQueue<object?>(() =>
    {
        work();
        return null;
    });

    // A single, self-contained interactive .html file — see
    // CanvasFlowWriter.BuildLiveHtml — rather than a bare .canvas JSON file
    // only Obsidian could render. A session recorded before this switch is
    // still a .canvas file and won't show up in file pickers filtered by
    // this extension; CanvasDocumentIo.Load still reads its content if it's
    // ever opened directly by full path.
    public const string OutputExtension = ".html";

    /// <summary>
    /// Existing files anywhere under the configured output folder (as paths
    /// relative to it, so files filed into subfolders stay distinguishable),
    /// newest first — used by the session-start file picker and the "Ablauf
    /// fortsetzen" tray action. Every session now requires an explicit file
    /// (chosen or newly named) instead of an auto-generated name, so callers
    /// always have something to list.
    /// </summary>
    public List<string> ListExistingFiles()
    {
        if (string.IsNullOrWhiteSpace(_config.OutputPath) || !Directory.Exists(_config.OutputPath))
        {
            return new List<string>();
        }

        return Directory.GetFiles(_config.OutputPath, "*" + OutputExtension, SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_config.OutputPath, f))
            .OrderByDescending(f => File.GetLastWriteTimeUtc(Path.Combine(_config.OutputPath, f)))
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
        if (HasActiveSession && string.Equals(_currentTargetFileName, targetFileName, StringComparison.OrdinalIgnoreCase) && !_isRunning)
        {
            // Resume paused session without re-opening from scratch
            _mouseHook.Start();
            if (_config.CaptureOnEnter)
            {
                _keyboardHook.Start();
            }

            _isRunning = true;
            var resumeSnapshot = RunOnWriterQueue(() => new StatusSnapshot(BuildStatusText(), _writer.GetPreview()));
            CanvasStatusChanged?.Invoke(resumeSnapshot.StatusText);
            FlowPreviewChanged?.Invoke(resumeSnapshot.Preview, false);
            LogService.Log($"Session fortgesetzt. Ziel: {_currentTargetFileName}");
            return;
        }

        if (HasActiveSession && !_isRunning)
        {
            Stop();
        }

        _currentTargetFileName = targetFileName;

        var targetDirectory = Path.GetDirectoryName(Path.Combine(_config.OutputPath, targetFileName));
        if (!string.IsNullOrEmpty(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        // Runs on the writer thread (see RunOnWriterQueue) so it can never
        // race an already-queued click from a previous session that hasn't
        // finished processing yet.
        var snapshot = RunOnWriterQueue(() =>
        {
            _writer.StartSession(_currentTargetFileName);
            return new StatusSnapshot(BuildStatusText(), _writer.GetPreview());
        });

        _mouseHook.Start();
        if (_config.CaptureOnEnter)
        {
            _keyboardHook.Start();
        }

        _isRunning = true;
        CanvasStatusChanged?.Invoke(snapshot.StatusText);
        FlowPreviewChanged?.Invoke(snapshot.Preview, false);
        LogService.Log($"Session gestartet. Ziel: {_currentTargetFileName}, Ausgabeordner: '{_config.OutputPath}'");
    }

    /// <summary>
    /// Tray menu "Ablauf öffnen...": loads an existing Canvas file into the
    /// Ablauf-Übersicht for viewing/editing — rename/delete/connect/
    /// disconnect all already work independent of a live recording (see
    /// e.g. RenameNode's own doc comment) — without starting a recording
    /// session: no hooks armed, <see cref="IsRunning"/> stays false. Refuses
    /// while a session IS running, since it would otherwise silently
    /// redirect that session's writer (cursor/target file) onto a different
    /// file out from under it, corrupting the next captured click.
    /// </summary>
    public void OpenForEditing(string targetFileName)
    {
        if (_isRunning)
        {
            InfoOccurred?.Invoke("Aktion ignoriert: bitte zuerst die laufende Aufnahme stoppen.");
            return;
        }

        _currentTargetFileName = targetFileName;
        var preview = RunOnWriterQueue(() =>
        {
            _writer.StartSession(targetFileName);
            return _writer.GetPreview();
        });

        FlowPreviewChanged?.Invoke(preview, false);
        LogService.Log($"Ablauf zum Bearbeiten geöffnet: {targetFileName}");
    }

    /// <summary>
    /// Pauses recording: stops capture hooks and flushes changes to disk,
    /// while keeping the active target file, loaded document, and current cursor
    /// intact so editing in the Ablauf-Übersicht can proceed seamlessly.
    /// </summary>
    public void Pause()
    {
        _mouseHook.Stop();
        _keyboardHook.Stop();
        _isRunning = false;
        RunOnWriterQueue(() => _writer.Pause());
        CanvasStatusChanged?.Invoke("Aufnahme pausiert");
        var preview = RunOnWriterQueue(() => _writer.GetPreview());
        FlowPreviewChanged?.Invoke(preview, false);
        LogService.Log("Session pausiert.");
    }

    public void Stop()
    {
        _mouseHook.Stop();
        _keyboardHook.Stop();
        RunOnWriterQueue(() => _writer.Stop());
        _isRunning = false;
        _currentTargetFileName = string.Empty;
        CanvasStatusChanged?.Invoke(null);
        LogService.Log("Session gestoppt.");
    }

    /// <summary>Bundled result of a writer action performed on the writer thread — captured there so CanvasStatusChanged/FlowPreviewChanged always reflect that exact action's outcome, never a state read moments later from a different thread.</summary>
    private readonly record struct StatusSnapshot(string? StatusText, FlowPreview? Preview);

    /// <summary>"Neue Session"-Aktion: closes out the current target file (a normal Stop) and immediately starts a fresh, explicitly-named one.</summary>
    public void StartNewSession(string targetFileName)
    {
        Stop();
        Start(targetFileName);
    }

    private string BuildStatusText()
    {
        var label = _writer.CurrentNodeLabel ?? "(noch kein Klick)";
        return $"Zuletzt: {label}";
    }

    /// <summary>
    /// Hotkey/button action: turns the current node into a decision point
    /// (a small diamond) and immediately forks+jumps onto its first named
    /// path — App.xaml.cs prompts for <paramref name="firstPathName"/>
    /// before calling this, the same dialog "+ Neuer Pfad" uses. There's
    /// no unnamed default continuation: additional paths, or resuming this
    /// first one later, both happen by clicking the diamond in the
    /// Ablauf-Übersicht (see <see cref="ListPaths"/>/<see cref="StartNewPath"/>/
    /// <see cref="ContinuePath"/>).
    /// </summary>
    public void MarkDecisionPoint(string firstPathName)
    {
        if (!HasActiveSession)
        {
            InfoOccurred?.Invoke("Aktion ignoriert: bitte zuerst eine Aufnahme starten oder einen Ablauf öffnen.");
            return;
        }

        // The mutation, and everything read afterward to describe its
        // outcome, happen together on the writer thread — otherwise a
        // click already queued just before this call could run in between
        // the mutation and, say, BuildStatusText() reading the result,
        // showing state that doesn't match what was just done.
        var (result, snapshot) = RunOnWriterQueue(() =>
        {
            var actionResult = _writer.MarkDecisionPoint(firstPathName);
            var statusSnapshot = actionResult.Success ? new StatusSnapshot(BuildStatusText(), _writer.GetPreview()) : default;
            return (actionResult, statusSnapshot);
        });

        if (result.Success)
        {
            if (_isRunning)
            {
                CanvasStatusChanged?.Invoke(snapshot.StatusText);
            }

            FlowPreviewChanged?.Invoke(snapshot.Preview, false);
            InfoOccurred?.Invoke($"Abzweigungspunkt gesetzt, Pfad \"{firstPathName}\" gestartet — nächste Aufnahme beginnt dort.");
        }
        else
        {
            InfoOccurred?.Invoke("Noch kein Klick vorhanden, der als Abzweigungspunkt markiert werden könnte.");
        }
    }

    /// <summary>Ablauf-Übersicht popup: every path already forking from a clicked decision point.</summary>
    public List<PathInfo> ListPaths(string decisionPointId) =>
        RunOnWriterQueue(() => _writer.ListPaths(decisionPointId));

    /// <summary>Ablauf-Übersicht popup action: forks a brand-new named path from a decision point.</summary>
    public void StartNewPath(string decisionPointId, string pathName)
    {
        if (!HasActiveSession)
        {
            InfoOccurred?.Invoke("Aktion ignoriert: bitte zuerst eine Aufnahme starten oder einen Ablauf öffnen.");
            return;
        }

        var (result, snapshot) = RunOnWriterQueue(() =>
        {
            var actionResult = _writer.StartNewPath(decisionPointId, pathName);
            var statusSnapshot = actionResult.Success ? new StatusSnapshot(BuildStatusText(), _writer.GetPreview()) : default;
            return (actionResult, statusSnapshot);
        });

        if (result.Success)
        {
            if (_isRunning)
            {
                CanvasStatusChanged?.Invoke(snapshot.StatusText);
            }

            FlowPreviewChanged?.Invoke(snapshot.Preview, false);
            InfoOccurred?.Invoke($"Neuer Pfad \"{pathName}\" angelegt — nächste Aufnahme beginnt dort.");
        }
        else
        {
            InfoOccurred?.Invoke("Abzweigungspunkt nicht gefunden.");
        }
    }

    /// <summary>Ablauf-Übersicht popup action: resumes an existing path at wherever it currently ends.</summary>
    public void ContinuePath(string pathStartNodeId)
    {
        if (!HasActiveSession)
        {
            InfoOccurred?.Invoke("Aktion ignoriert: bitte zuerst eine Aufnahme starten oder einen Ablauf öffnen.");
            return;
        }

        var (result, snapshot) = RunOnWriterQueue(() =>
        {
            var actionResult = _writer.ContinuePath(pathStartNodeId);
            var statusSnapshot = actionResult.Success ? new StatusSnapshot(BuildStatusText(), _writer.GetPreview()) : default;
            return (actionResult, statusSnapshot);
        });

        if (result.Success)
        {
            if (_isRunning)
            {
                CanvasStatusChanged?.Invoke(snapshot.StatusText);
            }

            FlowPreviewChanged?.Invoke(snapshot.Preview, false);
            InfoOccurred?.Invoke("Pfad ausgewählt — nächste Aufnahme knüpft hier an.");
        }
        else
        {
            InfoOccurred?.Invoke("Pfad nicht gefunden.");
        }
    }

    /// <summary>Tree-preview overlay's click-to-navigate: jumps the cursor to an arbitrary existing (non-decision-point) node.</summary>
    public void JumpToNode(string nodeId)
    {
        if (!HasActiveSession)
        {
            InfoOccurred?.Invoke("Aktion ignoriert: bitte zuerst eine Aufnahme starten oder einen Ablauf öffnen.");
            return;
        }

        var (result, snapshot, currentLabel) = RunOnWriterQueue(() =>
        {
            var actionResult = _writer.JumpToNode(nodeId);
            return actionResult.Success
                ? (actionResult, new StatusSnapshot(BuildStatusText(), _writer.GetPreview()), _writer.CurrentNodeLabel)
                : (actionResult, default, null);
        });

        if (result.Success)
        {
            if (_isRunning)
            {
                CanvasStatusChanged?.Invoke(snapshot.StatusText);
            }

            FlowPreviewChanged?.Invoke(snapshot.Preview, false);
            InfoOccurred?.Invoke($"Zu \"{currentLabel ?? "(ohne Beschreibung)"}\" gesprungen — nächste Aufnahme knüpft hier an.");
        }
        else
        {
            InfoOccurred?.Invoke("Knoten nicht gefunden.");
        }
    }

    /// <summary>
    /// Ablauf-Übersicht: renames a node's label. Deliberately not gated on
    /// <see cref="_isRunning"/> like the branch/navigation actions above —
    /// this is a data edit, not something that affects where the next
    /// *live* click connects from, so it's just as useful to fix a typo
    /// right after stopping as it is mid-recording.
    /// </summary>
    public void RenameNode(string nodeId, string newLabel)
    {
        var (result, snapshot) = RunOnWriterQueue(() =>
        {
            var actionResult = _writer.RenameNode(nodeId, newLabel);
            var statusSnapshot = actionResult.Success ? new StatusSnapshot(BuildStatusText(), _writer.GetPreview()) : default;
            return (actionResult, statusSnapshot);
        });

        if (result.Success)
        {
            if (_isRunning)
            {
                CanvasStatusChanged?.Invoke(snapshot.StatusText);
            }

            FlowPreviewChanged?.Invoke(snapshot.Preview, false);
        }
        else
        {
            InfoOccurred?.Invoke("Umbenennen fehlgeschlagen.");
        }
    }

    /// <summary>
    /// Ablauf-Übersicht: deletes a node (cascading to its whole downstream
    /// subtree if it has more than one child — see
    /// <see cref="IFlowWriter.DeleteNode"/>). The overlay is responsible
    /// for confirming a cascading delete with the user before calling this;
    /// by the time it's called here, the action is final. Not gated on
    /// <see cref="_isRunning"/> — see <see cref="RenameNode"/>.
    /// </summary>
    public void DeleteNode(string nodeId)
    {
        var (result, snapshot) = RunOnWriterQueue(() =>
        {
            var actionResult = _writer.DeleteNode(nodeId);
            var statusSnapshot = actionResult.Success ? new StatusSnapshot(BuildStatusText(), _writer.GetPreview()) : default;
            return (actionResult, statusSnapshot);
        });

        if (result.Success)
        {
            if (_isRunning)
            {
                CanvasStatusChanged?.Invoke(snapshot.StatusText);
            }

            FlowPreviewChanged?.Invoke(snapshot.Preview, false);
        }
        else
        {
            InfoOccurred?.Invoke("Löschen fehlgeschlagen.");
        }
    }

    /// <summary>Ablauf-Übersicht: manually connects two nodes with a new edge (the "Verbinden" toolbar gesture). Not gated on <see cref="_isRunning"/> — see <see cref="RenameNode"/>.</summary>
    public void ConnectNodes(string fromNodeId, string toNodeId)
    {
        var (result, snapshot) = RunOnWriterQueue(() =>
        {
            var actionResult = _writer.ConnectNodes(fromNodeId, toNodeId);
            var statusSnapshot = actionResult.Success ? new StatusSnapshot(BuildStatusText(), _writer.GetPreview()) : default;
            return (actionResult, statusSnapshot);
        });

        if (result.Success)
        {
            if (_isRunning)
            {
                CanvasStatusChanged?.Invoke(snapshot.StatusText);
            }

            FlowPreviewChanged?.Invoke(snapshot.Preview, false);
        }
        else
        {
            InfoOccurred?.Invoke("Verbinden nicht möglich (ungültige Knoten, bereits verbunden, oder würde einen Kreis erzeugen).");
        }
    }

    /// <summary>Ablauf-Übersicht: removes an existing manual/recorded edge (right-click a connector). Not gated on <see cref="_isRunning"/> — see <see cref="RenameNode"/>.</summary>
    public void DisconnectNodes(string fromNodeId, string toNodeId)
    {
        var (result, snapshot) = RunOnWriterQueue(() =>
        {
            var actionResult = _writer.DisconnectNodes(fromNodeId, toNodeId);
            var statusSnapshot = actionResult.Success ? new StatusSnapshot(BuildStatusText(), _writer.GetPreview()) : default;
            return (actionResult, statusSnapshot);
        });

        if (result.Success)
        {
            if (_isRunning)
            {
                CanvasStatusChanged?.Invoke(snapshot.StatusText);
            }

            FlowPreviewChanged?.Invoke(snapshot.Preview, false);
        }
        else
        {
            InfoOccurred?.Invoke("Verbindung konnte nicht entfernt werden.");
        }
    }

    /// <summary>Ablauf-Übersicht: drag-to-move a card to an explicit position (see IFlowWriter.MoveNode — no auto-layout re-run, so it isn't undone by the very drag that just set it). Not gated on <see cref="_isRunning"/> — see <see cref="RenameNode"/>.</summary>
    public void MoveNode(string nodeId, double x, double y)
    {
        var (result, snapshot) = RunOnWriterQueue(() =>
        {
            var actionResult = _writer.MoveNode(nodeId, x, y);
            var statusSnapshot = actionResult.Success ? new StatusSnapshot(BuildStatusText(), _writer.GetPreview()) : default;
            return (actionResult, statusSnapshot);
        });

        if (result.Success)
        {
            if (_isRunning)
            {
                CanvasStatusChanged?.Invoke(snapshot.StatusText);
            }

            FlowPreviewChanged?.Invoke(snapshot.Preview, false);
        }
    }

    /// <summary>Ablauf-Übersicht: creates a brand-new, isolated node at an explicit position (see IFlowWriter.AddManualNode — UML-style "+ Neuer Knoten hier" on the empty canvas). Not gated on <see cref="_isRunning"/> — see <see cref="RenameNode"/>.</summary>
    public void AddManualNode(string label, double x, double y, string? shape = null, string? color = null)
    {
        var (result, snapshot) = RunOnWriterQueue(() =>
        {
            var actionResult = _writer.AddManualNode(label, x, y, shape, color);
            var statusSnapshot = actionResult.Success ? new StatusSnapshot(BuildStatusText(), _writer.GetPreview()) : default;
            return (actionResult, statusSnapshot);
        });

        if (result.Success)
        {
            if (_isRunning)
            {
                CanvasStatusChanged?.Invoke(snapshot.StatusText);
            }

            FlowPreviewChanged?.Invoke(snapshot.Preview, false);
        }
    }

    /// <summary>For the Ablauf-Übersicht's resume-while-stopped flow: nodes already in <paramref name="fileName"/>.</summary>
    public List<ResumableNode> ListResumableCanvasNodes(string fileName) =>
        _writer.ListNodesForResume(fileName);

    /// <summary>Queues a chosen node as the starting point of the next Start() call.</summary>
    public void SetResumeAnchor(ResumableNode node) => _writer.SetResumeAnchor(node);

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
        // that block the message queue for too long. Queued (not
        // Task.Run): the hook thread already sees clicks in true
        // chronological order, and _writerQueue.Add preserves that exact
        // order all the way through to ProcessClick — an unordered thread-
        // pool Task.Run per click could let two rapid clicks' writes
        // interleave or even complete out of order.
        var point = e.Point;
        var timestamp = e.Timestamp;
        var targetFileName = _currentTargetFileName;

        LogService.Log($"{(isRightClick ? "Rechtsklick" : "Klick")} erkannt bei ({point.X}, {point.Y}).");
        _writerQueue.Add(() => ProcessClick(point, timestamp, targetFileName, isRightClick));
    }

    private void OnEnterPressed(object? sender, EnterKeyEventArgs e)
    {
        if (IsOwnUiFocused?.Invoke() == true)
        {
            // Never counts as a "skipped" capture either (no sound, no
            // balloon) — same reasoning as HandleMouseButtonDown's own
            // IsPointOnOwnUi check: this isn't a deliberate skip, it's not
            // a content interaction at all.
            LogService.Log("Enter-Erfassung ignoriert (DocuClick-eigenes Fenster hat den Fokus).");
            return;
        }

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
        _writerQueue.Add(() => ProcessEnterPress(timestamp, targetFileName));
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
        if (SkipForPasswordField(element, point)) return;

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
        if (SkipForPasswordField(element, null)) return;

        var fallbackWindowTitle = element?.WindowTitle ?? ForegroundWindowService.GetTitle();
        var description = DescriptionGenerator.Describe(element, fallbackWindowTitle, timestamp, InputAction.EnterKey);

        // No click point exists for a key press; the highlight (if any)
        // comes purely from the focused element's bounding rect.
        FinalizeCapture(description, timestamp, ScreenshotService.CaptureForegroundWindow, element, null, targetFileName);
    }

    /// <summary>
    /// The one kind of sensitive content this app can actually detect on
    /// its own (see ElementInfo.IsPassword) — skips the capture entirely,
    /// the same as the manual SkipRecordingModifier, instead of
    /// screenshotting/describing whatever's on screen at a password field.
    /// </summary>
    private bool SkipForPasswordField(ElementInfo? element, Point? point)
    {
        if (element?.IsPassword != true)
        {
            return false;
        }

        LogService.Log(point is { } p
            ? $"Klick bei ({p.X}, {p.Y}) übersprungen (Passwortfeld erkannt)."
            : "Enter-Erfassung übersprungen (Passwortfeld erkannt).");
        if (_config.EnableClickSound)
        {
            ClickFeedbackService.PlaySkipped();
        }

        return true;
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

            _writer.AddClickNode(description, screenshot, timestamp);
            FlowPreviewChanged?.Invoke(_writer.GetPreview(), true);

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
            // One failed capture (missing output path, UIA hiccup, locked
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
        _writerQueue.CompleteAdding();
        _writerThread.Join(TimeSpan.FromSeconds(2));
        _writerQueue.Dispose();
    }
}
