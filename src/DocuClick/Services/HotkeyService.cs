using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace DocuClick.Services;

/// <summary>
/// Global hotkeys via RegisterHotKey, backed by a hidden message-only
/// window (HWND_MESSAGE) so no visible window is needed just to receive
/// WM_HOTKEY.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    private const int WM_HOTKEY = 0x0312;
    private const int HWND_MESSAGE = -3;

    private HwndSource? _source;
    private readonly Dictionary<int, Action> _handlers = new();
    private int _nextId = 0xC000;

    public void Initialize()
    {
        var parameters = new HwndSourceParameters("DocuClickHotkeyWindow")
        {
            ParentWindow = new nint(HWND_MESSAGE),
            WindowStyle = 0
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public void Register(ModifierKeys modifiers, Key key, Action handler)
    {
        if (_source is null)
        {
            throw new InvalidOperationException("HotkeyService.Initialize() wurde nicht aufgerufen.");
        }

        var id = _nextId++;
        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);

        if (!RegisterHotKey(_source.Handle, id, (uint)modifiers, vk))
        {
            throw new InvalidOperationException(
                $"Hotkey {modifiers}+{key} konnte nicht registriert werden (evtl. bereits von einer anderen App belegt; Win32-Fehler {Marshal.GetLastWin32Error()}).");
        }

        _handlers[id] = handler;
    }

    public static ModifierKeys ParseModifiers(string spec)
    {
        var result = ModifierKeys.None;
        foreach (var part in spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            result |= part.ToLowerInvariant() switch
            {
                "control" or "ctrl" => ModifierKeys.Control,
                "alt" => ModifierKeys.Alt,
                "shift" => ModifierKeys.Shift,
                "windows" or "win" => ModifierKeys.Windows,
                _ => ModifierKeys.None
            };
        }

        return result;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _handlers.TryGetValue((int)wParam, out var handler))
        {
            handler();
            handled = true;
        }

        return nint.Zero;
    }

    public void Dispose()
    {
        if (_source is null)
        {
            return;
        }

        foreach (var id in _handlers.Keys)
        {
            UnregisterHotKey(_source.Handle, id);
        }

        _handlers.Clear();
        _source.RemoveHook(WndProc);
        _source.Dispose();
        _source = null;
    }
}
