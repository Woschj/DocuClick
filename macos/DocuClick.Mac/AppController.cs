using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DocuClick.Mac.Platform;
using DocuClick.Mac.UI;
using DocuClick.Services;
using SkiaSharp;

namespace DocuClick.Mac;

/// <summary>
/// The macOS app's wiring — the counterpart of the Windows App.xaml.cs
/// (v1.13): menu-bar icon, top bar, Ablauf-Übersicht, hotkeys, dialogs and
/// the shared <see cref="SessionManager"/>. Runs on the UI (main) thread;
/// SessionManager events arrive from its writer thread and are posted over.
/// </summary>
internal sealed class AppController : IDisposable
{
    private readonly Avalonia.Application _app;
    private readonly AppConfig _config;
    private readonly SessionManager _session;
    private readonly MacHotkeys _hotkeys = new();
    private readonly TopBarWindow _topBar;
    private readonly ToastOverlay _toast = new();
    private readonly TrayIcon _trayIcon;
    private readonly NativeMenuItem _toggleMenuItem = new("Aufnahme starten");
    private readonly DispatcherTimer _skipModifierPoll;
    private readonly LocalSaveService _saveService;
    private bool _saveServiceRunning;
    private FlowPreviewWindow? _flowWindow;
    private ZoomCursorBoxOverlay? _zoomCursorBox;
    private bool _dialogOpen;

    /// <summary>Set when the user closes the Ablauf-Übersicht — stops the next click from popping it open again until the top bar's "Ablauf" asks for it.</summary>
    private bool _flowWindowManuallyHidden;

    public AppController(Avalonia.Application app)
    {
        _app = app;
        _config = ConfigService.Load();
        _session = new SessionManager(_config, MacPlatform.Create(_config));
        _session.ErrorOccurred += message => Post(() => _toast.ShowMessage(message, isError: true));
        _session.InfoOccurred += message => Post(() => _toast.ShowMessage(message, isError: false));
        _session.FlowPreviewChanged += (preview, isRecordedClick) => Post(() => OnFlowPreviewChanged(preview, isRecordedClick));

        // Lets an Ablauf opened in a browser save its edits straight back
        // into its own file while DocuClick runs (see LocalSaveService).
        _saveService = new LocalSaveService(_session.ApplyExternalEdit);
        // The Obsidian plugin can start/pause recording and set decision
        // points (paired through a token written into the vault).
        if (string.IsNullOrEmpty(_config.RemoteControlToken))
        {
            _config.RemoteControlToken = ObsidianAppLink.NewToken();
            ConfigService.Save(_config);
        }

        _saveService.RemoteToken = _config.RemoteControlToken;
        _saveService.RemoteControl = command => Dispatcher.UIThread.InvokeAsync(() => HandleRemoteCommand(command)).GetTask();
        _saveServiceRunning = _saveService.Start();
        _session.ZoomToCursorChanged += active => Post(() =>
        {
            _topBar?.UpdateZoomState(active);
            if (!active)
            {
                _zoomCursorBox?.Cancel();
            }
        });

        // Any DocuClick window, menu, or the menu-bar icon — decided natively
        // from the window list, safe on the event-tap thread and covering
        // every dialog automatically.
        _session.IsPointOnOwnUi = point => Native.dc_point_on_own_ui(point.X, point.Y) == 1;
        // Enter confirming one of DocuClick's own dialogs is not content.
        _session.IsOwnUiFocused = () => Native.dc_frontmost_pid() == Environment.ProcessId;

        _topBar = new TopBarWindow(_config.ZoomToCursorRadius);
        _topBar.ToggleRecordingRequested += () => _ = ToggleRecordingAsync();
        _topBar.ShowFlowPreviewRequested += () => _ = ShowFlowPreviewAsync();
        _topBar.NewSessionRequested += () => _ = NewSessionAsync();
        _topBar.OpenOutputFolderRequested += OpenOutputFolder;
        _topBar.ZoomToCursorToggleRequested += () => _session.ToggleZoomToCursor();
        _topBar.SettingsRequested += () => _ = ShowSettingsAsync();
        _topBar.ZoomRadiusChanged += radius =>
        {
            _config.ZoomToCursorRadius = radius;
            _zoomCursorBox ??= new ZoomCursorBoxOverlay(radius);
            _zoomCursorBox.Preview(radius);
        };
        _topBar.ZoomRadiusCommitted += () => ConfigService.Save(_config);
        _topBar.Show();

        _trayIcon = CreateTrayIcon();

        // A cheap poll of the modifier state (not a key tap) for the top
        // bar's live "Skip" badge, like the Windows version.
        _skipModifierPoll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _skipModifierPoll.Tick += (_, _) => UpdateSkipBadge();
        _skipModifierPoll.Start();

        RegisterHotkeys();
        LogService.Log("DocuClick (macOS) gestartet.");

        var granted = PermissionsWindow.AllGranted;
        LogService.Log($"Berechtigungen vollständig: {granted}");
        if (granted)
        {
            Post(() => _toast.ShowMessage(
                $"DocuClick ist bereit — Aufnahme über die Leiste oben oder {MacHotkeys.Format(_config.StartStopModifiers, _config.StartStopKey)}.", isError: false));
        }
        else
        {
            Post(() => _ = ShowPermissionsAsync());
        }
    }

    private static void Post(Action action) => Dispatcher.UIThread.Post(action);

    // --- Menu-bar icon ------------------------------------------------------

    private TrayIcon CreateTrayIcon()
    {
        _toggleMenuItem.Click += (_, _) => _ = ToggleRecordingAsync();
        var newSession = new NativeMenuItem("Neue Session …");
        newSession.Click += (_, _) => _ = NewSessionAsync();
        var openFlow = new NativeMenuItem("Ablauf öffnen …");
        openFlow.Click += (_, _) => _ = OpenFlowAsync();
        var export = new NativeMenuItem("Nach draw.io exportieren …");
        export.Click += (_, _) => _ = ExportToDrawIoAsync();
        var settings = new NativeMenuItem("Einstellungen …");
        settings.Click += (_, _) => _ = ShowSettingsAsync();
        var permissions = new NativeMenuItem("Berechtigungen …");
        permissions.Click += (_, _) => _ = ShowPermissionsAsync();
        var quit = new NativeMenuItem("DocuClick beenden");
        quit.Click += (_, _) => Quit();

        var menu = new NativeMenu();
        foreach (var item in new NativeMenuItemBase[]
        {
            _toggleMenuItem, newSession, openFlow, new NativeMenuItemSeparator(),
            export, new NativeMenuItemSeparator(),
            settings, permissions, new NativeMenuItemSeparator(), quit
        })
        {
            menu.Items.Add(item);
        }

        var trayIcon = new TrayIcon { Icon = StatusIcon(recording: false), ToolTipText = "DocuClick – Bereit", Menu = menu };
        TrayIcon.SetIcons(_app, new TrayIcons { trayIcon });
        return trayIcon;
    }

    /// <summary>Same look as the Windows tray icon: dark disc with a red (recording), amber (paused) or gray dot.</summary>
    private static WindowIcon StatusIcon(bool recording, bool paused = false)
    {
        const int size = 44; // 22 pt @2x
        using var bitmap = new SKBitmap(size, size);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            using var body = new SKPaint { IsAntialias = true, Color = new SKColor(45, 45, 48) };
            canvas.DrawCircle(size / 2f, size / 2f, size / 2f - 3, body);
            var dotColor = recording ? new SKColor(0xE6, 0x39, 0x46) : paused ? new SKColor(0xF5, 0x9E, 0x0B) : new SKColor(150, 150, 155);
            using var dot = new SKPaint { IsAntialias = true, Color = dotColor };
            canvas.DrawCircle(size / 2f, size / 2f, size / 4.5f, dot);
        }

        using var data = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100);
        return new WindowIcon(new MemoryStream(data.ToArray()));
    }

    // --- Hotkeys / skip badge ------------------------------------------------------

    private void RegisterHotkeys()
    {
        _hotkeys.UnregisterAll();
        Register(_config.StartStopModifiers, _config.StartStopKey, "Aufnahme starten/stoppen", () => _ = ToggleRecordingAsync());
        Register(_config.BranchMarkModifiers, _config.BranchMarkKey, "Abzweigung setzen", () => _ = MarkDecisionPointAsync());
        Register(_config.ZoomToCursorModifiers, _config.ZoomToCursorKey, "Zoom-auf-Cursor umschalten", () => _session.ToggleZoomToCursor());
    }

    private void Register(string modifiers, string key, string label, Action action)
    {
        if (_hotkeys.Register(modifiers, key, action, out var error))
        {
            LogService.Log($"Hotkey registriert: {MacHotkeys.Format(modifiers, key)} -> {label}");
        }
        else
        {
            LogService.Log($"Hotkey '{label}' ({MacHotkeys.Format(modifiers, key)}) nicht registriert: {error}");
        }
    }

    private void UpdateSkipBadge()
    {
        var label = _config.SkipRecordingModifier switch
        {
            "Shift" => "⇧",
            "Control" => "⌃",
            "Alt" => "⌥",
            "Command" => "⌘",
            _ => null
        };
        _topBar.UpdateSkipModifierActive(_session.IsRunning && label is not null && _session.IsSkipModifierHeldNow(), label ?? "");
    }

    // --- Recording --------------------------------------------------------------

    /// <summary>
    /// Start/stop (menu, top bar, hotkey): stopping pauses (the session stays
    /// loaded for editing, like on Windows); starting resumes the loaded or
    /// last-used file without a dialog — "Neu" always asks.
    /// </summary>
    private async Task ToggleRecordingAsync()
    {
        if (_session.IsRunning)
        {
            _session.Pause();
            RefreshRecordingState();
            return;
        }

        var fileName = ResolveResumeFileName() ?? await ShowDialogAsync(() => new SessionStartWindow(_config));
        if (fileName is not null)
        {
            StartSession(fileName, isNewSession: false);
        }
    }

    private async Task NewSessionAsync()
    {
        var fileName = await ShowDialogAsync(() => new SessionStartWindow(_config));
        if (fileName is not null)
        {
            StartSession(fileName, isNewSession: true);
        }
    }

    private void StartSession(string fileName, bool isNewSession)
    {
        if (!PermissionsWindow.AllGranted)
        {
            _ = ShowPermissionsAsync();
            return;
        }

        try
        {
            if (isNewSession && _session.IsRunning)
            {
                _session.StartNewSession(fileName);
            }
            else
            {
                _session.Start(fileName);
            }

            RememberLastSession(fileName);
            if (isNewSession)
            {
                _toast.ShowMessage($"Neue Session gestartet: {Path.GetFileName(fileName)}", isError: false);
            }
        }
        catch (Exception ex)
        {
            LogService.Log($"Start fehlgeschlagen: {ex}");
            _ = MessageWindow.Show($"Aufnahme konnte nicht gestartet werden:\n{ex.Message}", isError: true);
        }

        RefreshRecordingState();
    }

    /// <summary>A command from the Obsidian plugin (see LocalSaveService /control); runs on the UI thread.</summary>
    private RemoteStatus HandleRemoteCommand(RemoteCommand command)
    {
        RemoteStatus Status(bool ok, string message) => new(ok, message, _session.IsRunning, _session.IsPaused, _session.CurrentTargetFileName);
        try
        {
            switch (command.Action)
            {
                case "start":
                    var file = command.File!;
                    if (_session.IsRunning && string.Equals(_session.CurrentTargetFileName, file, StringComparison.Ordinal))
                    {
                        return Status(true, "Aufnahme läuft bereits.");
                    }

                    if (!PermissionsWindow.AllGranted)
                    {
                        _ = ShowPermissionsAsync();
                        return Status(false, "DocuClick braucht noch Berechtigungen (siehe Fenster am Mac).");
                    }

                    StartSession(file, isNewSession: _session.IsRunning);
                    return Status(_session.IsRunning, _session.IsRunning ? "Aufnahme läuft." : "Aufnahme konnte nicht gestartet werden.");

                case "pause":
                    if (_session.IsRunning)
                    {
                        _session.Pause();
                        RefreshRecordingState();
                    }

                    return Status(true, "Aufnahme pausiert.");

                case "branch":
                    if (!_session.IsRunning)
                    {
                        return Status(false, "Es läuft keine Aufnahme.");
                    }

                    _session.MarkDecisionPoint(string.IsNullOrWhiteSpace(command.Name) ? "Pfad" : command.Name.Trim());
                    return Status(true, "Abzweigung gesetzt.");

                default:
                    return Status(true, _session.IsRunning ? "Aufnahme läuft." : _session.IsPaused ? "Aufnahme pausiert." : "Bereit.");
            }
        }
        catch (Exception ex)
        {
            LogService.Log($"Befehl aus Obsidian fehlgeschlagen: {ex}");
            return Status(false, ex.Message);
        }
    }

    private void RefreshRecordingState()
    {
        var recording = _session.IsRunning;
        var paused = _session.IsPaused;
        _toggleMenuItem.Header = recording ? "Aufnahme stoppen" : paused ? "Aufnahme fortsetzen" : "Aufnahme starten";
        _trayIcon.Icon = StatusIcon(recording, paused);
        _trayIcon.ToolTipText = recording ? "DocuClick – Aufnahme läuft" : paused ? "DocuClick – Aufnahme pausiert" : "DocuClick – Bereit";
        _topBar.UpdateStatus(recording, paused);
    }

    /// <summary>The currently loaded session, or else the last one used (if it still exists).</summary>
    private string? ResolveResumeFileName()
    {
        if (_session.HasActiveSession && !string.IsNullOrWhiteSpace(_session.CurrentTargetFileName))
        {
            return _session.CurrentTargetFileName;
        }

        return _config.LastSessionFileName is { Length: > 0 } last && File.Exists(last) ? last : null;
    }

    private void RememberLastSession(string filePath)
    {
        _config.LastSessionFileName = filePath;
        _config.RememberRecentOutputPath(Path.GetDirectoryName(filePath) ?? "");
        ConfigService.Save(_config);
    }

    private string? ResolveStartingFolder() => _session.CurrentSessionFolder ?? _config.RecentOutputPaths.FirstOrDefault();

    private void OpenOutputFolder()
    {
        var folder = ResolveStartingFolder();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            _toast.ShowMessage("Keine aktive Session und kein zuletzt verwendeter Ordner gefunden.", isError: false);
            return;
        }

        Process.Start("/usr/bin/open", new[] { folder });
    }

    // --- Branching / Ablauf-Übersicht --------------------------------------------------

    private async Task MarkDecisionPointAsync()
    {
        if (!_session.HasActiveSession)
        {
            _session.MarkDecisionPoint(string.Empty); // shows the "nothing to branch from" info
            return;
        }

        if (await ShowDialogAsync(() => new BranchNameWindow()) is { } name)
        {
            _session.MarkDecisionPoint(name);
        }
    }

    private void OnFlowPreviewChanged(FlowPreview? preview, bool isRecordedClick)
    {
        if (preview is null)
        {
            _flowWindow?.Hide();
            return;
        }

        var window = EnsureFlowWindow();
        window.UpdatePreview();
        if (!_flowWindowManuallyHidden && !window.IsVisible)
        {
            // Shown without taking focus from the app being recorded.
            window.ShowActivated = false;
            window.Show();
            window.ShowActivated = true;
        }
    }

    private FlowPreviewWindow EnsureFlowWindow()
    {
        if (_flowWindow is not null)
        {
            return _flowWindow;
        }

        var window = new FlowPreviewWindow();
        if (_saveServiceRunning)
        {
            // The page and screenshots come from the local service (see FlowPreviewWindow).
            window.PageHost = new EditorPageHost(_session, window, (folder, file) =>
            {
                var root = ObsidianVault.FindRoot(folder) ?? folder;
                var relative = Path.GetRelativePath(root, Path.GetFullPath(Path.Combine(folder, file)));
                return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
                    ? null
                    : _saveService.EditorImageUrl(relative.Replace('\\', '/'));
            });
            window.PageUrl = _saveService.EditorPageUrl;
            var pageHost = window.PageHost;
            _saveService.EditorPage = () => Dispatcher.UIThread.InvokeAsync(() => pageHost.BuildPage()).GetTask().GetAwaiter().GetResult();
            _saveService.EditorImageRoot = () => _session.CurrentSessionFolder is { } folder ? ObsidianVault.FindRoot(folder) ?? folder : null;
        }

        window.CloseRequested += () =>
        {
            _flowWindowManuallyHidden = true;
            window.Hide();
        };
        _flowWindow = window;
        return window;
    }

    /// <summary>Top bar "Ablauf": toggles the overview; with nothing loaded yet, asks for a file to open.</summary>
    private async Task ShowFlowPreviewAsync()
    {
        if (_flowWindow is not null)
        {
            if (_flowWindow.IsVisible)
            {
                _flowWindowManuallyHidden = true;
                _flowWindow.Hide();
            }
            else
            {
                _flowWindowManuallyHidden = false;
                _flowWindow.Show();
                _flowWindow.Activate();
            }

            return;
        }

        if (_session.IsRunning)
        {
            _toast.ShowMessage("Noch keine Ablauf-Übersicht vorhanden — sie erscheint mit dem ersten aufgezeichneten Klick.", isError: false);
            return;
        }

        await OpenFlowAsync();
    }

    // --- Files ---------------------------------------------------------------------

    private async Task<string?> PickAblaufFileAsync(string title)
    {
        var topLevel = (TopLevel)_topBar;
        var start = ResolveStartingFolder() is { } folder && Directory.Exists(folder)
            ? await topLevel.StorageProvider.TryGetFolderFromPathAsync(folder)
            : null;
        Native.dc_activate_self();
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = start,
            FileTypeFilter = new[] { Ui.AblaufFileType, FilePickerFileTypes.All }
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    /// <summary>Menu "Ablauf öffnen …": view/edit an existing Ablauf without recording.</summary>
    private async Task OpenFlowAsync()
    {
        if (await PickAblaufFileAsync("Ablauf zum Ansehen/Bearbeiten wählen") is not { } path)
        {
            return;
        }

        try
        {
            _flowWindowManuallyHidden = false;
            _session.OpenForEditing(path);
            RememberLastSession(path);
            RefreshRecordingState();
        }
        catch (Exception ex)
        {
            LogService.Log($"Ablauf öffnen fehlgeschlagen: {ex}");
            await MessageWindow.Show($"Ablauf konnte nicht geöffnet werden:\n{ex.Message}", isError: true);
        }
    }

    /// <summary>Menu "Nach draw.io exportieren …": converts an Ablauf into a .drawio file next to it.</summary>
    private async Task ExportToDrawIoAsync()
    {
        if (await PickAblaufFileAsync("Ablauf für den draw.io-Export wählen") is not { } path)
        {
            return;
        }

        var drawioPath = Path.ChangeExtension(path, ".drawio");
        if (File.Exists(drawioPath) && !await ConfirmWindow.AskAsync($"„{Path.GetFileName(drawioPath)}“ existiert bereits. Überschreiben?"))
        {
            return;
        }

        try
        {
            DrawIoConverter.Convert(path, Path.GetDirectoryName(path)!, drawioPath);
            _toast.ShowMessage($"Nach draw.io exportiert: {Path.GetFileName(drawioPath)}", isError: false);
        }
        catch (Exception ex)
        {
            LogService.Log($"draw.io-Export fehlgeschlagen: {ex}");
            await MessageWindow.Show($"Export fehlgeschlagen:\n{ex.Message}", isError: true);
        }
    }

    // --- Dialogs --------------------------------------------------------------------

    /// <summary>One dialog at a time (a second hotkey press while one is open is ignored).</summary>
    private async Task<T?> ShowDialogAsync<T>(Func<DialogWindow<T>> create)
    {
        if (_dialogOpen)
        {
            return default;
        }

        _dialogOpen = true;
        try
        {
            return await create().ShowAndWaitAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private async Task ShowSettingsAsync()
    {
        var saved = await ShowDialogAsync(() =>
        {
            var window = new SettingsWindow(_config);
            window.PermissionsRequested += () => _ = new PermissionsWindow().ShowAndWaitAsync();
            return window;
        });
        if (saved)
        {
            RegisterHotkeys();
        }
    }

    private Task ShowPermissionsAsync() => ShowDialogAsync(() => new PermissionsWindow());

    private void Quit()
    {
        Dispose();
        Environment.Exit(0);
    }

    public void Dispose()
    {
        _skipModifierPoll.Stop();
        _hotkeys.Dispose();
        _saveService.Dispose();
        _session.Dispose();
        _trayIcon.Dispose();
        LogService.Flush();
    }
}
