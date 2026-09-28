namespace DocuClick.Mac.Platform;

/// <summary>
/// Global hotkeys via Carbon RegisterEventHotKey (native). Key/modifier
/// names use the same config strings as Windows ("Control+Alt", "R", "F9");
/// "Alt" means ⌥ Option and "Command"/"Windows" mean ⌘.
/// </summary>
internal sealed unsafe class MacHotkeys : IDisposable
{
    private static MacHotkeys? _instance;
    private readonly Dictionary<int, Action> _handlers = new();
    private int _nextId = 1;

    public MacHotkeys()
    {
        _instance = this;
        Native.dc_hotkey_set_callback(&OnHotkey);
    }

    /// <summary>Registers a hotkey; returns false (with a reason) if the key is unknown or the combination is taken.</summary>
    public bool Register(string modifiersSpec, string keySpec, Action handler, out string? error)
    {
        error = null;
        if (!KeyCodes.TryGetValue(keySpec.Trim(), out var keyCode))
        {
            error = $"unbekannte Taste '{keySpec}'";
            return false;
        }

        var id = _nextId++;
        if (Native.dc_hotkey_register(id, keyCode, ParseModifiers(modifiersSpec)) == 0)
        {
            error = Native.LastError();
            return false;
        }

        _handlers[id] = handler;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _handlers.Keys)
        {
            Native.dc_hotkey_unregister(id);
        }

        _handlers.Clear();
    }

    public void Dispose() => UnregisterAll();

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static void OnHotkey(int id)
    {
        try
        {
            if (_instance?._handlers.TryGetValue(id, out var handler) == true)
            {
                handler();
            }
        }
        catch (Exception ex)
        {
            Services.LogService.Log($"Fehler im Hotkey-Handler: {ex}");
        }
    }

    private static uint ParseModifiers(string spec)
    {
        uint mask = 0;
        foreach (var part in spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            mask |= part.ToLowerInvariant() switch
            {
                "control" or "ctrl" => 4096u,
                "alt" or "option" => 2048u,
                "shift" => 512u,
                "command" or "cmd" or "windows" or "meta" => 256u,
                _ => 0u
            };
        }

        return mask;
    }

    /// <summary>Human-readable form with Mac symbols, e.g. "⌃⌥R".</summary>
    public static string Format(string modifiersSpec, string keySpec)
    {
        var mask = ParseModifiers(modifiersSpec);
        var symbols = (mask & 4096) != 0 ? "⌃" : "";
        symbols += (mask & 2048) != 0 ? "⌥" : "";
        symbols += (mask & 512) != 0 ? "⇧" : "";
        symbols += (mask & 256) != 0 ? "⌘" : "";
        var key = keySpec.Length == 2 && keySpec[0] == 'D' && char.IsDigit(keySpec[1]) ? keySpec[1..] : keySpec;
        return symbols + key;
    }

    /// <summary>Carbon virtual key codes (kVK_*), keyed by the Windows/Avalonia key names used in the config.</summary>
    private static readonly Dictionary<string, uint> KeyCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = 0x00, ["S"] = 0x01, ["D"] = 0x02, ["F"] = 0x03, ["H"] = 0x04, ["G"] = 0x05, ["Z"] = 0x06, ["X"] = 0x07,
        ["C"] = 0x08, ["V"] = 0x09, ["B"] = 0x0B, ["Q"] = 0x0C, ["W"] = 0x0D, ["E"] = 0x0E, ["R"] = 0x0F, ["Y"] = 0x10,
        ["T"] = 0x11, ["O"] = 0x1F, ["U"] = 0x20, ["I"] = 0x22, ["P"] = 0x23, ["L"] = 0x25, ["J"] = 0x26, ["K"] = 0x28,
        ["N"] = 0x2D, ["M"] = 0x2E,
        ["D1"] = 0x12, ["D2"] = 0x13, ["D3"] = 0x14, ["D4"] = 0x15, ["D6"] = 0x16, ["D5"] = 0x17, ["D9"] = 0x19,
        ["D7"] = 0x1A, ["D8"] = 0x1C, ["D0"] = 0x1D,
        ["F1"] = 0x7A, ["F2"] = 0x78, ["F3"] = 0x63, ["F4"] = 0x76, ["F5"] = 0x60, ["F6"] = 0x61, ["F7"] = 0x62,
        ["F8"] = 0x64, ["F9"] = 0x65, ["F10"] = 0x6D, ["F11"] = 0x67, ["F12"] = 0x6F,
        ["Space"] = 0x31, ["Return"] = 0x24, ["Enter"] = 0x24, ["Tab"] = 0x30
    };
}
