using System.Collections.Concurrent;
using DocuClick.Platform;
using SkiaSharp;

namespace DocuClick.Services;

/// <summary>
/// Glues the mouse/keyboard hooks to the capture pipeline (UI Automation
/// lookup -> screenshot -> highlight -> write) and owns the current
/// session's target file. Writes a branching flow via
/// <see cref="CanvasFlowWriter"/> — a separate, non-branching plain-note
/// writer existed once and was removed once the HTML flow format no longer
/// needed Obsidian. A draw.io export is always available afterward instead
/// of recording into it directly — see <see cref="DrawIoConverter"/>.
/// </summary>
public sealed class SessionManager : IDisposable
{
    private readonly PlatformServices _platform;
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
    public Func<ScreenPoint, bool>? IsPointOnOwnUi { get; set; }

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
    /// Absolute path to the target file of the current (or, once stopped,
    /// the most recent) session — null before any session has ever run,
    /// and while a session is loaded (running or paused).
    /// </summary>
    public string? CurrentTargetFileName => string.IsNullOrEmpty(_currentTargetFileName) ? null : _currentTargetFileName;

    /// <summary>The folder the current session's target file (and its Attachments subfolder) actually lives in — for the TopBar's "Ordner öffnen" and the draw.io export's InitialDirectory. Null under the same conditions as <see cref="CurrentTargetFileName"/>.</summary>
    public string? CurrentSessionFolder => CurrentTargetFileName is { } path ? Path.GetDirectoryName(path) : null;

    /// <summary>Whether a session target file is loaded and active (running or paused).</summary>
    public bool HasActiveSession => !string.IsNullOrEmpty(_currentTargetFileName);

    /// <summary>Whether a session is currently loaded but recording is paused.</summary>
    public bool IsPaused => HasActiveSession && !_isRunning;

    /// <summary>Retrieves the current flow preview snapshot from the writer thread.</summary>
    public FlowPreview? GetPreview() => RunOnWriterQueue(() => _writer.GetPreview());

    /// <summary>The loaded flow for an app-hosted editor page (see <see cref="EditorPageHost"/>); null without a loaded file.</summary>
    public EditorDocument? GetEditorDocument(Func<string, string?> image) =>
        HasActiveSession ? RunOnWriterQueue(() => _writer.BuildEditorDocument(image)) : null;

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

    public SessionManager(AppConfig config, PlatformServices platform)
    {
        _config = config;
        _platform = platform;
        _writer = new CanvasFlowWriter(config);
        // The writer's own debounced background save (drag-move, add-node)
        // needs to run its actual disk write on this same serial thread,
        // not a bare ThreadPool continuation — otherwise it can read/mutate
        // _doc concurrently with a click or edit action already running
        // here, corrupting state. _writerQueue.Add is thread-safe to call
        // from any thread, so a fire-and-forget hand-off is all this needs.
        _writer.RunOnWriterThread = action => _writerQueue.Add(action);
        _platform.Input.LeftButtonDown += OnLeftButtonDown;
        _platform.Input.RightButtonDown += OnRightButtonDown;
        _platform.Input.EnterPressed += OnEnterPressed;

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
                // A .docuclick diagram may have been edited in Obsidian
                // meanwhile: act on its current content, not a stale copy.
                _writer.SyncWithDisk();
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
    /// Starts a session against an explicit target file — an absolute path,
    /// freely chosen per session (see SessionStartWindow) rather than
    /// relative to any configured "output root". The target folder is
    /// created if it doesn't exist yet, so a brand-new folder picked in the
    /// session-start dialog works immediately.
    /// </summary>
    public void Start(string targetFilePath)
    {
        if (HasActiveSession && string.Equals(_currentTargetFileName, targetFilePath, StringComparison.OrdinalIgnoreCase) && !_isRunning)
        {
            // Resume paused session without re-opening from scratch
            StartCapturing();

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

        var targetDirectory = Path.GetDirectoryName(targetFilePath);
        if (!string.IsNullOrEmpty(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        // Runs on the writer thread (see RunOnWriterQueue) so it can never
        // race an already-queued click from a previous session that hasn't
        // finished processing yet. _currentTargetFileName is only committed
        // once this actually succeeds — a corrupt-file exception must leave
        // HasActiveSession/CurrentTargetFileName reporting whatever was
        // truly active before, not a file that never actually loaded.
        var snapshot = RunOnWriterQueue(() =>
        {
            _writer.StartSession(targetFilePath);
            return new StatusSnapshot(BuildStatusText(), _writer.GetPreview());
        });
        _currentTargetFileName = targetFilePath;

        // Recording into a vault: the plugin that opens the diagram note comes along.
        if (ObsidianVault.FindRoot(targetDirectory) is { } vaultRoot)
        {
            if (_config.AutoInstallObsidianPlugin && ObsidianPluginInstaller.EnsureInstalled(vaultRoot) is { } installMessage)
            {
                InfoOccurred?.Invoke(installMessage);
            }

            // Lets the plugin in this vault start/pause recording and set decision points.
            if (_config.RemoteControlToken is { Length: > 0 } token)
            {
                ObsidianAppLink.Write(vaultRoot, token);
            }
        }

        StartCapturing();

        _isRunning = true;
        CanvasStatusChanged?.Invoke(snapshot.StatusText);
        FlowPreviewChanged?.Invoke(snapshot.Preview, false);
        LogService.Log($"Session gestartet. Ziel: {_currentTargetFileName}");
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
    public void OpenForEditing(string targetFilePath)
    {
        if (_isRunning)
        {
            InfoOccurred?.Invoke("Aktion ignoriert: bitte zuerst die laufende Aufnahme stoppen.");
            return;
        }

        // Only committed once the writer queue action below actually
        // succeeds — a corrupt-file exception must leave whatever session
        // was previously active as the still-current one, not switch
        // CurrentTargetFileName over to a file that never actually loaded.
        var preview = RunOnWriterQueue(() =>
        {
            _writer.StartSession(targetFilePath);
            return _writer.GetPreview();
        });
        _currentTargetFileName = targetFilePath;

        FlowPreviewChanged?.Invoke(preview, false);
        LogService.Log($"Ablauf zum Bearbeiten geöffnet: {targetFilePath}");
    }

    /// <summary>
    /// Pauses recording: stops capture hooks and flushes changes to disk,
    /// while keeping the active target file, loaded document, and current cursor
    /// intact so editing in the Ablauf-Übersicht can proceed seamlessly.
    /// </summary>
    public void Pause()
    {
        StopCapturing();
        _isRunning = false;
        RunOnWriterQueue(() => _writer.Pause());
        CanvasStatusChanged?.Invoke("Aufnahme pausiert");
        var preview = RunOnWriterQueue(() => _writer.GetPreview());
        FlowPreviewChanged?.Invoke(preview, false);
        LogService.Log("Session pausiert.");
    }

    public void Stop()
    {
        StopCapturing();
        RunOnWriterQueue(() => _writer.Stop());
        _isRunning = false;
        _currentTargetFileName = string.Empty;
        CanvasStatusChanged?.Invoke(null);
        LogService.Log("Session gestoppt.");
    }

    /// <summary>
    /// Saves an Ablauf edited in a browser (via <see cref="LocalSaveService"/>).
    /// If it's the file currently loaded here, the loaded session takes the
    /// edit over (otherwise its next save would silently undo it); any other
    /// file is rewritten through a throwaway writer — the same HTML either way.
    /// </summary>
    public Task ApplyExternalEdit(string filePath, CanvasDocument document)
    {
        var preview = RunOnWriterQueue(() =>
        {
            if (HasActiveSession && string.Equals(Path.GetFullPath(_currentTargetFileName), Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase))
            {
                _writer.ReplaceDocument(document);
                return _writer.GetPreview();
            }

            var writer = new CanvasFlowWriter(_config);
            writer.StartSession(filePath);
            writer.ReplaceDocument(document);
            writer.Stop();
            return null;
        });

        if (preview is not null)
        {
            FlowPreviewChanged?.Invoke(preview, false);
        }

        return Task.CompletedTask;
    }

    /// <summary>Arms the platform's input monitoring (and, on macOS, the pre-click capture stream).</summary>
    private void StartCapturing()
    {
        _platform.Capture.BeginSession();
        _platform.Input.Start(_config.CaptureOnEnter);
    }

    private void StopCapturing()
    {
        _platform.Input.Stop();
        _platform.Capture.EndSession();
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
    /// <see cref="CanvasFlowWriter.DeleteNode"/>). The overlay is responsible
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

    /// <summary>Ablauf-Übersicht: updates an edge's color and line style.</summary>
    public void SetEdgeStyle(string fromNodeId, string toNodeId, string? color, string? lineStyle)
    {
        var (result, snapshot) = RunOnWriterQueue(() =>
        {
            var actionResult = _writer.SetEdgeStyle(fromNodeId, toNodeId, color, lineStyle);
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
            InfoOccurred?.Invoke("Verbindungsstil konnte nicht geändert werden.");
        }
    }

    /// <summary>Ablauf-Übersicht: reverses an edge's direction.</summary>
    public void ReverseEdge(string fromNodeId, string toNodeId)
    {
        var (result, snapshot) = RunOnWriterQueue(() =>
        {
            var actionResult = _writer.ReverseEdge(fromNodeId, toNodeId);
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
            InfoOccurred?.Invoke("Richtung konnte nicht umgekehrt werden.");
        }
    }

    /// <summary>Ablauf-Übersicht: drag-to-move a card to an explicit position (see <see cref="CanvasFlowWriter.MoveNode"/> — no auto-layout re-run, so it isn't undone by the very drag that just set it). Not gated on <see cref="_isRunning"/> — see <see cref="RenameNode"/>.</summary>
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

    /// <summary>Batch move for multiple nodes dragged simultaneously.</summary>
    public void MoveNodes(IReadOnlyList<(string NodeId, double X, double Y)> moves)
    {
        if (moves.Count == 0) return;

        var (result, snapshot) = RunOnWriterQueue(() =>
        {
            var actionResult = _writer.MoveNodes(moves);
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

    /// <summary>Ablauf-Übersicht: creates a brand-new, isolated node at an explicit position (see <see cref="CanvasFlowWriter.AddManualNode"/> — UML-style "+ Neuer Knoten hier" on the empty canvas). Not gated on <see cref="_isRunning"/> — see <see cref="RenameNode"/>.</summary>
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

    /// <summary>Ablauf-Übersicht: creates a brand-new node with an attached image at an explicit position.</summary>
    public void AddManualImageNode(string label, string imageSourcePath, double x, double y)
    {
        var (result, snapshot) = RunOnWriterQueue(() =>
        {
            var actionResult = _writer.AddManualImageNode(label, imageSourcePath, x, y);
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
            LogService.Log($"Klick bei {e.Point} ignoriert (DocuClick-eigene UI).");
            return;
        }

        if (IsSkipModifierDown(e.ShiftDown, e.ControlDown, e.AltDown, e.CommandDown))
        {
            LogService.Log($"Klick bei {e.Point} übersprungen ({_config.SkipRecordingModifier}-Taste gedrückt).");
            if (_config.EnableClickSound)
            {
                _platform.Sounds.PlaySkipped();
            }
            return;
        }

        // Grabbed right here, still inside the input callback — any later
        // and the clicked app may already have reacted to the click
        // (macOS pre-click stream; null elsewhere).
        var preClick = GrabPreClickFrame();

        // Copy everything the input thread needs to hand off, then return
        // immediately — input callbacks that block for too long get
        // unhooked/disabled by the OS. Queued (not Task.Run): the input
        // thread already sees clicks in true chronological order, and
        // _writerQueue.Add preserves that exact order all the way through
        // to ProcessClick — an unordered thread-pool Task.Run per click
        // could let two rapid clicks' writes interleave or even complete
        // out of order.
        var point = e.Point;
        var timestamp = e.Timestamp;
        var targetFileName = _currentTargetFileName;

        LogService.Log($"{(isRightClick ? "Rechtsklick" : "Klick")} erkannt bei {point}.");
        _writerQueue.Add(() => ProcessClick(point, timestamp, targetFileName, isRightClick, preClick));
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

        if (IsSkipModifierDown(e.ShiftDown, e.ControlDown, e.AltDown, e.CommandDown))
        {
            LogService.Log($"Enter-Erfassung übersprungen ({_config.SkipRecordingModifier}-Taste gedrückt).");
            if (_config.EnableClickSound)
            {
                _platform.Sounds.PlaySkipped();
            }
            return;
        }

        var preClick = GrabPreClickFrame();
        var timestamp = e.Timestamp;
        var targetFileName = _currentTargetFileName;

        LogService.Log("Enter-Taste erkannt.");
        _writerQueue.Add(() => ProcessEnterPress(timestamp, targetFileName, preClick));
    }

    private PreClickFrame? GrabPreClickFrame()
    {
        if (_config.CaptureTiming != "BeforeClick")
        {
            return null;
        }

        try
        {
            return _platform.Capture.GrabPreClickFrame();
        }
        catch (Exception ex)
        {
            LogService.Log($"Bild vor dem Klick nicht verfügbar: {ex.Message}");
            return null;
        }
    }

    private bool IsSkipModifierDown(bool shiftDown, bool controlDown, bool altDown, bool commandDown) => _config.SkipRecordingModifier switch
    {
        "Shift" => shiftDown,
        "Control" => controlDown,
        "Alt" => altDown,
        "Command" => commandDown,
        _ => false
    };

    /// <summary>
    /// True right now if the configured skip-recording modifier is held
    /// down — a cheap poll (not a hook) for the TopBar's live "wird gerade
    /// übersprungen" indicator, so holding the key gives immediate feedback
    /// instead of only the after-the-fact skip sound/log line. Purely
    /// informational: calling this never itself skips or logs anything,
    /// unlike the same check inside <see cref="HandleMouseButtonDown"/>/
    /// <see cref="OnEnterPressed"/>. Always false when no skip modifier is
    /// configured ("None").
    /// </summary>
    public bool IsSkipModifierHeldNow()
    {
        var modifiers = _platform.Input.CurrentModifiers;
        return IsSkipModifierDown(modifiers.Shift, modifiers.Control, modifiers.Alt, modifiers.Command);
    }

    private void ProcessClick(ScreenPoint point, DateTime timestamp, string targetFileName, bool isRightClick, PreClickFrame? preClick)
    {
        // Released right after the capture (not only after the whole click is
        // written), so a full-screen pre-click frame never outlives its use.
        var frame = preClick;
        try
        {
            // Accessibility first: a password field must be skipped before
            // anything is captured at all.
            var element = _config.UseUiAutomation ? _platform.Elements.GetElementAt(point) : null;
            if (SkipForPasswordField(element, point)) return;

            var fallbackWindowTitle = element?.WindowTitle ?? _platform.ForegroundWindow.GetTitle();
            var action = isRightClick ? InputAction.RightClick : InputAction.Click;
            var description = DescriptionGenerator.Describe(element, fallbackWindowTitle, timestamp, action);

            Func<CapturedFrame> capture = _zoomToCursorActive
                ? () => _platform.Capture.CaptureAroundPoint(point, _config.ZoomToCursorRadius, frame)
                : () => _platform.Capture.CaptureWindowAt(point, frame);

            FinalizeCapture(description, timestamp, () => CaptureThenRelease(capture, ref frame), element, point, targetFileName);
        }
        finally
        {
            frame?.Dispose();
        }
    }

    private void ProcessEnterPress(DateTime timestamp, string targetFileName, PreClickFrame? preClick)
    {
        var frame = preClick;
        try
        {
            var element = _config.UseUiAutomation ? _platform.Elements.GetFocusedElement() : null;
            if (SkipForPasswordField(element, null)) return;

            var fallbackWindowTitle = element?.WindowTitle ?? _platform.ForegroundWindow.GetTitle();
            var description = DescriptionGenerator.Describe(element, fallbackWindowTitle, timestamp, InputAction.EnterKey);

            // No click point exists for a key press; the highlight (if any)
            // comes purely from the focused element's bounding rect.
            FinalizeCapture(description, timestamp, () => CaptureThenRelease(() => _platform.Capture.CaptureForegroundWindow(frame), ref frame), element, null, targetFileName);
        }
        finally
        {
            frame?.Dispose();
        }
    }

    /// <summary>
    /// The one kind of sensitive content this app can actually detect on
    /// its own (see ElementInfo.IsPassword) — skips the capture entirely,
    /// the same as the manual SkipRecordingModifier, instead of
    /// screenshotting/describing whatever's on screen at a password field.
    /// </summary>
    private bool SkipForPasswordField(ElementInfo? element, ScreenPoint? point)
    {
        if (element?.IsPassword != true)
        {
            return false;
        }

        LogService.Log(point is { } p
            ? $"Klick bei {p} übersprungen (Passwortfeld erkannt)."
            : "Enter-Erfassung übersprungen (Passwortfeld erkannt).");
        if (_config.EnableClickSound)
        {
            _platform.Sounds.PlaySkipped();
        }

        return true;
    }

    private static CapturedFrame CaptureThenRelease(Func<CapturedFrame> capture, ref PreClickFrame? frame)
    {
        try
        {
            return capture();
        }
        finally
        {
            frame?.Dispose();
            frame = null;
        }
    }

    private void FinalizeCapture(
        string description,
        DateTime timestamp,
        Func<CapturedFrame> capture,
        ElementInfo? element,
        ScreenPoint? clickPoint,
        string targetFileName)
    {
        try
        {
            var captured = capture();
            using var bitmap = captured.Bitmap;
            DrawHighlight(bitmap, captured, element, clickPoint);
            var screenshot = Encode(bitmap, captured.Scale);

            _writer.AddClickNode(description, screenshot, timestamp);
            FlowPreviewChanged?.Invoke(_writer.GetPreview(), true);

            LogService.Log($"Eintrag geschrieben: \"{description}\" -> {targetFileName}");
            LastScreenshotCaptured?.Invoke(screenshot.Data);

            if (_config.EnableClickSound)
            {
                _platform.Sounds.PlayCaptured();
            }
        }
        catch (Exception ex)
        {
            // One failed capture (missing output path, accessibility hiccup,
            // locked file, ...) must not tear down the running session.
            LogService.Log($"Erfassung fehlgeschlagen: {ex}");
            if (_config.EnableClickSound)
            {
                _platform.Sounds.PlayError();
            }
            ErrorOccurred?.Invoke(ex.Message);
        }
    }

    /// <summary>
    /// Marks the click on the captured bitmap. Screen units are converted to
    /// bitmap pixels via the capture's scale (1 on Windows, 2 on Retina).
    /// </summary>
    private void DrawHighlight(SKBitmap bitmap, CapturedFrame captured, ElementInfo? element, ScreenPoint? clickPoint)
    {
        var color = SKColor.TryParse(_config.HighlightColorHex, out var parsed) ? parsed : new SKColor(0xE6, 0x39, 0x46);
        var scale = (float)captured.Scale;
        var bounds = captured.Bounds;

        // An accessibility element occasionally reports its parent
        // pane/window as its own bounding rect (poor automation trees,
        // clicks on window chrome, ...). Drawing that as a "highlight"
        // would paint most of the screenshot red, so only trust boxes that
        // are clearly smaller than the captured area itself; anything
        // bigger falls back to a plain click-circle (or no mark at all for
        // the Enter trigger, which has no click point).
        if (element?.BoundingRectangle is { Width: > 0, Height: > 0 } rect
            && rect.Width <= bounds.Width * 0.9
            && rect.Height <= bounds.Height * 0.9)
        {
            var local = SKRect.Create(
                (float)(rect.X - bounds.X) * scale,
                (float)(rect.Y - bounds.Y) * scale,
                (float)rect.Width * scale,
                (float)rect.Height * scale);
            HighlightRenderer.DrawBoundingBox(bitmap, local, color, _config.HighlightThickness * scale);
        }
        else if (clickPoint is { } point)
        {
            var local = new SKPoint((float)(point.X - bounds.X) * scale, (float)(point.Y - bounds.Y) * scale);
            HighlightRenderer.DrawClickCircle(bitmap, local, color, _config.HighlightRadius * scale, _config.HighlightThickness * scale);
        }
    }

    /// <summary>Encodes the highlighted capture in the configured format (WebP, JPEG or PNG), first scaling HiDPI captures down to 1 pixel per point if configured.</summary>
    private ScreenshotImage Encode(SKBitmap bitmap, double scale)
    {
        var source = bitmap;
        SKBitmap? resized = null;
        if (scale > 1.0 && _config.DownscaleHiDpiScreenshots)
        {
            var info = new SKImageInfo(
                Math.Max(1, (int)Math.Round(bitmap.Width / scale)),
                Math.Max(1, (int)Math.Round(bitmap.Height / scale)));
            resized = bitmap.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell));
            if (resized is not null)
            {
                source = resized;
                scale = 1.0;
            }
        }

        try
        {
            using var image = SKImage.FromBitmap(source);
            var (format, extension) = _config.ScreenshotFormat?.ToLowerInvariant() switch
            {
                "png" => (SKEncodedImageFormat.Png, "png"),
                "jpeg" or "jpg" => (SKEncodedImageFormat.Jpeg, "jpg"),
                _ => (SKEncodedImageFormat.Webp, "webp"),
            };
            using var data = image.Encode(format, Math.Clamp(_config.ScreenshotQuality, 50, 100));
            return new ScreenshotImage(data.ToArray(), source.Width, source.Height, scale, extension);
        }
        finally
        {
            resized?.Dispose();
        }
    }

    public void Dispose()
    {
        _platform.Input.Dispose();
        // On the writer thread like every other _doc access — otherwise
        // this could race whatever the writer thread is still finishing up
        // from a click queued just before app exit. Committing this before
        // CompleteAdding() means a drag/add-node from the last moment
        // before quitting doesn't get silently dropped by the 150ms
        // debounce never getting the chance to fire.
        RunOnWriterQueue(() => _writer.FlushPendingSave());
        _writerQueue.CompleteAdding();
        _writerThread.Join(TimeSpan.FromSeconds(2));
        _writerQueue.Dispose();
    }
}
