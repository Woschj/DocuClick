using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

// UseWindowsForms implicitly brings System.Drawing into every file too;
// combined with System.Windows.Media above, Color/Brushes exist in both
// and become ambiguous. This file is WPF-only UI, so alias to those.
using Brushes = System.Windows.Media.Brushes;

namespace DocuClick;

/// <summary>Shared setup for tiny, click-through, always-on-top HUD windows.</summary>
internal static class OverlayHelper
{
    internal static void ConfigureAsOverlay(Window window)
    {
        window.WindowStyle = WindowStyle.None;
        window.AllowsTransparency = true;
        window.Background = Brushes.Transparent;
        window.ShowInTaskbar = false;
        window.Topmost = true;
        window.ResizeMode = ResizeMode.NoResize;
        window.ShowActivated = false;
        window.Focusable = false;
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            NativeMethods.MakeClickThrough(hwnd);
            NativeMethods.ExcludeFromScreenCapture(hwnd);
        };
    }
}
