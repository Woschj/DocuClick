using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DocuClick.Platform;
using SkiaSharp;

namespace DocuClick.Mac.Platform;

/// <summary>Wires the macOS implementations (all backed by libDocuClickMac) into the shared core.</summary>
internal static class MacPlatform
{
    public static PlatformServices Create(AppConfig config) => new(
        new MacInputMonitor(),
        new MacScreenCapture(config),
        new MacElementInspector(),
        new MacForegroundWindow(),
        new MacFeedbackSounds());
}

/// <summary>Listen-only CGEventTap (native). Events arrive on the tap's own thread.</summary>
internal sealed unsafe class MacInputMonitor : IInputMonitor
{
    // CGEventFlags
    private const ulong ShiftMask = 0x20000, ControlMask = 0x40000, AlternateMask = 0x80000, CommandMask = 0x100000;

    private static MacInputMonitor? _active;

    public event EventHandler<MouseClickEventArgs>? LeftButtonDown;
    public event EventHandler<MouseClickEventArgs>? RightButtonDown;
    public event EventHandler<EnterKeyEventArgs>? EnterPressed;

    public void Start(bool captureEnter)
    {
        _active = this;
        if (Native.dc_input_start(&OnNativeInput, captureEnter ? 1 : 0) == 0)
        {
            throw new InvalidOperationException(Native.LastError());
        }
    }

    public void Stop()
    {
        Native.dc_input_stop();
        _active = null;
    }

    public ModifierState CurrentModifiers
    {
        get
        {
            var flags = Native.dc_modifier_flags();
            return new ModifierState((flags & ShiftMask) != 0, (flags & ControlMask) != 0, (flags & AlternateMask) != 0, (flags & CommandMask) != 0);
        }
    }

    public void Dispose() => Stop();

    [UnmanagedCallersOnly]
    private static void OnNativeInput(int kind, double x, double y, ulong flags)
    {
        var monitor = _active;
        if (monitor is null)
        {
            return;
        }

        try
        {
            if (kind == 2)
            {
                monitor.EnterPressed?.Invoke(monitor, new EnterKeyEventArgs
                {
                    Timestamp = DateTime.Now,
                    ShiftDown = (flags & ShiftMask) != 0,
                    ControlDown = (flags & ControlMask) != 0,
                    AltDown = (flags & AlternateMask) != 0,
                    CommandDown = (flags & CommandMask) != 0
                });
                return;
            }

            var args = new MouseClickEventArgs
            {
                Point = new ScreenPoint(x, y),
                Timestamp = DateTime.Now,
                ShiftDown = (flags & ShiftMask) != 0,
                ControlDown = (flags & ControlMask) != 0,
                AltDown = (flags & AlternateMask) != 0,
                CommandDown = (flags & CommandMask) != 0
            };
            (kind == 1 ? monitor.RightButtonDown : monitor.LeftButtonDown)?.Invoke(monitor, args);
        }
        catch (Exception ex)
        {
            // An exception must never unwind into native code.
            Services.LogService.Log($"Fehler im Eingabe-Callback: {ex}");
        }
    }
}

internal sealed class MacPreClickFrame(nint handle) : PreClickFrame
{
    private nint _handle = handle;

    public nint Handle => _handle;

    public override void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0)
        {
            Native.dc_preclick_release(handle);
        }
    }
}

/// <summary>ScreenCaptureKit captures (native), copied into SKBitmaps for the shared pipeline.</summary>
internal sealed class MacScreenCapture(AppConfig config) : IScreenCapture
{
    public void BeginSession() => Native.dc_capture_session_begin(config.CaptureTiming == "BeforeClick" ? 1 : 0);

    public void EndSession() => Native.dc_capture_session_end();

    public PreClickFrame? GrabPreClickFrame()
    {
        var handle = Native.dc_preclick_grab();
        return handle == 0 ? null : new MacPreClickFrame(handle);
    }

    public CapturedFrame CaptureWindowAt(ScreenPoint point, PreClickFrame? preClick) =>
        Take(Native.dc_capture_window_at(point.X, point.Y, HandleOf(preClick)));

    public CapturedFrame CaptureAroundPoint(ScreenPoint point, int radius, PreClickFrame? preClick) =>
        Take(Native.dc_capture_around(point.X, point.Y, radius, HandleOf(preClick)));

    public CapturedFrame CaptureForegroundWindow(PreClickFrame? preClick) =>
        Take(Native.dc_capture_frontmost(HandleOf(preClick)));

    private static nint HandleOf(PreClickFrame? frame) => (frame as MacPreClickFrame)?.Handle ?? 0;

    private static CapturedFrame Take(nint frame)
    {
        if (frame == 0)
        {
            throw new InvalidOperationException("Screenshot fehlgeschlagen: " + Native.LastError());
        }

        try
        {
            var width = Native.dc_frame_width(frame);
            var height = Native.dc_frame_height(frame);
            var sourceRowBytes = Native.dc_frame_bytes_per_row(frame);
            var source = Native.dc_frame_pixels(frame);

            var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            var destination = bitmap.GetPixels();
            var rowBytes = width * 4;
            unsafe
            {
                for (var y = 0; y < height; y++)
                {
                    Buffer.MemoryCopy(
                        (void*)(source + y * sourceRowBytes),
                        (void*)(destination + y * bitmap.RowBytes),
                        bitmap.RowBytes, rowBytes);
                }
            }

            return new CapturedFrame(
                bitmap,
                new ScreenRect(Native.dc_frame_x(frame), Native.dc_frame_y(frame), Native.dc_frame_w(frame), Native.dc_frame_h(frame)),
                Native.dc_frame_scale(frame));
        }
        finally
        {
            Native.dc_frame_free(frame);
        }
    }
}

internal sealed unsafe class MacElementInspector : IElementInspector
{
    public ElementInfo? GetElementAt(ScreenPoint point) => Take(Native.dc_element_at(point.X, point.Y));

    public ElementInfo? GetFocusedElement() => Take(Native.dc_element_focused());

    private static ElementInfo? Take(nint element)
    {
        if (element == 0)
        {
            return null;
        }

        try
        {
            var frame = stackalloc double[4];
            ScreenRect? bounds = Native.dc_element_frame(element, frame) != 0
                ? new ScreenRect(frame[0], frame[1], frame[2], frame[3])
                : null;
            return new ElementInfo(
                Native.Borrowed(Native.dc_element_name(element)),
                Native.Borrowed(Native.dc_element_type(element)),
                Native.Borrowed(Native.dc_element_window_title(element)),
                bounds,
                IsPassword: Native.dc_element_is_password(element) == 1);
        }
        finally
        {
            Native.dc_element_free(element);
        }
    }
}

internal sealed class MacForegroundWindow : IForegroundWindow
{
    public string? GetTitle() => Native.Take(Native.dc_frontmost_window_title());
}

internal sealed class MacFeedbackSounds : IFeedbackSounds
{
    public void PlayCaptured() => Native.dc_play_sound(0);
    public void PlaySkipped() => Native.dc_play_sound(1);
    public void PlayError() => Native.dc_play_sound(2);
}
