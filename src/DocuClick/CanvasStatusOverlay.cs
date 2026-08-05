using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// UseWindowsForms implicitly brings System.Drawing into every file too;
// combined with System.Windows.Media above, Color/Brushes exist in both
// and become ambiguous. This file is WPF-only UI, so alias to those.
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Image = System.Windows.Controls.Image;

namespace DocuClick;

/// <summary>
/// Small HUD panel under the recording dot, showing which canvas node/branch
/// the next click will connect from — so it's always visible where "you
/// currently are" in the flow without having to open the .canvas file. Also
/// shows a small thumbnail of the most recently captured screenshot, so a
/// bad capture (wrong window, missed highlight, ...) is obvious immediately
/// instead of only on the next look at the actual file.
/// </summary>
public sealed class CanvasStatusOverlay : Window
{
    private const double PanelWidth = 280;
    private const double ThumbnailHeight = 130;
    private const double EdgeMargin = 8;
    private const double TopOffset = RecordingIndicatorOverlay.TopBarClearance + 14 + EdgeMargin + 6; // below the top bar and the recording dot

    private readonly TextBlock _textBlock;
    private readonly Image _thumbnailImage;

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

        _thumbnailImage = new Image
        {
            Height = ThumbnailHeight,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(8, 0, 8, 8),
            Visibility = Visibility.Collapsed
        };

        var panel = new StackPanel();
        panel.Children.Add(_textBlock);
        panel.Children.Add(_thumbnailImage);

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(200, 30, 30, 32)),
            CornerRadius = new CornerRadius(6),
            Child = panel
        };

        var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        Left = bounds.Left + EdgeMargin;
        Top = bounds.Top + TopOffset;
    }

    public void UpdateText(string text) => _textBlock.Text = text;

    /// <summary>Shows a thumbnail decoded from PNG-encoded bytes of the last captured screenshot.</summary>
    public void UpdateThumbnail(byte[] pngBytes)
    {
        var bitmap = new BitmapImage();
        using (var stream = new MemoryStream(pngBytes))
        {
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
        }
        bitmap.Freeze();

        _thumbnailImage.Source = bitmap;
        _thumbnailImage.Visibility = Visibility.Visible;
    }
}
