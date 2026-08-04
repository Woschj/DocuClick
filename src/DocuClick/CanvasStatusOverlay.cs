using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

// UseWindowsForms implicitly brings System.Drawing into every file too;
// combined with System.Windows.Media above, Color/Brushes exist in both
// and become ambiguous. This file is WPF-only UI, so alias to those.
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;

namespace DocuClick;

/// <summary>
/// Small HUD panel under the recording dot, showing which canvas node/branch
/// the next click will connect from — so it's always visible where "you
/// currently are" in the flow without having to open the .canvas file.
/// </summary>
public sealed class CanvasStatusOverlay : Window
{
    private const double PanelWidth = 280;
    private const double EdgeMargin = 8;
    private const double TopOffset = 14 + EdgeMargin + 6; // below the recording dot

    private readonly TextBlock _textBlock;

    public CanvasStatusOverlay()
    {
        Width = PanelWidth;
        SizeToContent = SizeToContent.Height;
        OverlayHelper.ConfigureAsOverlay(this);

        _textBlock = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8, 6, 8, 6)
        };

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(200, 30, 30, 32)),
            CornerRadius = new CornerRadius(6),
            Child = _textBlock
        };

        var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        Left = bounds.Left + EdgeMargin;
        Top = bounds.Top + TopOffset;
    }

    public void UpdateText(string text) => _textBlock.Text = text;
}
