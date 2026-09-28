using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

// UseWindowsForms implicitly brings System.Drawing into every file too;
// combined with System.Windows.Media above, Color/Brushes exist in both
// and become ambiguous. This file is WPF-only UI, so alias to those.
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;

namespace DocuClick;

/// <summary>
/// Square outline that follows the cursor while the TopBar's zoom-radius
/// slider is actively being adjusted, previewing exactly the area the next
/// screenshot will crop to (see
/// <see cref="Services.ScreenshotService.CaptureAroundPoint"/> — a square
/// of side 2*radius, centered on the cursor). Click-through and excluded
/// from screen capture like every other HUD overlay, so it never ends up
/// baked into the very screenshot it's previewing.
///
/// Shown/hidden purely by <see cref="Preview"/>, called on every slider
/// tick, rather than for as long as Zoom-auf-Cursor mode itself is on —
/// staying visible for the whole time that mode is active would put a big
/// box permanently around the cursor for the rest of the recording
/// session, which is far more distracting than useful. A short idle timer
/// (not a paired mouse-down/up) drives the hide, since this session
/// already hit one real bug elsewhere from assuming a mouse-up always
/// reaches the control it started on.
/// </summary>
public sealed class ZoomCursorBoxOverlay : Window
{
    private static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(700);

    private readonly DispatcherTimer _followTimer;
    private readonly DispatcherTimer _hideTimer;
    private int _radius;

    public ZoomCursorBoxOverlay(int initialRadius)
    {
        _radius = Math.Max(10, initialRadius);

        Width = _radius * 2;
        Height = _radius * 2;
        OverlayHelper.ConfigureAsOverlay(this);

        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(220, 0x4C, 0xAF, 0xE8)),
            BorderThickness = new Thickness(2),
            Background = new SolidColorBrush(Color.FromArgb(20, 0x4C, 0xAF, 0xE8))
        };

        // Polls the cursor position instead of a global mouse-move hook —
        // MouseHookService only ever reports clicks (see its own doc
        // comment on why: handlers must return fast, and a hook firing on
        // every pixel of movement is exactly the kind of thing that gets a
        // low-level hook silently unhooked by Windows for being too slow).
        // DispatcherPriority.Render keeps this in step with the compositor
        // instead of fighting it for a slot on the normal Dispatcher queue.
        _followTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _followTimer.Tick += (_, _) => FollowCursor();

        _hideTimer = new DispatcherTimer { Interval = HideDelay };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            _followTimer.Stop();
            Hide();
        };
    }

    /// <summary>Shows the box (if not already) at the given radius and resets the auto-hide countdown — call on every live change from the TopBar's slider.</summary>
    public void Preview(int radius)
    {
        _radius = Math.Max(10, radius);
        Width = _radius * 2;
        Height = _radius * 2;
        FollowCursor();

        if (!IsVisible)
        {
            Show();
            _followTimer.Start();
        }

        _hideTimer.Stop();
        _hideTimer.Start();
    }

    /// <summary>Hides immediately — e.g. when Zoom-auf-Cursor mode itself is switched off mid-preview.</summary>
    public void Cancel()
    {
        _hideTimer.Stop();
        _followTimer.Stop();
        Hide();
    }

    private void FollowCursor()
    {
        var pos = System.Windows.Forms.Cursor.Position;
        Left = pos.X - _radius;
        Top = pos.Y - _radius;
    }
}
