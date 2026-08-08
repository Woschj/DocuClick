using System.Windows.Automation;

namespace DocuClick.Services;

public static class UiAutomationService
{
    // UI Automation is well known to occasionally hang against a
    // misbehaving target app's automation provider (slow/poor providers —
    // some Electron apps, older Win32 apps, anything under heavy load).
    // Without a timeout, that hang froze the writer thread indefinitely —
    // which also froze the UI thread for any subsequent branch action (all
    // of them block on the writer queue via SessionManager.RunOnWriterQueue)
    // and let clicks pile up unprocessed for the rest of the session, with
    // no visible error. Every lookup below runs on a pooled thread with a
    // hard deadline instead: if it doesn't finish in time, this simply
    // returns "no element" (the caller already treats that as normal — see
    // DescriptionGenerator's fallback path) and the actual write to the
    // vault carries on. The abandoned background call itself can't be
    // cancelled (COM/UIA calls have no cooperative cancellation), so it
    // keeps running on its own pooled thread rather than the one that
    // asked for it — one leaked pool thread per genuine hang is a far
    // better outcome than the whole app freezing.
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(800);

    public static ElementInfo? GetElementAt(System.Drawing.Point screenPoint) =>
        RunWithTimeout(() =>
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
        });

    /// <summary>Used for the Enter-key trigger, which has no click point to look up.</summary>
    public static ElementInfo? GetFocusedElement() =>
        RunWithTimeout(() =>
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
        });

    private static ElementInfo? RunWithTimeout(Func<ElementInfo?> work)
    {
        try
        {
            var task = Task.Run(work);
            return task.Wait(Timeout) ? task.Result : null;
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
