using DocuClick.Services;
using SkiaSharp;

namespace DocuClick.Core.Tests;

/// <summary>A throwaway session folder, deleted again after the test.</summary>
public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "docuclick-tests-" + Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

public static class TestImages
{
    public static ScreenshotImage Png(int width, int height, double scale = 1.0)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.White);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return new ScreenshotImage(data.ToArray(), width, height, scale);
    }
}
