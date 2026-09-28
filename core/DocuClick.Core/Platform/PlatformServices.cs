using SkiaSharp;

namespace DocuClick.Platform;

/// <summary>A left/right mouse button press seen by the global input monitor.</summary>
public sealed class MouseClickEventArgs : EventArgs
{
    public required ScreenPoint Point { get; init; }
    public required DateTime Timestamp { get; init; }
    public required bool ShiftDown { get; init; }
    public required bool ControlDown { get; init; }
    public required bool AltDown { get; init; }

    /// <summary>⌘ on macOS; always false on Windows.</summary>
    public bool CommandDown { get; init; }
}

/// <summary>An Enter key press seen by the global input monitor. No other key is ever reported.</summary>
public sealed class EnterKeyEventArgs : EventArgs
{
    public required DateTime Timestamp { get; init; }
    public required bool ShiftDown { get; init; }
    public required bool ControlDown { get; init; }
    public required bool AltDown { get; init; }

    /// <summary>⌘ on macOS; always false on Windows.</summary>
    public bool CommandDown { get; init; }
}

/// <summary>
/// System-wide mouse/keyboard monitoring (Windows: low-level hooks; macOS:
/// a listen-only CGEventTap). Events may be raised on any thread and
/// handlers must return quickly.
/// </summary>
public interface IInputMonitor : IDisposable
{
    event EventHandler<MouseClickEventArgs>? LeftButtonDown;
    event EventHandler<MouseClickEventArgs>? RightButtonDown;
    event EventHandler<EnterKeyEventArgs>? EnterPressed;

    /// <param name="captureEnter">Also report Enter key presses (never any other key).</param>
    void Start(bool captureEnter);
    void Stop();

    /// <summary>Modifier keys held down right now — a cheap poll (not a hook) for the TopBar's live "wird übersprungen" indicator.</summary>
    ModifierState CurrentModifiers { get; }
}

public readonly record struct ModifierState(bool Shift, bool Control, bool Alt, bool Command = false);

/// <summary>
/// A captured screenshot before highlighting/encoding. <see cref="Bounds"/>
/// is the captured area in screen units; <see cref="Scale"/> is bitmap
/// pixels per screen unit (1.0 on Windows, typically 2.0 on Retina Macs).
/// The receiver owns and disposes <see cref="Bitmap"/>.
/// </summary>
public sealed record CapturedFrame(SKBitmap Bitmap, ScreenRect Bounds, double Scale);

/// <summary>
/// Opaque handle to a frame grabbed at the moment of a click, before the
/// clicked app had a chance to react (opened menus, pressed-button states,
/// page navigations). Only platforms with a continuous capture stream
/// (macOS) produce these.
/// </summary>
public abstract class PreClickFrame : IDisposable
{
    public abstract void Dispose();
}

public interface IScreenCapture
{
    /// <summary>Called when a recording session starts/stops — e.g. to run a continuous capture stream only while recording.</summary>
    void BeginSession() { }
    void EndSession() { }

    /// <summary>
    /// Grabs the most recent frame synchronously, from inside the input
    /// callback. Must be cheap (no encoding, no I/O). Returns null if not
    /// supported or not available.
    /// </summary>
    PreClickFrame? GrabPreClickFrame() => null;

    /// <summary>
    /// Captures the top-level window under <paramref name="point"/>, falling
    /// back to the screen around it if there is no real window there (bare
    /// desktop, Dock, menu bar). Uses <paramref name="preClick"/> as the
    /// image source when given, cropped to the same area.
    /// </summary>
    CapturedFrame CaptureWindowAt(ScreenPoint point, PreClickFrame? preClick);

    /// <summary>Square crop centered on <paramref name="point"/> ("Zoom-auf-Cursor"), clamped to its screen.</summary>
    CapturedFrame CaptureAroundPoint(ScreenPoint point, int radius, PreClickFrame? preClick);

    /// <summary>The active window — for the Enter trigger, which has no click point.</summary>
    CapturedFrame CaptureForegroundWindow(PreClickFrame? preClick);
}

/// <summary>Accessibility lookup (Windows: UI Automation; macOS: AXUIElement).</summary>
public interface IElementInspector
{
    ElementInfo? GetElementAt(ScreenPoint point);
    ElementInfo? GetFocusedElement();
}

public interface IForegroundWindow
{
    /// <summary>Title of the active window, as a fallback when accessibility yields nothing.</summary>
    string? GetTitle();
}

/// <summary>Audible feedback, since the capture itself is deliberately invisible.</summary>
public interface IFeedbackSounds
{
    void PlayCaptured();
    void PlaySkipped();
    void PlayError();
}

/// <summary>Everything <see cref="Services.SessionManager"/> needs from the host OS.</summary>
public sealed record PlatformServices(
    IInputMonitor Input,
    IScreenCapture Capture,
    IElementInspector Elements,
    IForegroundWindow ForegroundWindow,
    IFeedbackSounds Sounds);
