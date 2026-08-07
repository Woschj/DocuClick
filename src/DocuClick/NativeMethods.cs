using System.Runtime.InteropServices;

namespace DocuClick;

internal static partial class NativeMethods
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(nint hIcon);

    internal const int GWL_EXSTYLE = -20;
    internal const nint WS_EX_TRANSPARENT = 0x20;
    internal const nint WS_EX_LAYERED = 0x80000;
    internal const nint WS_EX_TOOLWINDOW = 0x80;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    /// <summary>
    /// WS_EX_TRANSPARENT + WS_EX_LAYERED makes the window click-through (all
    /// mouse input passes to whatever is behind it); WS_EX_TOOLWINDOW hides
    /// it from Alt+Tab and the taskbar. Used for the recording/canvas-status
    /// overlays, which must never intercept the very clicks they're
    /// indicating.
    /// </summary>
    internal static void MakeClickThrough(nint hwnd)
    {
        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW);
    }

    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);

    /// <summary>
    /// Windows 10 2004+/11: excludes the window from GDI (CopyFromScreen)
    /// and modern screen-capture APIs entirely, so the overlay never ends
    /// up baked into a captured screenshot. Silently a no-op on older
    /// Windows versions.
    /// </summary>
    internal static void ExcludeFromScreenCapture(nint hwnd) => SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);

    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_ACTIVATE = 1;
    private const int MA_NOACTIVATE = 3;

    /// <summary>
    /// Incremented/decremented around every <c>ShowDialog()</c>/
    /// <c>MessageBox.Show()</c> call made from a window that also uses
    /// <see cref="DeliverActivatingClick"/> (BranchNameWindow, the delete
    /// confirmation) — see that method's doc comment for why this exists.
    /// A depth counter rather than a bool: BranchNameWindow's own "Bitte
    /// einen Namen eingeben" MessageBox nests inside an already-open
    /// ShowDialog, and must not prematurely clear the flag when it closes.
    /// </summary>
    internal static int ModalDialogDepth;

    /// <summary>
    /// HwndSource hook for windows with <c>ShowActivated = false</c>
    /// (TopBarWindow, FlowPreviewOverlay — both deliberately don't steal
    /// focus just from appearing). Belt-and-suspenders alongside each
    /// window's own <c>MouseEnter</c> handler, which pre-activates on hover
    /// so there's normally no "activating click" left by the time a click
    /// happens at all — this covers the edge case where a click arrives
    /// before that hover-activation has finished (fast flicks, or
    /// activation triggered some other way). Explicitly answering
    /// WM_MOUSEACTIVATE with MA_ACTIVATE (rather than leaving it to
    /// whatever Windows' default would be) activates the window without
    /// discarding the click that caused it.
    ///
    /// While a modal dialog owned by this same app is open (<see
    /// cref="ModalDialogDepth"/> &gt; 0), this instead answers
    /// MA_NOACTIVATE. Without that check, clicking "Abzweigung setzen" in
    /// FlowPreviewOverlay left the mouse sitting right over this same
    /// window; every stray WM_MOUSEACTIVATE it generated while the
    /// freshly-opened BranchNameWindow was still trying to take focus
    /// re-activated FlowPreviewOverlay instead, over and over — an
    /// activation fight neither side could win, which looked exactly like
    /// the whole app freezing (confirmed in testing: unrecoverable short of
    /// killing the process).
    /// </summary>
    internal static nint DeliverActivatingClick(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return ModalDialogDepth > 0 ? MA_NOACTIVATE : MA_ACTIVATE;
        }

        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    internal const uint GA_ROOT = 2;

    [LibraryImport("user32.dll")]
    internal static partial nint WindowFromPoint(POINT point);

    [LibraryImport("user32.dll")]
    internal static partial nint GetAncestor(nint hwnd, uint gaFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(nint hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NOTIFYICONIDENTIFIER
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public Guid guidItem;
    }

    /// <summary>
    /// Resolves a NotifyIcon's actual on-screen rectangle in the taskbar,
    /// so clicks on DocuClick's own tray icon can be excluded from the
    /// recording (a global WH_MOUSE_LL hook sees them just like any other
    /// click otherwise). WinForms' NotifyIcon has no public Handle/Id, so
    /// the caller obtains those via reflection.
    /// </summary>
    [LibraryImport("shell32.dll")]
    internal static partial int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);
}
