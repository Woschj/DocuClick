using DocuClick.Platform;

namespace DocuClick;

/// <summary>Accessibility data for the element under the cursor at click time (UI Automation on Windows, AXUIElement on macOS).</summary>
public sealed record ElementInfo(
    string? Name,
    string? ControlType,
    string? WindowTitle,
    ScreenRect? BoundingRectangle,
    /// <summary>
    /// True when the accessibility API itself flags this element as a password
    /// input (UIA IsPasswordProperty / AX secure text field) — SessionManager skips
    /// the capture entirely for these rather than screenshotting/describing
    /// whatever's on screen at that instant, the one case this app can
    /// actually detect and guard automatically (the manual
    /// SkipRecordingModifier still covers everything else).
    /// </summary>
    bool IsPassword = false);
