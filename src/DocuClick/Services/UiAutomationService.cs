using System.Windows.Automation;

namespace DocuClick.Services;

public static class UiAutomationService
{
    public static ElementInfo? GetElementAt(System.Drawing.Point screenPoint)
    {
        try
        {
            var element = AutomationElement.FromPoint(new System.Windows.Point(screenPoint.X, screenPoint.Y));
            if (element is null)
            {
                return null;
            }

            var current = element.Current;

            return new ElementInfo(
                Name: string.IsNullOrWhiteSpace(current.Name) ? null : current.Name,
                ControlType: current.ControlType?.LocalizedControlType,
                WindowTitle: GetWindowTitle(element),
                BoundingRectangle: current.BoundingRectangle.IsEmpty ? null : current.BoundingRectangle);
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
