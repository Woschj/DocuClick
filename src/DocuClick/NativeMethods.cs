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
