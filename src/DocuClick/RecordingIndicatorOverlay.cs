using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DocuClick;

/// <summary>
/// Small red dot pinned to the top-left corner of the primary screen while
/// recording is active — the actual capture is invisible on purpose, so
/// without some persistent on-screen cue there's no way to tell at a glance
/// whether DocuClick is currently watching for clicks.
/// </summary>
public sealed class RecordingIndicatorOverlay : Window
{
    private const double Size = 14;
    private const double Margin = 8;

    public RecordingIndicatorOverlay()
    {
        Width = Size;
        Height = Size;
        OverlayHelper.ConfigureAsOverlay(this);

        Content = new Ellipse
        {
            Width = Size,
            Height = Size,
            Fill = new SolidColorBrush(Color.FromRgb(230, 57, 70)),
            Stroke = Brushes.White,
            StrokeThickness = 1.5
        };

        var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        Left = bounds.Left + Margin;
        Top = bounds.Top + Margin;
    }
}
