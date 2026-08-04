using System.Drawing;
using System.Windows.Forms;

namespace DocuClick.Services;

public static class ScreenshotService
{
    public static Bitmap CaptureMonitorAt(Point screenPoint)
    {
        var screen = ScreenAt(screenPoint);
        var bounds = screen.Bounds;

        var bitmap = new Bitmap(bounds.Width, bounds.Height);
        using var g = Graphics.FromImage(bitmap);
        g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        return bitmap;
    }

    public static Point ToMonitorLocal(Point screenPoint)
    {
        var bounds = ScreenAt(screenPoint).Bounds;
        return new Point(screenPoint.X - bounds.X, screenPoint.Y - bounds.Y);
    }

    public static Rectangle ToMonitorLocal(System.Windows.Rect screenRect, Point referencePoint)
    {
        var bounds = ScreenAt(referencePoint).Bounds;
        return new Rectangle(
            (int)screenRect.X - bounds.X,
            (int)screenRect.Y - bounds.Y,
            (int)screenRect.Width,
            (int)screenRect.Height);
    }

    private static Screen ScreenAt(Point screenPoint) =>
        Screen.AllScreens.FirstOrDefault(s => s.Bounds.Contains(screenPoint)) ?? Screen.PrimaryScreen!;
}
