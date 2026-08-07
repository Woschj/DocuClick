using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

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
    internal const double BarHeight = 28;
    private const double CornerRadius = BarHeight / 2;

    private readonly Ellipse _statusDot;
    private readonly TextBlock _statusText;
    private readonly Button _toggleRecordingButton;
    private readonly Button _showFlowPreviewButton;
    private readonly Button _newSessionButton;
    private readonly Button _zoomToCursorButton;
    private readonly Slider _zoomRadiusSlider;

    public event Action? ToggleRecordingRequested;

    /// <summary>
    /// "Übersicht" button — reopens the Ablauf-Übersicht panel if the user
    /// closed it via its own header ✕. Replaced the old dedicated
    /// "Abzweigung"-button here: marking a decision point moved into the
    /// panel's own toolbar (reachable right where the rest of the editing
    /// — rename/delete/reparent/connect — already lives), so this bar only
    /// needed a way back in, not a duplicate control for the same action.
    /// </summary>
    public event Action? ShowFlowPreviewRequested;

    public event Action? NewSessionRequested;
    public event Action? ZoomToCursorToggleRequested;

    /// <summary>Fired live while the zoom-radius slider is being dragged/adjusted — the new radius in pixels. Not persisted to disk yet, see <see cref="ZoomRadiusCommitted"/>.</summary>
    public event Action<int>? ZoomRadiusChanged;

    /// <summary>Fired once the slider drag/keyboard adjustment settles — the point at which the caller should actually save the new radius to config.json, instead of on every intermediate tick.</summary>
    public event Action? ZoomRadiusCommitted;

    private const int ZoomRadiusMin = 50;
    private const int ZoomRadiusMax = 600;

    public TopBarWindow(int initialZoomRadius)
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

        // Small state dot ahead of the status text — same red used for
        // "recording" everywhere else in the app (RecordingIndicatorOverlay,
        // the minimap's "current node" box) so the color already means
        // something to the eye before reading a single word, and idle/
        // recording is tellable at a glance even at a distance where the
        // text itself isn't legible.
        _statusDot = new Ellipse
        {
            Width = 8,
            Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };

        _statusText = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        };

        var statusGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        statusGroup.Children.Add(_statusDot);
        statusGroup.Children.Add(_statusText);

        var buttonStyle = BuildButtonStyle();

        _toggleRecordingButton = CreateButton(buttonStyle, "Start", "Aufnahme starten/stoppen (wie der Tray-Menüpunkt bzw. der Start/Stop-Hotkey).");
        _toggleRecordingButton.Click += (_, _) => ToggleRecordingRequested?.Invoke();

        _showFlowPreviewButton = CreateButton(buttonStyle, "Übersicht",
            "Öffnet die Ablauf-Übersicht wieder, falls sie geschlossen wurde. Abzweigungen setzen, umbenennen, löschen, verbinden und verschieben passiert jetzt direkt im Panel.");
        _showFlowPreviewButton.Click += (_, _) => ShowFlowPreviewRequested?.Invoke();

        _newSessionButton = CreateButton(buttonStyle, "Neue Session", "Startet eine neue Aufnahme-Session (fragt nach Zieldatei) — schließt bei laufender Aufnahme zuerst die aktuelle Datei ab.");
        _newSessionButton.Click += (_, _) => NewSessionRequested?.Invoke();

        // Toggled per-screenshot from here instead of only via the global
        // hotkey/Settings, so switching between "whole window" and "just
        // around the cursor" doesn't require leaving the flow to open a menu.
        _zoomToCursorButton = CreateButton(buttonStyle, "Zoom: Aus", "Zoom-auf-Cursor umschalten: die nächsten Screenshots erfassen nur den Bereich um den Mauszeiger statt des ganzen Fensters (auch per Hotkey möglich, siehe Einstellungen).");
        _zoomToCursorButton.Click += (_, _) => ZoomToCursorToggleRequested?.Invoke();

        // Only shown while zoom-to-cursor is active — keeps the bar compact
        // the rest of the time instead of permanently reserving space for a
        // control that does nothing until then. A box overlay (see
        // ZoomCursorBoxOverlay) tracks the cursor to preview the resulting
        // capture area live while this is being dragged.
        _zoomRadiusSlider = new Slider
        {
            Minimum = ZoomRadiusMin,
            Maximum = ZoomRadiusMax,
            Value = Math.Clamp(initialZoomRadius, ZoomRadiusMin, ZoomRadiusMax),
            Width = 70,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 8, 0),
            Visibility = Visibility.Collapsed,
            Foreground = Brushes.White,
            ToolTip = "Größe des Zoom-auf-Cursor-Bereichs"
        };
        _zoomRadiusSlider.ValueChanged += (_, e) =>
        {
            var radius = (int)e.NewValue;
            _zoomRadiusSlider.ToolTip = $"Zoom-Bereich: {radius * 2}×{radius * 2}px";
            ZoomRadiusChanged?.Invoke(radius);
        };
        _zoomRadiusSlider.PreviewMouseUp += (_, _) => ZoomRadiusCommitted?.Invoke();
        _zoomRadiusSlider.KeyUp += (_, _) => ZoomRadiusCommitted?.Invoke();

        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(statusGroup);
        panel.Children.Add(CreateSeparator());
        panel.Children.Add(_toggleRecordingButton);
        panel.Children.Add(_showFlowPreviewButton);
        panel.Children.Add(CreateSeparator());
        panel.Children.Add(_newSessionButton);
        panel.Children.Add(CreateSeparator());
        panel.Children.Add(_zoomToCursorButton);
        panel.Children.Add(_zoomRadiusSlider);

        // A subtle top-to-bottom gradient (instead of a flat fill) plus a
        // glossy highlight strip across the upper half give the pill some
        // depth instead of reading as a flat-colored sticker; same accent
        // blue family as every dialog window otherwise. Fully rounded
        // (stadium shape) since the bar floats free instead of sitting
        // flush against the screen edge, with a soft drop shadow so it
        // visually lifts off whatever window/desktop is behind it.
        var background = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0x3E, 0x7C, 0xEA), 0.0),
                new GradientStop(Color.FromRgb(0x2D, 0x6C, 0xDF), 0.55),
                new GradientStop(Color.FromRgb(0x25, 0x5C, 0xC4), 1.0)
            }
        };

        var glossHighlight = new Border
        {
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Top,
            Height = BarHeight * 0.55,
            // Top corners rounded to match the pill's own CornerRadius,
            // bottom corners square: FrameworkElement.ClipToBounds only
            // clips to a plain rectangle, not a Border's rounded geometry,
            // so without this the highlight's flat rectangular top edge
            // would poke small square corners past the pill's rounded
            // silhouette. Square bottom corners are fine — that edge sits
            // inside the pill body, nowhere near its outer boundary.
            CornerRadius = new System.Windows.CornerRadius(CornerRadius, CornerRadius, 0, 0),
            Background = new LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0),
                EndPoint = new System.Windows.Point(0, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(55, 255, 255, 255), 0.0),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 1.0)
                }
            }
        };

        var pillContent = new Grid();
        pillContent.Children.Add(glossHighlight);
        pillContent.Children.Add(panel);

        var border = new Border
        {
            Background = background,
            CornerRadius = new System.Windows.CornerRadius(CornerRadius),
            Height = BarHeight,
            ClipToBounds = true,
            Child = pillContent,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                Opacity = 0.35,
                BlurRadius = 14,
                ShadowDepth = 2,
                Direction = 270
            }
        };
        Content = border;

        UpdateStatus(isRecording: false, detail: null, supportsBranching: false);
        UpdateZoomToCursorState(active: false);

        // Draggable, but not when the click originates on one of the
        // buttons or the zoom-radius slider — otherwise a button press (or
        // a drag along the slider's track) would also start a window drag
        // and the click/drag could get lost.
        border.MouseLeftButtonDown += (_, e) =>
        {
            if (!IsWithinInteractiveControl(e.OriginalSource as DependencyObject))
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
            HwndSource.FromHwnd(hwnd)?.AddHook(NativeMethods.DeliverActivatingClick);
        };

        // ShowActivated=false means this bar starts out inactive whenever
        // focus was last on whatever's being documented — the normal case.
        // WPF marks the routed MouseButtonEventArgs for the very click that
        // re-activates an inactive window as Handled=true unconditionally,
        // before any button ever sees it (Click never fires) — confirmed
        // via diagnostic logging on the Ablauf-Übersicht's pan gesture,
        // which shares this exact window setup; answering WM_MOUSEACTIVATE
        // with MA_ACTIVATE (see NativeMethods.DeliverActivatingClick) does
        // NOT prevent this, since it's WPF's own activation bookkeeping,
        // not the Win32 message. Activating pre-emptively on hover, before
        // any click happens, sidesteps it: by the time a click actually
        // lands, the bar is already active, so there's no "activating
        // click" left to eat.
        MouseEnter += (_, _) =>
        {
            if (!IsActive)
            {
                Activate();
            }
        };
    }

    private static bool IsWithinInteractiveControl(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is Button or Slider)
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

    /// <summary>Thin vertical divider between logical button groups (recording/branching, session, zoom) instead of one undifferentiated row.</summary>
    private static Border CreateSeparator() => new()
    {
        Width = 1,
        Margin = new Thickness(2, 6, 8, 6),
        Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255))
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
        // "Übersicht" and "Neue Session" are always clickable regardless of
        // supportsBranching/recording state — reopening the panel (or
        // starting a session that turns out not to support branching) are
        // both meaningful either way; SessionManager/FlowPreviewOverlay
        // themselves already handle the "doesn't apply right now" case via
        // an info balloon instead of a disabled button.

        // Same red as RecordingIndicatorOverlay/the minimap's "current node"
        // box when active; a dim translucent white at rest so it reads as
        // "off" without looking like an error state.
        _statusDot.Fill = isRecording
            ? new SolidColorBrush(Color.FromRgb(0xE6, 0x39, 0x46))
            : new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));

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
        _zoomRadiusSlider.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
    }
}
