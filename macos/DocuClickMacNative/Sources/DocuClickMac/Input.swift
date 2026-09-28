import CoreGraphics
import Foundation

/// kind: 0 = left click, 1 = right click (incl. Ctrl+click), 2 = Enter.
/// x/y: global screen points, origin top-left. flags: raw CGEventFlags.
public typealias DCInputCallback = @convention(c) (Int32, Double, Double, UInt64) -> Void

/// Listen-only CGEventTap on its own run-loop thread. Only ever looks at
/// mouse-down events and the Return/Enter key codes — every other key is
/// discarded unexamined, exactly like the Windows keyboard hook.
final class InputMonitor {
    static let shared = InputMonitor()

    private let lock = NSLock()
    private var tap: CFMachPort?
    private var runLoop: CFRunLoop?
    fileprivate var callback: DCInputCallback?
    fileprivate var captureEnter = false

    func start(callback: DCInputCallback, captureEnter: Bool) -> Bool {
        lock.lock(); defer { lock.unlock() }
        self.callback = callback
        self.captureEnter = captureEnter
        if tap != nil { return true }

        let mask = (1 << CGEventType.leftMouseDown.rawValue)
            | (1 << CGEventType.rightMouseDown.rawValue)
            | (1 << CGEventType.keyDown.rawValue)
        guard let port = CGEvent.tapCreate(
            tap: .cgSessionEventTap, place: .headInsertEventTap, options: .listenOnly,
            eventsOfInterest: CGEventMask(mask), callback: eventTapCallback, userInfo: nil) else {
            LastError.set("Event-Tap konnte nicht erstellt werden (Berechtigung „Eingabeüberwachung“ fehlt?).")
            return false
        }
        tap = port

        let ready = DispatchSemaphore(value: 0)
        let thread = Thread { [weak self] in
            let source = CFMachPortCreateRunLoopSource(nil, port, 0)
            CFRunLoopAddSource(CFRunLoopGetCurrent(), source, .commonModes)
            CGEvent.tapEnable(tap: port, enable: true)
            self?.runLoop = CFRunLoopGetCurrent()
            ready.signal()
            CFRunLoopRun()
        }
        thread.name = "DocuClick.EventTap"
        thread.qualityOfService = .userInteractive
        thread.start()
        ready.wait()
        return true
    }

    func stop() {
        lock.lock(); defer { lock.unlock() }
        callback = nil
        if let tap {
            CGEvent.tapEnable(tap: tap, enable: false)
            CFMachPortInvalidate(tap)
        }
        if let runLoop { CFRunLoopStop(runLoop) }
        tap = nil
        runLoop = nil
    }

    fileprivate func reenable() {
        if let tap { CGEvent.tapEnable(tap: tap, enable: true) }
    }
}

private let eventTapCallback: CGEventTapCallBack = { _, type, event, _ in
    let monitor = InputMonitor.shared
    switch type {
    case .tapDisabledByTimeout, .tapDisabledByUserInput:
        // The system disables taps whose callbacks are too slow; ours is
        // cheap, but make sure a hiccup never silently ends recording.
        monitor.reenable()
    case .leftMouseDown, .rightMouseDown:
        let isRight = type == .rightMouseDown || event.flags.contains(.maskControl)
        let location = event.location
        monitor.callback?(isRight ? 1 : 0, location.x, location.y, event.flags.rawValue)
    case .keyDown:
        guard monitor.captureEnter else { break }
        let keyCode = event.getIntegerValueField(.keyboardEventKeycode)
        if keyCode == 36 || keyCode == 76 { // Return, keypad Enter — nothing else is ever inspected
            monitor.callback?(2, 0, 0, event.flags.rawValue)
        }
    default:
        break
    }
    return Unmanaged.passUnretained(event)
}

/// Starts the global input monitor. Returns 1 on success, 0 (see dc_last_error) otherwise.
@_cdecl("dc_input_start")
public func dc_input_start(_ callback: DCInputCallback, _ captureEnter: Int32) -> Int32 {
    InputMonitor.shared.start(callback: callback, captureEnter: captureEnter != 0) ? 1 : 0
}

@_cdecl("dc_input_stop")
public func dc_input_stop() {
    InputMonitor.shared.stop()
}

/// Modifier keys held right now (raw CGEventFlags) — a cheap poll, not a
/// tap, for the top bar's live "wird übersprungen" badge.
@_cdecl("dc_modifier_flags")
public func dc_modifier_flags() -> UInt64 {
    CGEventSource.flagsState(.combinedSessionState).rawValue
}

/// Current mouse position in global top-left screen points → out[0..1].
@_cdecl("dc_mouse_location")
public func dc_mouse_location(_ out: UnsafeMutablePointer<Double>) {
    let location = CGEvent(source: nil)?.location ?? .zero
    out[0] = location.x
    out[1] = location.y
}
