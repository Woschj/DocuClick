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

        _trayApp = new TrayApp();
        _trayApp.RecordingStateChanged += OnRecordingStateChanged;
        _trayApp.SettingsRequested += OnSettingsRequested;
        _trayApp.ResumeFromPointRequested += OnResumeFromPointRequested;

        // Visible for the app's whole lifetime (not just while recording),
        // so there is always an at-a-glance answer to "is it running".
        _topBar = new TopBarWindow();
        _topBar.ToggleRecordingRequested += () => _trayApp?.ToggleRecording();
        _topBar.MarkBranchRequested += () => _sessionManager?.MarkBranchAnchor();
        _topBar.JumpBranchRequested += () => _sessionManager?.JumpToLastAnchor();
        _topBar.NewSessionRequested += OnNewSessionRequested;
        _topBar.Show();

        SetUpHotkeys();

        LogService.Log("DocuClick gestartet.");
    }

    private void SetUpHotkeys()
    {
        _hotkeyService?.Dispose();
        _hotkeyService = new HotkeyService();
        _hotkeyService.Initialize();

        RegisterHotkey(_config!.BranchMarkModifiers, _config.BranchMarkKey, "Abzweigungspunkt setzen",
            () => _sessionManager?.MarkBranchAnchor());
        RegisterHotkey(_config.BranchJumpModifiers, _config.BranchJumpKey, "Zu letztem Abzweigungspunkt springen",
            () => _sessionManager?.JumpToLastAnchor());
        RegisterHotkey(_config.StartStopModifiers, _config.StartStopKey, "Aufnahme starten/stoppen",
            () => _trayApp?.ToggleRecording());
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
        Dispatcher.Invoke(() => _trayApp?.ShowError(message));
    }

    private void OnSessionInfo(string message)
    {
        Dispatcher.Invoke(() => _trayApp?.ShowInfo(message));
    }

    private void OnBranchDepthChanged(int depth)
    {
        Dispatcher.Invoke(() =>
        {
            _trayApp?.SetBranchDepth(depth);
            _topBar?.UpdateStatus(_trayApp!.IsRecording, depth > 0 ? $"Branch-Tiefe {depth}" : null, _sessionManager!.SupportsBranching);
        });
    }

    private void OnCanvasStatusChanged(string? statusText)
    {
        Dispatcher.Invoke(() =>
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

    private void OnResumeFromPointRequested()
    {
        if (_sessionManager!.IsRunning)
        {
            _trayApp!.ShowInfo("Bitte erst die Aufnahme stoppen, bevor ein Fortsetzungspunkt gewählt wird.");
            return;
        }

        if (!_sessionManager.SupportsBranching)
        {
            _trayApp!.ShowInfo("Nur im Canvas- oder Word-Modus verfügbar (siehe Einstellungen).");
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

    private void OnRecordingStateChanged(bool isRecording)
    {
        if (isRecording)
        {
            var fileName = PromptForSessionFile();
            if (fileName is null)
            {
                _trayApp!.SetRecording(false);
                return;
            }

            try
            {
                _sessionManager!.Start(fileName);
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

    private void OnNewSessionRequested()
    {
        if (_sessionManager is null)
        {
            return;
        }

        if (!_sessionManager.IsRunning)
        {
            // Not recording yet: behaves like a plain Start (single prompt
            // via OnRecordingStateChanged, no double-prompt risk).
            _trayApp!.SetRecording(true);
            return;
        }

        var fileName = PromptForSessionFile();
        if (fileName is null)
        {
            return;
        }

        try
        {
            _sessionManager.StartNewSession(fileName);
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
        window.SettingsSaved += SetUpHotkeys;
        window.ShowDialog();
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
