using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using DocuClick.Mac.Platform;

namespace DocuClick.Mac.UI;

/// <summary>Borderless, transparent, always-on-top window base for the HUD elements.</summary>
internal abstract class OverlayWindow : Window
{
    protected OverlayWindow(bool clickThrough, bool nonActivating)
    {
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        NativeWindow.MakeOverlay(this, clickThrough, nonActivating);
    }

    /// <summary>Places the window relative to the main screen's visible area once it's shown.</summary>
    protected void PlaceOnOpen(Func<Rect, Size, Point> topLeft)
    {
        Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            var screen = NativeWindow.MainScreenVisibleFrame();
            var position = topLeft(screen, Bounds.Size);
            NativeWindow.SetTopLeft(this, position.X, position.Y);
        }, DispatcherPriority.Loaded);
    }
}

/// <summary>
/// Short info/error message under the top bar, fading after a few seconds —
/// the macOS stand-in for the Windows tray balloon tips. Click-through, so
/// it never gets in the way of the recording.
/// </summary>
internal sealed class ToastOverlay : OverlayWindow
{
    private readonly TextBlock _text;
    private readonly Border _border;
    private readonly DispatcherTimer _hideTimer;

    public ToastOverlay() : base(clickThrough: true, nonActivating: true)
    {
        MaxWidth = 460;
        SizeToContent = SizeToContent.WidthAndHeight;
        _text = new TextBlock { Foreground = Brushes.White, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        _border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8),
            BorderBrush = Ui.HudBorder,
            BorderThickness = new Thickness(1),
            Child = _text
        };
        Content = _border;
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            Hide();
        };
    }

    public void ShowMessage(string message, bool isError)
    {
        _text.Text = message;
        _border.Background = isError
            ? new SolidColorBrush(Color.FromArgb(240, 0x9B, 0x1C, 0x2A))
            : new SolidColorBrush(Color.FromArgb(240, 11, 15, 25));
        _hideTimer.Stop();
        _hideTimer.Interval = TimeSpan.FromSeconds(isError ? 6 : 4);
        Show();
        Dispatcher.UIThread.Post(() =>
        {
            var screen = NativeWindow.MainScreenVisibleFrame();
            NativeWindow.SetTopLeft(this, screen.X + (screen.Width - Bounds.Width) / 2, screen.Y + 52);
        }, DispatcherPriority.Loaded);
        _hideTimer.Start();
    }
}

/// <summary>
/// Live preview of the Zoom-auf-Cursor capture area while its radius slider
/// is being adjusted: a click-through square that follows the mouse and
/// disappears shortly after the adjustment stops (same as on Windows).
/// </summary>
internal sealed unsafe class ZoomCursorBoxOverlay : OverlayWindow
{
    private static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(700);
    private readonly DispatcherTimer _followTimer;
    private readonly DispatcherTimer _hideTimer;
    private int _radius;

    public ZoomCursorBoxOverlay(int initialRadius) : base(clickThrough: true, nonActivating: true)
    {
        _radius = Math.Max(10, initialRadius);
        Width = Height = _radius * 2;
        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(220, 0x4C, 0xAF, 0xE8)),
            BorderThickness = new Thickness(2),
            Background = new SolidColorBrush(Color.FromArgb(20, 0x4C, 0xAF, 0xE8))
        };
        _followTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _followTimer.Tick += (_, _) => FollowCursor();
        _hideTimer = new DispatcherTimer { Interval = HideDelay };
        _hideTimer.Tick += (_, _) => Cancel();
    }

    public void Preview(int radius)
    {
        _radius = Math.Max(10, radius);
        Width = Height = _radius * 2;
        if (!IsVisible)
        {
            Show();
            _followTimer.Start();
        }

        FollowCursor();
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    public void Cancel()
    {
        _hideTimer.Stop();
        _followTimer.Stop();
        Hide();
    }

    private void FollowCursor()
    {
        var location = stackalloc double[2];
        Native.dc_mouse_location(location);
        NativeWindow.SetTopLeft(this, location[0] - _radius, location[1] - _radius);
    }
}
