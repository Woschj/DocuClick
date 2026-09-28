import AppKit
import ApplicationServices
import ScreenCaptureKit

/// 1 if Accessibility access is granted. prompt=1 shows the system prompt if not.
@_cdecl("dc_perm_accessibility")
public func dc_perm_accessibility(_ prompt: Int32) -> Int32 {
    if prompt != 0 {
        let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        return AXIsProcessTrustedWithOptions(options) ? 1 : 0
    }
    return AXIsProcessTrusted() ? 1 : 0
}

/// 1 if Input Monitoring is granted. request=1 asks for it if not.
@_cdecl("dc_perm_input_monitoring")
public func dc_perm_input_monitoring(_ request: Int32) -> Int32 {
    if CGPreflightListenEventAccess() { return 1 }
    return request != 0 && CGRequestListenEventAccess() ? 1 : 0
}

/// 1 if Screen Recording is granted. request=1 asks for it if not
/// (a newly granted permission only takes effect after a restart).
@_cdecl("dc_perm_screen_recording")
public func dc_perm_screen_recording(_ request: Int32) -> Int32 {
    if CGPreflightScreenCaptureAccess() { return 1 }
    guard request != 0 else { return 0 }
    // CGRequestScreenCaptureAccess alone doesn't reliably register the app
    // (the probe only showed up in System Settings after its first real
    // ScreenCaptureKit call) — so also touch ScreenCaptureKit, which makes
    // macOS list DocuClick under "Bildschirmaufnahme" and show its prompt.
    SCShareableContent.getExcludingDesktopWindows(false, onScreenWindowsOnly: true) { _, _ in }
    return CGRequestScreenCaptureAccess() ? 1 : 0
}

/// Opens System Settings at 0 = Accessibility, 1 = Input Monitoring, 2 = Screen Recording.
@_cdecl("dc_open_privacy_pane")
public func dc_open_privacy_pane(_ which: Int32) {
    let anchors = ["Privacy_Accessibility", "Privacy_ListenEvent", "Privacy_ScreenCapture"]
    let anchor = anchors[Int(max(0, min(2, which)))]
    if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?\(anchor)") {
        NSWorkspace.shared.open(url)
    }
}
