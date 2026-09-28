import AppKit

/// Accepts either an NSWindow* or an NSView* (whatever the UI toolkit hands out).
func resolveWindow(_ handle: UnsafeMutableRawPointer) -> NSWindow? {
    let object = Unmanaged<NSObject>.fromOpaque(handle).takeUnretainedValue()
    return (object as? NSWindow) ?? (object as? NSView)?.window
}

/// Height of the primary screen, for converting between Cocoa's bottom-left
/// and the global top-left coordinate space used everywhere else.
private var primaryScreenHeight: CGFloat { NSScreen.screens.first?.frame.height ?? 0 }

private func topLeft(_ cocoa: NSRect) -> CGRect {
    CGRect(x: cocoa.minX, y: primaryScreenHeight - cocoa.maxY, width: cocoa.width, height: cocoa.height)
}

/// Window frame in global top-left screen points → out[0..3]. Main thread only.
@_cdecl("dc_window_frame")
public func dc_window_frame(_ handle: UnsafeMutableRawPointer, _ out: UnsafeMutablePointer<Double>) -> Int32 {
    guard let window = resolveWindow(handle) else { return 0 }
    let frame = topLeft(window.frame)
    out[0] = frame.minX; out[1] = frame.minY; out[2] = frame.width; out[3] = frame.height
    return window.isVisible ? 1 : 0
}

/// Moves the window so its top-left corner is at (x, y) in global top-left points. Main thread only.
@_cdecl("dc_window_set_top_left")
public func dc_window_set_top_left(_ handle: UnsafeMutableRawPointer, _ x: Double, _ y: Double) {
    guard let window = resolveWindow(handle) else { return }
    window.setFrameTopLeftPoint(NSPoint(x: x, y: primaryScreenHeight - y))
}

/// Visible area (without menu bar and Dock) of the main screen in global top-left points → out[0..3].
@_cdecl("dc_main_screen_visible_frame")
public func dc_main_screen_visible_frame(_ out: UnsafeMutablePointer<Double>) {
    let frame = topLeft(NSScreen.screens.first?.visibleFrame ?? .zero)
    out[0] = frame.minX; out[1] = frame.minY; out[2] = frame.width; out[3] = frame.height
}

/// PID of the frontmost app, to hand focus back to it after a DocuClick dialog closes.
@_cdecl("dc_frontmost_pid")
public func dc_frontmost_pid() -> Int32 {
    NSWorkspace.shared.frontmostApplication?.processIdentifier ?? 0
}

@_cdecl("dc_activate_pid")
public func dc_activate_pid(_ pid: Int32) {
    guard pid > 0, pid != getpid() else { return }
    NSRunningApplication(processIdentifier: pid)?.activate()
}

/// Brings DocuClick itself to the front (dialogs need keyboard focus).
@_cdecl("dc_activate_self")
public func dc_activate_self() {
    NSApp.activate(ignoringOtherApps: true)
}

/// Turns an Avalonia window (its NSWindow*) into a DocuClick overlay:
/// floats above normal windows on every Space and over full-screen apps,
/// never hides when the app is inactive. clickThrough=1 lets clicks pass to
/// whatever is underneath (recording dot, status HUD). nonActivating=1
/// keeps clicks on it from activating DocuClick, so the recorded app stays
/// frontmost (top bar, Ablauf-Übersicht). Must be called on the main thread.
@_cdecl("dc_window_make_overlay")
public func dc_window_make_overlay(_ handle: UnsafeMutableRawPointer, _ clickThrough: Int32, _ nonActivating: Int32) {
    guard let window = resolveWindow(handle) else { return }
    window.level = .statusBar
    window.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
    window.hidesOnDeactivate = false
    window.ignoresMouseEvents = clickThrough != 0
    ownUiLock.withLock {
        if clickThrough != 0 {
            clickThroughWindows.insert(window.windowNumber)
        } else {
            clickThroughWindows.remove(window.windowNumber)
        }
    }
    if nonActivating != 0 {
        NonActivating.apply(to: window)
    }
}

private let ownUiLock = NSLock()
private var clickThroughWindows = Set<Int>()

/// 1 if a mouse-down at (x, y) would go to one of DocuClick's own windows —
/// any level, including menus and the menu-bar status item. Asks the window
/// server which window would actually be hit (NSWindow.windowNumber(at:)),
/// so invisible click-through windows of other apps are skipped: the Dock
/// keeps a transparent full-screen window above normal floating windows,
/// and a plain "topmost window containing the point" check hit that one
/// instead of the Ablauf-Übersicht beneath it. Safe to call from the
/// event-tap thread (window-server queries only).
@_cdecl("dc_point_on_own_ui")
public func dc_point_on_own_ui(_ x: Double, _ y: Double) -> Int32 {
    let primaryHeight = CGDisplayBounds(CGMainDisplayID()).height
    let number = NSWindow.windowNumber(at: NSPoint(x: x, y: primaryHeight - y), belowWindowWithWindowNumber: 0)
    guard number > 0 else { return 0 }
    if ownUiLock.withLock({ clickThroughWindows.contains(number) }) { return 0 }
    guard let info = (CGWindowListCopyWindowInfo([.optionIncludingWindow], CGWindowID(number)) as? [[String: Any]])?.first,
          let pid = info[kCGWindowOwnerPID as String] as? pid_t else { return 0 }
    return pid == getpid() ? 1 : 0
}

/// Lets a borderless window be resized from its edges and corners like any
/// other Mac window (Avalonia's borderless windows have no resize mask).
@_cdecl("dc_window_make_resizable")
public func dc_window_make_resizable(_ handle: UnsafeMutableRawPointer) {
    guard let window = resolveWindow(handle) else { return }
    window.styleMask.insert(.resizable)
}

/// NSWindow has no public "don't activate the app on click" switch —
/// only NSPanel's .nonactivatingPanel style does. Avalonia windows are
/// plain NSWindow subclasses, so the class of this one window instance is
/// swapped for a generated subclass that refuses key/main status and
/// declines app activation (the probe verified the NSPanel behavior; this
/// replicates it without owning the window's creation).
private enum NonActivating {
    static var subclasses: [ObjectIdentifier: AnyClass] = [:]

    static func apply(to window: NSWindow) {
        let original: AnyClass = type(of: window)
        if subclasses.values.contains(where: { $0 == original }) {
            return // already applied (the window was shown again)
        }
        let subclass: AnyClass
        if let existing = subclasses[ObjectIdentifier(original)] {
            subclass = existing
        } else {
            let name = "DocuClickNonActivating_\(NSStringFromClass(original))"
            guard let created = objc_allocateClassPair(original, name, 0) else { return }
            let no: @convention(block) (AnyObject) -> Bool = { _ in false }
            let noImp = imp_implementationWithBlock(no)
            class_addMethod(created, #selector(getter: NSWindow.canBecomeKey), noImp, "c@:")
            class_addMethod(created, #selector(getter: NSWindow.canBecomeMain), noImp, "c@:")
            objc_registerClassPair(created)
            subclasses[ObjectIdentifier(original)] = created
            subclass = created
        }
        object_setClass(window, subclass)
        window.styleMask.insert(.nonactivatingPanel)
    }
}

/// 0 = captured (shutter), 1 = skipped, 2 = error.
@_cdecl("dc_play_sound")
public func dc_play_sound(_ kind: Int32) {
    DispatchQueue.main.async {
        let sound: NSSound?
        switch kind {
        case 0:
            sound = NSSound(contentsOfFile: "/System/Library/Components/CoreAudio.component/Contents/SharedSupport/SystemSounds/system/Grab.aif", byReference: true)
                ?? NSSound(named: "Tink")
        case 1:
            sound = NSSound(named: "Pop")
        default:
            sound = NSSound(named: "Basso")
        }
        sound?.play()
    }
}

/// Makes every WKWebView inside the given NSView/NSWindow draw no background
/// of its own, so a transparent page shows the host window's translucent
/// panel through it (like WebView2's DefaultBackgroundColor = Transparent).
/// Main thread only. Returns the number of web views changed.
@_cdecl("dc_webview_make_transparent")
public func dc_webview_make_transparent(_ handle: UnsafeMutableRawPointer) -> Int32 {
    let object = Unmanaged<NSObject>.fromOpaque(handle).takeUnretainedValue()
    guard let root = (object as? NSView) ?? (object as? NSWindow)?.contentView else { return 0 }
    var changed: Int32 = 0
    func visit(_ view: NSView) {
        if NSStringFromClass(type(of: view)).contains("WKWebView") || view.className.contains("WKWebView") {
            view.setValue(false, forKey: "drawsBackground")
            if view.responds(to: NSSelectorFromString("setUnderPageBackgroundColor:")) {
                view.setValue(NSColor.clear, forKey: "underPageBackgroundColor")
            }
            changed += 1
        }
        view.subviews.forEach(visit)
    }
    visit(root)
    return changed
}
