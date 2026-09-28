using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DocuClick.Services;
using SkiaSharp;

namespace DocuClick.Platform;

/// <summary>Wires the Windows implementations (hooks, GDI capture, UI Automation) into the shared core.</summary>
public static class WindowsPlatform
{
    public static PlatformServices Create() => new(
        new WindowsInputMonitor(),
        new WindowsScreenCapture(),
        new WindowsElementInspector(),
        new WindowsForegroundWindow(),
        new WindowsFeedbackSounds());
}

/// <summary>WH_MOUSE_LL + WH_KEYBOARD_LL hooks. Must be started on a thread that pumps messages (the WPF UI thread).</summary>
internal sealed class WindowsInputMonitor : IInputMonitor
{
    private readonly MouseHookService _mouseHook = new();
    private readonly KeyboardHookService _keyboardHook = new();

    public event EventHandler<MouseClickEventArgs>? LeftButtonDown
    {
        add => _mouseHook.LeftButtonDown += value;
        remove => _mouseHook.LeftButtonDown -= value;
    }

    public event EventHandler<MouseClickEventArgs>? RightButtonDown
    {
        add => _mouseHook.RightButtonDown += value;
        remove => _mouseHook.RightButtonDown -= value;
    }

    public event EventHandler<EnterKeyEventArgs>? EnterPressed
    {
        add => _keyboardHook.EnterPressed += value;
        remove => _keyboardHook.EnterPressed -= value;
    }

    public void Start(bool captureEnter)
    {
        _mouseHook.Start();
        if (captureEnter)
        {
            _keyboardHook.Start();
        }
    }

    public void Stop()
    {
        _mouseHook.Stop();
        _keyboardHook.Stop();
    }

    public ModifierState CurrentModifiers => new(ModifierKeyState.ShiftDown, ModifierKeyState.ControlDown, ModifierKeyState.AltDown);

    public void Dispose()
    {
        _mouseHook.Dispose();
        _keyboardHook.Dispose();
    }
}

/// <summary>GDI screen copy (<see cref="ScreenshotService"/>), converted to an SKBitmap for the shared pipeline. Screen units are physical pixels, so the scale is always 1.</summary>
internal sealed class WindowsScreenCapture : IScreenCapture
{
    public CapturedFrame CaptureWindowAt(ScreenPoint point, PreClickFrame? preClick) =>
        Convert(ScreenshotService.CaptureWindowAt(ToDrawing(point)));

    public CapturedFrame CaptureAroundPoint(ScreenPoint point, int radius, PreClickFrame? preClick) =>
        Convert(ScreenshotService.CaptureAroundPoint(ToDrawing(point), radius));

    public CapturedFrame CaptureForegroundWindow(PreClickFrame? preClick) =>
        Convert(ScreenshotService.CaptureForegroundWindow());

    private static System.Drawing.Point ToDrawing(ScreenPoint point) => new((int)point.X, (int)point.Y);

    private static CapturedFrame Convert(CapturedWindow captured)
    {
        using var bitmap = captured.Bitmap;
        var bounds = captured.Bounds;
        return new CapturedFrame(
            ToSkBitmap(bitmap),
            new ScreenRect(bounds.X, bounds.Y, bounds.Width, bounds.Height),
            Scale: 1.0);
    }

    private static SKBitmap ToSkBitmap(Bitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var result = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            // GDI's 32bpp (P)ARGB is B,G,R,A in memory — the same byte order
            // as Skia's Bgra8888, so rows copy over 1:1 (strides may differ).
            var rowBytes = width * 4;
            var row = new byte[rowBytes];
            var destination = result.GetPixels();
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, rowBytes);
                Marshal.Copy(row, 0, destination + y * result.RowBytes, rowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return result;
    }
}

internal sealed class WindowsElementInspector : IElementInspector
{
    public ElementInfo? GetElementAt(ScreenPoint point) => UiAutomationService.GetElementAt(point);
    public ElementInfo? GetFocusedElement() => UiAutomationService.GetFocusedElement();
}

internal sealed class WindowsForegroundWindow : IForegroundWindow
{
    public string? GetTitle() => ForegroundWindowService.GetTitle();
}

internal sealed class WindowsFeedbackSounds : IFeedbackSounds
{
    public void PlayCaptured() => ClickFeedbackService.PlayCaptured();
    public void PlaySkipped() => ClickFeedbackService.PlaySkipped();
    public void PlayError() => ClickFeedbackService.PlayError();
}
