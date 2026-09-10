using System.Drawing;
using System.Windows.Forms;
using DocuClick;

namespace DocuClick.Services;

public readonly record struct CapturedWindow(Bitmap Bitmap, Rectangle Bounds);

public static class ScreenshotService
{
    private const int MinimumWindowDimension = 40;

    /// <summary>
    /// Captures only the top-level window under the click, not the whole
    /// monitor. Falls back to the full monitor if no real window could be
    /// resolved at that point (e.g. click landed on bare desktop).
    /// </summary>
    public static CapturedWindow CaptureWindowAt(Point screenPoint)
    {
        var bounds = GetWindowBoundsAt(screenPoint) ?? ScreenAt(screenPoint).Bounds;
        return CaptureRegion(bounds);
    }

    /// <summary>
    /// Allocates the bitmap and copies the screen region into it, disposing
    /// the bitmap (and its native GDI handle) again if anything after the
    /// allocation throws — CopyFromScreen can fail on a transient GDI issue
    /// (secure-desktop transition, display mode change mid-capture, ...),
    /// and without this the already-allocated bitmap would otherwise leak on
    /// every one of those failures instead of being cleaned up by the
    /// caller, which never receives it in the first place.
    /// </summary>
    private static CapturedWindow CaptureRegion(Rectangle bounds)
    {
        var bitmap = new Bitmap(bounds.Width, bounds.Height);
        try
        {
            using var g = Graphics.FromImage(bitmap);
            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            return new CapturedWindow(bitmap, bounds);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Captures a tight square crop centered on <paramref name="screenPoint"/>
    /// instead of the whole clicked window — used when "Zoom-auf-Cursor" is
    /// active, for zooming in on small UI details. Clamped to the bounds of
    /// the screen the point is on so a click near the edge doesn't try to
    /// copy from off-screen.
    /// </summary>
    public static CapturedWindow CaptureAroundPoint(Point screenPoint, int radius)
    {
        var screen = ScreenAt(screenPoint);
        var side = radius * 2;
        var bounds = new Rectangle(screenPoint.X - radius, screenPoint.Y - radius, side, side);
        bounds.Intersect(screen.Bounds);

        return CaptureRegion(bounds);
    }

    /// <summary>
    /// Captures the currently active window — used by the Enter-key trigger,
    /// which has no click point to resolve a window from.
    /// </summary>
    public static CapturedWindow CaptureForegroundWindow()
    {
        var bounds = GetForegroundWindowBounds() ?? Screen.PrimaryScreen!.Bounds;
        return CaptureRegion(bounds);
    }

    private static Rectangle? GetForegroundWindowBounds()
    {
        var hwnd = ForegroundWindowService.GetHandle();
        return GetVisibleWindowBounds(hwnd);
    }

    public static Point ToLocal(Point screenPoint, Rectangle referenceBounds) =>
        new(screenPoint.X - referenceBounds.X, screenPoint.Y - referenceBounds.Y);

    public static Rectangle ToLocal(System.Windows.Rect screenRect, Rectangle referenceBounds) => new(
        (int)screenRect.X - referenceBounds.X,
        (int)screenRect.Y - referenceBounds.Y,
        (int)screenRect.Width,
        (int)screenRect.Height);

    private static Rectangle? GetWindowBoundsAt(Point screenPoint)
    {
        var hwnd = NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = screenPoint.X, Y = screenPoint.Y });
        if (hwnd == 0)
        {
            return null;
        }

        var root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        if (root == 0)
        {
            root = hwnd;
        }

        return GetVisibleWindowBounds(root);
    }

    private static Rectangle? GetVisibleWindowBounds(nint hwnd)
    {
        if (hwnd == 0)
        {
            return null;
        }

        // On Windows 10/11, GetWindowRect includes the invisible 7-8px drop-shadow
        // and resize margins around windows. DwmGetWindowAttribute with
        // DWMWA_EXTENDED_FRAME_BOUNDS returns the actual visible window frame,
        // avoiding ugly desktop-background artifacts around captured windows.
        NativeMethods.RECT rect;
        var hr = NativeMethods.DwmGetWindowAttribute(
            hwnd,
            NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
            out var dwmRect,
            System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.RECT>());

        if (hr == 0 && dwmRect.Right > dwmRect.Left && dwmRect.Bottom > dwmRect.Top)
        {
            rect = dwmRect;
        }
        else if (!NativeMethods.GetWindowRect(hwnd, out rect))
        {
            return null;
        }

        var bounds = new Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

        // A window smaller than this is almost certainly not a real
        // top-level application window (e.g. a stray tooltip/overlay) —
        // fall back to a full-monitor capture instead of a near-blank crop.
        return bounds.Width >= MinimumWindowDimension && bounds.Height >= MinimumWindowDimension ? bounds : null;
    }

    private static Screen ScreenAt(Point screenPoint) =>
        Screen.AllScreens.FirstOrDefault(s => s.Bounds.Contains(screenPoint)) ?? Screen.PrimaryScreen!;
}
