using SkiaSharp;

namespace DocuClick.Services;

/// <summary>
/// Draws the click marker directly onto the captured screenshot bitmap.
/// All coordinates and sizes are in bitmap pixels — callers convert from
/// screen units via the capture's scale first.
/// </summary>
public static class HighlightRenderer
{
    public static void DrawClickCircle(SKBitmap bitmap, SKPoint center, SKColor color, float radius, float thickness)
    {
        using var canvas = new SKCanvas(bitmap);
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color.WithAlpha(60) };
        canvas.DrawCircle(center, radius, fill);

        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = color, StrokeWidth = thickness };
        canvas.DrawCircle(center, radius, stroke);
    }

    public static void DrawBoundingBox(SKBitmap bitmap, SKRect rect, SKColor color, float thickness)
    {
        using var canvas = new SKCanvas(bitmap);
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color.WithAlpha(30) };
        canvas.DrawRect(rect, fill);

        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = color, StrokeWidth = thickness };
        canvas.DrawRect(rect, stroke);
    }
}
