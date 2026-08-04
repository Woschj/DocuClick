using System.Windows;
using System.Windows.Input;
using DocuClick.Services;

namespace DocuClick;

public partial class App : Application
{
    private TrayApp? _trayApp;
    private SessionManager? _sessionManager;
    private AppConfig? _config;
    private HotkeyService? _hotkeyService;
    private RecordingIndicatorOverlay? _recordingOverlay;
    private CanvasStatusOverlay? _canvasStatusOverlay;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
        Dispatcher.Invoke(() => _trayApp?.SetBranchDepth(depth));
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
            _trayApp!.ShowInfo("Nur im Canvas- oder draw.io-Modus verfügbar (siehe Einstellungen).");
            return;
        }

        var nodes = _sessionManager.ListResumableCanvasNodes();
        if (nodes.Count == 0)
        {
            _trayApp!.ShowInfo("Noch keine Knoten in der aktuellen Canvas-Datei vorhanden.");
            return;
        }

        var picker = new ResumePickerWindow(nodes);
        if (picker.ShowDialog() == true && picker.SelectedNode is not null)
        {
            _sessionManager.SetResumeAnchor(picker.SelectedNode);
            _trayApp!.ShowInfo($"Nächste Aufnahme wird angehängt an: {picker.SelectedNode.Label}");
        }
    }

    private void OnRecordingStateChanged(bool isRecording)
    {
        if (isRecording)
        {
            try
            {
                _sessionManager!.Start();
                _recordingOverlay ??= new RecordingIndicatorOverlay();
                _recordingOverlay.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Aufnahme konnte nicht gestartet werden:\n{ex.Message}",
                    "DocuClick", MessageBoxButton.OK, MessageBoxImage.Error);
                _trayApp!.SetRecording(false);
            }
        }
        else
        {
            _sessionManager!.Stop();
            _recordingOverlay?.Hide();
            _canvasStatusOverlay?.Hide();
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
        base.OnExit(e);
    }
}
