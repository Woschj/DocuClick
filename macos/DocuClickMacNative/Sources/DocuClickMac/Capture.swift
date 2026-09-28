import AppKit
import CoreMedia
import ScreenCaptureKit

// MARK: - Frames handed to .NET

/// A captured image as tightly packed BGRA (premultiplied) pixels plus the
/// captured area in global screen points and the pixel scale. Opaque to
/// .NET: read via the dc_frame_* accessors, release with dc_frame_free.
final class NativeFrame {
    let pixels: UnsafeMutableRawPointer
    let width: Int
    let height: Int
    let bytesPerRow: Int
    let bounds: CGRect
    let scale: Double

    init(pixels: UnsafeMutableRawPointer, width: Int, height: Int, bytesPerRow: Int, bounds: CGRect, scale: Double) {
        self.pixels = pixels
        self.width = width
        self.height = height
        self.bytesPerRow = bytesPerRow
        self.bounds = bounds
        self.scale = scale
    }

    deinit { free(pixels) }

    /// Renders a CGImage into a freshly allocated BGRA buffer.
    static func from(_ image: CGImage, bounds: CGRect, scale: Double) throws -> NativeFrame {
        let width = image.width, height = image.height, bytesPerRow = width * 4
        guard let pixels = malloc(bytesPerRow * height),
              let context = CGContext(
                data: pixels, width: width, height: height, bitsPerComponent: 8, bytesPerRow: bytesPerRow,
                space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue) else {
            throw NativeError("Bildpuffer konnte nicht angelegt werden.")
        }
        context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
        return NativeFrame(pixels: pixels, width: width, height: height, bytesPerRow: bytesPerRow, bounds: bounds, scale: scale)
    }
}

// MARK: - Window lookup

struct WindowInfo {
    let id: CGWindowID
    let pid: pid_t
    let frame: CGRect
    let title: String?
    let owner: String?
}

private let minimumWindowDimension: CGFloat = 40

/// On-screen, normal-level windows front to back, excluding DocuClick's own.
func onScreenWindows() -> [WindowInfo] {
    guard let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID)
            as? [[String: Any]] else { return [] }
    let ownPid = getpid()
    return list.compactMap { entry in
        guard (entry[kCGWindowLayer as String] as? Int) == 0,
              let pid = entry[kCGWindowOwnerPID as String] as? pid_t, pid != ownPid,
              let boundsDict = entry[kCGWindowBounds as String] as? NSDictionary,
              let frame = CGRect(dictionaryRepresentation: boundsDict as CFDictionary),
              frame.width >= minimumWindowDimension, frame.height >= minimumWindowDimension,
              let number = entry[kCGWindowNumber as String] as? UInt32 else { return nil }
        return WindowInfo(
            id: CGWindowID(number), pid: pid, frame: frame,
            title: (entry[kCGWindowName as String] as? String).flatMap { $0.isEmpty ? nil : $0 },
            owner: entry[kCGWindowOwnerName as String] as? String)
    }
}

func windowAt(_ point: CGPoint) -> WindowInfo? {
    onScreenWindows().first { $0.frame.contains(point) }
}

func frontmostWindow() -> WindowInfo? {
    guard let pid = NSWorkspace.shared.frontmostApplication?.processIdentifier else { return nil }
    return onScreenWindows().first { $0.pid == pid }
}

// MARK: - Pre-click stream

/// One continuous low-rate stream per display while a session runs, so a
/// click can use the frame from right before it (see PreClickSnapshot).
/// DocuClick's own windows are excluded from the streams and the exclusion
/// list is refreshed periodically — new windows don't show up in
/// SCShareableContent immediately (seen in the feasibility probe).
final class PreClickStreams: NSObject, SCStreamOutput, SCStreamDelegate {
    static let shared = PreClickStreams()

    private final class DisplayStream {
        let stream: SCStream
        let displayID: CGDirectDisplayID
        let frame: CGRect
        let scale: Double
        var latest: CVPixelBuffer?

        init(stream: SCStream, displayID: CGDirectDisplayID, frame: CGRect, scale: Double) {
            self.stream = stream
            self.displayID = displayID
            self.frame = frame
            self.scale = scale
        }
    }

    private let lock = NSLock()
    private var streams: [CGDirectDisplayID: DisplayStream] = [:]
    private var active = false
    private var reconciling = false
    private var timer: DispatchSourceTimer?
    private let queue = DispatchQueue(label: "DocuClick.PreClickStreams")
    private let sampleQueue = DispatchQueue(label: "DocuClick.PreClickStreams.samples")

    func begin() {
        lock.lock(); active = true; lock.unlock()
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now(), repeating: 2.0)
        timer.setEventHandler { [weak self] in self?.reconcile() }
        timer.resume()
        self.timer = timer
    }

    func end() {
        timer?.cancel()
        timer = nil
        lock.lock()
        active = false
        let all = Array(streams.values)
        streams.removeAll()
        lock.unlock()
        for entry in all { entry.stream.stopCapture { _ in } }
    }

    /// Latest frame of every display, retained for the caller.
    func snapshot() -> PreClickSnapshot? {
        lock.lock(); defer { lock.unlock() }
        let frames = streams.values.compactMap { entry in
            entry.latest.map { PreClickSnapshot.DisplayFrame(buffer: $0, displayFrame: entry.frame, scale: entry.scale) }
        }
        return frames.isEmpty ? nil : PreClickSnapshot(frames: frames)
    }

    /// Starts streams for new displays, drops vanished ones, refreshes the own-window exclusion.
    private func reconcile() {
        // A slow round (ScreenCaptureKit busy) must not overlap the next
        // tick and start a second stream for the same display.
        let shouldRun = lock.withLock {
            guard active, !reconciling else { return false }
            reconciling = true
            return true
        }
        guard shouldRun else { return }
        Task {
            defer { lock.withLock { reconciling = false } }
            guard let content = try? await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true) else { return }
            let own = content.windows.filter { $0.owningApplication?.processID == getpid() }
            let current = Dictionary(uniqueKeysWithValues: content.displays.map { ($0.displayID, $0) })

            let (removed, existing, missing) = lock.withLock {
                let vanished = streams.keys.filter { current[$0] == nil || streams[$0]!.frame != current[$0]!.frame }
                let removed = vanished.compactMap { streams.removeValue(forKey: $0) }
                return (removed, Array(streams.values), current.values.filter { streams[$0.displayID] == nil })
            }

            for entry in removed { try? await entry.stream.stopCapture() }
            for entry in existing {
                if let display = current[entry.displayID] {
                    try? await entry.stream.updateContentFilter(SCContentFilter(display: display, excludingWindows: own))
                }
            }
            for display in missing { await start(display, excluding: own) }
        }
    }

    private func start(_ display: SCDisplay, excluding own: [SCWindow]) async {
        let filter = SCContentFilter(display: display, excludingWindows: own)
        let scale = Double(filter.pointPixelScale)
        let config = SCStreamConfiguration()
        config.width = Int(Double(display.width) * scale)
        config.height = Int(Double(display.height) * scale)
        config.minimumFrameInterval = CMTime(value: 1, timescale: 15)
        config.queueDepth = 5
        config.showsCursor = false
        config.pixelFormat = kCVPixelFormatType_32BGRA
        let stream = SCStream(filter: filter, configuration: config, delegate: self)
        do {
            try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: sampleQueue)
            try await stream.startCapture()
            let kept = lock.withLock {
                guard active else { return false }
                streams[display.displayID] = DisplayStream(stream: stream, displayID: display.displayID, frame: display.frame, scale: scale)
                return true
            }
            if !kept {
                try? await stream.stopCapture()
            }
        } catch {
            // Retried by the next reconcile tick.
        }
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen,
              let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
              let rawStatus = attachments.first?[.status] as? Int,
              SCFrameStatus(rawValue: rawStatus) == .complete,
              let buffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        lock.lock()
        streams.values.first { $0.stream === stream }?.latest = buffer
        lock.unlock()
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        // E.g. -3815 while displays are being reconfigured: forget the
        // stream, the next reconcile tick starts a fresh one.
        lock.lock()
        if let key = streams.first(where: { $0.value.stream === stream })?.key {
            streams.removeValue(forKey: key)
        }
        lock.unlock()
    }
}

/// The frames of all displays at the moment of a click.
final class PreClickSnapshot {
    struct DisplayFrame {
        let buffer: CVPixelBuffer
        let displayFrame: CGRect
        let scale: Double
    }

    let frames: [DisplayFrame]
    init(frames: [DisplayFrame]) { self.frames = frames }

    /// Copies the part of the display containing the area's center that overlaps the area.
    func crop(_ area: CGRect) -> NativeFrame? {
        let center = CGPoint(x: area.midX, y: area.midY)
        guard let source = frames.first(where: { $0.displayFrame.contains(center) }) else { return nil }
        let clipped = area.intersection(source.displayFrame)
        guard !clipped.isEmpty else { return nil }

        let buffer = source.buffer
        CVPixelBufferLockBaseAddress(buffer, .readOnly)
        defer { CVPixelBufferUnlockBaseAddress(buffer, .readOnly) }
        guard let base = CVPixelBufferGetBaseAddress(buffer) else { return nil }
        let sourceWidth = CVPixelBufferGetWidth(buffer), sourceHeight = CVPixelBufferGetHeight(buffer)
        let sourceRowBytes = CVPixelBufferGetBytesPerRow(buffer)

        let x0 = max(0, min(sourceWidth, Int(((clipped.minX - source.displayFrame.minX) * source.scale).rounded())))
        let y0 = max(0, min(sourceHeight, Int(((clipped.minY - source.displayFrame.minY) * source.scale).rounded())))
        let x1 = max(x0, min(sourceWidth, Int(((clipped.maxX - source.displayFrame.minX) * source.scale).rounded())))
        let y1 = max(y0, min(sourceHeight, Int(((clipped.maxY - source.displayFrame.minY) * source.scale).rounded())))
        let width = x1 - x0, height = y1 - y0
        guard width > 0, height > 0 else { return nil }

        let rowBytes = width * 4
        guard let pixels = malloc(rowBytes * height) else { return nil }
        for row in 0..<height {
            memcpy(pixels + row * rowBytes, base + (y0 + row) * sourceRowBytes + x0 * 4, rowBytes)
        }
        return NativeFrame(pixels: pixels, width: width, height: height, bytesPerRow: rowBytes, bounds: clipped, scale: source.scale)
    }
}

// MARK: - One-shot captures (ScreenCaptureKit)

private func shareableContent() async throws -> SCShareableContent {
    try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
}

/// Exactly this window, even where other windows overlap it.
private func captureWindow(_ window: WindowInfo) async throws -> NativeFrame {
    let content = try await shareableContent()
    guard let scWindow = content.windows.first(where: { $0.windowID == window.id }) else {
        throw NativeError("Fenster \(window.id) ist nicht mehr vorhanden.")
    }
    let filter = SCContentFilter(desktopIndependentWindow: scWindow)
    let scale = Double(filter.pointPixelScale)
    let config = SCStreamConfiguration()
    config.width = Int(scWindow.frame.width * scale)
    config.height = Int(scWindow.frame.height * scale)
    config.showsCursor = false
    config.ignoreShadowsSingleWindow = true
    let image = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: config)
    return try NativeFrame.from(image, bounds: scWindow.frame, scale: scale)
}

/// A screen area (clipped to the display containing its center), without DocuClick's own windows.
private func captureArea(_ area: CGRect) async throws -> NativeFrame {
    let content = try await shareableContent()
    let center = CGPoint(x: area.midX, y: area.midY)
    guard let display = content.displays.first(where: { $0.frame.contains(center) }) ?? content.displays.first else {
        throw NativeError("Kein Bildschirm gefunden.")
    }
    let own = content.windows.filter { $0.owningApplication?.processID == getpid() }
    let filter = SCContentFilter(display: display, excludingWindows: own)
    let scale = Double(filter.pointPixelScale)
    let clipped = area.intersection(display.frame)
    let config = SCStreamConfiguration()
    config.sourceRect = clipped.offsetBy(dx: -display.frame.minX, dy: -display.frame.minY)
    config.width = Int(clipped.width * scale)
    config.height = Int(clipped.height * scale)
    config.showsCursor = false
    let image = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: config)
    return try NativeFrame.from(image, bounds: clipped, scale: scale)
}

private func displayFrame(containing point: CGPoint) -> CGRect {
    var count: UInt32 = 0
    var displays = [CGDirectDisplayID](repeating: 0, count: 16)
    CGGetDisplaysWithPoint(point, 16, &displays, &count)
    return CGDisplayBounds(count > 0 ? displays[0] : CGMainDisplayID())
}

private func capture(area: CGRect, window: WindowInfo?, preClick: PreClickSnapshot?) throws -> NativeFrame {
    if let preClick, let frame = preClick.crop(area) {
        return frame
    }
    return try runBlocking {
        if let window { return try await captureWindow(window) }
        return try await captureArea(area)
    }
}

private func wrap(_ body: () throws -> NativeFrame) -> UnsafeMutableRawPointer? {
    do {
        return Unmanaged.passRetained(try body()).toOpaque()
    } catch {
        LastError.set("\(error)")
        return nil
    }
}

private func snapshot(_ handle: UnsafeMutableRawPointer?) -> PreClickSnapshot? {
    handle.map { Unmanaged<PreClickSnapshot>.fromOpaque($0).takeUnretainedValue() }
}

// MARK: - C ABI

@_cdecl("dc_capture_session_begin")
public func dc_capture_session_begin(_ preClickStreams: Int32) {
    if preClickStreams != 0 { PreClickStreams.shared.begin() }
}

@_cdecl("dc_capture_session_end")
public func dc_capture_session_end() {
    PreClickStreams.shared.end()
}

/// Grabs the latest stream frames (cheap: retains buffers only). NULL if no stream is running.
@_cdecl("dc_preclick_grab")
public func dc_preclick_grab() -> UnsafeMutableRawPointer? {
    PreClickStreams.shared.snapshot().map { Unmanaged.passRetained($0).toOpaque() }
}

@_cdecl("dc_preclick_release")
public func dc_preclick_release(_ handle: UnsafeMutableRawPointer?) {
    if let handle { Unmanaged<PreClickSnapshot>.fromOpaque(handle).release() }
}

/// The window under the point, or its whole display if there is none (Dock, menu bar, desktop).
@_cdecl("dc_capture_window_at")
public func dc_capture_window_at(_ x: Double, _ y: Double, _ preClick: UnsafeMutableRawPointer?) -> UnsafeMutableRawPointer? {
    wrap {
        let point = CGPoint(x: x, y: y)
        let window = windowAt(point)
        return try capture(area: window?.frame ?? displayFrame(containing: point), window: window, preClick: snapshot(preClick))
    }
}

/// A square of 2×radius points around the point, clipped to its display.
@_cdecl("dc_capture_around")
public func dc_capture_around(_ x: Double, _ y: Double, _ radius: Double, _ preClick: UnsafeMutableRawPointer?) -> UnsafeMutableRawPointer? {
    wrap {
        let area = CGRect(x: x - radius, y: y - radius, width: radius * 2, height: radius * 2)
            .intersection(displayFrame(containing: CGPoint(x: x, y: y)))
        return try capture(area: area, window: nil, preClick: snapshot(preClick))
    }
}

/// The frontmost app's frontmost window, or the main display.
@_cdecl("dc_capture_frontmost")
public func dc_capture_frontmost(_ preClick: UnsafeMutableRawPointer?) -> UnsafeMutableRawPointer? {
    wrap {
        let window = frontmostWindow()
        return try capture(area: window?.frame ?? CGDisplayBounds(CGMainDisplayID()), window: window, preClick: snapshot(preClick))
    }
}

private func frame(_ handle: UnsafeMutableRawPointer) -> NativeFrame {
    Unmanaged<NativeFrame>.fromOpaque(handle).takeUnretainedValue()
}

@_cdecl("dc_frame_pixels") public func dc_frame_pixels(_ h: UnsafeMutableRawPointer) -> UnsafeMutableRawPointer { frame(h).pixels }
@_cdecl("dc_frame_width") public func dc_frame_width(_ h: UnsafeMutableRawPointer) -> Int32 { Int32(frame(h).width) }
@_cdecl("dc_frame_height") public func dc_frame_height(_ h: UnsafeMutableRawPointer) -> Int32 { Int32(frame(h).height) }
@_cdecl("dc_frame_bytes_per_row") public func dc_frame_bytes_per_row(_ h: UnsafeMutableRawPointer) -> Int32 { Int32(frame(h).bytesPerRow) }
@_cdecl("dc_frame_scale") public func dc_frame_scale(_ h: UnsafeMutableRawPointer) -> Double { frame(h).scale }
@_cdecl("dc_frame_x") public func dc_frame_x(_ h: UnsafeMutableRawPointer) -> Double { frame(h).bounds.minX }
@_cdecl("dc_frame_y") public func dc_frame_y(_ h: UnsafeMutableRawPointer) -> Double { frame(h).bounds.minY }
@_cdecl("dc_frame_w") public func dc_frame_w(_ h: UnsafeMutableRawPointer) -> Double { frame(h).bounds.width }
@_cdecl("dc_frame_h") public func dc_frame_h(_ h: UnsafeMutableRawPointer) -> Double { frame(h).bounds.height }

@_cdecl("dc_frame_free")
public func dc_frame_free(_ handle: UnsafeMutableRawPointer?) {
    if let handle { Unmanaged<NativeFrame>.fromOpaque(handle).release() }
}

/// Title of the frontmost window (caller frees with dc_free), or NULL.
@_cdecl("dc_frontmost_window_title")
public func dc_frontmost_window_title() -> UnsafeMutablePointer<CChar>? {
    let window = frontmostWindow()
    return duplicateCString(window?.title ?? window?.owner)
}
