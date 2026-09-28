import Carbon
import Foundation

/// Invoked on the main thread with the id passed to dc_hotkey_register.
public typealias DCHotkeyCallback = @convention(c) (Int32) -> Void

/// Global hotkeys via Carbon's RegisterEventHotKey — needs no extra
/// permission and consumes the key combination, like RegisterHotKey on
/// Windows. Must be used from the main thread.
private enum Hotkeys {
    static var callback: DCHotkeyCallback?
    static var refs: [Int32: EventHotKeyRef] = [:]
    static var handlerInstalled = false
    static let signature: OSType = 0x4443_4B59 // 'DCKY'

    static func installHandlerIfNeeded() {
        guard !handlerInstalled else { return }
        handlerInstalled = true
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, event, _ in
            var hotKeyID = EventHotKeyID()
            let status = GetEventParameter(
                event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                nil, MemoryLayout<EventHotKeyID>.size, nil, &hotKeyID)
            if status == noErr, hotKeyID.signature == Hotkeys.signature {
                Hotkeys.callback?(Int32(hotKeyID.id))
            }
            return noErr
        }, 1, &spec, nil, nil)
    }
}

@_cdecl("dc_hotkey_set_callback")
public func dc_hotkey_set_callback(_ callback: DCHotkeyCallback) {
    Hotkeys.callback = callback
    Hotkeys.installHandlerIfNeeded()
}

/// keyCode: virtual key code (kVK_*). modifiers: Carbon mask
/// (cmdKey 256, shiftKey 512, optionKey 2048, controlKey 4096).
/// Returns 1 on success, 0 if the combination is taken or invalid.
@_cdecl("dc_hotkey_register")
public func dc_hotkey_register(_ id: Int32, _ keyCode: UInt32, _ modifiers: UInt32) -> Int32 {
    Hotkeys.installHandlerIfNeeded()
    dc_hotkey_unregister(id)
    var ref: EventHotKeyRef?
    let status = RegisterEventHotKey(
        keyCode, modifiers, EventHotKeyID(signature: Hotkeys.signature, id: UInt32(id)),
        GetApplicationEventTarget(), 0, &ref)
    guard status == noErr, let ref else {
        LastError.set("Tastenkürzel konnte nicht registriert werden (OSStatus \(status)).")
        return 0
    }
    Hotkeys.refs[id] = ref
    return 1
}

@_cdecl("dc_hotkey_unregister")
public func dc_hotkey_unregister(_ id: Int32) {
    if let ref = Hotkeys.refs.removeValue(forKey: id) {
        UnregisterEventHotKey(ref)
    }
}
