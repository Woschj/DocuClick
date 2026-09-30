namespace DocuClick.Services;

/// <summary>
/// A finished (highlighted, PNG-encoded) screenshot as handed to the
/// writers. <see cref="Scale"/> is pixels per point: 1.0 on Windows,
/// 2.0 for an unscaled Retina capture. Writers lay out images by
/// <see cref="DisplayWidth"/>/<see cref="DisplayHeight"/> so Retina captures
/// don't produce cards twice the intended size.
/// </summary>
public sealed record ScreenshotImage(byte[] Data, int PixelWidth, int PixelHeight, double Scale = 1.0, string Extension = "png")
{
    public double DisplayWidth => PixelWidth / Scale;
    public double DisplayHeight => PixelHeight / Scale;
}
