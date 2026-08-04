using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

// UseWindowsForms implicitly brings System.Drawing into every file too;
// combined with System.Windows.Media above, Color/Brushes exist in both
// and become ambiguous. This file is WPF-only UI, so alias to those.
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;

namespace DocuClick;

/// <summary>
/// Slim bar pinned to the top edge of the primary screen, visible for the
/// entire lifetime of the app (not just while recording) so there is
/// always an at-a-glance answer to "is it running right now". Unlike the
/// recording dot / canvas-status HUD it hosts a real button ("Neue
/// Session"), so — deliberately, unlike those — it is NOT click-through.
/// </summary>
public sealed class TopBarWindow : Window
{
    private const double BarHeight = 30;

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
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = BarHeight;

        _statusText = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };

        _newSessionButton = new Button
        {
            Content = "Neue Session",
            Margin = new Thickness(0, 0, 12, 0),
            Padding = new Thickness(10, 2, 10, 2),
            IsEnabled = false,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Schließt das aktuelle Diagramm/die aktuelle Notiz ab und startet sofort eine neue Aufnahme-Session."
        };
        _newSessionButton.Click += (_, _) => NewSessionRequested?.Invoke();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_statusText, 0);
        Grid.SetColumn(_newSessionButton, 1);
        grid.Children.Add(_statusText);
        grid.Children.Add(_newSessionButton);

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(215, 30, 30, 32)),
            Child = grid
        };

        UpdateStatus(isRecording: false, detail: null);

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
