using System.Windows;
using System.Windows.Automation;
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
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Control = System.Windows.Controls.Control;
using Orientation = System.Windows.Controls.Orientation;
using Cursors = System.Windows.Input.Cursors;
using PenLineCap = System.Windows.Media.PenLineCap;

namespace DocuClick;

/// <summary>
/// Small floating, draggable pill centered at the top of the primary screen
/// on first launch — like a TeamViewer session toolbar, NOT a full-width
/// bar. It hosts real buttons (start/stop, branch controls, "Neue
/// Session") so it can't be click-through like the other overlays, which
/// is why it must stay content-sized and easily movable.
/// </summary>
public sealed class TopBarWindow : Window
{
    private const double BarHeight = 38;
    private const double CornerRadius = 19;

    private static readonly Geometry PlayIconGeo = Geometry.Parse("M 3 2.5 L 12.5 8 L 3 13.5 Z");
    private static readonly Geometry StopIconGeo = Geometry.Parse("M 3 3 H 13 V 13 H 3 Z");
    private static readonly Geometry FlowIconGeo = Geometry.Parse("M 2 8 H 5.5 M 5.5 8 L 9.5 4 M 5.5 8 L 9.5 12 M 9.5 4 H 13.5 M 9.5 12 H 13.5");
    private static readonly Geometry PlusIconGeo = Geometry.Parse("M 8 2.5 V 13.5 M 2.5 8 H 13.5");
    private static readonly Geometry ZoomIconGeo = Geometry.Parse("M 6.5 2 A 4.5 4.5 0 1 1 2 6.5 A 4.5 4.5 0 0 1 6.5 2 M 10 10 L 14 14");
    private static readonly Geometry ObsidianIconGeo = Geometry.Parse("M 8 2 L 13.5 6.5 L 8 14.5 L 2.5 6.5 Z M 2.5 6.5 H 13.5 M 8 2 V 14.5");

    private readonly Ellipse _statusDot;
    private readonly TextBlock _statusText;
    private readonly Path _toggleIcon;
    private readonly TextBlock _toggleText;
    private readonly Button _toggleRecordingButton;
    private readonly Button _showFlowPreviewButton;
    private readonly Button _newSessionButton;
    private readonly Button _copyObsidianButton;
    private readonly Path _zoomIcon;
    private readonly TextBlock _zoomText;
    private readonly Button _zoomToCursorButton;
    private readonly Slider _zoomRadiusSlider;

    public event Action? ToggleRecordingRequested;
    public event Action? ShowFlowPreviewRequested;
    public event Action? NewSessionRequested;
    public event Action? CopyObsidianEmbedRequested;
    public event Action? ZoomToCursorToggleRequested;

    /// <summary>Fired live while the zoom-radius slider is being dragged/adjusted — the new radius in pixels. Not persisted to disk yet, see <see cref="ZoomRadiusCommitted"/>.</summary>
    public event Action<int>? ZoomRadiusChanged;

    /// <summary>Fired once the slider drag/keyboard adjustment settles — the point at which the caller should actually save the new radius to config.json, instead of on every intermediate tick.</summary>
    public event Action? ZoomRadiusCommitted;

    private const int ZoomRadiusMin = 50;
    private const int ZoomRadiusMax = 600;

    public TopBarWindow(int initialZoomRadius)
    {
        var workArea = SystemParameters.WorkArea;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Top = workArea.Top + 8;

        // Subtle 6-dot drag grip on the far left
        var dragGrip = CreateDragGripVisual();

        // Status pill chip
        _statusDot = new Ellipse
        {
            Width = 8,
            Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };

        _statusText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9)),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };

        var statusChip = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new System.Windows.CornerRadius(13),
            Padding = new Thickness(8, 3, 10, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 4, 0)
        };
        var statusStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        statusStack.Children.Add(_statusDot);
        statusStack.Children.Add(_statusText);
        statusChip.Child = statusStack;

        var buttonStyle = BuildButtonStyle();

        // 1. Toggle Recording button
        _toggleRecordingButton = CreateIconButton(buttonStyle, PlayIconGeo, out _toggleIcon, out _toggleText, "Aufnahme",
            "Aufnahme starten/stoppen (wie der Tray-Menüpunkt bzw. der Start/Stop-Hotkey).");
        AutomationProperties.SetAutomationId(_toggleRecordingButton, "TopBar.ToggleRecording");
        _toggleRecordingButton.Click += (_, _) => ToggleRecordingRequested?.Invoke();

        // 2. Flow preview button
        _showFlowPreviewButton = CreateIconButton(buttonStyle, FlowIconGeo, out _, out _, "Ablauf",
            "Öffnet die Ablauf-Übersicht. Knoten lassen sich dort direkt anklicken, umbenennen, verschieben, verbinden und verzweigen.");
        AutomationProperties.SetAutomationId(_showFlowPreviewButton, "TopBar.ShowFlowPreview");
        _showFlowPreviewButton.Click += (_, _) => ShowFlowPreviewRequested?.Invoke();

        // 3. New session button
        _newSessionButton = CreateIconButton(buttonStyle, PlusIconGeo, out _, out _, "Neu",
            "Startet eine neue Aufnahme-Session (fragt nach Zieldatei) — schließt bei laufender Aufnahme zuerst die aktuelle Datei ab.");
        AutomationProperties.SetAutomationId(_newSessionButton, "TopBar.NewSession");
        _newSessionButton.Click += (_, _) => NewSessionRequested?.Invoke();

        // 4. Obsidian button
        _copyObsidianButton = CreateIconButton(buttonStyle, ObsidianIconGeo, out _, out _, "Obsidian",
            "Kopiert den interaktiven HTML-Einbindungscode für Obsidian in die Zwischenablage.");
        AutomationProperties.SetAutomationId(_copyObsidianButton, "TopBar.CopyObsidian");
        _copyObsidianButton.Click += (_, _) => CopyObsidianEmbedRequested?.Invoke();

        // 5. Zoom button
        _zoomToCursorButton = CreateIconButton(buttonStyle, ZoomIconGeo, out _zoomIcon, out _zoomText, "Zoom",
            "Zoom-auf-Cursor umschalten: die nächsten Screenshots erfassen nur den Bereich um den Mauszeiger statt des ganzen Fensters.");
        AutomationProperties.SetAutomationId(_zoomToCursorButton, "TopBar.ZoomToggle");
        _zoomToCursorButton.Click += (_, _) => ZoomToCursorToggleRequested?.Invoke();

        // 6. Custom-templated modern slider for zoom
        _zoomRadiusSlider = new Slider
        {
            Minimum = ZoomRadiusMin,
            Maximum = ZoomRadiusMax,
            Value = Math.Clamp(initialZoomRadius, ZoomRadiusMin, ZoomRadiusMax),
            Width = 72,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 8, 0),
            Visibility = Visibility.Collapsed,
            Style = Application.Current?.TryFindResource("ModernSlider") as Style,
            ToolTip = "Größe des Zoom-auf-Cursor-Bereichs"
        };
        AutomationProperties.SetAutomationId(_zoomRadiusSlider, "TopBar.ZoomRadiusSlider");
        _zoomRadiusSlider.ValueChanged += (_, e) =>
        {
            var radius = (int)e.NewValue;
            _zoomRadiusSlider.ToolTip = $"Zoom-Bereich: {radius * 2}×{radius * 2}px";
            ZoomRadiusChanged?.Invoke(radius);
        };
        _zoomRadiusSlider.PreviewMouseUp += (_, _) => ZoomRadiusCommitted?.Invoke();
        _zoomRadiusSlider.KeyUp += (_, _) => ZoomRadiusCommitted?.Invoke();

        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(dragGrip);
        panel.Children.Add(statusChip);
        panel.Children.Add(CreateSeparator());
        panel.Children.Add(_toggleRecordingButton);
        panel.Children.Add(_showFlowPreviewButton);
        panel.Children.Add(CreateSeparator());
        panel.Children.Add(_newSessionButton);
        panel.Children.Add(CreateSeparator());
        panel.Children.Add(_copyObsidianButton);
        panel.Children.Add(CreateSeparator());
        panel.Children.Add(_zoomToCursorButton);
        panel.Children.Add(_zoomRadiusSlider);

        // Modern acrylic dark slate background with refined edge and soft ambient shadow
        var background = new SolidColorBrush(Color.FromArgb(240, 11, 15, 25));

        var border = new Border
        {
            Background = background,
            CornerRadius = new System.Windows.CornerRadius(CornerRadius),
            BorderBrush = new SolidColorBrush(Color.FromArgb(42, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Height = BarHeight,
            ClipToBounds = true,
            Padding = new Thickness(2, 0, 6, 0),
            Child = panel,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                Opacity = 0.42,
                BlurRadius = 22,
                ShadowDepth = 4,
                Direction = 270
            }
        };
        Content = border;

        UpdateStatus(isRecording: false, detail: null, supportsBranching: false);
        UpdateZoomToCursorState(active: false);

        border.MouseLeftButtonDown += (_, e) =>
        {
            if (!IsWithinInteractiveControl(e.OriginalSource as DependencyObject))
            {
                Activate();
                DragMove();
            }
        };

        Loaded += (_, _) => Left = workArea.Left + (workArea.Width - ActualWidth) / 2;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.ExcludeFromScreenCapture(hwnd);
            HwndSource.FromHwnd(hwnd)?.AddHook(NativeMethods.DeliverActivatingClick);
        };

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

    private static Button CreateIconButton(
        Style style,
        Geometry iconGeo,
        out Path iconPath,
        out TextBlock labelBlock,
        string text,
        string tooltip,
        Brush? iconFill = null)
    {
        iconPath = new Path
        {
            Data = iconGeo,
            Fill = iconGeo == FlowIconGeo || iconGeo == PlusIconGeo || iconGeo == ZoomIconGeo || iconGeo == ObsidianIconGeo ? Brushes.Transparent : (iconFill ?? Brushes.White),
            Stroke = iconGeo == FlowIconGeo || iconGeo == PlusIconGeo || iconGeo == ZoomIconGeo || iconGeo == ObsidianIconGeo ? (iconFill ?? Brushes.White) : null,
            StrokeThickness = iconGeo == FlowIconGeo || iconGeo == PlusIconGeo || iconGeo == ZoomIconGeo || iconGeo == ObsidianIconGeo ? 1.4 : 0,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 11,
            Height = 11,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
        };

        labelBlock = new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };

        var stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(iconPath);
        stack.Children.Add(labelBlock);

        return new Button
        {
            Style = style,
            Content = stack,
            Margin = new Thickness(0, 0, 4, 0),
            ToolTip = tooltip
        };
    }

    private static FrameworkElement CreateDragGripVisual()
    {
        var grid = new Grid
        {
            Width = 8,
            Height = 16,
            Margin = new Thickness(8, 0, 4, 0),
            Cursor = Cursors.SizeAll,
            ToolTip = "Leiste verschieben",
            Background = Brushes.Transparent
        };

        var dotBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 2; col++)
            {
                var dot = new Ellipse
                {
                    Width = 2,
                    Height = 2,
                    Fill = dotBrush,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                    VerticalAlignment = System.Windows.VerticalAlignment.Top,
                    Margin = new Thickness(col * 4.5, row * 5.5 + 1.5, 0, 0)
                };
                grid.Children.Add(dot);
            }
        }

        return grid;
    }

    private static Border CreateSeparator() => new()
    {
        Width = 1,
        Height = 16,
        Margin = new Thickness(4, 0, 5, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(Color.FromArgb(35, 255, 255, 255))
    };

    private static Style BuildButtonStyle()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "ButtonBorder";
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.CornerRadiusProperty, new System.Windows.CornerRadius(13));
        border.SetValue(Border.PaddingProperty, new Thickness(9, 3, 9, 3));

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };

        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(TemplateProperty, template));
        style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromArgb(24, 255, 255, 255))));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(38, 255, 255, 255))));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 11.0));
        style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));

        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromArgb(50, 255, 255, 255))));
        hover.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(80, 255, 255, 255))));
        style.Triggers.Add(hover);

        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromArgb(15, 255, 255, 255))));
        style.Triggers.Add(pressed);

        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromArgb(100, 255, 255, 255))));
        disabled.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromArgb(10, 255, 255, 255))));
        disabled.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(15, 255, 255, 255))));
        style.Triggers.Add(disabled);

        return style;
    }

    public void UpdateStatus(bool isRecording, string? detail, bool supportsBranching)
    {
        var isPaused = !isRecording && detail == "Pausiert";

        if (isRecording)
        {
            _toggleIcon.Data = StopIconGeo;
            _toggleIcon.Fill = Brushes.White;
            _toggleIcon.Stroke = null;
            _toggleText.Text = "Stopp";
            _toggleRecordingButton.Background = new SolidColorBrush(Color.FromArgb(220, 0xF4, 0x3F, 0x5E));
            _toggleRecordingButton.BorderBrush = new SolidColorBrush(Color.FromRgb(0xF4, 0x3F, 0x5E));
        }
        else if (isPaused)
        {
            _toggleIcon.Data = PlayIconGeo;
            _toggleIcon.Fill = Brushes.White;
            _toggleIcon.Stroke = null;
            _toggleText.Text = "Fortsetzen";
            _toggleRecordingButton.Background = new SolidColorBrush(Color.FromArgb(200, 0xF5, 0x9E, 0x0B));
            _toggleRecordingButton.BorderBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
        }
        else
        {
            _toggleIcon.Data = PlayIconGeo;
            _toggleIcon.Fill = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));
            _toggleIcon.Stroke = null;
            _toggleText.Text = "Aufnahme";
            _toggleRecordingButton.Background = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255));
            _toggleRecordingButton.BorderBrush = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255));
        }

        _statusDot.Fill = isRecording
            ? new SolidColorBrush(Color.FromRgb(0xF4, 0x3F, 0x5E))
            : isPaused
                ? new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B))
                : new SolidColorBrush(Color.FromArgb(120, 255, 255, 255));

        var baseText = isRecording
            ? "Aufnahme läuft"
            : isPaused
                ? "Aufnahme pausiert"
                : "Bereit";
        _statusText.Text = (detail is null || isPaused) ? baseText : $"{baseText} · {detail}";
    }

    /// <summary>Reflects "Zoom-auf-Cursor" on/off — driven by <see cref="SessionManager.ZoomToCursorChanged"/>, whether toggled from here, the hotkey, or Settings.</summary>
    public void UpdateZoomToCursorState(bool active)
    {
        _zoomText.Text = active ? "Zoom: An" : "Zoom";
        _zoomIcon.Fill = active ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
        _zoomIcon.Stroke = active ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));

        _zoomToCursorButton.Background = active
            ? new SolidColorBrush(Color.FromArgb(200, 0x10, 0xB9, 0x81))
            : new SolidColorBrush(Color.FromArgb(24, 255, 255, 255));
        _zoomToCursorButton.BorderBrush = active
            ? new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81))
            : new SolidColorBrush(Color.FromArgb(38, 255, 255, 255));

        _zoomRadiusSlider.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
    }
}
