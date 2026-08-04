using System.Windows.Automation;

namespace DocuClick.Services;

public static class UiAutomationService
{
    public static ElementInfo? GetElementAt(System.Drawing.Point screenPoint)
    {
        try
        {
            var element = AutomationElement.FromPoint(new System.Windows.Point(screenPoint.X, screenPoint.Y));
            return element is null ? null : BuildElementInfo(element);
        }
        catch (Exception)
        {
            // UI Automation throws in plenty of ordinary situations
            // (elevated/secure-desktop windows, elements that vanish
            // between click and lookup, Electron/browser content with
            // no automation tree). Treat all of these as "no element".
            return null;
        }
    }

    /// <summary>Used for the Enter-key trigger, which has no click point to look up.</summary>
    public static ElementInfo? GetFocusedElement()
    {
        try
        {
            var element = AutomationElement.FocusedElement;
            return element is null ? null : BuildElementInfo(element);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static ElementInfo BuildElementInfo(AutomationElement element)
    {
        var current = element.Current;
        return new ElementInfo(
            Name: string.IsNullOrWhiteSpace(current.Name) ? null : current.Name,
            ControlType: current.ControlType?.LocalizedControlType,
            WindowTitle: GetWindowTitle(element),
            BoundingRectangle: current.BoundingRectangle.IsEmpty ? null : current.BoundingRectangle);
    }

    private static string? GetWindowTitle(AutomationElement element)
    {
        try
        {
            var walker = TreeWalker.ControlViewWalker;
            var node = element;

            while (node is not null && node.Current.ControlType != ControlType.Window)
            {
                node = walker.GetParent(node);
            }

            var title = node?.Current.Name;
            return string.IsNullOrWhiteSpace(title) ? null : title;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
