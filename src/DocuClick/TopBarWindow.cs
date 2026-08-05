using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

// UseWindowsForms implicitly brings System.Drawing/Windows.Forms into every
// file too; combined with the System.Windows(.Media) usings above, several
// names (Color, Brushes, Button, TextBox, Orientation, Cursors, ...) exist
// in both and become ambiguous. This file is WPF-only UI, so alias to those.
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Control = System.Windows.Controls.Control;
using Orientation = System.Windows.Controls.Orientation;
using Cursors = System.Windows.Input.Cursors;

namespace DocuClick;

/// <summary>
/// Small floating, draggable pill centered at the top of the primary screen
/// on first launch — like a TeamViewer session toolbar, NOT a full-width
/// bar. It hosts real buttons (start/stop, branch controls, "Neue
/// Session") so it can't be click-through like the other overlays, which
/// means it must stay content-sized: a full-width bar would block window
/// dragging, menu bars, and Snap zones along the entire top edge.
/// Visible for the app's whole lifetime (not just while recording), so
/// there is always an at-a-glance answer to "is it running right now".
/// </summary>
public sealed class TopBarWindow : Window
{
    internal const double BarHeight = 26;
    private const double CornerRadius = BarHeight / 2;

    private readonly TextBlock _statusText;
    private readonly Button _toggleRecordingButton;
    private readonly Button _markBranchButton;
    private readonly Button _jumpBranchButton;
    private readonly Button _newSessionButton;
    private readonly Button _zoomToCursorButton;

    public event Action? ToggleRecordingRequested;
    public event Action? MarkBranchRequested;
    public event Action? JumpBranchRequested;
    public event Action? NewSessionRequested;
    public event Action? ZoomToCursorToggleRequested;

    public TopBarWindow()
    {
        var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        // A small gap from the very top edge so the fully rounded top
        // corners are actually visible instead of being clipped by the
        // screen edge.
        Top = bounds.Top + 6;

        _statusText = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 10, 0)
        };

        var buttonStyle = BuildButtonStyle();

        _toggleRecordingButton = CreateButton(buttonStyle, "Start", "Aufnahme starten/stoppen (wie der Tray-Menüpunkt bzw. der Start/Stop-Hotkey).");
        _toggleRecordingButton.Click += (_, _) => ToggleRecordingRequested?.Invoke();

        _markBranchButton = CreateButton(buttonStyle, "Branch setzen", "Aktuellen Knoten/Abschnitt unter einem Namen als Branch-Punkt markieren (fragt nach dem Namen).");
        _markBranchButton.Click += (_, _) => MarkBranchRequested?.Invoke();

        _jumpBranchButton = CreateButton(buttonStyle, "Branch auswählen", "Zu einem benannten Branch-Punkt springen: der nächste Klick beginnt dort eine neue Abzweigung.");
        _jumpBranchButton.Click += (_, _) => JumpBranchRequested?.Invoke();

        _newSessionButton = CreateButton(buttonStyle, "Neue Session", "Startet eine neue Aufnahme-Session (fragt nach Zieldatei) — schließt bei laufender Aufnahme zuerst die aktuelle Datei ab.");
        _newSessionButton.Click += (_, _) => NewSessionRequested?.Invoke();

        // Toggled per-screenshot from here instead of only via the global
        // hotkey/Settings, so switching between "whole window" and "just
        // around the cursor" doesn't require leaving the flow to open a menu.
        _zoomToCursorButton = CreateButton(buttonStyle, "Zoom: Aus", "Zoom-auf-Cursor umschalten: die nächsten Screenshots erfassen nur den Bereich um den Mauszeiger statt des ganzen Fensters (auch per Hotkey möglich, siehe Einstellungen).");
        _zoomToCursorButton.Margin = new Thickness(0, 0, 8, 0);
        _zoomToCursorButton.Click += (_, _) => ZoomToCursorToggleRequested?.Invoke();

        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(_statusText);
        panel.Children.Add(_toggleRecordingButton);
        panel.Children.Add(_markBranchButton);
        panel.Children.Add(_jumpBranchButton);
        panel.Children.Add(_newSessionButton);
        panel.Children.Add(_zoomToCursorButton);

        // Solid, saturated blue (TeamViewer-toolbar style) instead of the
        // previous near-black bar, which blended into dark taskbars/title
        // bars and was hard to spot at a glance. Fully rounded (stadium
        // shape) now that the bar floats free instead of sitting flush
        // against the screen edge.
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x5D, 0xB3)),
            CornerRadius = new System.Windows.CornerRadius(CornerRadius),
            Height = BarHeight,
            Child = panel
        };
        Content = border;

        UpdateStatus(isRecording: false, detail: null, supportsBranching: false);
        UpdateZoomToCursorState(active: false);

        // Draggable, but not when the click originates on one of the
        // buttons — otherwise a button press would also start a drag and
        // the click could get lost.
        border.MouseLeftButtonDown += (_, e) =>
        {
            if (!IsWithinButton(e.OriginalSource as DependencyObject))
            {
                // ShowActivated is false (this bar must never steal focus
                // just by appearing), but DragMove()'s underlying SC_MOVE
                // needs the window activated to reliably initiate the move
                // on all Windows versions — activating only now, on an
                // explicit drag gesture, is fine.
                Activate();
                DragMove();
            }
        };

        // Content-sized (not screen-wide), so it only ever occupies a small
        // pill — everything outside it (window title bars, menus, Snap
        // zones) stays fully clickable, unlike an earlier full-width
        // version. Centered ONCE on first layout, not on every content
        // resize (e.g. when the branch-depth text changes length) — a
        // persistent re-centering would fight the user dragging it
        // elsewhere.
        Loaded += (_, _) => Left = bounds.Left + (bounds.Width - ActualWidth) / 2;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.ExcludeFromScreenCapture(hwnd);
        };
    }

    private static bool IsWithinButton(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is Button)
            {
                return true;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    private static Button CreateButton(Style style, string text, string tooltip) => new()
    {
        Style = style,
        Content = text,
        Margin = new Thickness(0, 0, 6, 0),
        ToolTip = tooltip
    };

    /// <summary>
    /// Frosted-glass pill buttons (semi-transparent white on the bar's
    /// blue) instead of default Windows button chrome, with hover/disabled
    /// states — built in code since this window has no XAML/resources of
    /// its own.
    /// </summary>
    private static Style BuildButtonStyle()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "ButtonBorder";
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        border.SetValue(Border.CornerRadiusProperty, new System.Windows.CornerRadius(CornerRadius - 4));
        border.SetValue(Border.PaddingProperty, new Thickness(9, 2, 9, 3));

        // Fully qualified: an unqualified "HorizontalAlignment"/"VerticalAlignment"
        // here would bind to the instance properties this Window inherits
        // from FrameworkElement (same simple name as the enum), not the enum.
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };

        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(TemplateProperty, template));
        style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromArgb(40, 255, 255, 255))));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 11.0));
        style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));

        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromArgb(75, 255, 255, 255))));
        style.Triggers.Add(hover);

        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromArgb(25, 255, 255, 255))));
        style.Triggers.Add(pressed);

        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromArgb(110, 255, 255, 255))));
        disabled.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromArgb(12, 255, 255, 255))));
        style.Triggers.Add(disabled);

        return style;
    }

    public void UpdateStatus(bool isRecording, string? detail, bool supportsBranching)
    {
        _toggleRecordingButton.Content = isRecording ? "Stop" : "Start";
        _markBranchButton.IsEnabled = isRecording && supportsBranching;
        _jumpBranchButton.IsEnabled = isRecording && supportsBranching;
        // "Neue Session" is always clickable: with no recording running it
        // just behaves like Start (see App.OnNewSessionRequested).

        var baseText = isRecording ? "DocuClick – Aufnahme läuft" : "DocuClick – Aufnahme gestoppt";
        _statusText.Text = detail is null ? baseText : $"{baseText} · {detail}";
    }

    /// <summary>Reflects "Zoom-auf-Cursor" on/off — driven by <see cref="SessionManager.ZoomToCursorChanged"/>, whether toggled from here, the hotkey, or Settings.</summary>
    public void UpdateZoomToCursorState(bool active)
    {
        _zoomToCursorButton.Content = active ? "Zoom: An" : "Zoom: Aus";
        // A local Background value (not a style setter) so it still shows
        // through everywhere except the hover/pressed triggers, which take
        // precedence over it as usual.
        _zoomToCursorButton.Background = active
            ? new SolidColorBrush(Color.FromArgb(160, 0x22, 0xC5, 0x5E))
            : new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
    }
}
