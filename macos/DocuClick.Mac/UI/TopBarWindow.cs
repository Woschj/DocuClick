using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Path = Avalonia.Controls.Shapes.Path;

namespace DocuClick.Mac.UI;

/// <summary>
/// Floating, draggable pill at the top of the main screen with the recording
/// controls — the macOS counterpart of the Windows TopBarWindow (same
/// buttons, icons and states). Non-activating: clicking it never takes
/// focus from the app being recorded (verified in the feasibility test).
/// </summary>
internal sealed class TopBarWindow : OverlayWindow
{
    private const double BarHeight = 34;
    private const int ZoomRadiusMin = 50;
    private const int ZoomRadiusMax = 600;

    private static readonly Geometry PlayIcon = Geometry.Parse("M 3 2.5 L 12.5 8 L 3 13.5 Z");
    private static readonly Geometry StopIcon = Geometry.Parse("M 3 3 H 13 V 13 H 3 Z");
    private static readonly Geometry FlowIcon = Geometry.Parse("M 2 8 H 5.5 M 5.5 8 L 9.5 4 M 5.5 8 L 9.5 12 M 9.5 4 H 13.5 M 9.5 12 H 13.5");
    private static readonly Geometry PlusIcon = Geometry.Parse("M 8 2.5 V 13.5 M 2.5 8 H 13.5");
    private static readonly Geometry ZoomIcon = Geometry.Parse("M 6.5 2 A 4.5 4.5 0 1 1 2 6.5 A 4.5 4.5 0 0 1 6.5 2 M 10 10 L 14 14");
    private static readonly Geometry FolderIcon = Geometry.Parse("M 2 4.5 H 6.5 L 8 6 H 14 V 13 H 2 Z");
    private static readonly Geometry GearIcon = Geometry.Parse("M 8 5.5 A 2.5 2.5 0 1 1 5.5 8 A 2.5 2.5 0 0 1 8 5.5 M 8 1.5 V 3.5 M 8 12.5 V 14.5 M 1.5 8 H 3.5 M 12.5 8 H 14.5 M 3.4 3.4 L 4.8 4.8 M 11.2 11.2 L 12.6 12.6 M 3.4 12.6 L 4.8 11.2 M 11.2 4.8 L 12.6 3.4");

    private readonly Ellipse _statusDot;
    private readonly TextBlock _statusText;
    private readonly Border _skipBadge;
    private readonly TextBlock _skipBadgeText;
    private readonly Button _toggleButton;
    private readonly Path _toggleIcon;
    private readonly TextBlock _toggleText;
    private readonly Button _zoomButton;
    private readonly Path _zoomIcon;
    private readonly TextBlock _zoomText;
    private readonly Slider _zoomSlider;

    public event Action? ToggleRecordingRequested;
    public event Action? ShowFlowPreviewRequested;
    public event Action? NewSessionRequested;
    public event Action? OpenOutputFolderRequested;
    public event Action? ZoomToCursorToggleRequested;
    public event Action? SettingsRequested;
    /// <summary>Live while the zoom slider moves (radius in points) — not saved yet, see <see cref="ZoomRadiusCommitted"/>.</summary>
    public event Action<int>? ZoomRadiusChanged;
    public event Action? ZoomRadiusCommitted;

    public TopBarWindow(int initialZoomRadius) : base(clickThrough: false, nonActivating: true)
    {
        SizeToContent = SizeToContent.WidthAndHeight;

        _statusDot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        _statusText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#F1F5F9")), FontSize = 12, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        var statusChip = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(13),
            Padding = new Thickness(8, 3, 10, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _statusDot, _statusText } }
        };

        _skipBadgeText = new TextBlock { Foreground = new SolidColorBrush(Color.Parse("#0B0F19")), FontSize = 12, FontWeight = FontWeight.Bold };
        _skipBadge = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F59E0B")), CornerRadius = new CornerRadius(13),
            Padding = new Thickness(9, 3), VerticalAlignment = VerticalAlignment.Center, IsVisible = false, Child = _skipBadgeText
        };

        _toggleButton = IconButton(PlayIcon, "Aufnahme", "Aufnahme starten/stoppen (auch per Tastenkürzel, siehe Einstellungen).", out _toggleIcon, out _toggleText);
        _toggleButton.Click += (_, _) => ToggleRecordingRequested?.Invoke();
        var flowButton = IconButton(FlowIcon, "Ablauf", "Öffnet die Ablauf-Übersicht. Knoten lassen sich dort anklicken, umbenennen, verschieben, verbinden und verzweigen.", out _, out _);
        flowButton.Click += (_, _) => ShowFlowPreviewRequested?.Invoke();
        var newButton = IconButton(PlusIcon, "Neu", "Startet eine neue Aufnahme-Session (fragt nach Zieldatei) — schließt bei laufender Aufnahme zuerst die aktuelle Datei ab.", out _, out _);
        newButton.Click += (_, _) => NewSessionRequested?.Invoke();
        var folderButton = IconButton(FolderIcon, "Ordner", "Öffnet den Ordner der aktuellen Session im Finder.", out _, out _);
        folderButton.Click += (_, _) => OpenOutputFolderRequested?.Invoke();
        _zoomButton = IconButton(ZoomIcon, "Zoom", "Zoom-auf-Cursor umschalten: die nächsten Screenshots erfassen nur den Bereich um den Mauszeiger.", out _zoomIcon, out _zoomText);
        _zoomButton.Click += (_, _) => ZoomToCursorToggleRequested?.Invoke();
        var settingsButton = IconButton(GearIcon, "", "Einstellungen", out _, out _);
        settingsButton.Click += (_, _) => SettingsRequested?.Invoke();

        _zoomSlider = new Slider
        {
            Minimum = ZoomRadiusMin, Maximum = ZoomRadiusMax, Width = 80,
            Value = Math.Clamp(initialZoomRadius, ZoomRadiusMin, ZoomRadiusMax),
            VerticalAlignment = VerticalAlignment.Center, IsVisible = false
        };
        ToolTip.SetTip(_zoomSlider, "Größe des Zoom-auf-Cursor-Bereichs");
        _zoomSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty)
            {
                var radius = (int)_zoomSlider.Value;
                ToolTip.SetTip(_zoomSlider, $"Zoom-Bereich: {radius * 2}×{radius * 2} Punkte");
                ZoomRadiusChanged?.Invoke(radius);
            }
        };
        _zoomSlider.AddHandler(PointerReleasedEvent, (_, _) => ZoomRadiusCommitted?.Invoke(), Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        _zoomSlider.KeyUp += (_, _) => ZoomRadiusCommitted?.Invoke();

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 6, 0),
            Children =
            {
                DragGrip(), statusChip, _skipBadge, Separator(),
                _toggleButton, flowButton, Separator(),
                newButton, folderButton, Separator(),
                _zoomButton, _zoomSlider, settingsButton
            }
        };

        var pill = new Border
        {
            Height = BarHeight,
            CornerRadius = new CornerRadius(BarHeight / 2),
            Background = new SolidColorBrush(Color.FromArgb(242, 11, 15, 25)),
            // A clearly visible light outline and shadow: the dark pill has to
            // stand out on dark wallpapers and in Dark Mode too (the first
            // Mac prototype's bar was hard to see).
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 4 22 0 #6B000000"),
            Margin = new Thickness(12, 4, 12, 20), // room for the shadow
            Child = row
        };
        Content = pill;

        pill.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && !IsInsideInteractive(e.Source))
            {
                BeginMoveDrag(e);
            }
        };

        UpdateStatus(isRecording: false, isPaused: false);
        UpdateZoomState(active: false);
        PlaceOnOpen((screen, size) => new Point(screen.X + (screen.Width - size.Width) / 2, screen.Y + 4));
    }

    public void UpdateStatus(bool isRecording, bool isPaused)
    {
        if (isRecording)
        {
            SetToggle(StopIcon, "Stopp", Brushes.White, Color.FromArgb(220, 0xF4, 0x3F, 0x5E), Color.Parse("#F43F5E"));
        }
        else if (isPaused)
        {
            SetToggle(PlayIcon, "Fortsetzen", Brushes.White, Color.FromArgb(200, 0xF5, 0x9E, 0x0B), Color.Parse("#F59E0B"));
        }
        else
        {
            SetToggle(PlayIcon, "Aufnahme", new SolidColorBrush(Color.Parse("#38BDF8")), Color.FromArgb(24, 255, 255, 255), Color.FromArgb(38, 255, 255, 255));
        }

        _statusDot.Fill = isRecording ? new SolidColorBrush(Color.Parse("#F43F5E"))
            : isPaused ? new SolidColorBrush(Color.Parse("#F59E0B"))
            : new SolidColorBrush(Color.FromArgb(120, 255, 255, 255));
        _statusText.Text = isRecording ? "Aufnahme läuft" : isPaused ? "Aufnahme pausiert" : "Bereit";
    }

    private void SetToggle(Geometry icon, string text, IBrush iconBrush, Color background, Color border)
    {
        _toggleIcon.Data = icon;
        _toggleIcon.Fill = iconBrush;
        _toggleText.Text = text;
        _toggleButton.Background = new SolidColorBrush(background);
        _toggleButton.BorderBrush = new SolidColorBrush(border);
    }

    public void UpdateZoomState(bool active)
    {
        _zoomText.Text = active ? "Zoom: An" : "Zoom";
        IBrush iconBrush = active ? Brushes.White : new SolidColorBrush(Color.Parse("#94A3B8"));
        _zoomIcon.Stroke = iconBrush;
        _zoomButton.Background = new SolidColorBrush(active ? Color.FromArgb(200, 0x10, 0xB9, 0x81) : Color.FromArgb(24, 255, 255, 255));
        _zoomButton.BorderBrush = new SolidColorBrush(active ? Color.Parse("#10B981") : Color.FromArgb(38, 255, 255, 255));
        _zoomSlider.IsVisible = active;
    }

    public void UpdateSkipModifierActive(bool active, string modifierLabel)
    {
        if (active)
        {
            _skipBadgeText.Text = $"⏸ Skip ({modifierLabel})";
        }

        _skipBadge.IsVisible = active;
    }

    private static bool IsInsideInteractive(object? source)
    {
        for (var element = source as StyledElement; element is not null; element = element.Parent)
        {
            if (element is Button or Slider)
            {
                return true;
            }
        }

        return false;
    }

    private static Control DragGrip()
    {
        var grid = new Canvas { Width = 8, Height = 16, Margin = new Thickness(6, 0, 2, 0), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.SizeAll) };
        ToolTip.SetTip(grid, "Leiste verschieben");
        for (var row = 0; row < 3; row++)
        {
            for (var col = 0; col < 2; col++)
            {
                var dot = new Ellipse { Width = 2, Height = 2, Fill = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)) };
                Canvas.SetLeft(dot, col * 4.5);
                Canvas.SetTop(dot, row * 5.5 + 1.5);
                grid.Children.Add(dot);
            }
        }

        return grid;
    }

    private static Border Separator() => new()
    {
        Width = 1, Height = 16, Margin = new Thickness(3, 0), VerticalAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255))
    };

    /// <summary>Icon + label pill button, translucent white on the dark bar, with hover/pressed states.</summary>
    private static Button IconButton(Geometry icon, string label, string tooltip, out Path iconPath, out TextBlock labelBlock)
    {
        var isFilled = icon == PlayIcon || icon == StopIcon;
        iconPath = new Path
        {
            Data = icon, Width = 13, Height = 13, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center,
            Fill = isFilled ? new SolidColorBrush(Color.Parse("#38BDF8")) : null,
            Stroke = isFilled ? null : new SolidColorBrush(Color.Parse("#CBD5E1")),
            StrokeThickness = 1.5, StrokeLineCap = PenLineCap.Round
        };
        labelBlock = new TextBlock
        {
            Text = label, FontSize = 12, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(label.Length > 0 ? 6 : 0, 0, 0, 0)
        };

        var button = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { iconPath, labelBlock } },
            Background = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(13),
            Padding = new Thickness(9, 3),
            MinHeight = 24,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(button, tooltip);

        // Keep the look in Fluent's pointer-over/pressed/disabled states.
        void State(string pseudoClass, Color background)
        {
            button.Styles.Add(new Style(x => x.OfType<Button>().Class(pseudoClass).Template().OfType<Avalonia.Controls.Presenters.ContentPresenter>())
            {
                Setters =
                {
                    new Setter(Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty, new SolidColorBrush(background)),
                    new Setter(Avalonia.Controls.Presenters.ContentPresenter.ForegroundProperty, Brushes.White)
                }
            });
        }

        State(":pointerover", Color.FromArgb(60, 255, 255, 255));
        State(":pressed", Color.FromArgb(15, 255, 255, 255));
        return button;
    }
}
