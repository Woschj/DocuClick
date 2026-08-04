using System.Runtime.InteropServices;

namespace DocuClick.Services;

/// <summary>Shared Shift/Control/Alt state lookup for the mouse and keyboard hooks.</summary>
internal static partial class ModifierKeyState
{
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12; // Alt

    [LibraryImport("user32.dll")]
    private static partial short GetKeyState(int nVirtKey);

    // High bit set = key currently down. GetKeyState (not GetAsyncKeyState)
    // reflects the state as of the last message retrieved by the calling
    // thread's queue, which matches what a hook callback should see.
    private static bool IsDown(int virtualKey) => (GetKeyState(virtualKey) & 0x8000) != 0;

    internal static bool ShiftDown => IsDown(VK_SHIFT);
    internal static bool ControlDown => IsDown(VK_CONTROL);
    internal static bool AltDown => IsDown(VK_MENU);
}
