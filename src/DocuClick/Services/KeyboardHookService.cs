using System.Runtime.InteropServices;

namespace DocuClick.Services;

public sealed class EnterKeyEventArgs : EventArgs
{
    public required DateTime Timestamp { get; init; }
    public required bool ShiftDown { get; init; }
    public required bool ControlDown { get; init; }
    public required bool AltDown { get; init; }
}

/// <summary>
/// Global WH_KEYBOARD_LL hook that exists for exactly one purpose: noticing
/// when the Enter key is pressed, so it can trigger a capture the same way
/// a left click does. It must never be extended to inspect or report any
/// other key — that would turn this from a single-purpose trigger into a
/// general keystroke logger, which DocuClick explicitly does not want to be.
/// The callback below only ever compares vkCode against VK_RETURN and
/// discards everything else; no key identity or text is ever read, stored,
/// or forwarded.
/// </summary>
public sealed class KeyboardHookService : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104; // Enter with Alt held still arrives here
    private const uint VK_RETURN = 0x0D;

    private delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    private readonly LowLevelKeyboardProc _proc;
    private nint _hookHandle;

    public event EventHandler<EnterKeyEventArgs>? EnterPressed;

    public bool IsEnabled { get; private set; }

    public KeyboardHookService()
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
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(curModule.ModuleName), 0);

        if (_hookHandle == 0)
        {
            throw new InvalidOperationException(
                $"Konnte globalen Keyboard-Hook nicht registrieren (Win32-Fehler {Marshal.GetLastWin32Error()}).");
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
        if (nCode >= 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
        {
            var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

            // The only comparison this hook ever makes. Every other key
            // falls straight through to CallNextHookEx below, unexamined.
            if (hookStruct.vkCode == VK_RETURN)
            {
                EnterPressed?.Invoke(this, new EnterKeyEventArgs
                {
                    Timestamp = DateTime.Now,
                    ShiftDown = ModifierKeyState.ShiftDown,
                    ControlDown = ModifierKeyState.ControlDown,
                    AltDown = ModifierKeyState.AltDown
                });
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose() => Stop();
}
