using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

// UseWindowsForms implicitly brings System.Drawing/Windows.Forms into every
// file too; combined with the System.Windows(.Media) usings above, several
// names (Color, Brushes, Button, TextBox, ...) exist in both and become
// ambiguous. This file is WPF-only UI, so alias to those.
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;

namespace DocuClick;

/// <summary>
/// Small floating pill centered at the top of the primary screen — like a
/// TeamViewer session toolbar, NOT a full-width bar. It hosts a real button
/// ("Neue Session") so it can't be click-through like the other overlays,
/// which means it must stay content-sized: a full-width bar would block
/// window dragging, menu bars, and Snap zones along the entire top edge.
/// Visible for the app's whole lifetime (not just while recording), so
/// there is always an at-a-glance answer to "is it running right now".
/// </summary>
public sealed class TopBarWindow : Window
{
    internal const double BarHeight = 22;

    private readonly TextBlock _statusText;
    private readonly Button _newSessionButton;

    public event Action? NewSessionRequested;

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
        Top = bounds.Top;

        _statusText = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0)
        };

        _newSessionButton = new Button
        {
            Content = "Neue Session",
            FontSize = 11,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(8, 0, 8, 0),
            IsEnabled = false,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Schließt das aktuelle Diagramm/die aktuelle Notiz ab und startet sofort eine neue Aufnahme-Session."
        };
        _newSessionButton.Click += (_, _) => NewSessionRequested?.Invoke();

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(_statusText);
        panel.Children.Add(_newSessionButton);

        // Solid, saturated blue (TeamViewer-toolbar style) instead of the
        // previous near-black bar, which blended into dark taskbars/title
        // bars and was hard to spot at a glance.
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x5D, 0xB3)),
            CornerRadius = new CornerRadius(0, 0, 6, 6),
            Height = BarHeight,
            Child = panel
        };

        UpdateStatus(isRecording: false, detail: null);

        // Content-sized (not screen-wide), so it only ever occupies a small
        // pill at the top-center — everything outside it (window title
        // bars, menus, Snap zones) stays fully clickable, unlike the
        // previous full-width version.
        SizeChanged += (_, _) => Left = bounds.Left + (bounds.Width - ActualWidth) / 2;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.ExcludeFromScreenCapture(hwnd);
        };
    }

    public void UpdateStatus(bool isRecording, string? detail)
    {
        _newSessionButton.IsEnabled = isRecording;
        var baseText = isRecording ? "DocuClick – Aufnahme läuft" : "DocuClick – Aufnahme gestoppt";
        _statusText.Text = detail is null ? baseText : $"{baseText} · {detail}";
    }
}
