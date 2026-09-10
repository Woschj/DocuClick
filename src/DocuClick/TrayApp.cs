using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace DocuClick;

/// <summary>
/// Owns the tray icon, its context menu, and the recording on/off state.
/// Later steps (mouse hook, screenshot, Obsidian writer) subscribe to
/// <see cref="RecordingStateChanged"/> instead of touching the tray directly.
/// </summary>
public sealed class TrayApp : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _toggleItem;
    private bool _isRecording;
    private bool _disposed;
    private string _baseStatusText = "DocuClick - Aufnahme gestoppt";

    public event Action<bool>? RecordingStateChanged;
    public event Action? SettingsRequested;

    /// <summary>Tray menu: "Nach draw.io exportieren..." — converts an existing Canvas-mode session into a .drawio file in one pass (see DrawIoConverter), since draw.io is no longer a live-recording mode.</summary>
    public event Action? ExportToDrawIoRequested;

    /// <summary>Tray menu: "Nach HTML exportieren..." — converts an existing Canvas-mode session into a single self-contained, shareable .html file (see HtmlFlowExporter).</summary>
    public event Action? ExportToHtmlRequested;

    public bool IsRecording => _isRecording;

    public TrayApp()
    {
        _toggleItem = new ToolStripMenuItem("Aufnahme starten", null, OnToggleClicked);
        var settingsItem = new ToolStripMenuItem("Einstellungen...", null, OnSettingsClicked);
        var exportItem = new ToolStripMenuItem("Nach draw.io exportieren...", null, (_, _) => ExportToDrawIoRequested?.Invoke());
        var exportHtmlItem = new ToolStripMenuItem("Nach HTML exportieren...", null, (_, _) => ExportToHtmlRequested?.Invoke());
        var exitItem = new ToolStripMenuItem("Beenden", null, OnExitClicked);

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add(_toggleItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(settingsItem);
        contextMenu.Items.Add(exportItem);
        contextMenu.Items.Add(exportHtmlItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = BuildStatusIcon(isRecording: false),
            Text = _baseStatusText,
            Visible = true,
            ContextMenuStrip = contextMenu
        };

        _notifyIcon.MouseClick += OnTrayIconClick;
    }

    public void ToggleRecording() => SetRecording(!_isRecording);

    public void SetRecording(bool isRecording) => SetRecording(isRecording, raiseEvent: true);

    /// <summary>
    /// Updates the icon/tooltip/menu to reflect a recording state that was
    /// already started/stopped elsewhere (e.g. "Neue Session" driving its
    /// own start sequence) — without re-firing <see cref="RecordingStateChanged"/>,
    /// which would otherwise re-trigger that same start/stop logic.
    /// </summary>
    public void SyncRecordingState(bool isRecording) => SetRecording(isRecording, raiseEvent: false);

    /// <summary>
    /// Updates the tray menu and tooltip to indicate whether a session is currently paused.
    /// </summary>
    public void UpdatePausedState(bool isPaused)
    {
        if (_isRecording)
        {
            return;
        }

        _toggleItem.Text = isPaused ? "Aufnahme fortsetzen" : "Aufnahme starten";
        _baseStatusText = isPaused ? "DocuClick - Aufnahme pausiert" : "DocuClick - Aufnahme gestoppt";
        UpdateTooltip();
    }

    private void SetRecording(bool isRecording, bool raiseEvent)
    {
        if (_isRecording == isRecording)
        {
            return;
        }

        _isRecording = isRecording;
        _toggleItem.Text = isRecording ? "Aufnahme stoppen" : "Aufnahme starten";
        _baseStatusText = isRecording ? "DocuClick - Aufnahme läuft" : "DocuClick - Aufnahme gestoppt";
        UpdateTooltip();

        var oldIcon = _notifyIcon.Icon;
        _notifyIcon.Icon = BuildStatusIcon(isRecording);
        oldIcon?.Dispose();

        if (raiseEvent)
        {
            RecordingStateChanged?.Invoke(isRecording);
        }
    }

    /// <summary>
    /// On-screen rectangle of this tray icon in the taskbar, if it can be
    /// resolved — used to exclude clicks on the icon itself from the
    /// recording. NotifyIcon exposes neither its window handle nor its
    /// icon id publicly, so both come via reflection into BCL-private
    /// fields; if that ever breaks (a future .NET changes field names),
    /// this fails closed (returns null, meaning "don't filter") instead of
    /// throwing.
    /// </summary>
    public Rectangle? GetIconScreenBounds()
    {
        try
        {
            var windowField = typeof(NotifyIcon).GetField("window", BindingFlags.NonPublic | BindingFlags.Instance);
            var idField = typeof(NotifyIcon).GetField("id", BindingFlags.NonPublic | BindingFlags.Instance);

            if (windowField?.GetValue(_notifyIcon) is not NativeWindow window
                || idField?.GetValue(_notifyIcon) is not int id)
            {
                return null;
            }

            var identifier = new NativeMethods.NOTIFYICONIDENTIFIER
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.NOTIFYICONIDENTIFIER>(),
                hWnd = window.Handle,
                uID = (uint)id
            };

            if (NativeMethods.Shell_NotifyIconGetRect(ref identifier, out var rect) != 0)
            {
                return null;
            }

            return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        }
        catch
        {
            return null;
        }
    }

    private void UpdateTooltip()
    {
        _notifyIcon.Text = _baseStatusText.Length > 63 ? _baseStatusText[..63] : _baseStatusText;
    }

    private void OnTrayIconClick(object? sender, MouseEventArgs e)
    {
        // Left-click opens settings (a stray click must never silently
        // start a recording session); right-click opens the context menu
        // (Windows does this automatically for ContextMenuStrip) which has
        // the actual start/stop item, and the start/stop hotkey covers the
        // "quick toggle" case instead.
        if (e.Button == MouseButtons.Left)
        {
            SettingsRequested?.Invoke();
        }
    }

    private void OnToggleClicked(object? sender, EventArgs e) => ToggleRecording();

    private void OnSettingsClicked(object? sender, EventArgs e) => SettingsRequested?.Invoke();

    public void ShowError(string message)
    {
        _notifyIcon.ShowBalloonTip(4000, "DocuClick - Fehler", message, ToolTipIcon.Error);
    }

    public void ShowInfo(string message)
    {
        _notifyIcon.ShowBalloonTip(3000, "DocuClick", message, ToolTipIcon.Info);
    }

    private void OnExitClicked(object? sender, EventArgs e)
    {
        Dispose();
        Application.Current.Shutdown();
    }

    /// <summary>
    /// Draws a small dot-in-circle glyph at runtime so the tray icon can
    /// reflect recording state (gray = stopped, red = recording) without
    /// shipping separate .ico assets.
    /// </summary>
    private static Icon BuildStatusIcon(bool isRecording)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var bodyBrush = new SolidBrush(Color.FromArgb(255, 45, 45, 48));
            g.FillEllipse(bodyBrush, 2, 2, 28, 28);

            var statusColor = isRecording
                ? Color.FromArgb(255, 220, 40, 40)
                : Color.FromArgb(255, 130, 130, 130);
            using var statusBrush = new SolidBrush(statusColor);
            g.FillEllipse(statusBrush, 9, 9, 14, 14);
        }

        var hIcon = bitmap.GetHicon();
        try
        {
            using var handleIcon = Icon.FromHandle(hIcon);
            return (Icon)handleIcon.Clone();
        }
        finally
        {
            // Icon.FromHandle does not own the HICON; it must be destroyed
            // explicitly or every toggle leaks a GDI handle.
            NativeMethods.DestroyIcon(hIcon);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Icon?.Dispose();
        _notifyIcon.Dispose();
    }
}
