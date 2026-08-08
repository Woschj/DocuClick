using System.Threading;
using System.Windows;
using System.Windows.Input;
using DocuClick.Services;

namespace DocuClick;

public partial class App : Application
{
    // Fixed GUID (not e.g. the assembly name) so it can never collide with
    // another vendor's mutex, and survives a rename of the exe/assembly.
    private const string SingleInstanceMutexName = "DocuClick-9F2B6E7C-9B0B-4C3E-8B7B-6B1E2C6C7B3A";

    private Mutex? _singleInstanceMutex;
    private TrayApp? _trayApp;
    private SessionManager? _sessionManager;
    private AppConfig? _config;
    private HotkeyService? _hotkeyService;
    private FlowPreviewOverlay? _flowPreviewOverlay;
    private TopBarWindow? _topBar;
    private ZoomCursorBoxOverlay? _zoomCursorBox;

    /// <summary>Set when the user closes the Ablauf-Übersicht via its own header ✕ — stops <see cref="OnFlowPreviewChanged"/> from popping it back open on the very next click, until the TopBar's "Übersicht" button explicitly asks for it again.</summary>
    private bool _flowPreviewManuallyHidden;

    /// <summary>Set by clicking a node in the Ablauf-Übersicht while stopped (see <see cref="OnFlowPreviewNodeClicked"/>), consumed once by the next session-start file picker.</summary>
    private string? _pendingResumeFileName;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "DocuClick läuft bereits (siehe Symbol im Infobereich der Taskleiste).",
                "DocuClick", MessageBoxButton.OK, MessageBoxImage.Information);
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        // The app has no window; it must stay alive until the user picks
        // "Beenden" from the tray menu.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _config = ConfigService.Load();
        _sessionManager = new SessionManager(_config);
        _sessionManager.ErrorOccurred += OnSessionError;
        _sessionManager.InfoOccurred += OnSessionInfo;
        _sessionManager.FlowPreviewChanged += OnFlowPreviewChanged;

        _trayApp = new TrayApp();
        _trayApp.RecordingStateChanged += OnRecordingStateChanged;
        _trayApp.SettingsRequested += OnSettingsRequested;

        // Visible for the app's whole lifetime (not just while recording),
        // so there is always an at-a-glance answer to "is it running".
        _topBar = new TopBarWindow(_config.ZoomToCursorRadius);
        _topBar.ToggleRecordingRequested += () => _trayApp?.ToggleRecording();
        _topBar.ShowFlowPreviewRequested += OnShowFlowPreviewRequested;
        _topBar.NewSessionRequested += OnNewSessionRequested;
        _topBar.ZoomToCursorToggleRequested += () => _sessionManager?.ToggleZoomToCursor();
        _topBar.ZoomRadiusChanged += radius =>
        {
            _config.ZoomToCursorRadius = radius;
            _zoomCursorBox ??= new ZoomCursorBoxOverlay(radius);
            _zoomCursorBox.Preview(radius);
        };
        _topBar.ZoomRadiusCommitted += () => ConfigService.Save(_config);
        _topBar.Show();

        _sessionManager.ZoomToCursorChanged += active => Dispatcher.BeginInvoke(() =>
        {
            _topBar?.UpdateZoomToCursorState(active);
            if (!active)
            {
                _zoomCursorBox?.Cancel();
            }
        });

        // Clicks on any of DocuClick's own interactive windows (top bar,
        // branch dialogs, session-start picker, settings, ...) or the tray
        // icon must never be recorded as content — the global mouse hook
        // otherwise can't tell them apart from a real click (see
        // SessionManager.IsPointOnOwnUi). Checked generically against
        // Application.Windows so this covers every current and future
        // dialog automatically, not just whichever ones are special-cased.
        _sessionManager.IsPointOnOwnUi = point =>
        {
            var drawingPoint = new System.Drawing.Point(point.X, point.Y);

            foreach (Window window in Windows)
            {
                // The zoom-cursor preview box is deliberately click-through
                // (WS_EX_TRANSPARENT) — a click "on" it actually lands on
                // whatever's underneath and must still be recorded normally,
                // so it's excluded from this check. It's also centered
                // exactly on the cursor by design, so without this every
                // single click while it's showing would otherwise be
                // swallowed as "clicked DocuClick's own UI" — no screenshot
                // ever taken (confirmed in testing).
                if (window is ZoomCursorBoxOverlay || !window.IsVisible)
                {
                    continue;
                }

                var bounds = new System.Drawing.Rectangle((int)window.Left, (int)window.Top, (int)window.ActualWidth, (int)window.ActualHeight);
                if (bounds.Contains(drawingPoint))
                {
                    return true;
                }
            }

            var trayBounds = _trayApp.GetIconScreenBounds();
            return trayBounds is { } trayRect && trayRect.Contains(drawingPoint);
        };

        SetUpHotkeys();

        LogService.Log("DocuClick gestartet.");
    }

    private void SetUpHotkeys()
    {
        _hotkeyService?.Dispose();
        _hotkeyService = new HotkeyService();
        _hotkeyService.Initialize();

        RegisterHotkey(_config!.BranchMarkModifiers, _config.BranchMarkKey, "Abzweigung setzen",
            OnDecisionPointRequested);
        RegisterHotkey(_config.StartStopModifiers, _config.StartStopKey, "Aufnahme starten/stoppen",
            () => _trayApp?.ToggleRecording());
        RegisterHotkey(_config.ZoomToCursorModifiers, _config.ZoomToCursorKey, "Zoom-auf-Cursor umschalten",
            () => _sessionManager?.ToggleZoomToCursor());
    }

    private void RegisterHotkey(string modifiersSpec, string keySpec, string label, Action action)
    {
        try
        {
            var modifiers = HotkeyService.ParseModifiers(modifiersSpec);
            if (!Enum.TryParse<Key>(keySpec, ignoreCase: true, out var key))
            {
                LogService.Log($"Hotkey '{label}' übersprungen: ungültige Taste '{keySpec}'.");
                return;
            }

            _hotkeyService!.Register(modifiers, key, action);
            LogService.Log($"Hotkey registriert: {modifiersSpec}+{keySpec} -> {label}");
        }
        catch (Exception ex)
        {
            LogService.Log($"Hotkey '{label}' ({modifiersSpec}+{keySpec}) konnte nicht registriert werden: {ex.Message}");
        }
    }

    private void OnSessionError(string message)
    {
        // These events fire from a background Task, but NotifyIcon must
        // be touched from the UI thread it was created on.
        Dispatcher.BeginInvoke(() => _trayApp?.ShowError(message));
    }

    private void OnSessionInfo(string message)
    {
        Dispatcher.BeginInvoke(() => _trayApp?.ShowInfo(message));
    }

    /// <summary>
    /// TopBar button/hotkey: marks the current node as a decision point and
    /// immediately forks its first path — prompts for that path's name
    /// first (same dialog "+ Neuer Pfad" uses), since a decision point
    /// with no named path yet would have nothing selectable in the
    /// Ablauf-Übersicht popup. Cancelling the prompt does nothing at all —
    /// still routes the "not running"/"mode doesn't support this" case
    /// through SessionManager's own info balloon rather than asking for a
    /// name first when it can't apply anyway.
    /// </summary>
    private void OnDecisionPointRequested()
    {
        if (_sessionManager is null)
        {
            return;
        }

        if (!_sessionManager.IsRunning || !_sessionManager.SupportsBranching)
        {
            _sessionManager.MarkDecisionPoint(string.Empty);
            return;
        }

        var nameWindow = new BranchNameWindow
        {
            Owner = _flowPreviewOverlay is { IsVisible: true } ? _flowPreviewOverlay : null
        };
        NativeMethods.ModalDialogDepth++;
        try
        {
            if (nameWindow.ShowDialog() == true && nameWindow.BranchName is { } name)
            {
                _sessionManager.MarkDecisionPoint(name);
            }
        }
        finally
        {
            NativeMethods.ModalDialogDepth--;
        }
    }

    /// <summary>Ablauf-Übersicht popup: "+ Neuer Pfad" was chosen and named.</summary>
    private void OnNewPathRequested(string decisionPointId, string pathName) => _sessionManager?.StartNewPath(decisionPointId, pathName);

    /// <summary>Ablauf-Übersicht popup: an existing path was chosen to continue.</summary>
    private void OnContinuePathRequested(string pathStartNodeId) => _sessionManager?.ContinuePath(pathStartNodeId);

    private void OnFlowPreviewChanged(FlowPreview? preview)
    {
        // Fires from SessionManager's dedicated writer thread for every
        // click/branch action — the overlay is WPF UI and must only be
        // touched from its own thread.
        Dispatcher.BeginInvoke(() =>
        {
            if (preview is null)
            {
                _flowPreviewOverlay?.Hide();
                return;
            }

            if (_flowPreviewOverlay is null)
            {
                _flowPreviewOverlay = new FlowPreviewOverlay();
                _flowPreviewOverlay.NodeClicked += OnFlowPreviewNodeClicked;
                _flowPreviewOverlay.PathsProvider = decisionPointId => _sessionManager?.ListPaths(decisionPointId) ?? new List<PathInfo>();
                _flowPreviewOverlay.NewPathRequested += OnNewPathRequested;
                _flowPreviewOverlay.ContinuePathRequested += OnContinuePathRequested;
                _flowPreviewOverlay.RenameRequested += (nodeId, newLabel) => _sessionManager?.RenameNode(nodeId, newLabel);
                _flowPreviewOverlay.DeleteRequested += nodeId => _sessionManager?.DeleteNode(nodeId);
                _flowPreviewOverlay.ReparentRequested += (nodeId, newParentId) => _sessionManager?.ReparentNode(nodeId, newParentId);
                _flowPreviewOverlay.ConnectRequested += (fromId, toId) => _sessionManager?.ConnectNodes(fromId, toId);
                _flowPreviewOverlay.DisconnectRequested += (fromId, toId) => _sessionManager?.DisconnectNodes(fromId, toId);
                _flowPreviewOverlay.CloseRequested += () =>
                {
                    _flowPreviewManuallyHidden = true;
                    _flowPreviewOverlay?.Hide();
                };
            }

            _flowPreviewOverlay.UpdatePreview(preview);
            if (!_flowPreviewManuallyHidden)
            {
                _flowPreviewOverlay.Show();
            }
        });
    }

    /// <summary>TopBar "Übersicht" button: toggles the Ablauf-Übersicht — closes it if it's currently showing (same as its own header ✕), reopens it otherwise. A no-op info balloon (not an error) if nothing has ever been recorded yet — there's simply nothing to show.</summary>
    private void OnShowFlowPreviewRequested()
    {
        if (_flowPreviewOverlay is null)
        {
            _trayApp?.ShowInfo("Noch keine Ablauf-Übersicht vorhanden — sie erscheint mit dem ersten aufgezeichneten Klick.");
            return;
        }

        if (_flowPreviewOverlay.IsVisible)
        {
            _flowPreviewManuallyHidden = true;
            _flowPreviewOverlay.Hide();
        }
        else
        {
            _flowPreviewManuallyHidden = false;
            _flowPreviewOverlay.Show();
        }
    }

    /// <summary>
    /// While recording, clicking a node in the Ablauf-Übersicht jumps the
    /// live cursor there (as before). While stopped, the overlay still
    /// shows the last session's file — so a click there instead primes
    /// *that* node as the attach point for the next Start(), replacing the
    /// old separate "Ablauf fortsetzen ab Punkt..." tray menu item and its
    /// own file/node picker dialogs: the overlay already shows exactly the
    /// same nodes those would have asked to choose from.
    /// </summary>
    private void OnFlowPreviewNodeClicked(string nodeId)
    {
        if (_sessionManager!.IsRunning)
        {
            _sessionManager.JumpToNode(nodeId);
            return;
        }

        if (_sessionManager.CurrentTargetFileName is not { } fileName)
        {
            return;
        }

        var node = _sessionManager.ListResumableCanvasNodes(fileName).FirstOrDefault(n => n.Id == nodeId);
        if (node is null)
        {
            return;
        }

        _sessionManager.SetResumeAnchor(node);
        _pendingResumeFileName = fileName;
        _trayApp!.ShowInfo($"Nächste Aufnahme wird angehängt an: {node.Label} (in {fileName})");
    }

    /// <summary>Shows the session-start file picker; null means the user cancelled.</summary>
    private string? PromptForSessionFile()
    {
        var window = new SessionStartWindow(_config!, _pendingResumeFileName);
        _pendingResumeFileName = null;
        return window.ShowDialog() == true ? window.SelectedFileName : null;
    }

    /// <summary>
    /// The file "Start" should resume without prompting: the last file
    /// used, unless a resume-from-point is pending (that always needs the
    /// dialog to actually apply) or the output mode changed since, making
    /// the remembered file's extension stale.
    /// </summary>
    private string? ResolveResumeFileName()
    {
        if (_pendingResumeFileName is not null)
        {
            return null;
        }

        var last = _config!.LastSessionFileName;
        if (string.IsNullOrWhiteSpace(last))
        {
            return null;
        }

        var expectedExtension = SessionManager.ExtensionForOutputMode(_config.OutputMode);
        return last.EndsWith(expectedExtension, StringComparison.OrdinalIgnoreCase) ? last : null;
    }

    private void RememberLastSession(string fileName)
    {
        _config!.LastSessionFileName = fileName;
        ConfigService.Save(_config);
    }

    /// <summary>
    /// "Start" (tray menu, top-bar button, hotkey): resumes the last-used
    /// file directly, no dialog — "Neue Session" is the action that asks
    /// for a file name every time.
    /// </summary>
    private void OnRecordingStateChanged(bool isRecording)
    {
        if (isRecording)
        {
            var fileName = ResolveResumeFileName() ?? PromptForSessionFile();
            if (fileName is null)
            {
                _trayApp!.SetRecording(false);
                return;
            }

            try
            {
                _sessionManager!.Start(fileName);
                RememberLastSession(fileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Aufnahme konnte nicht gestartet werden:\n{ex.Message}",
                    "DocuClick", MessageBoxButton.OK, MessageBoxImage.Error);
                _trayApp!.SetRecording(false);
                return;
            }
        }
        else
        {
            _sessionManager!.Stop();
        }

        _topBar?.UpdateStatus(isRecording, detail: null, _sessionManager!.SupportsBranching);
    }

    /// <summary>"Neue Session" (top-bar button only): always prompts for a target file, whether currently recording or not.</summary>
    private void OnNewSessionRequested()
    {
        if (_sessionManager is null)
        {
            return;
        }

        var fileName = PromptForSessionFile();
        if (fileName is null)
        {
            return;
        }

        try
        {
            if (_sessionManager.IsRunning)
            {
                _sessionManager.StartNewSession(fileName);
            }
            else
            {
                _sessionManager.Start(fileName);
                // Sync tray icon/tooltip without re-firing RecordingStateChanged
                // (that would re-enter here via OnRecordingStateChanged's own
                // start logic — this session is already started).
                _trayApp!.SyncRecordingState(true);
            }

            RememberLastSession(fileName);
            _trayApp!.ShowInfo($"Neue Session gestartet: {fileName}");
            _topBar?.UpdateStatus(true, detail: null, _sessionManager.SupportsBranching);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Neue Session konnte nicht gestartet werden:\n{ex.Message}",
                "DocuClick", MessageBoxButton.OK, MessageBoxImage.Error);
            _trayApp!.SetRecording(false);
        }
    }

    private void OnSettingsRequested()
    {
        var window = new SettingsWindow(_config!);
        window.SettingsSaved += OnSettingsSaved;
        window.ShowDialog();
    }

    private void OnSettingsSaved()
    {
        SetUpHotkeys();

        // The top bar's branch buttons reflect SupportsBranching for the
        // *active* output mode — if the user switches modes in Settings
        // while a recording is already running (nothing prevents that),
        // nothing else would ever refresh them until the next Stop/Start.
        _topBar?.UpdateStatus(_trayApp!.IsRecording, detail: null, _sessionManager!.SupportsBranching);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeyService?.Dispose();
        _sessionManager?.Dispose();
        _trayApp?.Dispose();
        _topBar?.Close();
        _zoomCursorBox?.Close();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
