using System.Runtime.InteropServices;

namespace DocuClick.Mac.Platform;

/// <summary>
/// P/Invoke surface of libDocuClickMac.dylib (macos/DocuClickMacNative).
/// Opaque handles (frames, elements, pre-click snapshots) are owned by the
/// Swift side and must be released with their matching *_free/_release call.
/// </summary>
internal static unsafe partial class Native
{
    private const string Lib = "libDocuClickMac";

    // --- Errors / memory
    [LibraryImport(Lib)] internal static partial nint dc_last_error();
    [LibraryImport(Lib)] internal static partial void dc_free(nint pointer);

    // --- Permissions (0/1)
    [LibraryImport(Lib)] internal static partial int dc_perm_accessibility(int prompt);
    [LibraryImport(Lib)] internal static partial int dc_perm_input_monitoring(int request);
    [LibraryImport(Lib)] internal static partial int dc_perm_screen_recording(int request);
    [LibraryImport(Lib)] internal static partial void dc_open_privacy_pane(int which);

    // --- Input
    [LibraryImport(Lib)] internal static partial int dc_input_start(delegate* unmanaged<int, double, double, ulong, void> callback, int captureEnter);
    [LibraryImport(Lib)] internal static partial void dc_input_stop();
    [LibraryImport(Lib)] internal static partial ulong dc_modifier_flags();
    [LibraryImport(Lib)] internal static partial void dc_mouse_location(double* location);

    // --- Hotkeys (main thread)
    [LibraryImport(Lib)] internal static partial void dc_hotkey_set_callback(delegate* unmanaged<int, void> callback);
    [LibraryImport(Lib)] internal static partial int dc_hotkey_register(int id, uint keyCode, uint modifiers);
    [LibraryImport(Lib)] internal static partial void dc_hotkey_unregister(int id);

    // --- Capture
    [LibraryImport(Lib)] internal static partial void dc_capture_session_begin(int preClickStreams);
    [LibraryImport(Lib)] internal static partial void dc_capture_session_end();
    [LibraryImport(Lib)] internal static partial nint dc_preclick_grab();
    [LibraryImport(Lib)] internal static partial void dc_preclick_release(nint handle);
    [LibraryImport(Lib)] internal static partial nint dc_capture_window_at(double x, double y, nint preClick);
    [LibraryImport(Lib)] internal static partial nint dc_capture_around(double x, double y, double radius, nint preClick);
    [LibraryImport(Lib)] internal static partial nint dc_capture_frontmost(nint preClick);
    [LibraryImport(Lib)] internal static partial nint dc_frame_pixels(nint frame);
    [LibraryImport(Lib)] internal static partial int dc_frame_width(nint frame);
    [LibraryImport(Lib)] internal static partial int dc_frame_height(nint frame);
    [LibraryImport(Lib)] internal static partial int dc_frame_bytes_per_row(nint frame);
    [LibraryImport(Lib)] internal static partial double dc_frame_scale(nint frame);
    [LibraryImport(Lib)] internal static partial double dc_frame_x(nint frame);
    [LibraryImport(Lib)] internal static partial double dc_frame_y(nint frame);
    [LibraryImport(Lib)] internal static partial double dc_frame_w(nint frame);
    [LibraryImport(Lib)] internal static partial double dc_frame_h(nint frame);
    [LibraryImport(Lib)] internal static partial void dc_frame_free(nint frame);
    [LibraryImport(Lib)] internal static partial nint dc_frontmost_window_title();

    // --- Accessibility
    [LibraryImport(Lib)] internal static partial nint dc_element_at(double x, double y);
    [LibraryImport(Lib)] internal static partial nint dc_element_focused();
    [LibraryImport(Lib)] internal static partial nint dc_element_name(nint element);
    [LibraryImport(Lib)] internal static partial nint dc_element_type(nint element);
    [LibraryImport(Lib)] internal static partial nint dc_element_window_title(nint element);
    [LibraryImport(Lib)] internal static partial int dc_element_frame(nint element, double* frame);
    [LibraryImport(Lib)] internal static partial int dc_element_is_password(nint element);
    [LibraryImport(Lib)] internal static partial void dc_element_free(nint element);

    // --- AppKit helpers (main thread)
    [LibraryImport(Lib)] internal static partial void dc_window_make_overlay(nint window, int clickThrough, int nonActivating);
    [LibraryImport(Lib)] internal static partial int dc_window_frame(nint window, double* frame);
    [LibraryImport(Lib)] internal static partial void dc_window_set_top_left(nint window, double x, double y);
    [LibraryImport(Lib)] internal static partial void dc_main_screen_visible_frame(double* frame);
    [LibraryImport(Lib)] internal static partial int dc_frontmost_pid();
    [LibraryImport(Lib)] internal static partial void dc_activate_pid(int pid);
    [LibraryImport(Lib)] internal static partial void dc_activate_self();
    [LibraryImport(Lib)] internal static partial void dc_play_sound(int kind);
    [LibraryImport(Lib)] internal static partial int dc_point_on_own_ui(double x, double y);
    [LibraryImport(Lib)] internal static partial int dc_webview_make_transparent(nint viewOrWindow);
    [LibraryImport(Lib)] internal static partial void dc_window_make_resizable(nint window);

    /// <summary>Reads a UTF-8 string owned by the native side (not freed).</summary>
    internal static string? Borrowed(nint utf8) => utf8 == 0 ? null : Marshal.PtrToStringUTF8(utf8);

    /// <summary>Reads and frees a UTF-8 string the native side allocated for us.</summary>
    internal static string? Take(nint utf8)
    {
        var value = Borrowed(utf8);
        if (utf8 != 0)
        {
            dc_free(utf8);
        }

        return value;
    }

    internal static string LastError() => Take(dc_last_error()) ?? "Unbekannter Fehler.";
}
