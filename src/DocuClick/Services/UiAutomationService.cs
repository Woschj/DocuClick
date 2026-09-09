using System.Threading;
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
    // output folder carries on. The abandoned background call itself can't be
    // cancelled (COM/UIA calls have no cooperative cancellation), so it
    // keeps running on its own pooled thread rather than the one that
    // asked for it — one leaked pool thread per genuine hang is a far
    // better outcome than the whole app freezing.
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(800);

    // Bounds how many abandoned lookups can pile up as leaked thread-pool
    // threads (see RunWithTimeout's own doc comment) — a target app that
    // reliably, permanently hangs UI Automation would otherwise leak one
    // more thread per click for the rest of the session. Once this many
    // lookups are presumed permanently hung, every further lookup is
    // skipped outright (treated as "no element", same as any other UIA
    // failure) instead of adding yet another one on top.
    private static readonly SemaphoreSlim ConcurrencySlots = new(4, 4);

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
        if (!ConcurrencySlots.Wait(0))
        {
            // Already at the cap — UI Automation against whatever's being
            // clicked right now is unreliable; skip rather than pile on
            // another leaked thread-pool thread that may never return.
            return null;
        }

        try
        {
            var task = Task.Run(() =>
            {
                try
                {
                    return work();
                }
                finally
                {
                    // Releases whenever the abandoned call eventually
                    // finishes, however long that takes — decoupled from
                    // whether the Wait below timed out.
                    ConcurrencySlots.Release();
                }
            });
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
            BoundingRectangle: current.BoundingRectangle.IsEmpty ? null : current.BoundingRectangle,
            IsPassword: IsPasswordElement(element));
    }

    /// <summary>
    /// AutomationElement.IsPasswordProperty — the one kind of sensitive
    /// content this app can actually detect on its own (a real PasswordBox/
    /// masked input reports this reliably; custom-drawn "hidden" fields that
    /// never registered it with UI Automation are still only covered by the
    /// manual SkipRecordingModifier, same as before). Read defensively: the
    /// property can throw against a poorly-behaved automation provider, same
    /// as any other UIA call.
    /// </summary>
    private static bool IsPasswordElement(AutomationElement element)
    {
        try
        {
            return element.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty) is true;
        }
        catch (Exception)
        {
            return false;
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
