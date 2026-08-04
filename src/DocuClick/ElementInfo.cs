namespace DocuClick;

/// <summary>UI Automation data for the element under the cursor at click time.</summary>
public sealed record ElementInfo(
    string? Name,
    string? ControlType,
    string? WindowTitle,
    System.Windows.Rect? BoundingRectangle);
