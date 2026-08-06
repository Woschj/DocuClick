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
    private RecordingIndicatorOverlay? _recordingOverlay;
    private CanvasStatusOverlay? _canvasStatusOverlay;
    private FlowPreviewOverlay? _flowPreviewOverlay;
    private TopBarWindow? _topBar;

    /// <summary>Set by "Ablauf fortsetzen ab Punkt...", consumed once by the next session-start file picker.</summary>
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
        _sessionManager.BranchDepthChanged += OnBranchDepthChanged;
        _sessionManager.CanvasStatusChanged += OnCanvasStatusChanged;
        _sessionManager.LastScreenshotCaptured += OnLastScreenshotCaptured;
        _sessionManager.FlowPreviewChanged += OnFlowPreviewChanged;

        _trayApp = new TrayApp();
        _trayApp.RecordingStateChanged += OnRecordingStateChanged;
        _trayApp.SettingsRequested += OnSettingsRequested;
        _trayApp.ResumeFromPointRequested += OnResumeFromPointRequested;

        // Visible for the app's whole lifetime (not just while recording),
        // so there is always an at-a-glance answer to "is it running".
        _topBar = new TopBarWindow();
        _topBar.ToggleRecordingRequested += () => _trayApp?.ToggleRecording();
        _topBar.MarkBranchRequested += OnMarkBranchRequested;
        _topBar.JumpBranchRequested += OnSelectBranchRequested;
        _topBar.NewSessionRequested += OnNewSessionRequested;
        _topBar.ZoomToCursorToggleRequested += () => _sessionManager?.ToggleZoomToCursor();
        _topBar.Show();

        _sessionManager.ZoomToCursorChanged += active => Dispatcher.BeginInvoke(() => _topBar?.UpdateZoomToCursorState(active));

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
                // The recording dot / canvas-status HUD are deliberately
                // click-through (WS_EX_TRANSPARENT) — a click "on" them
                // actually lands on whatever's underneath and must still
                // be recorded normally, so they're excluded from this check.
                if (window is RecordingIndicatorOverlay or CanvasStatusOverlay || !window.IsVisible)
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

        RegisterHotkey(_config!.BranchMarkModifiers, _config.BranchMarkKey, "Branch setzen",
            OnMarkBranchRequested);
        RegisterHotkey(_config.BranchJumpModifiers, _config.BranchJumpKey, "Branch auswählen",
            OnSelectBranchRequested);
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

    private void OnBranchDepthChanged(int depth)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _trayApp?.SetBranchDepth(depth);
            var currentBranch = _sessionManager!.CurrentBranchName;
            var detail = currentBranch is not null
                ? $"Branch: {currentBranch}"
                : depth > 0 ? $"{depth} Branch(es) gesetzt" : null;
            _topBar?.UpdateStatus(_trayApp!.IsRecording, detail, _sessionManager.SupportsBranching);
        });
    }

    /// <summary>Prompts for a branch name; null means the user cancelled.</summary>
    private string? PromptForBranchName()
    {
        var window = new BranchNameWindow();
        return window.ShowDialog() == true ? window.BranchName : null;
    }

    private void OnMarkBranchRequested()
    {
        if (_sessionManager is null)
        {
            return;
        }

        if (!_sessionManager.IsRunning || !_sessionManager.SupportsBranching)
        {
            // Still routes through SessionManager so the usual "not
            // running"/"mode doesn't support branching" info balloon fires
            // — no point showing a naming prompt first in that case.
            _sessionManager.MarkBranchAnchor(string.Empty);
            return;
        }

        var name = PromptForBranchName();
        if (name is not null)
        {
            _sessionManager.MarkBranchAnchor(name);
        }
    }

    private void OnSelectBranchRequested()
    {
        if (_sessionManager is null)
        {
            return;
        }

        if (!_sessionManager.IsRunning || !_sessionManager.SupportsBranching)
        {
            _sessionManager.JumpToAnchor(string.Empty);
            return;
        }

        var names = _sessionManager.ListBranchAnchorNames();
        if (names.Count == 0)
        {
            _trayApp!.ShowInfo("Noch keine Branches gesetzt (erst mit \"Branch setzen\" einen anlegen).");
            return;
        }

        var picker = new BranchPickerWindow(names);
        if (picker.ShowDialog() == true && picker.SelectedBranchName is not null)
        {
            _sessionManager.JumpToAnchor(picker.SelectedBranchName);
        }
    }

    private void OnCanvasStatusChanged(string? statusText)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (statusText is null)
            {
                _canvasStatusOverlay?.Hide();
                return;
            }

            _canvasStatusOverlay ??= new CanvasStatusOverlay();
            _canvasStatusOverlay.UpdateText(statusText);
            _canvasStatusOverlay.Show();
        });
    }

    private void OnLastScreenshotCaptured(byte[] pngBytes)
    {
        // This event fires from SessionManager's dedicated writer thread;
        // the overlay is WPF UI and must only be touched from its own
        // thread. Only relevant to modes that show the status overlay in
        // the first place (see OnCanvasStatusChanged) — for plain Note
        // mode the overlay is never shown, so updating a hidden thumbnail
        // would be pointless work on every single click.
        Dispatcher.BeginInvoke(() =>
        {
            if (_canvasStatusOverlay is { IsVisible: true })
            {
                _canvasStatusOverlay.UpdateThumbnail(pngBytes);
            }
        });
    }

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
                _flowPreviewOverlay.NodeClicked += nodeId => _sessionManager?.JumpToNode(nodeId);
            }

            _flowPreviewOverlay.UpdatePreview(preview);
            _flowPreviewOverlay.Show();
        });
    }

    private void OnResumeFromPointRequested()
    {
        if (_sessionManager!.IsRunning)
        {
            _trayApp!.ShowInfo("Bitte erst die Aufnahme stoppen, bevor ein Fortsetzungspunkt gewählt wird.");
            return;
        }

        if (!_sessionManager.SupportsBranching)
        {
            _trayApp!.ShowInfo("Nur im Canvas-, draw.io- oder Excalidraw-Modus verfügbar (siehe Einstellungen).");
            return;
        }

        var files = _sessionManager.ListExistingFiles();
        if (files.Count == 0)
        {
            _trayApp!.ShowInfo("Noch keine Datei im Zielordner vorhanden.");
            return;
        }

        // Newest file first (ListExistingFiles) — good enough default for
        // this secondary action without a second file-picker dialog just
        // for it; the session-start dialog is where file choice matters.
        var fileName = files[0];
        var nodes = _sessionManager.ListResumableCanvasNodes(fileName);
        if (nodes.Count == 0)
        {
            _trayApp!.ShowInfo($"Noch keine Knoten in \"{fileName}\" vorhanden.");
            return;
        }

        var picker = new ResumePickerWindow(nodes);
        if (picker.ShowDialog() == true && picker.SelectedNode is not null)
        {
            _sessionManager.SetResumeAnchor(picker.SelectedNode);
            _pendingResumeFileName = fileName;
            _trayApp!.ShowInfo($"Nächste Aufnahme wird angehängt an: {picker.SelectedNode.Label} (in {fileName})");
        }
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
                _recordingOverlay ??= new RecordingIndicatorOverlay();
                _recordingOverlay.Show();
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
            _recordingOverlay?.Hide();
            _canvasStatusOverlay?.Hide();
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
                _recordingOverlay ??= new RecordingIndicatorOverlay();
                _recordingOverlay.Show();
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
        _recordingOverlay?.Close();
        _canvasStatusOverlay?.Close();
        _topBar?.Close();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
