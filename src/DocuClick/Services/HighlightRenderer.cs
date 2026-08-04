using System.Drawing;
using System.Drawing.Drawing2D;

namespace DocuClick.Services;

/// <summary>Draws the click marker directly onto the captured screenshot bitmap.</summary>
public static class HighlightRenderer
{
    public static void DrawClickCircle(Bitmap bitmap, Point localPoint, Color color, int radius, int thickness)
    {
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var fillBrush = new SolidBrush(Color.FromArgb(60, color));
        g.FillEllipse(fillBrush, localPoint.X - radius, localPoint.Y - radius, radius * 2, radius * 2);

        using var pen = new Pen(color, thickness);
        g.DrawEllipse(pen, localPoint.X - radius, localPoint.Y - radius, radius * 2, radius * 2);
    }

    public static void DrawBoundingBox(Bitmap bitmap, Rectangle localRect, Color color, int thickness)
    {
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var fillBrush = new SolidBrush(Color.FromArgb(30, color));
        g.FillRectangle(fillBrush, localRect);

        using var pen = new Pen(color, thickness);
        g.DrawRectangle(pen, localRect);
    }
}
