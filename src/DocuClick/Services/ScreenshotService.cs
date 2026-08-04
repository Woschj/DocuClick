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

        var bitmap = new Bitmap(bounds.Width, bounds.Height);
        using var g = Graphics.FromImage(bitmap);
        g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        return new CapturedWindow(bitmap, bounds);
    }

    /// <summary>
    /// Captures the currently active window — used by the Enter-key trigger,
    /// which has no click point to resolve a window from.
    /// </summary>
    public static CapturedWindow CaptureForegroundWindow()
    {
        var bounds = GetForegroundWindowBounds() ?? Screen.PrimaryScreen!.Bounds;

        var bitmap = new Bitmap(bounds.Width, bounds.Height);
        using var g = Graphics.FromImage(bitmap);
        g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        return new CapturedWindow(bitmap, bounds);
    }

    private static Rectangle? GetForegroundWindowBounds()
    {
        var hwnd = ForegroundWindowService.GetHandle();
        if (hwnd == 0)
        {
            return null;
        }

        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return null;
        }

        var bounds = new Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        return bounds.Width >= MinimumWindowDimension && bounds.Height >= MinimumWindowDimension ? bounds : null;
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

        if (!NativeMethods.GetWindowRect(root, out var rect))
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
