import ApplicationServices
import Foundation

/// Accessibility info for one element; opaque to .NET (dc_element_* accessors).
final class NativeElement {
    let name: UnsafeMutablePointer<CChar>?
    let type: UnsafeMutablePointer<CChar>?
    let windowTitle: UnsafeMutablePointer<CChar>?
    let frame: CGRect?
    let isPassword: Bool

    init(name: String?, type: String?, windowTitle: String?, frame: CGRect?, isPassword: Bool) {
        self.name = duplicateCString(name)
        self.type = duplicateCString(type)
        self.windowTitle = duplicateCString(windowTitle)
        self.frame = frame
        self.isPassword = isPassword
    }

    deinit {
        free(name)
        free(type)
        free(windowTitle)
    }
}

private let systemWide: AXUIElement = {
    let element = AXUIElementCreateSystemWide()
    // A hung app must not stall the capture pipeline.
    AXUIElementSetMessagingTimeout(element, 0.25)
    return element
}()

private let manualAccessibilityLock = NSLock()
private var manualAccessibilityPids = Set<pid_t>()

private func attribute(_ element: AXUIElement, _ name: String) -> CFTypeRef? {
    var value: CFTypeRef?
    return AXUIElementCopyAttributeValue(element, name as CFString, &value) == .success ? value : nil
}

/// Electron/Chromium apps (VS Code, Slack, Teams, Chrome, ...) only build
/// their accessibility tree once asked to. Done once per process; the first
/// click in such an app may still come back without a name.
private func enableManualAccessibility(pid: pid_t) {
    manualAccessibilityLock.lock()
    let isNew = manualAccessibilityPids.insert(pid).inserted
    manualAccessibilityLock.unlock()
    guard isNew else { return }
    AXUIElementSetAttributeValue(AXUIElementCreateApplication(pid), "AXManualAccessibility" as CFString, kCFBooleanTrue)
}

private func describe(_ element: AXUIElement) -> NativeElement {
    var pid: pid_t = 0
    if AXUIElementGetPid(element, &pid) == .success, pid > 0 {
        enableManualAccessibility(pid: pid)
    }

    let name = [kAXTitleAttribute, kAXDescriptionAttribute, kAXValueAttribute, kAXHelpAttribute]
        .lazy
        .compactMap { attribute(element, $0) as? String }
        .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
        .first { !$0.isEmpty }

    var frame: CGRect?
    if let positionValue = attribute(element, kAXPositionAttribute),
       let sizeValue = attribute(element, kAXSizeAttribute),
       CFGetTypeID(positionValue) == AXValueGetTypeID(),
       CFGetTypeID(sizeValue) == AXValueGetTypeID() {
        var position = CGPoint.zero
        var size = CGSize.zero
        AXValueGetValue(positionValue as! AXValue, .cgPoint, &position)
        AXValueGetValue(sizeValue as! AXValue, .cgSize, &size)
        frame = CGRect(origin: position, size: size)
    }

    var windowTitle: String?
    if let window = attribute(element, kAXWindowAttribute), CFGetTypeID(window) == AXUIElementGetTypeID() {
        windowTitle = attribute(window as! AXUIElement, kAXTitleAttribute) as? String
    }

    // Secure text fields: never describe or capture them (DocuClick skips
    // the whole click, like UI Automation's IsPasswordProperty on Windows).
    // Their value is never read either — AX returns no value for them anyway.
    let isPassword = (attribute(element, kAXSubroleAttribute) as? String) == (kAXSecureTextFieldSubrole as String)

    // kAXRoleDescription is already localized by the target app ("Taste",
    // "Textfeld", ...), matching UI Automation's LocalizedControlType.
    return NativeElement(
        name: isPassword ? nil : name,
        type: attribute(element, kAXRoleDescriptionAttribute) as? String,
        windowTitle: windowTitle.flatMap { $0.isEmpty ? nil : $0 },
        frame: frame,
        isPassword: isPassword)
}

@_cdecl("dc_element_at")
public func dc_element_at(_ x: Double, _ y: Double) -> UnsafeMutableRawPointer? {
    var element: AXUIElement?
    let result = AXUIElementCopyElementAtPosition(systemWide, Float(x), Float(y), &element)
    guard result == .success, let element else {
        LastError.set("Kein Element am Punkt (AXError \(result.rawValue)).")
        return nil
    }
    return Unmanaged.passRetained(describe(element)).toOpaque()
}

@_cdecl("dc_element_focused")
public func dc_element_focused() -> UnsafeMutableRawPointer? {
    guard let focused = attribute(systemWide, kAXFocusedUIElementAttribute),
          CFGetTypeID(focused) == AXUIElementGetTypeID() else { return nil }
    return Unmanaged.passRetained(describe(focused as! AXUIElement)).toOpaque()
}

private func element(_ handle: UnsafeMutableRawPointer) -> NativeElement {
    Unmanaged<NativeElement>.fromOpaque(handle).takeUnretainedValue()
}

/// Strings stay valid until dc_element_free.
@_cdecl("dc_element_name") public func dc_element_name(_ h: UnsafeMutableRawPointer) -> UnsafeMutablePointer<CChar>? { element(h).name }
@_cdecl("dc_element_type") public func dc_element_type(_ h: UnsafeMutableRawPointer) -> UnsafeMutablePointer<CChar>? { element(h).type }
@_cdecl("dc_element_window_title") public func dc_element_window_title(_ h: UnsafeMutableRawPointer) -> UnsafeMutablePointer<CChar>? { element(h).windowTitle }

/// Writes the element's frame (screen points) to out[0..3] = x, y, width, height. Returns 0 if it has none.
@_cdecl("dc_element_frame")
public func dc_element_frame(_ h: UnsafeMutableRawPointer, _ out: UnsafeMutablePointer<Double>) -> Int32 {
    guard let frame = element(h).frame else { return 0 }
    out[0] = frame.minX
    out[1] = frame.minY
    out[2] = frame.width
    out[3] = frame.height
    return 1
}

@_cdecl("dc_element_is_password")
public func dc_element_is_password(_ h: UnsafeMutableRawPointer) -> Int32 { element(h).isPassword ? 1 : 0 }

@_cdecl("dc_element_free")
public func dc_element_free(_ handle: UnsafeMutableRawPointer?) {
    if let handle { Unmanaged<NativeElement>.fromOpaque(handle).release() }
}
