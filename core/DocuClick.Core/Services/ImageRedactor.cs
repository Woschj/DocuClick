using SkiaSharp;

namespace DocuClick.Services;

/// <summary>
/// Burns blacked-out / blurred areas (<see cref="CanvasNode.Redactions"/>)
/// into a screenshot — for files that leave DocuClick (the .html Ablauf with
/// embedded images). Same result as the editor's own preview
/// (drawRedacted in WebAssets/viewer.template.html): a blurred area is
/// scaled down to blocks of about 1/120 of the image (at least 8 px) and
/// back up, so text in it can't be read.
/// </summary>
public static class ImageRedactor
{
    /// <summary>Areas worth drawing: clamped to the image, empty or invalid ones left out.</summary>
    public static List<ImageRedaction> Clean(IEnumerable<ImageRedaction>? areas)
    {
        static double Unit(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
        return (areas ?? Enumerable.Empty<ImageRedaction>())
            .Where(a => a is not null)
            .Take(500)
            .Select(a => new ImageRedaction { X = Unit(a.X), Y = Unit(a.Y), W = Unit(a.W), H = Unit(a.H), Mode = a.Mode == "blur" ? "blur" : "black" })
            .Where(a => a.W > 0 && a.H > 0)
            .ToList();
    }

    /// <summary>The image with the areas drawn in, as WebP; null if the image can't be read.</summary>
    public static byte[]? Apply(byte[] image, IReadOnlyList<ImageRedaction> areas)
    {
        using var bitmap = SKBitmap.Decode(image);
        if (bitmap is null)
        {
            return null;
        }

        using (var canvas = new SKCanvas(bitmap))
        {
            var block = Math.Max(8, (int)Math.Round(Math.Max(bitmap.Width, bitmap.Height) / 120.0));
            foreach (var area in Clean(areas))
            {
                var x = (int)Math.Round(area.X * bitmap.Width);
                var y = (int)Math.Round(area.Y * bitmap.Height);
                var w = Math.Min(bitmap.Width - x, Math.Max(1, (int)Math.Round(area.W * bitmap.Width)));
                var h = Math.Min(bitmap.Height - y, Math.Max(1, (int)Math.Round(area.H * bitmap.Height)));
                if (w <= 0 || h <= 0)
                {
                    continue;
                }

                var rect = SKRectI.Create(x, y, w, h);
                if (area.Mode == "blur")
                {
                    using var region = new SKBitmap();
                    if (!bitmap.ExtractSubset(region, rect))
                    {
                        continue;
                    }

                    var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
                    using var small = region.Resize(new SKImageInfo(Math.Max(1, (int)Math.Round(w / (double)block)), Math.Max(1, (int)Math.Round(h / (double)block))), sampling);
                    using var smallImage = SKImage.FromBitmap(small);
                    canvas.DrawImage(smallImage, SKRect.Create(small.Width, small.Height), rect, new SKSamplingOptions(SKFilterMode.Linear), null);
                }
                else
                {
                    using var paint = new SKPaint { Color = SKColors.Black };
                    canvas.DrawRect(rect, paint);
                }
            }
        }

        using var result = SKImage.FromBitmap(bitmap);
        using var encoded = result.Encode(SKEncodedImageFormat.Webp, 90);
        return encoded.ToArray();
    }
}
