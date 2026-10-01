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

/// <summary>
/// A stand-in for an app's UI thread: one thread with its own
/// SynchronizationContext that runs posted work in order (like Avalonia's or
/// WPF's dispatcher). Background thread, never joined — a test that
/// deadlocks it must not hang the test run.
/// </summary>
internal sealed class SingleThreadContext : SynchronizationContext, IDisposable
{
    private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

    public SingleThreadContext()
    {
        var thread = new Thread(() =>
        {
            SetSynchronizationContext(this);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            {
                callback(state);
            }
        }) { IsBackground = true, Name = "Test-UI" };
        thread.Start();
    }

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

    public Task<T> InvokeAsync<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(_ =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }, null);
        return tcs.Task;
    }

    public void Dispose() => _queue.CompleteAdding();
}
