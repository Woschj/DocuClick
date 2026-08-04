using System.Runtime.InteropServices;

namespace DocuClick.Services;

public sealed class MouseClickEventArgs : EventArgs
{
    public required System.Drawing.Point Point { get; init; }
    public required DateTime Timestamp { get; init; }
    public required bool ShiftDown { get; init; }
    public required bool ControlDown { get; init; }
    public required bool AltDown { get; init; }
}

/// <summary>
/// Global, systemwide left-/right-click listener via a WH_MOUSE_LL hook.
/// Must be created/started on a thread that pumps Windows messages
/// (the WPF UI thread's Dispatcher loop qualifies).
/// </summary>
public sealed class MouseHookService : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;

    private delegate nint LowLevelMouseProc(int nCode, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    // Kept alive for the hook's lifetime: if the GC collects the delegate
    // while native code still holds a function pointer to it, the process
    // crashes on the next click.
    private readonly LowLevelMouseProc _proc;
    private nint _hookHandle;

    public event EventHandler<MouseClickEventArgs>? LeftButtonDown;
    public event EventHandler<MouseClickEventArgs>? RightButtonDown;

    public bool IsEnabled { get; private set; }

    public MouseHookService()
    {
        _proc = HookCallback;
    }

    public void Start()
    {
        if (IsEnabled)
        {
            return;
        }

        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        _hookHandle = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(curModule.ModuleName), 0);

        if (_hookHandle == 0)
        {
            throw new InvalidOperationException(
                $"Konnte globalen Mouse-Hook nicht registrieren (Win32-Fehler {Marshal.GetLastWin32Error()}).");
        }

        IsEnabled = true;
    }

    public void Stop()
    {
        if (!IsEnabled)
        {
            return;
        }

        UnhookWindowsHookEx(_hookHandle);
        _hookHandle = 0;
        IsEnabled = false;
    }

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && (wParam == WM_LBUTTONDOWN || wParam == WM_RBUTTONDOWN))
        {
            var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var args = new MouseClickEventArgs
            {
                Point = new System.Drawing.Point(hookStruct.pt.X, hookStruct.pt.Y),
                Timestamp = DateTime.Now,
                ShiftDown = ModifierKeyState.ShiftDown,
                ControlDown = ModifierKeyState.ControlDown,
                AltDown = ModifierKeyState.AltDown
            };

            // Handlers must return fast: a low-level hook that blocks the
            // message queue for too long gets silently unhooked by Windows,
            // which would kill click detection for the rest of the session.
            if (wParam == WM_LBUTTONDOWN)
            {
                LeftButtonDown?.Invoke(this, args);
            }
            else
            {
                RightButtonDown?.Invoke(this, args);
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose() => Stop();
}
