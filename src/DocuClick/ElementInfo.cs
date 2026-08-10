namespace DocuClick;

/// <summary>UI Automation data for the element under the cursor at click time.</summary>
public sealed record ElementInfo(
    string? Name,
    string? ControlType,
    string? WindowTitle,
    System.Windows.Rect? BoundingRectangle,
    /// <summary>
    /// True when UI Automation itself flags this element as a password
    /// input (AutomationElement.IsPasswordProperty) — SessionManager skips
    /// the capture entirely for these rather than screenshotting/describing
    /// whatever's on screen at that instant, the one case this app can
    /// actually detect and guard automatically (the manual
    /// SkipRecordingModifier still covers everything else).
    /// </summary>
    bool IsPassword = false);
