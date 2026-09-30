using System.Diagnostics;
using DocuClick.Services;

namespace DocuClick.Core.Tests;

/// <summary>
/// Measures a long recording (100 clicks with real UI screenshots from
/// docs/screenshots) through the writer: time per click, bytes written to
/// disk and the final file size. Runs only with
/// DOCUCLICK_BENCH=&lt;result file&gt;; the numbers are for comparing versions,
/// not a pass/fail test.
/// </summary>
public sealed class RecordingBenchmark : IDisposable
{
    private readonly TempFolder _folder = new();

    [Fact]
    public void Hundred_clicks()
    {
        var output = Environment.GetEnvironmentVariable("DOCUCLICK_BENCH");
        if (string.IsNullOrEmpty(output))
        {
            return;
        }

        var shots = Directory.GetFiles(Path.Combine(RepoRoot(), "docs", "screenshots"), "*.png").Order()
            .Select(path => new ScreenshotImage(File.ReadAllBytes(path), 1440, 900)).ToArray();
        var lines = new List<string>();
        foreach (var (mode, vault) in new[] { ("html", false), ("note", true) })
        {
            var folder = Path.Combine(_folder.Path, mode);
            Directory.CreateDirectory(folder);
            if (vault)
            {
                Directory.CreateDirectory(Path.Combine(folder, ".obsidian"));
            }

            var target = Path.Combine(folder, "Ablauf" + (vault ? ".md" : ".html"));
            var writer = new CanvasFlowWriter(new AppConfig());
            writer.StartSession(target);
            long written = 0;
            var clickTimes = new List<double>();
            var watch = new Stopwatch();
            for (var i = 0; i < 100; i++)
            {
                watch.Restart();
                writer.AddClickNode($"Linksklick auf Schritt {i + 1}", shots[i % shots.Length], DateTime.Now.AddMilliseconds(i));
                writer.GetPreview();
                watch.Stop();
                clickTimes.Add(watch.Elapsed.TotalMilliseconds);
                written += new FileInfo(target).Length;
            }

            watch.Restart();
            writer.Stop();
            var stopMs = watch.Elapsed.TotalMilliseconds;
            var attachments = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Where(f => !f.Equals(target)).Sum(f => new FileInfo(f).Length);
            lines.Add($"{mode}: first10 {clickTimes.Take(10).Average():F1} ms/click, last10 {clickTimes.TakeLast(10).Average():F1} ms/click, total {clickTimes.Sum() / 1000:F2} s, stop {stopMs:F0} ms, " +
                $"written {written / 1048576.0:F1} MB, file {new FileInfo(target).Length / 1048576.0:F2} MB, images {attachments / 1048576.0:F1} MB");
        }

        File.WriteAllLines(output, lines);
    }

    /// <summary>Size and encode time of the real screenshots per format (see AppConfig.ScreenshotFormat).</summary>
    [Fact]
    public void Screenshot_formats()
    {
        var output = Environment.GetEnvironmentVariable("DOCUCLICK_BENCH");
        if (string.IsNullOrEmpty(output))
        {
            return;
        }

        var lines = new List<string>();
        var bitmaps = Directory.GetFiles(Path.Combine(RepoRoot(), "docs", "screenshots"), "*.png").Order()
            .Select(path => SkiaSharp.SKBitmap.Decode(path)).Select(b => b.Resize(new SkiaSharp.SKImageInfo(1440, 1440 * b.Height / b.Width), new SkiaSharp.SKSamplingOptions(SkiaSharp.SKCubicResampler.Mitchell))).ToArray(); // typical window size
        foreach (var (name, format, quality) in new[] { ("PNG", SkiaSharp.SKEncodedImageFormat.Png, 100), ("WebP 85", SkiaSharp.SKEncodedImageFormat.Webp, 85), ("JPEG 85", SkiaSharp.SKEncodedImageFormat.Jpeg, 85) })
        {
            long bytes = 0;
            var watch = Stopwatch.StartNew();
            foreach (var bitmap in bitmaps)
            {
                using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
                using var data = image.Encode(format, quality);
                bytes += data.Size;
            }

            lines.Add($"{name}: {bytes / bitmaps.Length / 1024} KB/screenshot, {watch.Elapsed.TotalMilliseconds / bitmaps.Length:F0} ms/encode");
        }

        File.AppendAllLines(output, lines);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "global.json")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository-Wurzel nicht gefunden.");
    }

    public void Dispose() => _folder.Dispose();
}
